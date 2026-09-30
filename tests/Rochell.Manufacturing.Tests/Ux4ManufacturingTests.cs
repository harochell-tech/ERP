using System.Text.Json;
using Rochell.Manufacturing.Queries;
using Rochell.Manufacturing.Recipes;
using Rochell.Manufacturing.Runs;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Manufacturing.Tests;

/// <summary>UX4-01: the run's own recipe version and a positive curing minimum (E-UX4-9); variance, day totals and curing hours (E-UX4-2).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class Ux4ManufacturingTests(PostgresFixture postgres)
{
    private static async Task<JsonElement> Detail(TestHarness h, ProductionRunTests.Setup s, Guid run)
        => JsonDocument.Parse(await h.QueryAsync(new GetProductionRun(h.CompanyId, s.Manager, run), new GetProductionRunHandler())).RootElement;

    [Fact]
    public async Task A_new_recipe_cures_at_least_one_hour()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ProductionRunTests.SetupAsync(h, withCost: false);
        PrepareRecipe Recipe(string key, int minimum) => new(h.CompanyId, s.Supervisor, key, s.Plant, s.Block, s.Machine, 150m, 6m, 600m, minimum, 168, [new RecipeLineInput(s.Cement, 180m)]);

        var zero = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Recipe("r0", 0), new PrepareRecipeHandler()));
        await h.RunAsync(Recipe("r1", 1), new PrepareRecipeHandler());
        var bypass = await h.AppExecuteAsync("UPDATE mfg.recipe_version SET min_curing_hours = 0 WHERE status = 'DRAFT'");

        Assert.Equal(ManufacturingErrors.FieldInvalid, zero.Code);
        Assert.Equal("23514", bypass?.SqlState); // recipe_min_curing_positive
    }

    [Fact]
    public async Task A_run_keeps_the_recipe_version_it_started_with_after_a_new_one_is_approved()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ProductionRunTests.SetupAsync(h); // v1: cement 180 kg, sand 1.8 t, admixture 1.5 l per batch; curing 24–168 h
        var run = (await ProductionRunTests.Start(h, s, "start")).ResultRef;
        var v1 = await h.ScalarAsync<Guid>("SELECT recipe_version_id FROM mfg.production_run WHERE run_id = @r", ("r", run));
        var v2 = (await h.RunAsync(
            new PrepareRecipe(h.CompanyId, s.Supervisor, "v2", s.Plant, s.Block, s.Machine, 150m, 6m, 600m, 48, 168,
                [new RecipeLineInput(s.Cement, 200m), new RecipeLineInput(s.Sand, 2m), new RecipeLineInput(s.Additive, 1m)]),
            new PrepareRecipeHandler())).ResultRef;
        await h.RunAsync(new ApproveRecipe(h.CompanyId, s.Manager, "v2-a", s.Plant, v2), new ApproveRecipeHandler());

        var summary = JsonDocument.Parse((await ProductionRunTests.Record(h, s, run, "rec")).ResultPayload).RootElement; // 10 batches
        await h.RunAsync(new PostShiftSummary(h.CompanyId, s.Manager, "post", s.Plant, run, summary.GetProperty("version").GetInt64()), new PostShiftSummaryHandler());
        var detail = await Detail(h, s, run);

        // Theoretical from v1 × 10 batches (not v2's 2 000 / 20 / 10), and the lot cures v1's 24 hours (not 48).
        Assert.Equal((v1, 1), (detail.GetProperty("recipeVersionId").GetGuid(), detail.GetProperty("recipeVersion").GetInt32()));
        Assert.Equal("ADITIVO-P 1.5/15.000000|ARENA-LAVADA 1.8/18.000000|CEMENTO-GU 180/1800.000000", string.Join('|', detail.GetProperty("consumption").EnumerateArray().Select(c =>
            $"{c.GetProperty("materialCode").GetString()} {decimal.Parse(c.GetProperty("qtyPerBatch").GetString()!, System.Globalization.CultureInfo.InvariantCulture):0.######}/{c.GetProperty("theoreticalQty").GetString()}")));
        Assert.Equal(24.0, await h.ScalarAsync<double>("SELECT extract(epoch FROM releasable_at - curing_from)::float8 / 3600 FROM mfg.fg_lot"));
        Assert.Equal("ACTIVE:2|SUPERSEDED:1", await h.ScalarAsync<string>("SELECT string_agg(status || ':' || version, '|' ORDER BY version DESC) FROM mfg.recipe_version"));
    }

    [Fact]
    public async Task Consumption_variance_carries_its_sign_percentage_and_the_PRODUCTION_tolerance_and_the_day_totals_scrap()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ProductionRunTests.SetupAsync(h);
        var run = (await ProductionRunTests.Start(h, s, "start")).ResultRef;
        await ProductionRunTests.Record(h, s, run, "rec"); // 10 batches, 1 480 good, 20 mix scrap; cement 1 850 kg, sand 12.5 m³ = 18.375 t, admixture 15 l

        var withoutPolicy = await Detail(h, s, run);
        await h.CreateActivePolicyAsync("PRODUCTION", new Dictionary<string, string> { ["usage_tolerance_pct"] = "0.025" }, s.Today.AddDays(-1));
        var withPolicy = await Detail(h, s, run);
        var day = JsonDocument.Parse(await h.QueryAsync(new GetProductionDay(h.CompanyId, s.Manager, s.Plant, s.Today), new GetProductionDayHandler())).RootElement;

        static string Variance(JsonElement e, string list) => string.Join('|', e.GetProperty(list).EnumerateArray().Select(c =>
            $"{c.GetProperty("materialCode").GetString()} {c.GetProperty("difference").GetString()} {c.GetProperty("differencePct").GetString()} {c.GetProperty("outOfTolerance")}"));

        // Cement +50 of 1 800 = +2.78 % > 2.5 % (out); sand +0.375 of 18 = +2.08 % (in); admixture 0 of 15 = 0.00 % (in).
        Assert.Equal(JsonValueKind.Null, withoutPolicy.GetProperty("usageTolerancePct").ValueKind);
        Assert.All(withoutPolicy.GetProperty("consumption").EnumerateArray(), c => Assert.Equal(JsonValueKind.Null, c.GetProperty("outOfTolerance").ValueKind));
        Assert.Equal("ADITIVO-P 0.000000 0.00 False|ARENA-LAVADA 0.375000 2.08 False|CEMENTO-GU 50.000000 2.78 True", Variance(withPolicy, "consumption"));
        Assert.Equal("0.025", withPolicy.GetProperty("usageTolerancePct").GetString());
        Assert.Equal("ADITIVO-P 0.000000 0.00 False|ARENA-LAVADA 0.375000 2.08 False|CEMENTO-GU 50.000000 2.78 True", Variance(day, "materials"));
        Assert.Equal(("1480", "20", "0", "20"), (Units(day, "goodUnits"), Units(day, "mixScrapUnits"), Units(day, "freshScrapUnits"), Units(day, "scrapUnits")));

        static string Units(JsonElement e, string property) => decimal.Parse(e.GetProperty(property).GetString()!, System.Globalization.CultureInfo.InvariantCulture).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task A_lot_in_curing_shows_the_hours_left_rounded_up_and_zero_once_releasable()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ProductionRunTests.SetupAsync(h);
        var run = (await ProductionRunTests.Start(h, s, "start")).ResultRef;
        var summary = JsonDocument.Parse((await ProductionRunTests.Record(h, s, run, "rec")).ResultPayload).RootElement;
        await h.RunAsync(new PostShiftSummary(h.CompanyId, s.Manager, "post", s.Plant, run, summary.GetProperty("version").GetInt64()), new PostShiftSummaryHandler());

        async Task<(int List, int Detail)> HoursLeft(string interval)
        {
            await h.AdminRequireAsync(
                $"BEGIN; SET LOCAL session_replication_role = replica; UPDATE mfg.fg_lot SET curing_from = now() - interval '1 day', releasable_at = now() + interval '{interval}'; COMMIT;");
            var list = JsonDocument.Parse(await h.QueryAsync(new ListFgLots(h.CompanyId, s.Manager), new ListFgLotsHandler())).RootElement.GetProperty("items")[0];
            return (list.GetProperty("curingHoursRemaining").GetInt32(), (await Detail(h, s, run)).GetProperty("lot").GetProperty("curingHoursRemaining").GetInt32());
        }

        Assert.Equal((6, 6), await HoursLeft("5 hours 10 minutes"));
        Assert.Equal((0, 0), await HoursLeft("-1 hour"));
    }
}
