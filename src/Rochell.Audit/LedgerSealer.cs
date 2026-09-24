using System.Data.Common;
using System.Globalization;
using Rochell.Platform.Data;
using Rochell.Platform.Time;

namespace Rochell.Audit;

/// <summary>Result of one sealing pass over one chain.</summary>
public sealed record SealPass(Guid CompanyId, string Ledger, int Sealed, int Failed, bool Skipped);

/// <summary>
/// ADR-037 / E-PR15-3: the only writer of a chain. Runs as rochell_sealer, in its own transactions, never inside a command.
/// One pass per chain: take the chain's advisory lock (another sealer holding it → skip), read up to <see cref="BatchSize"/>
/// PENDING_SEAL groups in arrival order, recompute every row hash from the data (stored hashes are not trusted), seal the group,
/// or mark it SEAL_ERROR when a row no longer matches the hash it stored at insert. Nothing is sealed if the pass fails.
/// </summary>
public sealed class LedgerSealer(DbDataSource sealerDatabase, IClock clock)
{
    public const int BatchSize = 1000;

    /// <summary>Seals every chain of every company once.</summary>
    public async Task<IReadOnlyList<SealPass>> SealAllAsync(CancellationToken cancellationToken)
    {
        var companies = new List<Guid>();
        await using (var connection = await sealerDatabase.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = Sql.Command(connection, null, "SELECT company_id FROM md.company ORDER BY company_id"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                companies.Add(reader.GetGuid(0));
            }
        }

        var passes = new List<SealPass>();
        foreach (var company in companies)
        {
            foreach (var ledger in Chains.All)
            {
                passes.Add(await SealChainAsync(company, ledger, cancellationToken).ConfigureAwait(false));
            }
        }

        return passes;
    }

    /// <summary>
    /// Seals one chain. <paramref name="beforeCommit"/> runs after the batch is written and before COMMIT (tests use it to show
    /// that commands never wait for an open sealing transaction — HS-01, E-PR15-8).
    /// </summary>
    public async Task<SealPass> SealChainAsync(Guid companyId, string ledger, CancellationToken cancellationToken, Func<Task>? beforeCommit = null)
    {
        await using var connection = await sealerDatabase.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(connection, transaction, "SELECT set_config('app.company_id', @c, true)", cancellationToken, ("c", companyId.ToString())).ConfigureAwait(false);

        await using (var lockCommand = Sql.Command(
            connection,
            transaction,
            "SELECT pg_try_advisory_xact_lock(hashtextextended('seal:' || @c || ':' || @l, 0))",
            ("c", companyId.ToString()),
            ("l", ledger)))
        {
            if (await lockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                return new SealPass(companyId, ledger, 0, 0, Skipped: true);
            }
        }

        var (sequence, previous) = await LastSealAsync(connection, transaction, companyId, ledger, cancellationToken).ConfigureAwait(false);
        var pending = new List<Guid>();
        await using (var command = Sql.Command(
            connection,
            transaction,
            """
            SELECT group_ref FROM audit.integrity_state
            WHERE company_id = @c AND ledger = @l AND integrity_status = 'PENDING_SEAL'
            ORDER BY updated_at, group_ref LIMIT @n
            """,
            ("c", companyId),
            ("l", ledger),
            ("n", BatchSize)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                pending.Add(reader.GetGuid(0));
            }
        }

        var sealedCount = 0;
        var failed = 0;
        var now = clock.UtcNow;
        foreach (var group in pending)
        {
            var rows = await GroupReader.ReadAsync(connection, transaction, companyId, ledger, group, cancellationToken).ConfigureAwait(false);
            var error = rows.IsEmpty
                ? "The group has no rows."
                : rows.FirstAlteredRow is { } altered
                    ? string.Create(CultureInfo.InvariantCulture, $"Row {altered + 1} of the group no longer matches the hash it stored at insert.")
                    : null;
            if (error is not null)
            {
                await Sql.ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE audit.integrity_state SET integrity_status = 'SEAL_ERROR', error_detail = @e, updated_at = @t
                    WHERE company_id = @c AND ledger = @l AND group_ref = @g
                    """,
                    cancellationToken,
                    ("e", error),
                    ("t", now),
                    ("c", companyId),
                    ("l", ledger),
                    ("g", group)).ConfigureAwait(false);
                failed++;
                continue;
            }

            sequence++;
            var groupHash = rows.Hash();
            var chainHash = ChainHash.Chain(previous, sequence, groupHash);
            await Sql.ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO audit.ledger_seal (company_id, ledger, ledger_sequence, group_ref, group_hash, prev_hash, chain_hash, sealed_at)
                VALUES (@c, @l, @s, @g, @gh, @ph, @ch, @t)
                """,
                cancellationToken,
                ("c", companyId),
                ("l", ledger),
                ("s", sequence),
                ("g", group),
                ("gh", groupHash),
                ("ph", previous),
                ("ch", chainHash),
                ("t", now)).ConfigureAwait(false);
            await Sql.ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE audit.integrity_state SET integrity_status = 'SEALED', ledger_sequence = @s, updated_at = @t
                WHERE company_id = @c AND ledger = @l AND group_ref = @g
                """,
                cancellationToken,
                ("s", sequence),
                ("t", now),
                ("c", companyId),
                ("l", ledger),
                ("g", group)).ConfigureAwait(false);
            previous = chainHash;
            sealedCount++;
        }

        if (beforeCommit is not null)
        {
            await beforeCommit().ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SealPass(companyId, ledger, sealedCount, failed, Skipped: false);
    }

    /// <summary>Seals continuously every <paramref name="interval"/> until cancelled (the background worker).</summary>
    public async Task RunAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            await SealAllAsync(cancellationToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task<(long Sequence, byte[] ChainHash)> LastSealAsync(DbConnection connection, DbTransaction transaction, Guid companyId, string ledger, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            connection,
            transaction,
            "SELECT ledger_sequence, chain_hash FROM audit.ledger_seal WHERE company_id = @c AND ledger = @l ORDER BY ledger_sequence DESC LIMIT 1",
            ("c", companyId),
            ("l", ledger));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetInt64(0), reader.GetFieldValue<byte[]>(1))
            : (0, ChainHash.Genesis);
    }
}
