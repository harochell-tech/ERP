namespace Rochell.Tax;

public static class TaxErrors
{
    public const string AuthorizationOrderHasFreight = "AUTHORIZATION_ORDER_HAS_FREIGHT";
    public const string FiscalRuleInvalid = "FISCAL_RULE_INVALID";
    public const string RuleKindMismatch = "FISCAL_RULE_KIND_MISMATCH";
    public const string SourceInvalid = "FISCAL_SOURCE_INVALID";
    public const string SourceNotFound = "FISCAL_SOURCE_NOT_FOUND";
    public const string SourceNotEffective = "FISCAL_SOURCE_NOT_EFFECTIVE";
    public const string VersionNotFound = "FISCAL_RULE_VERSION_NOT_FOUND";
    public const string VersionNotConfigurable = "FISCAL_RULE_VERSION_NOT_CONFIGURABLE";
    public const string VersionNotReady = "FISCAL_RULE_VERSION_NOT_READY";
    public const string ActivatorIsConfigurer = "ACTIVATOR_IS_CONFIGURER";
    public const string AnotherItbisRuleActive = "ANOTHER_ITBIS_RULE_ACTIVE";
    public const string ProductionSourceRequired = "FISCAL_PRODUCTION_SOURCE_REQUIRED";
    public const string CasesRequired = "TEST_CASES_REQUIRED";

    /// <summary>E-FIS2-01-9: a report classification has no tax to compute, so it has no test runs.</summary>
    public const string TestsNotApplicable = "FISCAL_RULE_TESTS_NOT_APPLICABLE";
    public const string FiscalGateClosed = "FISCAL_GATE_CLOSED";
    public const string SubjectInvalid = "TAX_SUBJECT_INVALID";

    // E-FIS1-02: fiscal authorizations (CONFOTUR).
    public const string AuthorizationNotFound = "AUTHORIZATION_NOT_FOUND";
    public const string AuthorizationInvalidState = "AUTHORIZATION_INVALID_STATE";
    public const string AuthorizationVersionConflict = "AUTHORIZATION_VERSION_CONFLICT";
    public const string AuthorizationExpired = "AUTHORIZATION_EXPIRED";
    public const string AuthorizationFieldInvalid = "AUTHORIZATION_FIELD_INVALID";
    public const string AuthorizationLinesRequired = "AUTHORIZATION_LINES_REQUIRED";
    public const string AuthorizationProformaInvalid = "AUTHORIZATION_PROFORMA_INVALID";
    public const string AuthorizationCertificateRequired = "AUTHORIZATION_CERTIFICATE_REQUIRED";
    public const string AuthorizationCertificateDuplicate = "AUTHORIZATION_CERTIFICATE_DUPLICATE";
    public const string AuthorizationCustomerInvalid = "AUTHORIZATION_CUSTOMER_INVALID";
    public const string AuthorizationItemInvalid = "AUTHORIZATION_ITEM_INVALID";
    public const string AuthorizationSamePerson = "AUTHORIZATION_SAME_PERSON";
    public const string AuthorizationReasonRequired = "AUTHORIZATION_REASON_REQUIRED";
    public const string AuthorizationNotCovered = "AUTHORIZATION_NOT_COVERED";
    public const string AuthorizationExceeded = "AUTHORIZATION_EXCEEDED";
}

public static class FiscalRuleKinds
{
    public const string PurchaseItbis = "PURCHASE_ITBIS";
    public const string PurchaseWithholding = "PURCHASE_WITHHOLDING";

    /// <summary>E-VS3-05-1: output ITBIS of sales invoices.</summary>
    public const string SalesItbis = "SALES_ITBIS";

    /// <summary>E-FIS2-01-1: the 606 goods-and-services classification; read by the reports, never applied by the Tax Engine.</summary>
    public const string Report606Classification = "REPORT_606_CLASSIFICATION";

    /// <summary>
    /// E-CF1-3, E-CF1-01-6: the total from which a sale to the final consumer must identify its buyer; read by the cash sale, never
    /// applied by the Tax Engine.
    /// </summary>
    public const string ConsumerIdThreshold = "CONSUMER_ID_THRESHOLD";

    /// <summary>
    /// E-GAS-3, E-GAS-02-1/2: the tax type of an expense line — its label and its components (ITBIS, selective tax, other taxes,
    /// legal tip). Several are in force at once, one per rule code; a line gets only the components of its own type.
    /// </summary>
    public const string PurchaseTaxType = "PURCHASE_TAX_TYPE";

    public static bool IsSales(string kind) => kind == SalesItbis;

    /// <summary>
    /// E-FIS2-01-3: a rule kind that computes no tax — the reports or a command read it; it has no test runs, is READY with its
    /// official source, and neither applies in a determination nor closes the fiscal gate.
    /// </summary>
    public static bool IsReport(string kind) => kind is Report606Classification or ConsumerIdThreshold;
}

public static class TaxEffects
{
    public const string RecoverableInput = "RECOVERABLE_INPUT";
    public const string NonRecoverableInput = "NON_RECOVERABLE_INPUT";
    public const string Withholding = "WITHHOLDING";

    /// <summary>E-VS3-05-1: ITBIS charged on a sale (payable).</summary>
    public const string Output = "OUTPUT";

    /// <summary>E-GAS-10: taxes of an expense purchase that are not credited — each goes to its own expense account and 606 column.</summary>
    public const string SelectiveTax = "SELECTIVE_TAX";
    public const string OtherTax = "OTHER_TAX";
    public const string LegalTip = "LEGAL_TIP";
}

/// <summary>
/// E-GAS-01-7, E-GAS-02-5: what a purchase line is, for the rules that depend on it — a registered raw material, or an expense line
/// whose category is a service or a good.
/// </summary>
public static class TaxLineScopes
{
    public const string Inventory = "INVENTORY";
    public const string ExpenseService = "EXPENSE_SERVICE";
    public const string ExpenseGoods = "EXPENSE_GOODS";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Inventory, ExpenseService, ExpenseGoods };
}

public static class FiscalRuleStatus
{
    public const string BlockedPendingSource = "BLOCKED_PENDING_SOURCE";
    public const string Ready = "READY";
    public const string Active = "ACTIVE";
    public const string Retired = "RETIRED";
}

/// <summary>E-PR12-6: the supplier's taxpayer type, derived from its fiscal identifier.</summary>
public static class PartyTaxTypes
{
    public const string Company = "COMPANY";
    public const string Individual = "INDIVIDUAL";
    public const string Foreign = "FOREIGN";

    /// <summary>RNC of 9 digits = legal entity; cédula of 11 digits = individual; no identifier = foreign.</summary>
    public static string FromIdentifier(string? rnc) => rnc?.Length switch
    {
        null => Foreign,
        11 => Individual,
        _ => Company,
    };
}
