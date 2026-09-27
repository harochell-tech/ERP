using Npgsql;
using Rochell.Migrations;
using Testcontainers.PostgreSql;
using Xunit;

namespace Rochell.TestInfrastructure;

/// <summary>
/// A migrated database: admin = deployment role (owner); app = login that is a member of rochell_app;
/// sealer = login that is a member of rochell_sealer (PR-15).
/// </summary>
public sealed record TestDatabase(string AdminConnectionString, string AppConnectionString, string SealerConnectionString);

/// <summary>
/// An existing, empty database on a server the harness does not own (PF-01 on staging, E-B03-16), and the logins the harness
/// creates there for itself. The logins must not exist yet: roles are cluster-wide, so the caller drops them afterwards.
/// </summary>
public sealed record ExistingDatabase(string AdminConnectionString, string AppLogin, string AppPassword, string SealerLogin, string SealerPassword);

/// <summary>
/// One PostgreSQL 17 container per test assembly; every test gets its own database.
/// The container user plays the deployment role, as the migration CLI does in real environments.
/// <see cref="ForExistingDatabase"/> instead migrates one given empty database on an external server (no container).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string Image = "postgres:17.6-alpine";
    public const string AppLogin = "rochell_app_test";
    private const string AppPassword = "rochell_app_test_only";
    public const string SealerLogin = "rochell_sealer_test";
    private const string SealerPassword = "rochell_sealer_test_only";

    private readonly PostgreSqlContainer? _container;
    private readonly ExistingDatabase? _existing;
    private readonly SemaphoreSlim _roleLock = new(1, 1);
    private int _existingUsed;

    public PostgresFixture()
    {
        _container = new PostgreSqlBuilder()
            .WithImage(Image)
            .WithDatabase("rochell_admin")
            .WithUsername("rochell_deploy")
            .WithPassword("rochell_test_only")
            .Build();
    }

    private PostgresFixture(ExistingDatabase existing)
    {
        _existing = existing;
    }

    /// <summary>E-B03-16: a fixture over one existing empty database; migrated once, never created or dropped here.</summary>
    public static PostgresFixture ForExistingDatabase(ExistingDatabase existing) => new(existing);

    public Task InitializeAsync() => _container?.StartAsync() ?? Task.CompletedTask;

    public Task DisposeAsync() => _container?.DisposeAsync().AsTask() ?? Task.CompletedTask;

    public async Task<string> CreateEmptyDatabaseAsync()
    {
        var container = _container ?? throw new InvalidOperationException("An existing-database fixture cannot create databases.");
        var name = "t_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
#pragma warning disable CA2100 // Database name is a generated GUID, not user input.
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = name }.ConnectionString;
    }

    /// <summary>New database with all main migrations, then all test-only migrations, and an application login.</summary>
    public async Task<TestDatabase> CreateMigratedDatabaseAsync()
    {
        if (_existing is not null)
        {
            return await MigrateExistingDatabaseAsync(_existing);
        }

        var admin = await CreateEmptyDatabaseAsync();

        // One-off setup connections to a brand-new database must not stay in Npgsql's global pool: every test creates its own
        // database, so each would leave an idle pooled connection for minutes and a large suite exhausts max_connections (53300).
        var setup = new NpgsqlConnectionStringBuilder(admin) { Pooling = false }.ConnectionString;
        var runner = new MigrationRunner(() => new NpgsqlConnection(setup));
        await runner.MigrateAsync(TestPaths.MainSource);
        await runner.MigrateAsync(TestPaths.TestSource);
        await EnsureLoginsAsync(setup, new(admin, AppLogin, AppPassword, SealerLogin, SealerPassword), mustBeNew: false);

        var app = new NpgsqlConnectionStringBuilder(admin) { Username = AppLogin, Password = AppPassword }.ConnectionString;
        var sealer = new NpgsqlConnectionStringBuilder(admin) { Username = SealerLogin, Password = SealerPassword }.ConnectionString;
        return new TestDatabase(admin, app, sealer);
    }

    private async Task<TestDatabase> MigrateExistingDatabaseAsync(ExistingDatabase existing)
    {
        if (Interlocked.Exchange(ref _existingUsed, 1) != 0)
        {
            throw new InvalidOperationException("An existing-database fixture provides exactly one database.");
        }

        var setup = new NpgsqlConnectionStringBuilder(existing.AdminConnectionString) { Pooling = false }.ConnectionString;

        // The test-only migrations must never reach a real database: refuse anything that is not freshly created and empty.
        await using (var connection = new NpgsqlConnection(setup))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                SELECT (SELECT count(*) FROM pg_namespace
                        WHERE nspname NOT IN ('public', 'information_schema') AND nspname NOT LIKE 'pg\_%')
                     + (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public')
                """,
                connection);
            var objects = (long)(await command.ExecuteScalarAsync() ?? 0L);
            if (objects != 0)
            {
                throw new InvalidOperationException($"Database '{connection.Database}' is not empty ({objects} schemas or relations); refusing to migrate it.");
            }
        }

        var runner = new MigrationRunner(() => new NpgsqlConnection(setup));
        await runner.MigrateAsync(TestPaths.MainSource);
        await runner.MigrateAsync(TestPaths.TestSource);
        await EnsureLoginsAsync(setup, existing, mustBeNew: true);

        var admin = existing.AdminConnectionString;
        var app = new NpgsqlConnectionStringBuilder(admin) { Username = existing.AppLogin, Password = existing.AppPassword }.ConnectionString;
        var sealer = new NpgsqlConnectionStringBuilder(admin) { Username = existing.SealerLogin, Password = existing.SealerPassword }.ConnectionString;
        return new TestDatabase(admin, app, sealer);
    }

    private async Task EnsureLoginsAsync(string setupConnectionString, ExistingDatabase logins, bool mustBeNew)
    {
        await _roleLock.WaitAsync();
        try
        {
            await using var connection = new NpgsqlConnection(setupConnectionString);
            await connection.OpenAsync();
            await EnsureLoginAsync(connection, logins.AppLogin, logins.AppPassword, "rochell_app", mustBeNew);
            await EnsureLoginAsync(connection, logins.SealerLogin, logins.SealerPassword, "rochell_sealer", mustBeNew);
        }
        finally
        {
            _roleLock.Release();
        }
    }

    private static async Task EnsureLoginAsync(NpgsqlConnection connection, string login, string password, string group, bool mustBeNew)
    {
        // Role DDL takes no bind parameters: format() quotes the name and the password on the server.
        await using var build = new NpgsqlCommand(
            """
            SELECT CASE WHEN EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @login) THEN NULL
                        ELSE format('CREATE ROLE %I LOGIN PASSWORD %L IN ROLE %I', @login::text, @password::text, @group::text) END
            """,
            connection);
        build.Parameters.AddWithValue("login", login);
        build.Parameters.AddWithValue("password", password);
        build.Parameters.AddWithValue("group", group);
        if (await build.ExecuteScalarAsync() is not string ddl)
        {
            if (mustBeNew)
            {
                throw new InvalidOperationException($"Login '{login}' already exists; the harness only uses logins it creates itself.");
            }

            return;
        }

#pragma warning disable CA2100 // Built by format() with %I / %L on the server.
        await using var create = new NpgsqlCommand(ddl, connection);
#pragma warning restore CA2100
        await create.ExecuteNonQueryAsync();
    }
}
