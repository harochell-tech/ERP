using System.Text.Json;
using Rochell.Finance;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Procurement.GoodsReceipts;
using Rochell.Procurement.PurchaseOrders;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>T-03 ReverseGoodsReceipt: RC-01, RC-02, guards (E-8 §5.2), R-02 A exact inverse and R-02B reallocation (Patch 1 P-4, E-PR10-1).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class GoodsReceiptReversalTests(PostgresFixture postgres)
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

    private static async Task<(Guid Gr, Guid Lot)> ReceiveAsync(TestHarness h, TestReceiving r, Guid po, Guid line, decimal qty, string key, string? ticket = null)
    {
        var result = await h.RunAsync(
            new PostGoodsReceipt(h.CompanyId, r.Storekeeper, key, r.Purchasing.PlantId, po, r.LocationA, h.Clock.UtcNow.AddMinutes(-5), [new(line, qty)], ticket),
            new PostGoodsReceiptHandler());
        var lot = JsonDocument.Parse(result.ResultPayload).RootElement.GetProperty("lines")[0].GetProperty("lotId").GetGuid();
        return (result.ResultRef, lot);
    }

    private static Task<CommandResult> Reverse(TestHarness h, TestReceiving r, Guid gr, string key, Guid? session = null)
        => h.RunAsync(new ReverseGoodsReceipt(h.CompanyId, session ?? r.Purchasing.Controller, key, gr, "Recepción registrada contra la OC equivocada"), new ReverseGoodsReceiptHandler());

    private static Task Issue(TestHarness h, TestReceiving r, Guid lot, decimal qty, string key)
        => h.RunAsync(new TestIssueStock(h.CompanyId, h.SessionId, key, r.LocationA, r.Purchasing.Sand, lot, qty, Today(h)), new TestIssueStockHandler());

    /// <summary>valuation quantity | valuation value | GL RAW_MATERIAL | GL PURCHASE_PRICE_VARIANCE (debit − credit).</summary>
    private static Task<string?> Area(TestHarness h)
        => h.ScalarAsync<string>(
            """
            SELECT (SELECT quantity FROM inv.inv_valuation_balance) || '|' || (SELECT value FROM inv.inv_valuation_balance) || '|' ||
                   (SELECT coalesce(sum(debit - credit), 0) FROM fin.gl_entry WHERE account_role = 'RAW_MATERIAL') || '|' ||
                   (SELECT coalesce(sum(debit - credit), 0) FROM fin.gl_entry WHERE account_role = 'PURCHASE_PRICE_VARIANCE')
            """);

    [Fact]
    public async Task RC01_clean_reversal_restores_everything_and_keeps_the_original_journal()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        await h.EnableReallocationAsync();
        var (po, line) = await OrderAsync(h, r, 10m, 1500m, "rc01");
        var (gr, _) = await ReceiveAsync(h, r, po, line, 6m, "rc01-gr", ticket: "BAS-77");

        var result = await Reverse(h, r, gr, "rc01-rev");

        Assert.True(await h.ScalarAsync<bool>(
            "SELECT document_status = 'REVERSED' AND accounting_status = 'REVERSED' FROM pur.goods_receipt WHERE gr_id = @g", ("g", gr)));
        Assert.Equal("APPROVED|0.000000", await h.ScalarAsync<string>(
            "SELECT (SELECT status::text FROM pur.purchase_order) || '|' || (SELECT qty_received FROM pur.purchase_order_line)"));
        Assert.Equal("0.000000|0.0000|0.0000|0", await Area(h));
        Assert.Equal("AUTO,REVERSAL", await h.ScalarAsync<string>("SELECT string_agg(journal_type, ',' ORDER BY journal_type) FROM fin.gl_journal"));
        Assert.Equal(0m, await h.ScalarAsync<decimal>("SELECT sum(debit - credit) FROM fin.gl_entry WHERE account_role = 'GRNI'"));
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM inv.inv_stock_balance WHERE quantity <> 0"));
        Assert.True(JsonDocument.Parse(result.ResultPayload).RootElement.GetProperty("reallocationJournalId").ValueKind == JsonValueKind.Null);
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM core.document_link WHERE link_type = 'REVERSES' AND to_id = @g", ("g", gr)));

        // The weigh ticket is free again.
        await ReceiveAsync(h, r, po, line, 1m, "rc01-again", ticket: "BAS-77");
    }

    [Fact]
    public async Task Reversing_one_of_two_receipts_leaves_the_order_partially_received()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        await h.EnableReallocationAsync();
        var (po, line) = await OrderAsync(h, r, 10m, 1500m, "part");
        await ReceiveAsync(h, r, po, line, 4m, "part-1");
        var (second, _) = await ReceiveAsync(h, r, po, line, 6m, "part-2");

        await Reverse(h, r, second, "part-rev");

        Assert.Equal("PARTIALLY_RECEIVED|4.000000", await h.ScalarAsync<string>(
            "SELECT (SELECT status::text FROM pur.purchase_order) || '|' || (SELECT qty_received FROM pur.purchase_order_line)"));
        Assert.Equal("4.000000|6000.0000|6000.0000|0", await Area(h));
    }

    [Fact]
    public async Task RC02_a_lot_that_moved_after_the_receipt_requires_a_correction()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        await h.EnableReallocationAsync();
        await h.EnableTestIssueAsync();
        var (po, line) = await OrderAsync(h, r, 10m, 1500m, "rc02");
        var (gr, lot) = await ReceiveAsync(h, r, po, line, 6m, "rc02-gr");
        await Issue(h, r, lot, 1m, "rc02-issue");
        var before = await Area(h);

        var ex = await Assert.ThrowsAsync<DomainException>(() => Reverse(h, r, gr, "rc02-rev"));

        Assert.Equal(ProcurementErrors.UseReceiptCorrection, ex.Code);
        Assert.Equal(before, await Area(h));
        Assert.Equal(0L, await h.CountAsync("pur.goods_receipt_reversal"));
    }

    [Fact]
    public async Task A_receipt_is_reversed_only_once_only_by_authorized_users_with_step_up()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var r = await h.CreateReceivingSetupAsync();
        await h.EnableReallocationAsync();
        var (po, line) = await OrderAsync(h, r, 10m, 1500m, "once");
        var (gr, _) = await ReceiveAsync(h, r, po, line, 6m, "once-gr");

        var storekeeper = await Assert.ThrowsAsync<DomainException>(() => Reverse(h, r, gr, "once-0", session: r.Storekeeper));
        await Reverse(h, r, gr, "once-1");
        var twice = await Assert.ThrowsAsync<DomainException>(() => Reverse(h, r, gr, "once-2"));
        clock.Advance(TimeSpan.FromMinutes(6));
        var stale = await Assert.ThrowsAsync<DomainException>(() => Reverse(h, r, gr, "once-3"));

        Assert.Equal(AuthorizationErrors.NotAuthorized, storekeeper.Code);
        Assert.Equal(ProcurementErrors.ReceiptNotReversible, twice.Code);
        Assert.Equal(AuthorizationErrors.StepUpRequired, stale.Code);
        Assert.Equal(1L, await h.CountAsync("pur.goods_receipt_reversal"));
    }

    /// <summary>
    /// Two lots of the same item in the area; the first lot is consumed (R-T1) at the moving average; then the receipt of the
    /// untouched second lot is reversed. Expected area and GL after the reversal, per Patch 1 P-4 and E-PR10-1.
    /// </summary>
    [Theory]
    [InlineData(10, 20, 100, "0.000000|0.0000|0.0000|-500.0000")]     // qty 0, value −500 → R = +500: Dr RAW / Cr PPV
    [InlineData(30, 10, 100, "0.000000|0.0000|0.0000|1000.0000")]      // qty 0, value +1000 → R = −1000: Cr RAW / Dr PPV
    [InlineData(10, 30, 90, "10.000000|200.0000|200.0000|-1000.0000")] // qty 10, value −800 → target 10 × avg₀ 20 = 200 → R = +1000
    [InlineData(10, 20, 50, "50.000000|250.0000|250.0000|0")]          // qty 50, value 250 > 0 → no reallocation
    public async Task R02B_reallocates_only_orphan_or_non_positive_values(int firstPrice, int secondPrice, int consumedFromFirst, string expected)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        await h.EnableReallocationAsync();
        await h.EnableTestIssueAsync();
        var (po1, line1) = await OrderAsync(h, r, 100m, firstPrice, "r02b-1");
        var (po2, line2) = await OrderAsync(h, r, 100m, secondPrice, "r02b-2");
        var (_, lot1) = await ReceiveAsync(h, r, po1, line1, 100m, "r02b-gr1");
        var (gr2, _) = await ReceiveAsync(h, r, po2, line2, 100m, "r02b-gr2");
        await Issue(h, r, lot1, consumedFromFirst, "r02b-issue");

        var result = await Reverse(h, r, gr2, "r02b-rev");

        Assert.Equal(expected, await Area(h));
        var reallocated = JsonDocument.Parse(result.ResultPayload).RootElement.GetProperty("reallocationJournalId").ValueKind != JsonValueKind.Null;
        Assert.Equal(!expected.EndsWith("|0", StringComparison.Ordinal), reallocated);
        if (reallocated)
        {
            Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM fin.gl_journal WHERE journal_type = 'VALUATION_REALLOCATION'"));
            Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM inv.inv_value_entry WHERE movement_type = 'VALUATION_REALLOCATION'"));
        }
    }

    [Fact]
    public async Task Needed_reallocation_without_an_active_R02B_rolls_back_the_whole_reversal()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        await h.EnableReallocationAsync(approveR02B: false);
        await h.EnableTestIssueAsync();
        var (po1, line1) = await OrderAsync(h, r, 100m, 10m, "nb-1");
        var (po2, line2) = await OrderAsync(h, r, 100m, 20m, "nb-2");
        var (_, lot1) = await ReceiveAsync(h, r, po1, line1, 100m, "nb-gr1");
        var (gr2, _) = await ReceiveAsync(h, r, po2, line2, 100m, "nb-gr2");
        await Issue(h, r, lot1, 100m, "nb-issue");
        var before = await Area(h);

        var ex = await Assert.ThrowsAsync<DomainException>(() => Reverse(h, r, gr2, "nb-rev"));

        Assert.Equal(FinanceErrors.PostingPrerequisiteMissing, ex.Code);
        Assert.Equal(before, await Area(h));
        Assert.Equal("POSTED", await h.ScalarAsync<string>("SELECT document_status::text FROM pur.goods_receipt WHERE gr_id = @g", ("g", gr2)));
    }
}
