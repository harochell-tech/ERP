using System.Security.Cryptography;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Rochell.Audit.Worm;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Audit.Tests;

/// <summary>B-03 / E-B03-3, E-B03-4: digests anchored in S3 Object Lock, compliance mode, written once and never replaced.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class S3WormStoreTests(PostgresFixture postgres, S3WormFixture s3) : IClassFixture<S3WormFixture>, IDisposable
{
    private const int RetentionDays = 7;
    private readonly AmazonS3Client _client = s3.CreateClient();
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _client.Dispose();
        _key.Dispose();
    }

    private async Task<(S3WormStore Store, string Bucket)> StoreAsync()
    {
        var bucket = await s3.CreateBucketAsync();
        return (await S3WormStore.CreateAsync(_client, bucket, RetentionDays, SystemClock.Instance, CancellationToken.None), bucket);
    }

    private async Task<string> OnlyVersionAsync(string bucket, string key)
        => (await _client.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = key })).Versions.Single(v => v.IsDeleteMarker != true).VersionId;

    [Fact]
    public async Task An_object_is_written_once_under_compliance_retention()
    {
        var (store, bucket) = await StoreAsync();
        var before = DateTime.UtcNow;

        await store.PutAsync("gl/2026-09-24.json", [1, 2, 3], CancellationToken.None);
        var again = await Record.ExceptionAsync(() => store.PutAsync("gl/2026-09-24.json", [9], CancellationToken.None));

        Assert.IsType<WormObjectExistsException>(again);
        Assert.Equal(new byte[] { 1, 2, 3 }, await store.GetAsync("gl/2026-09-24.json", CancellationToken.None));
        Assert.Null(await store.GetAsync("gl/2026-09-25.json", CancellationToken.None));
        var retention = (await _client.GetObjectRetentionAsync(new GetObjectRetentionRequest
        {
            BucketName = bucket,
            Key = "gl/2026-09-24.json",
            VersionId = await OnlyVersionAsync(bucket, "gl/2026-09-24.json"),
        })).Retention;
        Assert.Equal(ObjectLockRetentionMode.Compliance, retention.Mode);
        Assert.InRange(retention.RetainUntilDate!.Value.ToUniversalTime(), before.AddDays(RetentionDays).AddMinutes(-1), DateTime.UtcNow.AddDays(RetentionDays).AddMinutes(1));
    }

    [Fact]
    public async Task Nobody_can_delete_shorten_or_replace_the_anchored_version()
    {
        var (store, bucket) = await StoreAsync();
        await store.PutAsync("digest.json", [1], CancellationToken.None);
        var version = await OnlyVersionAsync(bucket, "digest.json");

        var delete = await Record.ExceptionAsync(() => _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = "digest.json", VersionId = version }));
        var shorten = await Record.ExceptionAsync(() => _client.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = bucket,
            Key = "digest.json",
            VersionId = version,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Compliance, RetainUntilDate = DateTime.UtcNow.AddMinutes(1) },
        }));
        // Someone with write access adds a newer version and a delete marker: reads still return the anchored (oldest) version.
        await _client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "digest.json", ContentBody = "forged" });
        await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = "digest.json" });

        Assert.IsType<AmazonS3Exception>(delete);
        Assert.IsType<AmazonS3Exception>(shorten);
        Assert.Equal(new byte[] { 1 }, await store.GetAsync("digest.json", CancellationToken.None));
    }

    [Fact]
    public async Task An_oldest_version_without_compliance_retention_is_refused()
    {
        var bucket = await s3.CreateBucketAsync();
        await _client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "digest.json", ContentBody = "not locked" });
        var store = await S3WormStore.CreateAsync(_client, bucket, RetentionDays, SystemClock.Instance, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync("digest.json", CancellationToken.None));
    }

    [Fact]
    public async Task A_bucket_without_object_lock_or_a_retention_under_one_day_is_refused()
    {
        var plain = await s3.CreateBucketAsync(objectLock: false);
        var locked = await s3.CreateBucketAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => S3WormStore.CreateAsync(_client, plain, RetentionDays, SystemClock.Instance, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => S3WormStore.CreateAsync(_client, locked, 0, SystemClock.Instance, CancellationToken.None));
    }

    [Fact]
    public async Task Daily_digest_to_S3_verifies_and_is_not_rewritten()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        var today = BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);
        await h.RunAsync(new TestReceiveStock(h.CompanyId, h.SessionId, "r", s.LocationA, s.ItemId, 1m, 10.00m, today), new TestReceiveStockHandler());
        await new LedgerSealer(h.Sealer, h.Clock).SealAllAsync(CancellationToken.None);
        var (store, _) = await StoreAsync();
        var digester = new LedgerDigester(h.Sealer, store, new DigestSigner(_key));

        var first = await digester.DigestChainAsync(h.CompanyId, Chains.Gl, today, CancellationToken.None);
        var again = await digester.DigestChainAsync(h.CompanyId, Chains.Gl, today, CancellationToken.None);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var result = await h.RunAsync(new VerifyHashChain(h.CompanyId, controller, "verify"), new VerifyHashChainHandler(store, new DigestSigner(_key)));
        var report = JsonDocument.Parse(result.ResultPayload).RootElement;

        Assert.True(first.Created);
        Assert.False(again.Created);
        Assert.True(report.GetProperty("valid").GetBoolean(), report.GetRawText());
    }
}
