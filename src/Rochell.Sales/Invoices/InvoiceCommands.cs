using Rochell.Platform.Commands;

namespace Rochell.Sales.Invoices;

/// <summary>
/// E-VS3-05-3: a DRAFT invoice FA-… of one customer from delivered, not yet invoiced delivery lines (each for all it still owes,
/// at the order price, net of ITBIS).
/// </summary>
public sealed record CreateInvoiceFromDeliveries(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PartyId, IReadOnlyList<Guid> DeliveryLineIds) : ICommand;

/// <summary>
/// E-VS3-05-2/4/5/8: C-11 — commercial CONFIRMED, accounting POSTED (P-18) and fiscal PENDING_EXTERNAL in one transaction, with
/// the sales ITBIS of the invoice date, the AR document and the invoiced quantities. <paramref name="EcfType"/> overrides the
/// default (31 for an RNC, 32 for a cédula).
/// </summary>
public sealed record IssueInvoice(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid InvoiceId, long ExpectedVersion, string? EcfType = null) : ICommand;

/// <summary>
/// E-VS3-05-10 (SAL-07): the e-CF issued in the provider's portal, recorded against the invoice; any difference in receiver or
/// totals refuses it and the invoice stays PENDING_EXTERNAL.
/// </summary>
public sealed record RecordExternalFiscalDocument(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid InvoiceId, long ExpectedVersion, string Encf, DateTime IssuedAt, string SecurityCode, string EvidenceRef,
    string EvidenceSha256, string ReceiverRnc, decimal NetTotal, decimal TaxTotal, decimal Total) : ICommand;

/// <summary>E-VS3-05-11: an issued invoice never fiscalized and without receipts is voided (P-18 reversed), with a reason and step-up.</summary>
public sealed record VoidUnfiscalizedInvoice(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid InvoiceId, long ExpectedVersion, string Reason) : ICommand;
