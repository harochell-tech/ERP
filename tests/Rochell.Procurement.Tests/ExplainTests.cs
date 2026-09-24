using System.Text.Json;
using Npgsql;
using Rochell.Finance;
using Rochell.Finance.Explain;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Procurement.Ledger;
using Rochell.Procurement.SupplierInvoices;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>PR-17: EX-01 (Explain this entry), POL-01 (allocation method and policy recorded) and the read-only query pipeline.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ExplainTests(PostgresFixture postgres)
{
    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static Task<Guid> EntryAsync(TestHarness h, string ruleLineCode, string journalType = "AUTO", int generation = 1)
        => h.ScalarAsync<Guid>(
            "SELECT e.gl_entry_id FROM fin.gl_entry e JOIN fin.gl_journal j USING (journal_id) WHERE e.rule_line_code = @l AND j.journal_type = @t AND j.posting_generation = @g ORDER BY j.occurred_at DESC LIMIT 1",
            ("l", ruleLineCode),
            ("t", journalType),
            ("g", generation));

    private static async Task<JsonElement> ExplainAsync(TestHarness h, Guid session, Guid entry)
        => JsonDocument.Parse(await h.QueryAsync(new ExplainEntry(h.CompanyId, session, entry), new ExplainEntryHandler())).RootElement;

    private static async Task<Guid> PostedInvoiceAsync(TestHarness h, TestInvoicing s, decimal price)
    {
        var si = (await h.RunAsync(
            new RegisterSupplierInvoice(h.CompanyId, s.Clerk, "r", s.Purchasing.SupplierId, "B0100000001", Today(h), Today(h).AddDays(30), [new(s.PoLineId, 6m, price)]),
            new RegisterSupplierInvoiceHandler())).ResultRef;
        await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, s.Clerk, "m", si, 1), new MatchSupplierInvoiceHandler());
        var version = 2;
        if (price != 1500m)
        {
            await h.RunAsync(new ApproveMatchException(h.CompanyId, s.Purchasing.Controller, "a", si, 2, "Precio pactado"), new ApproveMatchExceptionHandler());
            version = 3;
        }

        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, s.Clerk, "p", si, version), new PostSupplierInvoiceHandler());
        return si;
    }

    [Fact]
    public async Task EX01_a_receipt_line_explains_its_event_document_rule_mapping_and_text()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        var grNo = await h.ScalarAsync<string>("SELECT gr_no FROM pur.goods_receipt");

        var x = await ExplainAsync(h, s.Purchasing.Controller, await EntryAsync(h, "R01-DR-INV"));

        Assert.Equal("GoodsReceiptPosted", x.GetProperty("event").GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(x.GetProperty("event").GetProperty("user").GetString()));
        Assert.Equal("GOODS_RECEIPT", x.GetProperty("document").GetProperty("kind").GetString());
        Assert.Equal(grNo, x.GetProperty("document").GetProperty("number").GetString());
        Assert.Equal("R-01", x.GetProperty("rule").GetProperty("code").GetString());
        Assert.Equal("RAW_MATERIAL", x.GetProperty("mapping").GetProperty("accountRole").GetString());
        Assert.Equal("PENDING_SEAL", x.GetProperty("integrity").GetProperty("status").GetString());
        Assert.Contains(grNo!, x.GetProperty("explanation").GetString(), StringComparison.Ordinal);
        Assert.True(x.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task POL01_a_price_difference_records_method_policy_and_coverage_and_Explain_shows_them()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await h.EnableInvoicePostingAsync();
        await PostedInvoiceAsync(h, s, 1600m);
        var entry = await EntryAsync(h, "R05-DR-INV");

        var x = await ExplainAsync(h, s.Purchasing.Controller, entry);

        var inputs = x.GetProperty("determinationInputs").GetProperty("inputs");
        Assert.Equal("STOCK_COVERAGE", inputs.GetProperty("invoice_price_variance_allocation_method").GetString());
        Assert.Equal(6m, decimal.Parse(inputs.GetProperty("area_quantity").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1m, decimal.Parse(inputs.GetProperty("coverage").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(600m, decimal.Parse(inputs.GetProperty("difference").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("INVENTORY", x.GetProperty("policies")[0].GetProperty("policy").GetString());
        Assert.Equal("SUPPLIER_INVOICE", x.GetProperty("document").GetProperty("kind").GetString());
        Assert.True(x.GetProperty("fiscal").GetProperty("lines").GetArrayLength() > 0);
        Assert.Contains("B0100000001", x.GetProperty("explanation").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_price_difference_needs_an_active_INVENTORY_policy()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await h.EnableInvoicePostingAsync(inventoryPolicy: false);

        var ex = await Assert.ThrowsAsync<DomainException>(() => PostedInvoiceAsync(h, s, 1600m));

        Assert.Equal(FinanceErrors.PostingPrerequisiteMissing, ex.Code);
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM fin.gl_journal j JOIN fin.posting_rule r USING (posting_rule_id) WHERE r.code IN ('R-04', 'R-05')"));
    }

    [Fact]
    public async Task A_valuation_reallocation_explains_its_coverages_and_policy()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await h.EnableInvoicePostingAsync();
        await h.EnableTestIssueAsync();
        var si = await PostedInvoiceAsync(h, s, 1600m);
        var lot = await h.ScalarAsync<Guid>("SELECT lot_id FROM inv.lot");
        await h.RunAsync(new TestIssueStock(h.CompanyId, h.SessionId, "issue", s.Receiving.LocationA, s.Purchasing.Sand, lot, 6m, Today(h)), new TestIssueStockHandler());
        await h.RunAsync(new ReverseSupplierInvoice(h.CompanyId, s.Purchasing.Controller, "rev", si, 4, "Anulada por el proveedor"), new ReverseSupplierInvoiceHandler());

        var x = await ExplainAsync(h, s.Purchasing.Controller, await EntryAsync(h, "R07B-DR-INV", "VALUATION_REALLOCATION"));

        var inputs = x.GetProperty("determinationInputs").GetProperty("inputs");
        Assert.Equal(1m, decimal.Parse(inputs.GetProperty("coverage_original").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0m, decimal.Parse(inputs.GetProperty("coverage_now").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("INVENTORY", x.GetProperty("policies")[0].GetProperty("policy").GetString());
        Assert.Contains("B0100000001", x.GetProperty("explanation").GetString(), StringComparison.Ordinal);
        Assert.True(x.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task Reversals_and_reposts_explain_themselves_through_the_original_line()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        var grNo = await h.ScalarAsync<string>("SELECT gr_no FROM pur.goods_receipt");
        var postingEvent = await h.ScalarAsync<Guid>("SELECT posting_event_id FROM pur.goods_receipt");
        await h.AdminRequireAsync($"UPDATE fin.account_role_map SET effective_to = '{Today(h):yyyy-MM-dd}' WHERE account_role = 'GRNI' AND status = 'ACTIVE'");
        await h.CreateActiveMapAsync("GRNI", await h.CreateAccountAsync("2106", "Recibido no facturado (correcta)", isControl: false), from: Today(h));
        await h.RunAsync(new RepostEvent(h.CompanyId, s.Purchasing.Controller, "repost", postingEvent, "R-01", "Mapeo de GRNI equivocado"), new RepostEventHandler());

        var reversal = await ExplainAsync(h, s.Purchasing.Controller, await EntryAsync(h, "R01-CR-GRNI", "REVERSAL"));
        var regenerated = await ExplainAsync(h, s.Purchasing.Controller, await EntryAsync(h, "R01-CR-GRNI", "AUTO", 2));

        Assert.StartsWith("Reversa exacta (repost por corrección de mapeo) de la línea", reversal.GetProperty("explanation").GetString(), StringComparison.Ordinal);
        Assert.Contains(grNo!, reversal.GetProperty("explanation").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("Generación 2 (repost por corrección de mapeo):", regenerated.GetProperty("explanation").GetString(), StringComparison.Ordinal);
        Assert.Equal("GOODS_RECEIPT", regenerated.GetProperty("document").GetProperty("kind").GetString());
        Assert.Equal("2106", await h.ScalarAsync<string>(
            "SELECT a.code FROM fin.account a WHERE a.account_id = (SELECT account_id FROM fin.gl_entry WHERE gl_entry_id = @e)", ("e", regenerated.GetProperty("entry").GetProperty("glEntryId").GetGuid())));
    }

    [Fact]
    public async Task A_late_entry_says_when_it_happened_and_why_it_was_posted_later()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var stock = await h.CreateStockSetupAsync();
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var lastMonth = new DateOnly(Today(h).Year, Today(h).Month, 1).AddMonths(-1).AddDays(4);
        await h.SetComponentAsync(lastMonth, "INV-MOV", "CLOSED");
        await h.RunAsync(new TestReceiveStock(h.CompanyId, h.SessionId, "late", stock.LocationA, stock.ItemId, 1m, 10.00m, lastMonth), new TestReceiveStockHandler());

        var x = await ExplainAsync(h, controller, await EntryAsync(h, "TR-DR-INV"));

        Assert.True(x.GetProperty("journal").GetProperty("lateEntry").GetBoolean());
        Assert.Contains($"Registro tardío: el hecho es del {lastMonth:yyyy-MM-dd}", x.GetProperty("explanation").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explain_needs_audit_read_and_an_existing_entry()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        var entry = await EntryAsync(h, "R01-DR-INV");

        var storekeeper = await Assert.ThrowsAsync<DomainException>(() => ExplainAsync(h, s.Receiving.Storekeeper, entry));
        var missing = await Assert.ThrowsAsync<DomainException>(() => ExplainAsync(h, s.Purchasing.Controller, Guid.NewGuid()));

        Assert.Equal(AuthorizationErrors.NotAuthorized, storekeeper.Code);
        Assert.Equal(FinanceErrors.JournalNotFound, missing.Code);
    }

    [Fact]
    public async Task Queries_run_read_only()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => h.QueryAsync(new WritingQuery(h.CompanyId, h.SessionId), new WritingQueryHandler()));

        Assert.Equal("25006", ex.SqlState);   // read_only_sql_transaction
    }
}
