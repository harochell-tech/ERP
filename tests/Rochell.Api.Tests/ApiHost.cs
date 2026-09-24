using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rochell.Api.Auth;
using Rochell.Api.Http;
using Rochell.Platform.Json;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>
/// The real API host (Program) in the Test environment over a test database, with the simulated IdP as Google and, when
/// given, a controllable clock. Background services stay off unless a test turns them on through <paramref name="settings"/>.
/// </summary>
public sealed class ApiHost(TestHarness harness, IClock? clock = null, IReadOnlyDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    public SimulatedIdp Idp { get; } = new();

    public TestHarness Harness { get; } = harness;

    /// <summary>Everything the host logged at Warning or above.</summary>
    public ConcurrentQueue<(LogLevel Level, string Message)> Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Test");
        var values = new Dictionary<string, string?>
        {
            ["Rochell:AppConnectionString"] = Harness.Database.AppConnectionString,
            ["Rochell:SealerConnectionString"] = Harness.Database.SealerConnectionString,
            ["Rochell:Identity:HostedDomain"] = TestHarness.HostedDomain,
            ["Rochell:Oidc:Authority"] = SimulatedIdp.Issuer,
            ["Rochell:Oidc:ClientId"] = SimulatedIdp.ClientId,
            ["Rochell:Oidc:ClientSecret"] = SimulatedIdp.ClientSecret,
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            values[key] = value;
        }

        foreach (var (key, value) in values)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.Configure<OpenIdConnectOptions>(OidcAuthentication.Scheme, o => o.BackchannelHttpHandler = Idp.Backchannel);
            services.AddSingleton<ILoggerProvider>(new CollectingLoggerProvider(Logs));
            if (clock is not null)
            {
                services.AddSingleton(clock);
            }
        });
    }

    /// <summary>A browser without a session: no automatic redirects, cookies kept, HTTPS (the session cookie is Secure).</summary>
    public HttpClient Browser()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add(CsrfHeader.Name, CsrfHeader.Value);
        return client;
    }

    /// <summary>Signs the user in through the whole OIDC flow and returns the browser holding the session cookie.</summary>
    public async Task<HttpClient> SignInAsync(Guid userId)
    {
        var browser = Browser();
        var callback = await StartAsync(browser, "/api/v1/auth/login?returnUrl=/inicio", userId);
        var done = await browser.GetAsync(callback);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        return browser;
    }

    /// <summary>The user behind a session created by the fixtures (setups return session ids).</summary>
    public async Task<HttpClient> SignInAsSessionUserAsync(Guid fixtureSessionId)
        => await SignInAsync(await Harness.ScalarAsync<Guid>("SELECT user_id FROM iam.session WHERE session_id = @s", ("s", fixtureSessionId)));

    /// <summary>Follows the API's redirect to the IdP, signs in there, and returns the callback URL (relative) the IdP redirects to.</summary>
    public async Task<string> StartAsync(HttpClient browser, string path, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(browser);
        Idp.AddAccount(TestHarness.ClaimsOf(userId));
        var challenge = await browser.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        return Idp.Authorize(challenge.Headers.Location!, TestHarness.SubjectOf(userId)).PathAndQuery;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Idp.Dispose();
        }
    }
}

internal sealed class CollectingLoggerProvider(ConcurrentQueue<(LogLevel Level, string Message)> logs) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Collector(logs);

    public void Dispose()
    {
    }

    private sealed class Collector(ConcurrentQueue<(LogLevel Level, string Message)> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                logs.Enqueue((logLevel, formatter(state, exception)));
            }
        }
    }
}

/// <summary>JSON over HTTP with the API conventions (decimals as strings).</summary>
public static class ApiCalls
{
    public static async Task<HttpResponseMessage> CommandAsync(this HttpClient client, Guid companyId, string module, string command, object body, string? idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/companies/{companyId}/{module}/{command}")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, ApiJson.Options), Encoding.UTF8, "application/json"),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add(CommandRunner.IdempotencyKeyHeader, idempotencyKey);
        }

        return await client.SendAsync(request);
    }

    /// <summary>Runs a command that must succeed; returns its response envelope.</summary>
    public static async Task<JsonElement> OkAsync(this HttpClient client, Guid companyId, string module, string command, object body, string? idempotencyKey = null)
    {
        var response = await client.CommandAsync(companyId, module, command, body, idempotencyKey ?? Guid.NewGuid().ToString());
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{command}: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    public static async Task<JsonElement> GetOkAsync(this HttpClient client, string path)
    {
        ArgumentNullException.ThrowIfNull(client);
        var response = await client.GetAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path}: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    /// <summary>The problem+json of a failed call: status and domain code.</summary>
    public static async Task<(HttpStatusCode Status, string? Code)> ProblemAsync(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response.StatusCode, problem.TryGetProperty("code", out var code) ? code.GetString() : null);
    }
}
