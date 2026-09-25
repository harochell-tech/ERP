using System.Text.Json;
using Rochell.Finance;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Procurement.GoodsReceipts;
using Rochell.Procurement.PurchaseOrders;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>T-02 PostGoodsReceipt: AT-01, AT-05 (all or nothing, P-1), CC-01, PD-01, tolerance (K-13), UOM conversion (E-PR09-4).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class GoodsReceiptTests(PostgresFixture postgres)
{
    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    /// <summary>Creates, submits and approves (Controller: no limit) an order; returns the order and its line ids by item.</summary>
    private static async Task<(Guid Po, Dictionary<Guid, Guid> Lines)> ApprovedOrderAsync(TestHarness h, TestReceiving r, PurchaseOrderLineInput[] lines, string key, DateOnly? orderDate = null)
    {
        var p = r.Purchasing;
        var po = (await h.RunAsync(new CreatePurchaseOrder(h.CompanyId, p.Buyer, key + "-c", p.PlantId, p.SupplierId, orderDate ?? Today(h), lines), new CreatePurchaseOrderHandler())).ResultRef;
        await h.RunAsync(new SubmitPurchaseOrder(h.CompanyId, p.Buyer, key + "-s", p.PlantId, po, 1), new SubmitPurchaseOrderHandler());
        await h.RunAsync(new ApprovePurchaseOrder(h.CompanyId, p.Controller, key + "-a", p.PlantId, po, 2), new ApprovePurchaseOrderHandler());
        var ids = new Dictionary<Guid, Guid>();
        await using var command = h.Admin.CreateCommand("SELECT item_id, po_line_id FROM pur.purchase_order_line WHERE po_id = @p");
        command.Parameters.AddWithValue("p", po);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids[reader.GetGuid(0)] = reader.GetGuid(1);
        }

        return (po, ids);
    }

    private static Task<CommandResult> Receive(TestHarness h, TestReceiving r, Guid po, string key, GoodsReceiptLineInput[] lines, Guid? location = null, DateTime? occurredAt = null, string? ticket = null, Guid? session = null)
        => h.RunAsync(
            new PostGoodsReceipt(h.CompanyId, session ?? r.Storekeeper, key, r.Purchasing.PlantId, po, location ?? r.LocationA, occurredAt ?? h.Clock.UtcNow.AddMinutes(-5), lines, ticket),
            new PostGoodsReceiptHandler());

    private static Task<string?> Snapshot(TestHarness h, Guid po)
        => h.ScalarAsync<string>(
            """
            SELECT (SELECT status::text FROM pur.purchase_order WHERE po_id = @p) || '|' ||
                   (SELECT string_agg(qty_received::text, ',' ORDER BY line_no) FROM pur.purchase_order_line WHERE po_id = @p) || '|' ||
                   (SELECT count(*) FROM pur.goods_receipt) || '|' || (SELECT count(*) FROM inv.lot) || '|' ||
                   (SELECT coalesce(sum(quantity), 0) FROM inv.inv_stock_balance) || '|' || (SELECT count(*) FROM fin.gl_journal)
            """,
            ("p", po));

    [Trait("Acceptance", "AT-01")]
    [Fact]
    public async Task AT01_receipt_moves_order_inventory_and_ledger_together()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        var p = r.Purchasing;
        var (po, lines) = await ApprovedOrderAsync(h, r, [new(p.Sand, "t", 10m, 1500m), new(p.Cement, "t", 5m, 7800m)], "at01");

        var first = await Receive(h, r, po, "gr-1", [new(lines[p.Sand], 6m, "LOTE-PROV-77")], ticket: "BAS-0001");

        Assert.Equal("PARTIALLY_RECEIVED|6.000000,0.000000|1|1|6.000000|1", await Snapshot(h, po));
        var grId = first.ResultRef;
        Assert.True(await h.ScalarAsync<bool>(
            "SELECT document_status = 'POSTED' AND accounting_status = 'POSTED' AND weigh_ticket_ref = 'BAS-0001' AND gr_no LIKE 'RM-____-________' FROM pur.goods_receipt WHERE gr_id = @g",
            ("g", grId)));
        Assert.Equal("R01-DR-INV:1301:9000.0000:0.0000:item,R01-CR-GRNI:2105:0.0000:9000.0000:party", await h.ScalarAsync<string>(
            """
            SELECT string_agg(e.rule_line_code || ':' || a.code || ':' || e.debit || ':' || e.credit || ':' ||
                   CASE WHEN e.item_id IS NOT NULL THEN 'item' ELSE 'party' END, ',' ORDER BY e.line_no)
            FROM fin.gl_entry e JOIN fin.account a USING (account_id)
            JOIN fin.gl_journal j USING (journal_id) JOIN pur.goods_receipt g ON g.posting_event_id = j.source_event_id
            WHERE g.gr_id = @g
            """,
            ("g", grId)));
        Assert.Equal("9000.0000|LOTE-PROV-77|6.000000|9000.0000", await h.ScalarAsync<string>(
            """
            SELECT (SELECT value FROM inv.inv_valuation_balance) || '|' || (SELECT supplier_lot_number FROM inv.lot) || '|' ||
                   (SELECT qty FROM core.document_link WHERE link_type = 'RECEIVES') || '|' || (SELECT amount FROM core.document_link WHERE link_type = 'RECEIVES')
            """));

        await Receive(h, r, po, "gr-2", [new(lines[p.Sand], 4m), new(lines[p.Cement], 5m)]);

        Assert.Equal("RECEIVED|10.000000,5.000000|2|3|15.000000|2", await Snapshot(h, po));
        Assert.Equal(54000.00m, await h.ScalarAsync<decimal>("SELECT sum(debit) - sum(credit) FROM fin.gl_entry WHERE account_role = 'RAW_MATERIAL'"));
        Assert.Equal(-54000.00m, await h.ScalarAsync<decimal>("SELECT sum(debit) - sum(credit) FROM fin.gl_entry WHERE account_role = 'GRNI'"));
    }

    [Fact]
    public async Task Receipt_in_a_purchase_UOM_is_stocked_in_the_base_UOM()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        var p = r.Purchasing;
        var (po, lines) = await ApprovedOrderAsync(h, r, [new(p.Sand, "m3", 10m, 2000m)], "uom");

        await Receive(h, r, po, "gr-uom", [new(lines[p.Sand], 4m)]);

        // 4 m3 × 1.45 t/m3 = 5.8 t; value 4 × 2000 = 8000.00 (E-PR09-4).
        Assert.Equal("5.800000|8000.0000|4.000000", await h.ScalarAsync<string>(
            "SELECT (SELECT quantity FROM inv.inv_stock_balance) || '|' || (SELECT value FROM inv.inv_valuation_balance) || '|' || (SELECT qty FROM pur.goods_receipt_line)"));
    }

    [Fact]
    public async Task Tolerance_is_enforced_and_approved_over_receipt_extends_it()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        var p = r.Purchasing;
        var (po, lines) = await ApprovedOrderAsync(h, r, [new(p.Sand, "t", 10m, 1500m)], "tol");

        var over = await Assert.ThrowsAsync<DomainException>(() => Receive(h, r, po, "tol-1", [new(lines[p.Sand], 10.3m)]));   // max 10.2 (2 %)
        await h.RunAsync(new ApproveOverReceipt(h.CompanyId, p.Approver, "tol-ok", p.PlantId, po, lines[p.Sand], 0.1m, "Camión lleno"), new ApproveOverReceiptHandler());
        await Receive(h, r, po, "tol-2", [new(lines[p.Sand], 10.3m)]);

        Assert.Equal(ProcurementErrors.ReceiptToleranceExceeded, over.Code);
        Assert.Equal("RECEIVED|10.300000|1|1|10.300000|1", await Snapshot(h, po));
    }

    public static TheoryData<string> MissingPrerequisites() => new() { "rule", "mapping" };

    [Trait("Acceptance", "AT-05")]
    [Theory]
    [MemberData(nameof(MissingPrerequisites))]
    public async Task AT05_missing_posting_prerequisite_writes_nothing(string missing)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync(approveR01: missing != "rule", mapGrni: missing != "mapping");
        var p = r.Purchasing;
        var (po, lines) = await ApprovedOrderAsync(h, r, [new(p.Sand, "t", 10m, 1500m)], "at05");

        var ex = await Assert.ThrowsAsync<DomainException>(() => Receive(h, r, po, "at05-gr", [new(lines[p.Sand], 5m)]));

        Assert.Equal(FinanceErrors.PostingPrerequisiteMissing, ex.Code);
        Assert.Equal("APPROVED|0.000000|0|0|0|0", await Snapshot(h, po));
        Assert.Equal(0L, await h.CountAsync("inv.inv_quantity_entry"));
    }

    [Fact]
    public async Task Only_approved_orders_locations_of_the_plant_fresh_tickets_and_past_times_are_accepted()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        var p = r.Purchasing;
        var (po, lines) = await ApprovedOrderAsync(h, r, [new(p.Sand, "t", 10m, 1500m)], "guards");
        var draft = (await h.RunAsync(new CreatePurchaseOrder(h.CompanyId, p.Buyer, "draft", p.PlantId, p.SupplierId, Today(h), [new(p.Sand, "t", 1m, 1500m)]), new CreatePurchaseOrderHandler())).ResultRef;
        var draftLine = await h.ScalarAsync<Guid>("SELECT po_line_id FROM pur.purchase_order_line WHERE po_id = @p", ("p", draft));
        var foreignLocation = await h.CreateLocationAsync(await h.CreatePlantAsync(), "OTRA");
        await Receive(h, r, po, "g-ok", [new(lines[p.Sand], 1m)], ticket: "BAS-9");

        var notApproved = await Assert.ThrowsAsync<DomainException>(() => Receive(h, r, draft, "g-1", [new(draftLine, 1m)]));
        var wrongLocation = await Assert.ThrowsAsync<DomainException>(() => Receive(h, r, po, "g-2", [new(lines[p.Sand], 1m)], location: foreignLocation));
        var usedTicket = await Assert.ThrowsAsync<DomainException>(() => Receive(h, r, po, "g-3", [new(lines[p.Sand], 1m)], ticket: "BAS-9"));
        var future = await Assert.ThrowsAsync<DomainException>(() => Receive(h, r, po, "g-4", [new(lines[p.Sand], 1m)], occurredAt: h.Clock.UtcNow.AddHours(1)));
        var otherPoLine = await Assert.ThrowsAsync<DomainException>(() => Receive(h, r, po, "g-5", [new(draftLine, 1m)]));

        Assert.Equal(ProcurementErrors.NotReceivable, notApproved.Code);
        Assert.Equal(ProcurementErrors.LocationNotInPlant, wrongLocation.Code);
        Assert.Equal(ProcurementErrors.WeighTicketUsed, usedTicket.Code);
        Assert.Equal(ProcurementErrors.OccurredInFuture, future.Code);
        Assert.Equal(ProcurementErrors.LineNotFound, otherPoLine.Code);
    }

    [Fact]
    public async Task Buyers_cannot_receive_and_duplicates_return_the_first_receipt()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        var p = r.Purchasing;
        var (po, lines) = await ApprovedOrderAsync(h, r, [new(p.Sand, "t", 10m, 1500m)], "perm");

        var denied = await Assert.ThrowsAsync<DomainException>(() => Receive(h, r, po, "p-1", [new(lines[p.Sand], 1m)], session: p.Buyer));
        var first = await Receive(h, r, po, "p-2", [new(lines[p.Sand], 1m)]);
        var again = await Receive(h, r, po, "p-2", [new(lines[p.Sand], 1m)]);

        Assert.Equal(AuthorizationErrors.NotAuthorized, denied.Code);
        Assert.True(again.Duplicate);
        Assert.Equal(first.ResultRef, again.ResultRef);
        Assert.Equal(1L, await h.CountAsync("pur.goods_receipt"));
    }

    [Trait("Acceptance", "PD-01")]
    [Fact]
    public async Task PD01_receipt_of_a_closed_month_is_posted_late_in_the_next_open_period()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        var p = r.Purchasing;
        var lastMonth = Today(h).AddMonths(-1);
        var (po, lines) = await ApprovedOrderAsync(h, r, [new(p.Sand, "t", 10m, 1500m)], "pd01", orderDate: lastMonth);
        await h.SetComponentAsync(lastMonth, "INV-MOV", "CLOSED");
        var occurred = new DateTime(lastMonth.Year, lastMonth.Month, 15, 16, 0, 0, DateTimeKind.Utc);

        var result = await Receive(h, r, po, "pd01-gr", [new(lines[p.Sand], 2m)], occurredAt: occurred);

        var payload = JsonDocument.Parse(result.ResultPayload).RootElement;
        var firstOfMonth = new DateOnly(Today(h).Year, Today(h).Month, 1);
        Assert.True(payload.GetProperty("lateEntry").GetBoolean());
        Assert.Equal(firstOfMonth.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), payload.GetProperty("postingDate").GetString());
        Assert.Equal($"{firstOfMonth:yyyy-MM-dd}|{lastMonth.Year:D4}-{lastMonth.Month:D2}-15", await h.ScalarAsync<string>(
            "SELECT posting_date::text || '|' || business_date::text FROM inv.inv_value_entry"));
    }

    [Trait("Acceptance", "CC-01")]
    [Fact]
    public async Task CC01_concurrent_receipts_never_exceed_the_tolerance()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        var p = r.Purchasing;
        var (po, lines) = await ApprovedOrderAsync(h, r, [new(p.Sand, "t", 10m, 1500m)], "cc01");

        var outcomes = await Task.WhenAll(Enumerable.Range(1, 5).Select(async i =>
        {
            try
            {
                await Receive(h, r, po, $"cc-{i}", [new(lines[p.Sand], 3m)]);
                return "ok";
            }
            catch (DomainException ex)
            {
                return ex.Code;
            }
        }));

        Assert.Equal(3, outcomes.Count(o => o == "ok"));
        Assert.Equal(2, outcomes.Count(o => o == ProcurementErrors.ReceiptToleranceExceeded));
        Assert.Equal("PARTIALLY_RECEIVED|9.000000|3|3|9.000000|3", await Snapshot(h, po));
    }
}
