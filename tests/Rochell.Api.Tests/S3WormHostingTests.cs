using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Rochell.Api.Http;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>B-03 (E-B03-3, E-B03-4): the API host uses S3 Object Lock for WORM in any environment, and fails closed otherwise.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class S3WormHostingTests(PostgresFixture postgres, S3WormFixture s3) : IClassFixture<S3WormFixture>, IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose() => _key.Dispose();

    private Dictionary<string, string?> Settings(string bucket) => new()
    {
        ["Rochell:Digest:Enabled"] = "true",
        ["Rochell:Digest:SigningKeyPem"] = _key.ExportPkcs8PrivateKeyPem(),
        ["Rochell:Audit:DigestPublicKeyPem"] = _key.ExportSubjectPublicKeyInfoPem(),
        ["Rochell:Audit:S3:Bucket"] = bucket,
        ["Rochell:Audit:S3:Region"] = S3WormFixture.Region,
        ["Rochell:Audit:S3:ServiceUrl"] = s3.ServiceUrl,
        ["Rochell:Audit:S3:RetentionDays"] = "7",
        ["Rochell:Audit:S3:AccessKeyId"] = S3WormFixture.AccessKey,
        ["Rochell:Audit:S3:SecretAccessKey"] = S3WormFixture.SecretKey,
    };

    [Fact]
    public async Task Outside_TEST_the_digest_starts_and_chains_verify_against_S3()
    {
        await using var h = await TestHarness.CreateAsync(postgres); // not TEST: the file-system store would be refused
        using var api = new ApiHost(h, settings: Settings(await s3.CreateBucketAsync()));
        var controller = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CONTROLLER"));

        var verify = await controller.OkAsync(h.CompanyId, "audit", "verify-hash-chain", new { });

        Assert.True(verify.GetProperty("result").GetProperty("valid").GetBoolean());
        Assert.DoesNotContain(api.Logs, l => l.Level == LogLevel.Critical);
    }

    [Fact]
    public async Task Digest_keys_can_come_from_files()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var directory = Directory.CreateTempSubdirectory("rochell-keys-");
        try
        {
            var signing = Path.Combine(directory.FullName, "digest-signing.pem");
            var verifying = Path.Combine(directory.FullName, "digest-public.pem");
            await File.WriteAllTextAsync(signing, _key.ExportPkcs8PrivateKeyPem());
            await File.WriteAllTextAsync(verifying, _key.ExportSubjectPublicKeyInfoPem());
            var settings = Settings(await s3.CreateBucketAsync());
            settings.Remove("Rochell:Digest:SigningKeyPem");
            settings.Remove("Rochell:Audit:DigestPublicKeyPem");
            settings["Rochell:Digest:SigningKeyPemFile"] = signing;
            settings["Rochell:Audit:DigestPublicKeyPemFile"] = verifying;
            using var api = new ApiHost(h, settings: settings);
            var controller = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CONTROLLER"));

            var verify = await controller.OkAsync(h.CompanyId, "audit", "verify-hash-chain", new { });

            Assert.True(verify.GetProperty("result").GetProperty("valid").GetBoolean());
            Assert.DoesNotContain(api.Logs, l => l.Level == LogLevel.Critical);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_missing_key_file_stops_the_host()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var settings = Settings(await s3.CreateBucketAsync());
        settings.Remove("Rochell:Digest:SigningKeyPem");
        settings["Rochell:Digest:SigningKeyPemFile"] = "/nonexistent/digest-signing.pem";
        using var api = new ApiHost(h, settings: settings);

        var ex = Assert.ThrowsAny<Exception>(() => api.CreateClient());
        Assert.Contains("SigningKeyPemFile", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bucket_without_object_lock_leaves_verification_unavailable()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h, settings: Settings(await s3.CreateBucketAsync(objectLock: false)));
        var controller = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CONTROLLER"));

        var verify = await controller.CommandAsync(h.CompanyId, "audit", "verify-hash-chain", new { }, "verificar");

        Assert.Equal((HttpStatusCode.ServiceUnavailable, ApiErrors.ServiceUnavailable), await verify.ProblemAsync());
        Assert.Contains(api.Logs, l => l.Level == LogLevel.Critical && l.Message.Contains("Object Lock", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Configuring_both_WORM_stores_is_refused()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.InitTestEnvironmentAsync();
        var settings = Settings(await s3.CreateBucketAsync());
        settings["Rochell:Audit:FileSystemWormRoot"] = Path.Combine(Path.GetTempPath(), "rochell-api-worm-" + Guid.NewGuid().ToString("N"));
        using var api = new ApiHost(h, settings: settings);
        var controller = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CONTROLLER"));

        var verify = await controller.CommandAsync(h.CompanyId, "audit", "verify-hash-chain", new { }, "verificar");

        Assert.Equal((HttpStatusCode.ServiceUnavailable, ApiErrors.ServiceUnavailable), await verify.ProblemAsync());
        Assert.Contains(api.Logs, l => l.Level == LogLevel.Critical && l.Message.Contains("not both", StringComparison.Ordinal));
    }
}
