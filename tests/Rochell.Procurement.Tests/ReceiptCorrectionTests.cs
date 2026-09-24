using System.Text.Json;
using Rochell.Finance;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Procurement.GoodsReceipts;
using Rochell.Procurement.PurchaseOrders;
using Rochell.Procurement.ReceiptCorrections;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>T-04/T-05 receipt corrections: R-03a, R-03b (q₁/q₂, RC-03), RC-04, RC-05, materiality and approval (E-PR11-4), lots (E-PR11-2/3).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ReceiptCorrectionTests(PostgresFixture postgres)
{
    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static async Task<(Guid Po, Guid PoLine)> OrderAsync(TestHarness h, TestReceiving r, decimal qty, decimal price, string key)
    {
        var p = r.Purchasing;
        var po = (await h.RunAsync(new CreatePurchaseOrder(h.CompanyId, p.Buyer, key + "-c", p.PlantId, p.SupplierId, Today(h), [new(p.Sand, "t", qty, price)]), new CreatePurchaseOrderHandler())).ResultRef;
        await h.RunAsync(new SubmitPurchaseOrder(h.CompanyId, p.Buyer, key + "-s", p.PlantId, po, 1), new SubmitPurchaseOrderHandler());
        await h.RunAsync(new ApprovePurchaseOrder(h.CompanyId, p.Controller, key + "-a", p.PlantId, po, 2), new ApprovePurchaseOrderHandler());
        return (po, await h.ScalarAsync<Guid>("SELECT po_line_id FROM pur.purchase_order_line WHERE po_id = @p", ("p", po)));
    }

    private static async Task<(Guid Gr, Guid GrLine, Guid Lot)> ReceiveAsync(TestHarness h, TestReceiving r, Guid po, Guid poLine, decimal qty, string key, Guid? location = null)
    {
        var result = await h.RunAsync(
            new PostGoodsReceipt(h.CompanyId, r.Storekeeper, key, r.Purchasing.PlantId, po, location ?? r.LocationA, h.Clock.UtcNow.AddMinutes(-5), [new(poLine, qty)]),
            new PostGoodsReceiptHandler());
        var line = JsonDocument.Parse(result.ResultPayload).RootElement.GetProperty("lines")[0];
        return (result.ResultRef, line.GetProperty("grLineId").GetGuid(), line.GetProperty("lotId").GetGuid());
    }

    private static async Task<(Guid Id, string Status)> CreateAsync(TestHarness h, TestReceiving r, Guid gr, Guid grLine, decimal delta, string key, Guid? session = null)
    {
        var result = await h.RunAsync(
            new CreateReceiptCorrection(h.CompanyId, session ?? r.Storekeeper, key, r.Purchasing.PlantId, gr, grLine, delta, "Ticket de báscula corregido", "BAS-2026-0147-R"),
            new CreateReceiptCorrectionHandler());
        return (result.ResultRef, JsonDocument.Parse(result.ResultPayload).RootElement.GetProperty("status").GetString()!);
    }

    private static Task<CommandResult> Approve(TestHarness h, TestReceiving r, Guid rc, string key)
        => h.RunAsync(new ApproveReceiptCorrection(h.CompanyId, r.Purchasing.Controller, key, rc), new ApproveReceiptCorrectionHandler());

    private static Task Issue(TestHarness h, TestReceiving r, Guid lot, decimal qty, string key, Guid? location = null)
        => h.RunAsync(new TestIssueStock(h.CompanyId, h.SessionId, key, location ?? r.LocationA, r.Purchasing.Sand, lot, qty, Today(h)), new TestIssueStockHandler());

    /// <summary>area qty | area value | RAW | GRNI | PPV | MUV (GL debit − credit) | PO qty received (first line).</summary>
    private static Task<string?> Books(TestHarness h, Guid po)
        => h.ScalarAsync<string>(
            """
            SELECT (SELECT quantity || '|' || value FROM inv.inv_valuation_balance) || '|' ||
                   (SELECT coalesce(sum(debit - credit), 0) FROM fin.gl_entry WHERE account_role = 'RAW_MATERIAL') || '|' ||
                   (SELECT coalesce(sum(debit - credit), 0) FROM fin.gl_entry WHERE account_role = 'GRNI') || '|' ||
                   (SELECT coalesce(sum(debit - credit), 0) FROM fin.gl_entry WHERE account_role = 'PURCHASE_PRICE_VARIANCE') || '|' ||
                   (SELECT coalesce(sum(debit - credit), 0) FROM fin.gl_entry WHERE account_role = 'MATERIAL_USAGE_VARIANCE') || '|' ||
                   (SELECT qty_received FROM pur.purchase_order_line WHERE po_id = @p)
            """,
            ("p", po));

    [Fact]
    public async Task R03a_more_received_than_recorded_under_materiality()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        await h.EnableCorrectionsAsync();
        var (po, poLine) = await OrderAsync(h, r, 10m, 1500m, "pos");
        var (gr, grLine, lot) = await ReceiveAsync(h, r, po, poLine, 6m, "pos-gr");

        var (rc, status) = await CreateAsync(h, r, gr, grLine, 1m, "pos-rc");
        await Approve(h, r, rc, "pos-ok");

        Assert.Equal("DRAFT", status);
        Assert.Equal("7.000000|10500.0000|10500.0000|-10500.0000|0|0|7.000000", await Books(h, po));
        Assert.Equal(7m, await h.ScalarAsync<decimal>("SELECT quantity FROM inv.inv_stock_balance WHERE lot_id = @l", ("l", lot)));
        Assert.Equal("CORRECTED|POSTED", await h.ScalarAsync<string>(
            "SELECT (SELECT document_status::text FROM pur.goods_receipt) || '|' || (SELECT document_status FROM pur.receipt_correction)"));
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM core.document_link WHERE link_type = 'CORRECTS' AND to_line_id = @g", ("g", grLine)));
    }

    [Fact]
    public async Task RC03_less_received_splits_into_remaining_stock_and_usage_variance()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        await h.EnableCorrectionsAsync();
        await h.EnableTestIssueAsync();
        var (po, poLine) = await OrderAsync(h, r, 30m, 1000m, "rc03");
        var (gr, grLine, lot) = await ReceiveAsync(h, r, po, poLine, 30m, "rc03-gr");
        await Issue(h, r, lot, 28m, "rc03-issue");

        var (rc, _) = await CreateAsync(h, r, gr, grLine, -3m, "rc03-rc");
        await Approve(h, r, rc, "rc03-ok");

        // q₁ = 2 (RAW −2 × avg 1000), q₂ = 1 (MUV −1 × P 1000), GRNI +3 × P; PPV 0; PO 27.
        Assert.Equal("0.000000|0.0000|0.0000|-27000.0000|0|-1000.0000|27.000000", await Books(h, po));
    }

    [Fact]
    public async Task R03b_takes_the_receipt_lot_first_then_the_oldest_lot_and_books_the_price_difference()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        await h.EnableCorrectionsAsync();
        await h.EnableTestIssueAsync();
        var (poA, lineA) = await OrderAsync(h, r, 10m, 3000m, "lots-a");
        var (poB, lineB) = await OrderAsync(h, r, 30m, 1000m, "lots-b");
        var (_, _, lotA) = await ReceiveAsync(h, r, poA, lineA, 10m, "lots-gr-a", location: r.LocationB);
        var (gr, grLine, lotB) = await ReceiveAsync(h, r, poB, lineB, 30m, "lots-gr-b");
        await Issue(h, r, lotB, 28m, "lots-issue"); // area 40 t / 60 000 → avg 1 500; leaves 12 t / 18 000 (lot B 2 t, lot A 10 t)

        var (rc, _) = await CreateAsync(h, r, gr, grLine, -3m, "lots-rc");
        await Approve(h, r, rc, "lots-ok");

        // q₁ = 3 at avg 1 500 = 4 500 (2 t of lot B, then 1 t of lot A); GRNI 3 × 1 000; PPV Dr 1 500; MUV 0.
        Assert.Equal("0.000000|9.000000", await h.ScalarAsync<string>(
            "SELECT (SELECT quantity FROM inv.inv_stock_balance WHERE lot_id = @b) || '|' || (SELECT quantity FROM inv.inv_stock_balance WHERE lot_id = @a)",
            ("b", lotB),
            ("a", lotA)));
        Assert.Equal("9.000000|13500.0000|13500.0000|-57000.0000|1500.0000|0|27.000000", await Books(h, poB));
    }

    [Fact]
    public async Task RC04_an_invoiced_quantity_cannot_be_corrected_away()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        await h.EnableCorrectionsAsync();
        var (po, poLine) = await OrderAsync(h, r, 30m, 1000m, "rc04");
        var (gr, grLine, _) = await ReceiveAsync(h, r, po, poLine, 30m, "rc04-gr");
        await h.AdminRequireAsync($"UPDATE pur.purchase_order_line SET qty_invoiced = 30, version = version + 1 WHERE po_line_id = '{poLine}'");

        var ex = await Assert.ThrowsAsync<DomainException>(() => CreateAsync(h, r, gr, grLine, -5m, "rc04-rc"));

        Assert.Equal(ProcurementErrors.AlreadyInvoiced, ex.Code);
    }

    [Fact]
    public async Task RC05_increase_beyond_tolerance_needs_an_approved_over_receipt()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        await h.EnableCorrectionsAsync();
        var p = r.Purchasing;
        var (po, poLine) = await OrderAsync(h, r, 10m, 1500m, "rc05");
        var (gr, grLine, _) = await ReceiveAsync(h, r, po, poLine, 10m, "rc05-gr");

        var ex = await Assert.ThrowsAsync<DomainException>(() => CreateAsync(h, r, gr, grLine, 0.5m, "rc05-1"));
        await h.RunAsync(new ApproveOverReceipt(h.CompanyId, p.Approver, "rc05-over", p.PlantId, po, poLine, 0.3m, "Camión con sobrepeso"), new ApproveOverReceiptHandler());
        var (rc, _) = await CreateAsync(h, r, gr, grLine, 0.5m, "rc05-2");
        await Approve(h, r, rc, "rc05-ok");

        Assert.Equal(ProcurementErrors.ReceiptToleranceExceeded, ex.Code);
        Assert.Equal(10.5m, await h.ScalarAsync<decimal>("SELECT qty_received FROM pur.purchase_order_line WHERE po_line_id = @l", ("l", poLine)));
    }

    [Fact]
    public async Task Above_materiality_waits_for_re_authenticated_approval_and_can_be_rejected()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var r = await h.CreateReceivingSetupAsync();
        await h.EnableCorrectionsAsync();
        var (po, poLine) = await OrderAsync(h, r, 20m, 1500m, "mat");
        var (gr, grLine, _) = await ReceiveAsync(h, r, po, poLine, 20m, "mat-gr");
        var before = await Books(h, po);

        var (big, status) = await CreateAsync(h, r, gr, grLine, -8m, "mat-rc");           // 8 × 1 500 = 12 000 > 10 000
        clock.Advance(TimeSpan.FromMinutes(6));
        var stale = await Assert.ThrowsAsync<DomainException>(() => Approve(h, r, big, "mat-ok"));
        await h.RunAsync(new RejectReceiptCorrection(h.CompanyId, r.Purchasing.Controller, "mat-no", big, "Ticket ilegible"), new RejectReceiptCorrectionHandler());
        var freshController = await h.SessionWithRolesAsync("CONTROLLER");
        var afterReject = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new ApproveReceiptCorrection(h.CompanyId, freshController, "mat-late", big), new ApproveReceiptCorrectionHandler()));

        Assert.Equal("PENDING_APPROVAL", status);
        Assert.Equal(AuthorizationErrors.StepUpRequired, stale.Code);
        Assert.Equal(ProcurementErrors.CorrectionNotPending, afterReject.Code);
        Assert.Equal(before, await Books(h, po));
        Assert.Equal("POSTED", await h.ScalarAsync<string>("SELECT document_status::text FROM pur.goods_receipt WHERE gr_id = @g", ("g", gr)));
    }

    [Fact]
    public async Task Missing_rule_writes_nothing_and_permissions_are_enforced()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        await h.EnableCorrectionsAsync(approveRules: false);
        var (po, poLine) = await OrderAsync(h, r, 10m, 1500m, "rule");
        var (gr, grLine, _) = await ReceiveAsync(h, r, po, poLine, 6m, "rule-gr");
        var before = await Books(h, po);

        var buyer = await Assert.ThrowsAsync<DomainException>(() => CreateAsync(h, r, gr, grLine, 1m, "rule-0", session: r.Purchasing.Buyer));
        var (rc, _) = await CreateAsync(h, r, gr, grLine, 1m, "rule-rc");
        var noRule = await Assert.ThrowsAsync<DomainException>(() => Approve(h, r, rc, "rule-ok"));

        Assert.Equal(AuthorizationErrors.NotAuthorized, buyer.Code);
        Assert.Equal(FinanceErrors.PostingPrerequisiteMissing, noRule.Code);
        Assert.Equal(before, await Books(h, po));
        Assert.Equal("DRAFT", await h.ScalarAsync<string>("SELECT document_status FROM pur.receipt_correction WHERE rc_id = @r", ("r", rc)));
    }

    [Fact]
    public async Task Corrected_receipts_cannot_be_reversed_and_reversed_receipts_cannot_be_corrected()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        await h.EnableCorrectionsAsync();
        var (po, poLine) = await OrderAsync(h, r, 20m, 1500m, "mix");
        var (corrected, correctedLine, _) = await ReceiveAsync(h, r, po, poLine, 6m, "mix-1");
        var (reversed, reversedLine, _) = await ReceiveAsync(h, r, po, poLine, 4m, "mix-2");
        var (rc, _) = await CreateAsync(h, r, corrected, correctedLine, 1m, "mix-rc");
        await Approve(h, r, rc, "mix-ok");
        await h.RunAsync(new ReverseGoodsReceipt(h.CompanyId, r.Purchasing.Controller, "mix-rev", reversed, "Duplicada"), new ReverseGoodsReceiptHandler());

        var reverseCorrected = await Assert.ThrowsAsync<DomainException>(() =>
            h.RunAsync(new ReverseGoodsReceipt(h.CompanyId, r.Purchasing.Controller, "mix-rev-2", corrected, "x"), new ReverseGoodsReceiptHandler()));
        var correctReversed = await Assert.ThrowsAsync<DomainException>(() => CreateAsync(h, r, reversed, reversedLine, 1m, "mix-rc-2"));

        Assert.Equal(ProcurementErrors.ReceiptNotReversible, reverseCorrected.Code);
        Assert.Equal(ProcurementErrors.InvalidState, correctReversed.Code);
    }
}
