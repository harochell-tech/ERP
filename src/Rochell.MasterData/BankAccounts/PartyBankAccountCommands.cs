using Rochell.Platform.Commands;

namespace Rochell.MasterData.BankAccounts;

/// <summary>
/// A new version of a supplier's bank account, in REVIEW (VS#2 §4, E-VS2-02-4: ACTIVE suppliers, one REVIEW at a time). A foreign supplier's
/// <paramref name="BankCode"/> is its SWIFT/BIC or bank name and <paramref name="AccountNumber"/> may be an IBAN (E-USD1-05-6).
/// </summary>
public sealed record RequestPartyBankAccount(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid PartyId,
    string BankCode,
    string AccountNumber,
    string AccountHolder) : ICommand;

/// <summary>
/// REVIEW → VERIFIED by someone other than the requester, with evidence of at least 20 characters (who was called, at which
/// number, when; E-VS2-01-4). Payable 72 calendar hours later (E-VS2-8); the version it replaces becomes SUPERSEDED.
/// </summary>
public sealed record VerifyPartyBankAccount(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PartyBankAccountId, string Evidence) : ICommand;

/// <summary>REVIEW → REJECTED with a reason, by someone other than the requester (E-VS2-01-10, E-VS2-02-5: no step-up).</summary>
public sealed record RejectPartyBankAccount(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PartyBankAccountId, string Reason) : ICommand;
