using Microsoft.Extensions.Configuration;
using Npgsql;
using Rochell.Migrations;
using Rochell.Platform.Hosting;

// Usage: rochell-migrate <migrate|verify|status|init-environment TEST|PRODUCTION>
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

        case "init-environment":
            {
                // Patch 1.1, correction 3: written once per database by the deployment role; never changed.
                var value = args.Length > 1 ? args[1] : string.Empty;
                if (value is not ("TEST" or "PRODUCTION"))
                {
                    await Console.Error.WriteLineAsync("Usage: rochell-migrate init-environment TEST|PRODUCTION");
                    return 1;
                }

                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                await using var insert = new NpgsqlCommand(
                    """
                    INSERT INTO core.deployment_environment (environment, set_by, set_at)
                    VALUES (@environment, current_user, now())
                    ON CONFLICT (singleton) DO NOTHING
                    """,
                    connection);
                insert.Parameters.AddWithValue("environment", value);
                await insert.ExecuteNonQueryAsync();

                await using var select = new NpgsqlCommand("SELECT environment FROM core.deployment_environment", connection);
                var current = (string?)await select.ExecuteScalarAsync();
                if (current != value)
                {
                    await Console.Error.WriteLineAsync($"Deployment environment is already '{current}' and cannot be changed to '{value}'.");
                    return 2;
                }

                Console.WriteLine($"Deployment environment: {current}.");
                return 0;
            }

        default:
            await Console.Error.WriteLineAsync("Usage: rochell-migrate <migrate|verify|status|init-environment TEST|PRODUCTION>");
            return 1;
    }
}
catch (MigrationException ex)
{
    await Console.Error.WriteLineAsync(ex.Message);
    return 2;
}
