using Npgsql;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.MasterData.Tests;

/// <summary>
/// MFG1-01 schema guarantees (Frozen Baseline MFG-1 §2, §7; E-MFG1-01-1…13), written as the application role through a fixture
/// command (the commands arrive in MFG1-02).
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ManufacturingSchemaTests(PostgresFixture postgres)
{
    private static Task Run(TestHarness h, string key, string sql, params TestState[] states)
        => h.RunAsync(new TestTreasurySql(h.CompanyId, h.SessionId, key, sql, states), new TestTreasurySqlHandler());

    private static async Task<string?> Fails(TestHarness h, string key, string sql, params TestState[] states)
    {
        var ex = await Record.ExceptionAsync(() => Run(h, key, sql, states));
        return ex is PostgresException pg ? pg.SqlState : ex?.GetType().Name;
    }

    private static async Task<Guid> FinishedGoodAsync(TestHarness h)
    {
        var id = Guid.CreateVersion7();
        await h.AdminRequireAsync($"INSERT INTO md.item VALUES ('{id}', '{h.CompanyId}', 'BLOQUE-6', 'Bloque de 6 pulgadas', 'FINISHED_GOOD', 'un', 'BLOQUE', 'ACTIVE', 1)");
        return id;
    }

    private static async Task<(Guid Plant, Guid Machine)> MachineAsync(TestHarness h)
    {
        var plant = await h.CreatePlantAsync();
        var machine = Guid.CreateVersion7();
        await Run(h, "machine", $"INSERT INTO md.machine VALUES ('{machine}', '{h.CompanyId}', '{plant}', 'BESSER-1', 'Besser V3-12', 'ACTIVE', 1)",
            new TestState("Machine", machine, null, "ACTIVE"));
        return (plant, machine);
    }

    [Fact]
    public async Task Machines_and_shifts_are_registered_active_keep_their_code_and_are_never_deleted()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (plant, machine) = await MachineAsync(h);
        var night = Guid.CreateVersion7();

        await Run(h, "night", $"INSERT INTO mfg.shift VALUES ('{night}', '{h.CompanyId}', '{plant}', 'NOCHE', '19:00', '07:00', 'ACTIVE', 1)",
            new TestState("Shift", night, null, "ACTIVE"));
        var recode = await Fails(h, "recode", $"UPDATE md.machine SET code = 'OTRA', version = 2 WHERE machine_id = '{machine}'");
        var skip = await Fails(h, "skip", $"UPDATE mfg.shift SET ends_at = '06:00', version = 3 WHERE shift_id = '{night}'");
        var delete = await Fails(h, "delete", $"DELETE FROM md.machine WHERE machine_id = '{machine}'");
        var noHistory = await Fails(h, "inactive", $"UPDATE mfg.shift SET status = 'INACTIVE', version = 2 WHERE shift_id = '{night}'");
        var sameTimes = await Fails(h, "same", $"INSERT INTO mfg.shift VALUES ('{Guid.CreateVersion7()}', '{h.CompanyId}', '{plant}', 'DIA', '07:00', '07:00', 'ACTIVE', 1)");
        await Run(h, "rename", $"UPDATE md.machine SET name = 'Besser V3-12 línea 1', status = 'INACTIVE', version = 2 WHERE machine_id = '{machine}'",
            new TestState("Machine", machine, "ACTIVE", "INACTIVE"));

        Assert.Equal(("42501", "P0001", "42501", "P0001", "23514"), (recode, skip, delete, noHistory, sameTimes)); // code: no UPDATE grant
        Assert.Equal("INACTIVE:2", await h.ScalarAsync<string>($"SELECT status || ':' || version FROM md.machine WHERE machine_id = '{machine}'"));
    }

    [Fact]
    public async Task Recipes_are_of_finished_goods_with_raw_material_lines_approved_by_another_person_and_one_is_active()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (_, machine) = await MachineAsync(h);
        var block = await FinishedGoodAsync(h);
        var cement = await h.CreateActiveItemAsync("CEMENTO-GU", "kg", "CEMENTO");
        var approver = await h.CreateUserAsync();
        var v1 = Guid.CreateVersion7();
        var v2 = Guid.CreateVersion7();
        string Recipe(Guid id, Guid item, int version) =>
            $"INSERT INTO mfg.recipe_version VALUES ('{id}', '{h.CompanyId}', '{item}', '{machine}', {version}, current_date, 150, 6, 600, 24, 168, 'DRAFT', @user, NULL)";
        string Line(Guid recipe, Guid item) => $"INSERT INTO mfg.recipe_line VALUES ('{recipe}', '{h.CompanyId}', '{item}', 350)";

        var rawRecipe = await Fails(h, "raw", Recipe(Guid.CreateVersion7(), cement, 1), new TestState("Recipe", Guid.Empty, null, "DRAFT"));
        await Run(h, "v1", Recipe(v1, block, 1) + "; " + Line(v1, cement), new TestState("Recipe", v1, null, "DRAFT"));
        var goodAsMaterial = await Fails(h, "fg-line", Line(v1, block));
        var self = await Fails(h, "self", $"UPDATE mfg.recipe_version SET status = 'ACTIVE', approved_by = @user WHERE recipe_version_id = '{v1}'",
            new TestState("Recipe", v1, "DRAFT", "ACTIVE"));
        await Run(h, "approve", $"UPDATE mfg.recipe_version SET status = 'ACTIVE', approved_by = '{approver}' WHERE recipe_version_id = '{v1}'",
            new TestState("Recipe", v1, "DRAFT", "ACTIVE"));
        var lineAfter = await Fails(h, "late-line", $"INSERT INTO mfg.recipe_line VALUES ('{v1}', '{h.CompanyId}', '{await h.CreateActiveItemAsync("ADITIVO-X", "l", "ADITIVO")}', 1)");
        var changeApproved = await Fails(h, "change", $"UPDATE mfg.recipe_version SET units_per_rack = 500 WHERE recipe_version_id = '{v1}'");
        await Run(h, "v2", Recipe(v2, block, 2), new TestState("Recipe", v2, null, "DRAFT"));
        var twoActive = await Fails(h, "two", $"UPDATE mfg.recipe_version SET status = 'ACTIVE', approved_by = '{approver}' WHERE recipe_version_id = '{v2}'",
            new TestState("Recipe", v2, "DRAFT", "ACTIVE"));
        var badCuring = await Fails(h, "curing", $"UPDATE mfg.recipe_version SET max_curing_hours = 24 WHERE recipe_version_id = '{v2}'");

        Assert.Equal(("P0001", "P0001", "23514", "P0001", "P0001", "23505", "23514"), (rawRecipe, goodAsMaterial, self, lineAfter, changeApproved, twoActive, badCuring));
        Assert.Equal("ACTIVE:350.000000", await h.ScalarAsync<string>(
            $"SELECT v.status || ':' || l.qty_per_batch FROM mfg.recipe_version v JOIN mfg.recipe_line l USING (recipe_version_id) WHERE v.recipe_version_id = '{v1}'"));
    }

    [Fact]
    public async Task The_standard_cost_breakdown_adds_up_and_its_material_lines_belong_to_a_draft()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var plant = await h.CreatePlantAsync();
        var area = await h.ScalarAsync<Guid>($"SELECT valuation_area_id FROM md.plant WHERE plant_id = '{plant}'");
        var block = await FinishedGoodAsync(h);
        var cement = await h.CreateActiveItemAsync("CEMENTO-GU", "kg", "CEMENTO");
        var approver = await h.CreateUserAsync();
        var cost = Guid.CreateVersion7();
        string Cost(Guid id, int version, string unit, string material, string conversion) =>
            $"INSERT INTO md.standard_cost_version VALUES ('{id}', '{h.CompanyId}', '{block}', '{area}', {version}, current_date, {unit}, 'DRAFT', @user, NULL, {material}, {conversion})";

        var wrongSum = await Fails(h, "sum", Cost(Guid.CreateVersion7(), 1, "28.00", "22.10", "5.00"), new TestState("StandardCost", Guid.Empty, null, "DRAFT"));
        var halfBreakdown = await Fails(h, "half", Cost(Guid.CreateVersion7(), 1, "28.00", "22.10", "NULL"), new TestState("StandardCost", Guid.Empty, null, "DRAFT"));
        await Run(h, "cost", Cost(cost, 1, "28.00", "22.10", "5.90") + $"; INSERT INTO md.standard_cost_material VALUES ('{cost}', '{h.CompanyId}', '{cement}', 1.2, 8.00)",
            new TestState("StandardCost", cost, null, "DRAFT"));
        var goodAsMaterial = await Fails(h, "fg", $"INSERT INTO md.standard_cost_material VALUES ('{cost}', '{h.CompanyId}', '{block}', 1, 1)");
        await Run(h, "approve", $"UPDATE md.standard_cost_version SET status = 'ACTIVE', approved_by = '{approver}' WHERE cost_version_id = '{cost}'",
            new TestState("StandardCost", cost, "DRAFT", "ACTIVE"));
        var changeApproved = await Fails(h, "change", $"UPDATE md.standard_cost_version SET conversion_cost = 5.80, material_cost = 22.20 WHERE cost_version_id = '{cost}'");
        var lineAfter = await Fails(h, "late", $"INSERT INTO md.standard_cost_material VALUES ('{cost}', '{h.CompanyId}', '{await h.CreateActiveItemAsync("ARENA", "t", "AGREGADO")}', 0.012, 1000)");

        Assert.Equal(("23514", "23514", "P0001", "P0001", "P0001"), (wrongSum, halfBreakdown, goodAsMaterial, changeApproved, lineAfter));
    }

    [Fact]
    public async Task The_curing_location_is_named_curado_and_there_is_one_per_plant()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var plant = await h.CreatePlantAsync();
        string Location(string code) => $"INSERT INTO md.location (location_id, company_id, plant_id, code, is_curing) VALUES ('{Guid.CreateVersion7()}', '{h.CompanyId}', '{plant}', '{code}', true)";

        var wrongCode = await Fails(h, "patio", Location("PATIO"));
        await Run(h, "curado", Location("CURADO"));
        var second = await Fails(h, "second", Location("CURADO"));

        Assert.Equal(("23514", "23505"), (wrongCode, second));
        Assert.Equal(1L, await h.ScalarAsync<long>($"SELECT count(*) FROM md.location WHERE plant_id = '{plant}' AND is_curing"));
    }
}
