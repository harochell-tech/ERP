using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Reconciliation.Queries;

/// <summary>E-PR18-4: accounting periods of a year with their component states (§11.7) and reopen requests (P-8).</summary>
public sealed record ListPeriods(Guid CompanyId, Guid SessionId, int Year) : IQuery;

public sealed record ComponentStateView(string Component, string Status, string? ClosedBy, DateTime? ClosedAt, long Version);

public sealed record ReopenRequestView(
    Guid RequestId,
    string Component,
    string Reason,
    string Status,
    string? RequestedBy,
    DateTime RequestedAt,
    string? SecondApprovedBy,
    DateTime? SecondApprovedAt,
    string? RejectedBy);

public sealed record PeriodView(
    Guid PeriodId,
    DateOnly StartsOn,
    DateOnly EndsOn,
    IReadOnlyList<ComponentStateView> Components,
    IReadOnlyList<ReopenRequestView> ReopenRequests);

public sealed record PeriodList(int Year, IReadOnlyList<PeriodView> Items);

[RequiresPermission("period:read")]
public sealed class ListPeriodsHandler : IQueryHandler<ListPeriods>
{
    private const int FirstYear = 2000;
    private const int LastYear = 2100;

    public string QueryType => "Reconciliation.ListPeriods";

    public async Task<string> HandleAsync(ListPeriods query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        if (query.Year is < FirstYear or > LastYear)
        {
            throw new DomainException(QueryErrors.InvalidParameter, $"year must be between {FirstYear} and {LastYear}.");
        }

        var from = new DateOnly(query.Year, 1, 1);
        var periods = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT period_id, starts_on, ends_on FROM fin.period WHERE company_id = @c AND starts_on >= @from AND starts_on < @to ORDER BY starts_on",
            r => new PeriodView(r.GetGuid(0), r.Date(1), r.Date(2), [], []),
            cancellationToken,
            ("c", context.CompanyId),
            ("from", from),
            ("to", from.AddYears(1))).ConfigureAwait(false);
        var ids = periods.Select(p => p.PeriodId).ToArray();

        var components = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT s.period_id, s.component, s.status, u.email, s.closed_at, s.version
            FROM fin.close_component_state s
            LEFT JOIN iam.user u ON u.user_id = s.closed_by
            WHERE s.company_id = @c AND s.period_id = ANY(@ids)
            ORDER BY s.component
            """,
            r => (PeriodId: r.GetGuid(0), View: new ComponentStateView(r.GetString(1), r.GetString(2), r.NullableString(3), r.NullableUtc(4), r.GetInt64(5))),
            cancellationToken,
            ("c", context.CompanyId),
            ("ids", ids)).ConfigureAwait(false))
            .ToLookup(c => c.PeriodId, c => c.View);

        var requests = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT q.period_id, q.request_id, q.component, q.reason, q.status, ru.email, q.requested_at, au.email, q.second_approved_at, xu.email
            FROM fin.reopen_request q
            LEFT JOIN iam.user ru ON ru.user_id = q.requested_by
            LEFT JOIN iam.user au ON au.user_id = q.second_approved_by
            LEFT JOIN iam.user xu ON xu.user_id = q.rejected_by
            WHERE q.company_id = @c AND q.period_id = ANY(@ids)
            ORDER BY q.requested_at
            """,
            r => (PeriodId: r.GetGuid(0), View: new ReopenRequestView(
                r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.NullableString(5), r.Utc(6), r.NullableString(7), r.NullableUtc(8), r.NullableString(9))),
            cancellationToken,
            ("c", context.CompanyId),
            ("ids", ids)).ConfigureAwait(false))
            .ToLookup(q => q.PeriodId, q => q.View);

        var items = periods.Select(p => p with { Components = components[p.PeriodId].ToList(), ReopenRequests = requests[p.PeriodId].ToList() }).ToList();
        return ApiJson.Serialize(new PeriodList(query.Year, items));
    }
}

/// <summary>Reconciliation runs, newest first, optionally of one reconciliation.</summary>
public sealed record ListReconciliationRuns(Guid CompanyId, Guid SessionId, string? ReconCode = null, int Limit = 50, int Offset = 0) : IQuery;

public sealed record ReconciliationRunSummary(
    Guid RunId,
    string ReconCode,
    string Description,
    DateTime AsOf,
    decimal? TotalA,
    decimal? TotalB,
    decimal? Difference,
    string Status,
    int ExceptionCount);

public sealed record ReconciliationRunList(IReadOnlyList<ReconciliationRunSummary> Items, int Limit, int Offset);

[RequiresPermission("reconciliation:read")]
public sealed class ListReconciliationRunsHandler : IQueryHandler<ListReconciliationRuns>
{
    public string QueryType => "Reconciliation.ListReconciliationRuns";

    public async Task<string> HandleAsync(ListReconciliationRuns query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            RunSelect + """
            WHERE r.company_id = @c AND (CAST(@code AS text) IS NULL OR r.recon_code = CAST(@code AS text))
            ORDER BY r.as_of DESC, r.run_id DESC
            LIMIT @limit OFFSET @offset
            """,
            MapRun,
            cancellationToken,
            ("c", context.CompanyId),
            ("code", query.ReconCode),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new ReconciliationRunList(items, query.Limit, query.Offset));
    }

    internal const string RunSelect = """
        SELECT r.run_id, r.recon_code, d.description, r.as_of, r.total_a, r.total_b, r.difference, r.status,
               (SELECT count(*)::int FROM rec.recon_exception x WHERE x.run_id = r.run_id)
        FROM rec.recon_run r
        JOIN rec.recon_definition d ON d.recon_code = r.recon_code

        """;

    internal static ReconciliationRunSummary MapRun(System.Data.Common.DbDataReader r)
        => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.Utc(3), r.NullableDecimal(4), r.NullableDecimal(5), r.NullableDecimal(6), r.GetString(7), r.GetInt32(8));
}

/// <summary>One reconciliation run with its exceptions (what differs, how it is classified and which component it blocks).</summary>
public sealed record GetReconciliationRun(Guid CompanyId, Guid SessionId, Guid RunId) : IQuery;

public sealed record ReconciliationExceptionView(
    Guid ExceptionId,
    string MatchKey,
    decimal? ValueA,
    decimal? ValueB,
    string Classification,
    string Severity,
    string? Component,
    string Status,
    string? Resolution);

public sealed record ReconciliationRunDetail(ReconciliationRunSummary Run, IReadOnlyList<ReconciliationExceptionView> Exceptions);

[RequiresPermission("reconciliation:read")]
public sealed class GetReconciliationRunHandler : IQueryHandler<GetReconciliationRun>
{
    public string QueryType => "Reconciliation.GetReconciliationRun";

    public async Task<string> HandleAsync(GetReconciliationRun query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var run = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            ListReconciliationRunsHandler.RunSelect + "WHERE r.company_id = @c AND r.run_id = @id",
            ListReconciliationRunsHandler.MapRun,
            cancellationToken,
            ("c", context.CompanyId),
            ("id", query.RunId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The reconciliation run does not exist.");

        var exceptions = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT exception_id, match_key, value_a, value_b, classification, severity, component, status, resolution
            FROM rec.recon_exception
            WHERE company_id = @c AND run_id = @id
            ORDER BY severity, match_key
            """,
            r => new ReconciliationExceptionView(
                r.GetGuid(0), r.GetString(1), r.NullableDecimal(2), r.NullableDecimal(3), r.GetString(4), r.GetString(5), r.NullableString(6), r.GetString(7), r.NullableString(8)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", query.RunId)).ConfigureAwait(false);
        return ApiJson.Serialize(new ReconciliationRunDetail(run, exceptions));
    }
}
