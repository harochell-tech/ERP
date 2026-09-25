using System.Security.Cryptography;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Npgsql;
using Rochell.Audit;
using Rochell.Audit.Worm;
using Rochell.Platform.Time;

namespace Rochell.Api.Hosting;

/// <summary>
/// WORM storage for digests (E-PR15-4). Either S3 Object Lock in compliance mode (<c>Rochell:Audit:S3</c>, B-03, any environment)
/// or the file-system store, accepted only when core.deployment_environment is TEST. Without a usable store the digest and the
/// verifier stay off.
/// </summary>
public sealed class WormAccess(AuditSettings settings, IClock clock, ILogger<WormAccess> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IWormStore? _store;
    private AmazonS3Client? _s3;

    /// <param name="database">Any connection that can read core.deployment_environment (application or sealer role).</param>
    public async Task<IWormStore?> GetAsync(NpgsqlDataSource database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_store is not null)
            {
                return _store;
            }

            var s3 = !string.IsNullOrWhiteSpace(settings.S3.Bucket);
            var files = !string.IsNullOrWhiteSpace(settings.FileSystemWormRoot);
            if (s3 && files)
            {
                logger.LogCritical("WORM storage is not available: configure either Rochell:Audit:S3 or Rochell:Audit:FileSystemWormRoot, not both.");
                return null;
            }

            if (s3)
            {
                _s3 ??= CreateS3Client(settings.S3);
                _store = await S3WormStore.CreateAsync(_s3, settings.S3.Bucket!, settings.S3.RetentionDays, clock, cancellationToken).ConfigureAwait(false);
            }
            else if (files)
            {
                await using var command = database.CreateCommand("SELECT environment FROM core.deployment_environment WHERE singleton");
                var environment = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
                _store = FileSystemWormStore.Create(settings.FileSystemWormRoot!, environment);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or AmazonServiceException)
        {
            logger.LogCritical(ex, "WORM storage is not available: {Reason}", ex.Message);
        }
        finally
        {
            _gate.Release();
        }

        return _store;
    }

    public void Dispose()
    {
        _s3?.Dispose();
        _gate.Dispose();
    }

    private static AmazonS3Client CreateS3Client(S3WormSettings s3)
    {
        var config = new AmazonS3Config();
        if (!string.IsNullOrWhiteSpace(s3.ServiceUrl))
        {
            config.ServiceURL = s3.ServiceUrl;
            config.ForcePathStyle = true;
            config.AuthenticationRegion = s3.Region;
        }
        else if (!string.IsNullOrWhiteSpace(s3.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(s3.Region);
        }

        return !string.IsNullOrWhiteSpace(s3.AccessKeyId) && !string.IsNullOrWhiteSpace(s3.SecretAccessKey)
            ? new AmazonS3Client(new BasicAWSCredentials(s3.AccessKeyId, s3.SecretAccessKey), config)
            : new AmazonS3Client(config);
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
