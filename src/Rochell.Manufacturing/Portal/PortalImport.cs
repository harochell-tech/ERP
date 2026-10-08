using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Manufacturing.Portal;

/// <summary>
/// E-MFG2-1, E-MFG2-01-5/6: reads the portal for <paramref name="From"/>…<paramref name="To"/> and keeps what changed — a machine's
/// shift when its figures differ from the last reading, a batch-plant post Core has not seen — and the state of the connection. Returns
/// the groups to bring up to date (a batch plant's machines, or an offline machine alone, per date and shift).
/// </summary>
public sealed record ImportPortalData(Guid CompanyId, Guid SessionId, string IdempotencyKey, DateOnly From, DateOnly To) : ICommand;

public sealed record PortalGroup(DateOnly Date, int ShiftNo, string Group);

public sealed record PortalImportResult(bool Ok, int Readings, int Posts, IReadOnlyList<PortalGroup> Groups, IReadOnlyList<string> Warnings, string? Error);

[RequiresPermission("shift_summary:record")]
public sealed class ImportPortalDataHandler(IPortalSource source) : ICommandHandler<ImportPortalData>
{
    public string CommandType => "Manufacturing.ImportPortalData";

    public async Task<string> HandleAsync(ImportPortalData command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        PortalExport export;
        try
        {
            export = await source.FetchAsync(command.From, command.To, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            var message = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            await SyncState.FailedAsync(context, message, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new PortalImportResult(false, 0, 0, [], [], message), PortalJson.Options);
        }

        var machines = await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT portal_code, batch_plant FROM mfg.portal_machine WHERE company_id = @c", r => (Code: r.GetString(0), BatchPlant: r.NullableString(1)),
            cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false);
        var warnings = new List<string>();
        var groups = new HashSet<PortalGroup>();
        var readings = 0;
        var now = context.Clock.UtcNow;
        foreach (var shift in export.Shifts)
        {
            var code = shift.Machine.Trim().ToLowerInvariant();
            var paired = machines.FirstOrDefault(m => m.Code == code);
            if (paired == default)
            {
                if (shift.Cycles > 0)
                {
                    warnings.Add($"La máquina «{code}» del portal no está emparejada: su producción no se importa.");
                }

                continue;
            }

            var date = PortalTime.Day(shift.Date);
            groups.Add(new PortalGroup(date, shift.ShiftNo, paired.BatchPlant ?? code));
            var content = JsonSerializer.SerializeToUtf8Bytes(shift, PortalJson.Options);
            var sha = SHA256.HashData(content);
            var last = (await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                "SELECT content_sha256 FROM mfg.portal_reading WHERE company_id = @c AND portal_code = @p AND shift_date = @d AND shift_no = @n ORDER BY fetched_at DESC LIMIT 1",
                r => (byte[])r.GetValue(0),
                cancellationToken,
                ("c", context.CompanyId),
                ("p", code),
                ("d", date),
                ("n", (short)shift.ShiftNo)).ConfigureAwait(false)).SingleOrDefault();
            if (last is not null && last.AsSpan().SequenceEqual(sha))
            {
                continue;
            }

            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO mfg.portal_reading (reading_id, company_id, portal_code, shift_date, shift_no, window_from, window_to, closed, cycles, cycles_without_mould,
                                                maintenance_cycles, dead_minutes, first_cycle, last_cycle, moulds, content_sha256, fetched_at)
                VALUES (@id, @c, @p, @d, @n, @from, @to, @closed, @cycles, @nomould, @maint, @dead, @first, @last, CAST(@moulds AS jsonb), @sha, @at)
                """,
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("p", code),
                ("d", date),
                ("n", (short)shift.ShiftNo),
                ("from", PortalTime.Parse(shift.From)),
                ("to", PortalTime.Parse(shift.To)),
                ("closed", shift.Closed),
                ("cycles", shift.Cycles),
                ("nomould", shift.CyclesWithoutMould),
                ("maint", shift.MaintenanceCycles),
                ("dead", shift.DeadMinutes),
                ("first", shift.FirstCycle is null ? DBNull.Value : PortalTime.Parse(shift.FirstCycle)),
                ("last", shift.LastCycle is null ? DBNull.Value : PortalTime.Parse(shift.LastCycle)),
                ("moulds", JsonSerializer.Serialize(shift.Moulds, PortalJson.Options)),
                ("sha", sha),
                ("at", now)).ConfigureAwait(false);
            readings++;
            if (shift.CyclesWithoutMould > 0)
            {
                warnings.Add($"{code} {shift.Date} turno {shift.ShiftNo}: {shift.CyclesWithoutMould} ciclo(s) sin molde registrado en el portal, no cuentan.");
            }
        }

        var posts = 0;
        foreach (var post in export.Posts)
        {
            var batchPlant = post.BatchPlant.Trim().ToLowerInvariant();
            if (!machines.Any(m => m.BatchPlant == batchPlant))
            {
                warnings.Add($"La dosificadora «{batchPlant}» no alimenta ninguna máquina emparejada: su consumo no se usa.");
                continue;
            }

            var date = PortalTime.Day(post.Date);
            groups.Add(new PortalGroup(date, post.ShiftNo, batchPlant));
            var inserted = await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO mfg.portal_consumption (consumption_id, company_id, portal_id, batch_plant, shift_date, shift_no, batches, materials, received_at, fetched_at)
                VALUES (@id, @c, @pid, @bp, @d, @n, @b, CAST(@m AS jsonb), @rec, @at)
                ON CONFLICT (company_id, portal_id) DO NOTHING
                """,
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("pid", post.Id),
                ("bp", batchPlant),
                ("d", date),
                ("n", (short)post.ShiftNo),
                ("b", (object?)post.Batches ?? DBNull.Value),
                ("m", JsonSerializer.Serialize(post.Materials, PortalJson.Options)),
                ("rec", PortalTime.Parse(post.ReceivedAt)),
                ("at", now)).ConfigureAwait(false);
            posts += inserted;
        }

        // MFG3-02 (E-MFG3-3, E-MFG3-01-1): each new version of a paired machine's stoppages, maintenance windows and daily reports.
        var pairedCodes = machines.Select(m => m.Code).ToHashSet(StringComparer.Ordinal);
        var extras = 0;
        foreach (var stop in export.Stoppages ?? [])
        {
            var code = stop.Machine.Trim().ToLowerInvariant();
            if (pairedCodes.Contains(code))
            {
                extras += await Sql.ExecuteAsync(
                    context.Connection,
                    context.Transaction,
                    """
                    INSERT INTO mfg.portal_stoppage (stoppage_row_id, company_id, portal_id, portal_code, started_at, ended_at, duration_seconds, reason, detail, content_sha256, fetched_at)
                    VALUES (@id, @c, @pid, @p, @start, @end, @secs, @reason, @detail, @sha, @at) ON CONFLICT (company_id, portal_id, content_sha256) DO NOTHING
                    """,
                    cancellationToken,
                    ("id", context.Ids.NewId()), ("c", context.CompanyId), ("pid", stop.Id), ("p", code), ("start", PortalTime.Parse(stop.Start)),
                    ("end", stop.End is null ? DBNull.Value : PortalTime.Parse(stop.End)), ("secs", (object?)stop.Seconds ?? DBNull.Value), ("reason", (object?)stop.Reason ?? DBNull.Value),
                    ("detail", (object?)stop.Detail ?? DBNull.Value), ("sha", SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(stop, PortalJson.Options))), ("at", now)).ConfigureAwait(false);
            }
        }

        foreach (var window in export.Maintenance ?? [])
        {
            var code = window.Machine.Trim().ToLowerInvariant();
            if (pairedCodes.Contains(code))
            {
                extras += await Sql.ExecuteAsync(
                    context.Connection,
                    context.Transaction,
                    """
                    INSERT INTO mfg.portal_maintenance (maintenance_row_id, company_id, portal_id, portal_code, starts_at, ends_at, reason, task_ref, content_sha256, fetched_at)
                    VALUES (@id, @c, @pid, @p, @from, @to, @reason, @task, @sha, @at) ON CONFLICT (company_id, portal_id, content_sha256) DO NOTHING
                    """,
                    cancellationToken,
                    ("id", context.Ids.NewId()), ("c", context.CompanyId), ("pid", window.Id), ("p", code), ("from", PortalTime.Parse(window.From)),
                    ("to", window.To is null ? DBNull.Value : PortalTime.Parse(window.To)), ("reason", window.Reason), ("task", (object?)window.Task ?? DBNull.Value),
                    ("sha", SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(window, PortalJson.Options))), ("at", now)).ConfigureAwait(false);
            }
        }

        foreach (var report in export.Reports ?? [])
        {
            var code = report.Machine.Trim().ToLowerInvariant();
            if (!pairedCodes.Contains(code))
            {
                continue;
            }

            var inserted = await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO mfg.portal_daily_report (report_row_id, company_id, portal_code, report_date, broken_units, cured_good, updated_in_portal, content_sha256, fetched_at)
                VALUES (@id, @c, @p, @d, @broken, CAST(@cured AS jsonb), @upd, @sha, @at) ON CONFLICT (company_id, portal_code, report_date, content_sha256) DO NOTHING
                """,
                cancellationToken,
                ("id", context.Ids.NewId()), ("c", context.CompanyId), ("p", code), ("d", PortalTime.Day(report.Date)), ("broken", (object?)report.Broken ?? DBNull.Value),
                ("cured", report.CuredGood is null ? DBNull.Value : JsonSerializer.Serialize(report.CuredGood)), ("upd", (object?)report.UpdatedAt ?? DBNull.Value),
                ("sha", SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(report, PortalJson.Options))), ("at", now)).ConfigureAwait(false);
            extras += inserted;
            if (inserted > 0)
            {
                // E-MFG3-01-4: a new report brings the machine's day up to date (its broken blocks on the draft).
                var machine = machines.First(m => m.Code == code);
                groups.Add(new PortalGroup(PortalTime.Day(report.Date), 1, machine.BatchPlant ?? code));
            }
        }

        await SyncState.SucceededAsync(context, warnings, cancellationToken).ConfigureAwait(false);
        var ordered = groups.OrderBy(g => g.Date).ThenBy(g => g.ShiftNo).ThenBy(g => g.Group, StringComparer.Ordinal).ToList();
        return JsonSerializer.Serialize(new PortalImportResult(true, readings, posts, ordered, warnings, null), PortalJson.Options);
    }
}

internal static class PortalJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>The connection's state for Producción › Portal and Inicio (E-MFG2-3).</summary>
internal static class SyncState
{
    public static Task SucceededAsync(CommandContext context, IReadOnlyList<string> warnings, CancellationToken cancellationToken)
        => Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO mfg.portal_sync_state (company_id, last_ok_at, last_error, last_error_at, warnings, version) VALUES (@c, @at, NULL, NULL, CAST(@w AS jsonb), 1)
            ON CONFLICT (company_id) DO UPDATE SET last_ok_at = @at, last_error = NULL, last_error_at = NULL, warnings = CAST(@w AS jsonb),
              version = mfg.portal_sync_state.version + 1
            """,
            cancellationToken,
            ("c", context.CompanyId),
            ("at", context.Clock.UtcNow),
            ("w", JsonSerializer.Serialize(warnings.Distinct().ToList())));

    public static Task FailedAsync(CommandContext context, string error, CancellationToken cancellationToken)
        => Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO mfg.portal_sync_state (company_id, last_ok_at, last_error, last_error_at, warnings, version) VALUES (@c, NULL, @e, @at, '[]', 1)
            ON CONFLICT (company_id) DO UPDATE SET last_error = @e, last_error_at = @at, version = mfg.portal_sync_state.version + 1
            """,
            cancellationToken,
            ("c", context.CompanyId),
            ("e", error),
            ("at", context.Clock.UtcNow));

    /// <summary>Adds the warnings of a group's synchronisation to the state (kept until the next import).</summary>
    public static Task AddWarningsAsync(CommandContext context, IReadOnlyList<string> warnings, CancellationToken cancellationToken)
        => warnings.Count == 0
            ? Task.CompletedTask
            : Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO mfg.portal_sync_state (company_id, warnings, version) VALUES (@c, CAST(@w AS jsonb), 1)
                ON CONFLICT (company_id) DO UPDATE SET
                  warnings = (SELECT coalesce(jsonb_agg(DISTINCT x), '[]') FROM jsonb_array_elements(mfg.portal_sync_state.warnings || CAST(@w AS jsonb)) x),
                  version = mfg.portal_sync_state.version + 1
                """,
                cancellationToken,
                ("c", context.CompanyId),
                ("w", JsonSerializer.Serialize(warnings)));
}
