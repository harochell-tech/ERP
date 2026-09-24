using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Procurement.GoodsReceipts;
using Rochell.Procurement.Ledger;
using Rochell.Procurement.PurchaseOrders;
using Rochell.Procurement.SupplierInvoices;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>T-11 RepostEvent (AT-06, E-PR14-2/3) and T-12 ApproveValuationResidualAdjustment (IV-03 adjustment, E-PR14-4/5).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class LedgerMaintenanceTests(PostgresFixture postgres)
{
    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static string Account(string code) =>
        $"(SELECT coalesce(sum(e.debit - e.credit), 0) FROM fin.gl_entry e JOIN fin.account a USING (account_id) WHERE a.code = '{code}')";

    /// <summary>Moves GRNI from its current account to a new one from today: the "wrong mapping" of AT-06 gets fixed.</summary>
    private static async Task RemapGrniAsync(TestHarness h)
    {
        await h.AdminRequireAsync($"UPDATE fin.account_role_map SET effective_to = '{Today(h):yyyy-MM-dd}' WHERE account_role = 'GRNI' AND status = 'ACTIVE'");
        await h.CreateActiveMapAsync("GRNI", await h.CreateAccountAsync("2106", "Recibido no facturado (correcta)", isControl: false), from: Today(h));
    }

    private static Task<CommandResult> Repost(TestHarness h, Guid session, Guid sourceEvent, string rule, string key)
        => h.RunAsync(new RepostEvent(h.CompanyId, session, key, sourceEvent, rule, "Mapeo de GRNI equivocado"), new RepostEventHandler());

    [Trait("Acceptance", "AT-06")]
    [Fact]
    public async Task AT06_repost_moves_the_journal_to_the_current_mapping_and_the_receipt_can_still_be_reversed()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();                 // receipt 6 t × 1 500 on GRNI account 2105
        await h.EnableReallocationAsync();
        var postingEvent = await h.ScalarAsync<Guid>("SELECT posting_event_id FROM pur.goods_receipt");
        await RemapGrniAsync(h);

        var result = JsonDocument.Parse((await Repost(h, s.Purchasing.Controller, postingEvent, "R-01", "repost")).ResultPayload).RootElement;

        Assert.Equal(2, result.GetProperty("generation").GetInt32());
        Assert.Equal("1,2", await h.ScalarAsync<string>(
            "SELECT string_agg(posting_generation::text, ',' ORDER BY posting_generation) FROM fin.gl_journal WHERE source_event_id = @e AND journal_type = 'AUTO'", ("e", postingEvent)));
        Assert.Equal("0.0000|-9000.0000|9000.0000|6.000000/9000.0000", await h.ScalarAsync<string>(
            $"SELECT {Account("2105")} || '|' || {Account("2106")} || '|' || {Account("1301")} || '|' || (SELECT quantity || '/' || value FROM inv.inv_valuation_balance)"));
        Assert.Equal("-9000.0000,9000.0000", await h.ScalarAsync<string>(
            "SELECT string_agg(amount::text, ',' ORDER BY amount) FROM inv.inv_value_entry WHERE movement_type::text = 'REPOST'"));

        // The reposted receipt still reverses: the engine follows the repost chain back to the receipt's value entry.
        await h.RunAsync(new ReverseGoodsReceipt(h.CompanyId, s.Purchasing.Controller, "rev", s.GoodsReceiptId, "Recepción duplicada"), new ReverseGoodsReceiptHandler());

        Assert.Equal("0.0000|0.0000|0.0000|0.000000/0.0000", await h.ScalarAsync<string>(
            $"SELECT {Account("2105")} || '|' || {Account("2106")} || '|' || {Account("1301")} || '|' || (SELECT quantity || '/' || value FROM inv.inv_valuation_balance)"));
    }

    [Fact]
    public async Task A_reposted_price_difference_still_reverses_with_its_invoice()
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
        var postingEvent = await h.ScalarAsync<Guid>("SELECT posting_event_id FROM pur.supplier_invoice");

        await Repost(h, s.Purchasing.Controller, postingEvent, "R-05", "repost-r05");
        await Repost(h, s.Purchasing.Controller, postingEvent, "R-05", "repost-r05-again");   // a chain of two reposts
        await h.RunAsync(new ReverseSupplierInvoice(h.CompanyId, s.Purchasing.Controller, "rev", si, 4, "Factura anulada por el proveedor"), new ReverseSupplierInvoiceHandler());

        Assert.Equal("1,2,3", await h.ScalarAsync<string>(
            "SELECT string_agg(j.posting_generation::text, ',' ORDER BY j.posting_generation) FROM fin.gl_journal j JOIN fin.posting_rule r USING (posting_rule_id) WHERE j.source_event_id = @e AND r.code = 'R-05' AND j.journal_type = 'AUTO'",
            ("e", postingEvent)));
        Assert.Equal("6.000000/9000.0000|9000.0000|0.0000", await h.ScalarAsync<string>(
            $"SELECT (SELECT quantity || '/' || value FROM inv.inv_valuation_balance) || '|' || {Account("1301")} || '|' || {Account("2101")}"));
    }

    [Fact]
    public async Task Repost_needs_a_posted_journal_the_controller_and_a_recent_re_authentication()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var s = await h.CreateInvoicingSetupAsync();
        var postingEvent = await h.ScalarAsync<Guid>("SELECT posting_event_id FROM pur.goods_receipt");

        var storekeeper = await Assert.ThrowsAsync<DomainException>(() => Repost(h, s.Receiving.Storekeeper, postingEvent, "R-01", "k0"));
        var nothing = await Assert.ThrowsAsync<DomainException>(() => Repost(h, s.Purchasing.Controller, postingEvent, "R-04", "k1"));
        clock.Advance(TimeSpan.FromMinutes(6));
        var stale = await Assert.ThrowsAsync<DomainException>(() => Repost(h, s.Purchasing.Controller, postingEvent, "R-01", "k2"));

        Assert.Equal(AuthorizationErrors.NotAuthorized, storekeeper.Code);
        Assert.Equal(ProcurementErrors.NothingToRepost, nothing.Code);
        Assert.Equal(AuthorizationErrors.StepUpRequired, stale.Code);
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM fin.gl_journal WHERE source_event_id = @e", ("e", postingEvent)));
    }

    public static TheoryData<string, string> CounterAccounts() => new()
    {
        { "PURCHASE_PRICE_VARIANCE", "5105" },
        { "INVENTORY_ADJUSTMENT", "5190" },
    };

    [Trait("Acceptance", "IV-03")]
    [Theory]
    [MemberData(nameof(CounterAccounts))]
    public async Task IV03_an_orphan_value_goes_to_zero_against_the_policy_account(string counterRole, string counterAccount)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await h.EnableReallocationAsync(approveR02B: false);
        await h.EnableTestIssueAsync();
        await h.CreateActiveMapAsync("INVENTORY_ADJUSTMENT", await h.CreateAccountAsync("5190", "Ajustes de inventario", isControl: false));
        await h.CreateActiveMapAsync("TEST_INCOME", await h.CreateAccountAsync("4190", "Ingreso de prueba", isControl: false));
        await h.CreateActivePolicyAsync("INVENTORY", new Dictionary<string, string>(PolicySetup.Inventory) { ["valuation_residual_account_role"] = counterRole });
        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id IN ('0192f001-0000-7000-8000-000000000008', '0192f000-0000-7000-8000-0000000000f3') AND version = 1");
        var area = await h.ScalarAsync<Guid>("SELECT valuation_area_id FROM inv.inv_valuation_balance");
        var lot = await h.ScalarAsync<Guid>("SELECT lot_id FROM inv.lot");

        // A consistent orphan: all 6 t leave (value 9 000), then a test adjustment leaves 0.37 with no quantity.
        await h.RunAsync(new TestIssueStock(h.CompanyId, h.SessionId, "issue", s.Receiving.LocationA, s.Purchasing.Sand, lot, 6m, Today(h)), new TestIssueStockHandler());
        await h.RunAsync(new TestAdjustValue(h.CompanyId, h.SessionId, "orphan", area, s.Purchasing.PlantId, s.Purchasing.Sand, 0.37m, Today(h)), new TestAdjustValueHandler());
        var notOrphan = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new ApproveValuationResidualAdjustment(h.CompanyId, s.Purchasing.Controller, "r-0", area, s.Purchasing.Cement, "x"), new ApproveValuationResidualAdjustmentHandler()));

        await h.RunAsync(new ApproveValuationResidualAdjustment(h.CompanyId, s.Purchasing.Controller, "r-1", area, s.Purchasing.Sand, "Residuo de redondeo"), new ApproveValuationResidualAdjustmentHandler());

        Assert.Equal(ProcurementErrors.NotOrphanResidual, notOrphan.Code);
        Assert.Equal("0.000000/0.0000|0.0000", await h.ScalarAsync<string>(
            $"SELECT (SELECT quantity || '/' || value FROM inv.inv_valuation_balance WHERE item_id = @i) || '|' || {Account("1301")}", ("i", s.Purchasing.Sand)));
        Assert.Equal(0.37m, await h.ScalarAsync<decimal>($"SELECT {Account(counterAccount)}"));
        Assert.Equal("RESIDUAL_ADJUSTMENT:-0.3700", await h.ScalarAsync<string>("SELECT movement_type::text || ':' || amount FROM inv.inv_value_entry WHERE movement_type::text = 'RESIDUAL_ADJUSTMENT'"));
    }
}
