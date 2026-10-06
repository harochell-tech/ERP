using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Finance.Configuration;

// E-B03-15-1: what the configuration screens list, read by who prepares, approves or audits it (configuration:read).
// Account role maps are loaded as DRAFT by the deployment CLI and posting rules arrive as DRAFT with the migrations; the screens
// only approve them (E-B03-15-2). "By" columns carry the user's e-mail (null for the deployment identity).

public sealed record ListAccounts(Guid CompanyId, Guid SessionId) : IQuery;

/// <summary>E-FIN1-01-1: <see cref="AccountClass"/> is null until the Controller sets it.</summary>
/// <summary>
/// E-UX4-2: <see cref="Balance"/> is Σ(debit − credit) of every entry of the account to date and <see cref="HasEntries"/> whether it
/// has any, so the chart of accounts can offer to deactivate only an account at zero.
/// </summary>
public sealed record AccountView(Guid AccountId, string Code, string Name, bool IsControl, string? AccountClass, string Status, decimal Balance, bool HasEntries);

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
            """
            SELECT a.account_id, a.code, a.name, a.is_control, a.account_class, a.status, coalesce(b.balance, 0)::numeric(19,2), b.account_id IS NOT NULL
            FROM fin.account a
            LEFT JOIN (SELECT account_id, sum(debit - credit) AS balance FROM fin.gl_entry WHERE company_id = @c GROUP BY account_id) b ON b.account_id = a.account_id
            WHERE a.company_id = @c ORDER BY a.code
            """,
            r => new AccountView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.NullableString(4), r.GetString(5), r.GetDecimal(6), r.GetBoolean(7)),
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
    string? ApprovedBy,
    string? AccountRoleName = null);

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
            SELECT m.map_id, m.account_role, m.item_category, a.code, a.name, m.effective_from, m.effective_to, m.status, coalesce(p.display_name, p.email), coalesce(ap.display_name, ap.email), ar.name
            FROM fin.account_role_map m
            JOIN fin.account a ON a.account_id = m.account_id
            JOIN fin.account_role ar ON ar.role_code = m.account_role
            JOIN iam.user p ON p.user_id = m.prepared_by
            LEFT JOIN iam.user ap ON ap.user_id = m.approved_by
            WHERE m.company_id = @c AND (CAST(@status AS text) IS NULL OR m.status = CAST(@status AS text))
            ORDER BY m.status, m.account_role, m.item_category NULLS FIRST, m.effective_from
            """,
            r => new AccountRoleMapView(
                r.GetGuid(0), r.GetString(1), r.NullableString(2), r.GetString(3), r.GetString(4), r.Date(5), r.IsDBNull(6) ? null : r.Date(6),
                r.GetString(7), r.NullableString(8), r.NullableString(9), r.NullableString(10)),
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
    string? ApprovedBy,
    IReadOnlyList<PostingRuleLineView>? Lines = null);

/// <summary>E-UX2-7: one line of the journal a rule generates, with its Spanish explanation (a template with {placeholders}).</summary>
public sealed record PostingRuleLineView(string Code, string Side, string AccountRole, string? AccountRoleName, string Amount, string? Explanation);

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
            SELECT r.code, r.event_type, v.version, v.status, v.close_component, v.effective_from, v.effective_to, coalesce(u.display_name, u.email)
            FROM fin.posting_rule r
            JOIN fin.posting_rule_version v ON v.posting_rule_id = r.posting_rule_id
            LEFT JOIN iam.user u ON u.user_id = v.approved_by
            ORDER BY r.code, v.version
            """,
            r => new PostingRuleVersionView(
                r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.Date(5), r.IsDBNull(6) ? null : r.Date(6), r.NullableString(7)),
            cancellationToken).ConfigureAwait(false);
        var lines = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT r.code, v.version, l.line ->> 'code', l.line ->> 'side', l.line ->> 'account_role', ar.name, l.line ->> 'amount',
                   v.explanation_templates ->> (l.line ->> 'code')
            FROM fin.posting_rule r
            JOIN fin.posting_rule_version v ON v.posting_rule_id = r.posting_rule_id
            CROSS JOIN LATERAL jsonb_array_elements(v.definition -> 'lines') WITH ORDINALITY AS l (line, n)
            LEFT JOIN fin.account_role ar ON ar.role_code = l.line ->> 'account_role'
            ORDER BY r.code, v.version, l.n
            """,
            r => (Rule: r.GetString(0), Version: r.GetInt32(1), Line: new PostingRuleLineView(
                r.GetString(2), r.GetString(3), r.GetString(4), r.NullableString(5), r.GetString(6), r.NullableString(7))),
            cancellationToken).ConfigureAwait(false)).ToLookup(l => (l.Rule, l.Version), l => l.Line);
        return ApiJson.Serialize(new PostingRuleList(items.Select(i => i with { Lines = lines[(i.RuleCode, i.Version)].ToList() }).ToList()));
    }
}

public sealed record ListAccountRoles(Guid CompanyId, Guid SessionId) : IQuery;

/// <summary>
/// E-UX2-5: an account role with its Spanish name. <see cref="UsedByActiveRule"/>: a line of an ACTIVE posting rule version posts to
/// it; <see cref="MappedToday"/>: the company has an ACTIVE mapping for it covering today (any item category).
/// </summary>
public sealed record AccountRoleView(string RoleCode, string? Name, string Description, bool IsControl, bool UsedByActiveRule, bool MappedToday);

public sealed record AccountRoleList(IReadOnlyList<AccountRoleView> Items);

[RequiresPermission("configuration:read")]
public sealed class ListAccountRolesHandler : IQueryHandler<ListAccountRoles>
{
    public string QueryType => "Finance.ListAccountRoles";

    public async Task<string> HandleAsync(ListAccountRoles query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT ar.role_code, ar.name, ar.description, ar.is_control,
                   EXISTS (SELECT 1 FROM fin.posting_rule_version v CROSS JOIN LATERAL jsonb_array_elements(v.definition -> 'lines') AS l (line)
                           WHERE v.status = 'ACTIVE' AND (v.effective_to IS NULL OR v.effective_to > @today) AND l.line ->> 'account_role' = ar.role_code),
                   EXISTS (SELECT 1 FROM fin.account_role_map m
                           WHERE m.company_id = @c AND m.account_role = ar.role_code AND m.status = 'ACTIVE'
                             AND m.effective_from <= @today AND (m.effective_to IS NULL OR m.effective_to > @today))
            FROM fin.account_role ar
            WHERE ar.role_code NOT IN ('MANUAL_ADJUSTMENT', 'PURCHASE_EXPENSE', 'FIXED_ASSET_COST', 'FIXED_ASSET_ACCUMULATED', 'FIXED_ASSET_DEPRECIATION')
            ORDER BY ar.name
            """,
            r => new AccountRoleView(r.GetString(0), r.NullableString(1), r.GetString(2), r.GetBoolean(3), r.GetBoolean(4), r.GetBoolean(5)),
            cancellationToken,
            ("c", context.CompanyId),
            ("today", Rochell.Platform.Time.BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow))).ConfigureAwait(false);
        return ApiJson.Serialize(new AccountRoleList(items));
    }
}
