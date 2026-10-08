using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Manufacturing.Portal;

// MFG2-03 (E-MFG2-3, E-MFG2-01-3/4/7): Producción › Portal — the plant manager pairs the portal's machines, moulds, shift numbers and
// batch-plant materials with Core's (portal:manage); everyone who reads production sees the pairings and the connection's state.

/// <summary>A portal machine (<c>planta2</c>) → a Core machine and the batch plant that feeds it (null: offline, consumption typed, E-MFG2-11).</summary>
public sealed record SetPortalMachine(Guid CompanyId, Guid SessionId, string IdempotencyKey, string PortalCode, Guid MachineId, string? BatchPlant) : ICommand;

/// <summary>A portal mould (<c>4</c>, <c>6</c>, <c>8</c>) → the finished good it makes.</summary>
public sealed record SetPortalMould(Guid CompanyId, Guid SessionId, string IdempotencyKey, string Mould, Guid ItemId) : ICommand;

/// <summary>A portal shift number → a Core shift (E-MFG2-13).</summary>
public sealed record SetPortalShift(Guid CompanyId, Guid SessionId, string IdempotencyKey, int ShiftNo, Guid ShiftId) : ICommand;

/// <summary>A batch-plant material code → the item, the unit the batch plant sends and the location it is issued from.</summary>
public sealed record SetPortalMaterial(Guid CompanyId, Guid SessionId, string IdempotencyKey, string BatchPlant, string MaterialCode, Guid ItemId, string Uom, Guid LocationId) : ICommand;

/// <summary>Removes a pairing: <c>MACHINE</c> (portal code), <c>MOULD</c>, <c>SHIFT</c> (number) or <c>MATERIAL</c> (<c>batchplant/CODE</c>).</summary>
public sealed record RemovePortalPairing(Guid CompanyId, Guid SessionId, string IdempotencyKey, string Kind, string Key) : ICommand;

internal static class PortalPairing
{
    public const string Aggregate = "PortalPairing";

    public static string Code(string? value, string pattern, string what, bool upper = false)
    {
        var text = (value ?? string.Empty).Trim();
        text = upper ? text.ToUpperInvariant() : text.ToLowerInvariant();
        return System.Text.RegularExpressions.Regex.IsMatch(text, pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1))
            ? text
            : throw new DomainException(ManufacturingErrors.PortalPairingInvalid, $"The {what} is not valid.");
    }

    public static async Task EventAsync(CommandContext context, string kind, string key, object detail, CancellationToken cancellationToken)
    {
        // One aggregate per company: every pairing change is an event of it, in order.
        await MfgSql.LockAsync(context, "portal-pairing", cancellationToken).ConfigureAwait(false);
        var version = await MfgSql.NextEventVersionAsync(context, Aggregate, context.CompanyId, cancellationToken).ConfigureAwait(false);
        await context.AppendEventAsync(
            new EventDraft("PortalPairingChanged", 1, Aggregate, context.CompanyId, version, JsonSerializer.Serialize(new { kind, key, detail }), Publish: true),
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task UpsertAsync(CommandContext context, string table, string columnsAndValues, string setSql, CancellationToken cancellationToken, params (string, object?)[] values)
        => await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            $"INSERT INTO {table} ({columnsAndValues}) ON CONFLICT ON CONSTRAINT {table.Split('.')[1]}_pk DO UPDATE SET {setSql}, version = {table}.version + 1",
            cancellationToken,
            values).ConfigureAwait(false);
}

[RequiresPermission("portal:manage")]
public sealed class SetPortalMachineHandler : ICommandHandler<SetPortalMachine>
{
    public string CommandType => "Manufacturing.SetPortalMachine";

    public async Task<string> HandleAsync(SetPortalMachine command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var code = PortalPairing.Code(command.PortalCode, "^[a-z0-9_-]{1,40}$", "portal machine code");
        var batchPlant = string.IsNullOrWhiteSpace(command.BatchPlant) ? null : PortalPairing.Code(command.BatchPlant, "^[a-z0-9_-]{1,40}$", "batch plant code");
        if (await MfgSql.ScalarAsync<string?>(context, "SELECT status FROM md.machine WHERE company_id = @c AND machine_id = @m", cancellationToken, ("c", context.CompanyId), ("m", command.MachineId))
                .ConfigureAwait(false) is null)
        {
            throw new DomainException(ManufacturingErrors.NotFound, "The machine does not exist.");
        }

        var other = await MfgSql.ScalarAsync<string?>(
            context, "SELECT portal_code FROM mfg.portal_machine WHERE company_id = @c AND machine_id = @m AND portal_code <> @p", cancellationToken,
            ("c", context.CompanyId), ("m", command.MachineId), ("p", code)).ConfigureAwait(false);
        if (other is not null)
        {
            throw new DomainException(ManufacturingErrors.PortalPairingInvalid, $"The machine is already paired with «{other}».");
        }

        await PortalPairing.EventAsync(context, "MACHINE", code, new { machineId = command.MachineId, batchPlant }, cancellationToken).ConfigureAwait(false);
        await PortalPairing.UpsertAsync(
            context, "mfg.portal_machine", "company_id, portal_code, machine_id, batch_plant, version) VALUES (@c, @p, @m, @b, 1", "machine_id = @m, batch_plant = @b", cancellationToken,
            ("c", context.CompanyId), ("p", code), ("m", command.MachineId), ("b", (object?)batchPlant ?? DBNull.Value)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { portalCode = code, machineId = command.MachineId, batchPlant });
    }
}

[RequiresPermission("portal:manage")]
public sealed class SetPortalMouldHandler : ICommandHandler<SetPortalMould>
{
    public string CommandType => "Manufacturing.SetPortalMould";

    public async Task<string> HandleAsync(SetPortalMould command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var mould = PortalPairing.Code(command.Mould, "^[a-z0-9]{1,10}$", "mould");
        if (await MfgSql.ScalarAsync<string?>(
                context, "SELECT item_type FROM md.item WHERE company_id = @c AND item_id = @i", cancellationToken, ("c", context.CompanyId), ("i", command.ItemId)).ConfigureAwait(false)
            != "FINISHED_GOOD")
        {
            throw new DomainException(ManufacturingErrors.PortalPairingInvalid, "A mould is paired with a finished good.");
        }

        await PortalPairing.EventAsync(context, "MOULD", mould, new { itemId = command.ItemId }, cancellationToken).ConfigureAwait(false);
        await PortalPairing.UpsertAsync(
            context, "mfg.portal_mould", "company_id, mould, item_id, version) VALUES (@c, @k, @i, 1", "item_id = @i", cancellationToken,
            ("c", context.CompanyId), ("k", mould), ("i", command.ItemId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { mould, itemId = command.ItemId });
    }
}

[RequiresPermission("portal:manage")]
public sealed class SetPortalShiftHandler : ICommandHandler<SetPortalShift>
{
    public string CommandType => "Manufacturing.SetPortalShift";

    public async Task<string> HandleAsync(SetPortalShift command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        if (command.ShiftNo is < 1 or > 9)
        {
            throw new DomainException(ManufacturingErrors.PortalPairingInvalid, "The portal's shift number is 1 to 9.");
        }

        if (await MfgSql.ScalarAsync<string?>(context, "SELECT code FROM mfg.shift WHERE company_id = @c AND shift_id = @s", cancellationToken, ("c", context.CompanyId), ("s", command.ShiftId))
                .ConfigureAwait(false) is null)
        {
            throw new DomainException(ManufacturingErrors.NotFound, "The shift does not exist.");
        }

        await PortalPairing.EventAsync(context, "SHIFT", command.ShiftNo.ToString(System.Globalization.CultureInfo.InvariantCulture), new { shiftId = command.ShiftId }, cancellationToken)
            .ConfigureAwait(false);
        await PortalPairing.UpsertAsync(
            context, "mfg.portal_shift", "company_id, shift_no, shift_id, version) VALUES (@c, @n, @s, 1", "shift_id = @s", cancellationToken,
            ("c", context.CompanyId), ("n", (short)command.ShiftNo), ("s", command.ShiftId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { shiftNo = command.ShiftNo, shiftId = command.ShiftId });
    }
}

[RequiresPermission("portal:manage")]
public sealed class SetPortalMaterialHandler : ICommandHandler<SetPortalMaterial>
{
    public string CommandType => "Manufacturing.SetPortalMaterial";

    public async Task<string> HandleAsync(SetPortalMaterial command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var batchPlant = PortalPairing.Code(command.BatchPlant, "^[a-z0-9_-]{1,40}$", "batch plant code");
        var code = PortalPairing.Code(command.MaterialCode, "^[A-Z0-9_.-]{1,40}$", "material code", upper: true);
        var uom = (command.Uom ?? string.Empty).Trim();
        var item = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT item_type, base_uom FROM md.item WHERE company_id = @c AND item_id = @i", r => (Type: r.GetString(0), BaseUom: r.GetString(1)),
            cancellationToken, ("c", context.CompanyId), ("i", command.ItemId)).ConfigureAwait(false)).SingleOrDefault();
        if (item == default || item.Type != "RAW_MATERIAL")
        {
            throw new DomainException(ManufacturingErrors.PortalPairingInvalid, "A batch-plant material is paired with a raw material.");
        }

        if (uom != item.BaseUom && await MfgSql.ScalarAsync<int?>(
                context, "SELECT 1 FROM md.uom_conversion WHERE company_id = @c AND item_id = @i AND from_uom = @u AND to_uom = @b AND effective_to IS NULL", cancellationToken,
                ("c", context.CompanyId), ("i", command.ItemId), ("u", uom), ("b", item.BaseUom)).ConfigureAwait(false) is null)
        {
            throw new DomainException(ManufacturingErrors.UomNotConvertible, $"Unit {uom} is neither the item's base unit ({item.BaseUom}) nor has a conversion in force to it.");
        }

        if (await MfgSql.ScalarAsync<Guid?>(
                context, "SELECT location_id FROM md.location WHERE company_id = @c AND location_id = @l AND NOT is_transit AND NOT is_curing", cancellationToken,
                ("c", context.CompanyId), ("l", command.LocationId)).ConfigureAwait(false) is null)
        {
            throw new DomainException(ManufacturingErrors.LocationInvalid, "Materials are issued from a stock location (not transit, not curing).");
        }

        await PortalPairing.EventAsync(context, "MATERIAL", $"{batchPlant}/{code}", new { itemId = command.ItemId, uom, locationId = command.LocationId }, cancellationToken).ConfigureAwait(false);
        await PortalPairing.UpsertAsync(
            context, "mfg.portal_material", "company_id, batch_plant, material_code, item_id, uom, location_id, version) VALUES (@c, @bp, @k, @i, @u, @l, 1",
            "item_id = @i, uom = @u, location_id = @l", cancellationToken,
            ("c", context.CompanyId), ("bp", batchPlant), ("k", code), ("i", command.ItemId), ("u", uom), ("l", command.LocationId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { batchPlant, materialCode = code, itemId = command.ItemId, uom, locationId = command.LocationId });
    }
}

[RequiresPermission("portal:manage")]
public sealed class RemovePortalPairingHandler : ICommandHandler<RemovePortalPairing>
{
    public string CommandType => "Manufacturing.RemovePortalPairing";

    public async Task<string> HandleAsync(RemovePortalPairing command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var key = (command.Key ?? string.Empty).Trim();
        var (sql, parameters) = command.Kind switch
        {
            "MACHINE" => ("DELETE FROM mfg.portal_machine WHERE company_id = @c AND portal_code = @k", new (string, object?)[] { ("k", key.ToLowerInvariant()) }),
            "MOULD" => ("DELETE FROM mfg.portal_mould WHERE company_id = @c AND mould = @k", [("k", key.ToLowerInvariant())]),
            "SHIFT" when short.TryParse(key, out var n) => ("DELETE FROM mfg.portal_shift WHERE company_id = @c AND shift_no = @k", [("k", n)]),
            "MATERIAL" when key.Contains('/', StringComparison.Ordinal) => (
                "DELETE FROM mfg.portal_material WHERE company_id = @c AND batch_plant = @b AND material_code = @k",
                [("b", key[..key.IndexOf('/', StringComparison.Ordinal)].ToLowerInvariant()), ("k", key[(key.IndexOf('/', StringComparison.Ordinal) + 1)..].ToUpperInvariant())]),
            _ => throw new DomainException(ManufacturingErrors.PortalPairingInvalid, "Kind is MACHINE, MOULD, SHIFT or MATERIAL (batchplant/CODE)."),
        };
        await PortalPairing.EventAsync(context, command.Kind, key, new { removed = true }, cancellationToken).ConfigureAwait(false);
        var removed = await Sql.ExecuteAsync(context.Connection, context.Transaction, sql, cancellationToken, [("c", context.CompanyId), .. parameters]).ConfigureAwait(false);
        return removed == 0
            ? throw new DomainException(ManufacturingErrors.NotFound, "There is no such pairing.")
            : JsonSerializer.Serialize(new { kind = command.Kind, key, removed });
    }
}

/// <summary>MFG2-03: the pairings, the connection's state and what is waiting (E-MFG2-3/7/8), for Producción › Portal and Inicio.</summary>
public sealed record GetPortalSetup(Guid CompanyId, Guid SessionId) : IQuery;

public sealed record PortalMachineView(string PortalCode, Guid MachineId, string MachineCode, string? BatchPlant);

public sealed record PortalMouldView(string Mould, Guid ItemId, string ItemCode, string ItemDescription);

public sealed record PortalShiftView(int ShiftNo, Guid ShiftId, string ShiftCode, TimeOnly StartsAt, TimeOnly EndsAt);

public sealed record PortalMaterialView(string BatchPlant, string MaterialCode, Guid ItemId, string ItemCode, string Uom, Guid LocationId, string LocationCode);

public sealed record PortalSetupView(
    IReadOnlyList<PortalMachineView> Machines, IReadOnlyList<PortalMouldView> Moulds, IReadOnlyList<PortalShiftView> Shifts, IReadOnlyList<PortalMaterialView> Materials,
    DateTime? LastOkAt, string? LastError, DateTime? LastErrorAt, IReadOnlyList<string> Warnings, IReadOnlyList<string> UnpairedMachines, int PendingConsumption,
    int OutOfTolerance);

[RequiresPermission("production:read")]
public sealed class GetPortalSetupHandler : IQueryHandler<GetPortalSetup>
{
    public string QueryType => "Manufacturing.GetPortalSetup";

    public async Task<string> HandleAsync(GetPortalSetup query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var machines = await Reading.ListAsync(
            context.Connection, context.Transaction,
            "SELECT p.portal_code, p.machine_id, m.code, p.batch_plant FROM mfg.portal_machine p JOIN md.machine m ON m.machine_id = p.machine_id WHERE p.company_id = @c ORDER BY p.portal_code",
            r => new PortalMachineView(r.GetString(0), r.GetGuid(1), r.GetString(2), r.NullableString(3)), cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false);
        var moulds = await Reading.ListAsync(
            context.Connection, context.Transaction,
            "SELECT p.mould, p.item_id, i.code, i.description FROM mfg.portal_mould p JOIN md.item i ON i.item_id = p.item_id WHERE p.company_id = @c ORDER BY p.mould",
            r => new PortalMouldView(r.GetString(0), r.GetGuid(1), r.GetString(2), r.GetString(3)), cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false);
        var shifts = await Reading.ListAsync(
            context.Connection, context.Transaction,
            "SELECT p.shift_no, p.shift_id, s.code, s.starts_at, s.ends_at FROM mfg.portal_shift p JOIN mfg.shift s ON s.shift_id = p.shift_id WHERE p.company_id = @c ORDER BY p.shift_no",
            r => new PortalShiftView(r.GetInt16(0), r.GetGuid(1), r.GetString(2), TimeOnly.FromTimeSpan(r.GetFieldValue<TimeSpan>(3)), TimeOnly.FromTimeSpan(r.GetFieldValue<TimeSpan>(4))),
            cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false);
        var materials = await Reading.ListAsync(
            context.Connection, context.Transaction,
            """
            SELECT p.batch_plant, p.material_code, p.item_id, i.code, p.uom, p.location_id, l.code
            FROM mfg.portal_material p JOIN md.item i ON i.item_id = p.item_id JOIN md.location l ON l.location_id = p.location_id
            WHERE p.company_id = @c ORDER BY p.batch_plant, p.material_code
            """,
            r => new PortalMaterialView(r.GetString(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetGuid(5), r.GetString(6)), cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        var state = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT last_ok_at, last_error, last_error_at, warnings::text FROM mfg.portal_sync_state WHERE company_id = @c",
            r => (Ok: r.IsDBNull(0) ? (DateTime?)null : r.Utc(0), Error: r.NullableString(1), ErrorAt: r.IsDBNull(2) ? (DateTime?)null : r.Utc(2),
                Warnings: JsonSerializer.Deserialize<List<string>>(r.GetString(3)) ?? []),
            cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false)).SingleOrDefault();
        var unpaired = await Reading.ListAsync(
            context.Connection, context.Transaction,
            "SELECT DISTINCT r.portal_code FROM mfg.portal_reading r WHERE r.company_id = @c AND NOT EXISTS (SELECT 1 FROM mfg.portal_machine p WHERE p.company_id = r.company_id AND p.portal_code = r.portal_code) ORDER BY 1",
            r => r.GetString(0), cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false);
        var counts = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT count(*) FILTER (WHERE s.consumption_source = 'PENDING')::int,
                   count(*) FILTER (WHERE EXISTS (
                     SELECT 1 FROM mfg.material_consumption c JOIN mfg.production_run r ON r.run_id = s.run_id
                     CROSS JOIN LATERAL (SELECT (x.value #>> '{}')::numeric AS pct FROM acc.accounting_policy_version v JOIN acc.accounting_policy_parameter x ON x.policy_version_id = v.policy_version_id
                                         WHERE v.company_id = s.company_id AND v.policy_code = 'PRODUCTION' AND v.status = 'ACTIVE' AND x.param_code = 'usage_tolerance_pct'
                                           AND v.effective_from <= r.business_date AND (v.effective_to IS NULL OR v.effective_to > r.business_date) LIMIT 1) t
                     WHERE c.summary_id = s.summary_id AND s.consumption_source = 'BATCH_PLANT' AND abs(c.qty - c.theoretical_qty) > t.pct * c.theoretical_qty))::int
            FROM mfg.shift_summary s WHERE s.company_id = @c AND s.status = 'DRAFT' AND s.source = 'PORTAL'
            """,
            r => (Pending: r.GetInt32(0), Out: r.GetInt32(1)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false)).Single();
        return ApiJson.Serialize(new PortalSetupView(
            machines, moulds, shifts, materials, state.Ok, state.Error, state.ErrorAt, state.Warnings ?? [], unpaired, counts.Pending, counts.Out));
    }
}
