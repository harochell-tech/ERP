using Rochell.Platform.Commands;

namespace Rochell.Procurement.PurchaseOrders;

/// <summary>One order line: quantity and unit price in the line UOM (E-PR08-4).</summary>
public sealed record PurchaseOrderLineInput(Guid ItemId, string Uom, decimal Quantity, decimal UnitPrice);

/// <summary>§11.1: creates a DRAFT order for an ACTIVE supplier with ACTIVE items.</summary>
public sealed record CreatePurchaseOrder(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid PlantId,
    Guid PartyId,
    DateOnly OrderDate,
    IReadOnlyList<PurchaseOrderLineInput> Lines) : IPlantScopedCommand;

/// <summary>E-PR08-5: replaces the lines of a DRAFT order.</summary>
public sealed record UpdatePurchaseOrderDraft(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid PlantId,
    Guid PurchaseOrderId,
    long ExpectedVersion,
    IReadOnlyList<PurchaseOrderLineInput> Lines) : IPlantScopedCommand;

public sealed record SubmitPurchaseOrder(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid PurchaseOrderId, long ExpectedVersion) : IPlantScopedCommand;

public sealed record ApprovePurchaseOrder(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid PurchaseOrderId, long ExpectedVersion) : IPlantScopedCommand;

public sealed record RejectPurchaseOrder(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid PurchaseOrderId, long ExpectedVersion, string Reason) : IPlantScopedCommand;

public sealed record CancelPurchaseOrder(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid PurchaseOrderId, long ExpectedVersion, string Reason) : IPlantScopedCommand;

/// <summary>Authorizes receiving <paramref name="AdditionalQuantity"/> above tolerance on one line (K-13).</summary>
public sealed record ApproveOverReceipt(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid PlantId,
    Guid PurchaseOrderId,
    Guid PurchaseOrderLineId,
    decimal AdditionalQuantity,
    string Reason) : IPlantScopedCommand;
