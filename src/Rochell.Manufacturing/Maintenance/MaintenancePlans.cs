using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;
using Rochell.Platform.Time;

namespace Rochell.Manufacturing.Maintenance;

// MFG3-03 (E-MFG3-8…10, E-MFG3-01-3/5): preventive maintenance. The plant manager (maintenance_plan:manage) defines tasks per machine
// every N cycles, running hours or days; the mechanic marks them done in the portal (its maintenance window names the task's code) or
// the manager records them here; each task is due from its last done (or its creation) and shows «Por vencer» at 90 %, «Vencida» at 100 %.

public static class MaintenanceErrors
{
    public const string TaskInvalid = "MAINTENANCE_TASK_INVALID";
}

public static class MaintenanceKinds
{
    public const string Cycles = "CYCLES";
    public const string RunningHours = "RUNNING_HOURS";
    public const string Days = "DAYS";
}

public sealed record DefineMaintenanceTask(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid MachineId, string Code, string Name, string FrequencyKind, int Every, string? Instructions = null) : ICommand;

public sealed record UpdateMaintenanceTask(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid TaskId, long ExpectedVersion, string Name, string FrequencyKind, int Every, string? Instructions = null) : ICommand;

/// <summary>E-MFG3-01-3: tasks are deactivated (and reactivated), never deleted.</summary>
public sealed record SetMaintenanceTaskStatus(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid TaskId, long ExpectedVersion, string Status) : ICommand;

/// <summary>E-MFG3-9: the plant manager records a task done in Core (a mechanic does it in the portal); <paramref name="DoneAt"/> is UTC.</summary>
public sealed record RecordMaintenanceDone(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid TaskId, DateTime DoneAt, string? Note = null) : ICommand;

internal static partial class Tasks
{
    public const string Aggregate = "MaintenanceTask";

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{0,29}$")]
    public static partial Regex Code();

    public static (string Name, string? Instructions) Validate(string name, string kind, int every, string? instructions)
    {
        var n = (name ?? string.Empty).Trim();
        var i = string.IsNullOrWhiteSpace(instructions) ? null : instructions.Trim();
        if (n.Length is 0 or > 120 || i is { Length: > 2000 } || kind is not (MaintenanceKinds.Cycles or MaintenanceKinds.RunningHours or MaintenanceKinds.Days) || every <= 0)
        {
            throw new DomainException(MaintenanceErrors.TaskInvalid, "A name (120 characters at most), every N cycles, running hours or days (N above 0), instructions of at most 2,000 characters.");
        }

        return (n, i);
    }

    public static async Task<long> LockAsync(CommandContext context, Guid taskId, long expectedVersion, CancellationToken cancellationToken)
    {
        await MfgSql.LockAsync(context, $"maintenance-task:{taskId}", cancellationToken).ConfigureAwait(false);
        var version = await MfgSql.ScalarAsync<long?>(
            context, "SELECT version FROM mfg.maintenance_task WHERE company_id = @c AND task_id = @t", cancellationToken, ("c", context.CompanyId), ("t", taskId)).ConfigureAwait(false)
            ?? throw new DomainException(ManufacturingErrors.NotFound, "The maintenance task does not exist.");
        if (version != expectedVersion)
        {
            throw new DomainException(ManufacturingErrors.VersionConflict, "The task changed; reload it.");
        }

        return version;
    }

    /// <summary>A Dominican local time (the portal's clock) from a UTC instant.</summary>
    public static DateTime Local(DateTime utc)
    {
        var day = BusinessCalendar.DefaultBusinessDate(utc);
        return day.ToDateTime(TimeOnly.MinValue) + (utc - BusinessCalendar.DayUtcRange(day).StartUtc);
    }
}

[RequiresPermission("maintenance_plan:manage")]
public sealed class DefineMaintenanceTaskHandler : ICommandHandler<DefineMaintenanceTask>
{
    public string CommandType => "Manufacturing.DefineMaintenanceTask";

    public async Task<string> HandleAsync(DefineMaintenanceTask command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var code = (command.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (!Tasks.Code().IsMatch(code))
        {
            throw new DomainException(MaintenanceErrors.TaskInvalid, "The code is 1–30 capital letters, digits, - or _ (it is what the mechanic chooses in the portal).");
        }

        var (name, instructions) = Tasks.Validate(command.Name, command.FrequencyKind, command.Every, command.Instructions);
        if (await MfgSql.ScalarAsync<string>(
                context, "SELECT status FROM md.machine WHERE company_id = @c AND machine_id = @m", cancellationToken, ("c", context.CompanyId), ("m", command.MachineId)).ConfigureAwait(false) is null)
        {
            throw new DomainException(ManufacturingErrors.NotFound, "The machine does not exist.");
        }

        if (await MfgSql.ScalarAsync<long>(context, "SELECT count(*) FROM mfg.maintenance_task WHERE company_id = @c AND code = @code", cancellationToken, ("c", context.CompanyId), ("code", code))
                .ConfigureAwait(false) > 0)
        {
            throw new DomainException(MaintenanceErrors.TaskInvalid, $"There is already a task {code}.");
        }

        var id = context.Ids.NewId();
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO mfg.maintenance_task (task_id, company_id, machine_id, code, name, frequency_kind, every, instructions, status, created_by, created_at, version)
            VALUES (@id, @c, @m, @code, @name, @kind, @every, @ins, 'ACTIVE', @by, @at, 1)
            """,
            cancellationToken,
            ("id", id), ("c", context.CompanyId), ("m", command.MachineId), ("code", code), ("name", name), ("kind", command.FrequencyKind), ("every", command.Every),
            ("ins", instructions), ("by", await MfgSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false)), ("at", context.Clock.UtcNow)).ConfigureAwait(false);
        await context.AppendEventAsync(
            new EventDraft("MaintenanceTaskDefined", 1, Tasks.Aggregate, id, 1, JsonSerializer.Serialize(new { taskId = id, code, machineId = command.MachineId, command.FrequencyKind, command.Every }),
                Publish: false),
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { taskId = id, code });
    }
}

[RequiresPermission("maintenance_plan:manage")]
public sealed class UpdateMaintenanceTaskHandler : ICommandHandler<UpdateMaintenanceTask>
{
    public string CommandType => "Manufacturing.UpdateMaintenanceTask";

    public async Task<string> HandleAsync(UpdateMaintenanceTask command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var (name, instructions) = Tasks.Validate(command.Name, command.FrequencyKind, command.Every, command.Instructions);
        var version = await Tasks.LockAsync(context, command.TaskId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false) + 1;
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE mfg.maintenance_task SET name = @name, frequency_kind = @kind, every = @every, instructions = @ins, version = @v WHERE company_id = @c AND task_id = @t",
            cancellationToken,
            ("name", name), ("kind", command.FrequencyKind), ("every", command.Every), ("ins", instructions), ("v", version), ("c", context.CompanyId), ("t", command.TaskId)).ConfigureAwait(false);
        await context.AppendEventAsync(
            new EventDraft("MaintenanceTaskUpdated", 1, Tasks.Aggregate, command.TaskId, version, JsonSerializer.Serialize(new { taskId = command.TaskId, command.FrequencyKind, command.Every }),
                Publish: false),
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { taskId = command.TaskId, version });
    }
}

[RequiresPermission("maintenance_plan:manage")]
public sealed class SetMaintenanceTaskStatusHandler : ICommandHandler<SetMaintenanceTaskStatus>
{
    public string CommandType => "Manufacturing.SetMaintenanceTaskStatus";

    public async Task<string> HandleAsync(SetMaintenanceTaskStatus command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        if (command.Status is not ("ACTIVE" or "INACTIVE"))
        {
            throw new DomainException(MaintenanceErrors.TaskInvalid, "A task is ACTIVE or INACTIVE.");
        }

        var version = await Tasks.LockAsync(context, command.TaskId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false) + 1;
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE mfg.maintenance_task SET status = @s, version = @v WHERE company_id = @c AND task_id = @t", cancellationToken,
            ("s", command.Status), ("v", version), ("c", context.CompanyId), ("t", command.TaskId)).ConfigureAwait(false);
        await context.AppendEventAsync(
            new EventDraft("MaintenanceTaskStatusChanged", 1, Tasks.Aggregate, command.TaskId, version, JsonSerializer.Serialize(new { taskId = command.TaskId, status = command.Status }),
                Publish: false),
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { taskId = command.TaskId, status = command.Status, version });
    }
}

[RequiresPermission("maintenance_plan:manage")]
public sealed class RecordMaintenanceDoneHandler : ICommandHandler<RecordMaintenanceDone>
{
    public string CommandType => "Manufacturing.RecordMaintenanceDone";

    public async Task<string> HandleAsync(RecordMaintenanceDone command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var note = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim();
        if (note is { Length: > 500 } || command.DoneAt > context.Clock.UtcNow)
        {
            throw new DomainException(MaintenanceErrors.TaskInvalid, "When it was done (not in the future) and a note of at most 500 characters.");
        }

        var status = await MfgSql.ScalarAsync<string>(
            context, "SELECT status FROM mfg.maintenance_task WHERE company_id = @c AND task_id = @t", cancellationToken, ("c", context.CompanyId), ("t", command.TaskId)).ConfigureAwait(false)
            ?? throw new DomainException(ManufacturingErrors.NotFound, "The maintenance task does not exist.");
        if (status != "ACTIVE")
        {
            throw new DomainException(MaintenanceErrors.TaskInvalid, "The task is inactive.");
        }

        var id = context.Ids.NewId();
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO mfg.maintenance_done (done_id, company_id, task_id, done_at, source, recorded_by, note, recorded_at)
            VALUES (@id, @c, @t, @at, 'CORE', @by, @note, @now)
            """,
            cancellationToken,
            ("id", id), ("c", context.CompanyId), ("t", command.TaskId), ("at", Tasks.Local(command.DoneAt)),
            ("by", await MfgSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false)), ("note", note), ("now", context.Clock.UtcNow)).ConfigureAwait(false);
        await context.AppendEventAsync(
            new EventDraft("MaintenanceDone", 1, "MaintenanceDone", id, 1, JsonSerializer.Serialize(new { taskId = command.TaskId, source = "CORE" }), Publish: false),
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { doneId = id, taskId = command.TaskId });
    }
}

/// <summary>E-MFG3-8/10: every task with how much of its interval has gone since it was last done, and the counts for Inicio.</summary>
public sealed record ListMaintenanceTasks(Guid CompanyId, Guid SessionId) : IQuery;

/// <remarks><paramref name="DoneAt"/> is Dominican local time, «yyyy-MM-dd HH:mm» (the portal's clock).</remarks>
public sealed record MaintenanceDoneView(string DoneAt, string Source, string? By, string? Note);

/// <remarks>
/// <c>Since</c> is cycles, running hours (planned shift time minus stoppages, E-MFG3-01-5) or days since the last done (or the task's
/// creation); <c>Used</c> = since ÷ every (4 decimals); <c>State</c> OK, POR_VENCER (≥ 0.9), VENCIDA (≥ 1), or INACTIVE.
/// </remarks>
public sealed record MaintenanceTaskView(
    Guid TaskId, Guid MachineId, string MachineCode, string Code, string Name, string FrequencyKind, int Every, string? Instructions, string Status, long Version,
    string? LastDoneAt, decimal Since, decimal Used, string State, IReadOnlyList<MaintenanceDoneView> Done);

public sealed record MaintenanceTaskList(IReadOnlyList<MaintenanceTaskView> Items, int DueSoon, int Overdue);

[RequiresPermission("production:read")]
public sealed class ListMaintenanceTasksHandler : IQueryHandler<ListMaintenanceTasks>
{
    private sealed record Row(Guid TaskId, Guid MachineId, string MachineCode, string Code, string Name, string Kind, int Every, string? Instructions, string Status, long Version, DateTime CreatedAt);

    private sealed record Shift(DateTime From, DateTime To, int Cycles);

    private sealed record Interval(DateTime From, DateTime? To);

    public string QueryType => "Manufacturing.ListMaintenanceTasks";

    public async Task<string> HandleAsync(ListMaintenanceTasks query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var now = Tasks.Local(context.Clock.UtcNow);
        var rows = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT t.task_id, t.machine_id, m.code, t.code, t.name, t.frequency_kind, t.every, t.instructions, t.status, t.version, t.created_at
            FROM mfg.maintenance_task t JOIN md.machine m ON m.machine_id = t.machine_id WHERE t.company_id = @c ORDER BY m.code, t.code
            """,
            r => new Row(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6), r.NullableString(7), r.GetString(8), r.GetInt64(9),
                r.GetFieldValue<DateTime>(10)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        var items = new List<MaintenanceTaskView>();
        foreach (var t in rows)
        {
            var done = await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT d.done_at, d.source, coalesce(u.display_name, u.email), d.note FROM mfg.maintenance_done d LEFT JOIN iam.user u ON u.user_id = d.recorded_by
                WHERE d.company_id = @c AND d.task_id = @t ORDER BY d.done_at DESC LIMIT 5
                """,
                r => (At: r.GetDateTime(0), View: new MaintenanceDoneView(r.GetDateTime(0).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), r.GetString(1), r.NullableString(2), r.NullableString(3))),
                cancellationToken,
                ("c", context.CompanyId), ("t", t.TaskId)).ConfigureAwait(false);
            var start = done.Count > 0 ? done[0].At : Tasks.Local(t.CreatedAt);
            var since = t.Kind == MaintenanceKinds.Days
                ? (now.Date - start.Date).Days
                : await SinceAsync(context, t.MachineId, start, now, t.Kind, cancellationToken).ConfigureAwait(false);
            var used = decimal.Round(since / t.Every, 4, MidpointRounding.AwayFromZero);
            var state = t.Status != "ACTIVE" ? "INACTIVE" : used >= 1m ? "VENCIDA" : used * 10 >= 9 ? "POR_VENCER" : "OK";
            items.Add(new MaintenanceTaskView(
                t.TaskId, t.MachineId, t.MachineCode, t.Code, t.Name, t.Kind, t.Every, t.Instructions, t.Status, t.Version, done.Count > 0 ? done[0].View.DoneAt : null, since, used, state,
                [.. done.Select(x => x.View)]));
        }

        return ApiJson.Serialize(new MaintenanceTaskList(items, items.Count(i => i.State == "POR_VENCER"), items.Count(i => i.State == "VENCIDA")));
    }

    /// <summary>Cycles, or running hours (planned − maintenance − stoppages, E-MFG3-01-5), of the machine's shifts since <paramref name="start"/>.</summary>
    private static async Task<decimal> SinceAsync(QueryContext context, Guid machine, DateTime start, DateTime now, string kind, CancellationToken cancellationToken)
    {
        var code = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT portal_code FROM mfg.portal_machine WHERE company_id = @c AND machine_id = @m", r => r.GetString(0), cancellationToken,
            ("c", context.CompanyId), ("m", machine)).ConfigureAwait(false)).SingleOrDefault();
        if (code is null)
        {
            return 0m;
        }

        var shifts = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT DISTINCT ON (shift_date, shift_no) window_from, window_to, cycles FROM mfg.portal_reading
            WHERE company_id = @c AND portal_code = @p AND window_to > @s ORDER BY shift_date, shift_no, fetched_at DESC
            """,
            r => new Shift(r.GetDateTime(0), r.GetDateTime(1), r.GetInt32(2)),
            cancellationToken,
            ("c", context.CompanyId), ("p", code), ("s", start)).ConfigureAwait(false);
        if (kind == MaintenanceKinds.Cycles)
        {
            // A shift that began before the task was done counts in full: the portal reports cycles per shift.
            return shifts.Where(s => s.From >= start || s.To > start).Sum(s => (decimal)s.Cycles);
        }

        var windows = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT DISTINCT ON (portal_id) starts_at, ends_at FROM mfg.portal_maintenance WHERE company_id = @c AND portal_code = @p ORDER BY portal_id, fetched_at DESC",
            r => new Interval(r.GetDateTime(0), r.IsDBNull(1) ? null : r.GetDateTime(1)),
            cancellationToken,
            ("c", context.CompanyId), ("p", code)).ConfigureAwait(false);
        var stops = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT DISTINCT ON (portal_id) started_at, CASE WHEN duration_seconds IS NOT NULL THEN started_at + duration_seconds * interval '1 second' ELSE ended_at END
            FROM mfg.portal_stoppage WHERE company_id = @c AND portal_code = @p AND started_at >= @s ORDER BY portal_id, fetched_at DESC
            """,
            r => new Interval(r.GetDateTime(0), r.IsDBNull(1) ? null : r.GetDateTime(1)),
            cancellationToken,
            ("c", context.CompanyId), ("p", code), ("s", start)).ConfigureAwait(false);
        decimal minutes = 0m;
        foreach (var s in shifts)
        {
            var from = s.From > start ? s.From : start;
            var to = s.To < now ? s.To : now;
            if (to <= from)
            {
                continue;
            }

            decimal planned = Span(from, to) - windows.Sum(w => Span(w.From > from ? w.From : from, (w.To ?? to) < to ? w.To ?? to : to));
            var stopped = stops.Where(x => x.From >= from && x.From < to && !windows.Any(w => x.From >= w.From && x.From < (w.To ?? to))).Sum(x => Span(x.From, (x.To ?? to) < to ? x.To ?? to : to));
            minutes += Math.Max(0m, planned - stopped);
        }

        return decimal.Round(minutes / 60, 2, MidpointRounding.AwayFromZero);
    }

    private static decimal Span(DateTime from, DateTime to) => to > from ? (decimal)(to - from).Ticks / TimeSpan.TicksPerMinute : 0m;
}

/// <summary>E-MFG3-9: a maintenance window the portal closed naming an ACTIVE task of its machine is that task done (once per window).</summary>
internal static class PortalMaintenanceDone
{
    public static async Task<int> RecordAsync(CommandContext context, string portalCode, long portalId, string? taskRef, DateTime? endsAt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(taskRef) || endsAt is null)
        {
            return 0;
        }

        var task = await MfgSql.ScalarAsync<Guid?>(
            context,
            """
            SELECT t.task_id FROM mfg.maintenance_task t JOIN mfg.portal_machine p ON p.machine_id = t.machine_id AND p.company_id = t.company_id
            WHERE t.company_id = @c AND t.code = @code AND t.status = 'ACTIVE' AND p.portal_code = @p
            """,
            cancellationToken,
            ("c", context.CompanyId), ("code", taskRef.Trim().ToUpperInvariant()), ("p", portalCode)).ConfigureAwait(false);
        if (task is null)
        {
            return 0;
        }

        return await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO mfg.maintenance_done (done_id, company_id, task_id, done_at, source, portal_maintenance_id, recorded_at)
            VALUES (@id, @c, @t, @at, 'PORTAL', @pid, @now) ON CONFLICT (company_id, task_id, portal_maintenance_id) DO NOTHING
            """,
            cancellationToken,
            ("id", context.Ids.NewId()), ("c", context.CompanyId), ("t", task.Value), ("at", endsAt.Value), ("pid", portalId), ("now", context.Clock.UtcNow)).ConfigureAwait(false);
    }
}
