using System.Data.Common;
using System.Diagnostics;

namespace Rochell.Migrations;

/// <summary>
/// Forward-only PostgreSQL migration runner.
/// - One transaction per script (script + journal row commit together or not at all).
/// - Session advisory lock: concurrent runners serialize; the second one finds nothing pending.
/// - The journal rejects UPDATE/DELETE (trigger). There is no "down" operation.
/// </summary>
public sealed class MigrationRunner
{
    /// <summary>Fixed advisory lock key reserved for the migration runner.</summary>
    public const long AdvisoryLockKey = 7_270_001_001;

    internal const string JournalDdl = """
        CREATE SCHEMA IF NOT EXISTS migrations;
        CREATE TABLE IF NOT EXISTS migrations.applied_migration (
          source        text        NOT NULL,
          version       integer     NOT NULL CHECK (version >= 1),
          name          text        NOT NULL,
          checksum      bytea       NOT NULL CHECK (octet_length(checksum) = 32),
          applied_at    timestamptz NOT NULL DEFAULT now(),
          applied_by    text        NOT NULL DEFAULT current_user,
          execution_ms  integer     NOT NULL CHECK (execution_ms >= 0),
          CONSTRAINT applied_migration_pk PRIMARY KEY (source, version)
        );
        CREATE OR REPLACE FUNCTION migrations.reject_journal_change() RETURNS trigger
          LANGUAGE plpgsql AS $$
          BEGIN
            RAISE EXCEPTION 'migrations.applied_migration is append-only (forward-only migrations)';
          END $$;
        CREATE OR REPLACE TRIGGER applied_migration_append_only
          BEFORE UPDATE OR DELETE ON migrations.applied_migration
          FOR EACH ROW EXECUTE FUNCTION migrations.reject_journal_change();
        """;

    private readonly Func<DbConnection> _connectionFactory;
    private readonly Action<string> _log;

    public MigrationRunner(Func<DbConnection> connectionFactory, Action<string>? log = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _log = log ?? (_ => { });
    }

    /// <summary>Applies all pending migrations of <paramref name="source"/>.</summary>
    public Task<MigrationResult> MigrateAsync(MigrationSource source, CancellationToken cancellationToken = default)
        => RunAsync(source, apply: true, cancellationToken);

    /// <summary>Validates journal vs files (modified, deleted, out-of-order) and lists pending migrations. Applies nothing.</summary>
    public Task<MigrationResult> VerifyAsync(MigrationSource source, CancellationToken cancellationToken = default)
        => RunAsync(source, apply: false, cancellationToken);

    private async Task<MigrationResult> RunAsync(MigrationSource source, bool apply, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var scripts = MigrationDiscovery.Discover(source);

        await using var connection = _connectionFactory();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, null, "SELECT pg_advisory_lock(@key)", cancellationToken, ("key", AdvisoryLockKey)).ConfigureAwait(false);
        try
        {
            await ExecuteAsync(connection, null, JournalDdl, cancellationToken).ConfigureAwait(false);
            var applied = await ReadAppliedAsync(connection, source.Name, cancellationToken).ConfigureAwait(false);
            var pending = MigrationPlanner.PendingScripts(scripts, applied);

            if (!apply)
            {
                return new MigrationResult(source.Name, applied.Count, [], pending.Select(p => p.FileName).ToList());
            }

            var appliedNow = new List<string>();
            foreach (var script in pending)
            {
                await ApplyAsync(connection, script, cancellationToken).ConfigureAwait(false);
                appliedNow.Add(script.FileName);
            }

            _log($"[{source.Name}] already applied: {applied.Count}; applied now: {appliedNow.Count}.");
            return new MigrationResult(source.Name, applied.Count, appliedNow, []);
        }
        finally
        {
            await ExecuteAsync(connection, null, "SELECT pg_advisory_unlock(@key)", CancellationToken.None, ("key", AdvisoryLockKey)).ConfigureAwait(false);
        }
    }

    private async Task ApplyAsync(DbConnection connection, MigrationScript script, CancellationToken cancellationToken)
    {
        _log($"[{script.Source}] applying {script.FileName} ({script.ChecksumHex[..12]}...)");
        var stopwatch = Stopwatch.StartNew();

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteAsync(connection, transaction, script.Sql, cancellationToken).ConfigureAwait(false);
            var elapsedMs = (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue);
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO migrations.applied_migration (source, version, name, checksum, execution_ms) VALUES (@source, @version, @name, @checksum, @ms)",
                cancellationToken,
                ("source", script.Source),
                ("version", script.Version),
                ("name", script.Name),
                ("checksum", script.Checksum),
                ("ms", elapsedMs)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (DbException)
            {
                // Connection already broken: the server discards the open transaction anyway.
            }

            throw new MigrationException(
                $"Migration '{script.Source}/{script.FileName}' failed and was rolled back: {ex.Message}", ex);
        }
    }

    private static async Task<IReadOnlyList<AppliedMigration>> ReadAppliedAsync(
        DbConnection connection,
        string source,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            null,
            "SELECT version, name, checksum FROM migrations.applied_migration WHERE source = @source ORDER BY version",
            ("source", source));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var rows = new List<AppliedMigration>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new AppliedMigration(source, reader.GetInt32(0), reader.GetString(1), reader.GetFieldValue<byte[]>(2)));
        }

        return rows;
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DbCommand CreateCommand(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100 // SQL comes from version-controlled migration files or constants, never from user input.
        command.CommandText = sql;
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return command;
    }
}
