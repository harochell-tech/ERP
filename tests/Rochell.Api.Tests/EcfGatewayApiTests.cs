using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Rochell.Api.Ecf;
using Rochell.Identity;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>VS4-02 (E-VS4-11, E-VS4-02-1/5/6): the gateway's switch, its e-NCF range endpoints, the webhook's secret and the worker.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class EcfGatewayApiTests(PostgresFixture postgres)
{
    private const string Secret = "test-webhook-secret";

    private static readonly Dictionary<string, string?> Simulated = new()
    {
        ["Rochell:Ecf:Mode"] = "SIMULATED",
        ["Rochell:Ecf:WebhookSecret"] = Secret,
        ["Rochell:Ecf:Interval"] = "01:00:00",
    };

    [Trait("AcceptanceVs4", "ECF-07")]
    [Fact]
    public async Task The_webhook_counts_only_with_the_secret_header_and_needs_no_CSRF_header()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.GrantAsync(h.CompanyId, IdentityConstants.DailyProcessUserId, "PROCESO_DIARIO");
        using var off = new ApiHost(h);
        using var on = new ApiHost(h, settings: Simulated);

        var whenOff = await Webhook(off.Browser(), Secret);
        var without = await Webhook(on.Browser(), null);
        var wrong = await Webhook(on.Browser(), "nope");
        var valid = await Webhook(on.Browser(), Secret);

        Assert.Equal(HttpStatusCode.NotFound, whenOff.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, without.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, valid.StatusCode);
        Assert.Equal("WEBHOOK:SIMULATED:01SIM", await h.ScalarAsync<string>("SELECT operation || ':' || mode || ':' || message FROM tax.ecf_call WHERE company_id = @c", ("c", h.CompanyId)));
    }

    [Fact]
    public async Task Ranges_are_prepared_and_approved_over_HTTP_and_the_worker_runs_as_the_daily_process()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.GrantAsync(h.CompanyId, IdentityConstants.DailyProcessUserId, "PROCESO_DIARIO");
        using var api = new ApiHost(h, settings: Simulated);
        var specialist = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("ESPECIALISTA_FISCAL"));
        var controller = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CONTROLLER"));

        var prepared = await specialist.OkAsync(h.CompanyId, "ecf", "prepare-ecf-series", new { ecfType = "31", from = 1, to = 500, validUntil = "2027-12-31" });
        var seriesId = prepared.GetProperty("result").GetProperty("seriesId").GetGuid();
        await controller.OkAsync(h.CompanyId, "ecf", "approve-ecf-series", new { seriesId, expectedVersion = 1 });
        var list = await specialist.GetOkAsync($"/api/v1/companies/{h.CompanyId}/ecf/series");
        var steps = await api.Services.GetRequiredService<EcfService>().RunOnceAsync(CancellationToken.None);

        var series = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal("ACTIVE", series.GetProperty("status").GetString());
        Assert.Equal("E310000000001", series.GetProperty("next").GetString());
        Assert.Equal(0, steps);
    }

    private static Task<HttpResponseMessage> Webhook(HttpClient client, string? secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, EcfWebhook.Path) { Content = new StringContent("""{"id":"01SIM"}""", Encoding.UTF8, "application/json") };
        if (secret is not null)
        {
            request.Headers.Add("X-Rochell-Ecf-Secret", secret);
        }

        return client.SendAsync(request);
    }
}
