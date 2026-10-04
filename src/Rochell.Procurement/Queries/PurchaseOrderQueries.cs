using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;
using Rochell.Procurement.PurchaseOrders;

namespace Rochell.Procurement.Queries;

/// <summary>E-PR18-4: purchase orders of the company (or of one plant), newest first.</summary>
public sealed record ListPurchaseOrders(
    Guid CompanyId,
    Guid SessionId,
    Guid? PlantId = null,
    string? Status = null,
    Guid? SupplierId = null,
    int Limit = 50,
    int Offset = 0) : IPlantScopedQuery;

/// <summary>E-UX4-2: <see cref="Total"/> is the order's net, Σ of each line's quantity × unit price rounded to 2 decimals.</summary>
public sealed record PurchaseOrderSummary(
    Guid PurchaseOrderId,
    string PoNo,
    Guid SupplierId,
    string SupplierName,
    Guid PlantId,
    string PlantCode,
    DateOnly OrderDate,
    string Status,
    long Version,
    decimal Total,
    string DocClass = "INVENTORY");

public sealed record PurchaseOrderList(IReadOnlyList<PurchaseOrderSummary> Items, int Limit, int Offset);

[RequiresPermission("purchase_order:read")]
public sealed class ListPurchaseOrdersHandler : IQueryHandler<ListPurchaseOrders>
{
    public string QueryType => "Procurement.ListPurchaseOrders";

    public async Task<string> HandleAsync(ListPurchaseOrders query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT po.po_id, po.po_no, po.party_id, p.legal_name, po.plant_id, pl.code, po.order_date, po.status::text, po.version,
                   (SELECT coalesce(sum(round(l.qty_ordered * l.unit_price, 2)), 0)::numeric(19,2) FROM pur.purchase_order_line l WHERE l.po_id = po.po_id), po.doc_class
            FROM pur.purchase_order po
            JOIN md.party p ON p.party_id = po.party_id
            JOIN md.plant pl ON pl.plant_id = po.plant_id
            WHERE po.company_id = @c
              AND (CAST(@plant AS uuid) IS NULL OR po.plant_id = CAST(@plant AS uuid))
              AND (CAST(@status AS text) IS NULL OR po.status::text = CAST(@status AS text))
              AND (CAST(@supplier AS uuid) IS NULL OR po.party_id = CAST(@supplier AS uuid))
            ORDER BY po.order_date DESC, po.po_no DESC
            LIMIT @limit OFFSET @offset
            """,
            r => new PurchaseOrderSummary(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetGuid(4), r.GetString(5), r.Date(6), r.GetString(7), r.GetInt64(8), r.GetDecimal(9), r.GetString(10)),
            cancellationToken,
            ("c", context.CompanyId),
            ("plant", query.PlantId),
            ("status", query.Status),
            ("supplier", query.SupplierId),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new PurchaseOrderList(items, query.Limit, query.Offset));
    }
}

/// <summary>One purchase order with its lines, receipts and status history.</summary>
public sealed record GetPurchaseOrder(Guid CompanyId, Guid SessionId, Guid PurchaseOrderId, Guid? PlantId = null) : IPlantScopedQuery;

/// <summary>E-UX3-5: <see cref="OpenQuantity"/> = ordered − received, never below 0. E-UX4-2: <see cref="NetAmount"/> = ordered × price, 2 decimals.</summary>
/// <summary>An inventory line names its item and unit; an expense line (GAS1-05) its description, category and tax type (item and unit null).</summary>
public sealed record PurchaseOrderLineView(
    Guid PoLineId,
    int LineNo,
    Guid? ItemId,
    string? ItemCode,
    string? ItemDescription,
    string? Uom,
    decimal QtyOrdered,
    decimal UnitPrice,
    decimal ReceiptTolerancePct,
    decimal QtyOverReceiptApproved,
    decimal QtyReceived,
    decimal QtyInvoiced,
    long Version,
    decimal OpenQuantity,
    decimal NetAmount,
    string? Description = null,
    Guid? ExpenseCategoryId = null,
    string? ExpenseCategoryName = null,
    Guid? TaxTypeId = null,
    string? TaxTypeCode = null);

public sealed record PurchaseOrderReceiptView(Guid GoodsReceiptId, string GrNo, string DocumentStatus, string AccountingStatus, DateTime OccurredAt);

public sealed record PurchaseOrderDetail(
    Guid PurchaseOrderId,
    string PoNo,
    int Revision,
    Guid SupplierId,
    string SupplierName,
    Guid PlantId,
    string PlantCode,
    DateOnly OrderDate,
    string Status,
    string? CreatedBy,
    string? ApprovedBy,
    DateTime? ApprovedAt,
    Guid? PolicyVersionId,
    long Version,
    IReadOnlyList<PurchaseOrderLineView> Lines,
    IReadOnlyList<PurchaseOrderReceiptView> GoodsReceipts,
    IReadOnlyList<StateChange> History,
    decimal Total,
    string DocClass = "INVENTORY");

[RequiresPermission("purchase_order:read")]
public sealed class GetPurchaseOrderHandler : IQueryHandler<GetPurchaseOrder>
{
    public string QueryType => "Procurement.GetPurchaseOrder";

    public async Task<string> HandleAsync(GetPurchaseOrder query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var header = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT po.po_id, po.po_no, po.revision, po.party_id, p.legal_name, po.plant_id, pl.code, po.order_date, po.status::text,
                   coalesce(cu.display_name, cu.email), coalesce(au.display_name, au.email), po.approved_at, po.policy_version_id, po.version, po.doc_class
            FROM pur.purchase_order po
            JOIN md.party p ON p.party_id = po.party_id
            JOIN md.plant pl ON pl.plant_id = po.plant_id
            LEFT JOIN iam.user cu ON cu.user_id = po.created_by
            LEFT JOIN iam.user au ON au.user_id = po.approved_by
            WHERE po.company_id = @c AND po.po_id = @id AND (CAST(@plant AS uuid) IS NULL OR po.plant_id = CAST(@plant AS uuid))
            """,
            r => new PurchaseOrderDetail(
                r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetGuid(3), r.GetString(4), r.GetGuid(5), r.GetString(6), r.Date(7), r.GetString(8),
                r.NullableString(9), r.NullableString(10), r.NullableUtc(11), r.NullableGuid(12), r.GetInt64(13), [], [], [], 0m, r.GetString(14)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", query.PurchaseOrderId),
            ("plant", query.PlantId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The purchase order does not exist.");

        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.po_line_id, l.line_no, l.item_id, i.code, i.description, l.uom, l.qty_ordered, l.unit_price, l.receipt_tolerance_pct,
                   l.qty_over_receipt_approved, l.qty_received, l.qty_invoiced, l.version,
                   greatest(l.qty_ordered - CASE WHEN l.item_id IS NULL THEN l.qty_invoiced ELSE l.qty_received END, 0),
                   round(l.qty_ordered * l.unit_price, 2)::numeric(19,2), l.description, l.expense_category_id, c.name, l.tax_rule_id, r.code
            FROM pur.purchase_order_line l
            LEFT JOIN md.item i ON i.item_id = l.item_id
            LEFT JOIN pur.expense_category c ON c.expense_category_id = l.expense_category_id
            LEFT JOIN tax.fiscal_rule r ON r.rule_id = l.tax_rule_id
            WHERE l.company_id = @c AND l.po_id = @id
            ORDER BY l.line_no
            """,
            r => new PurchaseOrderLineView(
                r.GetGuid(0), r.GetInt32(1), r.NullableGuid(2), r.NullableString(3), r.NullableString(4), r.NullableString(5), r.GetDecimal(6), r.GetDecimal(7),
                r.GetDecimal(8), r.GetDecimal(9), r.GetDecimal(10), r.GetDecimal(11), r.GetInt64(12), r.GetDecimal(13), r.GetDecimal(14),
                r.NullableString(15), r.NullableGuid(16), r.NullableString(17), r.NullableGuid(18), r.NullableString(19)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", query.PurchaseOrderId)).ConfigureAwait(false);

        var receipts = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT gr_id, gr_no, document_status::text, accounting_status::text, occurred_at
            FROM pur.goods_receipt
            WHERE company_id = @c AND po_id = @id
            ORDER BY occurred_at, gr_no
            """,
            r => new PurchaseOrderReceiptView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.Utc(4)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", query.PurchaseOrderId)).ConfigureAwait(false);

        var history = await StateHistory.ReadAsync(context, PurchaseOrderStore.Aggregate, query.PurchaseOrderId, cancellationToken).ConfigureAwait(false);
        return ApiJson.Serialize(header with { Lines = lines, GoodsReceipts = receipts, History = history, Total = Money.Zero + lines.Sum(l => l.NetAmount) });
    }
}

/// <summary>
/// E-UX3-5: the orders the warehouse can receive against — APPROVED and PARTIALLY_RECEIVED, oldest first, of the company or of one
/// plant — with each line's open and receivable quantities.
/// </summary>
public sealed record ListPurchaseOrdersToReceive(
    Guid CompanyId,
    Guid SessionId,
    Guid? PlantId = null,
    Guid? SupplierId = null,
    int Limit = 50,
    int Offset = 0) : IPlantScopedQuery;

/// <summary>
/// <see cref="OpenQuantity"/> = ordered − received (not below 0). <see cref="MaxReceivable"/> is what one receipt may still take,
/// exactly as PostGoodsReceipt validates it: ordered × (1 + receipt tolerance) + approved over-receipt − received, cut to the six
/// decimals a receipt quantity may have, not below 0.
/// </summary>
public sealed record PurchaseOrderLineToReceive(
    Guid PoLineId,
    int LineNo,
    Guid ItemId,
    string ItemCode,
    string ItemDescription,
    string Uom,
    decimal QtyOrdered,
    decimal QtyReceived,
    decimal OpenQuantity,
    decimal MaxReceivable,
    long Version);

/// <summary>
/// E-UX4-8: <see cref="DefaultLocationId"/> / <see cref="DefaultLocationCode"/> are where the plant receives raw material: its
/// location coded RECEPCION, otherwise its only location that is neither CURADO nor TRANSITO; null when neither applies.
/// </summary>
public sealed record PurchaseOrderToReceive(
    Guid PurchaseOrderId,
    string PoNo,
    Guid SupplierId,
    string SupplierName,
    Guid PlantId,
    string PlantCode,
    DateOnly OrderDate,
    DateTime? ApprovedAt,
    string Status,
    long Version,
    IReadOnlyList<PurchaseOrderLineToReceive> Lines,
    Guid? DefaultLocationId,
    string? DefaultLocationCode);

public sealed record PurchaseOrderToReceiveList(IReadOnlyList<PurchaseOrderToReceive> Items, int Limit, int Offset);

/// <summary>Read with <c>purchase_order:read</c>, the permission the receive screen already needs to open an order (Almacenista holds it).</summary>
[RequiresPermission("purchase_order:read")]
public sealed class ListPurchaseOrdersToReceiveHandler : IQueryHandler<ListPurchaseOrdersToReceive>
{
    /// <summary>E-UX4-8: the receiving location's code in the staging runbook and the test fixtures.</summary>
    public const string DefaultReceivingLocationCode = "RECEPCION";

    public string QueryType => "Procurement.ListPurchaseOrdersToReceive";

    public async Task<string> HandleAsync(ListPurchaseOrdersToReceive query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var orders = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT po.po_id, po.po_no, po.party_id, p.legal_name, po.plant_id, pl.code, po.order_date, po.approved_at, po.status::text, po.version,
                   dl.location_id, dl.code
            FROM pur.purchase_order po
            JOIN md.party p ON p.party_id = po.party_id
            JOIN md.plant pl ON pl.plant_id = po.plant_id
            LEFT JOIN LATERAL (
              SELECT l.location_id, l.code FROM md.location l
              WHERE l.plant_id = po.plant_id AND NOT l.is_curing AND NOT l.is_transit
                AND (l.code = @receiving
                     OR (SELECT count(*) FROM md.location x WHERE x.plant_id = po.plant_id AND NOT x.is_curing AND NOT x.is_transit) = 1)
              ORDER BY l.code = @receiving DESC
              LIMIT 1) dl ON true
            WHERE po.company_id = @c AND po.status::text IN ('APPROVED', 'PARTIALLY_RECEIVED') AND po.doc_class = 'INVENTORY' -- E-GAS-05-5
              AND (CAST(@plant AS uuid) IS NULL OR po.plant_id = CAST(@plant AS uuid))
              AND (CAST(@supplier AS uuid) IS NULL OR po.party_id = CAST(@supplier AS uuid))
            ORDER BY po.order_date, po.po_no
            LIMIT @limit OFFSET @offset
            """,
            r => new PurchaseOrderToReceive(
                r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetGuid(4), r.GetString(5), r.Date(6), r.NullableUtc(7), r.GetString(8), r.GetInt64(9), [],
                r.NullableGuid(10), r.NullableString(11)),
            cancellationToken,
            ("c", context.CompanyId),
            ("receiving", DefaultReceivingLocationCode),
            ("plant", query.PlantId),
            ("supplier", query.SupplierId),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);

        var lines = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.po_id, l.po_line_id, l.line_no, l.item_id, i.code, i.description, l.uom, l.qty_ordered, l.qty_received,
                   greatest(l.qty_ordered - l.qty_received, 0),
                   greatest(trunc(l.qty_ordered * (1 + l.receipt_tolerance_pct) + l.qty_over_receipt_approved - l.qty_received, 6), 0),
                   l.version
            FROM pur.purchase_order_line l
            JOIN md.item i ON i.item_id = l.item_id
            WHERE l.company_id = @c AND l.po_id = ANY(@ids)
            ORDER BY l.po_id, l.line_no
            """,
            r => (PoId: r.GetGuid(0), Line: new PurchaseOrderLineToReceive(
                r.GetGuid(1), r.GetInt32(2), r.GetGuid(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9),
                r.GetDecimal(10), r.GetInt64(11))),
            cancellationToken,
            ("c", context.CompanyId),
            ("ids", orders.Select(o => o.PurchaseOrderId).ToArray())).ConfigureAwait(false))
            .ToLookup(l => l.PoId, l => l.Line);

        var items = orders.Select(o => o with { Lines = lines[o.PurchaseOrderId].ToList() }).ToList();
        return ApiJson.Serialize(new PurchaseOrderToReceiveList(items, query.Limit, query.Offset));
    }
}
