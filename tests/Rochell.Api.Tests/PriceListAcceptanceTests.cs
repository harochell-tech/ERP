using System.Text.Json;
using Rochell.Platform.Time;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>
/// PRS-1 E2E-PR1 through the API (E-PRS-05-7): the Controller creates «Hoteles» with BLOQUE-6 at 42.00 and its freight to Bávaro at
/// 3.00 per block, the Aprobador de políticas approves it; Crédito moves the customer to it and the Controller approves; the Vendedor
/// orders 1,000 blocks delivered at Bávaro (42,000.00 + freight 3,000.00); Despacho delivers 600 with our truck and the POD; Facturación
/// invoices the delivery: 25,200.00 + ITBIS 4,536.00 + freight 1,800.00 exempt = 31,536.00. GENERAL keeps BLOQUE-6 at 50.00.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PriceListAcceptanceTests(PostgresFixture postgres)
{
    private const string SalesItbis = """{"tax_code":"ITBIS","rate":"0.18","effect":"OUTPUT","exempt_item_categories":["TRANSPORTE"]}""";
    private const string Hash = "5f70bf18a086007016e948b04aed3b82103a36bea41755b6cddfaf10ace3c6ef";

    private static Guid Ref(JsonElement response) => response.GetProperty("resultRef").GetGuid();

    [Trait("AcceptancePrs1", "E2E-PR1")]
    [Fact]
    public async Task E2EPR1_customer_list_order_with_freight_delivery_and_invoice_with_exempt_freight_over_HTTP()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        using var api = new ApiHost(h, clock);
        var c = h.CompanyId;
        var today = BusinessCalendar.DefaultBusinessDate(clock.UtcNow);
        for (var y = today.Year - 1; y <= today.Year + 1; y++)
        {
            await h.OpenPeriodsAsync(y);
        }

        // Deployment: plant and patio, maps (FREIGHT_REVENUE on 40500), policies, sales ITBIS with TRANSPORTE exempt.
        var plant = await h.CreatePlantAsync(code: "HIGUEY");
        var patio = await h.CreateLocationAsync(plant, "PATIO");
        var area = await h.ScalarAsync<Guid>("SELECT valuation_area_id FROM md.plant WHERE plant_id = @p", ("p", plant));
        foreach (var (role, code, control) in new[]
        {
            ("FINISHED_GOODS", "1350", true), ("FINISHED_GOODS_IN_TRANSIT", "1351", true), ("MIGRATION_CLEARING", "3990", false), ("COGS", "5100", false),
            ("CONTRACT_ASSET", "1240", true), ("REVENUE_PRODUCT", "40100", false), ("FREIGHT_REVENUE", "40500", false), ("TRANSIT_LOSS", "6900", false),
            ("AR_CONTROL", "1210", true), ("ITBIS_PAYABLE", "2150", false),
        })
        {
            await h.CreateActiveMapAsync(role, await h.CreateAccountAsync(code, role, control));
        }

        await h.CreateActivePolicyAsync("CREDIT", new Dictionary<string, string>
        {
            ["overdue_days_block"] = "30",
            ["ar_aging_bucket_1_days"] = "30",
            ["ar_aging_bucket_2_days"] = "60",
            ["ar_aging_bucket_3_days"] = "90",
        });
        await h.CreateActivePolicyAsync("REVENUE_ACCOUNTING", new Dictionary<string, string>
        {
            ["unbilled_delivery_presentation"] = "CONTRACT_ASSET",
            ["unbilled_aging_alert_days"] = "30",
            ["delivery_open_alert_hours"] = "24",
            ["cash_deposit_alert_days"] = "2",
        });
        await h.ActivateRuleAsync(await h.FiscalActorsAsync(), "itbis-ventas", "ITBIS_VENTAS", FiscalRuleKinds.SalesItbis, SalesItbis, new DateOnly(2026, 1, 1));

        var controller = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CONTROLLER"));
        var approver = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("APROBADOR_POLITICAS"));
        var storekeeper = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("ALMACENISTA"));
        var credit = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CREDITO"));
        var seller = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("VENDEDOR"));
        var dispatch = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("DESPACHO"));
        var billing = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("FACTURACION"));
        foreach (var (rule, version) in new[] { ("OPEN-INV", 1), ("P-15", 1), ("P-15R", 1), ("P-16", 2), ("P-30", 1), ("P-18", 1) })
        {
            await controller.OkAsync(c, "finance", "approve-posting-rule-version", new { ruleCode = rule, version });
        }

        // Masters: the block, its cost and opening stock, the freight item, the zone, GENERAL and «Hoteles».
        var block = Ref(await storekeeper.OkAsync(c, "master-data", "create-finished-good", new { code = "BLOQUE-6", description = "Bloque de 6 pulgadas", baseUom = "un", itemCategory = "BLOQUE" }));
        await controller.OkAsync(c, "master-data", "activate-item", new { itemId = block, expectedVersion = 1 });
        var freightItem = Ref(await storekeeper.OkAsync(c, "master-data", "create-freight-item", new { description = "Transporte de blocks" }));
        await controller.OkAsync(c, "master-data", "activate-item", new { itemId = freightItem, expectedVersion = 1 });
        var cost = (await controller.OkAsync(c, "sales", "prepare-standard-cost", new { itemId = block, valuationAreaId = area, unitCost = "32.75" })).GetProperty("result").GetProperty("costVersionId").GetGuid();
        await approver.OkAsync(c, "sales", "approve-standard-cost", new { costVersionId = cost });
        var opening = Ref(await controller.OkAsync(c, "sales", "prepare-opening-inventory", new
        {
            fileName = "apertura.csv",
            contentBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("planta,ubicacion,producto,cantidad,documento\nHIGUEY,PATIO,BLOQUE-6,2000,ADM-1\n")),
            cutoverDate = new DateOnly(today.Year, today.Month, 1),
        }));
        await approver.OkAsync(c, "sales", "post-opening-inventory", new { batchId = opening, expectedVersion = 1 });
        var bavaro = Ref(await credit.OkAsync(c, "sales", "create-delivery-zone", new { name = "Bávaro" }));
        var general = Ref(await controller.OkAsync(c, "sales", "prepare-price-list", new { lines = new[] { new { itemId = block, uom = "un", unitPrice = "50.00" } } }));
        await approver.OkAsync(c, "sales", "approve-price-list", new { priceListVersionId = general });
        var hoteles = Ref(await controller.OkAsync(c, "sales", "create-price-list", new { code = "HOTELES", name = "Hoteles" }));
        var hotelesV1 = Ref(await controller.OkAsync(c, "sales", "prepare-price-list", new
        {
            priceListId = hoteles,
            lines = new[] { new { itemId = block, uom = "un", unitPrice = "42.00" } },
            freight = new[] { new { itemId = block, uom = "un", zoneId = bavaro, unitPrice = "3.00" } },
        }));
        await approver.OkAsync(c, "sales", "approve-price-list", new { priceListVersionId = hotelesV1 });

        // The customer, on «Hoteles» from its terms.
        var customer = Ref(await seller.OkAsync(c, "sales", "create-customer", new { rnc = "131925332", legalName = "Hotel Playa Bávaro" }));
        var terms = (await credit.OkAsync(c, "sales", "prepare-customer-terms", new
        {
            partyId = customer,
            paymentTermsDays = 30,
            creditLimit = "1000000.00",
            creditHold = false,
            priceListId = hoteles,
        })).GetProperty("result").GetProperty("termsVersionId").GetGuid();
        await controller.OkAsync(c, "sales", "approve-customer-terms", new { termsVersionId = terms });
        await credit.OkAsync(c, "sales", "activate-customer", new { partyId = customer, expectedVersion = 1 });
        var detail = await credit.GetOkAsync($"/api/v1/companies/{c}/sales/customers/{customer}");
        Assert.Equal("Hoteles", detail.GetProperty("terms").EnumerateArray().Single(t => t.GetProperty("status").GetString() == "ACTIVE").GetProperty("priceListName").GetString());

        // The order with our truck to Bávaro: 1,000 × 42.00 + 1,000 × 3.00.
        var created = await seller.OkAsync(c, "sales", "create-sales-order", new
        {
            partyId = customer,
            plantId = plant,
            deliveryTermCode = "DELIVERED_OWN_TRANSPORT",
            siteAddress = "Hotel Playa Bávaro",
            requestedDate = (DateOnly?)null,
            customerPoRef = (string?)null,
            lines = new[] { new { itemId = block, uom = "un", quantity = "1000" } },
            deliveryZoneId = bavaro,
        });
        var order = Ref(created);
        Assert.Equal("45000.00", created.GetProperty("result").GetProperty("totalNet").GetString());
        await seller.OkAsync(c, "sales", "submit-for-credit", new { salesOrderId = order, expectedVersion = 1 });
        var view = await seller.GetOkAsync($"/api/v1/companies/{c}/sales/orders/{order}");
        var line = view.GetProperty("lines")[0];
        Assert.Equal("CONFIRMED|Bávaro|Hoteles|42.0000|3000.00", $"{view.GetProperty("header").GetProperty("status").GetString()}|{view.GetProperty("deliveryZoneName").GetString()}|" +
            $"{line.GetProperty("priceListName").GetString()}|{line.GetProperty("unitPrice").GetString()}|{line.GetProperty("freightAmount").GetString()}");

        // 600 delivered with our truck and the POD.
        var truck = Ref(await dispatch.OkAsync(c, "sales", "register-vehicle", new { plate = "L123456", capacityKg = "12000", fleetCode = "BR 09" }));
        var driver = Ref(await dispatch.OkAsync(c, "sales", "register-driver", new { fullName = "Juan Pérez", nationalId = "00112345678" }));
        var delivery = Ref(await dispatch.OkAsync(c, "sales", "plan-delivery", new { salesOrderId = order, lines = new[] { new { salesOrderLineId = line.GetProperty("salesOrderLineId").GetGuid(), quantity = "600" } } }));
        var deliveryLine = (await dispatch.GetOkAsync($"/api/v1/companies/{c}/sales/deliveries/{delivery}")).GetProperty("lines")[0].GetProperty("deliveryLineId").GetGuid();
        await dispatch.OkAsync(c, "sales", "start-loading", new { deliveryId = delivery, expectedVersion = 1, vehicleId = truck, driverId = driver, customerVehiclePlate = (string?)null, customerDriverName = (string?)null });
        await dispatch.OkAsync(c, "sales", "confirm-loaded", new { deliveryId = delivery, expectedVersion = 2, lines = new[] { new { deliveryLineId = deliveryLine, sourceLocationId = patio } } });
        await dispatch.OkAsync(c, "sales", "record-gate-out", new { deliveryId = delivery, expectedVersion = 3, grossKg = "18000", tareKg = "8000", weighTicketRef = "TK-1", weighTicketSha256 = Hash });
        await dispatch.OkAsync(c, "sales", "record-pod", new
        {
            deliveryId = delivery,
            expectedVersion = 4,
            receivedByName = "Ing. María Gómez",
            receivedAt = clock.UtcNow.AddMinutes(-5),
            evidenceRef = "pod.jpg",
            evidenceSha256 = Hash,
            lines = new[] { new { deliveryLineId = deliveryLine, qtyReceived = "600", qtyReturned = "0" } },
            exceptionReason = (string?)null,
        });
        var print = await dispatch.GetOkAsync($"/api/v1/companies/{c}/sales/deliveries/{delivery}/print");
        Assert.Equal("Transporte de blocks — Bávaro", print.GetProperty("lines")[0].GetProperty("freight").GetString());

        // The invoice: the block with ITBIS and the freight exempt.
        var invoice = Ref(await billing.OkAsync(c, "sales", "create-invoice-from-deliveries", new { partyId = customer, deliveryLineIds = new[] { deliveryLine } }));
        var issued = await billing.OkAsync(c, "sales", "issue-invoice", new { invoiceId = invoice, expectedVersion = 1 });
        Assert.Equal("4536.00|31536.00", $"{issued.GetProperty("result").GetProperty("taxTotal").GetString()}|{issued.GetProperty("result").GetProperty("total").GetString()}");
        var lines = (await billing.GetOkAsync($"/api/v1/companies/{c}/sales/invoices/{invoice}")).GetProperty("lines");
        Assert.Equal(
            "PRODUCT:25200.00:4536.00,FREIGHT:1800.00:0.00",
            string.Join(',', lines.EnumerateArray().Select(l => $"{l.GetProperty("lineKind").GetString()}:{l.GetProperty("netAmount").GetString()}:{l.GetProperty("itbis").GetString()}")));
        Assert.Equal("-1800.00|0.00", await h.ScalarAsync<string>(
            """
            SELECT (SELECT sum(debit - credit)::numeric(19,2) FROM fin.gl_entry WHERE account_role = 'FREIGHT_REVENUE')::text || '|' ||
                   (SELECT sum(debit - credit)::numeric(19,2) FROM fin.gl_entry WHERE account_role = 'CONTRACT_ASSET')::text
            """));
    }
}
