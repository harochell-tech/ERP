using Rochell.Finance.Configuration;
using Rochell.Platform.Commands;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Finance.Tests;

/// <summary>ApproveAccountRoleMap and ApprovePostingRuleVersion (Controller, step-up, four eyes, no overlaps).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ConfigurationApprovalTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Controller_approves_a_draft_mapping_which_closes_the_previous_one()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var oldAccount = await h.CreateAccountAsync("2105", "GRNI anterior", isControl: false);
        var newAccount = await h.CreateAccountAsync("2106", "GRNI nueva", isControl: false);
        var oldMap = await h.CreateActiveMapAsync("GRNI", oldAccount);
        var draft = await h.CreateMapAsync("GRNI", newAccount, new DateOnly(2030, 1, 1), null, active: false);

        await h.RunAsync(new ApproveAccountRoleMap(h.CompanyId, controller, "m-1", draft), new ApproveAccountRoleMapHandler());

        Assert.Equal("ACTIVE", await h.ScalarAsync<string>("SELECT status FROM fin.account_role_map WHERE map_id = @m", ("m", draft)));
        Assert.Equal("2030-01-01", await h.ScalarAsync<string>("SELECT effective_to::text FROM fin.account_role_map WHERE map_id = @m", ("m", oldMap)));
    }

    [Fact]
    public async Task Preparer_cannot_approve_and_non_controllers_are_rejected()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var controllerUser = await h.CreateUserAsync();
        await h.GrantAsync(h.CompanyId, controllerUser, "CONTROLLER");
        var controller = await h.CreateSessionAsync(controllerUser);
        var buyer = await h.SessionWithRolesAsync("COMPRADOR");
        var account = await h.CreateAccountAsync("2105", "GRNI", isControl: false);
        var draft = await h.CreateMapAsync("GRNI", account, new DateOnly(2030, 1, 1), null, active: false, preparedBy: controllerUser);

        var fourEyes = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveAccountRoleMap(h.CompanyId, controller, "m-2", draft), new ApproveAccountRoleMapHandler()));
        var notAllowed = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveAccountRoleMap(h.CompanyId, buyer, "m-3", draft), new ApproveAccountRoleMapHandler()));

        Assert.Equal(FinanceErrors.FourEyes, fourEyes.Code);
        Assert.Equal(AuthorizationErrors.NotAuthorized, notAllowed.Code);
    }

    [Fact]
    public async Task Mapping_that_starts_before_the_active_one_is_rejected()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var account = await h.CreateAccountAsync("2105", "GRNI", isControl: false);
        await h.CreateActiveMapAsync("GRNI", account, new DateOnly(2030, 1, 1));
        var draft = await h.CreateMapAsync("GRNI", account, new DateOnly(2029, 1, 1), null, active: false);

        var ex = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveAccountRoleMap(h.CompanyId, controller, "m-4", draft), new ApproveAccountRoleMapHandler()));

        Assert.Equal(FinanceErrors.Overlap, ex.Code);
    }

    [Fact]
    public async Task Approving_requires_recent_re_authentication()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var account = await h.CreateAccountAsync("2105", "GRNI", isControl: false);
        var draft = await h.CreateMapAsync("GRNI", account, new DateOnly(2030, 1, 1), null, active: false);
        clock.Advance(TimeSpan.FromMinutes(6));

        var ex = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveAccountRoleMap(h.CompanyId, controller, "m-5", draft), new ApproveAccountRoleMapHandler()));

        Assert.Equal(AuthorizationErrors.StepUpRequired, ex.Code);
    }

    [Fact]
    public async Task Rule_version_approval_activates_and_closes_the_previous_version()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.CreateLedgerAsync();
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        await h.AdminRequireAsync(
            """
            INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, effective_from, status)
            SELECT posting_rule_id, 2, definition, explanation_templates, close_component, DATE '2031-01-01', 'DRAFT'
            FROM fin.posting_rule_version WHERE posting_rule_id = '0192f000-0000-7000-8000-0000000000e1' AND version = 1
            """);

        await h.RunAsync(new ApprovePostingRuleVersion(h.CompanyId, controller, "r-1", FinanceSetup.TestRule, 2), new ApprovePostingRuleVersionHandler());

        Assert.Equal("ACTIVE|2031-01-01", await h.ScalarAsync<string>(
            "SELECT string_agg(status || '|' || coalesce(effective_to::text, 'open'), ';' ORDER BY version) FROM fin.posting_rule_version WHERE posting_rule_id = '0192f000-0000-7000-8000-0000000000e1' AND version = 1"));
        Assert.Equal("ACTIVE", await h.ScalarAsync<string>(
            "SELECT status FROM fin.posting_rule_version WHERE posting_rule_id = '0192f000-0000-7000-8000-0000000000e1' AND version = 2"));
    }

    [Theory]
    [InlineData("""{"lines": [{"code": "A", "side": "DEBIT", "account_role": "TEST_EXPENSE", "amount": "x"}]}""")]
    [InlineData("""{"lines": [{"code": "A", "side": "DEBIT", "account_role": "NO_EXISTE", "amount": "x"}, {"code": "B", "side": "CREDIT", "account_role": "TEST_INCOME", "amount": "x"}]}""")]
    [InlineData("""{"lines": [{"code": "A", "side": "DEBIT", "account_role": "TEST_CONTROL", "amount": "x"}, {"code": "B", "side": "CREDIT", "account_role": "TEST_INCOME", "amount": "x"}]}""")]
    [InlineData("""{"lines": [{"code": "A", "side": "DEBIT", "account_role": "TEST_EXPENSE", "amount": "x", "subledger": "AP"}, {"code": "B", "side": "CREDIT", "account_role": "TEST_INCOME", "amount": "x"}]}""")]
    [InlineData("""{"lines": [{"code": "A", "side": "DEBIT", "account_role": "TEST_EXPENSE", "amount": "x", "dimensions": ["warehouse"]}, {"code": "B", "side": "CREDIT", "account_role": "TEST_INCOME", "amount": "x"}]}""")]
    public async Task Invalid_rule_definitions_are_rejected_on_approval(string definition)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        await h.AdminRequireAsync(
            $$"""
            INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, effective_from, status)
            VALUES ('0192f000-0000-7000-8000-0000000000e1', 9, '{{definition}}', '{}', 'INV-MOV', DATE '2035-01-01', 'DRAFT')
            """);

        var ex = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApprovePostingRuleVersion(h.CompanyId, controller, "bad-rule", FinanceSetup.TestRule, 9), new ApprovePostingRuleVersionHandler()));

        Assert.Equal(FinanceErrors.RuleDefinitionInvalid, ex.Code);
    }
}
