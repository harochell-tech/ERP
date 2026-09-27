namespace Rochell.Treasury;

public static class TreasuryErrors
{
    public const string GlAccountNotFound = "GL_ACCOUNT_NOT_FOUND";
    public const string GlAccountNotEligible = "GL_ACCOUNT_NOT_ELIGIBLE";
    public const string BankAccountDuplicate = "BANK_ACCOUNT_DUPLICATE";
    public const string BankAccountNotFound = "NOT_FOUND";
    public const string BankAccountNotActive = "BANK_ACCOUNT_NOT_ACTIVE";
    public const string BankAccountHasOpenItems = "BANK_ACCOUNT_HAS_OPEN_ITEMS";
    public const string VersionConflict = "VERSION_CONFLICT";
    public const string ReasonRequired = "REASON_REQUIRED";
}
