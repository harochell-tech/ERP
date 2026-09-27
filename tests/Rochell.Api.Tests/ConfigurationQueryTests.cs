using System.Net;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>E-B03-15-1: the lists behind the configuration screens, guarded by configuration:read.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ConfigurationQueryTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Accounts_maps_posting_rules_and_policies_are_listed_for_the_controller()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var ledger = await h.CreateLedgerAsync();
        var draft = await h.CreateMapAsync("GRNI", ledger.IncomeAccount, new DateOnly(2026, 1, 1), null, active: false);
        await h.CreateActivePolicyAsync("POSTING", PolicySetup.Posting);
        using var api = new ApiHost(h);
        var controller = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CONTROLLER"));
        var root = $"/api/v1/companies/{h.CompanyId}/finance";

        var accounts = (await controller.GetOkAsync($"{root}/accounts")).GetProperty("items");
        var drafts = (await controller.GetOkAsync($"{root}/account-role-maps?status=DRAFT")).GetProperty("items");
        var rules = (await controller.GetOkAsync($"{root}/posting-rules")).GetProperty("items");
        var policies = (await controller.GetOkAsync($"{root}/accounting-policies")).GetProperty("items");

        Assert.Equal("2100,4100,6100", string.Join(',', accounts.EnumerateArray().Select(a => a.GetProperty("code").GetString())));
        var map = Assert.Single(drafts.EnumerateArray());
        Assert.Equal((draft.ToString(), "GRNI", "4100", "DRAFT"), (map.GetProperty("mapId").GetString(), map.GetProperty("accountRole").GetString(), map.GetProperty("accountCode").GetString(), map.GetProperty("status").GetString()));
        Assert.Contains(rules.EnumerateArray(), r => r.GetProperty("ruleCode").GetString() == "R-01" && r.GetProperty("status").GetString() == "DRAFT");
        var posting = policies.EnumerateArray().Single(p => p.GetProperty("policyCode").GetString() == "POSTING");
        Assert.NotEmpty(posting.GetProperty("definitions").EnumerateArray());
        var version = Assert.Single(posting.GetProperty("versions").EnumerateArray());
        Assert.Equal("ACTIVE", version.GetProperty("status").GetString());
        Assert.Equal(PolicySetup.Posting.Count, version.GetProperty("parameters").EnumerateObject().Count());
    }

    [Fact]
    public async Task Fiscal_sources_and_rules_show_versions_links_and_the_latest_test_run()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var actors = await h.FiscalActorsAsync();
        var version = await h.ActivateRuleAsync(actors, "itbis", "ITBIS_COMPRAS", "PURCHASE_ITBIS", TaxSetup.ItbisDefinition, new DateOnly(2026, 1, 1));
        using var api = new ApiHost(h);
        var specialist = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("ESPECIALISTA_FISCAL"));
        var root = $"/api/v1/companies/{h.CompanyId}/tax";

        var sources = (await specialist.GetOkAsync($"{root}/fiscal-sources")).GetProperty("items");
        var rules = (await specialist.GetOkAsync($"{root}/fiscal-rules")).GetProperty("items");

        var source = Assert.Single(sources.EnumerateArray());
        Assert.Equal(("TEST", new string('a', 64)), (source.GetProperty("environment").GetString(), source.GetProperty("fileSha256").GetString()));
        var rule = Assert.Single(rules.EnumerateArray());
        var active = Assert.Single(rule.GetProperty("versions").EnumerateArray());
        Assert.Equal((version.ToString(), "ACTIVE"), (active.GetProperty("ruleVersionId").GetString(), active.GetProperty("status").GetString()));
        Assert.Single(active.GetProperty("sources").EnumerateArray());
        Assert.True(active.GetProperty("latestTestRun").GetProperty("passed").GetBoolean());
    }

    [Fact]
    public async Task Configuration_lists_need_configuration_read()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);
        var buyer = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("COMPRADOR"));

        var response = await buyer.GetAsync($"/api/v1/companies/{h.CompanyId}/finance/account-role-maps");

        Assert.Equal((HttpStatusCode.Forbidden, "NOT_AUTHORIZED"), await response.ProblemAsync());
    }
}
