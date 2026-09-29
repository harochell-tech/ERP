using System.Text.Json;
using Rochell.Inventory;
using Rochell.Manufacturing.Machines;
using Rochell.Manufacturing.Queries;
using Rochell.Manufacturing.Recipes;
using Rochell.Manufacturing.Runs;
using Rochell.Manufacturing.Shifts;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Sales.Pricing;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Manufacturing.Tests;

/// <summary>MFG1-03: runs, shift summaries, consumption at moving average (P-08) and the lot into CURADO at standard (P-10) (E-MFG1-03-1…12).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ProductionRunTests(PostgresFixture postgres)
{
    private const string P08 = "0192f001-0000-7000-8000-000000000024";
    private const string P10 = "0192f001-0000-7000-8000-000000000025";

    private sealed record Setup(
        Guid Plant, Guid Patio, Guid Machine, Guid Day, Guid Block, Guid Cement, Guid Sand, Guid Additive, Guid Supervisor, Guid Manager, DateOnly Today, Guid Wip, Guid Fg, Guid Conversion, Guid Raw);

    private static async Task<Setup> SetupAsync(TestHarness h, bool withCost = true)
    {
        var stock = await h.CreateStockSetupAsync();
        var today = BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);
        var sand = stock.ItemId; // ARENA-LAVADA, t
        var cement = await h.CreateActiveItemAsync("CEMENTO-GU", "kg", "CEMENTO");
        var additive = await h.CreateActiveItemAsync("ADITIVO-P", "l", "ADITIVO");
        var block = Guid.CreateVersion7();
        await h.AdminRequireAsync(
            $"""
            INSERT INTO md.item VALUES ('{block}', '{h.CompanyId}', 'BLOQUE-6', 'Bloque de 6 pulgadas', 'FINISHED_GOOD', 'un', 'BLOQUE', 'ACTIVE', 1);
            INSERT INTO md.uom_conversion VALUES ('{h.CompanyId}', '{sand}', 'm3', 't', 1.47, current_date - 30, NULL);
            UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id IN ('{P08}', '{P10}') AND version = 1;
            """);
        var wip = await h.CreateAccountAsync("1340", "Producción en proceso", isControl: true);
        var fg = await h.CreateAccountAsync("1350", "Inventario de producto terminado", isControl: true);
        var conversion = await h.CreateAccountAsync("5150", "Absorción de conversión", isControl: false);
        await h.CreateActiveMapAsync("WIP", wip);
        await h.CreateActiveMapAsync("FINISHED_GOODS", fg);
        await h.CreateActiveMapAsync("CONVERSION_ABSORPTION", conversion);

        // Stock: cement 20,000 kg for 164,000.00; sand in two lots (10 t, then 100 t) for 109,900.00; admixture 200 l for 10,000.00.
        async Task Receive(string key, Guid item, decimal qty, decimal value, int daysAgo)
            => await h.RunAsync(new TestReceiveStock(h.CompanyId, h.SessionId, key, stock.LocationA, item, qty, value, today.AddDays(-daysAgo)), new TestReceiveStockHandler());
        await Receive("r-cement", cement, 20000m, 164000.00m, 2);
        await Receive("r-sand-1", sand, 10m, 9900.00m, 3);
        await Receive("r-sand-2", sand, 100m, 100000.00m, 2);
        await Receive("r-additive", additive, 200m, 10000.00m, 2);

        var manager = await h.SessionWithRolesAsync("GERENTE_PLANTA");
        var supervisor = await h.SessionWithRolesAsync("SUPERVISOR_PRODUCCION");
        var machine = (await h.RunAsync(new CreateMachine(h.CompanyId, manager, "m", stock.PlantId, "BESSER-1", "Besser V3-12"), new CreateMachineHandler())).ResultRef;
        var day = (await h.RunAsync(new DefineShift(h.CompanyId, manager, "s", stock.PlantId, "DIA", new TimeOnly(7, 0), new TimeOnly(19, 0)), new DefineShiftHandler())).ResultRef;
        var recipe = (await h.RunAsync(
            new PrepareRecipe(h.CompanyId, supervisor, "r", stock.PlantId, block, machine, 150m, 6m, 600m, 24, 168,
                [new RecipeLineInput(cement, 180m), new RecipeLineInput(sand, 1.8m), new RecipeLineInput(additive, 1.5m)]),
            new PrepareRecipeHandler())).ResultRef;
        await h.RunAsync(new ApproveRecipe(h.CompanyId, manager, "ra", stock.PlantId, recipe), new ApproveRecipeHandler());
        if (withCost)
        {
            var controller = await h.SessionWithRolesAsync("CONTROLLER");
            var approver = await h.SessionWithRolesAsync("APROBADOR_POLITICAS");
            var cost = (await h.RunAsync(
                new PrepareStandardCostFromRecipe(h.CompanyId, controller, "c", recipe, [new(cement, 8.00m), new(sand, 1000.00m), new(additive, 50.00m)], 5.90m),
                new PrepareStandardCostFromRecipeHandler())).ResultRef;
            await h.RunAsync(new ApproveStandardCost(h.CompanyId, approver, "ca", cost), new ApproveStandardCostHandler());
        }

        return new Setup(stock.PlantId, stock.LocationA, machine, day, block, cement, sand, additive, supervisor, manager, today, wip, fg, conversion, stock.RawMaterialAccount);
    }

    private static Task<CommandResult> Start(TestHarness h, Setup s, string key)
        => h.RunAsync(new StartProductionRun(h.CompanyId, s.Supervisor, key, s.Plant, s.Machine, s.Day, s.Today, s.Block), new StartProductionRunHandler());

    private static Task<CommandResult> Record(TestHarness h, Setup s, Guid run, string key, decimal cement = 1850m, decimal sandM3 = 12.5m)
        => h.RunAsync(
            new RecordShiftSummary(h.CompanyId, s.Supervisor, key, s.Plant, run, 10, 1480m, 20m, 0m,
                [new ConsumptionInput(s.Cement, s.Patio, cement, "kg"), new ConsumptionInput(s.Sand, s.Patio, sandM3, "m3"), new ConsumptionInput(s.Additive, s.Patio, 15m, "l")]),
            new RecordShiftSummaryHandler());

    private static string Balance(Guid account) => $"(SELECT coalesce(sum(debit - credit), 0) FROM fin.gl_entry WHERE account_id = '{account}')::numeric(19,2)::text";

    [Fact]
    public async Task A_posted_shift_summary_consumes_at_moving_average_and_receives_the_lot_into_curing_at_standard()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        var run = (await Start(h, s, "start")).ResultRef;

        var duplicate = await Assert.ThrowsAsync<DomainException>(() => Start(h, s, "start-2"));
        var mismatch = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new RecordShiftSummary(h.CompanyId, s.Supervisor, "bad", s.Plant, run, 10, 1480m, 0m, 0m, [new ConsumptionInput(s.Cement, s.Patio, 1850m, "kg")]), new RecordShiftSummaryHandler()));
        await Record(h, s, run, "rec-1", cement: 1800m);
        var summary = JsonDocument.Parse((await Record(h, s, run, "rec-2")).ResultPayload).RootElement;
        var posted = JsonDocument.Parse((await h.RunAsync(new PostShiftSummary(h.CompanyId, s.Manager, "post", s.Plant, run, summary.GetProperty("version").GetInt64()), new PostShiftSummaryHandler())).ResultPayload).RootElement;

        Assert.Equal((ManufacturingErrors.RunExists, ManufacturingErrors.MaterialsMismatch), (duplicate.Code, mismatch.Code));
        // Cement 1,850 of 20,000 kg × 164,000.00 = 15,170.00; sand 12.5 m³ × 1.47 = 18.375 t, FIFO: 10 t (lot of D-3) → 9,990.91 and
        // 8.375 t → 8,367.39 at the area's average (109,900.00 / 110 t); admixture 15 of 200 l → 750.00. Total 34,278.30.
        // Standard: 1,480 × 28.00 = 41,440.00 = materials 1,480 × 22.10 = 32,708.00 + conversion 8,732.00.
        Assert.Equal("POSTED:3:34278.30:41440.00", $"{posted.GetProperty("status").GetString()}:{posted.GetProperty("racks").GetInt32()}:{posted.GetProperty("consumptionValue").GetString()}:{posted.GetProperty("standardValue").GetString()}");
        Assert.Equal($"PT-BLOQUE-6-{s.Today:yyyyMMdd}-DIA", posted.GetProperty("lotCode").GetString());
        Assert.Equal("1570.30:41440.00:-8732.00", await h.ScalarAsync<string>($"SELECT {Balance(s.Wip)} || ':' || {Balance(s.Fg)} || ':' || {Balance(s.Conversion)}"));
        Assert.Equal("10.000000:9990.9100|8.375000:8367.3900", await h.ScalarAsync<string>(
            $"SELECT string_agg(i.qty::text || ':' || i.value::text, '|' ORDER BY l.lot_code) FROM mfg.consumption_issue i JOIN inv.lot l ON l.lot_id = i.lot_id WHERE i.material_item_id = '{s.Sand}'"));
        Assert.Equal("CURADO:1480.000000:41440.0000", await h.ScalarAsync<string>(
            $"SELECT l.code || ':' || b.quantity::text || ':' || v.value::text FROM inv.inv_stock_balance b JOIN md.location l ON l.location_id = b.location_id JOIN inv.inv_valuation_balance v ON v.item_id = b.item_id WHERE b.item_id = '{s.Block}'"));
        Assert.Equal("600.000000|600.000000|280.000000", await h.ScalarAsync<string>("SELECT string_agg(units::text, '|' ORDER BY rack_no) FROM mfg.rack"));
        var (start, _) = BusinessCalendar.DayUtcRange(s.Today);
        Assert.Equal((start.AddHours(19), start.AddHours(43)), (await h.ScalarAsync<DateTime>("SELECT curing_from FROM mfg.fg_lot"), await h.ScalarAsync<DateTime>("SELECT releasable_at FROM mfg.fg_lot")));
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetProductionRun(h.CompanyId, s.Manager, run), new GetProductionRunHandler())).RootElement;
        Assert.Equal("COMPLETED:POSTED:ADITIVO-P 15.000000/15.000000|ARENA-LAVADA 18.375000/18.000000|CEMENTO-GU 1850.000000/1800.000000", string.Join('|',
            new[] { $"{detail.GetProperty("run").GetProperty("status").GetString()}:{detail.GetProperty("summary").GetProperty("status").GetString()}:" }
                .Concat(detail.GetProperty("consumption").EnumerateArray().Select(c => $"{c.GetProperty("materialCode").GetString()} {c.GetProperty("qty").GetString()}/{c.GetProperty("theoreticalQty").GetString()}")))
            .Replace(":|", ":", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Production_needs_a_standard_with_breakdown_enough_stock_and_is_reversed_exactly_while_the_lot_is_in_curing()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var noCost = await SetupAsync(h, withCost: false);
        var missingCost = await Assert.ThrowsAsync<DomainException>(() => Start(h, noCost, "start"));

        await using var h2 = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h2);
        var run = (await Start(h2, s, "start")).ResultRef;
        var shortRecord = JsonDocument.Parse((await Record(h2, s, run, "rec-short", cement: 25000m)).ResultPayload).RootElement;
        var shortStock = await Assert.ThrowsAsync<DomainException>(() => h2.RunAsync(
            new PostShiftSummary(h2.CompanyId, s.Manager, "post-short", s.Plant, run, shortRecord.GetProperty("version").GetInt64()), new PostShiftSummaryHandler()));
        var summary = JsonDocument.Parse((await Record(h2, s, run, "rec")).ResultPayload).RootElement;
        await h2.RunAsync(new PostShiftSummary(h2.CompanyId, s.Manager, "post", s.Plant, run, summary.GetProperty("version").GetInt64()), new PostShiftSummaryHandler());
        var noReason = await Assert.ThrowsAsync<DomainException>(() => h2.RunAsync(new ReverseShiftSummary(h2.CompanyId, s.Manager, "rev-0", s.Plant, run, 3, " "), new ReverseShiftSummaryHandler()));
        var reversed = JsonDocument.Parse((await h2.RunAsync(new ReverseShiftSummary(h2.CompanyId, s.Manager, "rev", s.Plant, run, 3, "Unidades mal contadas"), new ReverseShiftSummaryHandler())).ResultPayload).RootElement;

        Assert.Equal((ManufacturingErrors.StandardCostMissing, InventoryErrors.InsufficientStock, ManufacturingErrors.ReasonRequired), (missingCost.Code, shortStock.Code, noReason.Code));
        Assert.Equal("REVERSED:IN_PROGRESS", $"{reversed.GetProperty("status").GetString()}:{reversed.GetProperty("runStatus").GetString()}");
        Assert.Equal("0.00:0.00:0.00", await h2.ScalarAsync<string>($"SELECT {Balance(s.Wip)} || ':' || {Balance(s.Fg)} || ':' || {Balance(s.Conversion)}"));
        Assert.Equal("20000.000000:164000.0000|110.000000:109900.0000|200.000000:10000.0000", await h2.ScalarAsync<string>(
            $"SELECT string_agg(quantity::text || ':' || value::text, '|' ORDER BY value DESC) FROM inv.inv_valuation_balance WHERE item_id IN ('{s.Cement}', '{s.Sand}', '{s.Additive}')"));
        Assert.Equal("0.000000", await h2.ScalarAsync<string>($"SELECT coalesce(sum(quantity), 0)::numeric(18,6)::text FROM inv.inv_stock_balance WHERE item_id = '{s.Block}'"));
        Assert.Equal("REVERSED,DRAFT:VOIDED", await h2.ScalarAsync<string>(
            "SELECT (SELECT string_agg(status, ',' ORDER BY version DESC) FROM mfg.shift_summary) || ':' || (SELECT string_agg(DISTINCT status, ',') FROM mfg.fg_lot)"));
        var draft = JsonDocument.Parse(await h2.QueryAsync(new GetProductionRun(h2.CompanyId, s.Manager, run), new GetProductionRunHandler())).RootElement.GetProperty("summary");
        await h2.RunAsync(new PostShiftSummary(h2.CompanyId, s.Manager, "repost", s.Plant, run, draft.GetProperty("version").GetInt64()), new PostShiftSummaryHandler());
        Assert.Equal($"PT-BLOQUE-6-{s.Today:yyyyMMdd}-DIA,PT-BLOQUE-6-{s.Today:yyyyMMdd}-DIA-2", await h2.ScalarAsync<string>(
            $"SELECT string_agg(lot_code, ',' ORDER BY lot_code) FROM inv.lot WHERE item_id = '{s.Block}'"));
    }
}
