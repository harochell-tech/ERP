using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.FixedAssets.Cards;

/// <summary>
/// E-AF-5, E-AF1-02-6: puts a card awaiting service into service — from <paramref name="InServiceOn"/> (not before its purchase), in a
/// plant, with who is in charge (a name or a position). It copies its category's ACTIVE class (life and residual) for good
/// (E-AF1-01-2); depreciation starts the following month.
/// </summary>
public sealed record PutFixedAssetInService(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid AssetId, long ExpectedVersion, DateOnly InServiceOn, Guid PlantId, string Responsible) : ICommand;

/// <summary>
/// E-AF-9, E-AF1-02-5: moves a card to another plant from <paramref name="EffectiveOn"/> (after the last month it was depreciated and
/// its last move, not after today), without a journal; each month's depreciation goes to the plant in force on its last day.
/// </summary>
public sealed record TransferFixedAsset(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid AssetId, long ExpectedVersion, Guid PlantId, DateOnly EffectiveOn) : ICommand;

/// <summary>E-AF1-02-7: corrects a live card's description and who is in charge; its cost changes only through documents.</summary>
public sealed record UpdateFixedAsset(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid AssetId, long ExpectedVersion, string Description, string? Responsible) : ICommand;

/// <summary>E-AF1-02-8: cards for the fixed-asset lines of invoices posted before AF-1, with the cost of their posted settlements.</summary>
public sealed record CreateCardsForPostedInvoices(Guid CompanyId, Guid SessionId, string IdempotencyKey) : ICommand;

internal static class CardRules
{
    public static string Responsible(string? value, bool required)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length switch
        {
            0 when !required => string.Empty,
            >= 1 and <= 120 => trimmed,
            _ => throw new DomainException(FixedAssetErrors.AssetInvalid, "Who is in charge has 1 to 120 characters."),
        };
    }

    public static async Task RequirePlantAsync(CommandContext context, Guid plantId, CancellationToken cancellationToken)
    {
        var found = await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT 1 FROM md.plant WHERE company_id = @c AND plant_id = @p", r => r.GetInt32(0), cancellationToken,
            ("c", context.CompanyId), ("p", plantId)).ConfigureAwait(false);
        if (found.Count == 0)
        {
            throw new DomainException(FixedAssetErrors.PlantNotFound, "The plant does not exist.");
        }
    }

    public static async Task<Guid> AppendAsync(CommandContext context, FixedAssetCards.CardRow card, string eventType, object payload, CancellationToken cancellationToken)
        => await context.AppendEventAsync(
            new EventDraft(eventType, 1, FixedAssetCards.Aggregate, card.Id, card.Version + 1, JsonSerializer.Serialize(payload), Publish: true),
            cancellationToken).ConfigureAwait(false);
}

[RequiresPermission("fixed_asset:manage")]
public sealed class PutFixedAssetInServiceHandler : ICommandHandler<PutFixedAssetInService>
{
    public string CommandType => "FixedAssets.PutFixedAssetInService";

    public async Task<string> HandleAsync(PutFixedAssetInService command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var responsible = CardRules.Responsible(command.Responsible, required: true);
        var card = await FixedAssetCards.LockAsync(context, command.AssetId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (card.Status != FixedAssetCards.AwaitingService)
        {
            throw new DomainException(FixedAssetErrors.InvalidState, $"{card.Number} is {card.Status}: only a card awaiting service is put into service.");
        }

        if (command.InServiceOn < card.AcquiredOn)
        {
            throw new DomainException(FixedAssetErrors.AssetInvalid, $"{card.Number} was bought on {card.AcquiredOn:yyyy-MM-dd}; it is not in service before.");
        }

        await CardRules.RequirePlantAsync(context, command.PlantId, cancellationToken).ConfigureAwait(false);
        var assetClass = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT asset_class_id, useful_life_months, residual_pct FROM fa.asset_class WHERE expense_category_id = @cat AND status = 'ACTIVE'",
            r => (Id: r.GetGuid(0), Life: r.GetInt32(1), Residual: r.GetDecimal(2)),
            cancellationToken,
            ("cat", card.CategoryId)).ConfigureAwait(false)).FirstOrDefault();
        if (assetClass == default)
        {
            throw new DomainException(FixedAssetErrors.ClassNotApproved, $"{card.Number}: its category has no approved class (useful life, residual, accounts) yet (E-AF1-01-4).");
        }

        var eventId = await CardRules.AppendAsync(
            context,
            card,
            "FixedAssetPutInService",
            new
            {
                assetId = card.Id,
                assetNo = card.Number,
                inServiceOn = command.InServiceOn,
                plantId = command.PlantId,
                responsible,
                assetClassId = assetClass.Id,
                usefulLifeMonths = assetClass.Life,
                residualPct = FixedAssetCards.Text(assetClass.Residual),
            },
            cancellationToken).ConfigureAwait(false);
        var version = card.Version + 1;
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            UPDATE fa.asset SET status = 'IN_SERVICE', in_service_on = @on, plant_id = @p, responsible = @r, asset_class_id = @k, useful_life_months = @life, residual_pct = @pct,
                                version = @v
            WHERE asset_id = @a
            """,
            cancellationToken,
            ("on", command.InServiceOn),
            ("p", command.PlantId),
            ("r", responsible),
            ("k", assetClass.Id),
            ("life", assetClass.Life),
            ("pct", assetClass.Residual),
            ("v", version),
            ("a", card.Id)).ConfigureAwait(false);
        await FixedAssetCards.MovementAsync(context, card.Id, "IN_SERVICE", command.InServiceOn, null, command.PlantId, assetClass.Id, eventId, cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(FixedAssetCards.Aggregate, card.Id, "DOCUMENT", card.Status, FixedAssetCards.InService, CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { assetId = card.Id, assetNo = card.Number, status = FixedAssetCards.InService, version });
    }
}

[RequiresPermission("fixed_asset:manage")]
public sealed class TransferFixedAssetHandler : ICommandHandler<TransferFixedAsset>
{
    public string CommandType => "FixedAssets.TransferFixedAsset";

    public async Task<string> HandleAsync(TransferFixedAsset command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var card = await FixedAssetCards.LockAsync(context, command.AssetId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (card.Status is not (FixedAssetCards.AwaitingService or FixedAssetCards.InService))
        {
            throw new DomainException(FixedAssetErrors.InvalidState, $"{card.Number} is {card.Status}: it no longer moves.");
        }

        if (command.PlantId == card.PlantId)
        {
            throw new DomainException(FixedAssetErrors.AssetInvalid, $"{card.Number} is already in that plant.");
        }

        await CardRules.RequirePlantAsync(context, command.PlantId, cancellationToken).ConfigureAwait(false);
        var today = FixedAssetCards.BusinessDate(context);
        var (depreciatedThrough, lastMove) = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT (SELECT max((r.month + interval '1 month' - interval '1 day')::date) FROM fa.depreciation_line l JOIN fa.depreciation_run r ON r.run_id = l.run_id
                    WHERE l.asset_id = @a AND r.status = 'POSTED'),
                   (SELECT max(movement_date) FROM fa.asset_movement WHERE asset_id = @a AND kind IN ('ACQUISITION', 'IN_SERVICE', 'TRANSFER', 'OPENING'))
            """,
            r => (Through: r.IsDBNull(0) ? (DateOnly?)null : r.Date(0), LastMove: r.Date(1)),
            cancellationToken,
            ("a", card.Id)).ConfigureAwait(false)).Single();
        if (command.EffectiveOn <= depreciatedThrough || command.EffectiveOn < lastMove || command.EffectiveOn > today)
        {
            throw new DomainException(
                FixedAssetErrors.AssetInvalid,
                $"{card.Number} moves from a date after its last depreciated month ({depreciatedThrough:yyyy-MM-dd}), not before its last move ({lastMove:yyyy-MM-dd}) and not after today (E-AF1-02-5).");
        }

        var eventId = await CardRules.AppendAsync(
            context, card, "FixedAssetTransferred", new { assetId = card.Id, assetNo = card.Number, fromPlantId = card.PlantId, toPlantId = command.PlantId, effectiveOn = command.EffectiveOn },
            cancellationToken).ConfigureAwait(false);
        var version = card.Version + 1;
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE fa.asset SET plant_id = @p, version = @v WHERE asset_id = @a",
            cancellationToken, ("p", command.PlantId), ("v", version), ("a", card.Id)).ConfigureAwait(false);
        await FixedAssetCards.MovementAsync(context, card.Id, "TRANSFER", command.EffectiveOn, null, command.PlantId, null, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { assetId = card.Id, plantId = command.PlantId, effectiveOn = command.EffectiveOn, version });
    }
}

[RequiresPermission("fixed_asset:manage")]
public sealed class UpdateFixedAssetHandler : ICommandHandler<UpdateFixedAsset>
{
    public string CommandType => "FixedAssets.UpdateFixedAsset";

    public async Task<string> HandleAsync(UpdateFixedAsset command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var description = (command.Description ?? string.Empty).Trim();
        if (description.Length is 0 or > 200)
        {
            throw new DomainException(FixedAssetErrors.AssetInvalid, "The description has 1 to 200 characters.");
        }

        var card = await FixedAssetCards.LockAsync(context, command.AssetId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (card.Status is not (FixedAssetCards.AwaitingService or FixedAssetCards.InService))
        {
            throw new DomainException(FixedAssetErrors.InvalidState, $"{card.Number} is {card.Status}: it is no longer corrected.");
        }

        var responsible = CardRules.Responsible(command.Responsible, required: card.Status == FixedAssetCards.InService);
        await CardRules.AppendAsync(context, card, "FixedAssetUpdated", new { assetId = card.Id, description, responsible }, cancellationToken).ConfigureAwait(false);
        var version = card.Version + 1;
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fa.asset SET description = @d, responsible = @r, version = @v WHERE asset_id = @a",
            cancellationToken,
            ("d", description),
            ("r", responsible.Length == 0 ? DBNull.Value : (object)responsible),
            ("v", version),
            ("a", card.Id)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { assetId = card.Id, version });
    }
}

[RequiresPermission("fixed_asset:manage")]
public sealed class CreateCardsForPostedInvoicesHandler : ICommandHandler<CreateCardsForPostedInvoices>
{
    public string CommandType => "FixedAssets.CreateCardsForPostedInvoices";

    public async Task<string> HandleAsync(CreateCardsForPostedInvoices command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var eventId = await context.AppendEventAsync(
            new EventDraft("FixedAssetCardsBackfillRequested", 1, "FixedAssetBackfill", context.ResultRef, 1, JsonSerializer.Serialize(new { backfillId = context.ResultRef }), Publish: false),
            cancellationToken).ConfigureAwait(false);
        var created = await FixedAssetCards.CreateForPostedInvoicesAsync(context, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { created = created.Count, assetIds = created });
    }
}
