using System.Data.Common;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.MasterData;

/// <summary>Optimistic, DRAFT-only updates for party and item rows (the database guard enforces the same rules).</summary>
internal static class MasterRows
{
    public static async Task<(string Status, long Version)?> ReadStateAsync(CommandContext context, string table, string idColumn, Guid id, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            $"SELECT status::text, version FROM md.{table} WHERE company_id = @company_id AND {idColumn} = @id FOR UPDATE",
            ("company_id", context.CompanyId),
            ("id", id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), reader.GetInt64(1))
            : null;
    }

    /// <summary>Locks the row and checks it exists, is DRAFT and has the expected version.</summary>
    public static async Task EnsureDraftAtVersionAsync(CommandContext context, string table, string idColumn, Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var state = await ReadStateAsync(context, table, idColumn, id, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(MasterDataErrors.NotFound, $"The {table} does not exist.");
        if (state.Version != expectedVersion)
        {
            throw new DomainException(MasterDataErrors.VersionConflict, $"The {table} changed (version {state.Version}, expected {expectedVersion}); reload and retry.");
        }

        if (state.Status != "DRAFT")
        {
            throw new DomainException(MasterDataErrors.NotDraft, $"The {table} is {state.Status}; only DRAFT rows can change in VS#1.");
        }
    }

    public static bool IsUniqueViolation(DbException ex) => ex.SqlState == SqlStates.UniqueViolation;
}
