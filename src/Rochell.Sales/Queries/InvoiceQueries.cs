using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Sales.Queries;

// E-VS3-05-14: invoices, their fiscal package and what can be billed, read with sales:read.

/// <summary><paramref name="OpenOnly"/> (E-VS3-07-14): issued invoices with an open amount, to apply receipts to.</summary>
public sealed record ListInvoices(
    Guid CompanyId, Guid SessionId, string? CommercialStatus = null, string? FiscalStatus = null, Guid? PartyId = null, int Limit = 50, int Offset = 0, bool OpenOnly = false) : IQuery;

public sealed record InvoiceSummary(
    Guid InvoiceId, string InvoiceNo, DateOnly? InvoiceDate, DateOnly? DueDate, Guid PartyId, string CustomerName, string EcfType, string? Encf, string CommercialStatus,
    string AccountingStatus, string FiscalStatus, decimal NetTotal, decimal? TaxTotal, decimal? Total, decimal? OpenAmount, long Version);

public sealed record InvoiceList(IReadOnlyList<InvoiceSummary> Items, int Limit, int Offset);

internal static class InvoiceReading
{
    public const string Select = """
        SELECT i.invoice_id, i.invoice_no, i.invoice_date, i.due_date, i.party_id, p.legal_name, i.ecf_type, i.encf, i.commercial_status, i.accounting_status, i.fiscal_status,
               i.net_total::numeric(19,2), i.tax_total::numeric(19,2), i.total::numeric(19,2), a.open_amount::numeric(19,2), i.version
        FROM sal.invoice i
        JOIN md.party p ON p.party_id = i.party_id
        LEFT JOIN fin.ar_document a ON a.ar_doc_id = i.ar_doc_id
        """;

    public static InvoiceSummary Map(System.Data.Common.DbDataReader r)
        => new(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.Date(2), r.IsDBNull(3) ? null : r.Date(3), r.GetGuid(4), r.GetString(5), r.GetString(6), r.NullableString(7),
            r.GetString(8), r.GetString(9), r.GetString(10), r.GetDecimal(11), r.IsDBNull(12) ? null : r.GetDecimal(12), r.IsDBNull(13) ? null : r.GetDecimal(13),
            r.IsDBNull(14) ? null : r.GetDecimal(14), r.GetInt64(15));
}

[RequiresPermission("sales:read")]
public sealed class ListInvoicesHandler : IQueryHandler<ListInvoices>
{
    public string QueryType => "Sales.ListInvoices";

    public async Task<string> HandleAsync(ListInvoices query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            InvoiceReading.Select + """
             WHERE i.company_id = @c AND (CAST(@cs AS text) IS NULL OR i.commercial_status = CAST(@cs AS text))
              AND (CAST(@fs AS text) IS NULL OR i.fiscal_status = CAST(@fs AS text)) AND (CAST(@p AS uuid) IS NULL OR i.party_id = CAST(@p AS uuid))
              AND (NOT @open OR (i.commercial_status IN ('CONFIRMED', 'PARTIALLY_PAID') AND a.open_amount > 0))
            ORDER BY i.invoice_no DESC
            LIMIT @limit OFFSET @offset
            """,
            InvoiceReading.Map,
            cancellationToken,
            ("c", context.CompanyId),
            ("cs", query.CommercialStatus),
            ("fs", query.FiscalStatus),
            ("p", query.PartyId),
            ("open", query.OpenOnly),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new InvoiceList(items, query.Limit, query.Offset));
    }
}

public sealed record GetInvoice(Guid CompanyId, Guid SessionId, Guid InvoiceId) : IQuery;

public sealed record InvoiceLineView(int LineNo, Guid DeliveryLineId, string DeliveryNo, Guid ItemId, string ItemCode, string ItemDescription, string Uom, decimal Quantity, decimal UnitPrice, decimal NetAmount, decimal Itbis);

public sealed record ExternalFiscalRecordView(string Encf, DateTime IssuedAt, string SecurityCode, string EvidenceRef, string EvidenceSha256, string? RecordedBy);

/// <summary>E-VS3-06-10: what each invoice line can still credit (net of CONFIRMED credit notes).</summary>
public sealed record CreditableLine(Guid InvoiceLineId, int LineNo, string ItemCode, decimal NetAmount, decimal Rate, decimal CreditedNet, decimal RemainingNet);

/// <summary>E-VS3-07-7: a withholding the customer made on the invoice (the Controller reverses it from the invoice's page, VS3-10b).</summary>
public sealed record InvoiceWithholdingView(Guid WithholdingId, string Kind, decimal Amount, DateOnly WithholdingDate, string CertificateNo, string Status, string? ReversalReason, long Version);

public sealed record InvoiceDetail(
    InvoiceSummary Header, string? VoidReason, string? IssuedBy, Guid? PostingEventId, IReadOnlyList<InvoiceLineView> Lines, ExternalFiscalRecordView? FiscalRecord, IReadOnlyList<StateChange> History,
    IReadOnlyList<CreditNoteSummary> CreditNotes, IReadOnlyList<CreditableLine> Creditable, IReadOnlyList<InvoiceWithholdingView> Withholdings);

internal static class InvoiceLines
{
    public static Task<List<InvoiceLineView>> ReadAsync(QueryContext context, Guid invoiceId, CancellationToken cancellationToken)
        => Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT il.line_no, il.delivery_line_id, d.delivery_no, il.item_id, it.code, it.description, il.uom, il.quantity, il.unit_price, il.net_amount::numeric(19,2),
                   coalesce((SELECT sum(t.amount) FROM tax.tax_determination_line t JOIN sal.invoice i ON i.tax_determination_id = t.determination_id
                             WHERE i.invoice_id = il.invoice_id AND t.subject_line_id = il.invoice_line_id AND t.effect = 'OUTPUT'), 0)::numeric(19,2)
            FROM sal.invoice_line il
            JOIN log.delivery_line dl ON dl.delivery_line_id = il.delivery_line_id
            JOIN log.delivery d ON d.delivery_id = dl.delivery_id
            JOIN md.item it ON it.item_id = il.item_id
            WHERE il.invoice_id = @i ORDER BY il.line_no
            """,
            r => new InvoiceLineView(r.GetInt32(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9), r.GetDecimal(10)),
            cancellationToken,
            ("i", invoiceId));
}

[RequiresPermission("sales:read")]
public sealed class GetInvoiceHandler : IQueryHandler<GetInvoice>
{
    public string QueryType => "Sales.GetInvoice";

    private sealed record Extra(string? VoidReason, string? IssuedBy, Guid? PostingEventId);

    public async Task<string> HandleAsync(GetInvoice query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var header = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, InvoiceReading.Select + " WHERE i.company_id = @c AND i.invoice_id = @i", InvoiceReading.Map, cancellationToken,
            ("c", context.CompanyId), ("i", query.InvoiceId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The invoice does not exist.");
        var extra = (await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT i.void_reason, coalesce(u.display_name, u.email), i.posting_event_id FROM sal.invoice i LEFT JOIN iam.user u ON u.user_id = i.issued_by WHERE i.invoice_id = @i",
            r => new Extra(r.NullableString(0), r.NullableString(1), r.NullableGuid(2)),
            cancellationToken,
            ("i", query.InvoiceId)).ConfigureAwait(false))!;
        var fiscal = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT f.encf, f.issued_at, f.security_code, f.evidence_ref, encode(f.evidence_sha256, 'hex'), coalesce(u.display_name, u.email) FROM tax.external_fiscal_record f LEFT JOIN iam.user u ON u.user_id = f.recorded_by WHERE f.invoice_id = @i",
            r => new ExternalFiscalRecordView(r.GetString(0), r.GetFieldValue<DateTime>(1), r.GetString(2), r.GetString(3), r.GetString(4), r.NullableString(5)),
            cancellationToken,
            ("i", query.InvoiceId)).ConfigureAwait(false);
        var lines = await InvoiceLines.ReadAsync(context, query.InvoiceId, cancellationToken).ConfigureAwait(false);
        var history = await StateHistory.ReadAsync(context, "Invoice", query.InvoiceId, cancellationToken).ConfigureAwait(false);
        var notes = await CreditNoteReading.ForInvoiceAsync(context, query.InvoiceId, cancellationToken).ConfigureAwait(false);
        var creditable = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT il.invoice_line_id, il.line_no, it.code, il.net_amount::numeric(19,2), coalesce(t.rate, 0), x.credited::numeric(19,2), (il.net_amount - x.credited)::numeric(19,2)
            FROM sal.invoice_line il
            JOIN sal.invoice i ON i.invoice_id = il.invoice_id
            JOIN md.item it ON it.item_id = il.item_id
            LEFT JOIN tax.tax_determination_line t ON t.determination_id = i.tax_determination_id AND t.subject_line_id = il.invoice_line_id AND t.effect = 'OUTPUT'
            CROSS JOIN LATERAL (SELECT coalesce(sum(cl.net_amount), 0) AS credited FROM sal.credit_note_line cl JOIN sal.credit_note n ON n.credit_note_id = cl.credit_note_id
                                WHERE cl.invoice_line_id = il.invoice_line_id AND n.commercial_status = 'CONFIRMED') x
            WHERE il.invoice_id = @i ORDER BY il.line_no
            """,
            r => new CreditableLine(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6)),
            cancellationToken,
            ("i", query.InvoiceId)).ConfigureAwait(false);
        var withholdings = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT withholding_id, kind, amount::numeric(19,2), withholding_date, certificate_no, status, reversal_reason, version
            FROM fin.customer_withholding WHERE invoice_id = @i ORDER BY withholding_date, certificate_no
            """,
            r => new InvoiceWithholdingView(r.GetGuid(0), r.GetString(1), r.GetDecimal(2), r.Date(3), r.GetString(4), r.GetString(5), r.NullableString(6), r.GetInt64(7)),
            cancellationToken,
            ("i", query.InvoiceId)).ConfigureAwait(false);
        return ApiJson.Serialize(new InvoiceDetail(header, extra.VoidReason, extra.IssuedBy, extra.PostingEventId, lines, fiscal, history, notes, creditable, withholdings));
    }
}

public sealed record GetInvoiceFiscalPackage(Guid CompanyId, Guid SessionId, Guid InvoiceId) : IQuery;

/// <summary>E-VS3-05-9 / v2.1 §4.1: everything the user types into the provider's portal, taken from the issued invoice.</summary>
/// <summary>E-FIS1-03-8: an e-CF 44 carries its exemption (every line IndicadorFacturacion 4 = exento; the certificate for InformacionAdicionalComprador).</summary>
public sealed record FiscalPackageExemption(string Regime, string CertificateNo, string ProjectName, string BillingIndicator);

public sealed record InvoiceFiscalPackage(
    string InvoiceNo, string EcfType, DateOnly InvoiceDate, DateOnly DueDate, string IssuerRnc, string IssuerName, string ReceiverRnc, string ReceiverName,
    IReadOnlyList<InvoiceLineView> Lines, decimal NetTotal, decimal TaxTotal, decimal Total, string FiscalStatus, FiscalPackageExemption? Exemption = null,
    string? ReceiverPassport = null);

[RequiresPermission("sales:read")]
public sealed class GetInvoiceFiscalPackageHandler : IQueryHandler<GetInvoiceFiscalPackage>
{
    public string QueryType => "Sales.GetInvoiceFiscalPackage";

    private sealed record Head(string No, string Type, DateOnly? Date, DateOnly? Due, string IssuerRnc, string IssuerName, string ReceiverRnc, string ReceiverName, decimal Net, decimal? Tax, decimal? Total, string Fiscal,
        string? Passport);

    public async Task<string> HandleAsync(GetInvoiceFiscalPackage query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var h = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT i.invoice_no, i.ecf_type, i.invoice_date, i.due_date, c.rnc, c.legal_name, coalesce(CASE WHEN i.buyer_id_kind IN ('CEDULA', 'RNC') THEN i.buyer_id END, p.rnc, ''), coalesce(i.buyer_name, p.legal_name),
                   i.net_total::numeric(19,2), i.tax_total::numeric(19,2), i.total::numeric(19,2), i.fiscal_status, CASE WHEN i.buyer_id_kind = 'PASAPORTE' THEN i.buyer_id END
            FROM sal.invoice i JOIN md.party p ON p.party_id = i.party_id JOIN md.company c ON c.company_id = i.company_id
            WHERE i.company_id = @c AND i.invoice_id = @i
            """,
            r => new Head(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.Date(2), r.IsDBNull(3) ? null : r.Date(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7),
                r.GetDecimal(8), r.IsDBNull(9) ? null : r.GetDecimal(9), r.IsDBNull(10) ? null : r.GetDecimal(10), r.GetString(11), r.NullableString(12)),
            cancellationToken,
            ("c", context.CompanyId),
            ("i", query.InvoiceId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The invoice does not exist.");
        if (h.Date is null || h.Due is null || h.Tax is null || h.Total is null)
        {
            throw new DomainException(QueryErrors.InvalidParameter, "The fiscal package exists once the invoice is issued.");
        }

        var lines = await InvoiceLines.ReadAsync(context, query.InvoiceId, cancellationToken).ConfigureAwait(false);
        var exemption = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT a.regime, a.certificate_no, a.project_name
            FROM sal.invoice i JOIN tax.fiscal_authorization a ON a.authorization_id = i.fiscal_authorization_id
            WHERE i.invoice_id = @i
            """,
            r => new FiscalPackageExemption(r.GetString(0), r.GetString(1), r.GetString(2), "4"),
            cancellationToken,
            ("i", query.InvoiceId)).ConfigureAwait(false);
        return ApiJson.Serialize(new InvoiceFiscalPackage(
            h.No, h.Type, h.Date.Value, h.Due.Value, h.IssuerRnc, h.IssuerName, h.ReceiverRnc, h.ReceiverName, lines, h.Net, h.Tax.Value, h.Total.Value, h.Fiscal, exemption, h.Passport));
    }
}

public sealed record ListBillableDeliveries(Guid CompanyId, Guid SessionId, Guid? PartyId = null) : IQuery;

public sealed record BillableDeliveryLine(
    Guid DeliveryLineId, string DeliveryNo, Guid SalesOrderId, string OrderNo, Guid PartyId, string CustomerName, string ItemCode, string Uom, decimal QtyDelivered, decimal QtyInvoiced,
    decimal QtyBillable, decimal UnitPrice, decimal BillableNet);

public sealed record BillableDeliveryList(IReadOnlyList<BillableDeliveryLine> Items);

[RequiresPermission("sales:read")]
public sealed class ListBillableDeliveriesHandler : IQueryHandler<ListBillableDeliveries>
{
    public string QueryType => "Sales.ListBillableDeliveries";

    public async Task<string> HandleAsync(ListBillableDeliveries query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT dl.delivery_line_id, d.delivery_no, o.sales_order_id, o.order_no, o.party_id, p.legal_name, i.code, dl.uom, dl.qty_delivered, dl.qty_invoiced,
                   dl.qty_delivered - dl.qty_invoiced, ol.unit_price, round((dl.qty_delivered - dl.qty_invoiced) * ol.unit_price, 2)::numeric(19,2)
            FROM log.delivery_line dl
            JOIN log.delivery d ON d.delivery_id = dl.delivery_id
            JOIN sal.sales_order o ON o.sales_order_id = d.sales_order_id
            JOIN sal.sales_order_line ol ON ol.line_id = dl.sales_order_line_id
            JOIN md.party p ON p.party_id = o.party_id
            JOIN md.item i ON i.item_id = dl.item_id
            WHERE dl.company_id = @c AND d.status IN ('DELIVERED', 'DELIVERED_WITH_EXCEPTIONS') AND dl.qty_delivered > dl.qty_invoiced
              AND (CAST(@p AS uuid) IS NULL OR o.party_id = CAST(@p AS uuid))
              AND NOT EXISTS (SELECT 1 FROM sal.proforma pf WHERE pf.delivery_id = d.delivery_id AND pf.status <> 'VOIDED') -- E-FIS1b-01-6: billed from the proforma
            ORDER BY p.legal_name, d.delivery_no, dl.line_no
            LIMIT 500
            """,
            r => new BillableDeliveryLine(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetGuid(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetDecimal(8), r.GetDecimal(9),
                r.GetDecimal(10), r.GetDecimal(11), r.GetDecimal(12)),
            cancellationToken,
            ("c", context.CompanyId),
            ("p", query.PartyId)).ConfigureAwait(false);
        return ApiJson.Serialize(new BillableDeliveryList(items));
    }
}
