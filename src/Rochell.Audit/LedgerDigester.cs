using System.Data.Common;
using Rochell.Audit.Worm;
using Rochell.Platform.Data;
using Rochell.Platform.Time;

namespace Rochell.Audit;

public sealed record DigestResult(Guid CompanyId, string Ledger, DateOnly Day, bool Created, long Items);

/// <summary>
/// E-PR15-5: at 00:15 local time, per chain, a digest of the previous day (local date of sealed_at, America/Santo_Domingo):
/// Merkle root of the day's chain hashes in sequence order, linked to the previous digest, signed, written once to WORM and
/// recorded in audit.ledger_digest. A day without seals has no digest. Running it again for a digested day changes nothing.
/// </summary>
public sealed class LedgerDigester(DbDataSource sealerDatabase, IWormStore worm, DigestSigner signer)
{
    public async Task<IReadOnlyList<DigestResult>> DigestDayAsync(DateOnly day, CancellationToken cancellationToken)
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

        var results = new List<DigestResult>();
        foreach (var company in companies)
        {
            foreach (var ledger in Chains.All)
            {
                results.Add(await DigestChainAsync(company, ledger, day, cancellationToken).ConfigureAwait(false));
            }
        }

        return results;
    }

    public async Task<DigestResult> DigestChainAsync(Guid companyId, string ledger, DateOnly day, CancellationToken cancellationToken)
    {
        await using var connection = await sealerDatabase.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(connection, transaction, "SELECT set_config('app.company_id', @c, true)", cancellationToken, ("c", companyId.ToString())).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended('digest:' || @c || ':' || @l, 0))",
            cancellationToken,
            ("c", companyId.ToString()),
            ("l", ledger)).ConfigureAwait(false);

        await using (var exists = Sql.Command(
            connection,
            transaction,
            "SELECT item_count FROM audit.ledger_digest WHERE company_id = @c AND ledger = @l AND digest_date = @d",
            ("c", companyId),
            ("l", ledger),
            ("d", day)))
        {
            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is int count)
            {
                return new DigestResult(companyId, ledger, day, Created: false, count);
            }
        }

        var (startUtc, endUtc) = BusinessCalendar.DayUtcRange(day);
        var seals = new List<(long Sequence, byte[] ChainHash)>();
        await using (var command = Sql.Command(
            connection,
            transaction,
            """
            SELECT ledger_sequence, chain_hash FROM audit.ledger_seal
            WHERE company_id = @c AND ledger = @l AND sealed_at >= @s AND sealed_at < @e ORDER BY ledger_sequence
            """,
            ("c", companyId),
            ("l", ledger),
            ("s", startUtc),
            ("e", endUtc)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                seals.Add((reader.GetInt64(0), reader.GetFieldValue<byte[]>(1)));
            }
        }

        if (seals.Count == 0)
        {
            return new DigestResult(companyId, ledger, day, Created: false, 0);
        }

        if (seals[^1].Sequence - seals[0].Sequence + 1 != seals.Count)
        {
            throw new InvalidOperationException($"Chain {ledger} of {companyId} has a gap among the seals of {day:yyyy-MM-dd}; no digest is written.");
        }

        byte[]? previousDigest;
        await using (var previous = Sql.Command(
            connection,
            transaction,
            "SELECT digest_hash FROM audit.ledger_digest WHERE company_id = @c AND ledger = @l AND digest_date < @d ORDER BY digest_date DESC LIMIT 1",
            ("c", companyId),
            ("l", ledger),
            ("d", day)))
        {
            previousDigest = await previous.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[];
        }

        var fields = new DigestFields(
            companyId, ledger, day, seals[0].Sequence, seals[^1].Sequence,
            ChainHash.MerkleRoot([.. seals.Select(s => s.ChainHash)]), seals[^1].ChainHash, previousDigest);
        var key = DigestDocument.WormKey(companyId, ledger, day);
        await Sql.ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO audit.ledger_digest (company_id, ledger, digest_date, first_seq, last_seq, item_count, merkle_root, last_chain_hash,
                                             prev_digest_hash, digest_hash, worm_object_key)
            VALUES (@c, @l, @d, @f, @t, @n, @m, @lc, @p, @h, @k)
            """,
            cancellationToken,
            ("c", companyId),
            ("l", ledger),
            ("d", day),
            ("f", fields.FirstSequence),
            ("t", fields.LastSequence),
            ("n", fields.ItemCount),
            ("m", fields.MerkleRoot),
            ("lc", fields.LastChainHash),
            ("p", previousDigest),
            ("h", DigestDocument.DigestHash(fields)),
            ("k", key)).ConfigureAwait(false);

        // WORM first, then COMMIT: if the commit fails, the retry finds the object; it is accepted only if it says the same.
        var envelope = DigestDocument.Envelope(fields, signer);
        try
        {
            await worm.PutAsync(key, envelope, cancellationToken).ConfigureAwait(false);
        }
        catch (WormObjectExistsException)
        {
            var existing = await worm.GetAsync(key, cancellationToken).ConfigureAwait(false);
            var (anchored, valid) = existing is null ? (null, false) : DigestDocument.Open(existing, signer);
            if (!valid || anchored is null || !anchored.MerkleRoot.AsSpan().SequenceEqual(fields.MerkleRoot)
                || anchored.FirstSequence != fields.FirstSequence || anchored.LastSequence != fields.LastSequence)
            {
                throw new InvalidOperationException($"WORM already holds a different digest for {key}.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new DigestResult(companyId, ledger, day, Created: true, fields.ItemCount);
    }
}
