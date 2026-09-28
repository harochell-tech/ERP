using Rochell.Platform.Commands;

namespace Rochell.Sales.Receipts;

/// <summary>
/// E-VS3-07-2: a receipt REC-… from an ACTIVE customer. TRANSFER names our bank account and its value date (≤ today); CHEQUE carries
/// bank, number and date (≤ today); CASH nothing else. P-23 debits the bank (transfer) or cash in transit (cheque, cash).
/// </summary>
public sealed record RecordReceipt(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PartyId, string Method, decimal Amount, DateOnly? ValueDate = null, Guid? BankAccountId = null,
    string? Reference = null, string? ChequeBank = null, string? ChequeNo = null, DateOnly? ChequeDate = null) : ICommand;

/// <summary>E-VS3-07-3: a deposit slip DEP-… of cheque and cash receipts in transit to one bank account (P-29).</summary>
public sealed record DepositReceipts(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid BankAccountId, IReadOnlyList<Guid> ReceiptIds) : ICommand;

public sealed record ReceiptApplicationInput(Guid InvoiceId, decimal Amount);

/// <summary>E-VS3-07-5: applies a receipt to open invoices of its customer (P-25).</summary>
public sealed record ApplyReceipt(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ReceiptId, long ExpectedVersion, IReadOnlyList<ReceiptApplicationInput> Applications) : ICommand;

/// <summary>E-VS3-07-6: undoes one whole application (its ReceiptApplied event) with the exact reversal of its P-25.</summary>
public sealed record UnapplyReceipt(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ReceiptId, Guid ApplicationEventId, string Reason) : ICommand;

/// <summary>E-VS3-07-8: a deposited cheque the bank returned: its applications are undone and P-24 takes it out of the bank.</summary>
public sealed record MarkReceiptBounced(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ReceiptId, long ExpectedVersion, string Reason) : ICommand;

/// <summary>E-VS3-07-9: a receipt recorded by mistake, with nothing applied and not deposited or matched: exact reversal of P-23.</summary>
public sealed record ReverseReceipt(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ReceiptId, long ExpectedVersion, string Reason) : ICommand;

/// <summary>E-VS3-07-7: a withholding the customer made on an invoice, from its certificate (P-27).</summary>
public sealed record RecordCustomerWithholding(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid InvoiceId, string Kind, decimal Amount, DateOnly WithholdingDate, string CertificateNo, string EvidenceRef,
    string EvidenceSha256) : ICommand;

/// <summary>E-VS3-07-7: the Controller reverses a withholding recorded by mistake (exact reversal of P-27).</summary>
public sealed record ReverseCustomerWithholding(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid WithholdingId, long ExpectedVersion, string Reason) : ICommand;
