namespace Rochell.Platform.Files;

/// <summary>
/// ENT1-02 (E-ENT-5): evidence files kept outside the database — the drivers' photos and signatures — in a private bucket apart from
/// the WORM one. The database keeps the key and its SHA-256; the store keeps the bytes. Keys never come from a client.
/// </summary>
public interface IEvidenceStore
{
    Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken);

    /// <summary>The file and its content type, or null when the key has nothing.</summary>
    Task<EvidenceFile?> GetAsync(string key, CancellationToken cancellationToken);
}

public sealed record EvidenceFile(byte[] Content, string ContentType);

/// <summary>Only JPEG and PNG are evidence (E-ENT1-01-8), recognised by their first bytes, not by what the client says.</summary>
public static class EvidenceImages
{
    public const int MaxBytes = 5 * 1024 * 1024; // type-limit: E-ENT1-01-8

    public static string? ContentType(ReadOnlySpan<byte> content)
        => content.Length > 8 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF ? "image/jpeg"
            : content.Length > 8 && content[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ? "image/png"
            : null;

    public static string Extension(string contentType) => contentType == "image/png" ? "png" : "jpg";
}
