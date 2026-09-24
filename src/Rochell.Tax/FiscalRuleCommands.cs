using Rochell.Platform.Commands;

namespace Rochell.Tax;

/// <summary>E-PR12-2: an official source, referenced by text and by the SHA-256 (64 hex characters) of the document consulted.</summary>
public sealed record RegisterFiscalSource(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    string OfficialSource,
    string DocumentTitle,
    string DocumentVersion,
    DateOnly PublicationDate,
    DateTime ConsultedAt,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    string UrlOrReference,
    string FileReference,
    string FileSha256) : ICommand;

/// <summary>Configures a new version of a rule (creating the rule on its first version). It starts BLOCKED_PENDING_SOURCE.</summary>
public sealed record ConfigureFiscalRuleVersion(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    string RuleCode,
    string RuleKind,
    string Definition,
    DateOnly EffectiveFrom) : ICommand;

public sealed record LinkFiscalSource(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid RuleVersionId, Guid SourceId) : ICommand;

/// <summary>One expected tax of a test case.</summary>
public sealed record ExpectedTax(string TaxCode, decimal Amount, string Effect);

/// <summary>
/// A regression case supplied by the fiscal team: the context and the taxes the version must produce.
/// <paramref name="ItbisAmount"/> feeds withholding rules whose base is ITBIS.
/// </summary>
public sealed record FiscalTestCase(string CaseId, string PartyType, string ItemCategory, decimal NetAmount, decimal ItbisAmount, IReadOnlyList<ExpectedTax> Expected);

/// <summary>Runs the regression cases against one version, in this environment (E-PR12-5), and records the result.</summary>
public sealed record RunFiscalRuleTests(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid RuleVersionId, IReadOnlyList<FiscalTestCase> Cases) : ICommand;

/// <summary>The fiscal specialist activates a READY version (≠ configurer, step-up). A predecessor is closed at the new start date.</summary>
public sealed record ActivateFiscalRuleVersion(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid RuleVersionId) : ICommand;
