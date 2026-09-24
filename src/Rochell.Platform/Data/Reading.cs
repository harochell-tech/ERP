using System.Data.Common;

namespace Rochell.Platform.Data;

/// <summary>Read helpers for the query side: constant SQL, parameters, one mapping function per row.</summary>
public static class Reading
{
    public static async Task<List<T>> ListAsync<T>(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        Func<DbDataReader, T> map,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(map);
        await using var command = Sql.Command(connection, transaction, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    public static async Task<T?> SingleOrDefaultAsync<T>(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        Func<DbDataReader, T> map,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
        where T : class
    {
        var rows = await ListAsync(connection, transaction, sql, map, cancellationToken, parameters).ConfigureAwait(false);
        return rows.Count switch
        {
            0 => null,
            1 => rows[0],
            _ => throw new InvalidOperationException("The query returned more than one row."),
        };
    }

    public static Guid? NullableGuid(this DbDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    }

    public static string? NullableString(this DbDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public static decimal? NullableDecimal(this DbDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
    }

    public static DateOnly Date(this DbDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader.GetFieldValue<DateOnly>(ordinal);
    }

    /// <summary>A timestamptz column (read as UTC).</summary>
    public static DateTime Utc(this DbDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader.GetFieldValue<DateTime>(ordinal);
    }

    public static DateTime? NullableUtc(this DbDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTime>(ordinal);
    }
}
