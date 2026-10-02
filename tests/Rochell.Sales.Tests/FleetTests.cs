using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Sales.Fleet;
using Rochell.Sales.Orders;
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

        var truck = (await h.RunAsync(new RegisterVehicle(h.CompanyId, dispatch, "v", "l-123 456", 12000m, "br  09"), new RegisterVehicleHandler())).ResultRef;
        var samePlate = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RegisterVehicle(h.CompanyId, dispatch, "v2", "L123456", 8000m, "HR 114"), new RegisterVehicleHandler()));
        var badPlate = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RegisterVehicle(h.CompanyId, dispatch, "v3", "L1", 8000m, "HR 114"), new RegisterVehicleHandler()));
        var bySeller = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RegisterVehicle(h.CompanyId, seller, "v4", "A123456", 8000m, "HR 114"), new RegisterVehicleHandler()));
        await h.RunAsync(new UpdateVehicle(h.CompanyId, dispatch, "cap", truck, 1, 14000m, "BR 09"), new UpdateVehicleHandler());
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

    /// <summary>FLT-01 (E-FLT-1…5): the vehicle's «ficha» and insurance policy, the driver's licence expiry (a warning, never a block) and both on the delivery.</summary>
    [Fact]
    public async Task Vehicles_carry_their_ficha_and_policy_and_drivers_the_expiry_of_their_licence()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var dispatch = await h.SessionWithRolesAsync("DESPACHO");
        var today = ReceiptTests.Today(h);

        var truck = (await h.RunAsync(new RegisterVehicle(h.CompanyId, dispatch, "v", "L123456", 12000m, " br   09 ", " POL-778899 "), new RegisterVehicleHandler())).ResultRef;
        var noCode = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RegisterVehicle(h.CompanyId, dispatch, "v2", "L654321", 8000m), new RegisterVehicleHandler()));
        var badCode = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RegisterVehicle(h.CompanyId, dispatch, "v3", "L654321", 8000m, "BR-09"), new RegisterVehicleHandler()));
        var sameCode = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RegisterVehicle(h.CompanyId, dispatch, "v4", "L654321", 8000m, "BR 09"), new RegisterVehicleHandler()));
        var other = (await h.RunAsync(new RegisterVehicle(h.CompanyId, dispatch, "v5", "L654321", 8000m, "HR 114"), new RegisterVehicleHandler())).ResultRef;
        var taken = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new UpdateVehicle(h.CompanyId, dispatch, "u", other, 1, 8000m, "br 09"), new UpdateVehicleHandler()));
        await h.RunAsync(new UpdateVehicle(h.CompanyId, dispatch, "u2", other, 1, 9000m, "HR 115", "POL-1"), new UpdateVehicleHandler());

        // A licence that expired yesterday and one that expires in ten days: both drivers are registered and stay usable (E-FLT-4).
        await h.RunAsync(new RegisterDriver(h.CompanyId, dispatch, "d1", "Ana Gil", "00112345678", today.AddDays(-1)), new RegisterDriverHandler());
        var pedro = (await h.RunAsync(new RegisterDriver(h.CompanyId, dispatch, "d2", "Pedro Mota", "00187654321"), new RegisterDriverHandler())).ResultRef;
        await h.RunAsync(new UpdateDriver(h.CompanyId, dispatch, "d3", pedro, 1, "Pedro Mota", today.AddDays(10)), new UpdateDriverHandler());

        Assert.Equal(
            (SalesErrors.FleetCodeInvalid, SalesErrors.FleetCodeInvalid, SalesErrors.FleetCodeDuplicate, SalesErrors.FleetCodeDuplicate),
            (noCode.Code, badCode.Code, sameCode.Code, taken.Code));
        var vehicles = JsonDocument.Parse(await h.QueryAsync(new ListVehicles(h.CompanyId, dispatch), new ListVehiclesHandler())).RootElement.GetProperty("items");
        Assert.Equal(
            "BR 09:L123456:POL-778899:1,HR 115:L654321:POL-1:2",
            string.Join(',', vehicles.EnumerateArray().Select(v => $"{v.GetProperty("fleetCode").GetString()}:{v.GetProperty("plate").GetString()}:{v.GetProperty("insurancePolicyNo").GetString()}:{v.GetProperty("version").GetInt64()}")));
        var drivers = JsonDocument.Parse(await h.QueryAsync(new ListDrivers(h.CompanyId, dispatch), new ListDriversHandler())).RootElement.GetProperty("items");
        Assert.Equal(
            $"Ana Gil:{today.AddDays(-1):yyyy-MM-dd}:-1,Pedro Mota:{today.AddDays(10):yyyy-MM-dd}:10",
            string.Join(',', drivers.EnumerateArray().Select(d => $"{d.GetProperty("fullName").GetString()}:{d.GetProperty("licenseExpiresOn").GetString()}:{d.GetProperty("daysToLicenseExpiry").GetInt32()}")));
        Assert.Equal(
            "BR 09|POL-778899",
            await h.ScalarAsync<string>($"SELECT (payload ->> 'fleetCode') || '|' || (payload ->> 'insurancePolicyNo') FROM core.domain_event WHERE aggregate_id = '{truck}' AND event_type = 'VehicleRegistered'"));
    }

    [Fact]
    public async Task The_delivery_shows_the_ficha_and_the_driver_and_the_list_filters_by_them()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await DeliveryTests.SetupAsync(h);
        var seller = await h.SessionWithRolesAsync("VENDEDOR");
        var (order, line) = await DeliveryTests.ConfirmedOrderAsync(h, s, DeliveryTerms.DeliveredOwnTransport, 100m);
        var (delivery, _) = await DeliveryTests.DispatchAsync(h, s, order, line, 40m, own: true, "d1");

        var print = JsonDocument.Parse(await h.QueryAsync(new GetDeliveryPrint(h.CompanyId, seller, delivery), new GetDeliveryPrintHandler())).RootElement;
        var byTruck = JsonDocument.Parse(await h.QueryAsync(new ListDeliveries(h.CompanyId, seller, VehicleId: s.Truck, DriverId: s.Driver), new ListDeliveriesHandler())).RootElement.GetProperty("items");
        var byAnother = JsonDocument.Parse(await h.QueryAsync(new ListDeliveries(h.CompanyId, seller, VehicleId: Guid.NewGuid()), new ListDeliveriesHandler())).RootElement.GetProperty("items");

        Assert.Equal("BR 09:L123456:Juan Pérez", $"{print.GetProperty("vehicleFleetCode").GetString()}:{print.GetProperty("vehiclePlate").GetString()}:{print.GetProperty("driverName").GetString()}");
        Assert.Equal("BR 09:Juan Pérez", $"{byTruck[0].GetProperty("fleetCode").GetString()}:{byTruck[0].GetProperty("driverName").GetString()}");
        Assert.Equal((1, 0), (byTruck.GetArrayLength(), byAnother.GetArrayLength()));
    }
}
