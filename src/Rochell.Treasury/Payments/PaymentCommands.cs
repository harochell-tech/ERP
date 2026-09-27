using Rochell.Platform.Commands;

namespace Rochell.Treasury.Payments;

/// <summary>One invoice a payment pays, fully or partly (E-VS2-9); at most one per invoice per payment (E-VS2-03-1).</summary>
public sealed record PaymentApplication(Guid ApDocId, decimal Amount);

/// <summary>
/// A transfer to one supplier (E-VS2-2, E-VS2-9), PREPARED by the treasurer: no journal and no reservation of invoice balances
/// (E-VS2-6). Its amount is the sum of the applications; the supplier account must be VERIFIED (payable or not yet, E-VS2-03-8);
/// the value date is not before the latest invoice applied (E-VS2-03-4). Amounts have at most 2 decimals (E-VS2-03-5).
/// </summary>
public sealed record PrepareSupplierPayment(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid PartyId,
    Guid BankAccountId,
    Guid PartyBankAccountId,
    DateOnly ValueDate,
    string? BankReference,
    IReadOnlyList<PaymentApplication> Applications) : ICommand;

/// <summary>Replaces a PREPARED payment's bank accounts, value date, reference and whole set of applications (E-VS2-03-6).</summary>
public sealed record UpdatePreparedPayment(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid PaymentId,
    long ExpectedVersion,
    Guid BankAccountId,
    Guid PartyBankAccountId,
    DateOnly ValueDate,
    string? BankReference,
    IReadOnlyList<PaymentApplication> Applications) : ICommand;

/// <summary>PREPARED → VOIDED with a reason (E-VS2-03-6).</summary>
public sealed record VoidPayment(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PaymentId, long ExpectedVersion, string Reason) : ICommand;

/// <summary>
/// PREPARED → RELEASED by someone other than the preparer, with step-up (VS#2 §4, E-VS2-5): re-validates every application
/// against the invoice's open amount under lock (PAY-05), requires the supplier account payable now (PAY-06, PAY-07) and a value
/// date not in the future (E-VS2-03-4), applies the payment and posts R-09 at the value date (E-VS2-7, late entry if BANK-REC or
/// AP-REC is closed, E-VS2-03-3).
/// </summary>
public sealed record ReleaseSupplierPayment(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PaymentId, long ExpectedVersion) : ICommand;
