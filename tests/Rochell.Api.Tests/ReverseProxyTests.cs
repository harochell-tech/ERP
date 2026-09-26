using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Rochell.Api.Auth;
using Rochell.Api.Http;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>E-B03-2: behind Caddy the scheme comes from X-Forwarded-Proto, and only from the configured proxy networks.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ReverseProxyTests(PostgresFixture postgres)
{
    private static string RedirectOf(HttpResponseMessage challenge)
        => System.Web.HttpUtility.ParseQueryString(challenge.Headers.Location!.Query)["redirect_uri"] ?? string.Empty;

    [Fact]
    public async Task A_trusted_proxy_sets_the_https_scheme_of_the_OIDC_redirect()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h, settings: new Dictionary<string, string?> { ["Rochell:ReverseProxy:TrustedNetworks:0"] = "172.30.0.0/24" })
        {
            RemoteIp = IPAddress.Parse("172.30.0.2"), // Caddy on the compose network
        };

        Assert.Equal("https://localhost" + OidcAuthentication.CallbackPath, await RedirectOfLoginAsync(api));
    }

    [Theory]
    [InlineData("172.30.0.0/24")]  // the request does not come from the proxy network
    [InlineData(null)]             // no proxy configured: forwarded headers are ignored outside Development
    public async Task Forwarded_headers_from_elsewhere_are_ignored(string? network)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var settings = network is null ? null : new Dictionary<string, string?> { ["Rochell:ReverseProxy:TrustedNetworks:0"] = network };
        using var api = new ApiHost(h, settings: settings) { RemoteIp = IPAddress.Parse("198.51.100.20") };

        Assert.Equal("http://localhost" + OidcAuthentication.CallbackPath, await RedirectOfLoginAsync(api));
    }

    private static async Task<string> RedirectOfLoginAsync(ApiHost api)
    {
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/login");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");
        var challenge = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        return RedirectOf(challenge);
    }
}
