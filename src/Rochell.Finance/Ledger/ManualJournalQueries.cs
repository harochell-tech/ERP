using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Finance.Ledger;

// FIN1-02: the adjustment journal read with ledger:read (Contador, Controller, Auditor, Director; E-FIN1-9).

public sealed record ListManualJournals(Guid CompanyId, Guid SessionId, string? Status = null, int Limit = 50, int Offset = 0) : IQuery;

public sealed record ManualJournalSummary(
    Guid ManualJournalId, string JournalNo, DateOnly PostingDate, string Description, string CloseComponent, bool AutoReverse, string Status, decimal Total,
    string? PreparedBy, string? ApprovedBy, long Version);

public sealed record ManualJournalList(IReadOnlyList<ManualJournalSummary> Items, int Limit, int Offset);

[RequiresPermission("ledger:read")]
public sealed class ListManualJournalsHandler : IQueryHandler<ListManualJournals>
{
    public string QueryType => "Finance.ListManualJournals";

    public async Task<string> HandleAsync(ListManualJournals query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT m.manual_journal_id, m.journal_no, m.posting_date, m.description, m.close_component, m.auto_reverse, m.status,
                   (SELECT coalesce(sum(l.debit), 0)::numeric(19,2) FROM fin.manual_journal_line l WHERE l.manual_journal_id = m.manual_journal_id AND l.journal_version =
                      (SELECT max(x.journal_version) FROM fin.manual_journal_line x WHERE x.manual_journal_id = m.manual_journal_id)),
                   pu.email, au.email, m.version
            FROM fin.manual_journal m
            JOIN iam.user pu ON pu.user_id = m.prepared_by
            LEFT JOIN iam.user au ON au.user_id = m.approved_by
            WHERE m.company_id = @c AND (CAST(@status AS text) IS NULL OR m.status = CAST(@status AS text))
            ORDER BY m.journal_no DESC
            LIMIT @limit OFFSET @offset
            """,
            r => new ManualJournalSummary(r.GetGuid(0), r.GetString(1), r.Date(2), r.GetString(3), r.GetString(4), r.GetBoolean(5), r.GetString(6), r.GetDecimal(7), r.NullableString(8), r.NullableString(9), r.GetInt64(10)),
            cancellationToken,
            ("c", context.CompanyId),
            ("status", query.Status),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new ManualJournalList(items, query.Limit, query.Offset));
    }
}

public sealed record GetManualJournal(Guid CompanyId, Guid SessionId, Guid ManualJournalId) : IQuery;

public sealed record ManualJournalLineView(int LineNo, Guid AccountId, string AccountCode, string AccountName, decimal Debit, decimal Credit, Guid? PlantId, Guid? PartyId, string? Memo);

public sealed record ManualJournalDetail(
    Guid ManualJournalId, string JournalNo, DateOnly PostingDate, string Description, string SupportRef, string SupportSha256, string CloseComponent, bool AutoReverse,
    string Status, string? PreparedBy, Guid PreparedById, string? ApprovedBy, string? RejectedBy, string? RejectionReason, Guid? PostingEventId, long Version,
    IReadOnlyList<ManualJournalLineView> Lines, IReadOnlyList<StateChange> History, decimal TotalDebit, decimal TotalCredit, decimal Difference);

[RequiresPermission("ledger:read")]
public sealed class GetManualJournalHandler : IQueryHandler<GetManualJournal>
{
    public string QueryType => "Finance.GetManualJournal";

    private sealed record Totals(decimal Debit, decimal Credit);

    private sealed record Header(
        string JournalNo, DateOnly PostingDate, string Description, string SupportRef, string SupportSha256, string Component, bool AutoReverse, string Status,
        string? PreparedBy, Guid PreparedById, string? ApprovedBy, string? RejectedBy, string? Reason, Guid? PostingEventId, long Version);

    public async Task<string> HandleAsync(GetManualJournal query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var h = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT m.journal_no, m.posting_date, m.description, m.support_ref, encode(m.support_sha256, 'hex'), m.close_component, m.auto_reverse, m.status,
                   pu.email, m.prepared_by, au.email, ru.email, m.rejection_reason, m.posting_event_id, m.version
            FROM fin.manual_journal m
            JOIN iam.user pu ON pu.user_id = m.prepared_by
            LEFT JOIN iam.user au ON au.user_id = m.approved_by
            LEFT JOIN iam.user ru ON ru.user_id = m.rejected_by
            WHERE m.company_id = @c AND m.manual_journal_id = @id
            """,
            r => new Header(r.GetString(0), r.Date(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetBoolean(6), r.GetString(7),
                r.NullableString(8), r.GetGuid(9), r.NullableString(10), r.NullableString(11), r.NullableString(12), r.NullableGuid(13), r.GetInt64(14)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", query.ManualJournalId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The adjustment does not exist.");
        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.line_no, l.account_id, a.code, a.name, l.debit::numeric(19,2), l.credit::numeric(19,2), l.plant_id, l.party_id, l.memo
            FROM fin.manual_journal_line l JOIN fin.account a ON a.account_id = l.account_id
            WHERE l.manual_journal_id = @id AND l.journal_version = (SELECT max(x.journal_version) FROM fin.manual_journal_line x WHERE x.manual_journal_id = @id)
            ORDER BY l.line_no
            """,
            r => new ManualJournalLineView(r.GetInt32(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetDecimal(4), r.GetDecimal(5), r.NullableGuid(6), r.NullableGuid(7), r.NullableString(8)),
            cancellationToken,
            ("id", query.ManualJournalId)).ConfigureAwait(false);
        var history = await StateHistory.ReadAsync(context, "ManualJournal", query.ManualJournalId, cancellationToken).ConfigureAwait(false);
        // E-FIN1-04-4: the screen never adds amounts; the server gives the totals of the current version.
        var totals = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT coalesce(sum(l.debit), 0)::numeric(19,2), coalesce(sum(l.credit), 0)::numeric(19,2) FROM fin.manual_journal_line l
            WHERE l.manual_journal_id = @id AND l.journal_version = (SELECT max(x.journal_version) FROM fin.manual_journal_line x WHERE x.manual_journal_id = @id)
            """,
            r => new Totals(r.GetDecimal(0), r.GetDecimal(1)),
            cancellationToken,
            ("id", query.ManualJournalId)).ConfigureAwait(false) ?? throw new InvalidOperationException("An aggregate always returns a row.");
        return ApiJson.Serialize(new ManualJournalDetail(
            query.ManualJournalId, h.JournalNo, h.PostingDate, h.Description, h.SupportRef, h.SupportSha256, h.Component, h.AutoReverse, h.Status, h.PreparedBy,
            h.PreparedById, h.ApprovedBy, h.RejectedBy, h.Reason, h.PostingEventId, h.Version, lines, history, totals.Debit, totals.Credit, totals.Debit - totals.Credit));
    }
}
