using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Rochell.Migrations.Tests.Infrastructure;

/// <summary>
/// One PostgreSQL 17 container per test run; every test gets its own empty database.
/// The container user plays the deployment role (schema owner), as the CLI does in real environments.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string Image = "postgres:17.6-alpine";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage(Image)
        .WithDatabase("rochell_admin")
        .WithUsername("rochell_deploy")
        .WithPassword("rochell_test_only")
        .Build();

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
}

[CollectionDefinition(Name)]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
