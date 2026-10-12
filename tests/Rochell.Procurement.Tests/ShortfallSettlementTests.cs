using System.Text.Json;
using Rochell.Platform.Time;
using Rochell.Procurement.GoodsReceipts;
using Rochell.Procurement.PurchaseOrders;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>
/// VS3-02c (E-VS3-02c-2): production left 4 t of cement short in PATIO-A at the standard price 7,000.00 (28,000.00). A receipt of 1 t while
/// P-48 is still DRAFT leaves it open; once P-48 is active, a receipt of 5 t at 7,800.00 covers the 4 t first: the area's moving average is
/// (7,800 + 39,000) ÷ 6 = 7,800, so 31,200.00 leave RAW_MATERIAL against 28,000.00 of the bridge account and 3,200.00 go to price variance.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ShortfallSettlementTests(PostgresFixture postgres)
{
    private const string P48 = "0192f001-0000-7000-8000-00000000003b";

    [Fact]
    public async Task A_receipt_covers_first_what_production_consumed_without_stock()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        var p = r.Purchasing;
        var today = BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);
        var bridge = await h.CreateAccountAsync("2190", "Materia prima consumida por regularizar", isControl: false);
        var variance = await h.CreateAccountAsync("5160", "Variación de precio de materiales", isControl: false);
        await h.CreateActiveMapAsync("MATERIAL_SHORTFALL", bridge);
        await h.CreateActiveMapAsync("MATERIAL_PRICE_VARIANCE", variance);
        await h.AdminRequireAsync(
            $"""
            INSERT INTO inv.material_shortfall (shortfall_id, company_id, plant_id, location_id, item_id, source_type, source_id, business_date, qty, std_price, value, event_id, status, created_at, version)
            VALUES (gen_random_uuid(), '{h.CompanyId}', '{p.PlantId}', '{r.LocationA}', '{p.Cement}', 'SHIFT_SUMMARY', gen_random_uuid(), '{today:yyyy-MM-dd}', 4, 7000, 28000, gen_random_uuid(), 'OPEN', now(), 1);
            """);

        async Task<JsonElement> ReceiveAsync(string key, decimal qty)
        {
            var po = (await h.RunAsync(new CreatePurchaseOrder(h.CompanyId, p.Buyer, key + "-c", p.PlantId, p.SupplierId, today, [new(p.Cement, "t", qty, 7800m)]), new CreatePurchaseOrderHandler())).ResultRef;
            await h.RunAsync(new SubmitPurchaseOrder(h.CompanyId, p.Buyer, key + "-s", p.PlantId, po, 1), new SubmitPurchaseOrderHandler());
            await h.RunAsync(new ApprovePurchaseOrder(h.CompanyId, p.Controller, key + "-a", p.PlantId, po, 2), new ApprovePurchaseOrderHandler());
            var line = await h.ScalarAsync<Guid>("SELECT po_line_id FROM pur.purchase_order_line WHERE po_id = @p", ("p", po));
            return JsonDocument.Parse((await h.RunAsync(
                new PostGoodsReceipt(h.CompanyId, r.Storekeeper, key, p.PlantId, po, r.LocationA, h.Clock.UtcNow.AddMinutes(-1), [new(line, qty)]), new PostGoodsReceiptHandler())).ResultPayload).RootElement;
        }

        var first = await ReceiveAsync("gr-1", 1m);
        var stillOpen = await h.ScalarAsync<string>("SELECT status || ':' || settled_qty FROM inv.material_shortfall");
        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '{P48}' AND version = 1");
        var second = await ReceiveAsync("gr-2", 5m);

        Assert.Equal(JsonValueKind.Null, first.GetProperty("settlementJournalId").ValueKind);
        Assert.Equal("OPEN:0.000000", stillOpen);
        Assert.Equal(1, second.GetProperty("shortfallsCovered").GetInt32());
        Assert.Equal("SETTLED:4.000000:28000.0000", await h.ScalarAsync<string>("SELECT status || ':' || settled_qty || ':' || settled_value FROM inv.material_shortfall"));
        Assert.Equal("4.000000:28000.0000:31200.0000:GOODS_RECEIPT", await h.ScalarAsync<string>("SELECT qty || ':' || std_value || ':' || actual_value || ':' || source_type FROM inv.material_shortfall_settlement"));
        Assert.Equal(("28000.00", "3200.00", 2m), (
            await h.ScalarAsync<string>($"SELECT coalesce(sum(debit - credit), 0)::numeric(19,2)::text FROM fin.gl_entry WHERE account_id = '{bridge}'"),
            await h.ScalarAsync<string>($"SELECT coalesce(sum(debit - credit), 0)::numeric(19,2)::text FROM fin.gl_entry WHERE account_id = '{variance}'"),
            await h.ScalarAsync<decimal>("SELECT sum(quantity) FROM inv.inv_stock_balance WHERE item_id = @i", ("i", p.Cement))));
    }
}
