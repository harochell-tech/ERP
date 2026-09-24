using Rochell.Platform.Commands;

namespace Rochell.Procurement.SupplierInvoices;

/// <summary>
/// One invoice line: the PO line it bills, the quantity (PO line UOM) and the supplier's unit price.
/// <paramref name="LineKind"/> is always INVENTORY_PO in VS#1 (E-10); anything else is rejected (SI-01).
/// </summary>
public sealed record SupplierInvoiceLineInput(Guid PurchaseOrderLineId, decimal Quantity, decimal UnitPrice, string LineKind = SupplierInvoiceLineKinds.InventoryPo);

/// <summary>T-06 (§11.4): registers a DRAFT invoice of an ACTIVE supplier with its fiscal number (E-PR13-4) and dates (E-PR13-5).</summary>
public sealed record RegisterSupplierInvoice(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid PartyId,
    string SupplierFiscalNumber,
    DateOnly DocDate,
    DateOnly DueDate,
    IReadOnlyList<SupplierInvoiceLineInput> Lines) : ICommand;

/// <summary>T-07: three-way match of every line against received-not-invoiced quantity and PO price (E-PR13-1/2). Also re-match.</summary>
public sealed record MatchSupplierInvoice(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SupplierInvoiceId, long ExpectedVersion) : ICommand;

/// <summary>Approves a price/amount match exception (approver ≠ registrar, step-up). Quantity excess is never approvable (E-PR13-1).</summary>
public sealed record ApproveMatchException(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SupplierInvoiceId, long ExpectedVersion, string Reason) : ICommand;

/// <summary>Voids an unposted invoice with a reason; its fiscal number becomes free again (SI-04).</summary>
public sealed record VoidSupplierInvoice(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SupplierInvoiceId, long ExpectedVersion, string Reason) : ICommand;

public static class SupplierInvoiceLineKinds
{
    public const string InventoryPo = "INVENTORY_PO";
}

public static class SupplierInvoiceStatus
{
    public const string Draft = "DRAFT";
    public const string MatchException = "MATCH_EXCEPTION";
    public const string Matched = "MATCHED";
    public const string Voided = "VOIDED";
    public const string Reversed = "REVERSED";
}
