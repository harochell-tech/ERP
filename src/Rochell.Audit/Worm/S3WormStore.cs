using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Rochell.Platform.Time;

namespace Rochell.Audit.Worm;

/// <summary>
/// Digests in S3 Object Lock, compliance mode, at a second provider (E-PR15-4, E-B03-3). Every object is written with
/// <c>If-None-Match: *</c> and a COMPLIANCE retention of <see cref="RetentionDays"/> days: until then nobody — not even the account
/// root — can delete or shorten it. Reads take the key's <b>oldest</b> version, so a later version or delete marker written by
/// anyone never replaces what was anchored, and refuse a version that is not under COMPLIANCE retention (GetObjectRetention).
/// </summary>
public sealed class S3WormStore : IWormStore
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly IClock _clock;

    private S3WormStore(IAmazonS3 s3, string bucket, int retentionDays, IClock clock)
    {
        _s3 = s3;
        _bucket = bucket;
        RetentionDays = retentionDays;
        _clock = clock;
    }

    public int RetentionDays { get; }

    /// <summary>Opens the store after checking that the bucket has Object Lock enabled; fails loudly otherwise.</summary>
    public static async Task<S3WormStore> CreateAsync(IAmazonS3 s3, string bucket, int retentionDays, IClock clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(s3);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(retentionDays, 1);

        GetObjectLockConfigurationResponse configuration;
        try
        {
            configuration = await s3.GetObjectLockConfigurationAsync(new GetObjectLockConfigurationRequest { BucketName = bucket }, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex)
        {
            throw new InvalidOperationException($"Bucket {bucket} has no Object Lock configuration ({ex.ErrorCode}); digests need a WORM bucket (B-03).", ex);
        }

        if (configuration.ObjectLockConfiguration?.ObjectLockEnabled != ObjectLockEnabled.Enabled)
        {
            throw new InvalidOperationException($"Bucket {bucket} does not have Object Lock enabled; digests need a WORM bucket (B-03).");
        }

        return new S3WormStore(s3, bucket, retentionDays, clock);
    }

    public async Task PutAsync(string key, byte[] content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(content);
        using var body = new MemoryStream(content, writable: false);
        try
        {
            await _s3.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = _bucket,
                    Key = key,
                    InputStream = body,
                    ContentType = "application/json",
                    IfNoneMatch = "*",
                    ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
                    ObjectLockMode = ObjectLockMode.Compliance,
                    ObjectLockRetainUntilDate = _clock.UtcNow.AddDays(RetentionDays),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            throw new WormObjectExistsException(key);
        }
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var versions = await _s3.ListVersionsAsync(new ListVersionsRequest { BucketName = _bucket, Prefix = key }, cancellationToken).ConfigureAwait(false);
        var first = (versions.Versions ?? [])
            .Where(v => v.Key == key && v.IsDeleteMarker != true)
            .OrderBy(v => v.LastModified)
            .FirstOrDefault();
        if (first is null)
        {
            return null;
        }

        var retention = await _s3.GetObjectRetentionAsync(
            new GetObjectRetentionRequest { BucketName = _bucket, Key = key, VersionId = first.VersionId }, cancellationToken).ConfigureAwait(false);
        if (retention.Retention?.Mode != ObjectLockRetentionMode.Compliance)
        {
            throw new InvalidOperationException($"WORM object {key} (version {first.VersionId}) is not under COMPLIANCE retention.");
        }

        using var response = await _s3.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = key, VersionId = first.VersionId }, cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
