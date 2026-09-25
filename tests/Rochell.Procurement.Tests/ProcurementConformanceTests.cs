using System.Data.Common;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Time;
using Rochell.Procurement.GoodsReceipts;
using Rochell.Procurement.PurchaseOrders;
using Rochell.Procurement.ReceiptCorrections;
using Rochell.Procurement.SupplierInvoices;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>E-PR19-7: RC-01b, CC-02, SI-06 and SI-06b with the Patch 1 figures, and VAL-03 at the database level.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ProcurementConformanceTests(PostgresFixture postgres)
{
    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static async Task<(Guid Po, Guid Line)> OrderAsync(TestHarness h, TestReceiving r, decimal qty, decimal price, string key)
    {
        var p = r.Purchasing;
        var po = (await h.RunAsync(new CreatePurchaseOrder(h.CompanyId, p.Buyer, key + "-c", p.PlantId, p.SupplierId, Today(h), [new(p.Sand, "t", qty, price)]), new CreatePurchaseOrderHandler())).ResultRef;
        await h.RunAsync(new SubmitPurchaseOrder(h.CompanyId, p.Buyer, key + "-s", p.PlantId, po, 1), new SubmitPurchaseOrderHandler());
        await h.RunAsync(new ApprovePurchaseOrder(h.CompanyId, p.Controller, key + "-a", p.PlantId, po, 2), new ApprovePurchaseOrderHandler());
        return (po, await h.ScalarAsync<Guid>("SELECT po_line_id FROM pur.purchase_order_line WHERE po_id = @p", ("p", po)));
    }

    private static async Task<(Guid Gr, Guid Line, Guid Lot)> ReceiveAsync(TestHarness h, TestReceiving r, Guid po, Guid poLine, decimal qty, string key)
    {
        var result = await h.RunAsync(
            new PostGoodsReceipt(h.CompanyId, r.Storekeeper, key, r.Purchasing.PlantId, po, r.LocationA, h.Clock.UtcNow.AddMinutes(-5), [new(poLine, qty)]),
            new PostGoodsReceiptHandler());
        var line = JsonDocument.Parse(result.ResultPayload).RootElement.GetProperty("lines")[0];
        return (result.ResultRef, line.GetProperty("grLineId").GetGuid(), line.GetProperty("lotId").GetGuid());
    }

    private static Task Issue(TestHarness h, TestReceiving r, Guid lot, decimal qty, string key)
        => h.RunAsync(new TestIssueStock(h.CompanyId, h.SessionId, key, r.LocationA, r.Purchasing.Sand, lot, qty, Today(h)), new TestIssueStockHandler());

    private static string Role(string role) => $"(SELECT coalesce(sum(debit - credit), 0) FROM fin.gl_entry WHERE account_role = '{role}')";

    /// <summary>valuation qty/value | RAW_MATERIAL | PURCHASE_PRICE_VARIANCE | GRNI (debit − credit).</summary>
    private static Task<string?> Books(TestHarness h)
        => h.ScalarAsync<string>(
            $"""
            SELECT (SELECT quantity || '/' || value FROM inv.inv_valuation_balance) || '|' || {Role("RAW_MATERIAL")} || '|' ||
                   {Role("PURCHASE_PRICE_VARIANCE")} || '|' || {Role("GRNI")}
            """);

    /// <summary>Debit/credit lines of the journals of one rule, as "ROLE:debit:credit" sorted.</summary>
    private static Task<string?> Lines(TestHarness h, string ruleCode)
        => h.ScalarAsync<string>(
            """
            SELECT string_agg(e.account_role || ':' || e.debit || ':' || e.credit, ',' ORDER BY e.account_role)
            FROM fin.gl_entry e JOIN fin.gl_journal j USING (journal_id) JOIN fin.posting_rule r USING (posting_rule_id)
            WHERE r.code = @r
            """,
            ("r", ruleCode));

    [Trait("Acceptance", "RC-01b")]
    [Fact]
    public async Task RC01b_reversing_the_untouched_receipt_after_the_other_lot_was_consumed_reallocates_300()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        await h.EnableReallocationAsync();
        await h.EnableTestIssueAsync();
        var (po1, line1) = await OrderAsync(h, r, 30m, 100m, "o1");
        var (po2, line2) = await OrderAsync(h, r, 30m, 80m, "o2");
        var (gr1, _, _) = await ReceiveAsync(h, r, po1, line1, 30m, "gr1");
        var (_, _, lot2) = await ReceiveAsync(h, r, po2, line2, 30m, "gr2");
        await Issue(h, r, lot2, 30m, "issue"); // avg 90 → 2 700 out; area 30 t / 2 700
        var before = await Books(h);

        await h.RunAsync(new ReverseGoodsReceipt(h.CompanyId, r.Purchasing.Controller, "rev", gr1, "Recepción contra la OC equivocada"), new ReverseGoodsReceiptHandler());

        // A: −30 t / −3 000 → 0 t / −300; B (R-02B, target 0): R = 300 → Dr RAW_MATERIAL 300 / Cr PPV 300 → 0 t / 0.
        Assert.Equal("30.000000/2700.0000|2700.0000|0|-5400.0000", before);
        Assert.Equal("0.000000/0.0000|0.0000|-300.0000|-2400.0000", await Books(h));
        Assert.Equal("PURCHASE_PRICE_VARIANCE:0.0000:300.0000,RAW_MATERIAL:300.0000:0.0000", await Lines(h, "R-02B"));
    }

    [Trait("Acceptance", "CC-02")]
    [Fact]
    public async Task CC02_a_reversal_and_a_correction_of_the_same_receipt_race_and_only_one_wins()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        await h.EnableCorrectionsAsync();
        var (po, line) = await OrderAsync(h, r, 100m, 1000m, "o");

        for (var i = 0; i < 5; i++)
        {
            var (gr, grLine, _) = await ReceiveAsync(h, r, po, line, 10m, $"gr-{i}");
            var outcomes = await Task.WhenAll(
                Attempt(() => h.RunAsync(new ReverseGoodsReceipt(h.CompanyId, r.Purchasing.Controller, $"rev-{i}", gr, "Duplicada"), new ReverseGoodsReceiptHandler())),
                Attempt(async () =>
                {
                    var rc = await h.RunAsync(
                        new CreateReceiptCorrection(h.CompanyId, r.Storekeeper, $"rc-{i}", r.Purchasing.PlantId, gr, grLine, -2m, "Ticket corregido", "ticket-{i}"),
                        new CreateReceiptCorrectionHandler());
                    await h.RunAsync(new ApproveReceiptCorrection(h.CompanyId, r.Purchasing.Controller, $"rca-{i}", rc.ResultRef), new ApproveReceiptCorrectionHandler());
                }));

            var status = await h.ScalarAsync<string>("SELECT document_status::text FROM pur.goods_receipt WHERE gr_id = @g", ("g", gr));
            Assert.Equal(1, outcomes.Count(o => o == "ok"));
            Assert.Equal(outcomes[0] == "ok" ? "REVERSED" : "CORRECTED", status);
        }

        Assert.Equal(0L, await h.ScalarAsync<long>(
            "SELECT count(*) FROM inv.inv_value_entry v WHERE (SELECT count(*) FROM fin.gl_entry e WHERE e.inv_value_entry_id = v.value_entry_id) <> 1"));
    }

    private static async Task<string> Attempt(Func<Task> run)
    {
        try
        {
            await run();
            return "ok";
        }
        catch (DomainException ex)
        {
            return ex.Code;
        }
    }

    /// <summary>AT-03 setup: AT-01 (40 t at 1 000), 30 t issued, invoice of 40 t at 1 050 (D = 2 000, s = 0.25) posted.</summary>
    private static async Task<(TestReceiving R, Guid Lot2, Guid Si, long Version)> PostedAt1050Async(TestHarness h)
    {
        var r = await h.CreateReceivingSetupAsync();
        await h.EnableInvoicePostingAsync();
        await h.EnableTestIssueAsync();
        var clerk = await h.SessionWithRolesAsync("CUENTAS_POR_PAGAR");
        var (po, line) = await OrderAsync(h, r, 40m, 1000m, "o");
        var (_, _, lot1) = await ReceiveAsync(h, r, po, line, 20m, "gr1");
        var (_, _, lot2) = await ReceiveAsync(h, r, po, line, 20m, "gr2");
        await Issue(h, r, lot1, 20m, "i1");
        await Issue(h, r, lot2, 10m, "i2");
        var si = (await h.RunAsync(
            new RegisterSupplierInvoice(h.CompanyId, clerk, "si", r.Purchasing.SupplierId, "B0100000001", Today(h), Today(h).AddDays(30), [new(line, 40m, 1050m)]),
            new RegisterSupplierInvoiceHandler())).ResultRef;
        await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, clerk, "m", si, 1), new MatchSupplierInvoiceHandler()); // 5 % over the PO price: exception
        await h.RunAsync(new ApproveMatchException(h.CompanyId, r.Purchasing.Controller, "x", si, 2, "Precio pactado"), new ApproveMatchExceptionHandler());
        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, clerk, "p", si, 3), new PostSupplierInvoiceHandler());
        return (r, lot2, si, 4);
    }

    [Trait("Acceptance", "SI-06")]
    [Fact]
    public async Task SI06_reversal_after_the_remaining_stock_left_moves_the_price_difference_back_to_variance()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (r, lot2, si, version) = await PostedAt1050Async(h);
        var posted = await Books(h);
        await Issue(h, r, lot2, 10m, "i3"); // s′ = 0: the last 10 t leave at 1 050

        await h.RunAsync(new ReverseSupplierInvoice(h.CompanyId, r.Purchasing.Controller, "rev", si, version, "Factura anulada por el proveedor"), new ReverseSupplierInvoiceHandler());

        // Posted (AT-03): 10 t / 10 500, RAW +500, PPV +1 500. Reversal: A exact inverse (RAW −500, PPV −1 500); B: R = 0.25 × 2 000 = 500,
        // Dr RAW 500 / Cr PPV 500 → inventory net 0, variance of the reversal −2 000, GRNI reopened, qty_invoiced 0.
        Assert.Equal("10.000000/10500.0000|10500.0000|1500.0000|0.0000", posted);
        Assert.Equal("0.000000/0.0000|0.0000|-500.0000|-40000.0000", await Books(h));
        Assert.Equal("PURCHASE_PRICE_VARIANCE:0.0000:500.0000,RAW_MATERIAL:500.0000:0.0000", await Lines(h, "R-07B"));
        Assert.Equal(0m, await h.ScalarAsync<decimal>("SELECT qty_invoiced FROM pur.purchase_order_line"));
    }

    [Trait("Acceptance", "SI-06b")]
    [Fact]
    public async Task SI06b_reversal_with_no_later_movement_is_only_the_exact_inverse()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (r, _, si, version) = await PostedAt1050Async(h);

        await h.RunAsync(new ReverseSupplierInvoice(h.CompanyId, r.Purchasing.Controller, "rev", si, version, "Factura anulada por el proveedor"), new ReverseSupplierInvoiceHandler());

        // s′ = s = 0.25: only journal A (RAW −500, PPV −1 500) → 10 t / 10 000, PPV 0; no R-07B journal.
        Assert.Equal("10.000000/10000.0000|10000.0000|0.0000|-40000.0000", await Books(h));
        Assert.Null(await Lines(h, "R-07B"));
    }

    [Trait("Acceptance", "VAL-03")]
    [Fact]
    public async Task VAL03_a_zero_price_line_is_rejected_by_the_database_too()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        var p = s.Purchasing;
        var draft = (await h.RunAsync(new CreatePurchaseOrder(h.CompanyId, p.Buyer, "d", p.PlantId, p.SupplierId, Today(h), [new(p.Sand, "t", 1m, 900m)]), new CreatePurchaseOrderHandler())).ResultRef;
        var si = (await h.RunAsync(
            new RegisterSupplierInvoice(h.CompanyId, s.Clerk, "si", p.SupplierId, "B0100000002", Today(h), Today(h).AddDays(30), [new(s.PoLineId, 1m, 1500m)]),
            new RegisterSupplierInvoiceHandler())).ResultRef;

        var poLine = await h.AppExecuteAsync(
            $"INSERT INTO pur.purchase_order_line (po_line_id, company_id, po_id, line_no, item_id, uom, qty_ordered, unit_price, receipt_tolerance_pct, version) VALUES (gen_random_uuid(), '{h.CompanyId}', '{draft}', 2, '{p.Sand}', 't', 1, 0, 0.02, 1)");
        var siLine = await h.AppExecuteAsync(
            $"INSERT INTO pur.supplier_invoice_line (si_line_id, company_id, si_id, line_no, line_kind, po_line_id, qty, unit_price, net_amount) VALUES (gen_random_uuid(), '{h.CompanyId}', '{si}', 2, 'INVENTORY_PO', '{s.PoLineId}', 1, 0, 0)");

        Assert.Equal(SqlStates.CheckViolation, poLine?.SqlState);
        Assert.Equal(SqlStates.CheckViolation, siLine?.SqlState);
    }
}
