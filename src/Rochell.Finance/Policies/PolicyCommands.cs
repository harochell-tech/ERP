using Rochell.Platform.Commands;

namespace Rochell.Finance.Policies;

/// <summary>Prepares a DRAFT version with every parameter of the policy (E-PR06-3). Values as strings (E-PR06-5).</summary>
public sealed record PrepareAccountingPolicyVersion(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    string PolicyCode,
    DateOnly EffectiveFrom,
    IReadOnlyDictionary<string, string> Parameters,
    string Justification) : ICommand;

/// <summary>Approves a DRAFT version (someone other than the preparer); closes the open version it replaces.</summary>
public sealed record ApproveAccountingPolicyVersion(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PolicyVersionId) : ICommand;
