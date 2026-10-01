namespace Rochell.Treasury.Statements;

public static class StatementErrors
{
    public const string NotFound = "NOT_FOUND";
    public const string VersionConflict = "VERSION_CONFLICT";
    public const string BankAccountNotActive = "BANK_ACCOUNT_NOT_ACTIVE";
    public const string FormatMissing = "BANK_FORMAT_MISSING";
    public const string FileInvalid = "BANK_FILE_INVALID";
    public const string FileTooLarge = "BANK_FILE_TOO_LARGE";
    public const string AlreadyImported = "STATEMENT_ALREADY_IMPORTED";
    public const string FieldsRequired = "STATEMENT_FIELDS_REQUIRED";
    public const string FieldsDisagree = "STATEMENT_FIELDS_DISAGREE";
    public const string BalanceMismatch = "STATEMENT_BALANCE_MISMATCH";
    public const string LineOutsidePeriod = "LINE_OUTSIDE_PERIOD";
    public const string PeriodClosed = "PERIOD_CLOSED";
    public const string LineNotUnmatched = "LINE_NOT_UNMATCHED";
    public const string LineNotMatched = "LINE_NOT_MATCHED";
    public const string LineNotDebit = "LINE_NOT_DEBIT";
    public const string PaymentNotReleased = "PAYMENT_NOT_RELEASED";
    public const string PaymentReversed = "PAYMENT_REVERSED";
    public const string PaymentNotReversed = "PAYMENT_NOT_REVERSED";
    public const string ReturnWithoutTransfer = "RETURN_WITHOUT_TRANSFER";
    public const string ReturnAlreadyMatched = "RETURN_ALREADY_MATCHED";
    public const string WrongBankAccount = "MATCH_WRONG_BANK_ACCOUNT";
    public const string AmountDiffers = "MATCH_AMOUNT_DIFFERS";
    public const string DateOutsideWindow = "MATCH_DATE_OUTSIDE_WINDOW";
    public const string ReasonRequired = "REASON_REQUIRED";
    public const string AmountInvalid = "AMOUNT_INVALID";
    public const string ReceiptNotMatchable = "RECEIPT_NOT_MATCHABLE";
    public const string MatchTargetRequired = "MATCH_TARGET_REQUIRED";
    public const string RefundNotMatchable = "REFUND_NOT_MATCHABLE";
}
