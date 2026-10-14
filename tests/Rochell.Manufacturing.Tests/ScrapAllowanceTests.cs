using System.Text.Json;
using Rochell.Finance.Configuration;
using Rochell.Manufacturing.Runs;
using Rochell.Platform.Commands;
using Rochell.Sales;
using Rochell.Sales.Pricing;
using Rochell.Sales.Queries;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Manufacturing.Tests;

/// <summary>
/// COST-01 (E-COST-1…3): a standard cost from the recipe with a scrap allowance of 1.50 per unit — 22.10 of materials + 5.90 of conversion
/// + 1.50 = 29.50. The lot enters curing at 29.50; P-10 version 2 credits the allowance to SCRAP_ALLOWANCE apart from the conversion.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ScrapAllowanceTests(PostgresFixture postgres)
{
    [Fact]
    public async Task The_scrap_allowance_is_part_of_the_standard_cost_and_is_absorbed_apart_from_the_conversion()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ProductionRunTests.SetupAsync(h, withCost: false);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var approver = await h.SessionWithRolesAsync("APROBADOR_POLITICAS");
        var recipe = await h.ScalarAsync<Guid>("SELECT recipe_version_id FROM mfg.recipe_version WHERE status = 'ACTIVE'");
        IReadOnlyList<MaterialPriceInput> prices = [new(s.Cement, 8.00m), new(s.Sand, 1000.00m), new(s.Additive, 50.00m)];

        var negative = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareStandardCostFromRecipe(h.CompanyId, controller, "c-neg", recipe, prices, 5.90m, -1m), new PrepareStandardCostFromRecipeHandler()));
        var prepared = await h.RunAsync(new PrepareStandardCostFromRecipe(h.CompanyId, controller, "c", recipe, prices, 5.90m, 1.50m), new PrepareStandardCostFromRecipeHandler());
        var cost = JsonDocument.Parse(prepared.ResultPayload).RootElement;
        await h.RunAsync(new ApproveStandardCost(h.CompanyId, approver, "ca", prepared.ResultRef), new ApproveStandardCostHandler());

        Assert.Equal(SalesErrors.AmountInvalid, negative.Code);
        Assert.Equal("29.5000:22.1000:5.9000:1.5000", string.Join(':', new[] { "unitCost", "materialCost", "conversionCost", "scrapAllowance" }.Select(p => cost.GetProperty(p).GetString())));
        var listed = JsonDocument.Parse(await h.QueryAsync(new ListStandardCosts(h.CompanyId, controller), new ListStandardCostsHandler())).RootElement.GetProperty("items")[0];
        Assert.Equal("29.5000:22.1000:5.9000:1.5000", string.Join(':', new[] { "unitCost", "materialCost", "conversionCost", "scrapAllowance" }.Select(p => listed.GetProperty(p).GetString())));

        var run = (await ProductionRunTests.Start(h, s, "start")).ResultRef;
        var summary = JsonDocument.Parse((await ProductionRunTests.Record(h, s, run, "rec")).ResultPayload).RootElement;
        var version = summary.GetProperty("version").GetInt64();

        // P-10 version 1 has no line for the allowance: nothing is posted until version 2 is in force with its account.
        var waiting = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new PostShiftSummary(h.CompanyId, s.Manager, "post-v1", s.Plant, run, version), new PostShiftSummaryHandler()));
        Assert.Equal(Rochell.Finance.FinanceErrors.PostingPrerequisiteMissing, waiting.Code);
        var allowance = await h.CreateAccountAsync("5155", "Merma estándar absorbida", isControl: false);
        await h.CreateActiveMapAsync("SCRAP_ALLOWANCE", allowance);
        await h.RunAsync(new ApprovePostingRuleVersion(h.CompanyId, controller, "p10-v2", "P-10", 2), new ApprovePostingRuleVersionHandler());
        var posted = JsonDocument.Parse((await h.RunAsync(new PostShiftSummary(h.CompanyId, s.Manager, "post", s.Plant, run, version), new PostShiftSummaryHandler())).ResultPayload).RootElement;

        // Standard: 1,480 × 29.50 = 43,660.00 = materials 1,480 × 22.10 = 32,708.00 + allowance 1,480 × 1.50 = 2,220.00 + conversion 8,732.00.
        // WIP keeps the consumption at moving average less the materials at standard: 34,278.30 − 32,708.00 = 1,570.30.
        Assert.Equal("POSTED:43660.00", $"{posted.GetProperty("status").GetString()}:{posted.GetProperty("standardValue").GetString()}");
        Assert.Equal("1570.30:43660.00:-8732.00:-2220.00", await h.ScalarAsync<string>(
            $"SELECT {ProductionRunTests.Balance(s.Wip)} || ':' || {ProductionRunTests.Balance(s.Fg)} || ':' || {ProductionRunTests.Balance(s.Conversion)} || ':' || {ProductionRunTests.Balance(allowance)}"));
    }

    [Fact]
    public async Task A_cost_written_by_hand_has_no_breakdown_and_no_allowance()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ProductionRunTests.SetupAsync(h, withCost: false);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var area = await h.ScalarAsync<Guid>($"SELECT valuation_area_id FROM md.plant WHERE plant_id = '{s.Plant}'");
        await h.RunAsync(new PrepareStandardCost(h.CompanyId, controller, "manual", s.Block, area, 30.00m), new PrepareStandardCostHandler());

        var listed = JsonDocument.Parse(await h.QueryAsync(new ListStandardCosts(h.CompanyId, controller), new ListStandardCostsHandler())).RootElement.GetProperty("items")[0];
        Assert.Equal("30.0000:Null:Null:Null", $"{listed.GetProperty("unitCost").GetString()}:{listed.GetProperty("materialCost").ValueKind}:{listed.GetProperty("conversionCost").ValueKind}:{listed.GetProperty("scrapAllowance").ValueKind}");
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM md.standard_cost_version WHERE material_cost IS NULL AND scrap_allowance = 0"));
    }
}
