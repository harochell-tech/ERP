namespace Rochell.Audit.Worm;

/// <summary>E-PR15-4: write-once storage for daily digests. An object, once written, can never be replaced or removed.</summary>
public interface IWormStore
{
    /// <summary>Writes a new object; throws <see cref="WormObjectExistsException"/> if the key already exists.</summary>
    Task PutAsync(string key, byte[] content, CancellationToken cancellationToken);

    /// <summary>The object's bytes, or null if it does not exist.</summary>
    Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken);
}

public sealed class WormObjectExistsException(string key) : InvalidOperationException($"WORM object {key} already exists and cannot be replaced.")
{
    public string Key { get; } = key;
}

/// <summary>
/// Write-once files, for CI and development only (E-PR15-4): a new file is created exclusively and marked read-only.
/// The production anchor is S3 Object Lock in compliance mode at a second provider (B-03); this store refuses PRODUCTION.
/// </summary>
public sealed class FileSystemWormStore : IWormStore
{
    private readonly string _root;

    private FileSystemWormStore(string root) => _root = Path.GetFullPath(root);

    /// <param name="deploymentEnvironment">core.deployment_environment of the database the digests belong to.</param>
    public static FileSystemWormStore Create(string root, string? deploymentEnvironment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (deploymentEnvironment is not "TEST")
        {
            throw new InvalidOperationException(
                $"The file-system WORM store is only for TEST environments (found '{deploymentEnvironment ?? "not initialized"}'); production digests need object-lock storage (B-03).");
        }

        Directory.CreateDirectory(root);
        return new FileSystemWormStore(root);
    }

    public async Task PutAsync(string key, byte[] content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException) when (File.Exists(path))
        {
            throw new WormObjectExistsException(key);
        }

        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        }

        File.SetAttributes(path, FileAttributes.ReadOnly);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathOf(key);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false) : null;
    }

    private string PathOf(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var path = Path.GetFullPath(Path.Combine(_root, key));
        return path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? path
            : throw new ArgumentException($"WORM key {key} escapes the store root.", nameof(key));
    }
}
