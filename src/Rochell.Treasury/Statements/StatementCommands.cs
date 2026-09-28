using Rochell.Platform.Commands;

namespace Rochell.Treasury.Statements;

/// <summary>
/// The treasurer imports a bank's CSV statement (E-VS2-4) for one company bank account: the file travels base64 (≤ 5 MB) and is
/// kept (E-VS2-05-2), read with the bank's format (E-VS2-05-1). Period and balances come from the file when the format has them,
/// else from these fields; opening + credits − debits must equal closing over all the file's lines (E-VS2-05-3). The same file
/// again is refused; an overlapping one adds only its new lines and reports the duplicates (E-VS2-05-4, IDM-04). One bad row
/// rejects the whole file (E-VS2-05-9); a new line in a period whose BANK-REC is closed too (E-VS2-05-5).
/// </summary>
public sealed record ImportBankStatement(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid BankAccountId,
    string FileName,
    string ContentBase64,
    DateOnly? PeriodFrom = null,
    DateOnly? PeriodTo = null,
    decimal? OpeningBalance = null,
    decimal? ClosingBalance = null) : ICommand;

/// <summary>
/// A person confirms that an UNMATCHED DEBIT line is a RELEASED payment of the same bank account: exact amount, line dated within
/// [value date, value date + 10 days] (E-VS2-05-6). The line becomes MATCHED and the payment CLEARED (E-VS2-05-8). A CREDIT line is
/// matched only as the bank's return of a REVERSED payment whose DEBIT line is matched: same account and amount, dated on or after
/// the reversal; the payment keeps its status (E-VS2-05-10).
/// </summary>
public sealed record MatchBankLine(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid LineId,
    long ExpectedLineVersion,
    Guid PaymentId,
    long ExpectedPaymentVersion) : ICommand;

/// <summary>
/// E-VS3-07-10: MatchBankLine for customer receipts. Exactly one of <paramref name="ReceiptId"/> / <paramref name="DepositId"/>, with its
/// version. A CREDIT line is a TRANSFER receipt of the same account and amount dated within ±10 days of its value date, or a deposit
/// slip of the same account and total dated within [deposit date, +10 days]; both become MATCHED. A DEBIT line is the bank taking back a
/// BOUNCED cheque of that account and amount, dated on or after its deposit.
/// </summary>
public sealed record MatchBankLineToReceipt(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid LineId,
    long ExpectedLineVersion,
    Guid? ReceiptId,
    Guid? DepositId,
    long ExpectedVersion) : ICommand;

/// <summary>
/// MATCHED → UNMATCHED with step-up and a reason; the payment goes back from CLEARED to RELEASED (E-VS2-01-11, E-VS2-05-8). The
/// DEBIT line of a reversed payment stays matched (E-VS2-04-1); its return (CREDIT) can be unmatched, the payment unchanged. A receipt's
/// or deposit's line goes back too, and the receipt or deposit (with its receipts) returns to DEPOSITED; a bounce line only unmatches.
/// </summary>
public sealed record UnmatchBankLine(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid LineId, long ExpectedVersion, string Reason) : ICommand;

/// <summary>
/// An UNMATCHED DEBIT line is a bank charge: its full amount is posted with R-10 at the line's value date (late entry if BANK-REC
/// is closed) and the line becomes CHARGE_RECOGNIZED (E-VS2-05-7).
/// </summary>
public sealed record RecognizeBankCharge(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid LineId, long ExpectedVersion) : ICommand;
