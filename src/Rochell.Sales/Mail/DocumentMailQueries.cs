using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Sales.Mail;

/// <summary>E-MAIL-7, E-MAIL-01-6: the sendings of a document, newest first (for a statement or the aging, the customer is the document).</summary>
public sealed record ListDocumentMail(Guid CompanyId, Guid SessionId, string DocumentType, Guid DocumentId) : IQuery;

public sealed record DocumentMailView(
    Guid MailId, string DocumentNo, string Status, IReadOnlyList<string> Recipients, string Subject, string RequestedBy, DateTime RequestedAt, int Attempts, string? LastError,
    DateTime? SentAt, string? DeliveryMode, IReadOnlyList<string>? DeliveredTo, string FileName, bool HasPdf);

/// <summary><c>SavedEmails</c>: the e-mails kept for the document's customer, the principal one first (E-MAIL-5) — what the sender is offered.</summary>
public sealed record DocumentMailList(IReadOnlyList<DocumentMailView> Items, IReadOnlyList<string> SavedEmails);

[RequiresPermission("sales:read")]
public sealed class ListDocumentMailHandler : IQueryHandler<ListDocumentMail>
{
    public string QueryType => "Sales.ListDocumentMail";

    public async Task<string> HandleAsync(ListDocumentMail query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT m.mail_id, m.document_no, m.status, m.recipients, m.subject, coalesce(u.display_name, u.email, '—'), m.requested_at, m.attempts, m.last_error, m.sent_at, m.delivery_mode,
                   m.delivered_to, m.file_name, m.pdf IS NOT NULL
            FROM core.mail_message m JOIN iam.user u ON u.user_id = m.requested_by
            WHERE m.company_id = @c AND m.document_type = @t AND m.document_id = @d
            ORDER BY m.requested_at DESC, m.mail_id DESC
            LIMIT 200
            """,
            r => new DocumentMailView(
                r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetFieldValue<string[]>(3), r.GetString(4), r.GetString(5), r.Utc(6), r.GetInt32(7), r.NullableString(8), r.NullableUtc(9),
                r.NullableString(10), r.IsDBNull(11) ? null : r.GetFieldValue<string[]>(11), r.GetString(12), r.GetBoolean(13)),
            cancellationToken,
            ("c", context.CompanyId),
            ("t", query.DocumentType),
            ("d", query.DocumentId)).ConfigureAwait(false);
        var saved = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT e.email FROM md.party_email e
            WHERE e.company_id = @c AND e.party_id = CASE @t
                    WHEN 'QUOTE' THEN (SELECT q.party_id FROM sal.quote q WHERE q.company_id = @c AND q.quote_id = @d)
                    WHEN 'PROFORMA' THEN (SELECT f.party_id FROM sal.proforma f WHERE f.company_id = @c AND f.proforma_id = @d)
                    WHEN 'DELIVERY' THEN (SELECT o.party_id FROM log.delivery x JOIN sal.sales_order o ON o.sales_order_id = x.sales_order_id WHERE x.company_id = @c AND x.delivery_id = @d)
                    ELSE @d END
            ORDER BY e.position
            """,
            r => r.GetString(0),
            cancellationToken,
            ("c", context.CompanyId),
            ("t", query.DocumentType),
            ("d", query.DocumentId)).ConfigureAwait(false);
        return ApiJson.Serialize(new DocumentMailList(items, saved));
    }
}

/// <summary>E-MAIL-01-6: the exact PDF that was sent (base64), once the dispatcher rendered it.</summary>
public sealed record GetDocumentMailPdf(Guid CompanyId, Guid SessionId, Guid MailId) : IQuery;

public sealed record DocumentMailPdf(string FileName, string Sha256, string ContentBase64);

[RequiresPermission("sales:read")]
public sealed class GetDocumentMailPdfHandler : IQueryHandler<GetDocumentMailPdf>
{
    public string QueryType => "Sales.GetDocumentMailPdf";

    public async Task<string> HandleAsync(GetDocumentMailPdf query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var pdf = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT file_name, pdf_sha256, pdf FROM core.mail_message WHERE company_id = @c AND mail_id = @id AND pdf IS NOT NULL",
            r => new DocumentMailPdf(r.GetString(0), r.GetString(1), Convert.ToBase64String(r.GetFieldValue<byte[]>(2))),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", query.MailId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The message does not exist or its PDF has not been rendered yet.");
        return ApiJson.Serialize(pdf);
    }
}
