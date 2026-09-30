using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;
using Rochell.Platform.Time;

namespace Rochell.Reconciliation.Queries;

/// <summary>
/// E-UX3-1: whether each component of a period can close, read-only and with the close guards' own rules (CloseComponentHandler):
/// ended = the period ends before today's business date (<c>PERIOD_NOT_ENDED</c> otherwise); sealed = no <c>audit.integrity_state</c>
/// group of the period is unsealed (GL / inventory by posting date, domain events by business date); per blocking reconciliation
/// (<c>rec.recon_blocking</c>) its latest run for the period — the latest <c>rec.recon_run</c> whose cutoff date is the period's end,
/// as stored by <c>RunReconciliation</c> with <c>CutoffDate</c> = end ("Verificar ahora") — and that run's ERROR findings that block
/// the component (component null or equal, as <see cref="ReconRun.Blocks"/>). Ready = ended, sealed, OPEN or REOPENED, and every
/// blocking reconciliation has such a run without blocking errors. The close itself runs the reconciliations again.
/// </summary>
public sealed record GetCloseReadiness(Guid CompanyId, Guid SessionId, Guid PeriodId) : IQuery;

/// <summary>A blocking reconciliation of a component: its latest run for the period, if any, and that run's blocking errors.</summary>
public sealed record CloseReadinessReconciliation(string ReconCode, string Name, Guid? RunId, string? RunStatus, DateTime? RunAt, int? BlockingErrors);

public sealed record CloseReadinessComponent(string Component, string Status, bool Ended, bool Sealed, bool Ready, IReadOnlyList<CloseReadinessReconciliation> Reconciliations);

public sealed record CloseReadiness(Guid PeriodId, DateOnly StartsOn, DateOnly EndsOn, bool Ended, bool Sealed, IReadOnlyList<CloseReadinessComponent> Components);

[RequiresPermission("period:read")]
public sealed class GetCloseReadinessHandler : IQueryHandler<GetCloseReadiness>
{
    public string QueryType => "Reconciliation.GetCloseReadiness";

    private sealed record Period(DateOnly StartsOn, DateOnly EndsOn, bool Sealed);

    public async Task<string> HandleAsync(GetCloseReadiness query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var period = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT p.starts_on, p.ends_on,
                   NOT EXISTS (SELECT 1 FROM audit.integrity_state i
                               WHERE i.company_id = p.company_id AND i.integrity_status <> 'SEALED'
                                 AND ((i.ledger <> 'DOMAIN_EVENT' AND i.posting_date BETWEEN p.starts_on AND p.ends_on)
                                      OR (i.ledger = 'DOMAIN_EVENT' AND i.business_date BETWEEN p.starts_on AND p.ends_on)))
            FROM fin.period p WHERE p.company_id = @c AND p.period_id = @p
            """,
            r => new Period(r.Date(0), r.Date(1), r.GetBoolean(2)),
            cancellationToken,
            ("c", context.CompanyId),
            ("p", query.PeriodId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The period does not exist.");

        var ended = period.EndsOn < BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);
        var reconciliations = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT b.component, b.recon_code, d.name, r.run_id, r.status, r.as_of,
                   CASE WHEN r.run_id IS NOT NULL THEN
                     (SELECT count(*)::int FROM rec.recon_exception x
                      WHERE x.run_id = r.run_id AND x.severity = 'ERROR' AND (x.component IS NULL OR x.component = b.component)) END
            FROM rec.recon_blocking b
            JOIN rec.recon_definition d ON d.recon_code = b.recon_code
            LEFT JOIN LATERAL (SELECT run_id, status, as_of FROM rec.recon_run
                               WHERE company_id = @c AND recon_code = b.recon_code AND cutoff_date = @e
                               ORDER BY as_of DESC, run_id DESC LIMIT 1) r ON true
            ORDER BY b.component, b.recon_code
            """,
            r => (Component: r.GetString(0), View: new CloseReadinessReconciliation(
                r.GetString(1), r.GetString(2), r.NullableGuid(3), r.NullableString(4), r.NullableUtc(5), r.IsDBNull(6) ? null : r.GetInt32(6))),
            cancellationToken,
            ("c", context.CompanyId),
            ("e", period.EndsOn)).ConfigureAwait(false))
            .ToLookup(x => x.Component, x => x.View);

        var components = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT component, status FROM fin.close_component_state WHERE company_id = @c AND period_id = @p ORDER BY component",
            r => (Component: r.GetString(0), Status: r.GetString(1)),
            cancellationToken,
            ("c", context.CompanyId),
            ("p", query.PeriodId)).ConfigureAwait(false);

        var views = components.Select(c =>
        {
            var blocking = reconciliations[c.Component].ToList();
            var ready = ended && period.Sealed && (c.Status is "OPEN" or "REOPENED") && blocking.All(b => b.RunId is not null && b.BlockingErrors == 0);
            return new CloseReadinessComponent(c.Component, c.Status, ended, period.Sealed, ready, blocking);
        }).ToList();
        return ApiJson.Serialize(new CloseReadiness(query.PeriodId, period.StartsOn, period.EndsOn, ended, period.Sealed, views));
    }
}
