using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Rochell.Audit;

/// <summary>v2.1 §8.2: group hash, chain hash and the daily Merkle root.</summary>
public static class ChainHash
{
    public const int Length = 32;

    /// <summary>prev_hash of the first seal of every chain.</summary>
    public static byte[] Genesis => new byte[Length];

    /// <summary>group_hash = SHA256(row_hash_1 || … || row_hash_n), rows in group order.</summary>
    public static byte[] Group(IEnumerable<byte[]> rowHashes)
    {
        ArgumentNullException.ThrowIfNull(rowHashes);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var row in rowHashes)
        {
            sha.AppendData(row);
        }

        return sha.GetHashAndReset();
    }

    /// <summary>chain_hash = SHA256(prev_hash || ledger_sequence (8 bytes, big-endian) || group_hash).</summary>
    public static byte[] Chain(byte[] previous, long sequence, byte[] groupHash)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(groupHash);
        Span<byte> buffer = stackalloc byte[Length + sizeof(long) + Length];
        previous.CopyTo(buffer);
        BinaryPrimitives.WriteInt64BigEndian(buffer[Length..], sequence);
        groupHash.CopyTo(buffer[(Length + sizeof(long))..]);
        return SHA256.HashData(buffer);
    }

    /// <summary>Binary SHA-256 Merkle root over the leaves in order; an odd leaf is paired with itself.</summary>
    public static byte[] MerkleRoot(IReadOnlyList<byte[]> leaves)
    {
        ArgumentNullException.ThrowIfNull(leaves);
        if (leaves.Count == 0)
        {
            throw new ArgumentException("A Merkle tree needs at least one leaf.", nameof(leaves));
        }

        var level = leaves.ToList();
        while (level.Count > 1)
        {
            var next = new List<byte[]>((level.Count + 1) / 2);
            for (var i = 0; i < level.Count; i += 2)
            {
                var left = level[i];
                var right = i + 1 < level.Count ? level[i + 1] : left;
                next.Add(SHA256.HashData([.. left, .. right]));
            }

            level = next;
        }

        return level[0];
    }
}
