using Microsoft.Extensions.Configuration;
using Npgsql;
using Rochell.Migrations;
using Rochell.Platform.Hosting;

// Usage: rochell-migrate <migrate|verify|status|init-environment TEST|PRODUCTION>
//        rochell-migrate create-user <email> <google-oidc-subject> <employee-id>       (E-PR03-5)
//        rochell-migrate grant-role <email> <ROLE_CODE> <company-rnc> [plant-id]         (bootstrap grants, E-PR03-5)
//        rochell-migrate create-plant <company-rnc> <PLANT_CODE> <VALUATION_AREA_CODE>    (E-PR04-2)
//        rochell-migrate create-location <company-rnc> <PLANT_CODE> <LOCATION_CODE>       (E-PR04-2)
//        rochell-migrate import-accounts <company-rnc> <accounts.csv>                     (E-PR05-1; code,name,is_control)
//        rochell-migrate import-account-map <company-rnc> <map.csv>                       (DRAFT maps; role,category,account_code,effective_from)
//        rochell-migrate open-periods <company-rnc> <year>                                (E-PR05-3)
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

        case "create-plant":
            {
                // E-PR04-2: plants (each with its own valuation area) are created by the deployment role in VS#1.
                if (args.Length != 4)
                {
                    await Console.Error.WriteLineAsync("Usage: rochell-migrate create-plant <company-rnc> <PLANT_CODE> <VALUATION_AREA_CODE>");
                    return 1;
                }

                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                await using var create = new NpgsqlCommand(
                    """
                    WITH company AS (SELECT company_id FROM md.company WHERE rnc = @rnc),
                         area AS (
                           INSERT INTO md.valuation_area (company_id, valuation_area_id, code)
                           SELECT company_id, @area_id, upper(@area_code) FROM company
                           RETURNING company_id, valuation_area_id)
                    INSERT INTO md.plant (plant_id, company_id, code, valuation_area_id)
                    SELECT @plant_id, company_id, upper(@plant_code), valuation_area_id FROM area
                    """,
                    connection);
                var plantId = Guid.CreateVersion7();
                create.Parameters.AddWithValue("rnc", args[1]);
                create.Parameters.AddWithValue("area_id", Guid.CreateVersion7());
                create.Parameters.AddWithValue("area_code", args[3]);
                create.Parameters.AddWithValue("plant_id", plantId);
                create.Parameters.AddWithValue("plant_code", args[2]);
                if (await create.ExecuteNonQueryAsync() != 1)
                {
                    await Console.Error.WriteLineAsync("Company not found.");
                    return 2;
                }

                Console.WriteLine($"Plant {args[2].ToUpperInvariant()} created: {plantId}.");
                return 0;
            }

        case "create-location":
            {
                if (args.Length != 4)
                {
                    await Console.Error.WriteLineAsync("Usage: rochell-migrate create-location <company-rnc> <PLANT_CODE> <LOCATION_CODE>");
                    return 1;
                }

                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                await using var create = new NpgsqlCommand(
                    """
                    INSERT INTO md.location (location_id, company_id, plant_id, code)
                    SELECT @id, p.company_id, p.plant_id, upper(@code)
                    FROM md.plant p JOIN md.company c ON c.company_id = p.company_id
                    WHERE c.rnc = @rnc AND p.code = upper(@plant)
                    """,
                    connection);
                create.Parameters.AddWithValue("id", Guid.CreateVersion7());
                create.Parameters.AddWithValue("code", args[3]);
                create.Parameters.AddWithValue("rnc", args[1]);
                create.Parameters.AddWithValue("plant", args[2]);
                if (await create.ExecuteNonQueryAsync() != 1)
                {
                    await Console.Error.WriteLineAsync("Company or plant not found.");
                    return 2;
                }

                Console.WriteLine($"Location {args[3].ToUpperInvariant()} created in plant {args[2].ToUpperInvariant()}.");
                return 0;
            }

        case "import-accounts":
            {
                // E-PR05-1: chart of accounts approved by the Controller, loaded by the deployment role. CSV: code,name,is_control
                if (args.Length != 3 || !File.Exists(args[2]))
                {
                    await Console.Error.WriteLineAsync("Usage: rochell-migrate import-accounts <company-rnc> <accounts.csv>");
                    return 1;
                }

                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                await using var tx = await connection.BeginTransactionAsync();
                var imported = 0;
                foreach (var fields in CsvRows(args[2], "code"))
                {
                    if (fields.Length < 3 || !bool.TryParse(fields[^1], out var isControl))
                    {
                        throw new FormatException($"Invalid account row: {string.Join(",", fields)}");
                    }

                    await using var insert = new NpgsqlCommand(
                        """
                        INSERT INTO fin.account (account_id, company_id, code, name, is_control)
                        SELECT @id, company_id, @code, @name, @control FROM md.company WHERE rnc = @rnc
                        """,
                        connection,
                        tx);
                    insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
                    insert.Parameters.AddWithValue("code", fields[0]);
                    insert.Parameters.AddWithValue("name", string.Join(",", fields[1..^1]));
                    insert.Parameters.AddWithValue("control", isControl);
                    insert.Parameters.AddWithValue("rnc", args[1]);
                    imported += await insert.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();
                Console.WriteLine($"{imported} account(s) imported.");
                return imported > 0 ? 0 : 2;
            }

        case "import-account-map":
            {
                // DRAFT role → account mappings; the Controller approves them in the application (ApproveAccountRoleMap).
                // CSV: account_role,item_category(empty = any),account_code,effective_from(yyyy-MM-dd)
                if (args.Length != 3 || !File.Exists(args[2]))
                {
                    await Console.Error.WriteLineAsync("Usage: rochell-migrate import-account-map <company-rnc> <map.csv>");
                    return 1;
                }

                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                await using var tx = await connection.BeginTransactionAsync();
                var imported = 0;
                foreach (var fields in CsvRows(args[2], "account_role"))
                {
                    if (fields.Length != 4 || !DateOnly.TryParseExact(fields[3], "yyyy-MM-dd", out var from))
                    {
                        throw new FormatException($"Invalid mapping row: {string.Join(",", fields)}");
                    }

                    await using var insert = new NpgsqlCommand(
                        """
                        INSERT INTO fin.account_role_map (map_id, company_id, account_role, item_category, account_id, effective_from, prepared_by, status)
                        SELECT @id, a.company_id, @role, @category, a.account_id, @from, '00000000-0000-7000-8000-00000000d001', 'DRAFT'
                        FROM fin.account a JOIN md.company c ON c.company_id = a.company_id
                        WHERE c.rnc = @rnc AND a.code = @account
                        """,
                        connection,
                        tx);
                    insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
                    insert.Parameters.AddWithValue("role", fields[0]);
                    insert.Parameters.AddWithValue("category", string.IsNullOrEmpty(fields[1]) ? DBNull.Value : fields[1]);
                    insert.Parameters.AddWithValue("from", from);
                    insert.Parameters.AddWithValue("rnc", args[1]);
                    insert.Parameters.AddWithValue("account", fields[2]);
                    if (await insert.ExecuteNonQueryAsync() != 1)
                    {
                        throw new FormatException($"Account {fields[2]} not found for company {args[1]}.");
                    }

                    imported++;
                }

                await tx.CommitAsync();
                Console.WriteLine($"{imported} DRAFT mapping(s) imported; approve them with ApproveAccountRoleMap.");
                return 0;
            }

        case "open-periods":
            {
                // E-PR05-3: twelve calendar-month periods with INV-MOV, AP-REC and BANK-REC (E-VS2-01-6) open. Existing periods are left untouched.
                if (args.Length != 3 || !int.TryParse(args[2], out var year) || year is < 2000 or > 2100)
                {
                    await Console.Error.WriteLineAsync("Usage: rochell-migrate open-periods <company-rnc> <year>");
                    return 1;
                }

                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                await using var open = new NpgsqlCommand(
                    """
                    WITH company AS (SELECT company_id FROM md.company WHERE rnc = @rnc),
                         months AS (SELECT make_date(@year, m, 1) AS starts_on FROM generate_series(1, 12) AS m),
                         inserted AS (
                           INSERT INTO fin.period (period_id, company_id, starts_on, ends_on)
                           SELECT gen_random_uuid(), c.company_id, m.starts_on, (m.starts_on + interval '1 month' - interval '1 day')::date
                           FROM company c CROSS JOIN months m
                           ON CONFLICT (company_id, starts_on) DO NOTHING
                           RETURNING company_id, period_id)
                    INSERT INTO fin.close_component_state (company_id, period_id, component, status, version)
                    SELECT i.company_id, i.period_id, comp, 'OPEN', 1
                    FROM inserted i CROSS JOIN (VALUES ('INV-MOV'), ('AP-REC'), ('BANK-REC')) AS v (comp)
                    """,
                    connection);
                open.Parameters.AddWithValue("rnc", args[1]);
                open.Parameters.AddWithValue("year", year);
                var components = await open.ExecuteNonQueryAsync();
                Console.WriteLine($"{components / 3} period(s) opened for {year}.");
                return 0;
            }

        default:
            await Console.Error.WriteLineAsync("Usage: rochell-migrate <migrate|verify|status|init-environment|create-user|grant-role|create-plant|create-location|import-accounts|import-account-map|open-periods>");
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

static IEnumerable<string[]> CsvRows(string path, string headerFirstField)
    => File.ReadLines(path)
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => line.Split(',').Select(f => f.Trim()).ToArray())
        .Where(fields => !string.Equals(fields[0], headerFirstField, StringComparison.OrdinalIgnoreCase));
