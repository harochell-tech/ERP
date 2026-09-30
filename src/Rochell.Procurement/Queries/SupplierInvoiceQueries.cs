using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;
using Rochell.Procurement.SupplierInvoices;

namespace Rochell.Procurement.Queries;

/// <summary>E-PR18-4: supplier invoices of the company (accounts payable works company-wide, §14), newest first.</summary>
public sealed record ListSupplierInvoices(
    Guid CompanyId,
    Guid SessionId,
    string? DocumentStatus = null,
    string? AccountingStatus = null,
    Guid? SupplierId = null,
    int Limit = 50,
    int Offset = 0) : IQuery;

/// <summary>
/// E-UX3-6: <see cref="TotalAmount"/> stays the net; <see cref="ItbisTotal"/> is the ITBIS of the invoice's tax determination
/// (recoverable and non-recoverable input, null until determined) and <see cref="GrossTotal"/> net + ITBIS; <see cref="OpenAmount"/>
/// is the AP document's open amount (null until posted); <see cref="PaymentStatus"/> is VOIDED, REVERSED, NOT_POSTED (no AP
/// document), PAID (open 0), PARTIAL (open below original) or OPEN. E-UX4-7: <see cref="PrintedTotal"/> is the total the supplier
/// printed (typed at registration, optional) and <see cref="PrintedTotalDifference"/> printed − gross, once the gross is determined.
/// </summary>
public sealed record SupplierInvoiceSummary(
    Guid SupplierInvoiceId,
    Guid SupplierId,
    string SupplierName,
    string SupplierFiscalNumber,
    DateOnly DocDate,
    DateOnly DueDate,
    string DocumentStatus,
    string AccountingStatus,
    decimal TotalAmount,
    long Version,
    decimal? ItbisTotal,
    decimal? GrossTotal,
    decimal? OpenAmount,
    string PaymentStatus,
    decimal? PrintedTotal,
    decimal? PrintedTotalDifference);

public sealed record SupplierInvoiceList(IReadOnlyList<SupplierInvoiceSummary> Items, int Limit, int Offset);

[RequiresPermission("supplier_invoice:read")]
public sealed class ListSupplierInvoicesHandler : IQueryHandler<ListSupplierInvoices>
{
    public string QueryType => "Procurement.ListSupplierInvoices";

    public async Task<string> HandleAsync(ListSupplierInvoices query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT si.si_id, si.party_id, p.legal_name, si.supplier_fiscal_number, si.doc_date, si.due_date, si.document_status::text,
                   si.accounting_status::text, si.total_amount, si.version,
            """ + ApAmounts.Columns + """
            FROM pur.supplier_invoice si
            JOIN md.party p ON p.party_id = si.party_id
            """ + ApAmounts.Joins + """
            WHERE si.company_id = @c
              AND (CAST(@doc AS text) IS NULL OR si.document_status::text = CAST(@doc AS text))
              AND (CAST(@acc AS text) IS NULL OR si.accounting_status::text = CAST(@acc AS text))
              AND (CAST(@supplier AS uuid) IS NULL OR si.party_id = CAST(@supplier AS uuid))
            ORDER BY si.doc_date DESC, si.si_id DESC
            LIMIT @limit OFFSET @offset
            """,
            r => new SupplierInvoiceSummary(
                r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.Date(4), r.Date(5), r.GetString(6), r.GetString(7), r.GetDecimal(8), r.GetInt64(9),
                r.NullableDecimal(10), r.NullableDecimal(11), r.NullableDecimal(12), r.GetString(13), r.NullableDecimal(14), r.NullableDecimal(15)),
            cancellationToken,
            ("c", context.CompanyId),
            ("doc", query.DocumentStatus),
            ("acc", query.AccountingStatus),
            ("supplier", query.SupplierId),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new SupplierInvoiceList(items, query.Limit, query.Offset));
    }
}

/// <summary>One supplier invoice with its lines and match results, determined taxes, payable and status history.</summary>
public sealed record GetSupplierInvoice(Guid CompanyId, Guid SessionId, Guid SupplierInvoiceId) : IQuery;

public sealed record MatchResultView(
    decimal QtyAvailableToInvoice,
    decimal QtyDiff,
    decimal PriceDiff,
    decimal AmountDiff,
    bool QtyExceeds,
    bool WithinTolerance,
    Guid PolicyVersionId,
    DateTime EvaluatedAt);

public sealed record SupplierInvoiceLineView(
    Guid SiLineId,
    int LineNo,
    string LineKind,
    Guid PoLineId,
    Guid PurchaseOrderId,
    string PoNo,
    Guid ItemId,
    string ItemCode,
    decimal Qty,
    decimal UnitPrice,
    decimal NetAmount,
    MatchResultView? Match);

public sealed record DeterminedTaxView(Guid SiLineId, string TaxCode, decimal Base, decimal Rate, decimal Amount, string Effect, Guid RuleVersionId);

public sealed record ApDocumentView(Guid ApDocumentId, string DocType, decimal OriginalAmount, decimal OpenAmount, DateOnly DueDate);

/// <summary>E-UX3-6: a released payment applied to the invoice — its net applied amount (0 once reversed) and its status.</summary>
public sealed record SupplierInvoicePaymentView(Guid PaymentId, string PaymentNo, DateOnly ValueDate, string Status, decimal AmountApplied);

/// <summary>E-UX3-6: the ITBIS, gross, open amount and payment status of a supplier invoice, in SQL (see <see cref="SupplierInvoiceSummary"/>).</summary>
internal static class ApAmounts
{
    public const string Columns = """
                   CASE WHEN si.tax_determination_id IS NOT NULL THEN coalesce(t.itbis, 0) END,
                   CASE WHEN si.tax_determination_id IS NOT NULL THEN si.total_amount + coalesce(t.itbis, 0) END,
                   ap.open_amount,
                   CASE WHEN si.document_status::text = 'VOIDED' THEN 'VOIDED'
                        WHEN si.accounting_status::text = 'REVERSED' THEN 'REVERSED'
                        WHEN ap.ap_doc_id IS NULL THEN 'NOT_POSTED'
                        WHEN ap.open_amount = 0 THEN 'PAID'
                        WHEN ap.open_amount < ap.original_amount THEN 'PARTIAL'
                        ELSE 'OPEN' END,
                   si.printed_total,
                   CASE WHEN si.tax_determination_id IS NOT NULL AND si.printed_total IS NOT NULL THEN (si.printed_total - (si.total_amount + coalesce(t.itbis, 0)))::numeric(19,2) END

        """;

    public const string Joins = """
            LEFT JOIN LATERAL (SELECT sum(d.amount) AS itbis FROM tax.tax_determination_line d
                               WHERE d.determination_id = si.tax_determination_id AND d.effect IN ('RECOVERABLE_INPUT', 'NON_RECOVERABLE_INPUT')) t ON true
            LEFT JOIN fin.ap_document ap ON ap.company_id = si.company_id AND ap.doc_type = 'SUPPLIER_INVOICE' AND ap.source_doc_id = si.si_id

        """;
}

public sealed record SupplierInvoiceDetail(
    Guid SupplierInvoiceId,
    Guid SupplierId,
    string SupplierName,
    string SupplierFiscalNumber,
    DateOnly DocDate,
    DateOnly DueDate,
    string DocumentStatus,
    string AccountingStatus,
    decimal TotalAmount,
    string? CreatedBy,
    string? ExceptionApprovedBy,
    Guid? TaxDeterminationId,
    Guid? PostingEventId,
    long Version,
    IReadOnlyList<SupplierInvoiceLineView> Lines,
    IReadOnlyList<DeterminedTaxView> Taxes,
    ApDocumentView? ApDocument,
    IReadOnlyList<StateChange> History,
    decimal? ItbisTotal,
    decimal? GrossTotal,
    decimal? OpenAmount,
    string PaymentStatus,
    IReadOnlyList<SupplierInvoicePaymentView> Payments,
    decimal? PrintedTotal,
    decimal? PrintedTotalDifference);

[RequiresPermission("supplier_invoice:read")]
public sealed class GetSupplierInvoiceHandler : IQueryHandler<GetSupplierInvoice>
{
    public string QueryType => "Procurement.GetSupplierInvoice";

    public async Task<string> HandleAsync(GetSupplierInvoice query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var header = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT si.si_id, si.party_id, p.legal_name, si.supplier_fiscal_number, si.doc_date, si.due_date, si.document_status::text,
                   si.accounting_status::text, si.total_amount, coalesce(cu.display_name, cu.email), coalesce(eu.display_name, eu.email), si.tax_determination_id, si.posting_event_id, si.version,
            """ + ApAmounts.Columns + """
            FROM pur.supplier_invoice si
            JOIN md.party p ON p.party_id = si.party_id
            """ + ApAmounts.Joins + """
            LEFT JOIN iam.user cu ON cu.user_id = si.created_by
            LEFT JOIN iam.user eu ON eu.user_id = si.exception_approved_by
            WHERE si.company_id = @c AND si.si_id = @id
            """,
            r => new SupplierInvoiceDetail(
                r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.Date(4), r.Date(5), r.GetString(6), r.GetString(7), r.GetDecimal(8),
                r.NullableString(9), r.NullableString(10), r.NullableGuid(11), r.NullableGuid(12), r.GetInt64(13), [], [], null, [],
                r.NullableDecimal(14), r.NullableDecimal(15), r.NullableDecimal(16), r.GetString(17), [], r.NullableDecimal(18), r.NullableDecimal(19)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", query.SupplierInvoiceId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The supplier invoice does not exist.");

        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.si_line_id, l.line_no, l.line_kind, l.po_line_id, po.po_id, po.po_no, pl.item_id, i.code, l.qty, l.unit_price, l.net_amount,
                   m.si_line_id IS NOT NULL, m.qty_available_to_invoice, m.qty_diff, m.price_diff, m.amount_diff, m.qty_exceeds, m.within_tolerance,
                   m.policy_version_id, m.evaluated_at
            FROM pur.supplier_invoice_line l
            JOIN pur.purchase_order_line pl ON pl.po_line_id = l.po_line_id
            JOIN pur.purchase_order po ON po.po_id = pl.po_id
            JOIN md.item i ON i.item_id = pl.item_id
            LEFT JOIN pur.match_result m ON m.si_line_id = l.si_line_id
            WHERE l.company_id = @c AND l.si_id = @id
            ORDER BY l.line_no
            """,
            r => new SupplierInvoiceLineView(
                r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetGuid(3), r.GetGuid(4), r.GetString(5), r.GetGuid(6), r.GetString(7), r.GetDecimal(8),
                r.GetDecimal(9), r.GetDecimal(10),
                r.GetBoolean(11)
                    ? new MatchResultView(r.GetDecimal(12), r.GetDecimal(13), r.GetDecimal(14), r.GetDecimal(15), r.GetBoolean(16), r.GetBoolean(17), r.GetGuid(18), r.Utc(19))
                    : null),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", query.SupplierInvoiceId)).ConfigureAwait(false);

        var taxes = header.TaxDeterminationId is not { } determination
            ? []
            : await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT subject_line_id, tax_code, base, rate, amount, effect, rule_version_id
                FROM tax.tax_determination_line
                WHERE company_id = @c AND determination_id = @d
                ORDER BY line_no
                """,
                r => new DeterminedTaxView(r.GetGuid(0), r.GetString(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4), r.GetString(5), r.GetGuid(6)),
                cancellationToken,
                ("c", context.CompanyId),
                ("d", determination)).ConfigureAwait(false);

        var ap = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT ap_doc_id, doc_type, original_amount, open_amount, due_date FROM fin.ap_document WHERE company_id = @c AND source_doc_id = @id",
            r => new ApDocumentView(r.GetGuid(0), r.GetString(1), r.GetDecimal(2), r.GetDecimal(3), r.Date(4)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", query.SupplierInvoiceId)).ConfigureAwait(false);

        // E-UX3-6: the payments that applied to it, read from the Treasury tables in SQL (Procurement does not reference Treasury).
        var payments = ap is null
            ? []
            : await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT p.payment_id, p.payment_no, p.value_date, p.status::text,
                       sum(CASE WHEN a.reverses_application_id IS NULL THEN a.amount ELSE -a.amount END)
                FROM fin.ap_application a JOIN fin.payment p ON p.payment_id = a.payment_id
                WHERE a.company_id = @c AND a.ap_doc_id = @d
                GROUP BY p.payment_id, p.payment_no, p.value_date, p.status
                ORDER BY p.value_date, p.payment_no
                """,
                r => new SupplierInvoicePaymentView(r.GetGuid(0), r.GetString(1), r.Date(2), r.GetString(3), r.GetDecimal(4)),
                cancellationToken,
                ("c", context.CompanyId),
                ("d", ap.ApDocumentId)).ConfigureAwait(false);

        var history = await StateHistory.ReadAsync(context, SupplierInvoiceStore.Aggregate, query.SupplierInvoiceId, cancellationToken).ConfigureAwait(false);
        return ApiJson.Serialize(header with { Lines = lines, Taxes = taxes, ApDocument = ap, History = history, Payments = payments });
    }
}
