using Rochell.Platform.Commands;

namespace Rochell.Sales.Invoices;

/// <summary>
/// E-VS3-05-3: a DRAFT invoice FA-… of one customer from delivered, not yet invoiced delivery lines (each for all it still owes,
/// at the order price, net of ITBIS). E-FIS1-03-1: with <paramref name="FiscalAuthorizationId"/> it is an exempt e-CF 44, every line
/// covered by that ACTIVE authorization of the customer.
/// </summary>
public sealed record CreateInvoiceFromDeliveries(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PartyId, IReadOnlyList<Guid> DeliveryLineIds, Guid? FiscalAuthorizationId = null)
    : ICommand;

/// <summary>
/// E-FIS1b-6, E-FIS1b-01-6: a DRAFT invoice from whole OPEN proformas of one customer — e-CF 44 with the authorization that cites
/// them, 31 / 32 with ITBIS without it. When issued it inherits what receipts were allocated to those proformas (E-FIS1b-01-7).
/// </summary>
public sealed record CreateInvoiceFromProformas(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PartyId, IReadOnlyList<Guid> ProformaIds, Guid? FiscalAuthorizationId = null) : ICommand;

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
    string EvidenceSha256, string? ReceiverRnc, decimal NetTotal, decimal TaxTotal, decimal Total, string? ReceiverPassport = null) : ICommand;

/// <summary>E-VS3-05-11: an issued invoice never fiscalized and without receipts is voided (P-18 reversed), with a reason and step-up.</summary>
public sealed record VoidUnfiscalizedInvoice(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid InvoiceId, long ExpectedVersion, string Reason) : ICommand;
