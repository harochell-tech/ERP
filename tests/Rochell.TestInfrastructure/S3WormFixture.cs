using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Rochell.TestInfrastructure;

/// <summary>
/// An S3-compatible server with Object Lock for the WORM store tests (B-03, E-B03-4). MinIO no longer publishes public images, so
/// the fixture runs RustFS, pinned; it enforces COMPLIANCE retention, conditional writes and versioning like S3.
/// </summary>
public sealed class S3WormFixture : IAsyncLifetime
{
    public const string Image = "rustfs/rustfs:1.0.0";
    public const string Region = "us-east-1";
    public const string AccessKey = "rochell_worm_test";
    public const string SecretKey = "rochell_worm_test_only";
    private const ushort Port = 9000;

    private readonly IContainer _container = new ContainerBuilder()
        .WithImage(Image)
        .WithPortBinding(Port, assignRandomHostPort: true)
        .WithEnvironment("RUSTFS_ACCESS_KEY", AccessKey)
        .WithEnvironment("RUSTFS_SECRET_KEY", SecretKey)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(Port))
        .Build();

    public string ServiceUrl => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(Port)}";

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        using var s3 = CreateClient();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await s3.ListBucketsAsync();
                return;
            }
            catch (Exception) when (attempt < 60)
            {
                await Task.Delay(250);
            }
        }
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public AmazonS3Client CreateClient()
        => new(new BasicAWSCredentials(AccessKey, SecretKey), new AmazonS3Config { ServiceURL = ServiceUrl, ForcePathStyle = true, AuthenticationRegion = Region });

    /// <summary>A new bucket, with Object Lock (and therefore versioning) enabled unless <paramref name="objectLock"/> is false.</summary>
    public async Task<string> CreateBucketAsync(bool objectLock = true)
    {
        var bucket = "worm-" + Guid.NewGuid().ToString("N")[..20];
        using var s3 = CreateClient();
        await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket, ObjectLockEnabledForBucket = objectLock });
        return bucket;
    }
}
