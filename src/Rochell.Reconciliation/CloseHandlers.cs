using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Hashing;
using Rochell.Platform.Time;

namespace Rochell.Reconciliation;

internal static class CloseSql
{
    public static async Task<Guid> SessionUserAsync(CommandContext context, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(context.Connection, context.Transaction, "SELECT user_id FROM iam.session WHERE session_id = @id", ("id", context.SessionId));
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public static string RequireReason(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? throw new DomainException(ReconciliationErrors.ReasonRequired, "A reason is required.") : reason.Trim();

    public static void RequireComponent(string component)
    {
        if (!Components.IsKnown(component))
        {
            throw new DomainException(ReconciliationErrors.UnknownComponent, $"Unknown close component {component}.");
        }
    }

    /// <summary>The same key the Posting Engine locks in shared mode for every posting into the period (K-20).</summary>
    public static Task LockPeriodExclusiveAsync(CommandContext context, Guid periodId, string component, CancellationToken cancellationToken)
        => Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended('period:' || @company || ':' || @period || ':' || @component, 0))",
            cancellationToken,
            ("company", context.CompanyId),
            ("period", periodId),
            ("component", component));

    public static async Task<(string Status, long Version)> ComponentStateAsync(CommandContext context, Guid periodId, string component, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT status, version FROM fin.close_component_state WHERE company_id = @c AND period_id = @p AND component = @k FOR UPDATE",
            ("c", context.CompanyId),
            ("p", periodId),
            ("k", component));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), reader.GetInt64(1))
            : throw new DomainException(ReconciliationErrors.PeriodNotFound, $"Period {periodId} has no {component} component.");
    }
}

[RequiresPermission("reconciliation:run")]
public sealed class RunReconciliationHandler : ICommandHandler<RunReconciliation>
{
    public string CommandType => "Reconciliation.RunReconciliation";

    public async Task<string> HandleAsync(RunReconciliation command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var runs = await Reconciliations.RunAsync(context, command.ReconCodes ?? Reconciliations.All, cancellationToken).ConfigureAwait(false);
        var summary = runs.Select(r => new
        {
            code = r.Code,
            runId = r.RunId,
            status = r.Status,
            errors = r.Findings.Count(f => f.Severity == "ERROR"),
            warnings = r.Findings.Count(f => f.Severity == "WARNING"),
        }).ToList();
        await context.AppendEventAsync(
            new EventDraft("ReconciliationCompleted", 1, "ReconciliationRun", context.ResultRef, 1, JsonSerializer.Serialize(new { runs = summary }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        var orphans = runs.Where(r => r.Code == "VAL-RESIDUAL").SelectMany(r => r.Findings).Where(f => f.Classification == "ORPHAN_VALUE").Select(f => f.MatchKey).ToList();
        if (orphans.Count > 0)
        {
            await context.AppendEventAsync(
                new EventDraft("ValuationResidualDetected", 1, "ValuationResidualFinding", context.Ids.NewId(), 1, JsonSerializer.Serialize(new { positions = orphans }), Publish: true),
                cancellationToken).ConfigureAwait(false);
        }

        return JsonSerializer.Serialize(new { runs = summary });
    }
}

/// <summary>
/// T-13 (Patch 1 / 1.1, E-PR16-5/6): SERIALIZABLE, with the period × component lock held exclusively so postings into it wait.
/// Guards: the period has ended; the component is OPEN or REOPENED; every ledger group of the period is SEALED (GL / inventory
/// by posting_date, domain events by business_date); the component's blocking reconciliations have no ERROR finding. Then a
/// snapshot of the reconciliations and the component's balances is stored and its SHA-256 recorded in the component state.
/// </summary>
[RequiresPermission("period_component:close", StepUp = true)]
[SerializableTransaction]
public sealed class CloseComponentHandler : ICommandHandler<CloseComponent>
{
    public string CommandType => "Finance.CloseComponent";

    public async Task<string> HandleAsync(CloseComponent command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        CloseSql.RequireComponent(command.Component);

        DateOnly startsOn;
        DateOnly endsOn;
        await using (var period = Sql.Command(context.Connection, context.Transaction, "SELECT starts_on, ends_on FROM fin.period WHERE company_id = @c AND period_id = @p", ("c", context.CompanyId), ("p", command.PeriodId)))
        await using (var reader = await period.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new DomainException(ReconciliationErrors.PeriodNotFound, $"Period {command.PeriodId} does not exist.");
            }

            (startsOn, endsOn) = (reader.GetFieldValue<DateOnly>(0), reader.GetFieldValue<DateOnly>(1));
        }

        var now = context.Clock.UtcNow;
        if (endsOn >= BusinessCalendar.DefaultBusinessDate(now))
        {
            throw new DomainException(ReconciliationErrors.PeriodNotEnded, $"The period ends on {endsOn:yyyy-MM-dd}; a period is closed only after it ends (E-PR16-5).");
        }

        await CloseSql.LockPeriodExclusiveAsync(context, command.PeriodId, command.Component, cancellationToken).ConfigureAwait(false);
        var (status, version) = await CloseSql.ComponentStateAsync(context, command.PeriodId, command.Component, cancellationToken).ConfigureAwait(false);
        if (status is not ("OPEN" or "REOPENED"))
        {
            throw new DomainException(ReconciliationErrors.ComponentNotOpen, $"{command.Component} of this period is {status}.");
        }

        // Patch 1.1 close gate: nothing of the period may be unsealed.
        var unsealed = new List<string>();
        await using (var gate = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT ledger, integrity_status, count(*) FROM audit.integrity_state
            WHERE company_id = @c AND integrity_status <> 'SEALED'
              AND ((ledger <> 'DOMAIN_EVENT' AND posting_date BETWEEN @s AND @e) OR (ledger = 'DOMAIN_EVENT' AND business_date BETWEEN @s AND @e))
            GROUP BY ledger, integrity_status ORDER BY ledger, integrity_status
            """,
            ("c", context.CompanyId),
            ("s", startsOn),
            ("e", endsOn)))
        await using (var reader = await gate.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                unsealed.Add(string.Create(CultureInfo.InvariantCulture, $"{reader.GetString(0)} {reader.GetString(1)}: {reader.GetInt64(2)}"));
            }
        }

        if (unsealed.Count > 0)
        {
            throw new DomainException(ReconciliationErrors.IntegrityNotSealed, $"Ledger groups of the period are not sealed ({string.Join("; ", unsealed)}).");
        }

        var codes = new List<string>();
        await using (var blocking = Sql.Command(context.Connection, context.Transaction, "SELECT recon_code FROM rec.recon_blocking WHERE component = @k ORDER BY recon_code", ("k", command.Component)))
        await using (var reader = await blocking.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                codes.Add(reader.GetString(0));
            }
        }

        var runs = await Reconciliations.RunAsync(context, codes, cancellationToken).ConfigureAwait(false);
        var blocked = runs.Where(r => r.Blocks(command.Component)).Select(r => r.Code).ToList();
        if (blocked.Count > 0)
        {
            throw new DomainException(ReconciliationErrors.ReconciliationErrorsFound, $"{command.Component} cannot close: {string.Join(", ", blocked)} report errors.");
        }

        var user = await CloseSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var content = JsonCanonicalizer.Canonicalize(JsonSerializer.Serialize(new
        {
            period_id = command.PeriodId,
            component = command.Component,
            starts_on = startsOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ends_on = endsOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            closed_at = now.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
            reconciliations = runs.Select(r => new { code = r.Code, run_id = r.RunId, status = r.Status, total_a = Text(r.TotalA), total_b = Text(r.TotalB) }),
            balances = await BalancesAsync(context, command.Component, cancellationToken).ConfigureAwait(false),
        }));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        var snapshotId = context.Ids.NewId();

        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ComponentClosed",
                1,
                "PeriodComponentClose",
                snapshotId,
                1,
                JsonSerializer.Serialize(new { periodId = command.PeriodId, component = command.Component, snapshotId, snapshotHash = Convert.ToHexStringLower(hash) }),
                Publish: true,
                OccurredAt: now),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO fin.close_snapshot (snapshot_id, company_id, period_id, component, closed_by, closed_at, content, content_hash)
            VALUES (@id, @c, @p, @k, @u, @t, CAST(@content AS jsonb), @h)
            """,
            cancellationToken,
            ("id", snapshotId),
            ("c", context.CompanyId),
            ("p", command.PeriodId),
            ("k", command.Component),
            ("u", user),
            ("t", now),
            ("content", content),
            ("h", hash)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            UPDATE fin.close_component_state SET status = 'CLOSED', closed_by = @u, closed_at = @t, snapshot_hash = @h, version = version + 1
            WHERE company_id = @c AND period_id = @p AND component = @k
            """,
            cancellationToken,
            ("u", user),
            ("t", now),
            ("h", hash),
            ("c", context.CompanyId),
            ("p", command.PeriodId),
            ("k", command.Component)).ConfigureAwait(false);
        await context.AppendStateAsync($"PeriodComponent:{command.Component}", command.PeriodId, "DOCUMENT", status, "CLOSED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { snapshotId, snapshotHash = Convert.ToHexStringLower(hash), version = version + 1 });
    }

    private static string? Text(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);

    /// <summary>INV-MOV: valuation by area × item and the RAW_MATERIAL accounts; AP-REC: open AP and AP_CONTROL by supplier.</summary>
    private static async Task<List<Dictionary<string, string?>>> BalancesAsync(CommandContext context, string component, CancellationToken cancellationToken)
    {
        var sql = component == Components.InventoryMovements
            ? """
              SELECT 'valuation' AS kind, valuation_area_id::text || '/' || item_id::text AS key, quantity::text AS a, value::text AS b
              FROM inv.inv_valuation_balance WHERE company_id = @c
              UNION ALL
              SELECT 'gl', a.code, NULL, sum(e.debit - e.credit)::text FROM fin.gl_entry e JOIN fin.account a ON a.account_id = e.account_id
              WHERE e.company_id = @c AND e.account_role = 'RAW_MATERIAL' GROUP BY a.code
              ORDER BY 1, 2
              """
            : """
              SELECT 'ap_open' AS kind, party_id::text AS key, NULL AS a, sum(open_amount)::text AS b FROM fin.ap_document WHERE company_id = @c GROUP BY party_id
              UNION ALL
              SELECT 'ap_control', coalesce(party_id::text, '-'), NULL, sum(credit - debit)::text FROM fin.gl_entry
              WHERE company_id = @c AND account_role = 'AP_CONTROL' GROUP BY party_id
              ORDER BY 1, 2
              """;
        var rows = new List<Dictionary<string, string?>>();
        await using var command = Sql.Command(context.Connection, context.Transaction, sql, ("c", context.CompanyId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["kind"] = reader.GetString(0),
                ["key"] = reader.GetString(1),
                ["quantity"] = reader.IsDBNull(2) ? null : reader.GetString(2),
                ["amount"] = reader.IsDBNull(3) ? null : reader.GetString(3),
            });
        }

        return rows;
    }
}
