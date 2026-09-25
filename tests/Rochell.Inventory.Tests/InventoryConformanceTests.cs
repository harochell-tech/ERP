using System.Globalization;
using System.Text.Json;
using Npgsql;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Inventory.Tests;

/// <summary>IV-01, IV-04 (E-PR19-5), IV-05 (E-PR19-6), the single owner of VS#1 (E-PR19-12) and the balance's tenant keys (E-PR19-13).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class InventoryConformanceTests(PostgresFixture postgres)
{
    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static async Task<Guid> Receive(TestHarness h, TestStock s, decimal qty, decimal value, string key)
    {
        var result = await h.RunAsync(new TestReceiveStock(h.CompanyId, h.SessionId, key, s.LocationA, s.ItemId, qty, value, Today(h)), new TestReceiveStockHandler());
        return JsonDocument.Parse(result.ResultPayload).RootElement.GetProperty("lotId").GetGuid();
    }

    private static async Task<string> Issue(TestHarness h, TestStock s, Guid lot, decimal qty, string key)
    {
        var result = await h.RunAsync(new TestIssueStock(h.CompanyId, h.SessionId, key, s.LocationA, s.ItemId, lot, qty, Today(h)), new TestIssueStockHandler());
        return JsonDocument.Parse(result.ResultPayload).RootElement.GetProperty("value").GetString()!;
    }

    [Trait("Acceptance", "IV-01")]
    [Fact]
    public async Task IV01_no_command_or_direct_write_leaves_stock_negative()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        var lot = await Receive(h, s, 10m, 1000m, "r");

        var command = await Assert.ThrowsAsync<DomainException>(() => Issue(h, s, lot, 10.000001m, "i"));
        var direct = await h.AppExecuteAsync("UPDATE inv.inv_stock_balance SET quantity = quantity - 10.000001");

        Assert.Equal(InventoryErrors.InsufficientStock, command.Code);
        Assert.Equal(SqlStates.CheckViolation, direct?.SqlState); // the application role's last guard is the CHECK
        Assert.Equal("10.000000", (await h.ScalarAsync<decimal>("SELECT quantity FROM inv.inv_stock_balance")).ToString(CultureInfo.InvariantCulture));
    }

    [Trait("Acceptance", "IV-04")]
    [Fact]
    public async Task IV04_there_is_no_RESERVED_availability_nor_reserved_quantity()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM pg_enum WHERE enumlabel = 'RESERVED'"));
        Assert.Equal(0L, await h.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.columns WHERE table_schema NOT IN ('pg_catalog', 'information_schema') AND column_name IN ('qty_reserved', 'availability')"));
    }

    /// <summary>E-PR19-12: VS#1 does not model separate ownership; everything in inventory belongs to the company.</summary>
    [Fact]
    public async Task Inventory_ledgers_have_a_single_owner_the_company()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        Assert.Equal(0L, await h.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'inv' AND column_name IN ('accounting_owner', 'legal_title_holder')"));
    }

    [Trait("Acceptance", "IV-05")]
    [Fact]
    public async Task IV05_the_physical_lot_issued_does_not_change_the_accounting()
    {
        // Two identical deployments: 10 t received at 100 and 10 t at 80 (average 90); 5 t issued from one lot or the other.
        var results = new List<string>();
        foreach (var fromCheaperLot in new[] { false, true })
        {
            await using var h = await TestHarness.CreateAsync(postgres);
            var s = await h.CreateStockSetupAsync();
            var expensive = await Receive(h, s, 10m, 1000m, "r-1");
            var cheap = await Receive(h, s, 10m, 800m, "r-2");

            var issued = await Issue(h, s, fromCheaperLot ? cheap : expensive, 5m, "i");

            results.Add(issued + "|" + await h.ScalarAsync<string>(
                """
                SELECT (SELECT quantity || '/' || value FROM inv.inv_valuation_balance) || '|' ||
                       (SELECT sum(debit - credit) FROM fin.gl_entry WHERE account_role = 'RAW_MATERIAL') || '|' ||
                       (SELECT sum(debit - credit) FROM fin.gl_entry WHERE account_role = 'TEST_EXPENSE')
                """));
        }

        Assert.Equal("450|15.000000/1350.0000|1350.0000|450.0000", results[0]);
        Assert.Equal(results[0], results[1]);
    }

    /// <summary>E-PR19-10: the control totals behind the O(1) position check are written only by the ledger triggers.</summary>
    [Fact]
    public async Task Position_control_totals_follow_the_ledgers_and_the_application_cannot_write_them()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        var lot = await Receive(h, s, 10m, 1000m, "r-1");
        await Receive(h, s, 5m, 600m, "r-2");
        await Issue(h, s, lot, 4m, "i");

        var update = await h.AppExecuteAsync("UPDATE inv.inv_valuation_balance SET ledger_value = ledger_value + 1");
        var insert = await h.AppExecuteAsync(
            $"INSERT INTO inv.inv_valuation_balance (company_id, valuation_area_id, item_id, quantity, value, ledger_value) SELECT company_id, valuation_area_id, '{s.ItemId}', 0, 0, 5 FROM md.plant WHERE plant_id = '{s.PlantId}' ON CONFLICT DO NOTHING");

        // 15 t / 1 600 → average 106.666…; 4 t issued at 426.67 → 11 t / 1 173.33.
        Assert.Equal("11.000000/1173.3300|11.000000/1173.3300/1173.3300", await h.ScalarAsync<string>(
            "SELECT quantity || '/' || value || '|' || ledger_quantity || '/' || ledger_value || '/' || gl_value FROM inv.inv_valuation_balance"));
        Assert.Equal("42501", update?.SqlState);
        Assert.Equal("42501", insert?.SqlState);
    }

    [Fact]
    public async Task A_stock_balance_row_belongs_to_the_company_of_its_item_and_lot()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        var lot = await Receive(h, s, 10m, 1000m, "r");
        var other = await h.CreateCompanyAsync();
        var plant = await h.CreatePlantAsync(other);
        var location = await h.CreateLocationAsync(plant, "AJENA", other);

        var foreign = await h.AdminExecuteAsync(
            $"INSERT INTO inv.inv_stock_balance (company_id, plant_id, location_id, item_id, lot_id, quantity) VALUES ('{other}', '{plant}', '{location}', '{s.ItemId}', '{lot}', 0)");

        Assert.Equal(SqlStates.ForeignKeyViolation, foreign?.SqlState);
        Assert.StartsWith("inv_stock_balance_company_", (foreign as PostgresException)?.ConstraintName, StringComparison.Ordinal);
        Assert.NotEqual("inv_stock_balance_company_location_fk", foreign?.ConstraintName);
    }
}
