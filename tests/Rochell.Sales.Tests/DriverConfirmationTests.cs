using System.Text.Json;
using Rochell.Identity;
using Rochell.Platform.Commands;
using Rochell.Sales.Deliveries;
using Rochell.Sales.Orders;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>
/// ENT1-02 (E-ENT-1…8, E-ENT1-01-1…10): the driver confirms from the delivery note's QR. Gate out opens the link; a wrong PIN is
/// kept and counted (5 lock); a full receipt is the POD (P-16); differences wait for Dispatch, whose POD completes them; a POD
/// recorded by Dispatch first annuls the link.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class DriverConfirmationTests(PostgresFixture postgres)
{
    private static readonly DriverLinkKey Key = new(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private sealed record Trip(DeliveryTests.Setup S, Guid Delivery, Guid Line, Guid Service);

    private static async Task<Trip> TripAsync(TestHarness h)
    {
        var s = await DeliveryTests.SetupAsync(h);
        await h.RunAsync(new SetDriverPin(h.CompanyId, s.Dispatch, "pin", s.Driver, "4821"), new SetDriverPinHandler());
        var (order, line) = await DeliveryTests.ConfirmedOrderAsync(h, s, DeliveryTerms.DeliveredOwnTransport, 600m);
        var (delivery, deliveryLine) = await DeliveryTests.DispatchAsync(h, s, order, line, 600m, own: true, "d");
        await h.GrantAsync(h.CompanyId, IdentityConstants.DeliveryConfirmationUserId, "CONFIRMACION_ENTREGA");
        var service = await h.Sessions.StartServiceSessionAsync(IdentityConstants.DeliveryConfirmationUserId);
        return new Trip(s, delivery, deliveryLine, service);
    }

    private static DriverLinkAccess Access(TestHarness h, Trip t, string pin, int generation = 1, string address = "10.0.0.7")
        => new(t.Delivery, generation, Key.Mac(h.CompanyId, t.Delivery, generation), pin, address);

    private static DriverReport Report(string outcome = "FULL", string? note = null, DateTime? phoneAt = null)
        => new("Ing. María Gómez", "00112345678", outcome, note, phoneAt, 18.583100m, -68.404700m, 12.50m, "PHOTO", "entregas/x/d/c.jpg", DeliveryTests.Hash);

    private static async Task<JsonElement> ConfirmAsync(TestHarness h, Trip t, string key, DriverLinkAccess access, DriverReport report)
        => JsonDocument.Parse((await h.RunAsync(new ConfirmDeliveryByDriver(h.CompanyId, t.Service, key, access, report), new ConfirmDeliveryByDriverHandler(Key))).ResultPayload).RootElement;

    [Fact]
    public async Task A_wrong_pin_is_kept_and_a_full_receipt_becomes_the_pod()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var t = await TripAsync(h);

        var wrong = await ConfirmAsync(h, t, "w", Access(h, t, "1111"), Report());
        var view = JsonDocument.Parse(await h.QueryAsync(new GetDriverDelivery(h.CompanyId, t.Service, t.Delivery, 1, Key.Mac(h.CompanyId, t.Delivery, 1)), new GetDriverDeliveryHandler(Key))).RootElement;
        var forged = JsonDocument.Parse(await h.QueryAsync(new GetDriverDelivery(h.CompanyId, t.Service, t.Delivery, 1, "AAAA"), new GetDriverDeliveryHandler(Key))).RootElement;
        var done = await ConfirmAsync(h, t, "ok", Access(h, t, "4821"), Report(phoneAt: h.Clock.UtcNow.AddMinutes(2)));

        Assert.Equal(("WRONG_PIN", 4), (wrong.GetProperty("state").GetString(), wrong.GetProperty("remaining").GetInt32()));
        Assert.Equal(("ACTIVE", "Constructora Uno", "600"), (view.GetProperty("State").GetString(), view.GetProperty("Customer").GetString(), view.GetProperty("Lines")[0].GetProperty("Quantity").GetString()));
        Assert.Equal("INVALID", forged.GetProperty("State").GetString());
        Assert.Equal(("DELIVERED", "DELIVERED"), (done.GetProperty("state").GetString(), done.GetProperty("status").GetString()));
        Assert.Equal("CONFIRMED", await h.ScalarAsync<string>("SELECT status FROM log.delivery_link WHERE delivery_id = @d", ("d", t.Delivery)));
        Assert.Equal("false,true", await h.ScalarAsync<string>("SELECT string_agg(pin_ok::text, ',' ORDER BY attempted_at, pin_ok) FROM log.delivery_link_attempt WHERE delivery_id = @d", ("d", t.Delivery)));
        Assert.Equal("Ing. María Gómez:true:600.000000", await h.ScalarAsync<string>(
            """
            SELECT p.received_by_name || ':' || (p.driver_confirmation_id = c.confirmation_id)::text || ':' || dl.qty_delivered
            FROM log.pod p JOIN log.driver_confirmation c ON c.delivery_id = p.delivery_id JOIN log.delivery_line dl ON dl.delivery_id = p.delivery_id
            WHERE p.delivery_id = @d
            """,
            ("d", t.Delivery)));
        // P-16 moved the cost out of transit: 600 blocks at 32.75.
        Assert.Equal("0.00", await DeliveryTests.Balance(h, t.S, "FINISHED_GOODS_IN_TRANSIT"));
        Assert.Equal("19650.00", await DeliveryTests.Balance(h, t.S, "COGS"));
        // E-ENT1-01-6: a phone two minutes ahead is within bounds: its time is the confirmation's (the POD never lies in the future).
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM log.driver_confirmation WHERE delivery_id = @d AND confirmed_at = phone_at AND phone_at > server_at", ("d", t.Delivery)));
    }

    [Fact]
    public async Task Five_wrong_pins_lock_the_link_and_dispatch_reopens_it_as_a_new_generation()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var t = await TripAsync(h);

        string? last = null;
        for (var i = 0; i < 5; i++)
        {
            last = JsonDocument.Parse((await h.RunAsync(new VerifyDriverPin(h.CompanyId, t.Service, $"v{i}", Access(h, t, "0000")), new VerifyDriverPinHandler(Key))).ResultPayload)
                .RootElement.GetProperty("state").GetString();
        }

        var lockedRight = await ConfirmAsync(h, t, "after", Access(h, t, "4821"), Report());
        var reopened = JsonDocument.Parse((await h.RunAsync(new ReopenDeliveryLink(h.CompanyId, t.S.Dispatch, "re", t.Delivery), new ReopenDeliveryLinkHandler())).ResultPayload).RootElement;
        var oldQr = await ConfirmAsync(h, t, "old", Access(h, t, "4821"), Report());
        var newQr = JsonDocument.Parse((await h.RunAsync(new VerifyDriverPin(h.CompanyId, t.Service, "new", Access(h, t, "4821", generation: 2)), new VerifyDriverPinHandler(Key))).ResultPayload).RootElement;

        Assert.Equal("LOCKED", last);
        Assert.Equal("LOCKED", lockedRight.GetProperty("state").GetString());
        Assert.Equal(2, reopened.GetProperty("generation").GetInt32());
        Assert.Equal("INVALID", oldQr.GetProperty("state").GetString());
        Assert.Equal("OK", newQr.GetProperty("state").GetString());
        var notDispatch = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SetDriverPin(h.CompanyId, t.S.Seller, "x", t.S.Driver, "1234"), new SetDriverPinHandler()));
        var badPin = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SetDriverPin(h.CompanyId, t.S.Dispatch, "y", t.S.Driver, "12a4"), new SetDriverPinHandler()));
        Assert.Equal(DriverConfirmationErrors.PinInvalid, badPin.Code);
        Assert.NotEqual(DriverConfirmationErrors.PinInvalid, notDispatch.Code);
    }

    [Fact]
    public async Task Differences_wait_for_dispatch_whose_pod_completes_them()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var t = await TripAsync(h);

        var silent = await Assert.ThrowsAsync<DomainException>(() => ConfirmAsync(h, t, "s", Access(h, t, "4821"), Report("DIFFERENCES")));
        var reported = await ConfirmAsync(h, t, "diff", Access(h, t, "4821"), Report("DIFFERENCES", "20 bloques rotos al descargar", phoneAt: h.Clock.UtcNow.AddHours(2)));
        var status = await h.ScalarAsync<string>("SELECT status FROM log.delivery WHERE delivery_id = @d", ("d", t.Delivery));
        await h.RunAsync(
            new RecordPod(h.CompanyId, t.S.Dispatch, "pod", t.Delivery, 4, "Ing. María Gómez", h.Clock.UtcNow, "entregas/x/d/c.jpg", DeliveryTests.Hash, [new(t.Line, 580m, 20m)], "Rotos al descargar"),
            new RecordPodHandler());

        Assert.Equal(DriverConfirmationErrors.ConfirmationInvalid, silent.Code);
        Assert.Equal("DIFFERENCES_REPORTED", reported.GetProperty("state").GetString());
        Assert.Equal("IN_TRANSIT", status);
        // E-ENT1-01-6: two hours ahead is not believed — the server's time.
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM log.driver_confirmation WHERE delivery_id = @d AND confirmed_at = server_at", ("d", t.Delivery)));
        Assert.Equal("DELIVERED_WITH_EXCEPTIONS:true", await h.ScalarAsync<string>(
            "SELECT d.status || ':' || (p.driver_confirmation_id IS NOT NULL)::text FROM log.delivery d JOIN log.pod p ON p.delivery_id = d.delivery_id WHERE d.delivery_id = @d", ("d", t.Delivery)));
    }

    [Fact]
    public async Task A_pod_recorded_by_dispatch_first_annuls_the_link()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var t = await TripAsync(h);

        await h.RunAsync(
            new RecordPod(h.CompanyId, t.S.Dispatch, "pod", t.Delivery, 4, "Capataz", h.Clock.UtcNow, "firma.pdf", DeliveryTests.Hash, [new(t.Line, 600m, 0m)], null), new RecordPodHandler());
        var view = JsonDocument.Parse(await h.QueryAsync(new GetDriverDelivery(h.CompanyId, t.Service, t.Delivery, 1, Key.Mac(h.CompanyId, t.Delivery, 1)), new GetDriverDeliveryHandler(Key))).RootElement;
        var late = await ConfirmAsync(h, t, "late", Access(h, t, "4821"), Report());

        Assert.Equal("RECORDED_BY_DISPATCH", view.GetProperty("State").GetString());
        Assert.Equal("ANNULLED", late.GetProperty("state").GetString());
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM log.driver_confirmation WHERE delivery_id = @d", ("d", t.Delivery)));
    }
}
