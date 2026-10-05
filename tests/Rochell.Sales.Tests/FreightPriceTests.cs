using System.Globalization;
using System.Text.Json;
using Rochell.MasterData;
using Rochell.MasterData.Items;
using Rochell.Platform.Commands;
using Rochell.Sales.Pricing;
using Rochell.Sales.Queries;
using Rochell.Sales.Zones;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>
/// PRS-03 (SRV-01, SRV-02, E-PRS-03-1…6): the freight item, delivery zones and the freight table of a price list version. The
/// recipe, opening-stock and order guards refusing a SERVICE item are in PriceListFreightSchemaTests and the migrations.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class FreightPriceTests(PostgresFixture postgres)
{
    private static async Task<Guid> ZoneAsync(TestHarness h, Guid session, string key, string name)
        => (await h.RunAsync(new CreateDeliveryZone(h.CompanyId, session, key, name), new CreateDeliveryZoneHandler())).ResultRef;

    private static async Task<string> ZonesAsync(TestHarness h, Guid session, string? status = null)
        => string.Join(',', JsonDocument.Parse(await h.QueryAsync(new ListDeliveryZones(h.CompanyId, session, status), new ListDeliveryZonesHandler())).RootElement
            .GetProperty("items").EnumerateArray().Select(z => $"{z.GetProperty("name").GetString()}:{z.GetProperty("status").GetString()}"));

    [Trait("AcceptancePrs1", "SRV-02")]
    [Fact]
    public async Task SRV02_zones_are_kept_by_Controller_or_Credito_named_once_renamed_and_an_inactive_one_takes_no_freight()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await DeliveryTests.SetupAsync(h);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var bavaro = await ZoneAsync(h, s.Credit, "z1", "  Bávaro ");
        var miches = await ZoneAsync(h, controller, "z2", "Miches");
        var twice = await Assert.ThrowsAsync<DomainException>(() => ZoneAsync(h, s.Credit, "z3", "BÁVARO"));
        var seller = await Assert.ThrowsAsync<DomainException>(() => ZoneAsync(h, s.Seller, "z4", "Macao"));
        await h.RunAsync(new RenameDeliveryZone(h.CompanyId, s.Credit, "r", bavaro, 1, "Bávaro - Punta Cana"), new RenameDeliveryZoneHandler());
        await h.RunAsync(new DeactivateDeliveryZone(h.CompanyId, s.Credit, "d", miches, 1), new DeactivateDeliveryZoneHandler());
        var all = await ZonesAsync(h, s.Seller);
        var active = await ZonesAsync(h, s.Seller, "ACTIVE");
        var freightToInactive = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PreparePriceList(h.CompanyId, controller, "frt-pl", [], null, [new(s.Block, "un", miches, 3.00m)]), new PreparePriceListHandler()));
        await h.RunAsync(new ReactivateDeliveryZone(h.CompanyId, controller, "re", miches, 2), new ReactivateDeliveryZoneHandler());

        Assert.Equal((ZoneErrors.NameUsed, AuthorizationErrors.NotAuthorized, ZoneErrors.Inactive), (twice.Code, seller.Code, freightToInactive.Code));
        Assert.Equal(("Bávaro - Punta Cana:ACTIVE,Miches:INACTIVE", "Bávaro - Punta Cana:ACTIVE"), (all, active));
        Assert.Equal("Bávaro - Punta Cana:ACTIVE,Miches:ACTIVE", await ZonesAsync(h, s.Seller));
        Assert.Equal(
            "DeliveryZone:null>ACTIVE,ACTIVE>INACTIVE,INACTIVE>ACTIVE",
            "DeliveryZone:" + await h.ScalarAsync<string>(
                $"SELECT string_agg(coalesce(from_state, 'null') || '>' || to_state, ',' ORDER BY state_history_id) FROM core.state_history WHERE aggregate_id = '{miches}'"));
    }

    [Trait("AcceptancePrs1", "SRV-01")]
    [Fact]
    public async Task SRV01_the_freight_item_is_one_service_created_and_activated_like_an_item_and_never_priced_or_costed()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await DeliveryTests.SetupAsync(h);
        var storekeeper = await h.SessionWithRolesAsync("ALMACENISTA");
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var freight = (await h.RunAsync(new CreateFreightItem(h.CompanyId, storekeeper, "f", "Transporte de blocks"), new CreateFreightItemHandler())).ResultRef;
        var second = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new CreateFreightItem(h.CompanyId, storekeeper, "f2", "Otro flete"), new CreateFreightItemHandler()));
        await h.RunAsync(new ActivateItem(h.CompanyId, controller, "a", freight, 1), new ActivateItemHandler());

        var priced = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new PreparePriceList(h.CompanyId, controller, "frt-pl", [new(freight, "un", 3.00m)]), new PreparePriceListHandler()));
        var area = await h.ScalarAsync<Guid>("SELECT valuation_area_id FROM md.plant WHERE plant_id = @p", ("p", s.Plant));
        var costed = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new PrepareStandardCost(h.CompanyId, controller, "frt-cost", freight, area, 1.00m), new PrepareStandardCostHandler()));

        Assert.Equal(MasterDataErrors.ItemCodeDuplicate, second.Code);
        Assert.Equal((SalesErrors.NotFinishedGood, SalesErrors.NotFinishedGood), (priced.Code, costed.Code));
        Assert.Equal("TRANSPORTE:SERVICE:TRANSPORTE:un:ACTIVE", await h.ScalarAsync<string>(
            $"SELECT code || ':' || item_type || ':' || item_category || ':' || base_uom || ':' || status FROM md.item WHERE item_id = '{freight}'"));
    }

    [Fact]
    public async Task A_version_carries_its_freight_table_alone_or_with_products_once_per_product_unit_and_zone()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await DeliveryTests.SetupAsync(h);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var approver = await h.SessionWithRolesAsync("APROBADOR_POLITICAS");
        var bavaro = await ZoneAsync(h, controller, "z1", "Bávaro");
        var capCana = await ZoneAsync(h, controller, "z2", "Cap Cana");
        var hoteles = (await h.RunAsync(new CreatePriceList(h.CompanyId, controller, "hl", "HOTELES", "Hoteles"), new CreatePriceListHandler())).ResultRef;

        var twice = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PreparePriceList(h.CompanyId, controller, "dup", [], hoteles, [new(s.Block, "un", bavaro, 3.00m), new(s.Block, "un", bavaro, 3.50m)]), new PreparePriceListHandler()));
        var badUnit = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PreparePriceList(h.CompanyId, controller, "uom", [], hoteles, [new(s.Block, "kg", bavaro, 3.00m)]), new PreparePriceListHandler()));
        var empty = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new PreparePriceList(h.CompanyId, controller, "none", [], hoteles), new PreparePriceListHandler()));
        var onlyFreight = (await h.RunAsync(
            new PreparePriceList(h.CompanyId, controller, "fr", [], hoteles, [new(s.Block, "un", bavaro, 3.00m), new(s.Block, "un", capCana, 4.25m)]), new PreparePriceListHandler())).ResultRef;
        await h.RunAsync(new ApprovePriceList(h.CompanyId, approver, "fra", onlyFreight), new ApprovePriceListHandler());
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetPriceList(h.CompanyId, s.Seller, onlyFreight), new GetPriceListHandler())).RootElement;

        Assert.Equal((SalesErrors.DuplicateLine, SalesErrors.UomNotConvertible, SalesErrors.LinesRequired), (twice.Code, badUnit.Code, empty.Code));
        Assert.Equal(0, detail.GetProperty("lines").GetArrayLength());
        Assert.Equal(
            "BLOQUE-6:un:Bávaro:3.0000,BLOQUE-6:un:Cap Cana:4.2500",
            string.Join(',', detail.GetProperty("freight").EnumerateArray().Select(f =>
                $"{f.GetProperty("itemCode").GetString()}:{f.GetProperty("uom").GetString()}:{f.GetProperty("zoneName").GetString()}:" +
                decimal.Parse(f.GetProperty("unitPrice").GetString()!, CultureInfo.InvariantCulture).ToString("0.0000", CultureInfo.InvariantCulture))));
        Assert.Equal("HOTELES:ACTIVE", $"{detail.GetProperty("header").GetProperty("priceListCode").GetString()}:{detail.GetProperty("header").GetProperty("status").GetString()}");
    }
}
