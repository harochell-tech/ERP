using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Manufacturing.Quality;

/// <summary>
/// E-LAB1-01-2: Calidad saves an item's requirements in one step (step-up): the lot prefix of its field code, the nominal measures
/// (cm) a specimen without measures uses, the net-area fraction and the 28-day minimums (kg/cm², both optional — without them the
/// verdict is «Sin requisito», E-LAB1-10). Every save is a new version.
/// </summary>
public sealed record SetItemSpec(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ItemId, string LotPrefix, decimal NominalWidthCm, decimal NominalHeightCm, decimal NominalLengthCm,
    decimal? NetAreaFraction = null, decimal? MinAvg28d = null, decimal? MinIndividual28d = null) : ICommand;

/// <summary>E-LAB1-01-3: Calidad sets one of the lab's parameters: <c>Number</c> for a number, <c>Text</c> for the equipment's data.</summary>
public sealed record SetLabParameter(Guid CompanyId, Guid SessionId, string IdempotencyKey, string Code, decimal? Number = null, string? Text = null) : ICommand;

/// <summary>E-LAB1-01-11: Calidad adds a failure type or renames one (also one of the shared seven).</summary>
public sealed record DefineFailureType(Guid CompanyId, Guid SessionId, string IdempotencyKey, string Code, string Name) : ICommand;

/// <summary><c>Status</c>: ACTIVE or INACTIVE; an inactive type is no longer offered, and the tests that used it keep it.</summary>
public sealed record SetFailureTypeStatus(Guid CompanyId, Guid SessionId, string IdempotencyKey, string Code, string Status) : ICommand;

/// <summary>E-LAB1-3: Calidad gives a machine its short code (P1, P2, P3), unique in the company; nothing clears it.</summary>
public sealed record SetMachineShortCode(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid MachineId, long ExpectedVersion, string ShortCode) : IPlantScopedCommand;

internal static partial class Specs
{
    [GeneratedRegex("^[A-Z0-9]{1,4}$")]
    public static partial Regex Prefix();

    [GeneratedRegex("^[A-Z0-9]{1,6}$")]
    public static partial Regex ShortCode();

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_]{0,29}$")]
    public static partial Regex FailureCode();

    public static decimal Positive(decimal value, string what)
        => value > 0m && decimal.Round(value, Lab.Decimals) == value ? value : throw new DomainException(QualityErrors.SpecInvalid, $"{what} must be greater than zero with at most 6 decimals.");
}

[RequiresPermission("lab_spec:manage", StepUp = true)]
public sealed class SetItemSpecHandler : ICommandHandler<SetItemSpec>
{
    public string CommandType => "Manufacturing.SetItemSpec";

    public async Task<string> HandleAsync(SetItemSpec command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var prefix = (command.LotPrefix ?? string.Empty).Trim().ToUpperInvariant();
        if (!Specs.Prefix().IsMatch(prefix))
        {
            throw new DomainException(QualityErrors.SpecInvalid, "The lot prefix is 1 to 4 capital letters or digits (4, 6, 8).");
        }

        var width = Specs.Positive(command.NominalWidthCm, "The nominal width");
        var height = Specs.Positive(command.NominalHeightCm, "The nominal height");
        var length = Specs.Positive(command.NominalLengthCm, "The nominal length");
        var minAvg = command.MinAvg28d is { } a ? Specs.Positive(a, "The minimum 28-day average") : (decimal?)null;
        var minOne = command.MinIndividual28d is { } o ? Specs.Positive(o, "The minimum 28-day individual strength") : (decimal?)null;
        if (command.NetAreaFraction is { } f && (f <= 0m || f > 1m || decimal.Round(f, Lab.Decimals) != f))
        {
            throw new DomainException(QualityErrors.SpecInvalid, "The net area is a fraction above 0 and up to 1 (0.55 for 55 %).");
        }

        if (minOne is not null && minAvg is null)
        {
            throw new DomainException(QualityErrors.SpecInvalid, "The individual minimum needs the minimum average.");
        }

        var type = await MfgSql.ScalarAsync<string>(context, "SELECT item_type FROM md.item WHERE company_id = @c AND item_id = @i", cancellationToken, ("c", context.CompanyId), ("i", command.ItemId))
            .ConfigureAwait(false) ?? throw new DomainException(ManufacturingErrors.NotFound, "The item does not exist.");
        if (type != "FINISHED_GOOD")
        {
            throw new DomainException(ManufacturingErrors.NotFinishedGood, "Only finished goods have lab requirements.");
        }

        await MfgSql.LockAsync(context, $"item-spec:{command.ItemId}", cancellationToken).ConfigureAwait(false);
        var version = (await MfgSql.ScalarAsync<int?>(
            context, "SELECT max(version) FROM qa.item_spec WHERE company_id = @c AND item_id = @i", cancellationToken, ("c", context.CompanyId), ("i", command.ItemId)).ConfigureAwait(false) ?? 0) + 1;
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO qa.item_spec (company_id, item_id, version, lot_prefix, nominal_width_cm, nominal_height_cm, nominal_length_cm, net_area_fraction, min_avg_28d, min_individual_28d, set_by, set_at)
            VALUES (@c, @i, @v, @p, @w, @h, @l, @net, @avg, @one, @by, @at)
            """,
            cancellationToken,
            ("c", context.CompanyId), ("i", command.ItemId), ("v", version), ("p", prefix), ("w", width), ("h", height), ("l", length), ("net", command.NetAreaFraction), ("avg", minAvg),
            ("one", minOne), ("by", await MfgSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false)), ("at", context.Clock.UtcNow)).ConfigureAwait(false);
        await context.AppendEventAsync(
            new EventDraft("ItemSpecSet", 1, "ItemSpec", command.ItemId, version,
                JsonSerializer.Serialize(new
                {
                    itemId = command.ItemId,
                    version,
                    lotPrefix = prefix,
                    minAvg28d = minAvg is null ? null : Lab.Text(minAvg.Value),
                    minIndividual28d = minOne is null ? null : Lab.Text(minOne.Value),
                }),
                Publish: false),
            cancellationToken).ConfigureAwait(false);

        // E-LAB1-01-4: the lots that were waiting for this item's prefix get their field code now.
        var coded = await FieldCodes.AssignWaitingAsync(context, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { itemId = command.ItemId, version, lotsCoded = coded });
    }
}

[RequiresPermission("lab_spec:manage")]
public sealed class SetLabParameterHandler : ICommandHandler<SetLabParameter>
{
    private sealed record Definition(string Kind, decimal? Min, decimal? Max, bool Whole);

    public string CommandType => "Manufacturing.SetLabParameter";

    public async Task<string> HandleAsync(SetLabParameter command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var code = (command.Code ?? string.Empty).Trim().ToUpperInvariant();
        var definition = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, "SELECT kind, min_value, max_value, whole FROM qa.parameter_default WHERE code = @p",
            r => new Definition(r.GetString(0), r.NullableDecimal(1), r.NullableDecimal(2), r.GetBoolean(3)), cancellationToken, ("p", code)).ConfigureAwait(false)
            ?? throw new DomainException(QualityErrors.ParameterInvalid, $"{code} is not a lab parameter.");
        var text = string.IsNullOrWhiteSpace(command.Text) ? null : command.Text.Trim();
        if (definition.Kind == "NUMBER")
        {
            if (text is not null || command.Number is not { } n || n < definition.Min || n > definition.Max || decimal.Round(n, 8) != n || (definition.Whole && decimal.Truncate(n) != n))
            {
                throw new DomainException(
                    QualityErrors.ParameterInvalid,
                    $"{code} is a {(definition.Whole ? "whole " : string.Empty)}number from {Lab.Text(definition.Min!.Value)} to {Lab.Text(definition.Max!.Value)}.");
            }
        }
        else if (command.Number is not null || text is null || text.Length > 120)
        {
            throw new DomainException(QualityErrors.ParameterInvalid, $"{code} is a text of 1 to 120 characters.");
        }

        var id = context.Ids.NewId();
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "INSERT INTO qa.parameter_value (value_id, company_id, code, number_value, text_value, set_by, set_at) VALUES (@id, @c, @p, @n, @t, @by, @at)",
            cancellationToken,
            ("id", id), ("c", context.CompanyId), ("p", code), ("n", command.Number), ("t", text), ("by", await MfgSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false)),
            ("at", context.Clock.UtcNow)).ConfigureAwait(false);
        var value = command.Number is { } number ? Lab.Text(number) : text;
        await context.AppendEventAsync(
            new EventDraft("LabParameterSet", 1, "LabParameter", id, 1, JsonSerializer.Serialize(new { code, value }), Publish: false), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { code, value });
    }
}

[RequiresPermission("lab_spec:manage")]
public sealed class DefineFailureTypeHandler : ICommandHandler<DefineFailureType>
{
    public string CommandType => "Manufacturing.DefineFailureType";

    public async Task<string> HandleAsync(DefineFailureType command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var code = (command.Code ?? string.Empty).Trim().ToUpperInvariant();
        var name = (command.Name ?? string.Empty).Trim();
        if (!Specs.FailureCode().IsMatch(code) || name.Length is 0 or > 80)
        {
            throw new DomainException(QualityErrors.ParameterInvalid, "A failure type has a code of 1 to 30 capital letters, digits or _ and a name of 1 to 80 characters.");
        }

        await MfgSql.LockAsync(context, $"failure-type:{code}", cancellationToken).ConfigureAwait(false);
        var version = await MfgSql.ScalarAsync<long?>(context, "SELECT version FROM qa.failure_type WHERE company_id = @c AND code = @code", cancellationToken, ("c", context.CompanyId), ("code", code))
            .ConfigureAwait(false);
        if (version is null)
        {
            await Sql.ExecuteAsync(
                context.Connection, context.Transaction, "INSERT INTO qa.failure_type (company_id, code, name, status, version) VALUES (@c, @code, @n, 'ACTIVE', 1)", cancellationToken,
                ("c", context.CompanyId), ("code", code), ("n", name)).ConfigureAwait(false);
        }
        else
        {
            await Sql.ExecuteAsync(
                context.Connection, context.Transaction, "UPDATE qa.failure_type SET name = @n, version = version + 1 WHERE company_id = @c AND code = @code", cancellationToken,
                ("c", context.CompanyId), ("code", code), ("n", name)).ConfigureAwait(false);
        }

        await context.AppendEventAsync(
            new EventDraft("FailureTypeDefined", 1, "FailureType", context.Ids.NewId(), 1, JsonSerializer.Serialize(new { code, name }), Publish: false), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { code, name, version = (version ?? 0) + 1 });
    }
}

[RequiresPermission("lab_spec:manage")]
public sealed class SetFailureTypeStatusHandler : ICommandHandler<SetFailureTypeStatus>
{
    public string CommandType => "Manufacturing.SetFailureTypeStatus";

    public async Task<string> HandleAsync(SetFailureTypeStatus command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var code = (command.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (command.Status is not ("ACTIVE" or "INACTIVE"))
        {
            throw new DomainException(QualityErrors.ParameterInvalid, "A failure type is ACTIVE or INACTIVE.");
        }

        await MfgSql.LockAsync(context, $"failure-type:{code}", cancellationToken).ConfigureAwait(false);
        var changed = await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE qa.failure_type SET status = @s, version = version + 1 WHERE company_id = @c AND code = @code", cancellationToken,
            ("c", context.CompanyId), ("code", code), ("s", command.Status)).ConfigureAwait(false);
        if (changed == 0)
        {
            // One of the shared types: the company gets its own row of it.
            changed = await Sql.ExecuteAsync(
                context.Connection, context.Transaction,
                "INSERT INTO qa.failure_type (company_id, code, name, status, version) SELECT @c, d.code, d.name, @s, 1 FROM qa.failure_type_default d WHERE d.code = @code", cancellationToken,
                ("c", context.CompanyId), ("code", code), ("s", command.Status)).ConfigureAwait(false);
        }

        if (changed == 0)
        {
            throw new DomainException(ManufacturingErrors.NotFound, $"There is no failure type {code}.");
        }

        await context.AppendEventAsync(
            new EventDraft("FailureTypeStatusChanged", 1, "FailureType", context.Ids.NewId(), 1, JsonSerializer.Serialize(new { code, status = command.Status }), Publish: false),
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { code, status = command.Status });
    }
}

[RequiresPermission("lab_spec:manage")]
public sealed class SetMachineShortCodeHandler : ICommandHandler<SetMachineShortCode>
{
    public string CommandType => "Manufacturing.SetMachineShortCode";

    public async Task<string> HandleAsync(SetMachineShortCode command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var shortCode = (command.ShortCode ?? string.Empty).Trim().ToUpperInvariant();
        if (!Specs.ShortCode().IsMatch(shortCode))
        {
            throw new DomainException(QualityErrors.SpecInvalid, "The machine's short code is 1 to 6 capital letters or digits (P1, P2, P3).");
        }

        var (_, version) = await MasterRows.LockAsync(context, MasterRows.Machines, command.PlantId, command.MachineId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        var next = version + 1;
        await context.AppendEventAsync(
            new EventDraft("MachineShortCodeSet", 1, MasterRows.Machines.Aggregate, command.MachineId, next, JsonSerializer.Serialize(new { machineId = command.MachineId, shortCode }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await Sql.ExecuteAsync(
                context.Connection, context.Transaction, "UPDATE md.machine SET short_code = @s, version = @v WHERE machine_id = @id", cancellationToken,
                ("s", shortCode), ("v", next), ("id", command.MachineId)).ConfigureAwait(false);
        }
        catch (DbException ex) when (ex.SqlState == SqlStates.UniqueViolation)
        {
            throw new DomainException(ManufacturingErrors.CodeDuplicate, $"Another machine already has the short code {shortCode}.");
        }

        // E-LAB1-01-4: the lots that were waiting for this machine's short code get their field code now.
        var coded = await FieldCodes.AssignWaitingAsync(context, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { machineId = command.MachineId, shortCode, version = next, lotsCoded = coded });
    }
}
