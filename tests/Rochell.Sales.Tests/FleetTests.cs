using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Sales.Fleet;
using Rochell.Sales.Queries;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>VS3-02: vehicles and drivers (E-VS3-01-4/15, E-VS3-02-9).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class FleetTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Dispatch_registers_updates_and_deactivates_vehicles_and_drivers()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var dispatch = await h.SessionWithRolesAsync("DESPACHO");
        var seller = await h.SessionWithRolesAsync("VENDEDOR");

        var truck = (await h.RunAsync(new RegisterVehicle(h.CompanyId, dispatch, "v", "l-123 456", 12000m), new RegisterVehicleHandler())).ResultRef;
        var samePlate = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RegisterVehicle(h.CompanyId, dispatch, "v2", "L123456", 8000m), new RegisterVehicleHandler()));
        var badPlate = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RegisterVehicle(h.CompanyId, dispatch, "v3", "L1", 8000m), new RegisterVehicleHandler()));
        var bySeller = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RegisterVehicle(h.CompanyId, seller, "v4", "A123456", 8000m), new RegisterVehicleHandler()));
        await h.RunAsync(new UpdateVehicle(h.CompanyId, dispatch, "cap", truck, 1, 14000m), new UpdateVehicleHandler());
        await h.RunAsync(new DeactivateVehicle(h.CompanyId, dispatch, "off", truck, 2), new DeactivateVehicleHandler());
        var stale = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ActivateVehicle(h.CompanyId, dispatch, "on", truck, 2), new ActivateVehicleHandler()));

        var driver = (await h.RunAsync(new RegisterDriver(h.CompanyId, dispatch, "d", "Juan Pérez", "001-1234567-8"), new RegisterDriverHandler())).ResultRef;
        var sameId = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RegisterDriver(h.CompanyId, dispatch, "d2", "Otro", "00112345678"), new RegisterDriverHandler()));
        var badId = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RegisterDriver(h.CompanyId, dispatch, "d3", "Otro", "123"), new RegisterDriverHandler()));
        await h.RunAsync(new UpdateDriver(h.CompanyId, dispatch, "name", driver, 1, "Juan A. Pérez"), new UpdateDriverHandler());
        await h.RunAsync(new DeactivateDriver(h.CompanyId, dispatch, "doff", driver, 2), new DeactivateDriverHandler());
        await h.RunAsync(new ActivateDriver(h.CompanyId, dispatch, "don", driver, 3), new ActivateDriverHandler());

        Assert.Equal(
            (SalesErrors.PlateDuplicate, SalesErrors.PlateInvalid, AuthorizationErrors.NotAuthorized, SalesErrors.VersionConflict, SalesErrors.NationalIdDuplicate, SalesErrors.NationalIdInvalid),
            (samePlate.Code, badPlate.Code, bySeller.Code, stale.Code, sameId.Code, badId.Code));
        var vehicles = JsonDocument.Parse(await h.QueryAsync(new ListVehicles(h.CompanyId, seller), new ListVehiclesHandler())).RootElement.GetProperty("items");
        Assert.Equal("L123456:14000.000000:INACTIVE:3", string.Join(':', vehicles[0].GetProperty("plate").GetString(), vehicles[0].GetProperty("capacityKg").GetString(),
            vehicles[0].GetProperty("status").GetString(), vehicles[0].GetProperty("version").GetInt64()));
        var drivers = JsonDocument.Parse(await h.QueryAsync(new ListDrivers(h.CompanyId, seller, "ACTIVE"), new ListDriversHandler())).RootElement.GetProperty("items");
        Assert.Equal("Juan A. Pérez:00112345678:4", string.Join(':', drivers[0].GetProperty("fullName").GetString(), drivers[0].GetProperty("nationalId").GetString(), drivers[0].GetProperty("version").GetInt64()));
        Assert.Equal("ACTIVE,INACTIVE,ACTIVE", await h.ScalarAsync<string>($"SELECT string_agg(to_state, ',' ORDER BY state_history_id) FROM core.state_history WHERE aggregate_id = '{driver}'"));
    }
}
