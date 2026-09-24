namespace Rochell.Tax;

public static class TaxErrors
{
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
    public const string FiscalGateClosed = "FISCAL_GATE_CLOSED";
    public const string SubjectInvalid = "TAX_SUBJECT_INVALID";
}

public static class FiscalRuleKinds
{
    public const string PurchaseItbis = "PURCHASE_ITBIS";
    public const string PurchaseWithholding = "PURCHASE_WITHHOLDING";
}

public static class TaxEffects
{
    public const string RecoverableInput = "RECOVERABLE_INPUT";
    public const string NonRecoverableInput = "NON_RECOVERABLE_INPUT";
    public const string Withholding = "WITHHOLDING";
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
