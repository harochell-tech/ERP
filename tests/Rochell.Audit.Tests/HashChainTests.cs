using System.Security.Cryptography;
using System.Text.Json;
using Rochell.Audit.Worm;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Procurement.SupplierInvoices;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Audit.Tests;

/// <summary>PR-15: integrity state, sealing, daily digests anchored in WORM and verification (HS-01, HS-02, E-PR15-1…8).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class HashChainTests(PostgresFixture postgres) : IDisposable
{
    private readonly string _wormRoot = Path.Combine(Path.GetTempPath(), "rochell-worm-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

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

    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private IWormStore Worm() => FileSystemWormStore.Create(_wormRoot, "TEST");

    private static Task<CommandResult> Receive(TestHarness h, TestStock s, string key)
        => h.RunAsync(new TestReceiveStock(h.CompanyId, h.SessionId, key, s.LocationA, s.ItemId, 1m, 10.00m, Today(h)), new TestReceiveStockHandler());

    private async Task<JsonElement> VerifyAsync(TestHarness h, string key, ECDsa? key2 = null)
    {
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var result = await h.RunAsync(new VerifyHashChain(h.CompanyId, controller, key), new VerifyHashChainHandler(Worm(), new DigestSigner(key2 ?? _key)));
        return JsonDocument.Parse(result.ResultPayload).RootElement;
    }

    private static JsonElement Chain(JsonElement report, string ledger)
        => report.GetProperty("chains").EnumerateArray().Single(c => c.GetProperty("ledger").GetString() == ledger);

    private static Task<string?> StatusesAsync(TestHarness h)
        => h.ScalarAsync<string>("SELECT string_agg(DISTINCT integrity_status, ',') FROM audit.integrity_state");

    /// <summary>Tampering as a superuser: triggers are off for the statement, exactly what the hash chain must still catch.</summary>
    private static Task Tamper(TestHarness h, string sql) => h.AdminRequireAsync($"BEGIN; SET LOCAL session_replication_role = replica; {sql}; COMMIT;");

    [Fact]
    public async Task Every_row_the_slice_writes_seals_and_verifies()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await h.EnableInvoicePostingAsync();
        var si = (await h.RunAsync(
            new RegisterSupplierInvoice(h.CompanyId, s.Clerk, "r", s.Purchasing.SupplierId, "B0100000001", Today(h), Today(h).AddDays(30), [new(s.PoLineId, 6m, 1600m)]),
            new RegisterSupplierInvoiceHandler())).ResultRef;
        await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, s.Clerk, "m", si, 1), new MatchSupplierInvoiceHandler());
        await h.RunAsync(new ApproveMatchException(h.CompanyId, s.Purchasing.Controller, "a", si, 2, "Precio pactado"), new ApproveMatchExceptionHandler());
        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, s.Clerk, "p", si, 3), new PostSupplierInvoiceHandler());
        await h.RunAsync(new ReverseSupplierInvoice(h.CompanyId, s.Purchasing.Controller, "v", si, 4, "Anulada por el proveedor"), new ReverseSupplierInvoiceHandler());
        Assert.Equal("PENDING_SEAL", await StatusesAsync(h));

        var passes = await new LedgerSealer(h.Sealer, h.Clock).SealAllAsync(CancellationToken.None);

        Assert.Equal(0, passes.Sum(p => p.Failed));
        Assert.Equal("SEALED", await StatusesAsync(h));
        Assert.True(await h.ScalarAsync<long>("SELECT count(*) FROM audit.ledger_seal WHERE ledger = 'GL'") >= 4);
        var report = await VerifyAsync(h, "verify");
        Assert.True(report.GetProperty("valid").GetBoolean(), report.GetRawText());
    }

    [Fact]
    public async Task HS01_two_hundred_concurrent_postings_seal_into_a_valid_chain()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        var sealer = new LedgerSealer(h.Sealer, h.Clock);
        using var stop = new CancellationTokenSource();
        var sealing = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await sealer.SealAllAsync(CancellationToken.None);
            }
        });

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Receive(h, s, $"hs01-{i}")));
        await stop.CancelAsync();
        await sealing;
        await sealer.SealAllAsync(CancellationToken.None);

        Assert.Equal("SEALED", await StatusesAsync(h));
        Assert.Equal(200L, await h.ScalarAsync<long>("SELECT max(ledger_sequence) FROM audit.ledger_seal WHERE ledger = 'GL'"));
        Assert.True((await VerifyAsync(h, "verify")).GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task HS01_a_command_commits_while_a_sealing_transaction_is_open()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        await Receive(h, s, "first");
        var sealer = new LedgerSealer(h.Sealer, h.Clock);

        var pass = await sealer.SealChainAsync(h.CompanyId, Chains.Gl, CancellationToken.None, async () =>
            await Receive(h, s, "during-sealing").WaitAsync(TimeSpan.FromSeconds(30)));
        await sealer.SealAllAsync(CancellationToken.None);

        Assert.Equal(1, pass.Sealed);
        Assert.Equal("SEALED", await StatusesAsync(h));
        Assert.Equal(2L, await h.ScalarAsync<long>("SELECT count(*) FROM audit.ledger_seal WHERE ledger = 'GL'"));
    }

    [Fact]
    public async Task HS02_an_amount_altered_by_a_superuser_is_reported_at_its_sequence()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        await Receive(h, s, "one");
        await Receive(h, s, "two");
        await Receive(h, s, "three");
        await new LedgerSealer(h.Sealer, h.Clock).SealAllAsync(CancellationToken.None);
        var second = await h.ScalarAsync<Guid>("SELECT group_ref FROM audit.ledger_seal WHERE ledger = 'GL' AND ledger_sequence = 2");

        await Tamper(h, $"UPDATE fin.gl_entry SET debit = debit + 1 WHERE journal_id = '{second}' AND debit > 0");

        var report = await VerifyAsync(h, "verify");
        Assert.False(report.GetProperty("valid").GetBoolean());
        Assert.Equal(2, Chain(report, Chains.Gl).GetProperty("firstInvalidSequence").GetInt64());
        Assert.True(Chain(report, Chains.DomainEvent).GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task A_journal_deleted_with_its_seal_leaves_a_gap_and_the_WORM_digest_disagrees()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        await Receive(h, s, "one");
        await Receive(h, s, "two");
        await Receive(h, s, "three");
        await new LedgerSealer(h.Sealer, h.Clock).SealAllAsync(CancellationToken.None);
        await new LedgerDigester(h.Sealer, Worm(), new DigestSigner(_key)).DigestDayAsync(Today(h), CancellationToken.None);
        var second = await h.ScalarAsync<Guid>("SELECT group_ref FROM audit.ledger_seal WHERE ledger = 'GL' AND ledger_sequence = 2");

        await Tamper(h, $"DELETE FROM fin.gl_entry WHERE journal_id = '{second}'; DELETE FROM fin.gl_journal WHERE journal_id = '{second}'; DELETE FROM audit.ledger_seal WHERE ledger = 'GL' AND ledger_sequence = 2");

        var gl = Chain(await VerifyAsync(h, "verify"), Chains.Gl);
        Assert.Equal("2-2", gl.GetProperty("gaps")[0].GetString());
        Assert.Equal(3, gl.GetProperty("firstInvalidSequence").GetInt64());
        Assert.Contains("Merkle root differs from WORM", gl.GetProperty("digestMismatches")[0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_group_altered_before_sealing_is_a_SEAL_ERROR_and_the_chain_goes_on()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        await Receive(h, s, "one");
        await Receive(h, s, "two");
        var first = await h.ScalarAsync<Guid>("SELECT journal_id FROM fin.gl_journal ORDER BY journal_id LIMIT 1");
        await Tamper(h, $"UPDATE fin.gl_entry SET rule_line_code = rule_line_code || 'X' WHERE journal_id = '{first}'");

        var passes = await new LedgerSealer(h.Sealer, h.Clock).SealAllAsync(CancellationToken.None);

        Assert.Equal(1, passes.Single(p => p.Ledger == Chains.Gl).Failed);
        Assert.Equal("SEAL_ERROR", await h.ScalarAsync<string>("SELECT integrity_status FROM audit.integrity_state WHERE ledger = 'GL' AND group_ref = @g", ("g", first)));
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM audit.ledger_seal WHERE ledger = 'GL'"));
        var gl = Chain(await VerifyAsync(h, "verify"), Chains.Gl);
        Assert.Equal(first, gl.GetProperty("sealErrors")[0].GetGuid());
        Assert.False(gl.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task A_sealed_group_cannot_receive_rows_and_the_application_cannot_write_integrity_data()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        await Receive(h, s, "one");
        await new LedgerSealer(h.Sealer, h.Clock).SealAllAsync(CancellationToken.None);
        var command = await h.ScalarAsync<Guid>("SELECT command_id FROM core.domain_event LIMIT 1");

        var grows = await h.AdminExecuteAsync(
            $"""
            INSERT INTO core.domain_event (event_id, company_id, command_id, command_event_index, event_type, schema_version, aggregate_type,
              aggregate_id, aggregate_version, event_sequence, occurred_at, recorded_at, business_date, session_id, correlation_id, payload, row_hash)
            SELECT gen_random_uuid(), company_id, command_id, 99, 'Forged', 1, 'Forged', gen_random_uuid(), 1, 1, now(), now(), business_date,
              session_id, correlation_id, '{"{}"}', row_hash FROM core.domain_event WHERE command_id = '{command}' LIMIT 1
            """);

        Assert.Contains("already SEALED", grows?.MessageText, StringComparison.Ordinal);
        await using var app = await h.App.OpenConnectionAsync();
        await using var update = app.CreateCommand();
        update.CommandText = "UPDATE audit.integrity_state SET updated_at = now()";
        var denied = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => update.ExecuteNonQueryAsync());
        Assert.Equal("42501", denied.SqlState);
    }

    [Fact]
    public async Task Daily_digests_are_written_once_to_WORM_and_chained_day_to_day()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var s = await h.CreateStockSetupAsync();
        var yesterdayClock = new FakeClock();
        yesterdayClock.Advance(TimeSpan.FromDays(-1));
        await Receive(h, s, "one");
        await new LedgerSealer(h.Sealer, yesterdayClock).SealAllAsync(CancellationToken.None);
        await Receive(h, s, "two");
        await new LedgerSealer(h.Sealer, clock).SealAllAsync(CancellationToken.None);
        var digester = new LedgerDigester(h.Sealer, Worm(), new DigestSigner(_key));
        var yesterday = BusinessCalendar.DefaultBusinessDate(yesterdayClock.UtcNow);

        var first = await digester.DigestChainAsync(h.CompanyId, Chains.Gl, yesterday, CancellationToken.None);
        var second = await digester.DigestChainAsync(h.CompanyId, Chains.Gl, Today(h), CancellationToken.None);
        var again = await digester.DigestChainAsync(h.CompanyId, Chains.Gl, Today(h), CancellationToken.None);

        Assert.True(first.Created);
        Assert.True(second.Created);
        Assert.False(again.Created);
        Assert.True(await h.ScalarAsync<bool>(
            "SELECT b.prev_digest_hash = a.digest_hash AND a.prev_digest_hash IS NULL FROM audit.ledger_digest a JOIN audit.ledger_digest b ON b.digest_date > a.digest_date WHERE a.ledger = 'GL' AND b.ledger = 'GL'"));
        var key = DigestDocument.WormKey(h.CompanyId, Chains.Gl, Today(h));
        await Assert.ThrowsAsync<WormObjectExistsException>(() => Worm().PutAsync(key, [1], CancellationToken.None));
        Assert.True((await VerifyAsync(h, "verify")).GetProperty("valid").GetBoolean());
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = Chain(await VerifyAsync(h, "verify-other-key", otherKey), Chains.Gl);
        Assert.Contains("WORM signature invalid", forged.GetProperty("digestMismatches")[0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_file_system_WORM_store_is_refused_outside_TEST()
    {
        Assert.Throws<InvalidOperationException>(() => FileSystemWormStore.Create(_wormRoot, "PRODUCTION"));
        Assert.Throws<InvalidOperationException>(() => FileSystemWormStore.Create(_wormRoot, deploymentEnvironment: null));
    }

    [Fact]
    public async Task Verification_needs_hash_verify()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        var denied = await Assert.ThrowsAsync<DomainException>(() =>
            h.RunAsync(new VerifyHashChain(h.CompanyId, h.SessionId, "v"), new VerifyHashChainHandler(Worm(), new DigestSigner(_key))));

        Assert.Equal(AuthorizationErrors.NotAuthorized, denied.Code);
    }
}
