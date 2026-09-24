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
    long Version);

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
                   si.accounting_status::text, si.total_amount, si.version
            FROM pur.supplier_invoice si
            JOIN md.party p ON p.party_id = si.party_id
            WHERE si.company_id = @c
              AND (CAST(@doc AS text) IS NULL OR si.document_status::text = CAST(@doc AS text))
              AND (CAST(@acc AS text) IS NULL OR si.accounting_status::text = CAST(@acc AS text))
              AND (CAST(@supplier AS uuid) IS NULL OR si.party_id = CAST(@supplier AS uuid))
            ORDER BY si.doc_date DESC, si.si_id DESC
            LIMIT @limit OFFSET @offset
            """,
            r => new SupplierInvoiceSummary(
                r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.Date(4), r.Date(5), r.GetString(6), r.GetString(7), r.GetDecimal(8), r.GetInt64(9)),
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
    IReadOnlyList<StateChange> History);

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
                   si.accounting_status::text, si.total_amount, cu.email, eu.email, si.tax_determination_id, si.posting_event_id, si.version
            FROM pur.supplier_invoice si
            JOIN md.party p ON p.party_id = si.party_id
            LEFT JOIN iam.user cu ON cu.user_id = si.created_by
            LEFT JOIN iam.user eu ON eu.user_id = si.exception_approved_by
            WHERE si.company_id = @c AND si.si_id = @id
            """,
            r => new SupplierInvoiceDetail(
                r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.Date(4), r.Date(5), r.GetString(6), r.GetString(7), r.GetDecimal(8),
                r.NullableString(9), r.NullableString(10), r.NullableGuid(11), r.NullableGuid(12), r.GetInt64(13), [], [], null, []),
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

        var history = await StateHistory.ReadAsync(context, SupplierInvoiceStore.Aggregate, query.SupplierInvoiceId, cancellationToken).ConfigureAwait(false);
        return ApiJson.Serialize(header with { Lines = lines, Taxes = taxes, ApDocument = ap, History = history });
    }
}
