using System.Security.Cryptography;
using System.Text.Json;
using Rochell.Audit;
using Rochell.Audit.Worm;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Procurement.Ledger;
using Rochell.Procurement.SupplierInvoices;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Reconciliation.Tests;

/// <summary>PR-16: reconciliations, CloseComponent (T-13, Patch 1 / 1.1) and reopening with a second approver (P-8, T-14).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class CloseTests(PostgresFixture postgres)
{
    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static DateOnly LastMonth(TestHarness h) => new DateOnly(Today(h).Year, Today(h).Month, 1).AddMonths(-1).AddDays(9);

    private static DateOnly FirstOfThisMonth(TestHarness h) => new(Today(h).Year, Today(h).Month, 1);

    private static Task<Guid> PeriodAsync(TestHarness h, DateOnly date)
        => h.ScalarAsync<Guid>("SELECT period_id FROM fin.period WHERE company_id = @c AND @d BETWEEN starts_on AND ends_on", ("c", h.CompanyId), ("d", date));

    private static Task SealAsync(TestHarness h) => new LedgerSealer(h.Sealer, h.Clock).SealAllAsync(CancellationToken.None);

    private static Task<CommandResult> Receive(TestHarness h, TestStock s, string key, DateOnly businessDate)
        => h.RunAsync(new TestReceiveStock(h.CompanyId, h.SessionId, key, s.LocationA, s.ItemId, 1m, 10.00m, businessDate), new TestReceiveStockHandler());

    private static async Task<CommandResult> Close(TestHarness h, Guid controller, DateOnly month, string component, string key)
        => await h.RunAsync(new CloseComponent(h.CompanyId, controller, key, await PeriodAsync(h, month), component), new CloseComponentHandler());

    private static async Task<JsonElement> ReconcileAsync(TestHarness h, Guid controller, string key)
        => JsonDocument.Parse((await h.RunAsync(new RunReconciliation(h.CompanyId, controller, key), new RunReconciliationHandler())).ResultPayload).RootElement;

    private static JsonElement Run(JsonElement result, string code)
        => result.GetProperty("runs").EnumerateArray().Single(r => r.GetProperty("code").GetString() == code);

    private static Task Tamper(TestHarness h, string sql) => h.AdminRequireAsync($"BEGIN; SET LOCAL session_replication_role = replica; {sql}; COMMIT;");

    private static Task<string?> StatusAsync(TestHarness h, DateOnly month, string component)
        => h.ScalarAsync<string>(
            "SELECT s.status FROM fin.close_component_state s JOIN fin.period p USING (period_id) WHERE p.company_id = @c AND @d BETWEEN p.starts_on AND p.ends_on AND s.component = @k",
            ("c", h.CompanyId),
            ("d", month),
            ("k", component));

    [Fact]
    public async Task A_consistent_slice_reconciles_with_no_findings()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await h.EnableInvoicePostingAsync();   // includes the INVENTORY policy that GRNI-AGING reads
        var si = (await h.RunAsync(
            new RegisterSupplierInvoice(h.CompanyId, s.Clerk, "r", s.Purchasing.SupplierId, "B0100000001", Today(h), Today(h).AddDays(30), [new(s.PoLineId, 6m, 1500m)]),
            new RegisterSupplierInvoiceHandler())).ResultRef;
        await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, s.Clerk, "m", si, 1), new MatchSupplierInvoiceHandler());
        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, s.Clerk, "p", si, 2), new PostSupplierInvoiceHandler());

        var result = await ReconcileAsync(h, s.Purchasing.Controller, "rec");

        Assert.All(result.GetProperty("runs").EnumerateArray(), r => Assert.Equal("MATCHED", r.GetProperty("status").GetString()));
        Assert.Equal(8L, await h.ScalarAsync<long>("SELECT count(*) FROM rec.recon_run"));
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM rec.recon_exception"));
    }

    [Trait("Acceptance", "PD-02")]
    [Fact]
    public async Task PD02_an_unsealed_group_of_the_period_blocks_the_close_until_the_sealer_runs()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        await Receive(h, s, "last-month", LastMonth(h));

        var unsealed = await Assert.ThrowsAsync<DomainException>(() => Close(h, controller, LastMonth(h), "INV-MOV", "c1"));
        await SealAsync(h);
        await Close(h, controller, LastMonth(h), "INV-MOV", "c2");
        await Receive(h, s, "late", LastMonth(h));

        Assert.Equal(ReconciliationErrors.IntegrityNotSealed, unsealed.Code);
        Assert.Equal("CLOSED", await StatusAsync(h, LastMonth(h), "INV-MOV"));
        Assert.True(await h.ScalarAsync<bool>(
            "SELECT s.snapshot_hash = n.content_hash FROM fin.close_component_state s JOIN fin.close_snapshot n USING (period_id, component) WHERE s.component = 'INV-MOV' AND s.status = 'CLOSED'"));
        Assert.Equal($"{FirstOfThisMonth(h):yyyy-MM-dd}|true", await h.ScalarAsync<string>(
            "SELECT posting_date::text || '|' || late_entry FROM fin.gl_journal ORDER BY occurred_at DESC LIMIT 1"));
    }

    [Trait("Acceptance", "INT-02")]
    [Fact]
    public async Task INT02_a_group_altered_before_sealing_is_a_SEAL_ERROR_that_blocks_the_close()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        await Receive(h, s, "last-month", LastMonth(h));
        var journal = await h.ScalarAsync<Guid>("SELECT journal_id FROM fin.gl_journal");
        await Tamper(h, $"UPDATE fin.gl_entry SET rule_line_code = rule_line_code || 'X' WHERE journal_id = '{journal}'");

        await SealAsync(h);
        var blocked = await Assert.ThrowsAsync<DomainException>(() => Close(h, controller, LastMonth(h), "INV-MOV", "c"));

        Assert.Equal("SEAL_ERROR", await h.ScalarAsync<string>("SELECT integrity_status FROM audit.integrity_state WHERE ledger = 'GL' AND group_ref = @g", ("g", journal)));
        Assert.Equal(ReconciliationErrors.IntegrityNotSealed, blocked.Code);
        Assert.Equal("OPEN", await StatusAsync(h, LastMonth(h), "INV-MOV"));
    }

    [Trait("Acceptance", "INT-03")]
    [Fact]
    public async Task INT03_one_unsealed_domain_event_of_the_period_blocks_the_close_until_it_is_sealed()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        await Receive(h, s, "last-month", LastMonth(h));
        await SealAsync(h);

        // A command with only a domain event (no ledger rows) whose business date falls in the period.
        var lastMonthUtc = LastMonth(h).ToDateTime(new TimeOnly(15, 0), DateTimeKind.Utc);
        await h.RunAsync(h.Ping("event-only", occurredAt: lastMonthUtc), new PingHandler());
        var pending = await h.ScalarAsync<string>(
            "SELECT string_agg(ledger || ':' || integrity_status || ':' || (posting_date IS NULL), ',') FROM audit.integrity_state WHERE integrity_status <> 'SEALED'");
        var blocked = await Assert.ThrowsAsync<DomainException>(() => Close(h, controller, LastMonth(h), "INV-MOV", "c1"));
        await SealAsync(h);
        await Close(h, controller, LastMonth(h), "INV-MOV", "c2");

        Assert.Equal("DOMAIN_EVENT:PENDING_SEAL:true", pending);
        Assert.Equal(ReconciliationErrors.IntegrityNotSealed, blocked.Code);
        Assert.Equal("CLOSED", await StatusAsync(h, LastMonth(h), "INV-MOV"));
    }

    [Trait("Acceptance", "CC-05")]
    [Fact]
    public async Task CC05_a_posting_into_a_period_being_closed_waits_and_lands_as_a_late_entry()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        var period = await PeriodAsync(h, LastMonth(h));
        await using var closing = await h.Admin.OpenConnectionAsync();
        await using var tx = await closing.BeginTransactionAsync();
        await using (var lockCommand = closing.CreateCommand())
        {
            lockCommand.Transaction = tx;
            lockCommand.CommandText = $"SELECT pg_advisory_xact_lock(hashtextextended('period:' || '{h.CompanyId}'::uuid || ':' || '{period}'::uuid || ':' || 'INV-MOV', 0))";
            await lockCommand.ExecuteNonQueryAsync();
        }

        var posting = Receive(h, s, "during-close", LastMonth(h));
        await Task.Delay(TimeSpan.FromSeconds(2));
        var waited = !posting.IsCompleted;
        await using (var closeCommand = closing.CreateCommand())
        {
            closeCommand.Transaction = tx;
            closeCommand.CommandText =
                $"UPDATE fin.close_component_state SET status = 'CLOSED', version = version + 1, closed_by = '{h.UserId}', closed_at = now(), snapshot_hash = sha256('fixture') WHERE period_id = '{period}' AND component = 'INV-MOV'";
            await closeCommand.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        await posting.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(waited);
        Assert.Equal($"{FirstOfThisMonth(h):yyyy-MM-dd}|true", await h.ScalarAsync<string>("SELECT posting_date::text || '|' || late_entry FROM fin.gl_journal"));
    }

    [Trait("Acceptance", "IV-03")]
    [Fact]
    public async Task IV03_an_orphan_value_blocks_INV_MOV_until_R06_removes_it()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await h.EnableReallocationAsync(approveR02B: false);
        await h.EnableTestIssueAsync();
        await h.CreateActiveMapAsync("TEST_INCOME", await h.CreateAccountAsync("4190", "Ingreso de prueba", isControl: false));
        await h.CreateActivePolicyAsync("INVENTORY", PolicySetup.Inventory);
        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id IN ('0192f001-0000-7000-8000-000000000008', '0192f000-0000-7000-8000-0000000000f3') AND version = 1");
        var area = await h.ScalarAsync<Guid>("SELECT valuation_area_id FROM inv.inv_valuation_balance");
        var lot = await h.ScalarAsync<Guid>("SELECT lot_id FROM inv.lot");
        await h.RunAsync(new TestIssueStock(h.CompanyId, h.SessionId, "issue", s.Receiving.LocationA, s.Purchasing.Sand, lot, 6m, Today(h)), new TestIssueStockHandler());
        await h.RunAsync(new TestAdjustValue(h.CompanyId, h.SessionId, "orphan", area, s.Purchasing.PlantId, s.Purchasing.Sand, 0.37m, Today(h)), new TestAdjustValueHandler());
        await SealAsync(h);

        var blocked = await Assert.ThrowsAsync<DomainException>(() => Close(h, s.Purchasing.Controller, LastMonth(h), "INV-MOV", "c1"));
        await h.RunAsync(new ApproveValuationResidualAdjustment(h.CompanyId, s.Purchasing.Controller, "r06", area, s.Purchasing.Sand, "Residuo"), new ApproveValuationResidualAdjustmentHandler());
        await SealAsync(h);
        await Close(h, s.Purchasing.Controller, LastMonth(h), "INV-MOV", "c2");

        Assert.Equal(ReconciliationErrors.ReconciliationErrorsFound, blocked.Code);
        Assert.Contains("VAL-RESIDUAL", blocked.Message, StringComparison.Ordinal);
        Assert.Equal("CLOSED", await StatusAsync(h, LastMonth(h), "INV-MOV"));
    }

    [Trait("Acceptance", "AT-07")]
    [Fact]
    public async Task AT07_a_journal_deleted_by_a_superuser_is_an_evidence_error_and_breaks_the_hash_chain()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await SealAsync(h);
        var journal = await h.ScalarAsync<Guid>("SELECT j.journal_id FROM fin.gl_journal j JOIN pur.goods_receipt g ON g.posting_event_id = j.source_event_id");

        await Tamper(h, $"DELETE FROM fin.gl_entry WHERE journal_id = '{journal}'; DELETE FROM fin.gl_journal WHERE journal_id = '{journal}'");

        var result = await ReconcileAsync(h, s.Purchasing.Controller, "rec");
        Assert.Equal("EXCEPTIONS", Run(result, "ACC-EVIDENCE").GetProperty("status").GetString());
        Assert.Equal($"GR:{s.GoodsReceiptId}|POSTED_WITHOUT_JOURNAL", await h.ScalarAsync<string>(
            "SELECT e.match_key || '|' || e.classification FROM rec.recon_exception e JOIN rec.recon_run r USING (run_id) WHERE r.recon_code = 'ACC-EVIDENCE'"));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var worm = FileSystemWormStore.Create(Path.Combine(Path.GetTempPath(), "rochell-worm-" + Guid.NewGuid().ToString("N")), "TEST");
        var verification = JsonDocument.Parse((await h.RunAsync(new VerifyHashChain(h.CompanyId, s.Purchasing.Controller, "verify"), new VerifyHashChainHandler(worm, new DigestSigner(key)))).ResultPayload).RootElement;
        Assert.False(verification.GetProperty("valid").GetBoolean());

        // The evidence error belongs to INV-MOV: accounts payable still closes, inventory does not.
        var inventory = await Assert.ThrowsAsync<DomainException>(() => Close(h, s.Purchasing.Controller, LastMonth(h), "INV-MOV", "c1"));
        await Close(h, s.Purchasing.Controller, LastMonth(h), "AP-REC", "c2");
        Assert.Equal(ReconciliationErrors.ReconciliationErrorsFound, inventory.Code);
        Assert.Equal("CLOSED", await StatusAsync(h, LastMonth(h), "AP-REC"));
    }

    [Trait("Acceptance", "RO-01")]
    [Fact]
    public async Task RO01_reopening_needs_a_second_approver_and_a_reclose_leaves_its_own_snapshot()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var director = await h.SessionWithRolesAsync("SEGUNDO_APROBADOR_CIERRE");
        await SealAsync(h);
        await Close(h, controller, LastMonth(h), "INV-MOV", "c1");
        var period = await PeriodAsync(h, LastMonth(h));

        var request = JsonDocument.Parse((await h.RunAsync(new RequestReopen(h.CompanyId, controller, "rq1", period, "INV-MOV", "Ajuste omitido"), new RequestReopenHandler())).ResultPayload)
            .RootElement.GetProperty("requestId").GetGuid();
        var duplicate = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RequestReopen(h.CompanyId, controller, "rq2", period, "INV-MOV", "Otra"), new RequestReopenHandler()));
        var self = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveReopen(h.CompanyId, controller, "ap0", request), new ApproveReopenHandler()));
        Assert.Equal("CLOSED", await StatusAsync(h, LastMonth(h), "INV-MOV"));
        await h.RunAsync(new ApproveReopen(h.CompanyId, director, "ap1", request), new ApproveReopenHandler());
        Assert.Equal("REOPENED", await StatusAsync(h, LastMonth(h), "INV-MOV"));
        await Receive(h, s, "in-reopened", LastMonth(h));
        await SealAsync(h);
        await Close(h, controller, LastMonth(h), "INV-MOV", "c2");
        var second = JsonDocument.Parse((await h.RunAsync(new RequestReopen(h.CompanyId, controller, "rq3", period, "INV-MOV", "Revisión"), new RequestReopenHandler())).ResultPayload)
            .RootElement.GetProperty("requestId").GetGuid();
        await h.RunAsync(new RejectReopen(h.CompanyId, director, "rj1", second, "No procede"), new RejectReopenHandler());

        Assert.Equal(ReconciliationErrors.ReopenAlreadyRequested, duplicate.Code);
        Assert.Equal(AuthorizationErrors.NotAuthorized, self.Code);
        Assert.Equal($"{LastMonth(h):yyyy-MM-dd}|false", await h.ScalarAsync<string>("SELECT posting_date::text || '|' || late_entry FROM fin.gl_journal"));
        Assert.Equal(2L, await h.ScalarAsync<long>("SELECT count(*) FROM fin.close_snapshot"));
        Assert.Equal("CLOSED|APPROVED,REJECTED", await h.ScalarAsync<string>(
            "SELECT (SELECT status FROM fin.close_component_state WHERE period_id = @p AND component = 'INV-MOV') || '|' || string_agg(status, ',' ORDER BY status) FROM fin.reopen_request",
            ("p", period)));
    }

    [Fact]
    public async Task A_period_closes_only_after_it_ends_once_and_by_someone_allowed_to()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.CreateStockSetupAsync();
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        await SealAsync(h);

        var running = await Assert.ThrowsAsync<DomainException>(() => Close(h, controller, Today(h), "INV-MOV", "c0"));
        var unknown = await Assert.ThrowsAsync<DomainException>(() => Close(h, controller, LastMonth(h), "GL", "c1"));
        await Close(h, controller, LastMonth(h), "INV-MOV", "c2");
        var twice = await Assert.ThrowsAsync<DomainException>(() => Close(h, controller, LastMonth(h), "INV-MOV", "c3"));
        var noPermission = await Assert.ThrowsAsync<DomainException>(() => Close(h, h.SessionId, LastMonth(h), "AP-REC", "c4"));

        Assert.Equal(ReconciliationErrors.PeriodNotEnded, running.Code);
        Assert.Equal(ReconciliationErrors.UnknownComponent, unknown.Code);
        Assert.Equal(ReconciliationErrors.ComponentNotOpen, twice.Code);
        Assert.Equal(AuthorizationErrors.NotAuthorized, noPermission.Code);
    }

    [Fact]
    public async Task Aged_GRNI_is_a_warning_that_blocks_no_component()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var s = await h.CreateInvoicingSetupAsync();
        await h.CreateActivePolicyAsync("INVENTORY", PolicySetup.Inventory, from: Today(h).AddDays(-1));
        clock.Advance(TimeSpan.FromDays(61));
        var controller = await h.SessionWithRolesAsync("CONTROLLER");   // sessions last 12 h: a new one after the clock moves

        var result = await ReconcileAsync(h, controller, "rec");

        Assert.Equal("EXCEPTIONS", Run(result, "GRNI-AGING").GetProperty("status").GetString());
        Assert.Equal(1, Run(result, "GRNI-AGING").GetProperty("warnings").GetInt32());
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM rec.recon_blocking WHERE recon_code = 'GRNI-AGING'"));
    }
}
