using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Finance.Explain;

/// <summary>
/// The journals a source event produced (every generation, reversals and reposts included), with their entries: the way from a
/// document's posting event to "Explain this entry" (EX-01). Same permission as Explain.
/// </summary>
public sealed record ListEventJournals(Guid CompanyId, Guid SessionId, Guid SourceEventId) : IQuery;

public sealed record GlEntryView(Guid GlEntryId, int LineNo, string RuleLineCode, string AccountCode, string AccountName, string AccountRole, decimal Debit, decimal Credit);

public sealed record GlJournalView(
    Guid JournalId,
    string RuleCode,
    int RuleVersion,
    string JournalType,
    int Generation,
    DateOnly PostingDate,
    bool LateEntry,
    Guid? ReversesJournalId,
    IReadOnlyList<GlEntryView> Entries);

public sealed record EventJournals(Guid SourceEventId, IReadOnlyList<GlJournalView> Journals);

[RequiresPermission("audit:read")]
public sealed class ListEventJournalsHandler : IQueryHandler<ListEventJournals>
{
    public string QueryType => "Finance.ListEventJournals";

    public async Task<string> HandleAsync(ListEventJournals query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var journals = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT j.journal_id, r.code, j.posting_rule_version, j.journal_type, j.posting_generation, j.posting_date, j.late_entry, j.reverses_journal_id
            FROM fin.gl_journal j
            JOIN fin.posting_rule r ON r.posting_rule_id = j.posting_rule_id
            WHERE j.company_id = @c AND j.source_event_id = @e
            ORDER BY j.posting_generation, r.code, j.journal_id
            """,
            r => new GlJournalView(r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetInt32(4), r.Date(5), r.GetBoolean(6), r.NullableGuid(7), []),
            cancellationToken,
            ("c", context.CompanyId),
            ("e", query.SourceEventId)).ConfigureAwait(false);

        var entries = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT e.journal_id, e.gl_entry_id, e.line_no, e.rule_line_code, a.code, a.name, e.account_role, e.debit, e.credit
            FROM fin.gl_entry e
            JOIN fin.account a ON a.account_id = e.account_id
            WHERE e.company_id = @c AND e.source_event_id = @e
            ORDER BY e.journal_id, e.line_no
            """,
            r => (JournalId: r.GetGuid(0), View: new GlEntryView(r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetDecimal(7), r.GetDecimal(8))),
            cancellationToken,
            ("c", context.CompanyId),
            ("e", query.SourceEventId)).ConfigureAwait(false))
            .ToLookup(e => e.JournalId, e => e.View);

        if (journals.Count == 0)
        {
            throw new DomainException(QueryErrors.NotFound, "The event has no journals.");
        }

        return ApiJson.Serialize(new EventJournals(query.SourceEventId, journals.Select(j => j with { Entries = entries[j.JournalId].ToList() }).ToList()));
    }
}
