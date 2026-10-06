using Rochell.Platform.Commands;

namespace Rochell.Treasury.BankAccounts;

/// <summary>
/// A company bank account (E-VS2-1: Controller, step-up). <paramref name="GlAccountCode"/> names its own control account in the
/// chart of accounts, used by no other bank account and by no role map (E-VS2-01-1, E-VS2-02-2). <paramref name="Currency"/> is DOP or
/// USD (E-USD1-05-1); a USD account's statements are in USD.
/// </summary>
public sealed record RegisterBankAccount(Guid CompanyId, Guid SessionId, string IdempotencyKey, string BankCode, string AccountNumber, string GlAccountCode, string Currency = "DOP") : ICommand;

/// <summary>ACTIVE → CLOSED with a reason; refused while payments or statement lines of the account are open (E-VS2-02-3).</summary>
public sealed record CloseBankAccount(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid BankAccountId, long ExpectedVersion, string Reason) : ICommand;

/// <summary>
/// E-UX4-6: gives a bank account a short name (1–60 characters, trimmed) shown as "alias · banco ••••6789"; null or blank removes it.
/// Changes nothing else; any status.
/// </summary>
public sealed record SetBankAccountAlias(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid BankAccountId, long ExpectedVersion, string? Alias) : ICommand;
