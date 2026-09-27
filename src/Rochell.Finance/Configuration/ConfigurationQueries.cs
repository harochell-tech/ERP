using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Finance.Configuration;

// E-B03-15-1: what the configuration screens list, read by who prepares, approves or audits it (configuration:read).
// Account role maps are loaded as DRAFT by the deployment CLI and posting rules arrive as DRAFT with the migrations; the screens
// only approve them (E-B03-15-2). "By" columns carry the user's e-mail (null for the deployment identity).

public sealed record ListAccounts(Guid CompanyId, Guid SessionId) : IQuery;

public sealed record AccountView(Guid AccountId, string Code, string Name, bool IsControl);

public sealed record AccountList(IReadOnlyList<AccountView> Items);

[RequiresPermission("configuration:read")]
public sealed class ListAccountsHandler : IQueryHandler<ListAccounts>
{
    public string QueryType => "Finance.ListAccounts";

    public async Task<string> HandleAsync(ListAccounts query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT account_id, code, name, is_control FROM fin.account WHERE company_id = @c ORDER BY code",
            r => new AccountView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetBoolean(3)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        return ApiJson.Serialize(new AccountList(items));
    }
}

public sealed record ListAccountRoleMaps(Guid CompanyId, Guid SessionId, string? Status = null) : IQuery;

public sealed record AccountRoleMapView(
    Guid MapId,
    string AccountRole,
    string? ItemCategory,
    string AccountCode,
    string AccountName,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    string Status,
    string? PreparedBy,
    string? ApprovedBy);

public sealed record AccountRoleMapList(IReadOnlyList<AccountRoleMapView> Items);

[RequiresPermission("configuration:read")]
public sealed class ListAccountRoleMapsHandler : IQueryHandler<ListAccountRoleMaps>
{
    public string QueryType => "Finance.ListAccountRoleMaps";

    public async Task<string> HandleAsync(ListAccountRoleMaps query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT m.map_id, m.account_role, m.item_category, a.code, a.name, m.effective_from, m.effective_to, m.status, p.email, ap.email
            FROM fin.account_role_map m
            JOIN fin.account a ON a.account_id = m.account_id
            JOIN iam.user p ON p.user_id = m.prepared_by
            LEFT JOIN iam.user ap ON ap.user_id = m.approved_by
            WHERE m.company_id = @c AND (CAST(@status AS text) IS NULL OR m.status = CAST(@status AS text))
            ORDER BY m.status, m.account_role, m.item_category NULLS FIRST, m.effective_from
            """,
            r => new AccountRoleMapView(
                r.GetGuid(0), r.GetString(1), r.NullableString(2), r.GetString(3), r.GetString(4), r.Date(5), r.IsDBNull(6) ? null : r.Date(6),
                r.GetString(7), r.NullableString(8), r.NullableString(9)),
            cancellationToken,
            ("c", context.CompanyId),
            ("status", query.Status)).ConfigureAwait(false);
        return ApiJson.Serialize(new AccountRoleMapList(items));
    }
}

public sealed record ListPostingRules(Guid CompanyId, Guid SessionId) : IQuery;

public sealed record PostingRuleVersionView(
    string RuleCode,
    string EventType,
    int Version,
    string Status,
    string CloseComponent,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    string? ApprovedBy);

public sealed record PostingRuleList(IReadOnlyList<PostingRuleVersionView> Items);

/// <summary>Posting rules are global (one definition for every company); the query still runs under the caller's company.</summary>
[RequiresPermission("configuration:read")]
public sealed class ListPostingRulesHandler : IQueryHandler<ListPostingRules>
{
    public string QueryType => "Finance.ListPostingRules";

    public async Task<string> HandleAsync(ListPostingRules query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT r.code, r.event_type, v.version, v.status, v.close_component, v.effective_from, v.effective_to, u.email
            FROM fin.posting_rule r
            JOIN fin.posting_rule_version v ON v.posting_rule_id = r.posting_rule_id
            LEFT JOIN iam.user u ON u.user_id = v.approved_by
            ORDER BY r.code, v.version
            """,
            r => new PostingRuleVersionView(
                r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.Date(5), r.IsDBNull(6) ? null : r.Date(6), r.NullableString(7)),
            cancellationToken).ConfigureAwait(false);
        return ApiJson.Serialize(new PostingRuleList(items));
    }
}
