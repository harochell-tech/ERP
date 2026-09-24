using System.Security.Cryptography;
using Npgsql;
using Rochell.Audit;
using Rochell.Audit.Worm;

namespace Rochell.Api.Hosting;

/// <summary>
/// WORM storage for digests (E-PR15-4). VS#1 has only the file-system store, accepted when core.deployment_environment is TEST;
/// production needs S3 Object Lock at a second provider (B-03), so outside TEST there is no store and the digest and the
/// verifier stay off.
/// </summary>
public sealed class WormAccess(AuditSettings settings, ILogger<WormAccess> logger)
{
    private IWormStore? _store;

    /// <param name="database">Any connection that can read core.deployment_environment (application or sealer role).</param>
    public async Task<IWormStore?> GetAsync(NpgsqlDataSource database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (_store is not null || string.IsNullOrWhiteSpace(settings.FileSystemWormRoot))
        {
            return _store;
        }

        await using var command = database.CreateCommand("SELECT environment FROM core.deployment_environment WHERE singleton");
        var environment = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        try
        {
            _store = FileSystemWormStore.Create(settings.FileSystemWormRoot, environment);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogCritical(ex, "WORM storage is not available: {Reason}", ex.Message);
        }

        return _store;
    }
}

/// <summary>Builds the hash:verify handler when WORM storage and the digest public key are configured.</summary>
public sealed class HashVerification(AuditSettings settings, WormAccess worm, AppDatabase database) : IDisposable
{
    private ECDsa? _key;
    private VerifyHashChainHandler? _handler;

    public async Task<VerifyHashChainHandler?> CreateHandlerAsync(CancellationToken cancellationToken)
    {
        if (_handler is not null)
        {
            return _handler;
        }

        if (string.IsNullOrWhiteSpace(settings.DigestPublicKeyPem)
            || await worm.GetAsync(database.DataSource, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return null;
        }

        var key = ECDsa.Create();
        key.ImportFromPem(settings.DigestPublicKeyPem);
        _key = key;
        return _handler = new VerifyHashChainHandler(store, new DigestSigner(key));
    }

    public void Dispose() => _key?.Dispose();
}

/// <summary>The application-role data source (rochell_app), wrapped so the sealer's source cannot be injected by mistake.</summary>
public sealed record AppDatabase(NpgsqlDataSource DataSource);

/// <summary>The sealer-role data source (rochell_sealer), only for the background services (E-PR18-5).</summary>
public sealed record SealerDatabase(NpgsqlDataSource DataSource);
