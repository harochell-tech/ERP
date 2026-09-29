using System.Text;
using System.Text.Json;
using Rochell.Manufacturing.Machines;
using Rochell.Manufacturing.Queries;
using Rochell.Manufacturing.Recipes;
using Rochell.Manufacturing.Shifts;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Sales;
using Rochell.Sales.Opening;
using Rochell.Sales.Pricing;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Manufacturing.Tests;

/// <summary>MFG1-02: machines, shifts, recipes, the standard cost from a recipe and the revaluation (E-MFG1-02-1…10).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ProductionMasterTests(PostgresFixture postgres)
{
    private const string OpenInv = "0192f001-0000-7000-8000-000000000011";
    private const string Reval = "0192f001-0000-7000-8000-000000000023";

    private sealed record Setup(Guid Plant, Guid OtherPlant, Guid Area, Guid Block, Guid Cement, Guid Sand, Guid Additive, Guid Manager, Guid Supervisor, Guid Controller, Guid Approver);

    private static async Task<Setup> SetupAsync(TestHarness h)
    {
        var plant = await h.CreatePlantAsync(code: "HIGUEY");
        var other = await h.CreatePlantAsync(code: "BAVARO");
        await h.CreateLocationAsync(plant, "PATIO");
        var area = await h.ScalarAsync<Guid>("SELECT valuation_area_id FROM md.plant WHERE plant_id = @p", ("p", plant));
        var block = Guid.CreateVersion7();
        await h.AdminRequireAsync($"INSERT INTO md.item VALUES ('{block}', '{h.CompanyId}', 'BLOQUE-6', 'Bloque de 6 pulgadas', 'FINISHED_GOOD', 'un', 'BLOQUE', 'ACTIVE', 1)");
        return new Setup(
            plant, other, area, block,
            await h.CreateActiveItemAsync("CEMENTO-GU", "kg", "CEMENTO"),
            await h.CreateActiveItemAsync("ARENA", "t", "AGREGADO"),
            await h.CreateActiveItemAsync("ADITIVO-P", "l", "ADITIVO"),
            await h.SessionWithRolesAsync("GERENTE_PLANTA"),
            await h.SessionWithRolesAsync("SUPERVISOR_PRODUCCION"),
            await h.SessionWithRolesAsync("CONTROLLER"),
            await h.SessionWithRolesAsync("APROBADOR_POLITICAS"));
    }

    private static JsonElement Json(CommandResult result) => JsonDocument.Parse(result.ResultPayload).RootElement;

    private static async Task<Guid> MachineAsync(TestHarness h, Setup s)
        => (await h.RunAsync(new CreateMachine(h.CompanyId, s.Manager, "m", s.Plant, "besser-1", "Besser V3-12"), new CreateMachineHandler())).ResultRef;

    private static Task<CommandResult> PrepareBlock(TestHarness h, Setup s, Guid machine, string key, Guid? session = null, Guid? item = null)
        => h.RunAsync(
            new PrepareRecipe(h.CompanyId, session ?? s.Supervisor, key, s.Plant, item ?? s.Block, machine, 150m, 6m, 600m, 24, 168,
                [new RecipeLineInput(s.Cement, 180m), new RecipeLineInput(s.Sand, 1.8m), new RecipeLineInput(s.Additive, 1.5m)]),
            new PrepareRecipeHandler());

    [Fact]
    public async Task The_plant_manager_registers_machines_and_shifts_of_a_plant_with_optimistic_versions()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);

        var machine = await MachineAsync(h, s);
        var duplicate = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new CreateMachine(h.CompanyId, s.Manager, "m2", s.Plant, "BESSER-1", "Otra"), new CreateMachineHandler()));
        var supervisor = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new CreateMachine(h.CompanyId, s.Supervisor, "m3", s.Plant, "BESSER-2", "Otra"), new CreateMachineHandler()));
        var otherPlant = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RenameMachine(h.CompanyId, s.Manager, "r0", s.OtherPlant, machine, 1, "X"), new RenameMachineHandler()));
        await h.RunAsync(new RenameMachine(h.CompanyId, s.Manager, "r1", s.Plant, machine, 1, "Besser V3-12 línea 1"), new RenameMachineHandler());
        var stale = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SetMachineStatus(h.CompanyId, s.Manager, "s0", s.Plant, machine, 1, "INACTIVE"), new SetMachineStatusHandler()));
        await h.RunAsync(new SetMachineStatus(h.CompanyId, s.Manager, "s1", s.Plant, machine, 2, "INACTIVE"), new SetMachineStatusHandler());
        var night = (await h.RunAsync(new DefineShift(h.CompanyId, s.Manager, "n", s.Plant, "noche", new TimeOnly(19, 0), new TimeOnly(7, 0)), new DefineShiftHandler())).ResultRef;
        await h.RunAsync(new DefineShift(h.CompanyId, s.Manager, "d", s.Plant, "DIA", new TimeOnly(7, 0), new TimeOnly(19, 0)), new DefineShiftHandler());
        var sameTimes = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new UpdateShiftTimes(h.CompanyId, s.Manager, "u0", s.Plant, night, 1, new TimeOnly(6, 0), new TimeOnly(6, 0)), new UpdateShiftTimesHandler()));
        await h.RunAsync(new UpdateShiftTimes(h.CompanyId, s.Manager, "u1", s.Plant, night, 1, new TimeOnly(19, 0), new TimeOnly(6, 30)), new UpdateShiftTimesHandler());

        Assert.Equal(
            (ManufacturingErrors.CodeDuplicate, AuthorizationErrors.NotAuthorized, ManufacturingErrors.PlantMismatch, ManufacturingErrors.VersionConflict, ManufacturingErrors.FieldInvalid),
            (duplicate.Code, supervisor.Code, otherPlant.Code, stale.Code, sameTimes.Code));
        var machines = JsonDocument.Parse(await h.QueryAsync(new ListMachines(h.CompanyId, s.Supervisor), new ListMachinesHandler())).RootElement.GetProperty("items");
        Assert.Equal("HIGUEY:BESSER-1:Besser V3-12 línea 1:INACTIVE:3", string.Join('|', machines.EnumerateArray().Select(m =>
            $"{m.GetProperty("plantCode").GetString()}:{m.GetProperty("code").GetString()}:{m.GetProperty("name").GetString()}:{m.GetProperty("status").GetString()}:{m.GetProperty("version").GetInt64()}")));
        var shifts = JsonDocument.Parse(await h.QueryAsync(new ListShifts(h.CompanyId, s.Supervisor, s.Plant), new ListShiftsHandler())).RootElement.GetProperty("items");
        Assert.Equal("DIA:07:00-19:00:False|NOCHE:19:00-06:30:True", string.Join('|', shifts.EnumerateArray().Select(x =>
            $"{x.GetProperty("code").GetString()}:{x.GetProperty("startsAt").GetString()}-{x.GetProperty("endsAt").GetString()}:{x.GetProperty("crossesMidnight").GetBoolean()}")));
    }

    [Fact]
    public async Task A_recipe_is_prepared_by_the_supervisor_and_approved_by_the_plant_manager_replacing_the_active_one()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        var machine = await MachineAsync(h, s);

        var rawAsProduct = await Assert.ThrowsAsync<DomainException>(() => PrepareBlock(h, s, machine, "raw", item: s.Cement));
        var repeated = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareRecipe(h.CompanyId, s.Supervisor, "dup", s.Plant, s.Block, machine, 150m, 6m, 600m, 24, 168, [new RecipeLineInput(s.Cement, 1m), new RecipeLineInput(s.Cement, 2m)]),
            new PrepareRecipeHandler()));
        var curing = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareRecipe(h.CompanyId, s.Supervisor, "cur", s.Plant, s.Block, machine, 150m, 6m, 600m, 48, 24, [new RecipeLineInput(s.Cement, 1m)]),
            new PrepareRecipeHandler()));
        var v1 = (await PrepareBlock(h, s, machine, "v1")).ResultRef;
        var managerPrepares = await Assert.ThrowsAsync<DomainException>(() => PrepareBlock(h, s, machine, "mgr", s.Manager));
        await h.RunAsync(new ApproveRecipe(h.CompanyId, s.Manager, "a1", s.Plant, v1), new ApproveRecipeHandler());
        var again = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveRecipe(h.CompanyId, s.Manager, "a1b", s.Plant, v1), new ApproveRecipeHandler()));
        var v2 = (await PrepareBlock(h, s, machine, "v2")).ResultRef;
        await h.RunAsync(new ApproveRecipe(h.CompanyId, s.Manager, "a2", s.Plant, v2), new ApproveRecipeHandler());
        await h.RunAsync(new SetMachineStatus(h.CompanyId, s.Manager, "off", s.Plant, machine, 1, "INACTIVE"), new SetMachineStatusHandler());
        var inactive = await Assert.ThrowsAsync<DomainException>(() => PrepareBlock(h, s, machine, "v3"));

        Assert.Equal(
            (ManufacturingErrors.NotFinishedGood, ManufacturingErrors.DuplicateLine, ManufacturingErrors.FieldInvalid, AuthorizationErrors.NotAuthorized, ManufacturingErrors.InvalidState, ManufacturingErrors.MachineNotActive),
            (rawAsProduct.Code, repeated.Code, curing.Code, managerPrepares.Code, again.Code, inactive.Code));
        var recipes = JsonDocument.Parse(await h.QueryAsync(new ListRecipes(h.CompanyId, s.Manager, s.Plant), new ListRecipesHandler())).RootElement.GetProperty("items");
        Assert.Equal("2:ACTIVE|1:SUPERSEDED", string.Join('|', recipes.EnumerateArray().Select(r => $"{r.GetProperty("version").GetInt32()}:{r.GetProperty("status").GetString()}")));
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetRecipe(h.CompanyId, s.Manager, v2), new GetRecipeHandler())).RootElement;
        Assert.Equal("ADITIVO-P:1.500000:l|ARENA:1.800000:t|CEMENTO-GU:180.000000:kg", string.Join('|', detail.GetProperty("lines").EnumerateArray().Select(l =>
            $"{l.GetProperty("materialCode").GetString()}:{l.GetProperty("qtyPerBatch").GetString()}:{l.GetProperty("baseUom").GetString()}")));
    }

    [Fact]
    public async Task The_standard_cost_from_the_recipe_breaks_down_materials_and_conversion()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        var machine = await MachineAsync(h, s);
        var recipe = (await PrepareBlock(h, s, machine, "v1")).ResultRef;
        MaterialPriceInput[] prices = [new(s.Cement, 8.00m), new(s.Sand, 1000.00m), new(s.Additive, 50.00m)];

        var draftRecipe = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new PrepareStandardCostFromRecipe(h.CompanyId, s.Controller, "c0", recipe, prices, 5.90m), new PrepareStandardCostFromRecipeHandler()));
        await h.RunAsync(new ApproveRecipe(h.CompanyId, s.Manager, "a1", s.Plant, recipe), new ApproveRecipeHandler());
        var missing = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareStandardCostFromRecipe(h.CompanyId, s.Controller, "c1", recipe, [new(s.Cement, 8.00m), new(s.Sand, 1000.00m)], 5.90m), new PrepareStandardCostFromRecipeHandler()));
        // 180 ÷ 150 = 1.2 kg × 8.00 + 1.8 ÷ 150 = 0.012 t × 1,000.00 + 1.5 ÷ 150 = 0.01 l × 50.00 = 22.10; + 5.90 = 28.00 (baseline §6).
        var cost = Json(await h.RunAsync(new PrepareStandardCostFromRecipe(h.CompanyId, s.Controller, "c2", recipe, prices, 5.90m), new PrepareStandardCostFromRecipeHandler()));

        Assert.Equal((SalesErrors.RecipeNotActive, SalesErrors.MaterialPricesMismatch), (draftRecipe.Code, missing.Code));
        Assert.Equal("28.0000:22.1000:5.9000", $"{cost.GetProperty("unitCost").GetString()}:{cost.GetProperty("materialCost").GetString()}:{cost.GetProperty("conversionCost").GetString()}");
        Assert.Equal("0.012000×1000.0000|0.010000×50.0000|1.200000×8.0000", await h.ScalarAsync<string>(
            "SELECT string_agg(m.std_qty_per_unit::text || '×' || m.std_price::text, '|' ORDER BY m.std_price DESC) FROM md.standard_cost_material m WHERE m.cost_version_id = @id",
            ("id", cost.GetProperty("costVersionId").GetGuid())));
        Assert.Equal(s.Area, await h.ScalarAsync<Guid>("SELECT valuation_area_id FROM md.standard_cost_version WHERE cost_version_id = @id", ("id", cost.GetProperty("costVersionId").GetGuid())));
    }

    [Fact]
    public async Task Approving_a_new_standard_with_stock_revalues_it_with_REVAL_unless_units_are_in_transit()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        var today = BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);
        for (var y = today.Year - 1; y <= today.Year + 1; y++)
        {
            await h.OpenPeriodsAsync(y);
        }

        var fg = await h.CreateAccountAsync("1350", "Inventario de producto terminado", isControl: true);
        var revaluation = await h.CreateAccountAsync("5190", "Revaluación de existencias", isControl: false);
        await h.CreateActiveMapAsync("FINISHED_GOODS", fg);
        await h.CreateActiveMapAsync("MIGRATION_CLEARING", await h.CreateAccountAsync("3990", "Contrapartida de migración", isControl: false));
        await h.CreateActiveMapAsync("STANDARD_REVALUATION", revaluation);
        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '{OpenInv}' AND version = 1");
        var first = (await h.RunAsync(new PrepareStandardCost(h.CompanyId, s.Controller, "c1", s.Block, s.Area, 28.00m), new PrepareStandardCostHandler())).ResultRef;
        await h.RunAsync(new ApproveStandardCost(h.CompanyId, s.Approver, "a1", first), new ApproveStandardCostHandler());
        var csv = Convert.ToBase64String(Encoding.UTF8.GetBytes("planta,ubicacion,producto,cantidad,documento\nHIGUEY,PATIO,BLOQUE-6,100.5,ADM-1\n"));
        var batch = (await h.RunAsync(new PrepareOpeningInventory(h.CompanyId, s.Controller, "o", "apertura.csv", csv, new DateOnly(today.Year, today.Month, 1)), new PrepareOpeningInventoryHandler())).ResultRef;
        await h.RunAsync(new PostOpeningInventory(h.CompanyId, s.Approver, "op", batch, 1), new PostOpeningInventoryHandler());

        var second = (await h.RunAsync(new PrepareStandardCost(h.CompanyId, s.Controller, "c2", s.Block, s.Area, 30.1234m), new PrepareStandardCostHandler())).ResultRef;
        var ruleDraft = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveStandardCost(h.CompanyId, s.Approver, "a2", second), new ApproveStandardCostHandler()));
        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '{Reval}' AND version = 1");
        // 100.5 × 30.1234 = 3,027.4017 → 3,027.40; before 100.5 × 28.00 = 2,814.00; revaluation +213.40.
        var approved = Json(await h.RunAsync(new ApproveStandardCost(h.CompanyId, s.Approver, "a3", second), new ApproveStandardCostHandler()));
        var third = (await h.RunAsync(new PrepareStandardCost(h.CompanyId, s.Controller, "c3", s.Block, s.Area, 27.00m), new PrepareStandardCostHandler())).ResultRef;
        var transit = Guid.CreateVersion7();
        var lot = await h.ScalarAsync<Guid>("SELECT lot_id FROM inv.lot LIMIT 1");
        await h.AdminRequireAsync(
            $"""
            INSERT INTO md.location (location_id, company_id, plant_id, code, is_transit) VALUES ('{transit}', '{h.CompanyId}', '{s.Plant}', 'TRANSITO', true);
            BEGIN; SET LOCAL session_replication_role = replica;
            INSERT INTO inv.inv_stock_balance VALUES ('{h.CompanyId}', '{s.Plant}', '{transit}', '{s.Block}', '{lot}', 5);
            COMMIT;
            """);
        var inTransit = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveStandardCost(h.CompanyId, s.Approver, "a4", third), new ApproveStandardCostHandler()));

        Assert.Equal((Finance.FinanceErrors.PostingPrerequisiteMissing, SalesErrors.InTransitExists), (ruleDraft.Code, inTransit.Code));
        Assert.Equal("213.40", approved.GetProperty("revaluation").GetString());
        Assert.Equal("100.500000:3027.4000", await h.ScalarAsync<string>($"SELECT quantity::text || ':' || value::text FROM inv.inv_valuation_balance WHERE item_id = '{s.Block}'"));
        Assert.Equal("3027.40:-213.40", await h.ScalarAsync<string>(
            $"SELECT (SELECT sum(debit - credit) FROM fin.gl_entry WHERE account_id = '{fg}')::numeric(19,2)::text || ':' || (SELECT sum(debit - credit) FROM fin.gl_entry WHERE account_id = '{revaluation}')::numeric(19,2)::text"));
        Assert.Equal("VALUATION_ADJUSTMENT:213.4000", await h.ScalarAsync<string>(
            $"SELECT movement_type::text || ':' || amount::text FROM inv.inv_value_entry WHERE item_id = '{s.Block}' AND movement_type::text = 'VALUATION_ADJUSTMENT'"));
    }
}
