namespace Rochell.Finance;

public static class FinanceErrors
{
    /// <summary>Patch 1 P-1: a posting requirement is missing; the whole command rolls back.</summary>
    public const string PostingPrerequisiteMissing = "POSTING_PREREQUISITE_MISSING";
    public const string PostingUnbalanced = "POSTING_UNBALANCED";
    public const string AlreadyReversed = "ALREADY_REVERSED";
    public const string JournalNotFound = "JOURNAL_NOT_FOUND";
    public const string ConfigurationNotFound = "CONFIGURATION_NOT_FOUND";
    public const string ConfigurationNotDraft = "CONFIGURATION_NOT_DRAFT";
    public const string FourEyes = "FOUR_EYES_REQUIRED";
    public const string Overlap = "EFFECTIVE_RANGE_OVERLAP";
    public const string RuleDefinitionInvalid = "RULE_DEFINITION_INVALID";
}
