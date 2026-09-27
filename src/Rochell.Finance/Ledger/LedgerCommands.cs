using Rochell.Platform.Commands;

namespace Rochell.Finance.Ledger;

/// <summary>E-FIN1-3/4: the Controller creates an account with its class (and control flag; a control account is moved only by its documents).</summary>
public sealed record CreateAccount(Guid CompanyId, Guid SessionId, string IdempotencyKey, string Code, string Name, string AccountClass, bool IsControl) : ICommand;

/// <summary>Renames an account or sets its class; code and control flag never change.</summary>
public sealed record UpdateAccount(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid AccountId, string Name, string AccountClass) : ICommand;

/// <summary>ACTIVE → INACTIVE, refused while the account has a balance (E-FIN1-7); INACTIVE → ACTIVE with <see cref="ActivateAccount"/>.</summary>
public sealed record DeactivateAccount(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid AccountId) : ICommand;

public sealed record ActivateAccount(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid AccountId) : ICommand;

/// <summary>One line of an adjustment: exactly one of debit or credit, 2 decimals, optional plant and party (E-FIN1-01-4).</summary>
public sealed record ManualJournalLine(Guid AccountId, decimal Debit, decimal Credit, Guid? PlantId = null, Guid? PartyId = null, string? Memo = null);

/// <summary>
/// E-FIN1-1/2/6/7/8: the Contador prepares an adjustment (DRAFT, number AJ-…) to active non-control accounts, with its support
/// (reference and SHA-256 as 64 hex characters), close component (ACR-NTX or ACR-TAX) and whether it reverses on the first day of
/// the next month.
/// </summary>
public sealed record PrepareManualJournal(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    DateOnly PostingDate,
    string Description,
    string SupportRef,
    string SupportSha256,
    string CloseComponent,
    bool AutoReverse,
    IReadOnlyList<ManualJournalLine> Lines) : ICommand;

/// <summary>Replaces a DRAFT adjustment's header and all its lines (a new version).</summary>
public sealed record UpdateManualJournal(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid ManualJournalId,
    long ExpectedVersion,
    DateOnly PostingDate,
    string Description,
    string SupportRef,
    string SupportSha256,
    string CloseComponent,
    bool AutoReverse,
    IReadOnlyList<ManualJournalLine> Lines) : ICommand;

/// <summary>DRAFT → PENDING_APPROVAL: balanced, at least two lines, its period's component open.</summary>
public sealed record SubmitManualJournal(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ManualJournalId, long ExpectedVersion) : ICommand;

/// <summary>PENDING_APPROVAL → DRAFT, for the Contador to correct it.</summary>
public sealed record WithdrawManualJournal(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ManualJournalId, long ExpectedVersion) : ICommand;

/// <summary>
/// PENDING_APPROVAL → POSTED by the Controller (not the preparer), with step-up: the MANUAL_ADJUSTMENT journal (P-34) and, when
/// auto-reversing, its exact reversal on the first day of the next month, in the same transaction (E-FIN1-01-6).
/// </summary>
public sealed record ApproveManualJournal(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ManualJournalId, long ExpectedVersion) : ICommand;

/// <summary>PENDING_APPROVAL → REJECTED with a reason (not by the preparer).</summary>
public sealed record RejectManualJournal(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ManualJournalId, long ExpectedVersion, string Reason) : ICommand;

/// <summary>POSTED → REVERSED (not auto-reversing ones): exact reversal on today's business date, with a reason and step-up.</summary>
public sealed record ReverseManualJournal(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ManualJournalId, long ExpectedVersion, string Reason) : ICommand;

/// <summary>One line of a report structure: its accounts (each once per structure) and the sign that presents Σ(debit − credit).</summary>
public sealed record ReportLineInput(string LineCode, string Caption, string? ParentLineCode, int Sign, int OrderNo, IReadOnlyList<Guid> AccountIds);

/// <summary>
/// E-FIN1-5, E-FIN1-03-1: the Controller prepares a new DRAFT version of the balance sheet (ASSET, LIABILITY, EQUITY accounts) or
/// the income statement (REVENUE, COST, EXPENSE accounts) with all its lines.
/// </summary>
public sealed record PrepareReportStructure(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, string Report, DateOnly EffectiveFrom, IReadOnlyList<ReportLineInput> Lines) : ICommand;

/// <summary>E-FIN1-03-2: DRAFT → ACTIVE (the previous ACTIVE → SUPERSEDED) by someone other than the preparer, with step-up, when
/// every active account of the report's classes is on a line.</summary>
public sealed record ApproveReportStructure(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid StructureVersionId) : ICommand;

public static class LedgerErrors
{
    public const string NotFound = "NOT_FOUND";
    public const string VersionConflict = "VERSION_CONFLICT";
    public const string InvalidState = "INVALID_STATE";
    public const string AccountDuplicate = "ACCOUNT_DUPLICATE";
    public const string AccountInvalid = "ACCOUNT_INVALID";
    public const string AccountHasBalance = "ACCOUNT_HAS_BALANCE";
    public const string AccountNotAllowed = "ACCOUNT_NOT_ALLOWED";
    public const string LinesInvalid = "LINES_INVALID";
    public const string Unbalanced = "JOURNAL_UNBALANCED";
    public const string SupportInvalid = "SUPPORT_INVALID";
    public const string DateInvalid = "DATE_INVALID";
    public const string ReasonRequired = "REASON_REQUIRED";
    public const string FourEyes = "FOUR_EYES_REQUIRED";
    public const string StructureInvalid = "STRUCTURE_INVALID";
    public const string StructureIncomplete = "STRUCTURE_INCOMPLETE";
    public const string StructureMissing = "STRUCTURE_MISSING";
    public const string AccountClassMissing = "ACCOUNT_CLASS_MISSING";
}
