using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Sales.Queries;

// E-VS3-06-10: credit notes, their fiscal package (with the e-NCF they modify) and what an invoice can still credit, read with sales:read.

public sealed record ListCreditNotes(Guid CompanyId, Guid SessionId, Guid? InvoiceId = null, string? FiscalStatus = null, Guid? PartyId = null, int Limit = 50, int Offset = 0) : IQuery;

public sealed record CreditNoteSummary(
    Guid CreditNoteId, string CreditNoteNo, Guid InvoiceId, string InvoiceNo, string? InvoiceEncf, Guid PartyId, string CustomerName, string ReasonCategory, string Reason,
    DateOnly? CreditDate, string? Encf, string CommercialStatus, string AccountingStatus, string FiscalStatus, decimal NetTotal, decimal TaxTotal, decimal Total, long Version);

public sealed record CreditNoteList(IReadOnlyList<CreditNoteSummary> Items, int Limit, int Offset);

internal static class CreditNoteReading
{
    public const string Select = """
        SELECT n.credit_note_id, n.credit_note_no, n.invoice_id, i.invoice_no, i.encf, n.party_id, p.legal_name, n.reason_category, n.reason, n.credit_date, n.encf,
               n.commercial_status, n.accounting_status, n.fiscal_status, n.net_total::numeric(19,2), n.tax_total::numeric(19,2), n.total::numeric(19,2), n.version
        FROM sal.credit_note n
        JOIN sal.invoice i ON i.invoice_id = n.invoice_id
        JOIN md.party p ON p.party_id = n.party_id
        """;

    public static CreditNoteSummary Map(System.Data.Common.DbDataReader r)
        => new(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.NullableString(4), r.GetGuid(5), r.GetString(6), r.GetString(7), r.GetString(8),
            r.IsDBNull(9) ? null : r.Date(9), r.NullableString(10), r.GetString(11), r.GetString(12), r.GetString(13), r.GetDecimal(14), r.GetDecimal(15), r.GetDecimal(16), r.GetInt64(17));

    public static Task<List<CreditNoteSummary>> ForInvoiceAsync(QueryContext context, Guid invoiceId, CancellationToken cancellationToken)
        => Reading.ListAsync(context.Connection, context.Transaction, Select + " WHERE n.invoice_id = @i ORDER BY n.credit_note_no", Map, cancellationToken, ("i", invoiceId));
}

[RequiresPermission("sales:read")]
public sealed class ListCreditNotesHandler : IQueryHandler<ListCreditNotes>
{
    public string QueryType => "Sales.ListCreditNotes";

    public async Task<string> HandleAsync(ListCreditNotes query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            CreditNoteReading.Select + """
             WHERE n.company_id = @c AND (CAST(@i AS uuid) IS NULL OR n.invoice_id = CAST(@i AS uuid))
              AND (CAST(@fs AS text) IS NULL OR n.fiscal_status = CAST(@fs AS text)) AND (CAST(@p AS uuid) IS NULL OR n.party_id = CAST(@p AS uuid))
            ORDER BY n.credit_note_no DESC
            LIMIT @limit OFFSET @offset
            """,
            CreditNoteReading.Map,
            cancellationToken,
            ("c", context.CompanyId),
            ("i", query.InvoiceId),
            ("fs", query.FiscalStatus),
            ("p", query.PartyId),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new CreditNoteList(items, query.Limit, query.Offset));
    }
}

public sealed record GetCreditNote(Guid CompanyId, Guid SessionId, Guid CreditNoteId) : IQuery;

public sealed record CreditNoteLineView(int LineNo, Guid InvoiceLineId, int InvoiceLineNo, string ItemCode, string ItemDescription, decimal NetAmount, decimal Rate, decimal Itbis);

/// <summary>E-UX3-9: <see cref="InvoiceIssuedById"/> is the user who issued the invoice; the credit note's issuer must be someone else.</summary>
public sealed record CreditNoteDetail(
    CreditNoteSummary Header, string? CreatedBy, string? IssuedBy, Guid? PostingEventId, IReadOnlyList<CreditNoteLineView> Lines, ExternalFiscalRecordView? FiscalRecord, IReadOnlyList<StateChange> History,
    Guid? InvoiceIssuedById, EcfStampView? Ecf = null);

internal static class CreditNoteLines
{
    public static Task<List<CreditNoteLineView>> ReadAsync(QueryContext context, Guid creditNoteId, CancellationToken cancellationToken)
        => Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT cl.line_no, cl.invoice_line_id, il.line_no, it.code, it.description, cl.net_amount::numeric(19,2), cl.rate, cl.itbis::numeric(19,2)
            FROM sal.credit_note_line cl
            JOIN sal.invoice_line il ON il.invoice_line_id = cl.invoice_line_id
            JOIN md.item it ON it.item_id = il.item_id
            WHERE cl.credit_note_id = @n ORDER BY cl.line_no
            """,
            r => new CreditNoteLineView(r.GetInt32(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7)),
            cancellationToken,
            ("n", creditNoteId));
}

[RequiresPermission("sales:read")]
public sealed class GetCreditNoteHandler : IQueryHandler<GetCreditNote>
{
    public string QueryType => "Sales.GetCreditNote";

    private sealed record Extra(string? CreatedBy, string? IssuedBy, Guid? PostingEventId, Guid? InvoiceIssuedById);

    public async Task<string> HandleAsync(GetCreditNote query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var header = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, CreditNoteReading.Select + " WHERE n.company_id = @c AND n.credit_note_id = @n", CreditNoteReading.Map, cancellationToken,
            ("c", context.CompanyId), ("n", query.CreditNoteId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The credit note does not exist.");
        var extra = (await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT coalesce(c.display_name, c.email), coalesce(u.display_name, u.email), n.posting_event_id, i.issued_by FROM sal.credit_note n
            JOIN sal.invoice i ON i.invoice_id = n.invoice_id
            LEFT JOIN iam.user c ON c.user_id = n.created_by LEFT JOIN iam.user u ON u.user_id = n.issued_by
            WHERE n.credit_note_id = @n
            """,
            r => new Extra(r.NullableString(0), r.NullableString(1), r.NullableGuid(2), r.NullableGuid(3)),
            cancellationToken,
            ("n", query.CreditNoteId)).ConfigureAwait(false))!;
        var fiscal = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT f.encf, f.issued_at, f.security_code, f.evidence_ref, encode(f.evidence_sha256, 'hex'), coalesce(u.display_name, u.email) FROM tax.external_fiscal_record f LEFT JOIN iam.user u ON u.user_id = f.recorded_by WHERE f.credit_note_id = @n",
            r => new ExternalFiscalRecordView(r.GetString(0), r.GetFieldValue<DateTime>(1), r.GetString(2), r.GetString(3), r.GetString(4), r.NullableString(5)),
            cancellationToken,
            ("n", query.CreditNoteId)).ConfigureAwait(false);
        var lines = await CreditNoteLines.ReadAsync(context, query.CreditNoteId, cancellationToken).ConfigureAwait(false);
        var history = await StateHistory.ReadAsync(context, "CreditNote", query.CreditNoteId, cancellationToken).ConfigureAwait(false);
        var ecf = await EcfStamps.LatestAsync(context, query.CreditNoteId, cancellationToken).ConfigureAwait(false);
        return ApiJson.Serialize(new CreditNoteDetail(header, extra.CreatedBy, extra.IssuedBy, extra.PostingEventId, lines, fiscal, history, extra.InvoiceIssuedById, ecf));
    }
}

public sealed record GetCreditNoteFiscalPackage(Guid CompanyId, Guid SessionId, Guid CreditNoteId) : IQuery;

/// <summary>E-VS3-06-5: what the user types into the provider's portal for the e-CF type 34, including the e-NCF it modifies.</summary>
public sealed record CreditNoteFiscalPackage(
    string CreditNoteNo, string EcfType, DateOnly CreditDate, string ModifiedEncf, string InvoiceNo, DateOnly InvoiceDate, string ReasonCategory, string Reason, string IssuerRnc,
    string IssuerName, string ReceiverRnc, string ReceiverName, IReadOnlyList<CreditNoteLineView> Lines, decimal NetTotal, decimal TaxTotal, decimal Total, string FiscalStatus,
    string? ReceiverPassport = null);

[RequiresPermission("sales:read")]
public sealed class GetCreditNoteFiscalPackageHandler : IQueryHandler<GetCreditNoteFiscalPackage>
{
    public const string EcfType = "34";

    public string QueryType => "Sales.GetCreditNoteFiscalPackage";

    private sealed record Head(
        string No, DateOnly? Date, string? ModifiedEncf, string InvoiceNo, DateOnly? InvoiceDate, string Category, string Reason, string IssuerRnc, string IssuerName, string ReceiverRnc,
        string ReceiverName, decimal Net, decimal Tax, decimal Total, string Fiscal, string? Passport);

    public async Task<string> HandleAsync(GetCreditNoteFiscalPackage query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var h = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT n.credit_note_no, n.credit_date, i.encf, i.invoice_no, i.invoice_date, n.reason_category, n.reason, c.rnc, c.legal_name,
                   coalesce(CASE WHEN i.buyer_id_kind IN ('CEDULA', 'RNC') THEN i.buyer_id END, p.rnc, ''), coalesce(i.buyer_name, p.legal_name),
                   n.net_total::numeric(19,2), n.tax_total::numeric(19,2), n.total::numeric(19,2), n.fiscal_status, CASE WHEN i.buyer_id_kind = 'PASAPORTE' THEN i.buyer_id END
            FROM sal.credit_note n JOIN sal.invoice i ON i.invoice_id = n.invoice_id JOIN md.party p ON p.party_id = n.party_id JOIN md.company c ON c.company_id = n.company_id
            WHERE n.company_id = @c AND n.credit_note_id = @n
            """,
            r => new Head(r.GetString(0), r.IsDBNull(1) ? null : r.Date(1), r.NullableString(2), r.GetString(3), r.IsDBNull(4) ? null : r.Date(4), r.GetString(5), r.GetString(6),
                r.GetString(7), r.GetString(8), r.GetString(9), r.GetString(10), r.GetDecimal(11), r.GetDecimal(12), r.GetDecimal(13), r.GetString(14), r.NullableString(15)),
            cancellationToken,
            ("c", context.CompanyId),
            ("n", query.CreditNoteId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The credit note does not exist.");
        if (h.Date is null || h.ModifiedEncf is null || h.InvoiceDate is null)
        {
            throw new DomainException(QueryErrors.InvalidParameter, "The fiscal package exists once the credit note is issued.");
        }

        var lines = await CreditNoteLines.ReadAsync(context, query.CreditNoteId, cancellationToken).ConfigureAwait(false);
        return ApiJson.Serialize(new CreditNoteFiscalPackage(
            h.No, EcfType, h.Date.Value, h.ModifiedEncf, h.InvoiceNo, h.InvoiceDate.Value, h.Category, h.Reason, h.IssuerRnc, h.IssuerName, h.ReceiverRnc, h.ReceiverName, lines, h.Net, h.Tax,
            h.Total, h.Fiscal, h.Passport));
    }
}
