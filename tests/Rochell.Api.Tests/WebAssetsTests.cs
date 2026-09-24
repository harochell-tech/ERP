using System.Net;
using Rochell.Api.Http;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>E-PR18b-2: the host serves the web export from its own origin, with security headers, without touching /api.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class WebAssetsTests(PostgresFixture postgres) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("rochell-web-").FullName;

    [Fact]
    public async Task Pages_are_served_with_the_content_security_policy_and_api_paths_are_untouched()
    {
        Directory.CreateDirectory(Path.Combine(_root, "compras", "ordenes"));
        await File.WriteAllTextAsync(Path.Combine(_root, "index.html"), "<html>inicio</html>");
        await File.WriteAllTextAsync(Path.Combine(_root, "compras", "ordenes", "index.html"), "<html>ordenes</html>");
        await File.WriteAllTextAsync(Path.Combine(_root, "404.html"), "<html>no existe</html>");
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h, settings: new Dictionary<string, string?> { ["Rochell:WebRoot"] = _root });
        var browser = api.Browser();

        var home = await browser.GetAsync("/");
        var orders = await browser.GetAsync("/compras/ordenes/");
        var missing = await browser.GetAsync("/no/existe/");
        var session = await browser.GetAsync("/api/v1/session");

        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Equal("<html>inicio</html>", await home.Content.ReadAsStringAsync());
        Assert.Equal(WebAssets.ContentSecurityPolicy, Assert.Single(home.Headers.GetValues("Content-Security-Policy")));
        Assert.Contains("frame-ancestors 'none'", WebAssets.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.Equal("nosniff", Assert.Single(home.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("<html>ordenes</html>", await orders.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("<html>no existe</html>", await missing.Content.ReadAsStringAsync());
        Assert.Equal((HttpStatusCode.Unauthorized, "SESSION_INVALID"), await session.ProblemAsync());
        Assert.False(session.Headers.Contains("Content-Security-Policy"));
    }

    [Fact]
    public async Task A_missing_web_root_stops_the_host()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h, settings: new Dictionary<string, string?> { ["Rochell:WebRoot"] = Path.Combine(_root, "no-existe") });

        var error = Assert.Throws<InvalidOperationException>(() => api.CreateClient());

        Assert.Contains("npm run build", error.Message, StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
