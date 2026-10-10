using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;
using Rochell.Api.Auth;
using Rochell.Api.Deliveries;
using Rochell.Api.Quality;
using Rochell.Api.Ecf;
using Rochell.Api.Endpoints;
using Rochell.Api.Hosting;
using Rochell.Api.Http;
using Rochell.Api.Mail;
using Rochell.Identity;
using Rochell.Identity.Authorization;
using Rochell.Identity.Sessions;
using Rochell.Manufacturing.Portal;
using Rochell.Platform.Commands;
using Rochell.Platform.Hosting;
using Rochell.Platform.Json;
using Rochell.Platform.Mail;
using Rochell.Platform.Observability;
using Rochell.Platform.Queries;
using Rochell.Platform.Time;
using Rochell.Tax.Ecf;

// PR-18a (E-PR18-1…7): HTTP transport over the command and query pipelines. The host adds no business rules.
var builder = WebApplication.CreateBuilder(args);
RochellEnvironments.EnsureSupported(builder.Environment.EnvironmentName);

var settings = builder.Configuration.GetSection(ApiOptions.Section).Get<ApiOptions>() ?? new ApiOptions();
settings.Digest.SigningKeyPem ??= FromFile(settings.Digest.SigningKeyPemFile, "Digest:SigningKeyPemFile");
settings.Audit.DigestPublicKeyPem ??= FromFile(settings.Audit.DigestPublicKeyPemFile, "Audit:DigestPublicKeyPemFile");
var appConnection = Required(settings.AppConnectionString, "AppConnectionString");
var hostedDomain = Required(settings.Identity.HostedDomain, "Identity:HostedDomain");
Required(settings.Oidc.Authority, "Oidc:Authority");
Required(settings.Oidc.ClientId, "Oidc:ClientId");
Required(settings.Oidc.ClientSecret, "Oidc:ClientSecret");

var services = builder.Services;
services.AddSingleton(settings.Sealer);
services.AddSingleton(settings.Digest);
services.AddSingleton(settings.FiscalExpiry);
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
services.AddSingleton<FiscalExpiryService>();
services.AddHostedService(sp => sp.GetRequiredService<FiscalExpiryService>());

// E-MAIL-01-4: outgoing mail is Off unless configured; Redirect and Live need the sender, the relay and the PDF renderer.
services.AddSingleton(settings.Mail);
services.AddSingleton(new MailSwitch(settings.Mail.Mode != MailMode.Off));
if (settings.Mail.Mode != MailMode.Off)
{
    Required(settings.Mail.FromAddress, "Mail:FromAddress");
    Required(settings.Mail.Smtp.Host, "Mail:Smtp:Host");
    Required(settings.Mail.RendererUrl, "Mail:RendererUrl");
    if (settings.Mail.Mode == MailMode.Redirect)
    {
        Required(settings.Mail.RedirectTo, "Mail:RedirectTo");
    }

    services.AddSingleton<IMailTransport, SmtpMailTransport>();
    services.AddHttpClient<IPdfRenderer, GotenbergPdfRenderer>(client => client.Timeout = TimeSpan.FromSeconds(60));
    services.AddHostedService<MailService>();
}

// E-VS4-11, E-VS4-02-6: the e-CF gateway is Off unless configured; Sandbox / Production call Alanube with the server's token; Simulated
// only where the environment is Development or Test.
// E-VS4-04-7: the token and the webhook secret live in files on the server, never in the repository or the .env.
if (settings.Ecf.Enabled && string.IsNullOrWhiteSpace(settings.Ecf.Token) && !string.IsNullOrWhiteSpace(settings.Ecf.TokenFile) && settings.Ecf.Mode != EcfModes.Simulated)
{
    settings.Ecf.Token = FromFile(settings.Ecf.TokenFile, "Ecf:TokenFile")!.Trim();
}

if (string.IsNullOrWhiteSpace(settings.Ecf.WebhookSecret) && !string.IsNullOrWhiteSpace(settings.Ecf.WebhookSecretFile) && File.Exists(settings.Ecf.WebhookSecretFile))
{
    settings.Ecf.WebhookSecret = File.ReadAllText(settings.Ecf.WebhookSecretFile).Trim();
}

// E-VS4-06-1: the sandbox's test RNC never reaches Production — only a SANDBOX deployment may set it.
if (!string.IsNullOrWhiteSpace(settings.Ecf.SandboxSenderRnc) && settings.Ecf.Mode != EcfModes.Sandbox)
{
    throw new InvalidOperationException($"Ecf:SandboxSenderRnc is only for Ecf:Mode SANDBOX, not {settings.Ecf.Mode} (E-VS4-06-1).");
}

services.AddSingleton(settings.Ecf);
services.AddSingleton(new EcfSwitch(settings.Ecf.Enabled));
services.AddSingleton<IEcfSourceUpdater, Rochell.Sales.Ecf.InvoiceEcfUpdater>();
services.AddSingleton<IEcfSourceUpdater, Rochell.Sales.Ecf.CreditNoteEcfUpdater>();
switch (settings.Ecf.Mode)
{
    case EcfModes.Off:
        services.AddSingleton<IEcfProvider>(OffEcfProvider.Instance);
        services.AddSingleton<IEcfReception>(OffEcfProvider.Instance);
        break;
    case EcfModes.Simulated:
        if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment(RochellEnvironments.Test))
        {
            throw new InvalidOperationException("Ecf:Mode SIMULATED is only for Development and Test (E-VS4-02-6).");
        }

        services.AddSingleton<SimulatedEcfProvider>();
        services.AddSingleton<IEcfProvider>(sp => sp.GetRequiredService<SimulatedEcfProvider>());
        services.AddSingleton<IEcfReception>(sp => sp.GetRequiredService<SimulatedEcfProvider>());
        break;
    case EcfModes.Sandbox or EcfModes.Production:
        Required(settings.Ecf.BaseUrl, "Ecf:BaseUrl");
        Required(settings.Ecf.Token, "Ecf:Token");
        services.AddHttpClient<AlanubeProvider>(client => client.Timeout = settings.Ecf.CallTimeout + TimeSpan.FromSeconds(5));
        services.AddTransient<IEcfProvider>(sp => sp.GetRequiredService<AlanubeProvider>());
        services.AddTransient<IEcfReception>(sp => sp.GetRequiredService<AlanubeProvider>());
        break;
    default:
        throw new InvalidOperationException($"Ecf:Mode is OFF, SANDBOX, PRODUCTION or SIMULATED, not {settings.Ecf.Mode}.");
}

if (settings.Ecf.Enabled)
{
    services.AddSingleton<EcfService>();
    services.AddHostedService(sp => sp.GetRequiredService<EcfService>());

    // OCR1-02 (E-OCR1-01-9, E-OCR1-02-3): suppliers' e-CF read every hour, and the commercial responses sent.
    services.AddSingleton<ReceivedDocumentsService>();
    services.AddHostedService(sp => sp.GetRequiredService<ReceivedDocumentsService>());
}

services.AddSingleton<ReceptionNudge>();

// MFG2-02 (E-MFG2-1): the machines' portal, read every 15 min when configured; its key is a file on the server.
services.AddSingleton(settings.Portal);
if (settings.Portal.Enabled)
{
    settings.Portal.Token ??= FromFile(settings.Portal.TokenFile, "Portal:TokenFile")?.Trim();
    Required(settings.Portal.Token, "Portal:Token");
    services.AddHttpClient<IPortalSource, PortalHttpSource>(client => client.Timeout = TimeSpan.FromSeconds(60));
    services.AddSingleton<PortalService>();
    services.AddHostedService(sp => sp.GetRequiredService<PortalService>());
}
else
{
    services.AddSingleton<IPortalSource, NoPortalSource>();
}

// ENT1-02 (E-ENT-1/5, E-ENT1-01-2): the drivers' page. Off without the link key; with it, the evidence store is required — the private
// B2 bucket (its key id and secret as files on the server), or a folder in Development and Test.
var linkKey = settings.Deliveries.LinkKey
    ?? (!string.IsNullOrWhiteSpace(settings.Deliveries.LinkKeyFile) && File.Exists(settings.Deliveries.LinkKeyFile) ? File.ReadAllText(settings.Deliveries.LinkKeyFile).Trim() : null);
// The evidence store (ENT-1 photos and signatures, OCR1-04 invoice photos): the private B2 bucket, or a folder in Development and Test.
var evidenceStore = false;
if (!string.IsNullOrWhiteSpace(settings.Deliveries.Evidence.Bucket))
{
    Required(settings.Deliveries.Evidence.AccessKeyIdFile, "Deliveries:Evidence:AccessKeyIdFile");
    Required(settings.Deliveries.Evidence.SecretAccessKeyFile, "Deliveries:Evidence:SecretAccessKeyFile");
    services.AddSingleton<Rochell.Platform.Files.IEvidenceStore>(_ => Rochell.Api.Deliveries.S3EvidenceStore.Create(settings.Deliveries.Evidence));
    evidenceStore = true;
}
else if (!string.IsNullOrWhiteSpace(settings.Deliveries.EvidenceRoot) && (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment(RochellEnvironments.Test)))
{
    services.AddSingleton<Rochell.Platform.Files.IEvidenceStore>(new Rochell.Api.Deliveries.FileSystemEvidenceStore(settings.Deliveries.EvidenceRoot));
    evidenceStore = true;
}

if (!string.IsNullOrWhiteSpace(linkKey))
{
    services.AddSingleton(new Rochell.Sales.Deliveries.DriverLinkKey(Convert.FromBase64String(linkKey)));
    if (!evidenceStore)
    {
        throw new InvalidOperationException("Deliveries:LinkKey is set but no evidence store: configure Deliveries:Evidence (B2) or, in Development / Test, Deliveries:EvidenceRoot.");
    }
}

// OCR1-04 (E-OCR-5, E-OCR1-01-10): reading invoice photos by AI, Off by default; the key is a file on the server.
services.AddSingleton(settings.Ocr);
switch (settings.Ocr.Mode)
{
    case Rochell.Api.Ocr.OcrModes.Off:
        break;
    case Rochell.Api.Ocr.OcrModes.Simulated:
        if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment(RochellEnvironments.Test))
        {
            throw new InvalidOperationException("Ocr:Mode SIMULATED is only for Development and Test.");
        }

        services.AddSingleton<Rochell.Procurement.SupplierDocuments.SimulatedSupplierDocumentReader>();
        services.AddSingleton<Rochell.Procurement.SupplierDocuments.ISupplierDocumentReader>(sp => sp.GetRequiredService<Rochell.Procurement.SupplierDocuments.SimulatedSupplierDocumentReader>());
        break;
    case Rochell.Api.Ocr.OcrModes.Anthropic:
        settings.Ocr.ApiKey ??= FromFile(settings.Ocr.ApiKeyFile, "Ocr:ApiKeyFile")?.Trim();
        Required(settings.Ocr.ApiKey, "Ocr:ApiKey");
        services.AddHttpClient<Rochell.Procurement.SupplierDocuments.ISupplierDocumentReader, Rochell.Api.Ocr.AnthropicSupplierDocumentReader>(
            client => client.Timeout = settings.Ocr.Timeout + TimeSpan.FromSeconds(10));
        break;
    default:
        throw new InvalidOperationException($"Ocr:Mode is OFF, ANTHROPIC or SIMULATED, not {settings.Ocr.Mode}.");
}

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

var behindProxy = settings.ReverseProxy.TrustedNetworks.Count > 0;
if (behindProxy)
{
    // E-B03-2: behind Caddy the scheme (https, for the OIDC redirect and Secure cookies) and client address come from the proxy,
    // trusted only from its own networks.
    services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var network in settings.ReverseProxy.TrustedNetworks)
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        }
    });
}
else if (builder.Environment.IsDevelopment())
{
    // `next dev` forwards /api and the sign-in pages from its own origin (E-PR18b-2): honour X-Forwarded-* from loopback only.
    services.Configure<ForwardedHeadersOptions>(options => options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost);
}

var app = builder.Build();
if (behindProxy || app.Environment.IsDevelopment())
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
app.MapGet("/api/v1/environment", () => Results.Json(new EnvironmentInfo(string.IsNullOrWhiteSpace(settings.EnvironmentBadge) ? null : settings.EnvironmentBadge.Trim(), settings.Mail.Mode.ToString().ToUpperInvariant(), settings.Ocr.Enabled)))
    .WithTags("Auth")
    .WithName("GetEnvironment")
    .WithSummary("E-PAR-3: the deployment's label for the top bar (null: the web decides from the host name) and, E-MAIL-01-4, whether it sends mail (OFF, REDIRECT, LIVE). No sign-in needed.")
    .Produces<EnvironmentInfo>();
var company = app.MapGroup("/api/v1/companies/{companyId:guid}");
app.MapEcfWebhook();
app.MapDriverPages(); // ENT1-02
app.MapCertificatePages(); // LAB1-03c
company.MapCommandEndpoints();
company.MapQueryEndpoints();

await app.RunAsync();

static string Required(string? value, string key)
    => string.IsNullOrWhiteSpace(value)
        ? throw new InvalidOperationException($"Configuration {ApiOptions.Section}:{key} is required (environment variable {ApiOptions.Section}__{key.Replace(":", "__", StringComparison.Ordinal)}).")
        : value;

// E-B03-6: keys kept in files on the server. A configured file that cannot be read stops the host.
static string? FromFile(string? path, string key)
    => string.IsNullOrWhiteSpace(path)
        ? null
        : File.Exists(path)
            ? File.ReadAllText(path)
            : throw new InvalidOperationException($"Configuration {ApiOptions.Section}:{key} points to {path}, which does not exist.");

/// <summary>Entry point, visible to the end-to-end tests (WebApplicationFactory).</summary>
public partial class Program;

/// <summary>E-PAR-3: what the web shows in its top bar for this deployment.</summary>
/// <remarks>OCR1-04: <paramref name="OcrEnabled"/> — whether invoice photos are read by AI here (the button is shown only then).</remarks>
public sealed record EnvironmentInfo(string? Badge, string MailMode, bool OcrEnabled = false);
