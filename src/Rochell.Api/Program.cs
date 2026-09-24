using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;
using Rochell.Api.Auth;
using Rochell.Api.Endpoints;
using Rochell.Api.Hosting;
using Rochell.Api.Http;
using Rochell.Identity;
using Rochell.Identity.Authorization;
using Rochell.Identity.Sessions;
using Rochell.Platform.Commands;
using Rochell.Platform.Hosting;
using Rochell.Platform.Json;
using Rochell.Platform.Observability;
using Rochell.Platform.Queries;
using Rochell.Platform.Time;

// PR-18a (E-PR18-1…7): HTTP transport over the command and query pipelines. The host adds no business rules.
var builder = WebApplication.CreateBuilder(args);
RochellEnvironments.EnsureSupported(builder.Environment.EnvironmentName);

var settings = builder.Configuration.GetSection(ApiOptions.Section).Get<ApiOptions>() ?? new ApiOptions();
var appConnection = Required(settings.AppConnectionString, "AppConnectionString");
var hostedDomain = Required(settings.Identity.HostedDomain, "Identity:HostedDomain");
Required(settings.Oidc.Authority, "Oidc:Authority");
Required(settings.Oidc.ClientId, "Oidc:ClientId");
Required(settings.Oidc.ClientSecret, "Oidc:ClientSecret");

var services = builder.Services;
services.AddSingleton(settings.Sealer);
services.AddSingleton(settings.Digest);
services.AddSingleton(settings.Audit);
services.AddSingleton<IClock>(SystemClock.Instance);
services.AddSingleton(new AppDatabase(NpgsqlDataSource.Create(appConnection)));
services.AddSingleton(new IdentityOptions
{
    HostedDomain = hostedDomain,
    StepUpMaxAge = settings.Identity.StepUpMaxAge,
    SessionAbsoluteLifetime = settings.Identity.SessionAbsoluteLifetime,
    SessionIdleTimeout = settings.Identity.SessionIdleTimeout,
});
services.AddSingleton<ICommandAuthorizer>(sp => new SqlCommandAuthorizer(sp.GetRequiredService<IdentityOptions>(), sp.GetRequiredService<IClock>()));
services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<RequestLogWriter>>();
    return new RequestLogWriter(sp.GetRequiredService<AppDatabase>().DataSource, ex => logger.LogError(ex, "request_log batch dropped."));
});
services.AddSingleton<IRequestLogSink>(sp => sp.GetRequiredService<RequestLogWriter>());
services.AddSingleton(sp => new CommandPipeline(
    sp.GetRequiredService<AppDatabase>().DataSource, sp.GetRequiredService<ICommandAuthorizer>(), sp.GetRequiredService<IRequestLogSink>(), sp.GetRequiredService<IClock>()));
services.AddSingleton(sp => new QueryPipeline(sp.GetRequiredService<AppDatabase>().DataSource, sp.GetRequiredService<ICommandAuthorizer>(), sp.GetRequiredService<IClock>()));
services.AddSingleton(sp => new SessionService(sp.GetRequiredService<AppDatabase>().DataSource, sp.GetRequiredService<IdentityOptions>(), sp.GetRequiredService<IClock>()));
services.AddSingleton<SessionCookie>();
services.AddSingleton<CommandRunner>();
services.AddSingleton<QueryRunner>();
services.AddSingleton<WormAccess>();
services.AddSingleton<HashVerification>();
services.AddCommandHandlers();
services.AddQueryHandlers();
services.AddHostedService<RequestLogFlusher>();

// E-PR18-5: the sealer and the digest connect as rochell_sealer and are switched on by configuration.
if (settings.Sealer.Enabled || settings.Digest.Enabled)
{
    services.AddSingleton(new SealerDatabase(NpgsqlDataSource.Create(Required(settings.SealerConnectionString, "SealerConnectionString"))));
    services.AddHostedService<SealerService>();
    services.AddHostedService<DigestService>();
}

var dataProtection = services.AddDataProtection().SetApplicationName("Rochell.Api");
if (!string.IsNullOrWhiteSpace(settings.DataProtectionKeysPath))
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(settings.DataProtectionKeysPath));
}

services.AddProblemDetails();
services.ConfigureHttpJsonOptions(options => ApiJson.Configure(options.SerializerOptions));
services.AddRochellOidc(settings.Oidc, hostedDomain);
services.AddRochellOpenApi();

if (builder.Environment.IsDevelopment())
{
    // `next dev` forwards /api and the sign-in pages from its own origin (E-PR18b-2): honour X-Forwarded-* from loopback only.
    services.Configure<ForwardedHeadersOptions>(options => options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost);
}

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.UseForwardedHeaders();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCorrelation();
if (!string.IsNullOrWhiteSpace(settings.WebRoot))
{
    app.UseWebAssets(settings.WebRoot);
}

app.UseCsrfHeader();
app.UseAuthentication();

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment(RochellEnvironments.Test))
{
    app.MapOpenApi("/openapi/{documentName}.json");
}

app.MapAuthEndpoints();
var company = app.MapGroup("/api/v1/companies/{companyId:guid}");
company.MapCommandEndpoints();
company.MapQueryEndpoints();

await app.RunAsync();

static string Required(string? value, string key)
    => string.IsNullOrWhiteSpace(value)
        ? throw new InvalidOperationException($"Configuration {ApiOptions.Section}:{key} is required (environment variable {ApiOptions.Section}__{key.Replace(":", "__", StringComparison.Ordinal)}).")
        : value;

/// <summary>Entry point, visible to the end-to-end tests (WebApplicationFactory).</summary>
public partial class Program;
