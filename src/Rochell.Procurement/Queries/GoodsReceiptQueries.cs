using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;
using Rochell.Procurement.GoodsReceipts;

namespace Rochell.Procurement.Queries;

/// <summary>E-PR18-4: goods receipts of the company (or of one plant), newest first.</summary>
public sealed record ListGoodsReceipts(
    Guid CompanyId,
    Guid SessionId,
    Guid? PlantId = null,
    Guid? PurchaseOrderId = null,
    string? DocumentStatus = null,
    int Limit = 50,
    int Offset = 0) : IPlantScopedQuery;

public sealed record GoodsReceiptSummary(
    Guid GoodsReceiptId,
    string GrNo,
    Guid PurchaseOrderId,
    string PoNo,
    Guid PlantId,
    Guid LocationId,
    string LocationCode,
    string DocumentStatus,
    string AccountingStatus,
    DateTime OccurredAt,
    long Version);

public sealed record GoodsReceiptList(IReadOnlyList<GoodsReceiptSummary> Items, int Limit, int Offset);

[RequiresPermission("goods_receipt:read")]
public sealed class ListGoodsReceiptsHandler : IQueryHandler<ListGoodsReceipts>
{
    public string QueryType => "Procurement.ListGoodsReceipts";

    public async Task<string> HandleAsync(ListGoodsReceipts query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT gr.gr_id, gr.gr_no, gr.po_id, po.po_no, po.plant_id, gr.location_id, loc.code, gr.document_status::text,
                   gr.accounting_status::text, gr.occurred_at, gr.version
            FROM pur.goods_receipt gr
            JOIN pur.purchase_order po ON po.po_id = gr.po_id
            JOIN md.location loc ON loc.location_id = gr.location_id
            WHERE gr.company_id = @c
              AND (CAST(@plant AS uuid) IS NULL OR po.plant_id = CAST(@plant AS uuid))
              AND (CAST(@po AS uuid) IS NULL OR gr.po_id = CAST(@po AS uuid))
              AND (CAST(@status AS text) IS NULL OR gr.document_status::text = CAST(@status AS text))
            ORDER BY gr.occurred_at DESC, gr.gr_no DESC
            LIMIT @limit OFFSET @offset
            """,
            r => new GoodsReceiptSummary(
                r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetGuid(4), r.GetGuid(5), r.GetString(6), r.GetString(7), r.GetString(8),
                r.Utc(9), r.GetInt64(10)),
            cancellationToken,
            ("c", context.CompanyId),
            ("plant", query.PlantId),
            ("po", query.PurchaseOrderId),
            ("status", query.DocumentStatus),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new GoodsReceiptList(items, query.Limit, query.Offset));
    }
}

/// <summary>One goods receipt with its lines, its reversal, its quantity corrections and its status history.</summary>
public sealed record GetGoodsReceipt(Guid CompanyId, Guid SessionId, Guid GoodsReceiptId, Guid? PlantId = null) : IPlantScopedQuery;

public sealed record GoodsReceiptLineView(
    Guid GrLineId,
    Guid PoLineId,
    int PoLineNo,
    Guid ItemId,
    string ItemCode,
    Guid LotId,
    string LotCode,
    string? SupplierLotNumber,
    decimal Qty,
    decimal UnitPrice);

public sealed record GoodsReceiptReversalView(Guid ReversalId, string Reason, string AccountingStatus, Guid PostingEventId);

public sealed record ReceiptCorrectionView(
    Guid CorrectionId,
    Guid GoodsReceiptId,
    string GrNo,
    Guid GrLineId,
    decimal DeltaQty,
    string Reason,
    string EvidenceObjectKey,
    string DocumentStatus,
    string AccountingStatus,
    string? CreatedBy,
    string? ApprovedBy,
    Guid? PostingEventId,
    long Version);

public sealed record GoodsReceiptDetail(
    Guid GoodsReceiptId,
    string GrNo,
    Guid PurchaseOrderId,
    string PoNo,
    Guid PlantId,
    Guid LocationId,
    string LocationCode,
    string? WeighTicketRef,
    string DocumentStatus,
    string AccountingStatus,
    DateTime OccurredAt,
    Guid PostingEventId,
    long Version,
    IReadOnlyList<GoodsReceiptLineView> Lines,
    GoodsReceiptReversalView? Reversal,
    IReadOnlyList<ReceiptCorrectionView> Corrections,
    IReadOnlyList<StateChange> History);

[RequiresPermission("goods_receipt:read")]
public sealed class GetGoodsReceiptHandler : IQueryHandler<GetGoodsReceipt>
{
    public string QueryType => "Procurement.GetGoodsReceipt";

    public async Task<string> HandleAsync(GetGoodsReceipt query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var header = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT gr.gr_id, gr.gr_no, gr.po_id, po.po_no, po.plant_id, gr.location_id, loc.code, gr.weigh_ticket_ref, gr.document_status::text,
                   gr.accounting_status::text, gr.occurred_at, gr.posting_event_id, gr.version
            FROM pur.goods_receipt gr
            JOIN pur.purchase_order po ON po.po_id = gr.po_id
            JOIN md.location loc ON loc.location_id = gr.location_id
            WHERE gr.company_id = @c AND gr.gr_id = @id AND (CAST(@plant AS uuid) IS NULL OR po.plant_id = CAST(@plant AS uuid))
            """,
            r => new GoodsReceiptDetail(
                r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetGuid(4), r.GetGuid(5), r.GetString(6), r.NullableString(7), r.GetString(8),
                r.GetString(9), r.Utc(10), r.GetGuid(11), r.GetInt64(12), [], null, [], []),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", query.GoodsReceiptId),
            ("plant", query.PlantId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The goods receipt does not exist.");

        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.gr_line_id, l.po_line_id, pl.line_no, pl.item_id, i.code, l.lot_id, lot.lot_code, lot.supplier_lot_number, l.qty, l.unit_price
            FROM pur.goods_receipt_line l
            JOIN pur.purchase_order_line pl ON pl.po_line_id = l.po_line_id
            JOIN md.item i ON i.item_id = pl.item_id
            JOIN inv.lot lot ON lot.lot_id = l.lot_id
            WHERE l.company_id = @c AND l.gr_id = @id
            ORDER BY pl.line_no, l.gr_line_id
            """,
            r => new GoodsReceiptLineView(
                r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetGuid(3), r.GetString(4), r.GetGuid(5), r.GetString(6), r.NullableString(7), r.GetDecimal(8), r.GetDecimal(9)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", query.GoodsReceiptId)).ConfigureAwait(false);

        var reversal = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT grr_id, reason, accounting_status::text, posting_event_id FROM pur.goods_receipt_reversal WHERE company_id = @c AND reversed_gr_id = @id",
            r => new GoodsReceiptReversalView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetGuid(3)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", query.GoodsReceiptId)).ConfigureAwait(false);

        var corrections = await ReceiptCorrectionReader.ListAsync(context, null, query.GoodsReceiptId, null, int.MaxValue, 0, cancellationToken).ConfigureAwait(false);
        var history = await StateHistory.ReadAsync(context, PostGoodsReceiptHandler.Aggregate, query.GoodsReceiptId, cancellationToken).ConfigureAwait(false);
        return ApiJson.Serialize(header with { Lines = lines, Reversal = reversal, Corrections = corrections, History = history });
    }
}

/// <summary>Receipt quantity corrections (E-8), e.g. those pending the Controller's approval.</summary>
public sealed record ListReceiptCorrections(
    Guid CompanyId,
    Guid SessionId,
    Guid? PlantId = null,
    string? DocumentStatus = null,
    int Limit = 50,
    int Offset = 0) : IPlantScopedQuery;

public sealed record ReceiptCorrectionList(IReadOnlyList<ReceiptCorrectionView> Items, int Limit, int Offset);

[RequiresPermission("goods_receipt:read")]
public sealed class ListReceiptCorrectionsHandler : IQueryHandler<ListReceiptCorrections>
{
    public string QueryType => "Procurement.ListReceiptCorrections";

    public async Task<string> HandleAsync(ListReceiptCorrections query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var items = await ReceiptCorrectionReader.ListAsync(context, query.PlantId, null, query.DocumentStatus, query.Limit, query.Offset, cancellationToken).ConfigureAwait(false);
        return ApiJson.Serialize(new ReceiptCorrectionList(items, query.Limit, query.Offset));
    }
}

internal static class ReceiptCorrectionReader
{
    public static Task<List<ReceiptCorrectionView>> ListAsync(
        QueryContext context,
        Guid? plantId,
        Guid? goodsReceiptId,
        string? documentStatus,
        int limit,
        int offset,
        CancellationToken cancellationToken)
        => Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT rc.rc_id, rc.gr_id, gr.gr_no, rc.gr_line_id, rc.delta_qty, rc.reason, rc.evidence_object_key, rc.document_status,
                   rc.accounting_status::text, cu.email, au.email, rc.posting_event_id, rc.version
            FROM pur.receipt_correction rc
            JOIN pur.goods_receipt gr ON gr.gr_id = rc.gr_id
            JOIN pur.purchase_order po ON po.po_id = gr.po_id
            LEFT JOIN iam.user cu ON cu.user_id = rc.created_by
            LEFT JOIN iam.user au ON au.user_id = rc.approved_by
            WHERE rc.company_id = @c
              AND (CAST(@plant AS uuid) IS NULL OR po.plant_id = CAST(@plant AS uuid))
              AND (CAST(@gr AS uuid) IS NULL OR rc.gr_id = CAST(@gr AS uuid))
              AND (CAST(@status AS text) IS NULL OR rc.document_status = CAST(@status AS text))
            ORDER BY rc.rc_id DESC
            LIMIT @limit OFFSET @offset
            """,
            r => new ReceiptCorrectionView(
                r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.GetDecimal(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8),
                r.NullableString(9), r.NullableString(10), r.NullableGuid(11), r.GetInt64(12)),
            cancellationToken,
            ("c", context.CompanyId),
            ("plant", plantId),
            ("gr", goodsReceiptId),
            ("status", documentStatus),
            ("limit", limit),
            ("offset", offset));
}
