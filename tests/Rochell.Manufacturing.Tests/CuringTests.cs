using System.Text.Json;
using Rochell.Manufacturing.Lots;
using Rochell.Manufacturing.Queries;
using Rochell.Manufacturing.Runs;
using Rochell.Platform.Commands;
using Rochell.Sales.Queries;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Manufacturing.Tests;

/// <summary>MFG1-04: release after curing, block and unblock, scrap (P-12) and CURADO out of dispatch (E-MFG1-04-1…7; MFG-04…06).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class CuringTests(PostgresFixture postgres)
{
    private const string P12 = "0192f001-0000-7000-8000-000000000026";

    private static async Task<(ProductionRunTests.Setup S, Guid Run, Guid Lot, Guid Curing, Guid Scrap)> PostedAsync(TestHarness h)
    {
        var s = await ProductionRunTests.SetupAsync(h);
        var scrap = await h.CreateAccountAsync("5160", "Scrap de producto terminado", isControl: false);
        await h.CreateActiveMapAsync("PRODUCTION_SCRAP", scrap);
        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '{P12}' AND version = 1");
        var run = (await ProductionRunTests.Start(h, s, "start")).ResultRef;
        var summary = JsonDocument.Parse((await ProductionRunTests.Record(h, s, run, "rec")).ResultPayload).RootElement;
        var posted = JsonDocument.Parse((await h.RunAsync(new PostShiftSummary(h.CompanyId, s.Manager, "post", s.Plant, run, summary.GetProperty("version").GetInt64()), new PostShiftSummaryHandler())).ResultPayload).RootElement;
        var curing = await h.ScalarAsync<Guid>("SELECT location_id FROM md.location WHERE plant_id = @p AND is_curing", ("p", s.Plant));
        return (s, run, posted.GetProperty("lotId").GetGuid(), curing, scrap);
    }

    [Trait("AcceptanceMfg1", "MFG-04")]
    [Trait("AcceptanceMfg1", "MFG-05")]
    [Fact]
    public async Task Calidad_releases_a_cured_lot_to_the_yard_without_a_journal_and_nothing_in_curing_is_offered_for_dispatch()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var (s, run, lot, _, _) = await PostedAsync(h);
        var quality = await h.SessionWithRolesAsync("CALIDAD");
        var seller = await h.SessionWithRolesAsync("VENDEDOR");

        var plants = await h.QueryAsync(new ListSalesPlants(h.CompanyId, seller), new ListSalesPlantsHandler());
        var early = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ReleaseLot(h.CompanyId, quality, "early", s.Plant, lot, 1, s.Patio), new ReleaseLotHandler()));
        var supervisor = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ReleaseLot(h.CompanyId, s.Supervisor, "sup", s.Plant, lot, 1, s.Patio), new ReleaseLotHandler()));
        var journals = await h.ScalarAsync<long>("SELECT count(*) FROM fin.gl_journal");
        clock.Advance(TimeSpan.FromHours(48));
        quality = await h.SessionWithRolesAsync("CALIDAD");
        var released = JsonDocument.Parse((await h.RunAsync(new ReleaseLot(h.CompanyId, quality, "rel", s.Plant, lot, 1, s.Patio), new ReleaseLotHandler())).ResultPayload).RootElement;
        var manager = await h.SessionWithRolesAsync("GERENTE_PLANTA");
        var reverse = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ReverseShiftSummary(h.CompanyId, manager, "rev", s.Plant, run, 2, "Tarde"), new ReverseShiftSummaryHandler()));

        Assert.DoesNotContain("CURADO", plants, StringComparison.Ordinal);
        Assert.Equal((ManufacturingErrors.CuringNotDone, AuthorizationErrors.NotAuthorized, ManufacturingErrors.LotMoved), (early.Code, supervisor.Code, reverse.Code));
        Assert.Equal("RELEASED:1480.000000", $"{released.GetProperty("status").GetString()}:{released.GetProperty("quantity").GetString()}");
        Assert.Equal(journals, await h.ScalarAsync<long>("SELECT count(*) FROM fin.gl_journal"));
        Assert.Equal("PATIO-A:1480.000000|CURADO:0.000000", await h.ScalarAsync<string>(
            $"SELECT string_agg(l.code || ':' || b.quantity::text, '|' ORDER BY l.is_curing, l.code) FROM inv.inv_stock_balance b JOIN md.location l ON l.location_id = b.location_id WHERE b.item_id = '{s.Block}'"));
        Assert.Equal("41440.0000:RELEASED", await h.ScalarAsync<string>(
            $"SELECT (SELECT value::text FROM inv.inv_valuation_balance WHERE item_id = '{s.Block}') || ':' || (SELECT string_agg(DISTINCT status, ',') FROM mfg.rack)"));
        var lots = JsonDocument.Parse(await h.QueryAsync(new ListFgLots(h.CompanyId, quality, s.Plant), new ListFgLotsHandler())).RootElement.GetProperty("items");
        Assert.Equal("RELEASED:True:PATIO-A:1480.000000:3", string.Join('|', lots.EnumerateArray().Select(l =>
            $"{l.GetProperty("status").GetString()}:{l.GetProperty("curingDone").GetBoolean()}:{l.GetProperty("locationCode").GetString()}:{l.GetProperty("quantity").GetString()}:{l.GetProperty("racks").GetInt32()}")));
    }

    [Trait("AcceptanceMfg1", "MFG-06")]
    [Fact]
    public async Task A_blocked_lot_is_not_released_and_scrap_is_posted_at_valuation_cost_until_the_lot_is_scrapped()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var (s, _, lot, curing, scrapAccount) = await PostedAsync(h);
        var quality = await h.SessionWithRolesAsync("CALIDAD");

        var noReason = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new BlockLot(h.CompanyId, quality, "b0", s.Plant, lot, 1, " "), new BlockLotHandler()));
        await h.RunAsync(new BlockLot(h.CompanyId, quality, "b1", s.Plant, lot, 1, "Fisuras en la cara"), new BlockLotHandler());
        clock.Advance(TimeSpan.FromHours(48));
        quality = await h.SessionWithRolesAsync("CALIDAD");
        var blocked = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ReleaseLot(h.CompanyId, quality, "r0", s.Plant, lot, 2, s.Patio), new ReleaseLotHandler()));
        var manager = await h.SessionWithRolesAsync("GERENTE_PLANTA");
        // 100 of 1,480 units valued 41,440.00 → 2,800.00 (point CURING); the lot stays BLOCKED.
        var first = JsonDocument.Parse((await h.RunAsync(new ScrapLot(h.CompanyId, manager, "s1", s.Plant, lot, curing, 100m, "Rotura al desmoldar"), new ScrapLotHandler())).ResultPayload).RootElement;
        await h.RunAsync(new UnblockLot(h.CompanyId, quality, "u1", s.Plant, lot, 2, "Revisado: fisuras superficiales"), new UnblockLotHandler());
        await h.RunAsync(new ReleaseLot(h.CompanyId, quality, "r1", s.Plant, lot, 3, s.Patio), new ReleaseLotHandler());
        var tooMuch = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ScrapLot(h.CompanyId, manager, "s2", s.Plant, lot, s.Patio, 1381m, "Todo"), new ScrapLotHandler()));
        // The last 1,380 units take the remaining 38,640.00 (point YARD); the lot becomes SCRAPPED.
        var last = JsonDocument.Parse((await h.RunAsync(new ScrapLot(h.CompanyId, manager, "s3", s.Plant, lot, s.Patio, 1380m, "Lote rechazado por el cliente"), new ScrapLotHandler())).ResultPayload).RootElement;

        Assert.Equal((ManufacturingErrors.ReasonRequired, ManufacturingErrors.InvalidState, Inventory.InventoryErrors.InsufficientStock), (noReason.Code, blocked.Code, tooMuch.Code));
        Assert.Equal("CURING:2800.00:BLOCKED", $"{first.GetProperty("point").GetString()}:{first.GetProperty("value").GetString()}:{first.GetProperty("lotStatus").GetString()}");
        Assert.Equal("YARD:38640.00:SCRAPPED", $"{last.GetProperty("point").GetString()}:{last.GetProperty("value").GetString()}:{last.GetProperty("lotStatus").GetString()}");
        Assert.Equal("41440.00:0.00", await h.ScalarAsync<string>($"SELECT {ProductionRunTests.Balance(scrapAccount)} || ':' || {ProductionRunTests.Balance(s.Fg)}"));
        Assert.Equal("SCRAPPED:SCRAPPED:2", await h.ScalarAsync<string>(
            "SELECT (SELECT status FROM mfg.fg_lot) || ':' || (SELECT string_agg(DISTINCT status, ',') FROM mfg.rack) || ':' || (SELECT count(*) FROM mfg.lot_scrap)"));
    }
}
