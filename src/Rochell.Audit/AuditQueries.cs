using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Audit;

// UX4-01 · E-UX4-15: the auditor finds a document's journals and sees when the hash chains were last verified (audit:read).

/// <summary>
/// E-UX4-15: journals by a document number or an id. <paramref name="Text"/> as a UUID matches a journal, its source event or the
/// event's aggregate (the document id); otherwise it is compared, ignoring case, with the documents' numbers — goods receipt
/// (RM-…), supplier invoice (NCF), payment (PAG-…), manual journal, sales invoice and credit note (number or e-NCF), receipt, deposit,
/// delivery (conduce), production run — and matches the journals of the events of that document or of the events whose payload names
/// it (a reversal, a correction, a run's shift summary). Newest first.
/// </summary>
public sealed record SearchJournals(Guid CompanyId, Guid SessionId, string Text, int Limit = 50, int Offset = 0) : IQuery;

/// <summary><see cref="DocumentNumber"/>: the number of the event's own document when it has one of the kinds searched, else null.</summary>
public sealed record JournalSearchHit(
    Guid JournalId, Guid SourceEventId, string EventType, string AggregateType, string? DocumentNumber, string RuleCode, string JournalType, int Generation, DateOnly PostingDate,
    decimal TotalDebit, Guid? ReversesJournalId);

public sealed record JournalSearchResult(string Text, IReadOnlyList<JournalSearchHit> Items, int Limit, int Offset);

[RequiresPermission("audit:read")]
public sealed class SearchJournalsHandler : IQueryHandler<SearchJournals>
{
    public const int MaxTextLength = 100;

    public string QueryType => "Audit.SearchJournals";

    private const string Documents = """
        SELECT gr_id AS id, gr_no AS no FROM pur.goods_receipt WHERE company_id = @c
        UNION ALL SELECT si_id, supplier_fiscal_number FROM pur.supplier_invoice WHERE company_id = @c
        UNION ALL SELECT payment_id, payment_no FROM fin.payment WHERE company_id = @c
        UNION ALL SELECT manual_journal_id, journal_no FROM fin.manual_journal WHERE company_id = @c
        UNION ALL SELECT invoice_id, invoice_no FROM sal.invoice WHERE company_id = @c
        UNION ALL SELECT invoice_id, encf FROM sal.invoice WHERE company_id = @c AND encf IS NOT NULL
        UNION ALL SELECT credit_note_id, credit_note_no FROM sal.credit_note WHERE company_id = @c
        UNION ALL SELECT credit_note_id, encf FROM sal.credit_note WHERE company_id = @c AND encf IS NOT NULL
        UNION ALL SELECT receipt_id, receipt_no FROM fin.receipt WHERE company_id = @c
        UNION ALL SELECT deposit_id, deposit_no FROM fin.receipt_deposit WHERE company_id = @c
        UNION ALL SELECT delivery_id, delivery_no FROM log.delivery WHERE company_id = @c
        UNION ALL SELECT run_id, run_no FROM mfg.production_run WHERE company_id = @c
        """;

    public async Task<string> HandleAsync(SearchJournals query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var text = (query.Text ?? string.Empty).Trim();
        if (text.Length is 0 or > MaxTextLength)
        {
            throw new DomainException(QueryErrors.InvalidParameter, $"Search for a document number or id of 1 to {MaxTextLength} characters.");
        }

        Guid? id = Guid.TryParse(text, out var parsed) ? parsed : null;
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            $"""
            WITH docs AS ({Documents}),
                 hits AS (SELECT id FROM docs WHERE upper(no) = upper(@q))
            SELECT j.journal_id, j.source_event_id, ev.event_type, ev.aggregate_type, (SELECT d.no FROM docs d WHERE d.id = ev.aggregate_id ORDER BY d.no LIMIT 1),
                   coalesce(r.code, 'P-34'), j.journal_type, j.posting_generation, j.posting_date,
                   (SELECT coalesce(sum(e.debit), 0)::numeric(19,2) FROM fin.gl_entry e WHERE e.journal_id = j.journal_id), j.reverses_journal_id
            FROM fin.gl_journal j
            JOIN core.domain_event ev ON ev.company_id = j.company_id AND ev.event_id = j.source_event_id
            LEFT JOIN fin.posting_rule r ON r.posting_rule_id = j.posting_rule_id
            WHERE j.company_id = @c
              AND (   (CAST(@id AS uuid) IS NOT NULL AND CAST(@id AS uuid) IN (j.journal_id, j.source_event_id, ev.aggregate_id))
                   OR ev.aggregate_id IN (SELECT id FROM hits)
                   OR EXISTS (SELECT 1 FROM jsonb_each_text(ev.payload) kv
                              WHERE kv.value IN (SELECT id::text FROM hits) OR (CAST(@id AS uuid) IS NOT NULL AND kv.value = CAST(@id AS uuid)::text)))
            ORDER BY j.posting_date DESC, ev.occurred_at DESC, j.journal_id DESC
            LIMIT @limit OFFSET @offset
            """,
            r => new JournalSearchHit(
                r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.NullableString(4), r.GetString(5), r.GetString(6), r.GetInt32(7), r.Date(8), r.GetDecimal(9),
                r.NullableGuid(10)),
            cancellationToken,
            ("c", context.CompanyId),
            ("q", text),
            ("id", id),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new JournalSearchResult(text, items, query.Limit, query.Offset));
    }
}

/// <summary>
/// E-UX4-15: the integrity of the ledgers at a glance. A verification (VerifyHashChain) is a command, so its result is kept in
/// <c>core.command_log</c>: <see cref="IntegrityStatus.LastVerification"/> is the latest committed one — when, by whom, valid or not, per
/// chain — or null if none ran (a verification that failed technically rolled back and left nothing). Beside it: the latest daily
/// digest per chain and how many groups wait to be sealed or failed sealing now.
/// </summary>
public sealed record GetIntegrityStatus(Guid CompanyId, Guid SessionId) : IQuery;

public sealed record VerifiedChain(string Ledger, bool Valid, long Seals);

public sealed record IntegrityVerification(DateTime VerifiedAt, string? VerifiedBy, bool Valid, IReadOnlyList<VerifiedChain> Chains);

public sealed record ChainState(string Ledger, DateOnly? LastDigestDate, long LastSequence, int PendingSeal, int SealErrors);

public sealed record IntegrityStatus(IntegrityVerification? LastVerification, IReadOnlyList<ChainState> Chains);

[RequiresPermission("audit:read")]
public sealed class GetIntegrityStatusHandler : IQueryHandler<GetIntegrityStatus>
{
    public string QueryType => "Audit.GetIntegrityStatus";

    private sealed record Row(DateTime At, string? By, string Payload);

    public async Task<string> HandleAsync(GetIntegrityStatus query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var last = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT c.committed_at, coalesce(u.display_name, u.email), c.result_payload::text
            FROM core.command_log c
            LEFT JOIN iam.session s ON s.session_id = c.session_id
            LEFT JOIN iam.user u ON u.user_id = s.user_id
            WHERE c.company_id = @c AND c.command_type = 'Audit.VerifyHashChain' AND c.committed_at IS NOT NULL
            ORDER BY c.committed_at DESC
            LIMIT 1
            """,
            r => new Row(r.Utc(0), r.NullableString(1), r.GetString(2)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        IntegrityVerification? verification = null;
        if (last is not null)
        {
            using var payload = JsonDocument.Parse(last.Payload);
            var root = payload.RootElement;
            var chains = root.GetProperty("chains").EnumerateArray()
                .Select(c => new VerifiedChain(c.GetProperty("ledger").GetString()!, c.GetProperty("valid").GetBoolean(), c.GetProperty("seals").GetInt64()))
                .ToList();
            verification = new IntegrityVerification(last.At, last.By, root.GetProperty("valid").GetBoolean(), chains);
        }

        var states = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.ledger,
                   (SELECT max(d.digest_date) FROM audit.ledger_digest d WHERE d.company_id = @c AND d.ledger = l.ledger),
                   coalesce((SELECT max(s.ledger_sequence) FROM audit.ledger_seal s WHERE s.company_id = @c AND s.ledger = l.ledger), 0),
                   (SELECT count(*)::int FROM audit.integrity_state i WHERE i.company_id = @c AND i.ledger = l.ledger AND i.integrity_status = 'PENDING_SEAL'),
                   (SELECT count(*)::int FROM audit.integrity_state i WHERE i.company_id = @c AND i.ledger = l.ledger AND i.integrity_status = 'SEAL_ERROR')
            FROM unnest(@ledgers) WITH ORDINALITY AS l (ledger, n)
            ORDER BY l.n
            """,
            r => new ChainState(r.GetString(0), r.IsDBNull(1) ? null : r.Date(1), r.GetInt64(2), r.GetInt32(3), r.GetInt32(4)),
            cancellationToken,
            ("c", context.CompanyId),
            ("ledgers", Chains.All.ToArray())).ConfigureAwait(false);
        return ApiJson.Serialize(new IntegrityStatus(verification, states));
    }
}
