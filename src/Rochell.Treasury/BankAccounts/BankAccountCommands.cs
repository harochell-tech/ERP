using Rochell.Platform.Commands;

namespace Rochell.Treasury.BankAccounts;

/// <summary>
/// A company bank account (E-VS2-1: Controller, step-up). <paramref name="GlAccountCode"/> names its own control account in the
/// chart of accounts, used by no other bank account and by no role map (E-VS2-01-1, E-VS2-02-2). DOP only in VS#2.
/// </summary>
public sealed record RegisterBankAccount(Guid CompanyId, Guid SessionId, string IdempotencyKey, string BankCode, string AccountNumber, string GlAccountCode) : ICommand;

/// <summary>ACTIVE → CLOSED with a reason; refused while payments or statement lines of the account are open (E-VS2-02-3).</summary>
public sealed record CloseBankAccount(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid BankAccountId, long ExpectedVersion, string Reason) : ICommand;
