using System.Globalization;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Rochell.Api.Auth;
using Rochell.TestInfrastructure;
using Rochell.Testing.Oidc;

namespace Rochell.DevStack;

/// <summary>
/// E-PR18b-9: a complete local stack without Google or a shared database. PostgreSQL 17 in Docker (Testcontainers), all
/// migrations, a seeded company with every slice role, the simulated IdP with a user picker, and the real API host on
/// Kestrel serving the web export. Development and the Playwright journey only.
///
///   dotnet run --project tests/Rochell.DevStack -- [--port 5080] [--web-root web/out] [--public-origin http://localhost:3000]
///
/// --public-origin is the origin the browser uses (next dev on :3000 forwards /api and /dev-idp here).
/// </summary>
internal static class DevStackProgram
{
    public static async Task Main(string[] args)
    {
        var port = int.Parse(Arg("--port") ?? "5080", CultureInfo.InvariantCulture);
        var webRoot = Arg("--web-root");
        var publicOrigin = (Arg("--public-origin") ?? $"http://localhost:{port}").TrimEnd('/');

        // WebApplicationFactory looks for the API's content root next to the solution; it lives under src/.
        Environment.SetEnvironmentVariable("ASPNETCORE_TEST_CONTENTROOT_ROCHELL_API", Path.Combine(RepositoryRoot(), "src", "Rochell.Api"));

        var postgres = new PostgresFixture();
        await postgres.InitializeAsync();
        try
        {
            await using var harness = await TestHarness.CreateAsync(postgres);
            var accounts = await DevSeed.RunAsync(harness);
            using var idp = new SimulatedIdp(publicOrigin + SimulatedIdp.BrowserPath);
            foreach (var account in accounts)
            {
                idp.AddAccount(TestHarness.ClaimsOf(account.UserId), account.Label);
            }

            using var host = new DevApiHost(harness, idp, webRoot is null ? null : Path.GetFullPath(webRoot));
            host.UseKestrel(port);
            host.StartServer();

            Console.WriteLine($"Rochell dev stack listening on http://localhost:{port} (browser origin {publicOrigin}).");
            Console.WriteLine($"Company {harness.CompanyId}; sign in at {publicOrigin}/ with one of:");
            foreach (var account in accounts)
            {
                Console.WriteLine("  - " + account.Label);
            }

            var stop = new TaskCompletionSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.TrySetResult();
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
            await stop.Task;
        }
        finally
        {
            await postgres.DisposeAsync();
        }

        static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Rochell.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("Repository root (Rochell.slnx) not found.");
        }

        string? Arg(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }
}

/// <summary>The production API host in Development with the simulated IdP (backchannel and user picker) and the web export.</summary>
internal sealed class DevApiHost(TestHarness harness, SimulatedIdp idp, string? webRoot) : WebApplicationFactory<global::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Logging:LogLevel:Default", "Information");
        builder.UseSetting("Rochell:AppConnectionString", harness.Database.AppConnectionString);
        builder.UseSetting("Rochell:SealerConnectionString", harness.Database.SealerConnectionString);
        builder.UseSetting("Rochell:Sealer:Enabled", "true");
        builder.UseSetting("Rochell:Identity:HostedDomain", TestHarness.HostedDomain);
        builder.UseSetting("Rochell:Oidc:Authority", SimulatedIdp.Issuer);
        builder.UseSetting("Rochell:Oidc:ClientId", SimulatedIdp.ClientId);
        builder.UseSetting("Rochell:Oidc:ClientSecret", SimulatedIdp.ClientSecret);
        if (webRoot is not null)
        {
            builder.UseSetting("Rochell:WebRoot", webRoot);
        }

        builder.ConfigureTestServices(services =>
        {
            services.Configure<OpenIdConnectOptions>(OidcAuthentication.Scheme, o => o.BackchannelHttpHandler = idp.Backchannel);
            services.AddSingleton<IStartupFilter>(new IdpPage(idp));
        });
    }
}

/// <summary>Mounts the IdP's user picker in front of the API pipeline.</summary>
internal sealed class IdpPage(SimulatedIdp idp) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        => app =>
        {
            app.Map(SimulatedIdp.BrowserPath, branch => branch.Run(idp.HandleBrowserAsync));
            next(app);
        };
}
