using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Rochell.Api.Http;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>E-PR18-5: sealer and digest hosted by the API, switched by configuration; WORM only in TEST (B-03).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class BackgroundServiceTests(PostgresFixture postgres) : IDisposable
{
    private readonly string _wormRoot = Path.Combine(Path.GetTempPath(), "rochell-api-worm-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    [Fact]
    public async Task The_hosted_sealer_seals_what_commands_write()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h, settings: new Dictionary<string, string?> { ["Rochell:Sealer:Enabled"] = "true", ["Rochell:Sealer:Interval"] = "00:00:00.200" });
        var buyer = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("COMPRADOR"));

        await buyer.OkAsync(h.CompanyId, "master-data", "create-supplier", new { rnc = "130000011", legalName = "Agregados del Este, S.R.L." });

        for (var i = 0; i < 50 && await h.ScalarAsync<long>("SELECT count(*) FROM audit.integrity_state WHERE integrity_status <> 'SEALED'") > 0; i++)
        {
            await Task.Delay(100);
        }

        Assert.Equal("DOMAIN_EVENT:SEALED", await h.ScalarAsync<string>("SELECT string_agg(DISTINCT ledger || ':' || integrity_status, ',') FROM audit.integrity_state"));
    }

    [Trait("Acceptance", "INT-02")]
    [Fact]
    public async Task A_group_altered_before_sealing_raises_a_critical_alert_from_the_hosted_sealer()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        await h.RunAsync(new TestReceiveStock(h.CompanyId, h.SessionId, "r", s.LocationA, s.ItemId, 1m, 10.00m, BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow)), new TestReceiveStockHandler());
        await h.AdminRequireAsync("BEGIN; SET LOCAL session_replication_role = replica; UPDATE fin.gl_entry SET rule_line_code = rule_line_code || 'X'; COMMIT;");

        // The row is altered before the host (and its sealer) starts, so the first sealing pass finds it.
        using var api = new ApiHost(h, settings: new Dictionary<string, string?> { ["Rochell:Sealer:Enabled"] = "true", ["Rochell:Sealer:Interval"] = "00:00:00.200" });
        _ = api.CreateClient();
        for (var i = 0; i < 50 && !api.Logs.Any(l => l.Level == LogLevel.Critical); i++)
        {
            await Task.Delay(100);
        }

        Assert.Equal("SEAL_ERROR", await h.ScalarAsync<string>("SELECT integrity_status FROM audit.integrity_state WHERE ledger = 'GL'"));
        Assert.Contains(api.Logs, l => l.Level == LogLevel.Critical && l.Message.Contains("SEAL_ERROR", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Outside_TEST_the_digest_does_not_start_and_verification_is_unavailable()
    {
        await using var h = await TestHarness.CreateAsync(postgres); // core.deployment_environment not initialized: not TEST
        using var api = new ApiHost(h, settings: new Dictionary<string, string?>
        {
            ["Rochell:Digest:Enabled"] = "true",
            ["Rochell:Digest:SigningKeyPem"] = _key.ExportPkcs8PrivateKeyPem(),
            ["Rochell:Audit:FileSystemWormRoot"] = _wormRoot,
            ["Rochell:Audit:DigestPublicKeyPem"] = _key.ExportSubjectPublicKeyInfoPem(),
        });
        var controller = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CONTROLLER"));

        var verify = await controller.CommandAsync(h.CompanyId, "audit", "verify-hash-chain", new { }, "verificar");

        Assert.Equal((HttpStatusCode.ServiceUnavailable, ApiErrors.ServiceUnavailable), await verify.ProblemAsync());
        Assert.Contains(api.Logs, l => l.Level == LogLevel.Critical && l.Message.StartsWith("Daily digest NOT started: no WORM storage", StringComparison.Ordinal));
        Assert.False(Directory.Exists(_wormRoot));
    }

    [Fact]
    public async Task In_TEST_with_file_system_WORM_the_chains_verify_over_http()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.InitTestEnvironmentAsync();
        using var api = new ApiHost(h, settings: new Dictionary<string, string?>
        {
            ["Rochell:Audit:FileSystemWormRoot"] = _wormRoot,
            ["Rochell:Audit:DigestPublicKeyPem"] = _key.ExportSubjectPublicKeyInfoPem(),
        });
        var controller = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CONTROLLER"));

        var verify = await controller.OkAsync(h.CompanyId, "audit", "verify-hash-chain", new { });

        Assert.True(verify.GetProperty("result").GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task Without_WORM_configuration_verification_is_unavailable_even_in_TEST()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.InitTestEnvironmentAsync();
        using var api = new ApiHost(h);
        var auditor = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("AUDITOR"));

        var verify = await auditor.CommandAsync(h.CompanyId, "audit", "verify-hash-chain", new { }, "verificar");

        Assert.Equal((HttpStatusCode.ServiceUnavailable, ApiErrors.ServiceUnavailable), await verify.ProblemAsync());
    }

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(_wormRoot))
        {
            foreach (var file in Directory.EnumerateFiles(_wormRoot, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_wormRoot, recursive: true);
        }
    }
}
