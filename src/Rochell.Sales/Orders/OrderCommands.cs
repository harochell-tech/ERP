using Rochell.Platform.Commands;

namespace Rochell.Sales.Orders;

public static class DeliveryTerms
{
    public const string PickupAtPlant = "PICKUP_AT_PLANT";
    public const string DeliveredOwnTransport = "DELIVERED_OWN_TRANSPORT";
}

/// <summary>A line of a sales order: finished good, unit of the price list and quantity (the price comes from the list, E-VS3-03-5).</summary>
public sealed record SalesOrderLineInput(Guid ItemId, string Uom, decimal Quantity);

/// <summary>E-VS3-03-5/6: the Vendedor creates a DRAFT order PV-… priced from the price list in force (net of ITBIS).</summary>
public sealed record CreateSalesOrder(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PartyId, Guid PlantId, string DeliveryTermCode, string? SiteAddress, DateOnly? RequestedDate,
    string? CustomerPoRef, IReadOnlyList<SalesOrderLineInput> Lines) : ICommand;

/// <summary>Replaces a DRAFT order's header and lines (new lines version, repriced from the list in force).</summary>
public sealed record UpdateSalesOrderDraft(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SalesOrderId, long ExpectedVersion, Guid PlantId, string DeliveryTermCode, string? SiteAddress,
    DateOnly? RequestedDate, string? CustomerPoRef, IReadOnlyList<SalesOrderLineInput> Lines) : ICommand;

/// <summary>E-VS3-03-4: DRAFT → CONFIRMED (credit auto-approved) or PENDING_CREDIT (Crédito decides).</summary>
public sealed record SubmitForCredit(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SalesOrderId, long ExpectedVersion) : ICommand;

/// <summary>PENDING_CREDIT → CONFIRMED by Crédito (step-up).</summary>
public sealed record ApproveCredit(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SalesOrderId, long ExpectedVersion) : ICommand;

/// <summary>PENDING_CREDIT → DRAFT by Crédito, with a reason.</summary>
public sealed record RejectCredit(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SalesOrderId, long ExpectedVersion, string Reason) : ICommand;

/// <summary>DRAFT, PENDING_CREDIT or CONFIRMED without deliveries → CANCELLED, with a reason.</summary>
public sealed record CancelSalesOrder(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SalesOrderId, long ExpectedVersion, string Reason) : ICommand;
