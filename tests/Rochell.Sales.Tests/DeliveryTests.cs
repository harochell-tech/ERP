using System.Text;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Sales.Customers;
using Rochell.Sales.Deliveries;
using Rochell.Sales.Fleet;
using Rochell.Sales.Opening;
using Rochell.Sales.Orders;
using Rochell.Sales.Pricing;
using Rochell.Sales.Queries;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>VS3-04: deliveries, gate out, POD and control transfer (SAL-03…05; E-VS3-04-1…15).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class DeliveryTests(PostgresFixture postgres)
{
    internal const string Hash = "5f70bf18a086007016e948b04aed3b82103a36bea41755b6cddfaf10ace3c6ef";

    internal sealed record Setup(Guid Seller, Guid Credit, Guid Dispatch, Guid Customer, Guid Plant, Guid Patio, Guid Block, Guid Truck, Guid Driver, Dictionary<string, Guid> Accounts);

    /// <summary>
    /// 2,000 blocks opened at the 32.75 standard cost (lots AP-…-1: 1,200 and AP-…-2: 800, value 65,500.00), a price of 50.00 per
    /// block, an ACTIVE customer with a 1,000,000.00 limit, our truck of 12,000 kg and its driver; every VS#3 rule approved.
    /// </summary>
    internal static async Task<Setup> SetupAsync(TestHarness h, string presentation = "CONTRACT_ASSET")
    {
        var today = BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);
        for (var y = today.Year - 1; y <= today.Year + 1; y++)
        {
            await h.OpenPeriodsAsync(y);
        }

        var plant = await h.CreatePlantAsync(code: "HIGUEY");
        var patio = await h.CreateLocationAsync(plant, "PATIO");
        var area = await h.ScalarAsync<Guid>("SELECT valuation_area_id FROM md.plant WHERE plant_id = @p", ("p", plant));
        var block = Guid.CreateVersion7();
        await h.AdminRequireAsync(
            $"""
            INSERT INTO md.item VALUES ('{block}', '{h.CompanyId}', 'BLOQUE-6', 'Bloque de 6 pulgadas', 'FINISHED_GOOD', 'un', 'BLOQUE', 'ACTIVE', 1);
            UPDATE fin.posting_rule_version v SET status = 'ACTIVE', approved_by = '{h.UserId}'
            FROM fin.posting_rule r WHERE r.posting_rule_id = v.posting_rule_id AND r.code IN ('OPEN-INV', 'P-15', 'P-15R', 'P-16', 'P-30', 'P-18');
            """);
        var accounts = new Dictionary<string, Guid>();
        foreach (var (role, code, control) in new[]
        {
            ("FINISHED_GOODS", "1350", true), ("FINISHED_GOODS_IN_TRANSIT", "1351", true), ("MIGRATION_CLEARING", "3990", false), ("COGS", "5100", false),
            ("CONTRACT_ASSET", "1240", true), ("UNBILLED_RECEIVABLE", "1245", true), ("REVENUE_PRODUCT", "4100", false), ("TRANSIT_LOSS", "6900", false),
            ("AR_CONTROL", "1210", true), ("ITBIS_PAYABLE", "2150", false),
        })
        {
            accounts[role] = await h.CreateAccountAsync(code, role, control);
            await h.CreateActiveMapAsync(role, accounts[role]);
        }

        await h.CreateActivePolicyAsync("CREDIT", new Dictionary<string, string> { ["overdue_days_block"] = "30" });
        await h.CreateActivePolicyAsync("REVENUE_ACCOUNTING", new Dictionary<string, string> { ["unbilled_delivery_presentation"] = presentation });
        var seller = await h.SessionWithRolesAsync("VENDEDOR");
        var credit = await h.SessionWithRolesAsync("CREDITO");
        var dispatch = await h.SessionWithRolesAsync("DESPACHO");
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var approver = await h.SessionWithRolesAsync("APROBADOR_POLITICAS");

        var cost = JsonDocument.Parse((await h.RunAsync(new PrepareStandardCost(h.CompanyId, controller, "cost", block, area, 32.75m), new PrepareStandardCostHandler())).ResultPayload)
            .RootElement.GetProperty("costVersionId").GetGuid();
        await h.RunAsync(new ApproveStandardCost(h.CompanyId, approver, "cost-a", cost), new ApproveStandardCostHandler());
        var csv = Convert.ToBase64String(Encoding.UTF8.GetBytes("planta,ubicacion,producto,cantidad,documento\nHIGUEY,PATIO,BLOQUE-6,1200,ADM-1\nHIGUEY,PATIO,BLOQUE-6,800,ADM-2\n"));
        var batch = (await h.RunAsync(new PrepareOpeningInventory(h.CompanyId, controller, "open", "apertura.csv", csv, new DateOnly(today.Year, today.Month, 1)), new PrepareOpeningInventoryHandler())).ResultRef;
        await h.RunAsync(new PostOpeningInventory(h.CompanyId, approver, "open-post", batch, 1), new PostOpeningInventoryHandler());
        var list = (await h.RunAsync(new PreparePriceList(h.CompanyId, controller, "pl", [new(block, "un", 50.00m)]), new PreparePriceListHandler())).ResultRef;
        await h.RunAsync(new ApprovePriceList(h.CompanyId, approver, "pl-a", list), new ApprovePriceListHandler());

        var customer = (await h.RunAsync(new CreateCustomer(h.CompanyId, seller, "c", "131925332", "Constructora Uno"), new CreateCustomerHandler())).ResultRef;
        var terms = JsonDocument.Parse((await h.RunAsync(new PrepareCustomerTerms(h.CompanyId, credit, "t", customer, 30, 1000000.00m, false), new PrepareCustomerTermsHandler())).ResultPayload)
            .RootElement.GetProperty("termsVersionId").GetGuid();
        await h.RunAsync(new ApproveCustomerTerms(h.CompanyId, controller, "t-a", terms), new ApproveCustomerTermsHandler());
        await h.RunAsync(new ActivateCustomer(h.CompanyId, credit, "act", customer, 1), new ActivateCustomerHandler());
        var truck = (await h.RunAsync(new RegisterVehicle(h.CompanyId, dispatch, "truck", "L123456", 12000m), new RegisterVehicleHandler())).ResultRef;
        var driver = (await h.RunAsync(new RegisterDriver(h.CompanyId, dispatch, "driver", "Juan Pérez", "00112345678"), new RegisterDriverHandler())).ResultRef;
        return new Setup(seller, credit, dispatch, customer, plant, patio, block, truck, driver, accounts);
    }

    internal static async Task<(Guid Order, Guid Line)> ConfirmedOrderAsync(TestHarness h, Setup s, string term, decimal quantity, string key = "o")
    {
        var order = (await h.RunAsync(
            new CreateSalesOrder(h.CompanyId, s.Seller, key, s.Customer, s.Plant, term, term == DeliveryTerms.DeliveredOwnTransport ? "Obra Punta Cana" : null, null, null, [new(s.Block, "un", quantity)]),
            new CreateSalesOrderHandler())).ResultRef;
        await h.RunAsync(new SubmitForCredit(h.CompanyId, s.Seller, key + "-s", order, 1), new SubmitForCreditHandler());
        return (order, await h.ScalarAsync<Guid>("SELECT line_id FROM sal.sales_order_line WHERE sales_order_id = @o", ("o", order)));
    }

    /// <summary>Plan, load and gate out one delivery; returns it and its line.</summary>
    internal static async Task<(Guid Delivery, Guid Line)> DispatchAsync(TestHarness h, Setup s, Guid order, Guid orderLine, decimal quantity, bool own, string key, decimal netKg = 10000m)
    {
        var delivery = (await h.RunAsync(new PlanDelivery(h.CompanyId, s.Dispatch, key, order, [new(orderLine, quantity)]), new PlanDeliveryHandler())).ResultRef;
        var line = await h.ScalarAsync<Guid>("SELECT delivery_line_id FROM log.delivery_line WHERE delivery_id = @d", ("d", delivery));
        await h.RunAsync(
            own ? new StartLoading(h.CompanyId, s.Dispatch, key + "-l", delivery, 1, s.Truck, s.Driver, null, null) : new StartLoading(h.CompanyId, s.Dispatch, key + "-l", delivery, 1, null, null, "a 123-456", "Pedro Cliente"),
            new StartLoadingHandler());
        await h.RunAsync(new ConfirmLoaded(h.CompanyId, s.Dispatch, key + "-c", delivery, 2, [new(line, s.Patio)]), new ConfirmLoadedHandler());
        await h.RunAsync(new RecordGateOut(h.CompanyId, s.Dispatch, key + "-g", delivery, 3, 8000m + netKg, 8000m, "TK-" + key, Hash), new RecordGateOutHandler());
        return (delivery, line);
    }

    internal static Task<string?> Balance(TestHarness h, Setup s, string role)
        => h.ScalarAsync<string>("SELECT coalesce(sum(debit - credit), 0)::numeric(19,2)::text FROM fin.gl_entry WHERE account_id = @a", ("a", s.Accounts[role]));

    internal static async Task<string> Balances(TestHarness h, Setup s, params string[] roles)
        => string.Join('|', await Task.WhenAll(roles.Select(async r => $"{r}={await Balance(h, s, r)}")));

    [Trait("AcceptanceVs3", "SAL-03")]
    [Fact]
    public async Task SAL03_a_pickup_transfers_control_at_the_gate_with_cost_revenue_and_contract_asset()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        var (order, orderLine) = await ConfirmedOrderAsync(h, s, DeliveryTerms.PickupAtPlant, 1000m);

        var (delivery, _) = await DispatchAsync(h, s, order, orderLine, 1000m, own: false, "d1");

        // 1,000 × 32.75 = 32,750.00 at cost; 1,000 × 50.00 = 50,000.00 of revenue against the contract asset (E-9 CONTRACT_ASSET).
        Assert.Equal(
            "FINISHED_GOODS=32750.00|COGS=32750.00|CONTRACT_ASSET=50000.00|REVENUE_PRODUCT=-50000.00|FINISHED_GOODS_IN_TRANSIT=0.00",
            await Balances(h, s, "FINISHED_GOODS", "COGS", "CONTRACT_ASSET", "REVENUE_PRODUCT", "FINISHED_GOODS_IN_TRANSIT"));
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetDelivery(h.CompanyId, s.Seller, delivery), new GetDeliveryHandler())).RootElement;
        Assert.Equal(("DELIVERED", "CD-000001", "A 123-456"), (detail.GetProperty("header").GetProperty("status").GetString(), detail.GetProperty("header").GetProperty("deliveryNo").GetString(),
            detail.GetProperty("customerVehiclePlate").GetString()));
        Assert.Equal("GATE_OUT:TRANSFERRED", string.Join('|', detail.GetProperty("assessments").EnumerateArray().Select(a => $"{a.GetProperty("triggerPoint").GetString()}:{a.GetProperty("result").GetString()}")));
        Assert.Equal($"AP-BLOQUE-6-{new DateOnly(BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow).Year, BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow).Month, 1):yyyyMMdd}-1:1000.000000",
            string.Join('|', detail.GetProperty("lines")[0].GetProperty("lots").EnumerateArray().Select(l => $"{l.GetProperty("lotCode").GetString()}:{l.GetProperty("baseQuantity").GetString()}")));
        Assert.Equal("DELIVERED", await h.ScalarAsync<string>("SELECT status FROM sal.sales_order WHERE sales_order_id = @o", ("o", order)));
        Assert.Equal("1000.000000:32750.0000", await h.ScalarAsync<string>($"SELECT quantity::text || ':' || value::text FROM inv.inv_valuation_balance WHERE item_id = '{s.Block}'"));
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM md.location WHERE is_transit")); // a pickup never uses TRANSITO
    }

    [Trait("AcceptanceVs3", "SAL-04")]
    [Fact]
    public async Task SAL04_a_site_delivery_moves_to_transit_at_the_gate_and_recognizes_revenue_only_at_the_POD()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        var (order, orderLine) = await ConfirmedOrderAsync(h, s, DeliveryTerms.DeliveredOwnTransport, 1000m);

        var (delivery, line) = await DispatchAsync(h, s, order, orderLine, 600m, own: true, "d1");
        var atGate = await Balances(h, s, "FINISHED_GOODS", "FINISHED_GOODS_IN_TRANSIT", "REVENUE_PRODUCT", "COGS");
        await h.RunAsync(
            new RecordPod(h.CompanyId, s.Dispatch, "pod", delivery, 4, "Ing. María Gómez", h.Clock.UtcNow.AddMinutes(-5), "foto-pod-001.jpg", Hash, [new(line, 600m, 0m)], null),
            new RecordPodHandler());

        Assert.Equal("FINISHED_GOODS=45850.00|FINISHED_GOODS_IN_TRANSIT=19650.00|REVENUE_PRODUCT=0.00|COGS=0.00", atGate); // 600 × 32.75 still ours, no revenue before the POD
        Assert.Equal(
            "FINISHED_GOODS=45850.00|FINISHED_GOODS_IN_TRANSIT=0.00|COGS=19650.00|CONTRACT_ASSET=30000.00|REVENUE_PRODUCT=-30000.00",
            await Balances(h, s, "FINISHED_GOODS", "FINISHED_GOODS_IN_TRANSIT", "COGS", "CONTRACT_ASSET", "REVENUE_PRODUCT"));
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetDelivery(h.CompanyId, s.Credit, delivery), new GetDeliveryHandler())).RootElement;
        Assert.Equal("DELIVERED", detail.GetProperty("header").GetProperty("status").GetString());
        Assert.Equal("GATE_OUT:RETAINED|POD:TRANSFERRED", string.Join('|', detail.GetProperty("assessments").EnumerateArray().Select(a => $"{a.GetProperty("triggerPoint").GetString()}:{a.GetProperty("result").GetString()}")));
        Assert.Equal("Ing. María Gómez", detail.GetProperty("pod").GetProperty("receivedByName").GetString());
        Assert.Equal("PLANNED,LOADING,LOADED,IN_TRANSIT,DELIVERED", string.Join(',', detail.GetProperty("history").EnumerateArray().Select(x => x.GetProperty("to").GetString())));
        Assert.Equal("PARTIALLY_DELIVERED:600.000000", await h.ScalarAsync<string>(
            "SELECT o.status || ':' || l.qty_delivered::text FROM sal.sales_order o JOIN sal.sales_order_line l ON l.sales_order_id = o.sales_order_id WHERE o.sales_order_id = @o", ("o", order)));
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM md.location WHERE is_transit AND code = 'TRANSITO'"));
    }

    [Trait("AcceptanceVs3", "SAL-05")]
    [Fact]
    public async Task SAL05_a_POD_with_a_shortage_transfers_only_what_was_received_returns_the_rest_and_writes_off_the_loss()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h, presentation: "UNBILLED_RECEIVABLE");
        var (order, orderLine) = await ConfirmedOrderAsync(h, s, DeliveryTerms.DeliveredOwnTransport, 100m);
        var (delivery, line) = await DispatchAsync(h, s, order, orderLine, 100m, own: true, "d1");

        var noReason = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new RecordPod(h.CompanyId, s.Dispatch, "p0", delivery, 4, "Capataz", h.Clock.UtcNow, "firma.pdf", Hash, [new(line, 90m, 5m)], null), new RecordPodHandler()));
        await h.RunAsync(
            new RecordPod(h.CompanyId, s.Dispatch, "pod", delivery, 4, "Capataz", h.Clock.UtcNow, "firma.pdf", Hash, [new(line, 90m, 5m)], "5 rotos en el camino, 5 rechazados por calidad"),
            new RecordPodHandler());

        Assert.Equal(DeliveryErrors.ExceptionReasonRequired, noReason.Code);
        // Received 90 → cost 2,947.50, revenue 4,500.00 (UNBILLED_RECEIVABLE per E-9); returned 5 → 163.75 back to the patio; lost 5 → 163.75.
        Assert.Equal(
            "FINISHED_GOODS=62388.75|FINISHED_GOODS_IN_TRANSIT=0.00|COGS=2947.50|UNBILLED_RECEIVABLE=4500.00|CONTRACT_ASSET=0.00|REVENUE_PRODUCT=-4500.00|TRANSIT_LOSS=163.75",
            await Balances(h, s, "FINISHED_GOODS", "FINISHED_GOODS_IN_TRANSIT", "COGS", "UNBILLED_RECEIVABLE", "CONTRACT_ASSET", "REVENUE_PRODUCT", "TRANSIT_LOSS"));
        Assert.Equal("DELIVERED_WITH_EXCEPTIONS:90.000000:5.000000:5.000000", await h.ScalarAsync<string>(
            "SELECT d.status || ':' || l.qty_delivered::text || ':' || l.qty_returned::text || ':' || l.qty_lost::text FROM log.delivery d JOIN log.delivery_line l ON l.delivery_id = d.delivery_id WHERE d.delivery_id = @d",
            ("d", delivery)));
        Assert.Equal("1905.000000", await h.ScalarAsync<string>($"SELECT quantity::text FROM inv.inv_valuation_balance WHERE item_id = '{s.Block}'")); // 2,000 − 90 − 5 lost
        Assert.Equal("P-15,P-15R,P-16,P-30", await h.ScalarAsync<string>(
            "SELECT string_agg(DISTINCT r.code, ',' ORDER BY r.code) FROM fin.gl_journal j JOIN fin.posting_rule r ON r.posting_rule_id = j.posting_rule_id WHERE r.code LIKE 'P-%'"));
    }

    [Fact]
    public async Task A_total_rejection_returns_everything_and_the_quantity_can_be_delivered_again()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        var (order, orderLine) = await ConfirmedOrderAsync(h, s, DeliveryTerms.DeliveredOwnTransport, 100m);
        var (delivery, _) = await DispatchAsync(h, s, order, orderLine, 100m, own: true, "d1");

        await h.RunAsync(new RecordReturnTrip(h.CompanyId, s.Dispatch, "back", delivery, 4, "Obra cerrada, no recibieron"), new RecordReturnTripHandler());
        var replanned = (await h.RunAsync(new PlanDelivery(h.CompanyId, s.Dispatch, "again", order, [new(orderLine, 100m)]), new PlanDeliveryHandler())).ResultRef;
        var overPlan = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new PlanDelivery(h.CompanyId, s.Dispatch, "over", order, [new(orderLine, 1m)]), new PlanDeliveryHandler()));
        await h.RunAsync(new CancelDelivery(h.CompanyId, s.Dispatch, "cancel", replanned, 1, "Cliente pospuso"), new CancelDeliveryHandler());

        Assert.Equal(DeliveryErrors.QuantityExceedsOpen, overPlan.Code);
        Assert.Equal("FINISHED_GOODS=65500.00|FINISHED_GOODS_IN_TRANSIT=0.00|REVENUE_PRODUCT=0.00", await Balances(h, s, "FINISHED_GOODS", "FINISHED_GOODS_IN_TRANSIT", "REVENUE_PRODUCT"));
        Assert.Equal("RETURNED,CANCELLED", await h.ScalarAsync<string>("SELECT string_agg(status, ',' ORDER BY delivery_no) FROM log.delivery"));
        Assert.Equal("CONFIRMED", await h.ScalarAsync<string>("SELECT status FROM sal.sales_order WHERE sales_order_id = @o", ("o", order)));
    }

    [Fact]
    public async Task Transport_weight_stock_and_close_short_rules_hold()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        var (order, orderLine) = await ConfirmedOrderAsync(h, s, DeliveryTerms.DeliveredOwnTransport, 1000m);

        var planned = (await h.RunAsync(new PlanDelivery(h.CompanyId, s.Dispatch, "p", order, [new(orderLine, 500m)]), new PlanDeliveryHandler())).ResultRef;
        var line = await h.ScalarAsync<Guid>("SELECT delivery_line_id FROM log.delivery_line WHERE delivery_id = @d", ("d", planned));
        var noTruck = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new StartLoading(h.CompanyId, s.Dispatch, "l0", planned, 1, null, null, "A1", "X"), new StartLoadingHandler()));
        await h.RunAsync(new StartLoading(h.CompanyId, s.Dispatch, "l", planned, 1, s.Truck, s.Driver, null, null), new StartLoadingHandler());
        await h.RunAsync(new ConfirmLoaded(h.CompanyId, s.Dispatch, "c", planned, 2, [new(line, s.Patio)]), new ConfirmLoadedHandler());
        var overweight = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RecordGateOut(h.CompanyId, s.Dispatch, "g0", planned, 3, 21000m, 8000m, "TK-1", Hash), new RecordGateOutHandler()));
        var badHash = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RecordGateOut(h.CompanyId, s.Dispatch, "g1", planned, 3, 18000m, 8000m, "TK-1", "abc"), new RecordGateOutHandler()));
        await h.RunAsync(new RecordGateOut(h.CompanyId, s.Dispatch, "g", planned, 3, 18000m, 8000m, "TK-1", Hash), new RecordGateOutHandler());
        var line2 = await h.ScalarAsync<Guid>("SELECT delivery_line_id FROM log.delivery_line WHERE delivery_id = @d", ("d", planned));
        var openDelivery = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new CloseShortSalesOrder(h.CompanyId, s.Credit, "close0", order, 2, "No quiere más"), new CloseShortSalesOrderHandler()));
        await h.RunAsync(new RecordPod(h.CompanyId, s.Dispatch, "pod", planned, 4, "Capataz", h.Clock.UtcNow, "firma.pdf", Hash, [new(line2, 500m, 0m)], null), new RecordPodHandler());
        var sellerCloses = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new CloseShortSalesOrder(h.CompanyId, s.Seller, "close1", order, 3, "No quiere más"), new CloseShortSalesOrderHandler()));
        await h.RunAsync(new CloseShortSalesOrder(h.CompanyId, s.Credit, "close", order, 3, "El cliente no necesita el resto"), new CloseShortSalesOrderHandler());
        var afterClose = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new PlanDelivery(h.CompanyId, s.Dispatch, "p2", order, [new(orderLine, 1m)]), new PlanDeliveryHandler()));

        Assert.Equal(
            (DeliveryErrors.TransportRequired, DeliveryErrors.OverCapacity, DeliveryErrors.EvidenceInvalid, SalesErrors.InvalidState, AuthorizationErrors.NotAuthorized, SalesErrors.InvalidState),
            (noTruck.Code, overweight.Code, badHash.Code, openDelivery.Code, sellerCloses.Code, afterClose.Code));
        Assert.Equal("CLOSED:El cliente no necesita el resto", await h.ScalarAsync<string>("SELECT status || ':' || close_reason FROM sal.sales_order WHERE sales_order_id = @o", ("o", order)));
        var list = JsonDocument.Parse(await h.QueryAsync(new ListDeliveries(h.CompanyId, s.Seller, SalesOrderId: order), new ListDeliveriesHandler())).RootElement.GetProperty("items");
        Assert.Equal(1, list.GetArrayLength());
    }
}
