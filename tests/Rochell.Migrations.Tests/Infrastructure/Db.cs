using Npgsql;

namespace Rochell.Migrations.Tests.Infrastructure;

internal static class Db
{
    public static MigrationRunner Runner(string connectionString)
        => new(() => new NpgsqlConnection(connectionString));

    public static async Task<T?> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
#pragma warning disable CA2100 // Test SQL literals.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
#pragma warning disable CA2100 // Test SQL literals.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync();
    }
}
