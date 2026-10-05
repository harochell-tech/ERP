using Rochell.Platform.Commands;

namespace Rochell.Procurement.SupplierInvoices;

/// <summary>
/// One invoice line: the PO line it bills, the quantity (PO line UOM) and the supplier's unit price.
/// <paramref name="LineKind"/> is always INVENTORY_PO in VS#1 (E-10); anything else is rejected (SI-01).
/// </summary>
public sealed record SupplierInvoiceLineInput(Guid PurchaseOrderLineId, decimal Quantity, decimal UnitPrice, string LineKind = SupplierInvoiceLineKinds.InventoryPo);

/// <summary>
/// T-06 (§11.4): registers a DRAFT invoice of an ACTIVE supplier with its fiscal number (E-PR13-4) and dates (E-PR13-5).
/// E-UX4-7: <paramref name="PrintedTotal"/> is the total printed on the supplier's document (ITBIS included), kept to compare with
/// the determined gross once the invoice is posted.
/// </summary>
public sealed record RegisterSupplierInvoice(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid PartyId,
    string SupplierFiscalNumber,
    DateOnly DocDate,
    DateOnly DueDate,
    IReadOnlyList<SupplierInvoiceLineInput> Lines,
    decimal? PrintedTotal = null) : ICommand;

/// <summary>
/// E-GAS-04-1: one line of an expense invoice — what was bought (free text), its expense category (the account and the 606 type),
/// its tax type (a PURCHASE_TAX_TYPE rule), the quantity and the unit price.
/// </summary>
/// <remarks>E-GAS-05-2: <paramref name="PurchaseOrderLineId"/> names the expense order line it bills; its category and tax type are the order line's.</remarks>
public sealed record ExpenseLineInput(string Description, Guid ExpenseCategoryId, Guid? TaxTypeId, decimal Quantity, decimal UnitPrice, Guid? PurchaseOrderLineId = null);

/// <summary>
/// E-GAS-04-1, E-GAS-6: registers a DRAFT expense invoice without a purchase order — electricity, telephone, tolls, repairs. It
/// goes through the same match (against the approval amount, E-GAS-04-2), exception approval, posting (P-37), void and reversal as
/// any supplier invoice. <paramref name="PlantId"/> is mandatory (E-GAS-01-8).
/// For a foreign supplier (E-USD1-03-3): <paramref name="SupplierFiscalNumber"/> is the supplier's own invoice number, prices are in USD,
/// lines carry no tax type, and the invoice takes the rate of its date (E-USD1-03-4).
/// </summary>
public sealed record RegisterExpenseInvoice(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid PartyId,
    string SupplierFiscalNumber,
    DateOnly DocDate,
    DateOnly DueDate,
    Guid PlantId,
    IReadOnlyList<ExpenseLineInput> Lines,
    decimal? PrintedTotal = null,
    Guid? PurchaseOrderId = null) : ICommand;

public static class SupplierInvoiceClasses
{
    public const string Inventory = "INVENTORY";
    public const string Expense = "EXPENSE";
}

/// <summary>T-07: three-way match of every line against received-not-invoiced quantity and PO price (E-PR13-1/2). Also re-match.</summary>
public sealed record MatchSupplierInvoice(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SupplierInvoiceId, long ExpectedVersion) : ICommand;

/// <summary>Approves a price/amount match exception (approver ≠ registrar, step-up). Quantity excess is never approvable (E-PR13-1).</summary>
public sealed record ApproveMatchException(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SupplierInvoiceId, long ExpectedVersion, string Reason) : ICommand;

/// <summary>Voids an unposted invoice with a reason; its fiscal number becomes free again (SI-04).</summary>
public sealed record VoidSupplierInvoice(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SupplierInvoiceId, long ExpectedVersion, string Reason) : ICommand;

public static class SupplierInvoiceLineKinds
{
    public const string InventoryPo = "INVENTORY_PO";

    /// <summary>E-GAS-1: a line of an expense invoice (description, category, tax type).</summary>
    public const string Expense = "EXPENSE";
}

public static class SupplierInvoiceStatus
{
    public const string Draft = "DRAFT";
    public const string MatchException = "MATCH_EXCEPTION";
    public const string Matched = "MATCHED";
    public const string Voided = "VOIDED";
    public const string Reversed = "REVERSED";
}

/// <summary>
/// T-09 (C-14): posts a MATCHED invoice — fiscal determination at the invoice date, R-04 (AP-REC) and R-05 (INV-MOV), the AP
/// document, invoiced quantities and BILLS links, in one transaction. Non-recoverable ITBIS records POSTING_BLOCKED (E-PR13-3).
/// </summary>
public sealed record PostSupplierInvoice(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SupplierInvoiceId, long ExpectedVersion) : ICommand;

/// <summary>T-10 (R-07): reverses a posted invoice whose AP document is fully open (Controller, step-up), on the reversal date (E-PR13b-3).</summary>
public sealed record ReverseSupplierInvoice(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SupplierInvoiceId, long ExpectedVersion, string Reason) : ICommand;
