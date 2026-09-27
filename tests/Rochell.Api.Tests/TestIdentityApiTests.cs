using System.Net;
using System.Net.Http.Json;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>E-B03-14 (a) over HTTP: list test identities, act as one (the cookie now names the acting session), return.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class TestIdentityApiTests(PostgresFixture postgres)
{
    private static async Task<Guid> SyntheticAsync(TestHarness h, string email, string role)
    {
        var id = Guid.CreateVersion7();
        await h.AdminRequireAsync($"INSERT INTO iam.user (user_id, kind, email, status) VALUES ('{id}', 'SYNTHETIC', '{email}', 'ACTIVE')");
        await h.GrantAsync(h.CompanyId, id, role);
        return id;
    }

    [Fact]
    public async Task A_tester_acts_as_a_synthetic_buyer_and_returns()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.InitTestEnvironmentAsync();
        using var api = new ApiHost(h);
        var tester = await h.CreateUserAsync();
        await h.GrantAsync(h.CompanyId, tester, "PROBADOR");
        var buyer = await SyntheticAsync(h, "comprador@staging.invalid", "COMPRADOR");
        var browser = await api.SignInAsync(tester);

        var identities = await browser.GetOkAsync($"/api/v1/auth/test-identities?companyId={h.CompanyId}");
        var actAs = await browser.PostAsJsonAsync("/api/v1/auth/act-as", new { companyId = h.CompanyId, userId = buyer });
        var acting = await browser.GetOkAsync("/api/v1/session");
        var created = await browser.OkAsync(h.CompanyId, "master-data", "create-supplier", new { rnc = "130000011", legalName = "Agregados del Este, S.R.L." });
        var stop = await browser.PostAsync("/api/v1/auth/act-as/stop", null);
        var back = await browser.GetOkAsync("/api/v1/session");

        Assert.Equal("comprador@staging.invalid", identities[0].GetProperty("email").GetString());
        Assert.Equal(HttpStatusCode.NoContent, actAs.StatusCode);
        Assert.Equal("comprador@staging.invalid", acting.GetProperty("email").GetString());
        Assert.Equal($"{tester:N}@{TestHarness.HostedDomain}", acting.GetProperty("authenticatedEmail").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Object, created.ValueKind);
        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        Assert.Equal($"{tester:N}@{TestHarness.HostedDomain}", back.GetProperty("email").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, back.GetProperty("authenticatedEmail").ValueKind);
    }

    [Fact]
    public async Task Without_act_as_the_identities_are_not_listed_nor_usable()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.InitTestEnvironmentAsync();
        using var api = new ApiHost(h);
        var buyer = await SyntheticAsync(h, "comprador@staging.invalid", "COMPRADOR");
        var browser = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CONTROLLER"));

        var list = await browser.GetAsync($"/api/v1/auth/test-identities?companyId={h.CompanyId}");
        var actAs = await browser.PostAsJsonAsync("/api/v1/auth/act-as", new { companyId = h.CompanyId, userId = buyer });

        Assert.Equal((HttpStatusCode.Forbidden, "NOT_AUTHORIZED"), await list.ProblemAsync());
        Assert.Equal((HttpStatusCode.Forbidden, "NOT_AUTHORIZED"), await actAs.ProblemAsync());
    }
}
