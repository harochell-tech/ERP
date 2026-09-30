using System.Text.Json;
using Rochell.Finance.Configuration;
using Rochell.Finance.Policies;
using Rochell.Platform.Commands;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Finance.Tests;

/// <summary>UX2-01 (E-UX2-2, 4, 5, 6, 7): readable catalogues, mappings prepared on screen, rule lines, a clear missing parameter.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ConfigurationUxTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_contador_prepares_a_mapping_on_screen_and_the_controller_approves_it()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var contador = await h.SessionWithRolesAsync("CONTADOR");
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var account = await h.CreateAccountAsync("2105", "Recibido no facturado", isControl: false);

        var prepared = await h.RunAsync(new PrepareAccountRoleMap(h.CompanyId, contador, "p-1", "GRNI", null, account, new DateOnly(2026, 1, 1)), new PrepareAccountRoleMapHandler());
        await h.RunAsync(new ApproveAccountRoleMap(h.CompanyId, controller, "a-1", prepared.ResultRef), new ApproveAccountRoleMapHandler());

        Assert.Equal("ACTIVE", await h.ScalarAsync<string>("SELECT status FROM fin.account_role_map WHERE map_id = @m", ("m", prepared.ResultRef)));
        Assert.Equal("AccountRoleMapPrepared:1,AccountRoleMapApproved:2", await h.ScalarAsync<string>(
            "SELECT string_agg(event_type || ':' || aggregate_version, ',' ORDER BY aggregate_version) FROM core.domain_event WHERE aggregate_id = @m", ("m", prepared.ResultRef)));
        Assert.Equal("-:DRAFT,DRAFT:ACTIVE", await h.ScalarAsync<string>(
            "SELECT string_agg(coalesce(from_state, '-') || ':' || to_state, ',' ORDER BY to_state = 'ACTIVE') FROM core.state_history WHERE aggregate_id = @m", ("m", prepared.ResultRef)));
        var maps = JsonDocument.Parse(await h.QueryAsync(new ListAccountRoleMaps(h.CompanyId, controller), new ListAccountRoleMapsHandler()));
        Assert.Equal("Recibido no facturado (GRNI)", maps.RootElement.GetProperty("items")[0].GetProperty("accountRoleName").GetString());
    }

    [Fact]
    public async Task Preparing_refuses_technical_roles_mismatched_accounts_and_people_without_the_permission()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var contador = await h.SessionWithRolesAsync("CONTADOR");
        var buyer = await h.SessionWithRolesAsync("COMPRADOR");
        var regular = await h.CreateAccountAsync("2105", "GRNI", isControl: false);
        var control = await h.CreateAccountAsync("2101", "Proveedores", isControl: true);
        Task<CommandResult> Prepare(Guid session, string key, string role, Guid account, string? category = null)
            => h.RunAsync(new PrepareAccountRoleMap(h.CompanyId, session, key, role, category, account, new DateOnly(2026, 1, 1)), new PrepareAccountRoleMapHandler());

        var technical = await Assert.ThrowsAsync<DomainException>(() => Prepare(contador, "x-1", "MANUAL_ADJUSTMENT", regular));
        var mismatch = await Assert.ThrowsAsync<DomainException>(() => Prepare(contador, "x-2", "AP_CONTROL", regular));
        var category = await Assert.ThrowsAsync<DomainException>(() => Prepare(contador, "x-3", "RAW_MATERIAL", control, "BLOQUE"));
        var notAllowed = await Assert.ThrowsAsync<DomainException>(() => Prepare(buyer, "x-4", "GRNI", regular));

        Assert.Equal(
            (FinanceErrors.AccountRoleInvalid, FinanceErrors.MapAccountInvalid, FinanceErrors.AccountRoleInvalid, AuthorizationErrors.NotAuthorized),
            (technical.Code, mismatch.Code, category.Code, notAllowed.Code));
        Assert.Equal(0L, await h.CountAsync("fin.account_role_map"));
    }

    [Fact]
    public async Task Account_roles_and_posting_rule_lines_are_named_and_the_unmapped_roles_flagged()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.CreateLedgerAsync(mapIncome: false); // the test rule posts to TEST_EXPENSE, TEST_CONTROL and TEST_INCOME
        var controller = await h.SessionWithRolesAsync("CONTROLLER");

        var roles = JsonDocument.Parse(await h.QueryAsync(new ListAccountRoles(h.CompanyId, controller), new ListAccountRolesHandler())).RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(r => r.GetProperty("roleCode").GetString()!, r => (Name: r.GetProperty("name").GetString(), Used: r.GetProperty("usedByActiveRule").GetBoolean(), Mapped: r.GetProperty("mappedToday").GetBoolean()));
        var rules = JsonDocument.Parse(await h.QueryAsync(new ListPostingRules(h.CompanyId, controller), new ListPostingRulesHandler())).RootElement.GetProperty("items").EnumerateArray().ToList();

        Assert.False(roles.ContainsKey("MANUAL_ADJUSTMENT"));
        Assert.All(roles.Where(r => !r.Key.StartsWith("TEST_", StringComparison.Ordinal)), r => Assert.False(string.IsNullOrWhiteSpace(r.Value.Name)));
        Assert.Equal(((true, true), (true, false), (false, false)), ((roles["TEST_EXPENSE"].Used, roles["TEST_EXPENSE"].Mapped), (roles["TEST_INCOME"].Used, roles["TEST_INCOME"].Mapped), (roles["GRNI"].Used, roles["GRNI"].Mapped)));
        var r04 = rules.First(r => r.GetProperty("ruleCode").GetString() == "R-04").GetProperty("lines").EnumerateArray().First();
        Assert.Equal(("DEBIT", "GRNI", "Recibido no facturado (GRNI)"), (r04.GetProperty("side").GetString(), r04.GetProperty("accountRole").GetString(), r04.GetProperty("accountRoleName").GetString()));
        Assert.StartsWith("Factura {ncf}:", r04.GetProperty("explanation").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_policy_parameter_and_account_role_carries_its_screen_texts()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        Assert.Equal(0L, await h.ScalarAsync<long>(
            "SELECT count(*) FROM acc.policy_parameter_definition WHERE label IS NULL OR unit IS NULL OR example IS NULL OR affects IS NULL"));
        Assert.Equal(0L, await h.ScalarAsync<long>(
            "SELECT count(*) FROM acc.policy_parameter_definition WHERE (unit = 'PERCENT') <> (value_type = 'DECIMAL_PERCENT') OR (unit = 'OPTION') <> (value_type = 'ENUM')"));
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM acc.accounting_policy WHERE name IS NULL"));
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM fin.account_role WHERE name IS NULL AND role_code NOT LIKE 'TEST%'")); // fixtures have none
    }

    [Fact]
    public void A_policy_version_missing_a_later_parameter_is_a_missing_prerequisite()
    {
        var policy = new ResolvedPolicy(Guid.NewGuid(), "REVENUE_ACCOUNTING", 1, new Dictionary<string, string>(StringComparer.Ordinal));

        var ex = Assert.Throws<DomainException>(() => policy.Integer("authorization_expiry_alert_days"));

        Assert.Equal(FinanceErrors.PostingPrerequisiteMissing, ex.Code);
        Assert.Contains("authorization_expiry_alert_days", ex.Message, StringComparison.Ordinal);
    }
}
