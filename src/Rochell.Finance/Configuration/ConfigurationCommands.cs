using Rochell.Platform.Commands;

namespace Rochell.Finance.Configuration;

/// <summary>
/// E-UX2-6 (b): prepares a DRAFT account-role mapping from the screens (the deployment CLI still imports them). <see cref="ItemCategory"/>
/// null maps every category.
/// </summary>
public sealed record PrepareAccountRoleMap(Guid CompanyId, Guid SessionId, string IdempotencyKey, string AccountRole, string? ItemCategory, Guid AccountId, DateOnly EffectiveFrom) : ICommand;

/// <summary>Approves a DRAFT account-role mapping (prepared on screen or by the deployment role); closes the open mapping it replaces.</summary>
public sealed record ApproveAccountRoleMap(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid MapId) : ICommand;

/// <summary>Approves a DRAFT posting rule version (E-PR05-2); closes the open version it replaces.</summary>
public sealed record ApprovePostingRuleVersion(Guid CompanyId, Guid SessionId, string IdempotencyKey, string RuleCode, int Version) : ICommand;
