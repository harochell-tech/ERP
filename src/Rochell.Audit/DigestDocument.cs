using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rochell.Platform.Hashing;

namespace Rochell.Audit;

/// <summary>The daily digest of one chain (v2.1 §8.2), as anchored in WORM. All values are strings (ADR-015).</summary>
public sealed record DigestFields(
    Guid CompanyId,
    string Ledger,
    DateOnly DigestDate,
    long FirstSequence,
    long LastSequence,
    byte[] MerkleRoot,
    byte[] LastChainHash,
    byte[]? PreviousDigestHash)
{
    public int ItemCount => checked((int)(LastSequence - FirstSequence + 1));
}

/// <summary>
/// Canonical JSON (RFC 8785) of the digest; digest_hash = SHA-256 of the canonical fields; the WORM object is
/// {"digest": fields + digest_hash, "signature": base64(ECDSA over the canonical "digest" object)}.
/// </summary>
public static class DigestDocument
{
    public const string HashVersion = "1";

    public static string WormKey(Guid companyId, string ledger, DateOnly day)
        => string.Create(CultureInfo.InvariantCulture, $"{companyId:D}/{ledger}/{day:yyyy-MM-dd}.json");

    public static byte[] DigestHash(DigestFields fields) => SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(Map(fields))));

    public static byte[] Envelope(DigestFields fields, DigestSigner signer)
    {
        ArgumentNullException.ThrowIfNull(signer);
        var digest = Map(fields);
        digest["digest_hash"] = Convert.ToHexStringLower(DigestHash(fields));
        var signed = Canonical(digest);
        var envelope = Canonical(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["digest"] = JsonDocument.Parse(signed).RootElement,
            ["signature"] = Convert.ToBase64String(signer.Sign(Encoding.UTF8.GetBytes(signed))),
        });
        return Encoding.UTF8.GetBytes(envelope);
    }

    /// <summary>Reads a WORM object back; null fields when the signature does not verify.</summary>
    public static (DigestFields? Fields, bool SignatureValid) Open(byte[] envelope, DigestSigner verifier)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(verifier);
        using var document = JsonDocument.Parse(envelope);
        var digest = document.RootElement.GetProperty("digest");
        var signed = Encoding.UTF8.GetBytes(JsonCanonicalizer.Canonicalize(digest.GetRawText()));
        var signature = Convert.FromBase64String(document.RootElement.GetProperty("signature").GetString()!);
        if (!verifier.Verify(signed, signature))
        {
            return (null, false);
        }

        string Text(string name) => digest.GetProperty(name).GetString()!;
        var previous = digest.GetProperty("prev_digest_hash");
        return (new DigestFields(
            Guid.Parse(Text("company_id")),
            Text("ledger"),
            DateOnly.ParseExact(Text("digest_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            long.Parse(Text("first_seq"), CultureInfo.InvariantCulture),
            long.Parse(Text("last_seq"), CultureInfo.InvariantCulture),
            Convert.FromHexString(Text("merkle_root")),
            Convert.FromHexString(Text("last_chain_hash")),
            previous.ValueKind == JsonValueKind.Null ? null : Convert.FromHexString(previous.GetString()!)), true);
    }

    private static SortedDictionary<string, object?> Map(DigestFields f)
    {
        ArgumentNullException.ThrowIfNull(f);
        return new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["hash_version"] = HashVersion,
            ["company_id"] = f.CompanyId.ToString("D"),
            ["ledger"] = f.Ledger,
            ["digest_date"] = f.DigestDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["first_seq"] = f.FirstSequence.ToString(CultureInfo.InvariantCulture),
            ["last_seq"] = f.LastSequence.ToString(CultureInfo.InvariantCulture),
            ["item_count"] = f.ItemCount.ToString(CultureInfo.InvariantCulture),
            ["merkle_root"] = Convert.ToHexStringLower(f.MerkleRoot),
            ["last_chain_hash"] = Convert.ToHexStringLower(f.LastChainHash),
            ["prev_digest_hash"] = f.PreviousDigestHash is null ? null : Convert.ToHexStringLower(f.PreviousDigestHash),
        };
    }

    private static string Canonical(object value) => JsonCanonicalizer.Canonicalize(JsonSerializer.Serialize(value));
}
