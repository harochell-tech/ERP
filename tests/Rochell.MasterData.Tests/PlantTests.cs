using System.Text.Json;
using Rochell.MasterData.Plants;
using Rochell.MasterData.Queries;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.MasterData.Tests;

/// <summary>
/// PLT-01 (E-PLT-1…5): plants and locations from the screen. A plant is born with its valuation area and RECEPCION, PATIO, CURADO
/// (curing) and TRANSITO (transit); more locations are added; nothing is deleted — plants and locations are deactivated and leave the
/// lists of new documents, the plants screen still shows them.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PlantTests(PostgresFixture postgres)
{
    private static async Task<JsonElement> PlantsAsync(TestHarness h, Guid session, bool all = false)
        => JsonDocument.Parse(await h.QueryAsync(new ListPlants(h.CompanyId, session, IncludeInactive: all), new ListPlantsHandler())).RootElement.GetProperty("items");

    private static string Codes(JsonElement plants)
        => string.Join(';', plants.EnumerateArray().Select(p =>
            $"{p.GetProperty("code").GetString()}:{string.Join(',', p.GetProperty("locations").EnumerateArray().Select(l => l.GetProperty("code").GetString()))}"));

    [Fact]
    public async Task The_controller_creates_a_plant_with_its_four_locations_and_adds_and_renames_another()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var buyer = await h.SessionWithRolesAsync("COMPRADOR");

        var plant = (await h.RunAsync(new CreatePlant(h.CompanyId, controller, "p1", " higuey ", "Planta Higüey"), new CreatePlantHandler())).ResultRef;
        var duplicate = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new CreatePlant(h.CompanyId, controller, "p2", "HIGUEY", "Otra"), new CreatePlantHandler()));
        var badCode = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new CreatePlant(h.CompanyId, controller, "p3", "H", "Corta"), new CreatePlantHandler()));
        var notAllowed = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new CreatePlant(h.CompanyId, buyer, "p4", "OTRA", "Otra"), new CreatePlantHandler()));
        await h.RunAsync(new CreateLocation(h.CompanyId, controller, "l1", plant, "bodega-2", "Bodega de cemento"), new CreateLocationHandler());
        var twice = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new CreateLocation(h.CompanyId, controller, "l2", plant, "CURADO", "Otro curado"), new CreateLocationHandler()));
        var patio = await h.ScalarAsync<Guid>("SELECT location_id FROM md.location WHERE plant_id = @p AND code = 'PATIO'", ("p", plant));
        await h.RunAsync(new RenameLocation(h.CompanyId, controller, "r1", patio, "Patio norte"), new RenameLocationHandler());

        Assert.Equal("HIGUEY:BODEGA-2,CURADO,PATIO,RECEPCION,TRANSITO", Codes(await PlantsAsync(h, controller)));
        Assert.Equal(
            "BODEGA-2:Bodega de cemento:false:false,CURADO:Curado:true:false,PATIO:Patio norte:false:false,RECEPCION:Recepción de materiales:false:false,TRANSITO:En tránsito al cliente:false:true",
            await h.ScalarAsync<string>(
                "SELECT string_agg(code || ':' || name || ':' || is_curing || ':' || is_transit, ',' ORDER BY code) FROM md.location WHERE plant_id = @p", ("p", plant)));
        Assert.Equal("HIGUEY", await h.ScalarAsync<string>("SELECT va.code FROM md.plant p JOIN md.valuation_area va USING (valuation_area_id) WHERE p.plant_id = @p", ("p", plant)));
        Assert.Equal((PlantErrors.CodeUsed, PlantErrors.CodeInvalid, AuthorizationErrors.NotAuthorized), (duplicate.Code, badCode.Code, notAllowed.Code));
        Assert.Equal(PlantErrors.LocationCodeUsed, twice.Code);
        Assert.Equal("LocationCreated,LocationRenamed,PlantCreated", await h.ScalarAsync<string>(
            "SELECT string_agg(event_type, ',' ORDER BY event_type) FROM core.domain_event WHERE aggregate_type = 'Plant'"));
    }

    [Fact]
    public async Task Plants_and_locations_are_deactivated_never_deleted_and_leave_the_lists_of_new_documents()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var first = (await h.RunAsync(new CreatePlant(h.CompanyId, controller, "p1", "MATILLA", "Planta Matilla"), new CreatePlantHandler())).ResultRef;
        var last = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SetPlantStatus(h.CompanyId, controller, "s0", first, false), new SetPlantStatusHandler()));
        await h.RunAsync(new CreatePlant(h.CompanyId, controller, "p2", "HIGUEY", "Planta Higüey"), new CreatePlantHandler());

        var curing = await h.ScalarAsync<Guid>("SELECT location_id FROM md.location WHERE plant_id = @p AND code = 'CURADO'", ("p", first));
        var system = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SetLocationStatus(h.CompanyId, controller, "c0", curing, false), new SetLocationStatusHandler()));
        var patio = await h.ScalarAsync<Guid>("SELECT location_id FROM md.location WHERE plant_id = @p AND code = 'PATIO'", ("p", first));
        await h.RunAsync(new SetLocationStatus(h.CompanyId, controller, "c1", patio, false), new SetLocationStatusHandler());
        await h.RunAsync(new SetPlantStatus(h.CompanyId, controller, "s1", first, false), new SetPlantStatusHandler());
        var offered = Codes(await PlantsAsync(h, controller));
        var all = await PlantsAsync(h, controller, all: true);
        await h.RunAsync(new SetPlantStatus(h.CompanyId, controller, "s2", first, true), new SetPlantStatusHandler());
        var back = Codes(await PlantsAsync(h, controller));
        var delete = await h.AdminExecuteAsync($"DELETE FROM md.location WHERE location_id = '{patio}'");
        var recode = await h.AdminExecuteAsync($"UPDATE md.plant SET code = 'OTRA' WHERE plant_id = '{first}'");

        Assert.Equal((PlantErrors.LastPlant, PlantErrors.LocationSystem), (last.Code, system.Code));
        Assert.Equal("HIGUEY:CURADO,PATIO,RECEPCION,TRANSITO", offered);
        Assert.Equal(
            "HIGUEY:ACTIVE,MATILLA:INACTIVE",
            string.Join(',', all.EnumerateArray().Select(p => $"{p.GetProperty("code").GetString()}:{p.GetProperty("status").GetString()}")));
        Assert.Equal("INACTIVE", all.EnumerateArray().Single(p => p.GetProperty("code").GetString() == "MATILLA").GetProperty("locations").EnumerateArray()
            .Single(l => l.GetProperty("code").GetString() == "PATIO").GetProperty("status").GetString());
        Assert.Equal("HIGUEY:CURADO,PATIO,RECEPCION,TRANSITO;MATILLA:CURADO,RECEPCION,TRANSITO", back); // the plant is back; PATIO stays off
        Assert.Equal(SqlStates.RaiseException, delete?.SqlState);
        Assert.Equal(SqlStates.RaiseException, recode?.SqlState);
    }
}
