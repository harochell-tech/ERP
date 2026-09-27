namespace Rochell.Treasury.Payments;

public static class PaymentErrors
{
    public const string NotFound = "NOT_FOUND";
    public const string VersionConflict = "VERSION_CONFLICT";
    public const string NotPrepared = "PAYMENT_NOT_PREPARED";
    public const string SupplierNotActive = "SUPPLIER_NOT_ACTIVE";
    public const string BankAccountNotActive = "BANK_ACCOUNT_NOT_ACTIVE";
    public const string PartyBankAccountNotVerified = "PARTY_BANK_ACCOUNT_NOT_VERIFIED";
    public const string PartyBankAccountNotPayable = "PARTY_BANK_ACCOUNT_NOT_PAYABLE";
    public const string ApplicationsRequired = "APPLICATIONS_REQUIRED";
    public const string ApplicationDuplicate = "APPLICATION_DUPLICATE";
    public const string ApplicationWrongSupplier = "APPLICATION_WRONG_SUPPLIER";
    public const string ApplicationExceedsOpenAmount = "APPLICATION_EXCEEDS_OPEN_AMOUNT";
    public const string AmountInvalid = "AMOUNT_INVALID";
    public const string ValueDateBeforeInvoice = "VALUE_DATE_BEFORE_INVOICE";
    public const string ValueDateInFuture = "VALUE_DATE_IN_FUTURE";
    public const string SamePerson = "SAME_PERSON";
    public const string ReasonRequired = "REASON_REQUIRED";
    public const string NotReversible = "PAYMENT_NOT_REVERSIBLE";
    public const string PostingMissing = "POSTING_PREREQUISITE_MISSING";
}
