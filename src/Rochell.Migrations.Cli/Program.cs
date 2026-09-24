using Microsoft.Extensions.Configuration;
using Npgsql;
using Rochell.Migrations;
using Rochell.Platform.Hosting;

// Usage: rochell-migrate <migrate|verify|status|init-environment TEST|PRODUCTION>
//        rochell-migrate create-user <email> <google-oidc-subject> <employee-id>       (E-PR03-5)
//        rochell-migrate grant-role <email> <ROLE_CODE> <company-rnc> [plant-id]         (bootstrap grants, E-PR03-5)
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

        case "create-user":
            {
                // E-PR03-5: in VS#1 human users are provisioned by the deployment role.
                if (args.Length != 4 || !Guid.TryParse(args[3], out var employeeId))
                {
                    await Console.Error.WriteLineAsync("Usage: rochell-migrate create-user <email> <google-oidc-subject> <employee-id>");
                    return 1;
                }

                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                await using var insert = new NpgsqlCommand(
                    """
                    INSERT INTO iam.user (user_id, kind, employee_id, email, oidc_subject, status)
                    VALUES (@id, 'HUMAN', @employee, lower(@email), @subject, 'ACTIVE')
                    """,
                    connection);
                var userId = Guid.CreateVersion7();
                insert.Parameters.AddWithValue("id", userId);
                insert.Parameters.AddWithValue("employee", employeeId);
                insert.Parameters.AddWithValue("email", args[1]);
                insert.Parameters.AddWithValue("subject", args[2]);
                await insert.ExecuteNonQueryAsync();
                Console.WriteLine($"User {args[1].ToLowerInvariant()} created: {userId}.");
                return 0;
            }

        case "grant-role":
            {
                // E-PR03-5: bootstrap grants (e.g. the first security administrators) by the deployment role.
                // Segregation-of-duties rules still apply (database trigger). Regular changes use RequestRoleAssignment.
                if (args.Length is < 4 or > 5)
                {
                    await Console.Error.WriteLineAsync("Usage: rochell-migrate grant-role <email> <ROLE_CODE> <company-rnc> [plant-id]");
                    return 1;
                }

                Guid? plantId = null;
                if (args.Length == 5)
                {
                    if (!Guid.TryParse(args[4], out var parsedPlant))
                    {
                        await Console.Error.WriteLineAsync("plant-id must be a UUID.");
                        return 1;
                    }

                    plantId = parsedPlant;
                }

                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                await using var grant = new NpgsqlCommand(
                    """
                    INSERT INTO iam.role_assignment (assignment_id, company_id, user_id, role_id, plant_id, valid_from, granted_by)
                    SELECT @id, c.company_id, u.user_id, r.role_id, @plant, now(), '00000000-0000-7000-8000-00000000d001'
                    FROM iam.user u, iam.role r, md.company c
                    WHERE u.email = lower(@email) AND r.code = @role AND c.rnc = @rnc
                    """,
                    connection);
                grant.Parameters.AddWithValue("id", Guid.CreateVersion7());
                grant.Parameters.AddWithValue("plant", (object?)plantId ?? DBNull.Value);
                grant.Parameters.AddWithValue("email", args[1]);
                grant.Parameters.AddWithValue("role", args[2]);
                grant.Parameters.AddWithValue("rnc", args[3]);
                if (await grant.ExecuteNonQueryAsync() != 1)
                {
                    await Console.Error.WriteLineAsync("User, role or company not found.");
                    return 2;
                }

                Console.WriteLine($"Role {args[2]} granted to {args[1].ToLowerInvariant()} in company {args[3]}.");
                return 0;
            }

        default:
            await Console.Error.WriteLineAsync("Usage: rochell-migrate <migrate|verify|status|init-environment TEST|PRODUCTION|create-user|grant-role>");
            return 1;
    }
}
catch (MigrationException ex)
{
    await Console.Error.WriteLineAsync(ex.Message);
    return 2;
}
catch (PostgresException ex)
{
    // e.g. SOD_CONFLICT, duplicate e-mail or subject, missing employee.
    await Console.Error.WriteLineAsync(ex.MessageText);
    return 2;
}
