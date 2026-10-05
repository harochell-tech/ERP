using Rochell.Platform.Commands;

namespace Rochell.Treasury.Payments;

/// <summary>One invoice a payment pays, fully or partly (E-VS2-9); at most one per invoice per payment (E-VS2-03-1).</summary>
public sealed record PaymentApplication(Guid ApDocId, decimal Amount);

/// <summary>
/// A transfer to one supplier (E-VS2-2, E-VS2-9), PREPARED by the treasurer: no journal and no reservation of invoice balances
/// (E-VS2-6). Its amount is the sum of the applications; the supplier account must be VERIFIED (payable or not yet, E-VS2-03-8);
/// the value date is not before the latest invoice applied (E-VS2-03-4). Amounts have at most 2 decimals (E-VS2-03-5).
/// USD invoices (E-USD1-05-2/3) are paid alone with amounts in USD: from a USD account at the approved rate of the value date, from a
/// peso account at <paramref name="ExchangeRate"/>, the rate the bank charged.
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
    IReadOnlyList<PaymentApplication> Applications,
    decimal? ExchangeRate = null) : ICommand;

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
    IReadOnlyList<PaymentApplication> Applications,
    decimal? ExchangeRate = null) : ICommand;

/// <summary>PREPARED → VOIDED with a reason (E-VS2-03-6).</summary>
public sealed record VoidPayment(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PaymentId, long ExpectedVersion, string Reason) : ICommand;

/// <summary>
/// PREPARED → RELEASED by someone other than the preparer, with step-up (VS#2 §4, E-VS2-5): re-validates every application
/// against the invoice's open amount under lock (PAY-05), requires the supplier account payable now (PAY-06, PAY-07) and a value
/// date not in the future (E-VS2-03-4), applies the payment and posts R-09 at the value date (E-VS2-7, late entry if BANK-REC or
/// AP-REC is closed, E-VS2-03-3).
/// </summary>
public sealed record ReleaseSupplierPayment(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PaymentId, long ExpectedVersion) : ICommand;

/// <summary>
/// RELEASED or CLEARED → REVERSED (VS#2 §4, PAY-08): the Controller, with step-up and a reason of at least 10 characters
/// (E-VS2-04-5), reverses the whole payment (E-VS2-04-3) on today's business date (E-VS2-04-2): one reversal row per live
/// application, invoice balances restored, and the exact reversal of the live R-09 journal (E-VS2-04-4). A CLEARED payment's
/// statement line stays matched to it (E-VS2-04-1).
/// </summary>
public sealed record ReversePayment(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PaymentId, long ExpectedVersion, string Reason) : ICommand;
