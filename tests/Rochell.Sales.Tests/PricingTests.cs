using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Sales.Pricing;
using Rochell.Sales.Queries;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>VS3-02: standard cost and price list (E-VS3-01-3/13/14, E-VS3-02-7/8).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PricingTests(PostgresFixture postgres)
{
    private sealed record Setup(Guid Controller, Guid Approver, Guid Block, Guid Paver, Guid Sand, Guid Area);

    private static async Task<Setup> SetupAsync(TestHarness h)
    {
        var plant = await h.CreatePlantAsync();
        var area = await h.ScalarAsync<Guid>("SELECT valuation_area_id FROM md.plant WHERE plant_id = @p", ("p", plant));
        var block = Guid.CreateVersion7();
        var paver = Guid.CreateVersion7();
        var sand = Guid.CreateVersion7();
        await h.AdminRequireAsync(
            $"""
            INSERT INTO md.item VALUES ('{block}', '{h.CompanyId}', 'BLOQUE-6', 'Bloque de 6 pulgadas', 'FINISHED_GOOD', 'un', 'BLOQUE', 'ACTIVE', 1);
            INSERT INTO md.item VALUES ('{paver}', '{h.CompanyId}', 'ADOQUIN-H', 'Adoquín holandés', 'FINISHED_GOOD', 'un', 'ADOQUIN', 'ACTIVE', 1);
            INSERT INTO md.item VALUES ('{sand}', '{h.CompanyId}', 'ARENA', 'Arena', 'RAW_MATERIAL', 't', 'AGREGADO', 'ACTIVE', 1);
            INSERT INTO md.uom_conversion VALUES ('{h.CompanyId}', '{paver}', 'm3', 'un', 40, current_date - 1, NULL);
            """);
        return new Setup(await h.SessionWithRolesAsync("CONTROLLER"), await h.SessionWithRolesAsync("APROBADOR_POLITICAS"), block, paver, sand, area);
    }

    private static async Task<Guid> Cost(TestHarness h, Setup s, string key, decimal cost)
        => JsonDocument.Parse((await h.RunAsync(new PrepareStandardCost(h.CompanyId, s.Controller, key, s.Block, s.Area, cost), new PrepareStandardCostHandler())).ResultPayload)
            .RootElement.GetProperty("costVersionId").GetGuid();

    [Fact]
    public async Task A_standard_cost_is_prepared_by_the_controller_approved_by_the_policy_approver_and_revalues_stock_only_with_REVAL_active()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);

        var raw = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new PrepareStandardCost(h.CompanyId, s.Controller, "raw", s.Sand, s.Area, 10m), new PrepareStandardCostHandler()));
        var first = await Cost(h, s, "c1", 30.00m);
        var replaced = await Cost(h, s, "c2", 32.7512m);
        var tooPrecise = await Assert.ThrowsAsync<DomainException>(() => Cost(h, s, "c3", 1.00001m));
        var self = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveStandardCost(h.CompanyId, s.Controller, "self", first), new ApproveStandardCostHandler()));
        await h.RunAsync(new ApproveStandardCost(h.CompanyId, s.Approver, "a1", first), new ApproveStandardCostHandler());
        var second = await Cost(h, s, "c4", 35.00m);
        // Stock of the item in the area (the opening document arrives in VS3-02b): a fixture balance, deferred checks off.
        await h.AdminRequireAsync($"BEGIN; SET LOCAL session_replication_role = replica; INSERT INTO inv.inv_valuation_balance VALUES ('{h.CompanyId}', '{s.Area}', '{s.Block}', 100, 3275.12); COMMIT;");
        var frozen = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveStandardCost(h.CompanyId, s.Approver, "a2", second), new ApproveStandardCostHandler()));

        Assert.Equal(first, replaced);
        Assert.Equal(
            (SalesErrors.NotFinishedGood, SalesErrors.AmountInvalid, AuthorizationErrors.NotAuthorized, Finance.FinanceErrors.PostingPrerequisiteMissing), // E-MFG1-02-8: REVAL still DRAFT
            (raw.Code, tooPrecise.Code, self.Code, frozen.Code));
        var costs = JsonDocument.Parse(await h.QueryAsync(new ListStandardCosts(h.CompanyId, s.Controller), new ListStandardCostsHandler())).RootElement.GetProperty("items");
        Assert.Equal("2:DRAFT:35.0000|1:ACTIVE:32.7512", string.Join('|', costs.EnumerateArray().Select(c => $"{c.GetProperty("version").GetInt32()}:{c.GetProperty("status").GetString()}:{c.GetProperty("unitCost").GetString()}")));
    }

    [Fact]
    public async Task A_price_list_takes_finished_goods_in_base_or_convertible_units_and_replaces_the_list_in_force()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);

        Task<CommandResult> Prepare(string key, params PriceListLine[] lines) => h.RunAsync(new PreparePriceList(h.CompanyId, s.Controller, key, lines), new PreparePriceListHandler());

        var raw = await Assert.ThrowsAsync<DomainException>(() => Prepare("raw", new PriceListLine(s.Sand, "t", 900m)));
        var noConversion = await Assert.ThrowsAsync<DomainException>(() => Prepare("uom", new PriceListLine(s.Block, "m3", 900m)));
        var duplicate = await Assert.ThrowsAsync<DomainException>(() => Prepare("dup", new PriceListLine(s.Block, "un", 45m), new PriceListLine(s.Block, "un", 46m)));
        var empty = await Assert.ThrowsAsync<DomainException>(() => Prepare("empty"));
        var v1 = (await Prepare("v1", new PriceListLine(s.Block, "un", 45.00m), new PriceListLine(s.Paver, "m3", 1850.00m))).ResultRef;
        await h.RunAsync(new ApprovePriceList(h.CompanyId, s.Approver, "a1", v1), new ApprovePriceListHandler());
        var v2 = (await Prepare("v2", new PriceListLine(s.Block, "un", 47.50m))).ResultRef;
        var controllerApproves = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApprovePriceList(h.CompanyId, s.Controller, "self", v2), new ApprovePriceListHandler()));
        await h.RunAsync(new ApprovePriceList(h.CompanyId, s.Approver, "a2", v2), new ApprovePriceListHandler());

        Assert.Equal(
            (SalesErrors.NotFinishedGood, SalesErrors.UomNotConvertible, SalesErrors.DuplicateLine, SalesErrors.LinesRequired, AuthorizationErrors.NotAuthorized),
            (raw.Code, noConversion.Code, duplicate.Code, empty.Code, controllerApproves.Code));
        var lists = JsonDocument.Parse(await h.QueryAsync(new ListPriceLists(h.CompanyId, s.Controller), new ListPriceListsHandler())).RootElement.GetProperty("items");
        Assert.Equal("2:ACTIVE:1|1:SUPERSEDED:2", string.Join('|', lists.EnumerateArray().Select(l => $"{l.GetProperty("version").GetInt32()}:{l.GetProperty("status").GetString()}:{l.GetProperty("lines").GetInt32()}")));
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetPriceList(h.CompanyId, s.Controller, v1), new GetPriceListHandler())).RootElement;
        Assert.Equal("ADOQUIN-H:m3:1850.0000|BLOQUE-6:un:45.0000", string.Join('|', detail.GetProperty("lines").EnumerateArray().Select(l =>
            $"{l.GetProperty("itemCode").GetString()}:{l.GetProperty("uom").GetString()}:{l.GetProperty("unitPrice").GetString()}")));
    }
}
