using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Finance.Policies;

// E-B03-15-1: accounting policies with their parameter definitions and every version of the company, for the prepare and
// approve screens. Parameter values are strings, as prepared (E-PR06-5).

public sealed record ListAccountingPolicies(Guid CompanyId, Guid SessionId) : IQuery;

/// <summary>E-UX2-2: <see cref="Label"/>, <see cref="Unit"/> (PERCENT, AMOUNT, DAYS, HOURS, MINUTES, OPTION), <see cref="Example"/> and <see cref="Affects"/> for the screens.</summary>
public sealed record PolicyParameterDefinitionView(
    string ParamCode, string ValueType, decimal? MinValue, decimal? MaxValue, IReadOnlyList<string>? AllowedValues, string Description,
    string? Label = null, string? Unit = null, string? Example = null, string? Affects = null);

public sealed record PolicyVersionView(
    Guid PolicyVersionId,
    int Version,
    string Status,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    string? PreparedBy,
    string? ApprovedBy,
    string Justification,
    IReadOnlyDictionary<string, string> Parameters);

/// <summary>E-UX2-2: <see cref="PreparerRoles"/> and <see cref="ApproverRoles"/> name the roles that prepare and approve versions.</summary>
public sealed record AccountingPolicyView(
    string PolicyCode,
    string OwnerRole,
    string Description,
    IReadOnlyList<PolicyParameterDefinitionView> Definitions,
    IReadOnlyList<PolicyVersionView> Versions,
    string? Name = null,
    IReadOnlyList<string>? PreparerRoles = null,
    IReadOnlyList<string>? ApproverRoles = null);

public sealed record AccountingPolicyList(IReadOnlyList<AccountingPolicyView> Items);

[RequiresPermission("configuration:read")]
public sealed class ListAccountingPoliciesHandler : IQueryHandler<ListAccountingPolicies>
{
    public string QueryType => "Finance.ListAccountingPolicies";

    public async Task<string> HandleAsync(ListAccountingPolicies query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var policies = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT policy_code, owner_role, description, name FROM acc.accounting_policy ORDER BY policy_code",
            r => (Code: r.GetString(0), Owner: r.GetString(1), Description: r.GetString(2), Name: r.NullableString(3)),
            cancellationToken).ConfigureAwait(false);
        var definitions = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT policy_code, param_code, value_type, min_value, max_value, allowed_values, description, label, unit, example, affects
            FROM acc.policy_parameter_definition ORDER BY policy_code, param_code
            """,
            r => (Policy: r.GetString(0), View: new PolicyParameterDefinitionView(
                r.GetString(1), r.GetString(2), r.NullableDecimal(3), r.NullableDecimal(4), r.IsDBNull(5) ? null : r.GetFieldValue<string[]>(5), r.GetString(6),
                r.NullableString(7), r.NullableString(8), r.NullableString(9), r.NullableString(10))),
            cancellationToken).ConfigureAwait(false)).ToLookup(d => d.Policy, d => d.View);
        var values = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT policy_version_id, param_code, value #>> '{}' FROM acc.accounting_policy_parameter WHERE company_id = @c",
            r => (Version: r.GetGuid(0), Code: r.GetString(1), Value: r.GetString(2)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false)).ToLookup(v => v.Version);
        var versions = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT v.policy_code, v.policy_version_id, v.version, v.status, v.effective_from, v.effective_to, coalesce(p.display_name, p.email), coalesce(a.display_name, a.email), v.justification
            FROM acc.accounting_policy_version v
            JOIN iam.user p ON p.user_id = v.prepared_by
            LEFT JOIN iam.user a ON a.user_id = v.approved_by
            WHERE v.company_id = @c
            ORDER BY v.policy_code, v.version DESC
            """,
            r => (Policy: r.GetString(0), Id: r.GetGuid(1), Version: r.GetInt32(2), Status: r.GetString(3), From: r.Date(4), To: r.IsDBNull(5) ? (DateOnly?)null : r.Date(5),
                PreparedBy: r.NullableString(6), ApprovedBy: r.NullableString(7), Justification: r.GetString(8)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false))
            .Select(v => (v.Policy, View: new PolicyVersionView(
                v.Id, v.Version, v.Status, v.From, v.To, v.PreparedBy, v.ApprovedBy, v.Justification,
                values[v.Id].ToDictionary(p => p.Code, p => p.Value, StringComparer.Ordinal))))
            .ToLookup(v => v.Policy, v => v.View);

        var roles = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT DISTINCT rp.permission_code, r.name FROM iam.role_permission rp JOIN iam.role r ON r.role_id = rp.role_id
            WHERE rp.permission_code IN ('accounting_policy:prepare', 'accounting_policy:approve') AND r.code <> 'SUPERADMIN'
            ORDER BY r.name
            """,
            r => (Permission: r.GetString(0), Role: r.GetString(1)),
            cancellationToken).ConfigureAwait(false)).ToLookup(r => r.Permission, r => r.Role);

        return ApiJson.Serialize(new AccountingPolicyList(policies
            .Select(p => new AccountingPolicyView(
                p.Code, p.Owner, p.Description, definitions[p.Code].ToList(), versions[p.Code].ToList(), p.Name,
                roles["accounting_policy:prepare"].ToList(), roles["accounting_policy:approve"].ToList()))
            .ToList()));
    }
}
