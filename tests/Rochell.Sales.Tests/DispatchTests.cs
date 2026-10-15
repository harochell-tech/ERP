using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Sales.Deliveries;
using Rochell.Sales.Orders;
using Rochell.Sales.Queries;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>
/// DSP-01 (E-DSP-1…5): the transport chosen with the delivery starts its loading; at a plant without a truck scale «Despachar» confirms
/// the loading and records the gate-out in one command with no weighing; the lots chosen per line leave first, up to the quantity chosen.
/// With a truck scale the weighing is required as before. Opening of DeliveryTests: lots AP-…-1 (1,200) and AP-…-2 (800).
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class DispatchTests(PostgresFixture postgres)
{
    private static async Task<(Guid Delivery, Guid Line, Guid Lot1, Guid Lot2)> PlannedWithTransportAsync(TestHarness h, DeliveryTests.Setup s, decimal quantity)
    {
        var (order, orderLine) = await DeliveryTests.ConfirmedOrderAsync(h, s, DeliveryTerms.PickupAtPlant, quantity);
        var delivery = (await h.RunAsync(new PlanDelivery(h.CompanyId, s.Dispatch, "p", order, [new(orderLine, quantity)], CustomerVehiclePlate: "a 123-456", CustomerDriverName: "Pedro Cliente"),
            new PlanDeliveryHandler())).ResultRef;
        var line = await h.ScalarAsync<Guid>("SELECT delivery_line_id FROM log.delivery_line WHERE delivery_id = @d", ("d", delivery));
        var lot1 = await h.ScalarAsync<Guid>("SELECT lot_id FROM inv.lot WHERE lot_code LIKE '%-1' AND item_id = @i", ("i", s.Block));
        var lot2 = await h.ScalarAsync<Guid>("SELECT lot_id FROM inv.lot WHERE lot_code LIKE '%-2' AND item_id = @i", ("i", s.Block));
        return (delivery, line, lot1, lot2);
    }

    [Fact]
    public async Task Without_a_truck_scale_Despachar_takes_the_chosen_lot_first_and_records_no_weighing()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await DeliveryTests.SetupAsync(h);
        var (delivery, line, lot1, lot2) = await PlannedWithTransportAsync(h, s, 500m);

        var planned = JsonDocument.Parse(await h.QueryAsync(new GetDelivery(h.CompanyId, s.Seller, delivery), new GetDeliveryHandler())).RootElement;
        var lots = JsonDocument.Parse(await h.QueryAsync(new ListDispatchLots(h.CompanyId, s.Seller, delivery), new ListDispatchLotsHandler())).RootElement.GetProperty("items");
        var zero = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new DispatchDelivery(h.CompanyId, s.Dispatch, "d0", delivery, 2, [new(line, s.Patio, [new ScannedRack(lot2, Quantity: 0m)])]), new DispatchDeliveryHandler()));
        var dispatched = JsonDocument.Parse((await h.RunAsync(
            new DispatchDelivery(h.CompanyId, s.Dispatch, "d", delivery, 2, [new(line, s.Patio, [new ScannedRack(lot2, Quantity: 300m)])]), new DispatchDeliveryHandler())).ResultPayload).RootElement;
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetDelivery(h.CompanyId, s.Seller, delivery), new GetDeliveryHandler())).RootElement;

        // E-DSP-2: the transport given with the delivery started its loading; E-DSP-1: the plant has no truck scale.
        Assert.Equal(("LOADING", false), (planned.GetProperty("header").GetProperty("status").GetString(), planned.GetProperty("plantHasTruckScale").GetBoolean()));
        Assert.Equal("PATIO:1200.000000|PATIO:800.000000", string.Join('|', lots.EnumerateArray().Select(l => $"{l.GetProperty("locationCode").GetString()}:{l.GetProperty("quantity").GetString()}")));
        Assert.Equal(DeliveryErrors.ScanInvalid, zero.Code);

        // E-DSP-3 / 5: 300 from the chosen lot -2, the other 200 FIFO from lot -1; delivered at the gate, nothing weighed.
        Assert.Equal("DELIVERED", dispatched.GetProperty("status").GetString());
        Assert.Equal("1:200.000000|2:300.000000", string.Join('|', detail.GetProperty("lines")[0].GetProperty("lots").EnumerateArray()
            .Select(l => $"{l.GetProperty("lotCode").GetString()![^1..]}:{l.GetProperty("baseQuantity").GetString()}")));
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("grossKg").ValueKind);
        Assert.Equal("200.000000|300.000000", await h.ScalarAsync<string>(
            $"SELECT string_agg(base_quantity::text, '|' ORDER BY lot_id = '{lot2}') FROM log.delivery_line_lot WHERE delivery_line_id = @l AND lot_id IN ('{lot1}', '{lot2}')", ("l", line)));
    }

    [Fact]
    public async Task Without_a_truck_scale_a_site_delivery_with_our_truck_is_dispatched_from_a_chosen_lot_without_a_quantity()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await DeliveryTests.SetupAsync(h);
        var (order, orderLine) = await DeliveryTests.ConfirmedOrderAsync(h, s, DeliveryTerms.DeliveredOwnTransport, 500m);
        var delivery = (await h.RunAsync(new PlanDelivery(h.CompanyId, s.Dispatch, "p", order, [new(orderLine, 500m)], VehicleId: s.Truck, DriverId: s.Driver), new PlanDeliveryHandler())).ResultRef;
        var line = await h.ScalarAsync<Guid>("SELECT delivery_line_id FROM log.delivery_line WHERE delivery_id = @d", ("d", delivery));
        var lot1 = await h.ScalarAsync<Guid>("SELECT lot_id FROM inv.lot WHERE lot_code LIKE '%-1' AND item_id = @i", ("i", s.Block));

        var dispatched = JsonDocument.Parse((await h.RunAsync(
            new DispatchDelivery(h.CompanyId, s.Dispatch, "d", delivery, 2, [new(line, s.Patio, [new ScannedRack(lot1)])]), new DispatchDeliveryHandler())).ResultPayload).RootElement;
        Assert.Equal("IN_TRANSIT", dispatched.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_TRANSITO_location_without_its_flag_is_refused_clearly_instead_of_colliding()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await DeliveryTests.SetupAsync(h);
        await h.AdminRequireAsync(
            $"""
            DO $$ BEGIN
              PERFORM set_config('session_replication_role', 'replica', true);
              DELETE FROM md.location WHERE plant_id = '{s.Plant}' AND is_transit;
              INSERT INTO md.location (location_id, company_id, plant_id, code) VALUES (gen_random_uuid(), '{h.CompanyId}', '{s.Plant}', 'TRANSITO');
            END $$
            """);
        var (order, orderLine) = await DeliveryTests.ConfirmedOrderAsync(h, s, DeliveryTerms.DeliveredOwnTransport, 500m);
        var delivery = (await h.RunAsync(new PlanDelivery(h.CompanyId, s.Dispatch, "p", order, [new(orderLine, 500m)], VehicleId: s.Truck, DriverId: s.Driver), new PlanDeliveryHandler())).ResultRef;
        var line = await h.ScalarAsync<Guid>("SELECT delivery_line_id FROM log.delivery_line WHERE delivery_id = @d", ("d", delivery));

        var refused = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new DispatchDelivery(h.CompanyId, s.Dispatch, "d", delivery, 2, [new(line, s.Patio)]), new DispatchDeliveryHandler()));
        Assert.Equal(DeliveryErrors.LocationInvalid, refused.Code);
    }

    [Fact]
    public async Task With_a_truck_scale_the_gate_out_is_weighed_and_Despachar_is_refused()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await DeliveryTests.SetupAsync(h);
        await h.AdminRequireAsync($"UPDATE md.plant SET has_truck_scale = true WHERE plant_id = '{s.Plant}'");
        var (delivery, line, _, _) = await PlannedWithTransportAsync(h, s, 500m);

        var refused = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new DispatchDelivery(h.CompanyId, s.Dispatch, "d", delivery, 2, [new(line, s.Patio)]), new DispatchDeliveryHandler()));
        await h.RunAsync(new ConfirmLoaded(h.CompanyId, s.Dispatch, "c", delivery, 2, [new(line, s.Patio)]), new ConfirmLoadedHandler());
        var unweighed = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RecordGateOut(h.CompanyId, s.Dispatch, "g0", delivery, 3), new RecordGateOutHandler()));
        await h.RunAsync(new RecordGateOut(h.CompanyId, s.Dispatch, "g", delivery, 3, 18000m, 8000m, "TK-1", DeliveryTests.Hash), new RecordGateOutHandler());

        Assert.Equal((DeliveryErrors.WeighingInvalid, DeliveryErrors.WeighingInvalid), (refused.Code, unweighed.Code));
        Assert.Equal("18000.000000:8000.000000:TK-1", await h.ScalarAsync<string>("SELECT gross_kg::text || ':' || tare_kg::text || ':' || weigh_ticket_ref FROM log.delivery WHERE delivery_id = @d", ("d", delivery)));
    }
}
