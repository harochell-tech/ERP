using System.Net;
using System.Text.Json;
using Rochell.Api.Auth;
using Rochell.Identity.Sessions;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>E-PR18-2: OIDC sign-in, session cookie, step-up, logout and anti-CSRF, through the simulated IdP.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class AuthenticationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Sign_in_opens_a_session_and_sets_a_strict_http_only_secure_cookie()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);
        var user = await h.CreateUserAsync();
        await h.GrantAsync(h.CompanyId, user, "COMPRADOR");
        var browser = api.Browser();

        var challenge = await browser.GetAsync("/api/v1/auth/login?returnUrl=/compras/ordenes");
        api.Idp.AddAccount(TestHarness.ClaimsOf(user));
        var callback = api.Idp.Authorize(challenge.Headers.Location!, TestHarness.SubjectOf(user));
        var done = await browser.GetAsync(callback.PathAndQuery);

        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        Assert.Equal("https://localhost" + OidcAuthentication.CallbackPath, api.Idp.LastAuthorizeRequest["redirect_uri"]);
        Assert.Equal(TestHarness.HostedDomain, api.Idp.LastAuthorizeRequest["hd"]);
        Assert.False(api.Idp.LastAuthorizeRequest.ContainsKey("prompt"));
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Contains("url=/compras/ordenes", await done.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var cookie = Assert.Single(done.Headers.GetValues("Set-Cookie"), c => c.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM iam.session WHERE user_id = @u AND auth_method = 'OIDC_GOOGLE'", ("u", user)));

        var session = await browser.GetOkAsync("/api/v1/session");
        Assert.Equal($"{user:N}@{TestHarness.HostedDomain}", session.GetProperty("email").GetString());
        var company = Assert.Single(session.GetProperty("companies").EnumerateArray());
        Assert.Equal(h.CompanyId, company.GetProperty("companyId").GetGuid());
        Assert.Equal("COMPRADOR", Assert.Single(company.GetProperty("assignments").EnumerateArray()).GetProperty("roleCode").GetString());
        Assert.Contains("purchase_order:read", company.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(JsonValueKind.String, session.GetProperty("stepUpValidUntil").ValueKind); // a fresh login counts as re-authentication
    }

    [Theory]
    [InlineData("otro-dominio.com", true)]
    [InlineData(TestHarness.HostedDomain, false)]
    public async Task Sign_in_is_rejected_for_foreign_domains_and_unverified_e_mails(string domain, bool verified)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);
        var user = await h.CreateUserAsync();
        var browser = api.Browser();
        var challenge = await browser.GetAsync("/api/v1/auth/login");
        api.Idp.AddAccount(new OidcClaims(TestHarness.SubjectOf(user), $"{user:N}@{domain}", verified, domain));

        var done = await browser.GetAsync(api.Idp.Authorize(challenge.Headers.Location!, TestHarness.SubjectOf(user)).PathAndQuery);

        Assert.Equal(HttpStatusCode.Forbidden, done.StatusCode);
        Assert.DoesNotContain(done.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [], c => c.StartsWith(SessionCookie.Name, StringComparison.Ordinal));
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM iam.session WHERE user_id = @u", ("u", user)));
    }

    [Fact]
    public async Task A_forged_or_missing_cookie_is_no_session()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);
        var browser = api.Browser();

        var none = await browser.GetAsync("/api/v1/session");

        // The raw iam.session id (visible in command_log and domain_event) is not a valid cookie value.
        var forged = new HttpRequestMessage(HttpMethod.Get, "/api/v1/session");
        forged.Headers.Add("Cookie", $"{SessionCookie.Name}={h.SessionId:N}");
        var raw = await api.CreateClient(new() { BaseAddress = new Uri("https://localhost") }).SendAsync(forged);

        Assert.Equal((HttpStatusCode.Unauthorized, "SESSION_INVALID"), await none.ProblemAsync());
        Assert.Equal((HttpStatusCode.Unauthorized, "SESSION_INVALID"), await raw.ProblemAsync());
    }

    [Fact]
    public async Task Logout_ends_the_session_in_the_database()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);
        var user = await h.CreateUserAsync();
        var browser = await api.SignInAsync(user);

        var logout = await browser.PostAsync("/api/v1/auth/logout", null);
        var after = await browser.GetAsync("/api/v1/session");

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM iam.session WHERE user_id = @u AND logout_at IS NOT NULL", ("u", user)));
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task State_changing_requests_without_the_anti_csrf_header_are_refused()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);
        var user = await h.CreateUserAsync();
        var browser = await api.SignInAsync(user);
        browser.DefaultRequestHeaders.Remove(Http.CsrfHeader.Name);

        var logout = await browser.PostAsync("/api/v1/auth/logout", null);

        Assert.Equal((HttpStatusCode.Forbidden, "CSRF_HEADER_REQUIRED"), await logout.ProblemAsync());
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM iam.session WHERE user_id = @u AND logout_at IS NULL", ("u", user)));
    }

    [Theory]
    [InlineData("https://evil.example/phish")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    public async Task The_return_url_never_leaves_the_site(string returnUrl)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);
        var user = await h.CreateUserAsync();
        var browser = api.Browser();

        var callback = await api.StartAsync(browser, "/api/v1/auth/login?returnUrl=" + Uri.EscapeDataString(returnUrl), user);
        var page = await (await browser.GetAsync(callback)).Content.ReadAsStringAsync();

        Assert.Contains("url=/\"", page, StringComparison.Ordinal);
    }
}
