using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Time;

namespace Rochell.MasterData.Items;

internal static partial class ItemRules
{
    public const string Aggregate = "Item";
    public const string ConversionAggregate = "UomConversion";

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{1,39}$", RegexOptions.CultureInvariant)]
    public static partial Regex CodeFormat();

    public static async Task<bool> UomExistsAsync(CommandContext context, string uom, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(context.Connection, context.Transaction, "SELECT EXISTS (SELECT 1 FROM md.uom WHERE uom_code = @u)", ("u", uom));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }
}

[RequiresPermission("item:create")]
public sealed class CreateRawMaterialHandler : ICommandHandler<CreateRawMaterial>
{
    public string CommandType => "MasterData.CreateRawMaterial";

    public async Task<string> HandleAsync(CreateRawMaterial command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var code = (command.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (!ItemRules.CodeFormat().IsMatch(code))
        {
            throw new DomainException(MasterDataErrors.ItemCodeInvalid, "Item code must be 2–40 characters: A–Z, 0–9, '-' or '_'.");
        }

        if (string.IsNullOrWhiteSpace(command.Description))
        {
            throw new DomainException(MasterDataErrors.FieldRequired, "The description is required.");
        }

        if (!ItemCategories.All.Contains(command.ItemCategory, StringComparer.Ordinal))
        {
            throw new DomainException(MasterDataErrors.CategoryInvalid, $"Category must be one of: {string.Join(", ", ItemCategories.All)}.");
        }

        if (!await ItemRules.UomExistsAsync(context, command.BaseUom, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(MasterDataErrors.UomUnknown, $"Unit of measure '{command.BaseUom}' does not exist.");
        }

        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ItemCreated",
                1,
                ItemRules.Aggregate,
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new { itemId = context.ResultRef, code, description = command.Description.Trim(), baseUom = command.BaseUom, itemCategory = command.ItemCategory }),
                Publish: false),
            cancellationToken).ConfigureAwait(false);

        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO md.item (item_id, company_id, code, description, item_type, base_uom, item_category, status, version)
                VALUES (@id, @company_id, @code, @description, 'RAW_MATERIAL', @base_uom, @category, 'DRAFT', 1)
                """,
                cancellationToken,
                ("id", context.ResultRef),
                ("company_id", context.CompanyId),
                ("code", code),
                ("description", command.Description.Trim()),
                ("base_uom", command.BaseUom),
                ("category", command.ItemCategory)).ConfigureAwait(false);
        }
        catch (DbException ex) when (MasterRows.IsUniqueViolation(ex))
        {
            throw new DomainException(MasterDataErrors.ItemCodeDuplicate, $"Item code {code} already exists in this company.");
        }

        await context.AppendStateAsync(ItemRules.Aggregate, context.ResultRef, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { itemId = context.ResultRef, code, status = "DRAFT", version = 1 });
    }
}

/// <summary>E-PR04-1: requires item:activate (Controller), because a conversion changes inventory quantities.</summary>
[RequiresPermission("item:activate")]
public sealed class DefineUomConversionHandler : ICommandHandler<DefineUomConversion>
{
    private const decimal MaxFactor = 9_999_999_999.99999999m; // numeric(18,8)

    public string CommandType => "MasterData.DefineUomConversion";

    public async Task<string> HandleAsync(DefineUomConversion command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        if (command.Factor <= 0 || command.Factor > MaxFactor || decimal.Round(command.Factor, 8) != command.Factor)
        {
            throw new DomainException(MasterDataErrors.ConversionInvalid, "The factor must be positive, with at most 10 integer digits and 8 decimals.");
        }

        var today = BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);
        if (command.EffectiveFrom < today)
        {
            throw new DomainException(MasterDataErrors.ConversionRetroactive, $"Conversions cannot start before today ({today:yyyy-MM-dd}); the past is not recalculated.");
        }

        var baseUom = await BaseUomAsync(context, command.ItemId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(MasterDataErrors.NotFound, "The item does not exist.");
        if (!await ItemRules.UomExistsAsync(context, command.FromUom, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(MasterDataErrors.UomUnknown, $"Unit of measure '{command.FromUom}' does not exist.");
        }

        if (command.FromUom == baseUom)
        {
            throw new DomainException(MasterDataErrors.ConversionInvalid, "The source unit must differ from the item's base unit.");
        }

        // Close the open conversion (if any); it must have started strictly before the new one.
        var open = await OpenConversionStartAsync(context, command.ItemId, command.FromUom, baseUom, cancellationToken).ConfigureAwait(false);
        if (open is not null)
        {
            if (open.Value >= command.EffectiveFrom)
            {
                throw new DomainException(MasterDataErrors.ConversionRetroactive, $"The current conversion starts on {open.Value:yyyy-MM-dd}; a new one must start later.");
            }

            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE md.uom_conversion SET effective_to = @to WHERE company_id = @company_id AND item_id = @item_id AND from_uom = @from AND to_uom = @base AND effective_to IS NULL",
                cancellationToken,
                ("to", command.EffectiveFrom),
                ("company_id", context.CompanyId),
                ("item_id", command.ItemId),
                ("from", command.FromUom),
                ("base", baseUom)).ConfigureAwait(false);
        }

        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "UomConversionDefined",
                1,
                ItemRules.ConversionAggregate,
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new
                {
                    itemId = command.ItemId,
                    fromUom = command.FromUom,
                    toUom = baseUom,
                    factor = command.Factor.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    effectiveFrom = command.EffectiveFrom.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    previousClosed = open is not null,
                }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO md.uom_conversion (company_id, item_id, from_uom, to_uom, factor, effective_from)
            VALUES (@company_id, @item_id, @from, @base, @factor, @effective_from)
            """,
            cancellationToken,
            ("company_id", context.CompanyId),
            ("item_id", command.ItemId),
            ("from", command.FromUom),
            ("base", baseUom),
            ("factor", command.Factor),
            ("effective_from", command.EffectiveFrom)).ConfigureAwait(false);
        await context.AppendStateAsync(ItemRules.ConversionAggregate, context.ResultRef, "DOCUMENT", null, "ACTIVE", CommandType, eventId, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Serialize(new { itemId = command.ItemId, fromUom = command.FromUom, toUom = baseUom });
    }

    private static async Task<string?> BaseUomAsync(CommandContext context, Guid itemId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT base_uom FROM md.item WHERE company_id = @company_id AND item_id = @item_id FOR SHARE",
            ("company_id", context.CompanyId),
            ("item_id", itemId));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static async Task<DateOnly?> OpenConversionStartAsync(CommandContext context, Guid itemId, string fromUom, string baseUom, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT effective_from FROM md.uom_conversion
            WHERE company_id = @company_id AND item_id = @item_id AND from_uom = @from AND to_uom = @base AND effective_to IS NULL
            FOR UPDATE
            """,
            ("company_id", context.CompanyId),
            ("item_id", itemId),
            ("from", fromUom),
            ("base", baseUom));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? reader.GetFieldValue<DateOnly>(0) : null;
    }
}

[RequiresPermission("item:activate")]
public sealed class ActivateItemHandler : ICommandHandler<ActivateItem>
{
    public string CommandType => "MasterData.ActivateItem";

    public async Task<string> HandleAsync(ActivateItem command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        await MasterRows.EnsureDraftAtVersionAsync(context, "item", "item_id", command.ItemId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        var newVersion = command.ExpectedVersion + 1;

        var eventId = await context.AppendEventAsync(
            new EventDraft("ItemActivated", 1, ItemRules.Aggregate, command.ItemId, newVersion, JsonSerializer.Serialize(new { itemId = command.ItemId }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE md.item SET status = 'ACTIVE', version = @version WHERE item_id = @id",
            cancellationToken,
            ("version", newVersion),
            ("id", command.ItemId)).ConfigureAwait(false);
        await context.AppendStateAsync(ItemRules.Aggregate, command.ItemId, "DOCUMENT", "DRAFT", "ACTIVE", CommandType, eventId, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Serialize(new { itemId = command.ItemId, status = "ACTIVE", version = newVersion });
    }
}
