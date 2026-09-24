using System.Globalization;
using System.Text.Json;
using Rochell.Audit.Worm;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Audit;

/// <summary>E-PR15-6: verifies every chain of the company from the data as it is now, against the digests anchored in WORM.</summary>
public sealed record VerifyHashChain(Guid CompanyId, Guid SessionId, string IdempotencyKey) : ICommand;

/// <summary>What the verification found in one chain.</summary>
public sealed record ChainReport(
    string Ledger,
    long Seals,
    long? FirstInvalidSequence,
    IReadOnlyList<string> Gaps,
    IReadOnlyList<string> DigestMismatches,
    long StalePending,
    IReadOnlyList<Guid> SealErrors)
{
    public bool Valid => FirstInvalidSequence is null && Gaps.Count == 0 && DigestMismatches.Count == 0 && StalePending == 0 && SealErrors.Count == 0;
}

/// <summary>
/// v2.1 §8.3. Per chain: (1) recompute every row hash → group hash → chain hash with the recomputed previous hash, and report
/// the first ledger_sequence that no longer matches; (2) sequence gaps; (3) each day's Merkle root recomputed and compared with
/// the digest read from WORM (signature checked), never with the database copy; (4) groups PENDING_SEAL for more than
/// <see cref="StaleAfter"/>; (5) groups in SEAL_ERROR. Read-only: findings are returned, not stored (reconciliations, PR-16).
/// </summary>
[RequiresPermission("hash:verify")]
public sealed class VerifyHashChainHandler(IWormStore worm, DigestSigner verifier) : ICommandHandler<VerifyHashChain>
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    public string CommandType => "Audit.VerifyHashChain";

    public async Task<string> HandleAsync(VerifyHashChain command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reports = new List<ChainReport>();
        foreach (var ledger in Chains.All)
        {
            reports.Add(await VerifyChainAsync(context, ledger, cancellationToken).ConfigureAwait(false));
        }

        return JsonSerializer.Serialize(new
        {
            valid = reports.All(r => r.Valid),
            chains = reports.Select(r => new
            {
                ledger = r.Ledger,
                seals = r.Seals,
                valid = r.Valid,
                firstInvalidSequence = r.FirstInvalidSequence,
                gaps = r.Gaps,
                digestMismatches = r.DigestMismatches,
                stalePending = r.StalePending,
                sealErrors = r.SealErrors,
            }),
        });
    }

    private async Task<ChainReport> VerifyChainAsync(CommandContext context, string ledger, CancellationToken cancellationToken)
    {
        var seals = new List<(long Sequence, Guid Group, byte[] Chain)>();
        await using (var command = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT ledger_sequence, group_ref, chain_hash FROM audit.ledger_seal WHERE company_id = @c AND ledger = @l ORDER BY ledger_sequence",
            ("c", context.CompanyId),
            ("l", ledger)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                seals.Add((reader.GetInt64(0), reader.GetGuid(1), reader.GetFieldValue<byte[]>(2)));
            }
        }

        long? firstInvalid = null;
        var gaps = new List<string>();
        var recomputed = new Dictionary<long, byte[]>();
        var previous = ChainHash.Genesis;
        long expected = 1;
        foreach (var (sequence, group, stored) in seals)
        {
            if (sequence != expected)
            {
                gaps.Add(string.Create(CultureInfo.InvariantCulture, $"{expected}-{sequence - 1}"));
            }

            var rows = await GroupReader.ReadAsync(context.Connection, context.Transaction, context.CompanyId, ledger, group, cancellationToken).ConfigureAwait(false);
            var chain = ChainHash.Chain(previous, sequence, rows.Hash());
            if (rows.IsEmpty || !chain.AsSpan().SequenceEqual(stored))
            {
                firstInvalid ??= sequence;
            }

            recomputed[sequence] = chain;
            previous = chain;
            expected = sequence + 1;
        }

        var mismatches = new List<string>();
        var digests = new List<(DateOnly Day, string Key)>();
        await using (var command = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT digest_date, worm_object_key FROM audit.ledger_digest WHERE company_id = @c AND ledger = @l ORDER BY digest_date",
            ("c", context.CompanyId),
            ("l", ledger)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                digests.Add((reader.GetFieldValue<DateOnly>(0), reader.GetString(1)));
            }
        }

        foreach (var (day, key) in digests)
        {
            var label = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var envelope = await worm.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (envelope is null)
            {
                mismatches.Add($"{label}: digest missing in WORM");
                continue;
            }

            var (anchored, signatureValid) = DigestDocument.Open(envelope, verifier);
            if (!signatureValid || anchored is null)
            {
                mismatches.Add($"{label}: WORM signature invalid");
                continue;
            }

            var leaves = new List<byte[]>();
            for (var s = anchored.FirstSequence; s <= anchored.LastSequence; s++)
            {
                if (!recomputed.TryGetValue(s, out var leaf))
                {
                    break;
                }

                leaves.Add(leaf);
            }

            if (leaves.Count != anchored.ItemCount || !ChainHash.MerkleRoot(leaves).AsSpan().SequenceEqual(anchored.MerkleRoot))
            {
                mismatches.Add($"{label}: Merkle root differs from WORM");
            }
        }

        long stale;
        await using (var command = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT count(*) FROM audit.integrity_state WHERE company_id = @c AND ledger = @l AND integrity_status = 'PENDING_SEAL' AND updated_at < @t",
            ("c", context.CompanyId),
            ("l", ledger),
            ("t", context.Clock.UtcNow - StaleAfter)))
        {
            stale = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        var errors = new List<Guid>();
        await using (var command = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT group_ref FROM audit.integrity_state WHERE company_id = @c AND ledger = @l AND integrity_status = 'SEAL_ERROR' ORDER BY group_ref",
            ("c", context.CompanyId),
            ("l", ledger)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                errors.Add(reader.GetGuid(0));
            }
        }

        return new ChainReport(ledger, seals.Count, firstInvalid, gaps, mismatches, stale, errors);
    }
}
