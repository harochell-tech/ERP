using System.Globalization;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;
using Rochell.Platform.Time;

namespace Rochell.Manufacturing.Portal;

// MFG3-02 (E-MFG3-3…7, E-MFG3-01-2): the ideal cycle per machine and product, and each machine's efficiency per shift from what the
// portal reported — planned time without scheduled maintenance, stoppages, cycles per mould — and the shift's summary (good and scrap).

/// <summary>E-MFG3-5, E-MFG3-01-2: the ideal seconds per cycle of a machine making a product, from a date on.</summary>
public sealed record SetIdealCycle(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid MachineId, Guid ItemId, DateOnly ValidFrom, decimal Seconds) : ICommand;

[RequiresPermission("production_master:manage")]
public sealed class SetIdealCycleHandler : ICommandHandler<SetIdealCycle>
{
    public string CommandType => "Manufacturing.SetIdealCycle";

    public async Task<string> HandleAsync(SetIdealCycle command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        if (command.Seconds <= 0m || command.Seconds > 3600m || decimal.Round(command.Seconds, 3) != command.Seconds) // type-limit: an hour per cycle at most
        {
            throw new DomainException(ManufacturingErrors.IdealCycleInvalid, "The ideal cycle is more than 0 and at most 3,600 seconds, with up to 3 decimals.");
        }

        var machine = await MfgSql.ScalarAsync<string>(
            context, "SELECT status FROM md.machine WHERE company_id = @c AND machine_id = @m", cancellationToken, ("c", context.CompanyId), ("m", command.MachineId)).ConfigureAwait(false);
        var item = await MfgSql.ScalarAsync<string>(
            context, "SELECT item_type FROM md.item WHERE company_id = @c AND item_id = @i", cancellationToken, ("c", context.CompanyId), ("i", command.ItemId)).ConfigureAwait(false);
        if (machine is null || item != "FINISHED_GOOD")
        {
            throw new DomainException(ManufacturingErrors.IdealCycleInvalid, "A machine of the company and a finished good.");
        }

        var exists = await MfgSql.ScalarAsync<long>(
            context, "SELECT count(*) FROM mfg.ideal_cycle WHERE company_id = @c AND machine_id = @m AND item_id = @i AND valid_from = @f", cancellationToken,
            ("c", context.CompanyId), ("m", command.MachineId), ("i", command.ItemId), ("f", command.ValidFrom)).ConfigureAwait(false);
        if (exists > 0)
        {
            throw new DomainException(ManufacturingErrors.IdealCycleInvalid, "There is already an ideal cycle from that date: give the new one a later date.");
        }

        var by = await MfgSql.ScalarAsync<Guid>(
            context, "SELECT user_id FROM iam.session WHERE session_id = @s", cancellationToken, ("s", context.SessionId)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "INSERT INTO mfg.ideal_cycle (company_id, machine_id, item_id, valid_from, seconds, set_by, set_at) VALUES (@c, @m, @i, @f, @s, @by, @at)",
            cancellationToken,
            ("c", context.CompanyId), ("m", command.MachineId), ("i", command.ItemId), ("f", command.ValidFrom), ("s", command.Seconds), ("by", by), ("at", context.Clock.UtcNow))
            .ConfigureAwait(false);
        await context.AppendEventAsync(
            new EventDraft("IdealCycleSet", 1, "IdealCycle", context.Ids.NewId(), 1,
                JsonSerializer.Serialize(new
                {
                    machineId = command.MachineId,
                    itemId = command.ItemId,
                    validFrom = command.ValidFrom,
                    seconds = command.Seconds.ToString(CultureInfo.InvariantCulture),
                }),
                Publish: false),
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { machineId = command.MachineId, itemId = command.ItemId, validFrom = command.ValidFrom });
    }
}

/// <summary>E-MFG3-4/6/7: each paired machine's efficiency per shift in a period, and its totals with the stoppages' reasons.</summary>
public sealed record GetMachineEfficiency(Guid CompanyId, Guid SessionId, DateOnly From, DateOnly To) : IQuery;

public sealed record StoppageReasonView(string Reason, int Count, decimal Minutes);

/// <remarks>
/// Ratios are 0–1 with 4 decimals; null when they cannot be computed (no running time, no ideal cycle for a product, no summary).
/// <c>LostUnits</c> = stoppage seconds ÷ ideal cycle × blocks per cycle of the shift's main mould; <c>LostValue</c> at the product's
/// ACTIVE standard cost (information only, E-MFG3-6).
/// </remarks>
public sealed record ShiftEfficiencyView(
    DateOnly Date, int ShiftNo, string PortalCode, Guid MachineId, string MachineCode, string MachineName, decimal PlannedMinutes, decimal MaintenanceMinutes,
    decimal StoppageMinutes, decimal RunningMinutes, int Cycles, decimal? Availability, decimal? Performance, decimal? Quality, decimal? Oee, decimal? LostUnits, decimal? LostValue,
    int StoppagesWithoutReason, IReadOnlyList<StoppageReasonView> Reasons);

public sealed record MachineEfficiencyTotal(
    Guid MachineId, string MachineCode, string MachineName, decimal PlannedMinutes, decimal StoppageMinutes, decimal RunningMinutes, decimal? Availability, decimal? Performance,
    decimal? Quality, decimal? Oee, decimal? LostUnits, decimal? LostValue, IReadOnlyList<StoppageReasonView> Reasons);

public sealed record MachineEfficiency(
    DateOnly From, DateOnly To, IReadOnlyList<ShiftEfficiencyView> Shifts, IReadOnlyList<MachineEfficiencyTotal> Machines, IReadOnlyList<string> MissingIdealCycles,
    int StoppagesWithoutReason);

[RequiresPermission("production:read")]
public sealed class GetMachineEfficiencyHandler : IQueryHandler<GetMachineEfficiency>
{
    private sealed record Machine(string Code, Guid MachineId, string MachineCode, string Name, Guid PlantId);

    private sealed record ShiftRow(DateOnly Date, int ShiftNo, DateTime From, DateTime To, int Cycles, string Moulds);

    private sealed record Window(DateTime From, DateTime? To);

    private sealed record Stop(DateTime Start, DateTime? End, int? Seconds, string? Reason);

    private sealed record Totals(decimal Good, decimal Scrap);

    private sealed record Acc(decimal Planned, decimal Stop, decimal Running, decimal IdealSeconds, bool PerformanceKnown, decimal Good, decimal Scrap, bool QualityKnown, decimal? Lost, decimal? Value);

    public string QueryType => "Manufacturing.GetMachineEfficiency";

    public async Task<string> HandleAsync(GetMachineEfficiency query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        if (query.To < query.From || query.To.DayNumber - query.From.DayNumber > 92)
        {
            throw new DomainException(QueryErrors.InvalidParameter, "A period of at most 93 days.");
        }

        var today = BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);
        var localNow = today.ToDateTime(TimeOnly.MinValue) + (context.Clock.UtcNow - BusinessCalendar.DayUtcRange(today).StartUtc);
        var machines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT p.portal_code, p.machine_id, m.code, m.name, m.plant_id FROM mfg.portal_machine p JOIN md.machine m ON m.machine_id = p.machine_id
            WHERE p.company_id = @c ORDER BY m.code
            """,
            r => new Machine(r.GetString(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetGuid(4)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        var moulds = await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT mould, item_id FROM mfg.portal_mould WHERE company_id = @c", r => (Mould: r.GetString(0), Item: r.GetGuid(1)), cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        var shifts = new List<ShiftEfficiencyView>();
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var totals = new List<MachineEfficiencyTotal>();
        var unreasoned = 0;
        foreach (var machine in machines)
        {
            var rows = await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT DISTINCT ON (shift_date, shift_no) shift_date, shift_no, window_from, window_to, cycles, moulds::text FROM mfg.portal_reading
                WHERE company_id = @c AND portal_code = @p AND shift_date BETWEEN @f AND @t ORDER BY shift_date, shift_no, fetched_at DESC
                """,
                r => new ShiftRow(r.Date(0), r.GetInt16(1), r.GetDateTime(2), r.GetDateTime(3), r.GetInt32(4), r.GetString(5)),
                cancellationToken,
                ("c", context.CompanyId), ("p", machine.Code), ("f", query.From), ("t", query.To)).ConfigureAwait(false);
            var windows = await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT DISTINCT ON (portal_id) starts_at, ends_at FROM mfg.portal_maintenance
                WHERE company_id = @c AND portal_code = @p AND starts_at < @t ORDER BY portal_id, fetched_at DESC
                """,
                r => new Window(r.GetDateTime(0), r.IsDBNull(1) ? null : r.GetDateTime(1)),
                cancellationToken,
                ("c", context.CompanyId), ("p", machine.Code), ("t", query.To.AddDays(2).ToDateTime(TimeOnly.MinValue))).ConfigureAwait(false);
            var stops = await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT DISTINCT ON (portal_id) started_at, ended_at, duration_seconds, reason FROM mfg.portal_stoppage
                WHERE company_id = @c AND portal_code = @p AND started_at >= @f AND started_at < @t ORDER BY portal_id, fetched_at DESC
                """,
                r => new Stop(r.GetDateTime(0), r.IsDBNull(1) ? null : r.GetDateTime(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.NullableString(3)),
                cancellationToken,
                ("c", context.CompanyId), ("p", machine.Code), ("f", query.From.AddDays(-1).ToDateTime(TimeOnly.MinValue)), ("t", query.To.AddDays(2).ToDateTime(TimeOnly.MinValue)))
                .ConfigureAwait(false);
            var acc = new Acc(0m, 0m, 0m, 0m, true, 0m, 0m, true, 0m, 0m);
            var machineReasons = new Dictionary<string, (int Count, decimal Minutes)>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var end = row.To < localNow ? row.To : localNow;
                var planned = Minutes(row.From, end) - windows.Sum(w => Minutes(Max(w.From, row.From), Min(w.To ?? end, end)));
                var maintenance = Minutes(row.From, end) - planned;
                var inShift = stops.Where(s => s.Start >= row.From && s.Start < end && !windows.Any(w => s.Start >= w.From && s.Start < (w.To ?? end))).ToList();
                var reasons = new Dictionary<string, (int Count, decimal Minutes)>(StringComparer.Ordinal);
                decimal stopMinutes = 0m;
                foreach (var stop in inShift)
                {
                    var stopEnd = stop.Seconds is { } secs ? stop.Start.AddSeconds(secs) : stop.End ?? end;
                    var minutes = Minutes(stop.Start, Min(stopEnd, end));
                    stopMinutes += minutes;
                    var reason = stop.Reason ?? "SIN_RAZON";
                    reasons[reason] = (reasons.GetValueOrDefault(reason).Count + 1, reasons.GetValueOrDefault(reason).Minutes + minutes);
                    machineReasons[reason] = (machineReasons.GetValueOrDefault(reason).Count + 1, machineReasons.GetValueOrDefault(reason).Minutes + minutes);
                }

                var withoutReason = inShift.Count(s => s.Reason is null);
                unreasoned += withoutReason;
                var running = Math.Max(0m, planned - stopMinutes);

                // Performance: Σ cycles × ideal seconds of each mould's product ÷ running seconds.
                var parsed = JsonSerializer.Deserialize<List<PortalMould>>(row.Moulds, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
                decimal idealSeconds = 0m;
                var known = true;
                decimal? lostUnits = null;
                decimal? lostValue = null;
                foreach (var mould in parsed.Where(m => m.Cycles > 0))
                {
                    var item = moulds.FirstOrDefault(m => m.Mould == mould.Mould).Item;
                    var ideal = item == Guid.Empty ? null : await IdealAsync(context, machine.MachineId, item, row.Date, cancellationToken).ConfigureAwait(false);
                    if (ideal is not { } seconds)
                    {
                        known = false;
                        missing.Add($"{machine.MachineCode} · molde {mould.Mould}\"");
                        continue;
                    }

                    idealSeconds += mould.Cycles * seconds;
                }

                var main = parsed.Where(m => m.Cycles > 0).OrderByDescending(m => m.Cycles).FirstOrDefault();
                if (main is not null && moulds.FirstOrDefault(m => m.Mould == main.Mould).Item is var mainItem && mainItem != Guid.Empty
                    && await IdealAsync(context, machine.MachineId, mainItem, row.Date, cancellationToken).ConfigureAwait(false) is { } mainSeconds)
                {
                    lostUnits = decimal.Round(stopMinutes * 60 / mainSeconds * main.BlocksPerCycle, 0, MidpointRounding.AwayFromZero);
                    var cost = (await Reading.ListAsync(
                        context.Connection,
                        context.Transaction,
                        """
                        SELECT c.unit_cost FROM md.standard_cost_version c JOIN md.plant p ON p.valuation_area_id = c.valuation_area_id
                        WHERE c.company_id = @c AND c.item_id = @i AND p.plant_id = @p AND c.status = 'ACTIVE'
                        """,
                        r => (decimal?)r.GetDecimal(0),
                        cancellationToken,
                        ("c", context.CompanyId), ("i", mainItem), ("p", machine.PlantId)).ConfigureAwait(false)).FirstOrDefault();
                    lostValue = cost is { } unitCost ? decimal.Round(lostUnits.Value * unitCost, 2, MidpointRounding.AwayFromZero) : null;
                }

                var quality = await QualityAsync(context, machine.MachineId, row.Date, row.ShiftNo, cancellationToken).ConfigureAwait(false);
                decimal? availability = planned > 0m ? Ratio(running / planned) : null;
                decimal? performance = known && running > 0m && parsed.Any(m => m.Cycles > 0) ? Ratio(idealSeconds / (running * 60)) : null;
                decimal? q = quality is { } t && t.Good + t.Scrap > 0m ? Ratio(t.Good / (t.Good + t.Scrap)) : null;
                decimal? oee = availability is { } a && performance is { } p && q is { } qq ? Ratio(a * p * qq) : null;
                shifts.Add(new ShiftEfficiencyView(
                    row.Date, row.ShiftNo, machine.Code, machine.MachineId, machine.MachineCode, machine.Name, Round(planned), Round(maintenance), Round(stopMinutes), Round(running), row.Cycles,
                    availability, performance, q, oee, lostUnits, lostValue, withoutReason, Reasons(reasons)));
                acc = acc with
                {
                    Planned = acc.Planned + planned,
                    Stop = acc.Stop + stopMinutes,
                    Running = acc.Running + running,
                    IdealSeconds = acc.IdealSeconds + idealSeconds,
                    PerformanceKnown = acc.PerformanceKnown && known,
                    Good = acc.Good + (quality?.Good ?? 0m),
                    Scrap = acc.Scrap + (quality?.Scrap ?? 0m),
                    QualityKnown = acc.QualityKnown && quality is not null,
                    Lost = acc.Lost is { } l && lostUnits is { } lu ? l + lu : null,
                    Value = acc.Value is { } v && lostValue is { } lv ? v + lv : null,
                };
            }

            if (rows.Count == 0)
            {
                continue;
            }

            decimal? ma = acc.Planned > 0m ? Ratio(acc.Running / acc.Planned) : null;
            decimal? mp = acc.PerformanceKnown && acc.Running > 0m ? Ratio(acc.IdealSeconds / (acc.Running * 60)) : null;
            decimal? mq = acc.QualityKnown && acc.Good + acc.Scrap > 0m ? Ratio(acc.Good / (acc.Good + acc.Scrap)) : null;
            totals.Add(new MachineEfficiencyTotal(
                machine.MachineId, machine.MachineCode, machine.Name, Round(acc.Planned), Round(acc.Stop), Round(acc.Running), ma, mp, mq,
                ma is { } x && mp is { } y && mq is { } z ? Ratio(x * y * z) : null, acc.Lost, acc.Value, Reasons(machineReasons)));
        }

        return ApiJson.Serialize(new MachineEfficiency(query.From, query.To, shifts, totals, [.. missing], unreasoned));
    }

    private static async Task<decimal?> IdealAsync(QueryContext context, Guid machine, Guid item, DateOnly date, CancellationToken cancellationToken)
        => (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT seconds FROM mfg.ideal_cycle WHERE company_id = @c AND machine_id = @m AND item_id = @i AND valid_from <= @d ORDER BY valid_from DESC LIMIT 1",
            r => (decimal?)r.GetDecimal(0),
            cancellationToken,
            ("c", context.CompanyId), ("m", machine), ("i", item), ("d", date)).ConfigureAwait(false)).SingleOrDefault();

    /// <summary>The good units and scrap of the machine's summaries of that date and portal shift (draft or posted, not reversed).</summary>
    private static async Task<Totals?> QualityAsync(QueryContext context, Guid machine, DateOnly date, int shiftNo, CancellationToken cancellationToken)
        => await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT sum(s.good_units), sum(s.mix_scrap_units + s.fresh_scrap_units)
            FROM mfg.shift_summary s JOIN mfg.production_run r ON r.run_id = s.run_id JOIN mfg.portal_shift ps ON ps.shift_id = r.shift_id AND ps.company_id = r.company_id
            WHERE r.company_id = @c AND r.machine_id = @m AND r.business_date = @d AND ps.shift_no = @n AND s.status IN ('DRAFT', 'POSTED')
            HAVING count(*) > 0
            """,
            r => new Totals(r.GetDecimal(0), r.GetDecimal(1)),
            cancellationToken,
            ("c", context.CompanyId), ("m", machine), ("d", date), ("n", (short)shiftNo)).ConfigureAwait(false);

    private static decimal Minutes(DateTime from, DateTime? to) => to is { } t && t > from ? (decimal)(t - from).Ticks / TimeSpan.TicksPerMinute : 0m;

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private static decimal Ratio(decimal value) => decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    private static decimal Round(decimal minutes) => decimal.Round(minutes, 1, MidpointRounding.AwayFromZero);

    private static IReadOnlyList<StoppageReasonView> Reasons(Dictionary<string, (int Count, decimal Minutes)> reasons)
        => [.. reasons.OrderByDescending(r => r.Value.Minutes).Select(r => new StoppageReasonView(r.Key, r.Value.Count, Round(r.Value.Minutes)))];
}

/// <summary>E-MFG3-5: the ideal cycles in force today per machine and product, with when they started and the earlier ones.</summary>
public sealed record ListIdealCycles(Guid CompanyId, Guid SessionId) : IQuery;

public sealed record IdealCycleView(Guid MachineId, string MachineCode, Guid ItemId, string ItemCode, DateOnly ValidFrom, decimal Seconds, bool InForce);

public sealed record IdealCycleList(IReadOnlyList<IdealCycleView> Items);

[RequiresPermission("production:read")]
public sealed class ListIdealCyclesHandler : IQueryHandler<ListIdealCycles>
{
    public string QueryType => "Manufacturing.ListIdealCycles";

    public async Task<string> HandleAsync(ListIdealCycles query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var today = BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT c.machine_id, m.code, c.item_id, i.code, c.valid_from, c.seconds,
                   c.valid_from = (SELECT max(x.valid_from) FROM mfg.ideal_cycle x WHERE x.company_id = c.company_id AND x.machine_id = c.machine_id AND x.item_id = c.item_id
                                   AND x.valid_from <= @today)
            FROM mfg.ideal_cycle c JOIN md.machine m ON m.machine_id = c.machine_id JOIN md.item i ON i.item_id = c.item_id
            WHERE c.company_id = @c ORDER BY m.code, i.code, c.valid_from DESC
            """,
            r => new IdealCycleView(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.Date(4), r.GetDecimal(5), r.GetBoolean(6)),
            cancellationToken,
            ("c", context.CompanyId), ("today", today)).ConfigureAwait(false);
        return ApiJson.Serialize(new IdealCycleList(items));
    }
}
