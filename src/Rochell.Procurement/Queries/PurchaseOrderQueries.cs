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

public sealed record PurchaseOrderSummary(
    Guid PurchaseOrderId,
    string PoNo,
    Guid SupplierId,
    string SupplierName,
    Guid PlantId,
    string PlantCode,
    DateOnly OrderDate,
    string Status,
    long Version);

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
            SELECT po.po_id, po.po_no, po.party_id, p.legal_name, po.plant_id, pl.code, po.order_date, po.status::text, po.version
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
            r => new PurchaseOrderSummary(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetGuid(4), r.GetString(5), r.Date(6), r.GetString(7), r.GetInt64(8)),
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

public sealed record PurchaseOrderLineView(
    Guid PoLineId,
    int LineNo,
    Guid ItemId,
    string ItemCode,
    string ItemDescription,
    string Uom,
    decimal QtyOrdered,
    decimal UnitPrice,
    decimal ReceiptTolerancePct,
    decimal QtyOverReceiptApproved,
    decimal QtyReceived,
    decimal QtyInvoiced,
    long Version);

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
    IReadOnlyList<StateChange> History);

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
                   cu.email, au.email, po.approved_at, po.policy_version_id, po.version
            FROM pur.purchase_order po
            JOIN md.party p ON p.party_id = po.party_id
            JOIN md.plant pl ON pl.plant_id = po.plant_id
            LEFT JOIN iam.user cu ON cu.user_id = po.created_by
            LEFT JOIN iam.user au ON au.user_id = po.approved_by
            WHERE po.company_id = @c AND po.po_id = @id AND (CAST(@plant AS uuid) IS NULL OR po.plant_id = CAST(@plant AS uuid))
            """,
            r => new PurchaseOrderDetail(
                r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetGuid(3), r.GetString(4), r.GetGuid(5), r.GetString(6), r.Date(7), r.GetString(8),
                r.NullableString(9), r.NullableString(10), r.NullableUtc(11), r.NullableGuid(12), r.GetInt64(13), [], [], []),
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
                   l.qty_over_receipt_approved, l.qty_received, l.qty_invoiced, l.version
            FROM pur.purchase_order_line l
            JOIN md.item i ON i.item_id = l.item_id
            WHERE l.company_id = @c AND l.po_id = @id
            ORDER BY l.line_no
            """,
            r => new PurchaseOrderLineView(
                r.GetGuid(0), r.GetInt32(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetDecimal(6), r.GetDecimal(7),
                r.GetDecimal(8), r.GetDecimal(9), r.GetDecimal(10), r.GetDecimal(11), r.GetInt64(12)),
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
        return ApiJson.Serialize(header with { Lines = lines, GoodsReceipts = receipts, History = history });
    }
}
