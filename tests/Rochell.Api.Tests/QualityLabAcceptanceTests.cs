using System.Globalization;
using System.Text.Json;
using Rochell.Audit;
using Rochell.Platform.Time;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>
/// LAB-1 through the API (LAB1-02; E-LAB1-4, E-LAB1-02-6, 7, 11, 14): a released lot with one delivery out and another one loading gets
/// real breaks at 28 days that do not comply — the lot is blocked, the gate-out no longer takes it, and the recall lists the delivery,
/// its customer and invoice and the stock left; from the delivery, the recall goes back to the run, its consumption and the verdict.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class QualityLabAcceptanceTests(PostgresFixture postgres)
{
    private const string SalesItbis = """{"tax_code":"ITBIS","rate":"0.18","effect":"OUTPUT","exempt_item_categories":[]}""";
    private const string Hash = "5f70bf18a086007016e948b04aed3b82103a36bea41755b6cddfaf10ace3c6ef";

    private static Guid Ref(JsonElement response) => response.GetProperty("resultRef").GetGuid();

    [Trait("AcceptanceLab1", "LAB-05")]
    [Trait("AcceptanceLab1", "LAB-14")]
    [Fact]
    public async Task A_real_no_cumple_blocks_a_released_lot_the_gate_out_skips_it_and_the_recall_lists_where_it_went_over_HTTP()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        using var api = new ApiHost(h, clock);
        var c = h.CompanyId;
        var today = BusinessCalendar.DefaultBusinessDate(clock.UtcNow);

        // Deployment and fixtures: plant with PATIO-A, periods, raw materials with stock (VS#1 receipts), maps, policies, SALES_ITBIS.
        var stock = await h.CreateStockSetupAsync();
        var plant = stock.PlantId;
        var patio = stock.LocationA;
        var sand = stock.ItemId;
        var cement = await h.CreateActiveItemAsync("CEMENTO-GU", "kg", "CEMENTO");
        var additive = await h.CreateActiveItemAsync("ADITIVO-P", "l", "ADITIVO");
        await h.RunAsync(new TestReceiveStock(c, h.SessionId, "r-cement", patio, cement, 20000m, 164000.00m, today), new TestReceiveStockHandler());
        await h.RunAsync(new TestReceiveStock(c, h.SessionId, "r-sand", patio, sand, 110m, 109900.00m, today), new TestReceiveStockHandler());
        await h.RunAsync(new TestReceiveStock(c, h.SessionId, "r-additive", patio, additive, 200m, 10000.00m, today), new TestReceiveStockHandler());
        foreach (var (role, code, control) in new[]
        {
            ("WIP", "1340", true), ("FINISHED_GOODS", "1350", true), ("FINISHED_GOODS_IN_TRANSIT", "1351", true), ("CONVERSION_ABSORPTION", "5150", false),
            ("MATERIAL_USAGE_VARIANCE", "5102", false), ("MATERIAL_PRICE_VARIANCE", "5103", false), ("COGS", "5100", false), ("CONTRACT_ASSET", "1240", true),
            ("REVENUE_PRODUCT", "4110", false), ("TRANSIT_LOSS", "6900", false), ("AR_CONTROL", "1210", true), ("ITBIS_PAYABLE", "2150", false),
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
        });
        await h.CreateActivePolicyAsync("PRODUCTION", new Dictionary<string, string> { ["usage_tolerance_pct"] = "0.05" });
        var actors = await h.FiscalActorsAsync();
        await h.ActivateRuleAsync(actors, "itbis-ventas", "ITBIS_VENTAS", FiscalRuleKinds.SalesItbis, SalesItbis, new DateOnly(2026, 1, 1));

        // The people.
        var controller = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CONTROLLER"));
        var approver = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("APROBADOR_POLITICAS"));
        var storekeeper = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("ALMACENISTA"));
        var manager = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("GERENTE_PLANTA"));
        var supervisor = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("SUPERVISOR_PRODUCCION"));
        var seller = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("VENDEDOR"));
        var credit = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CREDITO"));
        var dispatch = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("DESPACHO"));
        var billing = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("FACTURACION"));

        foreach (var rule in new[] { "P-08", "P-10", "P-13", "P-15", "P-15R", "P-16", "P-30", "P-18" })
        {
            await controller.OkAsync(c, "finance", "approve-posting-rule-version", new { ruleCode = rule, version = 1 });
        }

        // Production masters: machine, day shift, the product, its recipe and its standard cost from the recipe.
        var machine = Ref(await manager.OkAsync(c, "manufacturing", "create-machine", new { plantId = plant, code = "BESSER-1", name = "Besser V3-12" }));
        var day = Ref(await manager.OkAsync(c, "manufacturing", "define-shift", new { plantId = plant, code = "DIA", startsAt = "07:00:00", endsAt = "19:00:00" }));
        var block = Ref(await storekeeper.OkAsync(c, "master-data", "create-finished-good", new { code = "BLOQUE-6", description = "Bloque de 6 pulgadas", baseUom = "un", itemCategory = "BLOQUE" }));
        await controller.OkAsync(c, "master-data", "activate-item", new { itemId = block, expectedVersion = 1 });
        var recipe = Ref(await supervisor.OkAsync(c, "manufacturing", "prepare-recipe", new
        {
            plantId = plant,
            itemId = block,
            machineId = machine,
            unitsPerBatch = "150",
            unitsPerCycle = "6",
            unitsPerRack = "600",
            minCuringHours = 24,
            maxCuringHours = 168,
            lines = new[] { new { materialItemId = cement, qtyPerBatch = "180" }, new { materialItemId = sand, qtyPerBatch = "1.8" }, new { materialItemId = additive, qtyPerBatch = "1.5" } },
        }));
        await manager.OkAsync(c, "manufacturing", "approve-recipe", new { plantId = plant, recipeVersionId = recipe });
        var cost = (await controller.OkAsync(c, "sales", "prepare-standard-cost-from-recipe", new
        {
            recipeVersionId = recipe,
            materialPrices = new[] { new { materialItemId = cement, stdPrice = "8.00" }, new { materialItemId = sand, stdPrice = "1000.00" }, new { materialItemId = additive, stdPrice = "50.00" } },
            conversionCost = "5.90",
        })).GetProperty("result");
        Assert.Equal("28.0000", cost.GetProperty("unitCost").GetString());
        await approver.OkAsync(c, "sales", "approve-standard-cost", new { costVersionId = cost.GetProperty("costVersionId").GetGuid() });

        // LAB-1: Calidad loads Block's requirements (100 kg/cm² at 28 days) and the machine's short code, so the lot gets its field code.
        var quality = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CALIDAD"));
        await quality.OkAsync(c, "manufacturing", "set-item-spec", new
        {
            itemId = block,
            lotPrefix = "6",
            nominalWidthCm = "19.5",
            nominalHeightCm = "19.5",
            nominalLengthCm = "39.5",
            netAreaFraction = (string?)null,
            minAvg28d = "100",
            minIndividual28d = (string?)null,
        });
        await quality.OkAsync(c, "manufacturing", "set-machine-short-code", new { plantId = plant, machineId = machine, expectedVersion = 1, shortCode = "P1" });

        // The day's run: 10 batches, 1,480 good units; the plant manager posts it (P-08 and P-10).
        var run = Ref(await supervisor.OkAsync(c, "manufacturing", "start-production-run", new { plantId = plant, machineId = machine, shiftId = day, businessDate = today, itemId = block }));
        await supervisor.OkAsync(c, "manufacturing", "record-shift-summary", new
        {
            plantId = plant,
            runId = run,
            batches = 10,
            goodUnits = "1480",
            mixScrapUnits = "20",
            freshScrapUnits = "0",
            consumption = new[]
            {
                new { materialItemId = cement, locationId = patio, quantity = "1850", uom = "kg" },
                new { materialItemId = sand, locationId = patio, quantity = "18.375", uom = "t" },
                new { materialItemId = additive, locationId = patio, quantity = "15", uom = "l" },
            },
        });
        var posted = (await manager.OkAsync(c, "manufacturing", "post-shift-summary", new { plantId = plant, runId = run, expectedVersion = 1 })).GetProperty("result");
        var lot = posted.GetProperty("lotId").GetGuid();

        // Two days later Calidad releases the lot to PATIO-A. A customer takes 1,000 blocks (invoiced); a second truck of 300 is loaded.
        var prices = Ref(await controller.OkAsync(c, "sales", "prepare-price-list", new { lines = new[] { new { itemId = block, uom = "un", unitPrice = "50.00" } } }));
        await approver.OkAsync(c, "sales", "approve-price-list", new { priceListVersionId = prices });
        var customer = Ref(await seller.OkAsync(c, "sales", "create-customer", new { rnc = "131925332", legalName = "Constructora Uno" }));
        var terms = (await credit.OkAsync(c, "sales", "prepare-customer-terms", new { partyId = customer, paymentTermsDays = 30, creditLimit = "1000000.00", creditHold = false }))
            .GetProperty("result").GetProperty("termsVersionId").GetGuid();
        await controller.OkAsync(c, "sales", "approve-customer-terms", new { termsVersionId = terms });
        await credit.OkAsync(c, "sales", "activate-customer", new { partyId = customer, expectedVersion = 1 });
        clock.Advance(TimeSpan.FromHours(48));
        quality = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CALIDAD"));
        seller = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("VENDEDOR"));
        dispatch = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("DESPACHO"));
        billing = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("FACTURACION"));
        await quality.OkAsync(c, "manufacturing", "release-lot", new { plantId = plant, lotId = lot, expectedVersion = 1, toLocationId = patio });

        async Task<(Guid Delivery, Guid Line)> LoadAsync(string quantity, string plate)
        {
            var order = Ref(await seller.OkAsync(c, "sales", "create-sales-order", new
            {
                partyId = customer,
                plantId = plant,
                deliveryTermCode = "PICKUP_AT_PLANT",
                siteAddress = (string?)null,
                requestedDate = (DateOnly?)null,
                customerPoRef = (string?)null,
                lines = new[] { new { itemId = block, uom = "un", quantity } },
            }));
            await seller.OkAsync(c, "sales", "submit-for-credit", new { salesOrderId = order, expectedVersion = 1 });
            var orderLine = (await seller.GetOkAsync($"/api/v1/companies/{c}/sales/orders/{order}")).GetProperty("lines")[0].GetProperty("salesOrderLineId").GetGuid();
            var delivery = Ref(await dispatch.OkAsync(c, "sales", "plan-delivery", new { salesOrderId = order, lines = new[] { new { salesOrderLineId = orderLine, quantity } } }));
            var line = (await dispatch.GetOkAsync($"/api/v1/companies/{c}/sales/deliveries/{delivery}")).GetProperty("lines")[0].GetProperty("deliveryLineId").GetGuid();
            await dispatch.OkAsync(c, "sales", "start-loading", new { deliveryId = delivery, expectedVersion = 1, vehicleId = (Guid?)null, driverId = (Guid?)null, customerVehiclePlate = plate, customerDriverName = "Pedro Díaz" });
            await dispatch.OkAsync(c, "sales", "confirm-loaded", new { deliveryId = delivery, expectedVersion = 2, lines = new[] { new { deliveryLineId = line, sourceLocationId = patio } } });
            return (delivery, line);
        }

        var (first, firstLine) = await LoadAsync("1000", "G123456");
        await dispatch.OkAsync(c, "sales", "record-gate-out", new { deliveryId = first, expectedVersion = 3, grossKg = "26000", tareKg = "8000", weighTicketRef = "TK-1", weighTicketSha256 = Hash });
        var invoice = Ref(await billing.OkAsync(c, "sales", "create-invoice-from-deliveries", new { partyId = customer, deliveryLineIds = new[] { firstLine } }));
        await billing.OkAsync(c, "sales", "issue-invoice", new { invoiceId = invoice, expectedVersion = 1 });
        var (second, _) = await LoadAsync("300", "G654321");

        // 28 days after production the lab breaks three specimens: 30,000 kg ÷ (19.5 × 39.5 = 770.25 cm²) = 38.948393 kg/cm² — below 100.
        clock.Advance(TimeSpan.FromDays(26));
        var lab = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("LABORATORIO"));
        quality = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CALIDAD"));
        dispatch = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("DESPACHO"));
        var specimen = new { loadKg = "30000", widthCm = "19.5", heightCm = "19.5", lengthCm = "39.5" };
        var recorded = (await lab.OkAsync(c, "manufacturing", "record-compression-tests", new { plantId = plant, lotId = lot, breakDate = today.AddDays(28), specimens = new[] { specimen, specimen, specimen } }))
            .GetProperty("result");
        var gateOut = new { deliveryId = second, expectedVersion = 3, grossKg = "13400", tareKg = "8000", weighTicketRef = "TK-2", weighTicketSha256 = Hash };
        var skipped = await (await dispatch.CommandAsync(c, "sales", "record-gate-out", gateOut, Guid.NewGuid().ToString())).ProblemAsync();
        var forward = await lab.GetOkAsync($"/api/v1/companies/{c}/manufacturing/lab/lots/{lot}/recall");
        var backward = await lab.GetOkAsync($"/api/v1/companies/{c}/manufacturing/lab/deliveries/{first}/recall");
        var blockedList = await lab.GetOkAsync($"/api/v1/companies/{c}/manufacturing/lab/lots?view=BLOCKED_BY_LAB");

        // Calidad unblocks with a reason (say, the customer accepts the lot for a non-structural use): the truck leaves.
        var version = forward.GetProperty("lot").GetProperty("version").GetInt64();
        await quality.OkAsync(c, "manufacturing", "unblock-lot", new { plantId = plant, lotId = lot, expectedVersion = version, reason = "Aceptado por el cliente para uso no estructural" });
        await dispatch.OkAsync(c, "sales", "record-gate-out", gateOut);

        var evaluation = recorded.GetProperty("evaluation");
        Assert.Equal("FAILS:REAL:38.948393:True", $"{evaluation.GetProperty("verdict").GetString()}:{evaluation.GetProperty("basis").GetString()}:{evaluation.GetProperty("strength28d").GetString()}:{evaluation.GetProperty("blocked").GetBoolean()}");
        Assert.Equal("STOCK_BLOCKED_BY_QUALITY", skipped.Code);
        var fieldCode = $"6{today:ddMMyy}P1-DIA";
        Assert.Equal($"{fieldCode}:BLOCKED:LAB:FAILS", string.Join(':', new[] { "fieldCode", "status", "blockCause", "verdict" }.Select(p => forward.GetProperty("lot").GetProperty(p).GetString())));
        // A pickup at the plant is delivered at the gate (E-VS3-04).
        var went = Assert.Single(forward.GetProperty("deliveries").EnumerateArray());
        Assert.Equal("DELIVERED:Constructora Uno:BLOQUE-6:1000.000000:True", string.Join(':',
            went.GetProperty("status").GetString(), went.GetProperty("customerName").GetString(), went.GetProperty("itemCode").GetString(), went.GetProperty("baseQuantity").GetString(),
            went.GetProperty("invoiceNos").GetString()!.Length > 0));
        Assert.Equal((first, 1), (went.GetProperty("deliveryId").GetGuid(), forward.GetProperty("customers").GetInt32()));
        // 1,480 produced: 1,000 left with the first truck; 480 are still in the yard (the second truck's 300 never left the location).
        Assert.Equal("1000.000000:480.000000:PATIO-A=480.000000", $"{forward.GetProperty("dispatched").GetString()}:{forward.GetProperty("inStock").GetString()}:"
            + string.Join(',', forward.GetProperty("stock").EnumerateArray().Select(s => $"{s.GetProperty("locationCode").GetString()}={s.GetProperty("quantity").GetString()}")));
        var taken = Assert.Single(backward.GetProperty("lots").EnumerateArray());
        Assert.Equal($"Constructora Uno:{fieldCode}:BESSER-1:DIA:FAILS:1000.000000:1:3", string.Join(':',
            backward.GetProperty("customerName").GetString(), taken.GetProperty("lot").GetProperty("fieldCode").GetString(), taken.GetProperty("lot").GetProperty("machineCode").GetString(),
            taken.GetProperty("lot").GetProperty("shiftCode").GetString(), taken.GetProperty("lot").GetProperty("verdict").GetString(), taken.GetProperty("baseQuantity").GetString(),
            taken.GetProperty("recipeVersion").GetInt32(), taken.GetProperty("lot").GetProperty("compressionTests").GetInt32()));
        Assert.Equal("ADITIVO-P 15.000000/15.000000|ARENA-LAVADA 18.375000/18.000000|CEMENTO-GU 1850.000000/1800.000000", string.Join('|',
            taken.GetProperty("consumption").EnumerateArray().Select(m => $"{m.GetProperty("materialCode").GetString()} {m.GetProperty("qty").GetString()}/{m.GetProperty("theoreticalQty").GetString()}")));
        Assert.Equal((1, 1), (blockedList.GetProperty("blockedByLab").GetInt32(), blockedList.GetProperty("items").GetArrayLength()));
        Assert.Equal("RELEASED:180.000000", await h.ScalarAsync<string>(
            $"SELECT (SELECT status FROM mfg.fg_lot) || ':' || (SELECT sum(b.quantity)::numeric(18,6)::text FROM inv.inv_stock_balance b JOIN md.location l ON l.location_id = b.location_id WHERE b.lot_id = '{lot}' AND l.code = 'PATIO-A')"));
    }
}
