using Npgsql;
using Rochell.Migrations;
using Testcontainers.PostgreSql;
using Xunit;

namespace Rochell.TestInfrastructure;

/// <summary>A migrated database: admin = deployment role (owner); app = login that is a member of rochell_app.</summary>
public sealed record TestDatabase(string AdminConnectionString, string AppConnectionString);

/// <summary>
/// One PostgreSQL 17 container per test assembly; every test gets its own database.
/// The container user plays the deployment role, as the migration CLI does in real environments.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string Image = "postgres:17.6-alpine";
    public const string AppLogin = "rochell_app_test";
    private const string AppPassword = "rochell_app_test_only";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage(Image)
        .WithDatabase("rochell_admin")
        .WithUsername("rochell_deploy")
        .WithPassword("rochell_test_only")
        .Build();

    private readonly SemaphoreSlim _roleLock = new(1, 1);

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<string> CreateEmptyDatabaseAsync()
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
#pragma warning disable CA2100 // Database name is a generated GUID, not user input.
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }

    /// <summary>New database with all main migrations, then all test-only migrations, and an application login.</summary>
    public async Task<TestDatabase> CreateMigratedDatabaseAsync()
    {
        var admin = await CreateEmptyDatabaseAsync();
        var runner = new MigrationRunner(() => new NpgsqlConnection(admin));
        await runner.MigrateAsync(TestPaths.MainSource);
        await runner.MigrateAsync(TestPaths.TestSource);
        await EnsureAppLoginAsync(admin);

        var app = new NpgsqlConnectionStringBuilder(admin) { Username = AppLogin, Password = AppPassword }.ConnectionString;
        return new TestDatabase(admin, app);
    }

    private async Task EnsureAppLoginAsync(string adminConnectionString)
    {
        await _roleLock.WaitAsync();
        try
        {
            await using var connection = new NpgsqlConnection(adminConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"""
                DO $$
                BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{AppLogin}') THEN
                    CREATE ROLE {AppLogin} LOGIN PASSWORD '{AppPassword}' IN ROLE rochell_app;
                  END IF;
                END $$;
                """,
                connection);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _roleLock.Release();
        }
    }
}
