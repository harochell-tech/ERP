using Rochell.Platform.Commands;

namespace Rochell.Procurement.GoodsReceipts;

/// <summary>One received line: quantity in the PO line UOM (E-PR08-4) and the supplier's lot number, if any.</summary>
public sealed record GoodsReceiptLineInput(Guid PurchaseOrderLineId, decimal Quantity, string? SupplierLotNumber = null);

/// <summary>
/// T-02 PostGoodsReceipt (§11.2, C-01): receives material of an APPROVED / PARTIALLY_RECEIVED order into a location of its plant.
/// <paramref name="OccurredAt"/> is the physical time of the receipt (weighing); the business date is its Dominican calendar date.
/// </summary>
public sealed record PostGoodsReceipt(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid PlantId,
    Guid PurchaseOrderId,
    Guid LocationId,
    DateTime OccurredAt,
    IReadOnlyList<GoodsReceiptLineInput> Lines,
    string? WeighTicketRef = null) : IPlantScopedCommand;
