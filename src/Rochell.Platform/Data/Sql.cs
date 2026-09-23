using System.Data.Common;

namespace Rochell.Platform.Data;

/// <summary>PostgreSQL SQLSTATE codes used by the platform.</summary>
public static class SqlStates
{
    public const string UniqueViolation = "23505";
    public const string ForeignKeyViolation = "23503";
    public const string CheckViolation = "23514";
    public const string SerializationFailure = "40001";
    public const string DeadlockDetected = "40P01";
    public const string RaiseException = "P0001";

    public static bool IsRetryable(string? sqlState) => sqlState is SerializationFailure or DeadlockDetected;
}

/// <summary>Provider-agnostic command helpers. SQL text is always constant; values always go through parameters.</summary>
public static class Sql
{
    public static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100 // Constant SQL; values are parameters.
        command.CommandText = sql;
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    public static async Task<int> ExecuteAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
