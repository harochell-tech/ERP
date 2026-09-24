namespace Rochell.TestInfrastructure;

public static class PolicySetup
{
    public static readonly IReadOnlyDictionary<string, string> Purchasing = new Dictionary<string, string>
    {
        ["receipt_tolerance_pct"] = "0.02",
        ["match_qty_tolerance_pct"] = "0.01",
        ["match_price_tolerance_pct"] = "0.01",
        ["match_amount_tolerance_abs"] = "5.00",
        ["po_approval_limit"] = "100000.00",
        ["po_approval_step_up_threshold"] = "50000.00",
    };

    public static readonly IReadOnlyDictionary<string, string> Inventory = new Dictionary<string, string>
    {
        ["inventory_adjustment_materiality"] = "10000.00",
        ["grni_aging_alert_days"] = "60",
        ["invoice_price_variance_allocation_method"] = "STOCK_COVERAGE",
        ["valuation_residual_account_role"] = "PURCHASE_PRICE_VARIANCE",
    };

    public static readonly IReadOnlyDictionary<string, string> Posting = new Dictionary<string, string>
    {
        ["rounding_difference_tolerance"] = "0.05",
        ["late_entry_hours"] = "24",
    };

    /// <summary>ACTIVE policy version prepared by the deployment identity and approved by the harness user. Returns its id.</summary>
    public static async Task<Guid> CreateActivePolicyAsync(this TestHarness h, string policyCode, IReadOnlyDictionary<string, string> parameters, DateOnly? from = null)
    {
        ArgumentNullException.ThrowIfNull(h);
        ArgumentNullException.ThrowIfNull(parameters);
        var id = Guid.CreateVersion7();
        await using (var version = h.Admin.CreateCommand(
            """
            INSERT INTO acc.accounting_policy_version (policy_version_id, policy_code, company_id, version, status, effective_from, prepared_by, justification)
            SELECT @id, @policy, @company, coalesce(max(version), 0) + 1, 'DRAFT', @from, '00000000-0000-7000-8000-00000000d001', 'fixture'
            FROM acc.accounting_policy_version WHERE company_id = @company AND policy_code = @policy
            """))
        {
            version.Parameters.AddWithValue("id", id);
            version.Parameters.AddWithValue("policy", policyCode);
            version.Parameters.AddWithValue("company", h.CompanyId);
            version.Parameters.AddWithValue("from", from ?? new DateOnly(2020, 1, 1));
            await version.ExecuteNonQueryAsync();
        }

        foreach (var (code, value) in parameters)
        {
            await using var parameter = h.Admin.CreateCommand(
                "INSERT INTO acc.accounting_policy_parameter (company_id, policy_version_id, param_code, value) VALUES (@c, @v, @p, to_jsonb(CAST(@value AS text)))");
            parameter.Parameters.AddWithValue("c", h.CompanyId);
            parameter.Parameters.AddWithValue("v", id);
            parameter.Parameters.AddWithValue("p", code);
            parameter.Parameters.AddWithValue("value", value);
            await parameter.ExecuteNonQueryAsync();
        }

        await h.AdminRequireAsync($"UPDATE acc.accounting_policy_version SET status = 'ACTIVE', approved_by = '{h.UserId}', approved_at = now() WHERE policy_version_id = '{id}'");
        return id;
    }
}
