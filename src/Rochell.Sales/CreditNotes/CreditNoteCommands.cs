using Rochell.Platform.Commands;

namespace Rochell.Sales.CreditNotes;

/// <summary>One line of a credit note: the invoice line and the net amount credited on it (E-VS3-06-1).</summary>
public sealed record CreditNoteLineInput(Guid InvoiceLineId, decimal NetAmount);

/// <summary>
/// E-VS3-06-1/2/5: a DRAFT credit note NC-… on a fiscalized invoice, net per invoice line (never above what the line still
/// has to credit), ITBIS at the rate of the invoice's determination, with a reason category and text.
/// </summary>
public sealed record CreateCreditNote(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid InvoiceId, string ReasonCategory, string Reason, IReadOnlyList<CreditNoteLineInput> Lines) : ICommand;

/// <summary>
/// E-VS3-06-3/4/6/7: CONFIRMED · POSTED (P-22) · PENDING_EXTERNAL; the invoice's receivable goes down; issued by someone other
/// than who issued the invoice, with step-up; the invoice becomes CREDITED once fully credited.
/// </summary>
public sealed record IssueCreditNote(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid CreditNoteId, long ExpectedVersion) : ICommand;

/// <summary>E-VS3-06-8: the e-CF type 34 issued in the provider's portal, checked against the note like an invoice's.</summary>
public sealed record RecordExternalCreditNoteDocument(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid CreditNoteId, long ExpectedVersion, string Encf, DateTime IssuedAt, string SecurityCode, string EvidenceRef,
    string EvidenceSha256, string? ReceiverRnc, decimal NetTotal, decimal TaxTotal, decimal Total, string? ReceiverPassport = null) : ICommand;
