using Rochell.Platform.Commands;

namespace Rochell.Finance.Configuration;

/// <summary>Approves a DRAFT account-role mapping (prepared by the deployment role); closes the open mapping it replaces.</summary>
public sealed record ApproveAccountRoleMap(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid MapId) : ICommand;

/// <summary>Approves a DRAFT posting rule version (E-PR05-2); closes the open version it replaces.</summary>
public sealed record ApprovePostingRuleVersion(Guid CompanyId, Guid SessionId, string IdempotencyKey, string RuleCode, int Version) : ICommand;
