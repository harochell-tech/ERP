using Microsoft.Extensions.Configuration;
using Npgsql;
using Rochell.Migrations;
using Rochell.Platform.Hosting;

// Usage: rochell-migrate <migrate|verify|status>
// Environment: DOTNET_ENVIRONMENT = Development | Test | Staging
// Connection:  ConnectionStrings:Rochell (appsettings.Development.json or env var ConnectionStrings__Rochell).
//              Must use the deployment role (schema owner), never the application role.
var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? RochellEnvironments.Development;
RochellEnvironments.EnsureSupported(environment);

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile($"appsettings.{environment}.json", optional: false)
    .AddEnvironmentVariables()
    .Build();

var command = args.Length > 0 ? args[0] : "status";
var connectionString = configuration.GetConnectionString("Rochell");
if (string.IsNullOrWhiteSpace(connectionString))
{
    await Console.Error.WriteLineAsync("Missing connection string 'ConnectionStrings:Rochell' (set ConnectionStrings__Rochell).");
    return 1;
}

var source = new MigrationSource(MigrationSource.Main, Path.Combine(AppContext.BaseDirectory, "migrations", MigrationSource.Main));
var runner = new MigrationRunner(() => new NpgsqlConnection(connectionString), Console.WriteLine);

try
{
    switch (command)
    {
        case "migrate":
            {
                var result = await runner.MigrateAsync(source);
                Console.WriteLine($"Environment {environment}: {result.AppliedNow.Count} migration(s) applied, {result.AlreadyApplied} already applied.");
                return 0;
            }

        case "verify":
        case "status":
            {
                var result = await runner.VerifyAsync(source);
                Console.WriteLine($"Environment {environment}: {result.AlreadyApplied} applied, checksums OK, {result.Pending.Count} pending.");
                foreach (var pending in result.Pending)
                {
                    Console.WriteLine($"  pending: {pending}");
                }

                return 0;
            }

        default:
            await Console.Error.WriteLineAsync("Usage: rochell-migrate <migrate|verify|status>");
            return 1;
    }
}
catch (MigrationException ex)
{
    await Console.Error.WriteLineAsync(ex.Message);
    return 2;
}
