using System.Text.Json;
using Rochell.Platform.Time;
using Rochell.Procurement.SupplierInvoices;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>B-02 review repro: R-07B with s′ &gt; s.</summary>
/// <summary>Regression tests of the interim ledger review findings #24 and #29 (E-VS1-6, E-VS1-11).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class InterimReviewInvoiceTests(PostgresFixture postgres)
{
    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    /// <summary>
    /// B02-5. R-07B uses s′ = min(area qty now, Q) / Q without capping it at s. When stock grows between posting and reversal
    /// (new receipts), s′ &gt; s and R-07B credits RAW_MATERIAL with price difference that never entered inventory: the new stock
    /// loses value and PPV keeps a debit although the invoice was fully reversed.
    /// Scenario: 6 t received at 1 500 (9 000) → all 6 t issued (area empty) → invoice 6 t at 1 600 posted (D = 600, s = 0: all to
    /// PPV) → 6 t received again at 9 000 → invoice reversed (s′ = 1, R = (0 − 1) × 600 = −600).
    /// Expected: valuation 6 t / 9 000 and PPV back to 0 (nothing of D was ever in stock). Actual: 6 t / 8 400, PPV +600.
    /// </summary>
    [Fact]
    public async Task B02_5_invoice_reversal_never_takes_out_of_inventory_more_price_difference_than_R05_put_in()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await h.EnableInvoicePostingAsync();
        await h.EnableTestIssueAsync();
        await h.CreateActiveMapAsync("TEST_INCOME", await h.CreateAccountAsync("4190", "Ingreso de prueba", isControl: false));
        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '0192f000-0000-7000-8000-0000000000f1' AND version = 1");

        var lot = await h.ScalarAsync<Guid>("SELECT lot_id FROM inv.lot");
        await h.RunAsync(new TestIssueStock(h.CompanyId, h.SessionId, "issue", s.Receiving.LocationA, s.Purchasing.Sand, lot, 6m, Today(h)), new TestIssueStockHandler());

        var si = (await h.RunAsync(
            new RegisterSupplierInvoice(h.CompanyId, s.Clerk, "si-r", s.Purchasing.SupplierId, "B0100000001", Today(h), Today(h).AddDays(30), [new(s.PoLineId, 6m, 1600m)]),
            new RegisterSupplierInvoiceHandler())).ResultRef;
        var match = JsonDocument.Parse((await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, s.Clerk, "si-m", si, 1), new MatchSupplierInvoiceHandler())).ResultPayload).RootElement;
        var version = 2L;
        if (match.GetProperty("status").GetString() != SupplierInvoiceStatus.Matched)
        {
            await h.RunAsync(new ApproveMatchException(h.CompanyId, s.Purchasing.Controller, "si-a", si, 2, "Precio pactado"), new ApproveMatchExceptionHandler());
            version = 3;
        }

        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, s.Clerk, "post", si, version), new PostSupplierInvoiceHandler());
        var afterPost = await h.ScalarAsync<string>("SELECT quantity || '/' || value FROM inv.inv_valuation_balance");

        await h.RunAsync(new TestReceiveStock(h.CompanyId, h.SessionId, "restock", s.Receiving.LocationA, s.Purchasing.Sand, 6m, 9000.00m, Today(h)), new TestReceiveStockHandler());
        await h.RunAsync(new ReverseSupplierInvoice(h.CompanyId, s.Purchasing.Controller, "rev", si, version + 1, "Factura anulada"), new ReverseSupplierInvoiceHandler());

        var valuation = await h.ScalarAsync<string>("SELECT quantity || '/' || value FROM inv.inv_valuation_balance");
        var ppv = await h.ScalarAsync<decimal>("SELECT coalesce(sum(debit - credit), 0) FROM fin.gl_entry WHERE account_role = 'PURCHASE_PRICE_VARIANCE'");

        Assert.Equal("0.000000/0.0000", afterPost);          // s = 0: nothing of D went to inventory
        Assert.Equal("6.000000/9000.0000", valuation);       // fails: 6.000000/8400.0000
        Assert.Equal(0m, ppv);                               // fails: 600
    }

    /// <summary>
    /// E-VS1-11 (#29): two lines of the same item in one invoice share the stock that covers them. Orders A and B of 10 t at 1 000,
    /// both received (20 t / 20 000); 10 t issued (10 t / 10 000); one invoice bills both at 1 100 (D = 1 000 each). Q = 20, s = 10/20:
    /// 500 of each line to inventory (10 t / 11 000, unit cost 1 100) and 500 to PPV — not 2 000 into 10 t as per-line coverage did.
    /// </summary>
    [Fact]
    public async Task Coverage_is_shared_by_the_lines_of_one_invoice_for_the_same_item()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        await h.EnableInvoicePostingAsync();
        await h.EnableTestIssueAsync();
        var p = r.Purchasing;
        var clerk = await h.SessionWithRolesAsync("CUENTAS_POR_PAGAR");
        var poLines = new List<Guid>();
        Guid lot = default;
        for (var i = 0; i < 2; i++)
        {
            var po = (await h.RunAsync(new Rochell.Procurement.PurchaseOrders.CreatePurchaseOrder(h.CompanyId, p.Buyer, $"po-{i}", p.PlantId, p.SupplierId, Today(h), [new(p.Sand, "t", 10m, 1000m)]), new Rochell.Procurement.PurchaseOrders.CreatePurchaseOrderHandler())).ResultRef;
            await h.RunAsync(new Rochell.Procurement.PurchaseOrders.SubmitPurchaseOrder(h.CompanyId, p.Buyer, $"po-{i}-s", p.PlantId, po, 1), new Rochell.Procurement.PurchaseOrders.SubmitPurchaseOrderHandler());
            await h.RunAsync(new Rochell.Procurement.PurchaseOrders.ApprovePurchaseOrder(h.CompanyId, p.Controller, $"po-{i}-a", p.PlantId, po, 2), new Rochell.Procurement.PurchaseOrders.ApprovePurchaseOrderHandler());
            var line = await h.ScalarAsync<Guid>("SELECT po_line_id FROM pur.purchase_order_line WHERE po_id = @p", ("p", po));
            var gr = await h.RunAsync(
                new Rochell.Procurement.GoodsReceipts.PostGoodsReceipt(h.CompanyId, r.Storekeeper, $"gr-{i}", p.PlantId, po, r.LocationA, h.Clock.UtcNow.AddMinutes(-5), [new(line, 10m)]),
                new Rochell.Procurement.GoodsReceipts.PostGoodsReceiptHandler());
            lot = JsonDocument.Parse(gr.ResultPayload).RootElement.GetProperty("lines")[0].GetProperty("lotId").GetGuid();
            poLines.Add(line);
        }

        await h.RunAsync(new TestIssueStock(h.CompanyId, h.SessionId, "issue", r.LocationA, p.Sand, lot, 10m, Today(h)), new TestIssueStockHandler());
        var si = (await h.RunAsync(
            new RegisterSupplierInvoice(h.CompanyId, clerk, "si", p.SupplierId, "B0100000001", Today(h), Today(h).AddDays(30), [new(poLines[0], 10m, 1100m), new(poLines[1], 10m, 1100m)]),
            new RegisterSupplierInvoiceHandler())).ResultRef;
        await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, clerk, "m", si, 1), new MatchSupplierInvoiceHandler());
        await h.RunAsync(new ApproveMatchException(h.CompanyId, p.Controller, "x", si, 2, "Precio pactado"), new ApproveMatchExceptionHandler());
        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, clerk, "post", si, 3), new PostSupplierInvoiceHandler());

        Assert.Equal("10.000000/11000.0000|1000.0000", await h.ScalarAsync<string>(
            "SELECT (SELECT quantity || '/' || value FROM inv.inv_valuation_balance) || '|' || (SELECT sum(debit - credit) FROM fin.gl_entry WHERE account_role = 'PURCHASE_PRICE_VARIANCE')"));

        // Reversal with no later movement: s′ = s = 0.5, only the exact inverse; back to 10 t / 10 000 and PPV 0.
        await h.RunAsync(new ReverseSupplierInvoice(h.CompanyId, p.Controller, "rev", si, 4, "Factura anulada"), new ReverseSupplierInvoiceHandler());
        Assert.Equal("10.000000/10000.0000|0.0000", await h.ScalarAsync<string>(
            "SELECT (SELECT quantity || '/' || value FROM inv.inv_valuation_balance) || '|' || (SELECT sum(debit - credit) FROM fin.gl_entry WHERE account_role = 'PURCHASE_PRICE_VARIANCE')"));
    }
}
