using System.Text.Json;
using Rochell.Finance;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Procurement.ReceiptCorrections;
using Rochell.Procurement.SupplierInvoices;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>T-09/T-10: AT-02, AT-03 (STOCK_COVERAGE), withholding, SI-06, SI-07, SI-08, R-07B and CC-03.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SupplierInvoicePostingTests(PostgresFixture postgres)
{
    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    /// <summary>Registers and matches (approving a price exception when needed). Returns the invoice and its version.</summary>
    private static async Task<(Guid Si, long Version)> MatchedAsync(TestHarness h, TestInvoicing s, decimal qty, decimal price, string key = "si")
    {
        var si = (await h.RunAsync(
            new RegisterSupplierInvoice(h.CompanyId, s.Clerk, key + "-r", s.Purchasing.SupplierId, "B0100000001", Today(h), Today(h).AddDays(30), [new(s.PoLineId, qty, price)]),
            new RegisterSupplierInvoiceHandler())).ResultRef;
        var match = JsonDocument.Parse((await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, s.Clerk, key + "-m", si, 1), new MatchSupplierInvoiceHandler())).ResultPayload).RootElement;
        if (match.GetProperty("status").GetString() == SupplierInvoiceStatus.Matched)
        {
            return (si, 2);
        }

        await h.RunAsync(new ApproveMatchException(h.CompanyId, s.Purchasing.Controller, key + "-a", si, 2, "Precio pactado"), new ApproveMatchExceptionHandler());
        return (si, 3);
    }

    private static Task<CommandResult> Post(TestHarness h, TestInvoicing s, Guid si, long version, string key = "post")
        => h.RunAsync(new PostSupplierInvoice(h.CompanyId, s.Clerk, key, si, version), new PostSupplierInvoiceHandler());

    private static async Task Issue(TestHarness h, TestInvoicing s, decimal qty, string key)
    {
        var lot = await h.ScalarAsync<Guid>("SELECT lot_id FROM inv.lot");
        await h.RunAsync(new TestIssueStock(h.CompanyId, h.SessionId, key, s.Receiving.LocationA, s.Purchasing.Sand, lot, qty, Today(h)), new TestIssueStockHandler());
    }

    private static string Role(string role) => $"(SELECT coalesce(sum(debit - credit), 0) FROM fin.gl_entry WHERE account_role = '{role}')";

    /// <summary>GRNI | AP | ITBIS | WHT | RAW | PPV | valuation qty/value | qty invoiced | AP doc original/open.</summary>
    private static Task<string?> Books(TestHarness h)
        => h.ScalarAsync<string>(
            $"""
            SELECT {Role("GRNI")} || '|' || {Role("AP_CONTROL")} || '|' || {Role("ITBIS_RECOVERABLE")} || '|' || {Role("WITHHOLDING_PAYABLE")} || '|' ||
                   {Role("RAW_MATERIAL")} || '|' || {Role("PURCHASE_PRICE_VARIANCE")} || '|' ||
                   (SELECT quantity || '/' || value FROM inv.inv_valuation_balance) || '|' || (SELECT qty_invoiced FROM pur.purchase_order_line) || '|' ||
                   coalesce((SELECT original_amount || '/' || open_amount FROM fin.ap_document), '-')
            """);

    [Fact]
    public async Task AT02_invoice_at_the_PO_price_clears_GRNI_and_creates_the_payable()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await h.EnableInvoicePostingAsync();
        var (si, version) = await MatchedAsync(h, s, 6m, 1500m);

        await Post(h, s, si, version);

        // Receipt: Dr RAW 9 000 / Cr GRNI 9 000. R-04: Dr GRNI 9 000 + ITBIS 1 620 / Cr AP 10 620.
        Assert.Equal("0.0000|-10620.0000|1620.0000|0|9000.0000|0|6.000000/9000.0000|6.000000|10620.0000/10620.0000", await Books(h));
        Assert.Equal("MATCHED|POSTED", await h.ScalarAsync<string>("SELECT document_status::text || '|' || accounting_status::text FROM pur.supplier_invoice"));
        Assert.Equal("R-04", await h.ScalarAsync<string>(
            "SELECT string_agg(r.code, ',') FROM fin.gl_journal j JOIN fin.posting_rule r USING (posting_rule_id) JOIN pur.supplier_invoice si ON si.posting_event_id = j.source_event_id"));
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM core.document_link WHERE link_type = 'BILLS'"));
        Assert.Equal("AP", await h.ScalarAsync<string>("SELECT subledger_type FROM fin.gl_entry WHERE account_role = 'AP_CONTROL'"));
    }

    [Fact]
    public async Task AT03_price_difference_fully_covered_by_stock_goes_to_inventory()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await h.EnableInvoicePostingAsync();
        var (si, version) = await MatchedAsync(h, s, 6m, 1600m);

        await Post(h, s, si, version);

        // D = 600, s = 1: R-05 Dr RAW 600 / Cr AP 600. ITBIS on 9 600 = 1 728. AP = 9 600 + 1 728.
        Assert.Equal("0.0000|-11328.0000|1728.0000|0|9600.0000|0|6.000000/9600.0000|6.000000|11328.0000/11328.0000", await Books(h));
        Assert.Equal("PRICE_ADJUSTMENT:600.0000", await h.ScalarAsync<string>("SELECT movement_type::text || ':' || amount FROM inv.inv_value_entry WHERE movement_type::text = 'PRICE_ADJUSTMENT'"));
    }

    [Fact]
    public async Task AT03_only_the_share_still_in_stock_goes_to_inventory_the_rest_to_PPV()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await h.EnableInvoicePostingAsync();
        await h.EnableTestIssueAsync();
        await Issue(h, s, 3m, "issue");                           // 6 t / 9 000 → 3 t / 4 500
        var (si, version) = await MatchedAsync(h, s, 6m, 1600m);

        await Post(h, s, si, version);

        // s = min(3, 6) / 6 = 0.5: RAW +300, PPV +300 → 3 t / 4 800 (unit cost 1 600).
        Assert.Equal("0.0000|-11328.0000|1728.0000|0|4800.0000|300.0000|3.000000/4800.0000|6.000000|11328.0000/11328.0000", await Books(h));
    }

    [Fact]
    public async Task Withholding_reduces_the_payable_and_is_owed_to_the_tax_authority()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await h.EnableInvoicePostingAsync(withholdingDefinition: """{"tax_code":"RET_ITBIS","rate":"0.30","base":"ITBIS","party_types":["COMPANY"]}""");
        var (si, version) = await MatchedAsync(h, s, 6m, 1500m);

        await Post(h, s, si, version);

        // W = 30 % × 1 620 = 486: AP 9 000 + 1 620 − 486 = 10 134.
        Assert.Equal("0.0000|-10134.0000|1620.0000|-486.0000|9000.0000|0|6.000000/9000.0000|6.000000|10134.0000/10134.0000", await Books(h));
    }

    [Fact]
    public async Task SI08_non_recoverable_ITBIS_blocks_posting_and_the_invoice_can_then_be_voided()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await h.EnableInvoicePostingAsync(itbisDefinition: """{"tax_code":"ITBIS","rate":"0.18","effect":"NON_RECOVERABLE_INPUT"}""");
        var (si, version) = await MatchedAsync(h, s, 6m, 1500m);

        var result = JsonDocument.Parse((await Post(h, s, si, version)).ResultPayload).RootElement;
        var blocked = await h.ScalarAsync<string>("SELECT document_status::text || '|' || accounting_status::text || '|' || (tax_determination_id IS NOT NULL) FROM pur.supplier_invoice");
        var books = await Books(h);
        await h.RunAsync(new VoidSupplierInvoice(h.CompanyId, s.Clerk, "void", si, version + 1, "ITBIS no recuperable; se registra de otra forma"), new VoidSupplierInvoiceHandler());

        Assert.Equal("POSTING_BLOCKED", result.GetProperty("accountingStatus").GetString());
        Assert.Equal("MATCHED|POSTING_BLOCKED|true", blocked);
        Assert.Equal("-9000.0000|0|0|0|9000.0000|0|6.000000/9000.0000|0.000000|-", books);
        Assert.Equal("VOIDED|NOT_POSTED", await h.ScalarAsync<string>("SELECT document_status::text || '|' || accounting_status::text FROM pur.supplier_invoice"));
    }

    [Fact]
    public async Task SI07_a_withholding_rule_pending_its_source_closes_the_gate_and_nothing_is_written()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await h.EnableInvoicePostingAsync();
        await h.ConfigureAsync(await h.FiscalActorsAsync(), "ret", "RET-COMPRAS", FiscalRuleKinds.PurchaseWithholding, TaxSetup.WithholdingDefinition, new DateOnly(2026, 1, 1));
        var (si, version) = await MatchedAsync(h, s, 6m, 1500m);

        var ex = await Assert.ThrowsAsync<DomainException>(() => Post(h, s, si, version));

        Assert.Equal(TaxErrors.FiscalGateClosed, ex.Code);
        Assert.Equal("MATCHED|NOT_POSTED", await h.ScalarAsync<string>("SELECT document_status::text || '|' || accounting_status::text FROM pur.supplier_invoice"));
        Assert.Equal(0L, await h.CountAsync("tax.tax_determination"));
        Assert.Equal("-9000.0000|0|0|0|9000.0000|0|6.000000/9000.0000|0.000000|-", await Books(h));
    }

    [Fact]
    public async Task SI06_reversal_reopens_GRNI_and_releases_the_invoiced_quantity()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await h.EnableInvoicePostingAsync();
        var (si, version) = await MatchedAsync(h, s, 6m, 1500m);
        await Post(h, s, si, version);

        var clerk = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ReverseSupplierInvoice(h.CompanyId, s.Clerk, "rev-0", si, version + 1, "x"), new ReverseSupplierInvoiceHandler()));
        await h.RunAsync(new ReverseSupplierInvoice(h.CompanyId, s.Purchasing.Controller, "rev", si, version + 1, "Factura duplicada del proveedor"), new ReverseSupplierInvoiceHandler());

        Assert.Equal(AuthorizationErrors.NotAuthorized, clerk.Code);
        Assert.Equal("-9000.0000|0.0000|0.0000|0|9000.0000|0|6.000000/9000.0000|0.000000|10620.0000/0.0000", await Books(h));
        Assert.Equal("REVERSED|REVERSED", await h.ScalarAsync<string>("SELECT document_status::text || '|' || accounting_status::text FROM pur.supplier_invoice"));
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM fin.gl_journal WHERE journal_type = 'REVERSAL'"));
    }

    [Fact]
    public async Task R07B_reversal_takes_out_of_inventory_only_the_price_difference_still_in_stock()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await h.EnableInvoicePostingAsync();
        await h.EnableTestIssueAsync();
        var (si, version) = await MatchedAsync(h, s, 6m, 1600m);
        await Post(h, s, si, version);                             // 6 t / 9 600
        await Issue(h, s, 3m, "issue");                           // 3 t / 4 800

        var result = JsonDocument.Parse((await h.RunAsync(
            new ReverseSupplierInvoice(h.CompanyId, s.Purchasing.Controller, "rev", si, version + 1, "Nota del proveedor anula la factura"), new ReverseSupplierInvoiceHandler())).ResultPayload).RootElement;

        // Exact R-05 reversal −600 → 4 200; s′ = 3 / 6 = 0.5 → R-07B moves (1 − 0.5) × 600 = 300 back: 3 t / 4 500 (unit cost 1 500).
        Assert.Equal(3, result.GetProperty("journals").GetArrayLength());
        Assert.Equal("-9000.0000|0.0000|0.0000|0|4500.0000|-300.0000|3.000000/4500.0000|0.000000|11328.0000/0.0000", await Books(h));
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM inv.inv_value_entry WHERE reverses_value_entry_id IS NOT NULL AND movement_type::text = 'PRICE_ADJUSTMENT'"));
    }

    [Fact]
    public async Task Posting_needs_a_matched_invoice_and_its_rules_and_writes_nothing_otherwise()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await h.EnableInvoicePostingAsync(approveRules: false);
        var draft = (await h.RunAsync(
            new RegisterSupplierInvoice(h.CompanyId, s.Clerk, "d", s.Purchasing.SupplierId, "B0100000009", Today(h), Today(h).AddDays(30), [new(s.PoLineId, 1m, 1500m)]),
            new RegisterSupplierInvoiceHandler())).ResultRef;
        var (si, version) = await MatchedAsync(h, s, 5m, 1500m, "m");

        var notMatched = await Assert.ThrowsAsync<DomainException>(() => Post(h, s, draft, 1, "p-0"));
        var noRule = await Assert.ThrowsAsync<DomainException>(() => Post(h, s, si, version, "p-1"));
        var storekeeper = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new PostSupplierInvoice(h.CompanyId, s.Receiving.Storekeeper, "p-2", si, version), new PostSupplierInvoiceHandler()));

        Assert.Equal(ProcurementErrors.InvalidState, notMatched.Code);
        Assert.Equal(FinanceErrors.PostingPrerequisiteMissing, noRule.Code);
        Assert.Equal(AuthorizationErrors.NotAuthorized, storekeeper.Code);
        Assert.Equal(0L, await h.CountAsync("tax.tax_determination"));
        Assert.Equal(0L, await h.CountAsync("fin.ap_document"));
    }

    [Fact]
    public async Task CC03_posting_and_a_negative_receipt_correction_never_leave_more_invoiced_than_received()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await h.EnableInvoicePostingAsync();
        await h.CreateActivePolicyAsync("INVENTORY", PolicySetup.Inventory);   // corrections without re-creating the PPV account
        await h.AdminRequireAsync(
            $"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id IN ('{CorrectionSetup.R03A}', '{CorrectionSetup.R03B}') AND version = 1");
        var (si, version) = await MatchedAsync(h, s, 6m, 1500m);
        var rc = (await h.RunAsync(
            new CreateReceiptCorrection(h.CompanyId, s.Receiving.Storekeeper, "rc", s.Purchasing.PlantId, s.GoodsReceiptId, s.GoodsReceiptLineId, -2m, "Ticket corregido", "BAS-3"),
            new CreateReceiptCorrectionHandler())).ResultRef;

        async Task<string> Attempt(Func<Task> action)
        {
            try
            {
                await action();
                return "ok";
            }
            catch (DomainException ex)
            {
                return ex.Code;
            }
        }

        var outcomes = await Task.WhenAll(
            Attempt(() => Post(h, s, si, version)),
            Attempt(() => h.RunAsync(new ApproveReceiptCorrection(h.CompanyId, s.Purchasing.Controller, "rc-ok", rc), new ApproveReceiptCorrectionHandler())));

        Assert.Equal(1, outcomes.Count(o => o == "ok"));
        Assert.True(outcomes.Any(o => o is ProcurementErrors.AlreadyInvoiced or ProcurementErrors.QtyExceedsAvailable), string.Join(", ", outcomes));
        Assert.True(await h.ScalarAsync<bool>("SELECT qty_invoiced <= qty_received FROM pur.purchase_order_line"));
    }
}
