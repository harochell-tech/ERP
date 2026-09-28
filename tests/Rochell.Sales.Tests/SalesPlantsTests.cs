using System.Text.Json;
using Rochell.Sales.Queries;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>E-VS3-10-13: the plants and stock locations for the sales screens, read with sales:read.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SalesPlantsTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_seller_reads_the_plants_and_their_stock_locations_without_the_transit_one()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var plant = await h.CreatePlantAsync(code: "HIGUEY");
        await h.CreateLocationAsync(plant, "PATIO");
        await h.AdminRequireAsync($"UPDATE md.location SET is_transit = true WHERE plant_id = '{plant}' AND code <> 'PATIO'");
        var seller = await h.SessionWithRolesAsync("VENDEDOR");

        var plants = JsonDocument.Parse(await h.QueryAsync(new ListSalesPlants(h.CompanyId, seller), new ListSalesPlantsHandler())).RootElement.GetProperty("items");

        var only = Assert.Single(plants.EnumerateArray());
        Assert.Equal("HIGUEY|PATIO", $"{only.GetProperty("code").GetString()}|{string.Join(',', only.GetProperty("locations").EnumerateArray().Select(l => l.GetProperty("code").GetString()))}");
    }
}
