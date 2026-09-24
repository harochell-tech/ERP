using System.Text.Json;
using Rochell.Inventory;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Inventory.Tests;

/// <summary>AT-01, AT-02, moving average (ADR-005, E-PR07-1), CON-03 and row hashes of the inventory ledgers.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class InventoryLedgerTests(PostgresFixture postgres)
{
    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static async Task<Guid> Receive(TestHarness h, Guid location, Guid item, decimal qty, decimal value, string key)
    {
        var result = await h.RunAsync(new TestReceiveStock(h.CompanyId, h.SessionId, key, location, item, qty, value, Today(h)), new TestReceiveStockHandler());
        return JsonDocument.Parse(result.ResultPayload).RootElement.GetProperty("lotId").GetGuid();
    }

    private static async Task<decimal> Issue(TestHarness h, Guid location, Guid item, Guid lot, decimal qty, string key)
    {
        var result = await h.RunAsync(new TestIssueStock(h.CompanyId, h.SessionId, key, location, item, lot, qty, Today(h)), new TestIssueStockHandler());
        return decimal.Parse(JsonDocument.Parse(result.ResultPayload).RootElement.GetProperty("value").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Task<string?> Position(TestHarness h, Guid item)
        => h.ScalarAsync<string>(
            """
            SELECT (SELECT quantity FROM inv.inv_valuation_balance WHERE item_id = @i) || '|' ||
                   (SELECT value FROM inv.inv_valuation_balance WHERE item_id = @i) || '|' ||
                   (SELECT coalesce(sum(debit - credit), 0) FROM fin.gl_entry WHERE item_id = @i AND account_role = 'RAW_MATERIAL')
            """,
            ("i", item));

    [Fact]
    public async Task AT01_AT02_receipts_keep_ledgers_balances_and_GL_in_agreement()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();

        var lot1 = await Receive(h, s.LocationA, s.ItemId, 100m, 1000.00m, "r-1");
        var lot2 = await Receive(h, s.LocationB, s.ItemId, 50.5m, 757.50m, "r-2");

        Assert.NotEqual(lot1, lot2);
        Assert.Equal("150.500000|1757.5000|1757.5000", await Position(h, s.ItemId));
        Assert.Equal("100.000000;50.500000", await h.ScalarAsync<string>(
            "SELECT string_agg(quantity::text, ';' ORDER BY quantity DESC) FROM inv.inv_stock_balance WHERE item_id = @i", ("i", s.ItemId)));
        Assert.Equal(0L, await h.ScalarAsync<long>(
            """
            SELECT count(*) FROM inv.inv_stock_balance b
            WHERE b.quantity <> (SELECT sum(q.quantity) FROM inv.inv_quantity_entry q WHERE q.location_id = b.location_id AND q.item_id = b.item_id AND q.lot_id = b.lot_id)
            """));
        Assert.Equal(0L, await h.ScalarAsync<long>(
            "SELECT count(*) FROM inv.inv_value_entry v WHERE NOT EXISTS (SELECT 1 FROM fin.gl_entry g WHERE g.inv_value_entry_id = v.value_entry_id AND g.debit - g.credit = v.amount)"));
    }

    [Fact]
    public async Task Moving_average_issue_and_the_last_unit_takes_the_remaining_value()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        var lot1 = await Receive(h, s.LocationA, s.ItemId, 100m, 1000.00m, "m-1");
        var lot2 = await Receive(h, s.LocationA, s.ItemId, 100m, 1500.00m, "m-2");

        var first = await Issue(h, s.LocationA, s.ItemId, lot1, 50m, "m-3");      // average 12.50
        var second = await Issue(h, s.LocationA, s.ItemId, lot1, 50m, "m-4");
        var last = await Issue(h, s.LocationA, s.ItemId, lot2, 100m, "m-5");       // remaining value

        Assert.Equal(625.00m, first);
        Assert.Equal(625.00m, second);
        Assert.Equal(1250.00m, last);
        Assert.Equal("0.000000|0.0000|0.0000", await Position(h, s.ItemId));
    }

    [Fact]
    public async Task Rounding_to_two_decimals_never_leaves_value_behind()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        var lot = await Receive(h, s.LocationA, s.ItemId, 3m, 10.00m, "q-1");

        var values = new[]
        {
            await Issue(h, s.LocationA, s.ItemId, lot, 1m, "q-2"), // 10.00 / 3 = 3.333… → 3.33
            await Issue(h, s.LocationA, s.ItemId, lot, 1m, "q-3"), // 6.67 / 2 = 3.335 → 3.34 (half-up)
            await Issue(h, s.LocationA, s.ItemId, lot, 1m, "q-4"), // last unit → 3.33
        };

        Assert.Equal([3.33m, 3.34m, 3.33m], values);
        Assert.Equal("0.000000|0.0000|0.0000", await Position(h, s.ItemId));
    }

    [Fact]
    public async Task CON03_issue_beyond_stock_is_rejected_and_nothing_is_written()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        var lot = await Receive(h, s.LocationA, s.ItemId, 10m, 100.00m, "n-1");
        var before = await h.CountsAsync();

        var ex = await Assert.ThrowsAsync<DomainException>(() => Issue(h, s.LocationA, s.ItemId, lot, 10.000001m, "n-2"));
        var otherLocation = await Assert.ThrowsAsync<DomainException>(() => Issue(h, s.LocationB, s.ItemId, lot, 1m, "n-3"));

        Assert.Equal(InventoryErrors.InsufficientStock, ex.Code);
        Assert.Equal(InventoryErrors.InsufficientStock, otherLocation.Code);
        Assert.Equal(before, await h.CountsAsync());
        Assert.Equal("10.000000|100.0000|100.0000", await Position(h, s.ItemId));
    }

    [Fact]
    public async Task CON03_fifty_concurrent_issues_never_take_stock_below_zero()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        var lot = await Receive(h, s.LocationA, s.ItemId, 30m, 300.00m, "c-0");

        var attempts = await Task.WhenAll(Enumerable.Range(1, 50).Select(async i =>
        {
            try
            {
                await Issue(h, s.LocationA, s.ItemId, lot, 1m, $"c-{i}");
                return "ok";
            }
            catch (DomainException ex)
            {
                return ex.Code;
            }
        }));

        Assert.Equal(30, attempts.Count(a => a == "ok"));
        Assert.Equal(20, attempts.Count(a => a == InventoryErrors.InsufficientStock));
        Assert.Equal("0.000000|0.0000|0.0000", await Position(h, s.ItemId));
        Assert.Equal(0m, await h.ScalarAsync<decimal>("SELECT sum(quantity) FROM inv.inv_quantity_entry WHERE item_id = @i", ("i", s.ItemId)));
    }

    [Fact]
    public async Task Row_hashes_of_both_ledgers_recompute_from_stored_rows()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        var lot = await Receive(h, s.LocationA, s.ItemId, 12.345678m, 123.45m, "h-1");
        await Issue(h, s.LocationA, s.ItemId, lot, 2m, "h-2");

        var checkedRows = 0;
        await using (var q = h.Admin.CreateCommand(
            """
            SELECT quantity_entry_id, company_id, movement_type::text, plant_id, location_id, item_id, lot_id, quantity, source_event_id,
                   source_document_type, source_document_id, source_line_id, reverses_quantity_entry_id, occurred_at, recorded_at,
                   business_date, posting_date, row_hash
            FROM inv.inv_quantity_entry
            """))
        await using (var reader = await q.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                Assert.Equal(reader.GetFieldValue<byte[]>(17), InventoryLedger.ReadQuantityEntry(reader).ComputeRowHash());
                checkedRows++;
            }
        }

        await using (var v = h.Admin.CreateCommand(
            """
            SELECT value_entry_id, company_id, movement_type::text, valuation_area_id, plant_id, item_id, quantity_entry_id, amount,
                   source_event_id, reverses_value_entry_id, occurred_at, recorded_at, business_date, posting_date, row_hash
            FROM inv.inv_value_entry
            """))
        await using (var reader = await v.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                Assert.Equal(reader.GetFieldValue<byte[]>(14), InventoryLedger.ReadValueEntry(reader).ComputeRowHash());
                checkedRows++;
            }
        }

        Assert.Equal(4, checkedRows);
    }
}
