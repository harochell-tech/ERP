using System.Globalization;
using System.Text.Json;
using Rochell.Audit;
using Rochell.Platform.Time;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>
/// MFG-1 E2E-M1 through the API (E-MFG1-06-2): machine and shift → recipe → standard cost from the recipe → run → shift summary →
/// posting (P-08, P-10) → curing (dispatch from CURADO refused, MFG-04) → release → order → pickup at the plant → invoice → collector
/// settlement (P-13) → OP-DAY, COST-SET and INV-MOV closed in that order. The raw materials' stock and what deployment puts in a real
/// environment (periods, plant, account maps, policies, the fiscal rule, the ledger seal) come from the fixtures.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ManufacturingAcceptanceTests(PostgresFixture postgres)
{
    private const string SalesItbis = """{"tax_code":"ITBIS","rate":"0.18","effect":"OUTPUT","exempt_item_categories":[]}""";
    private const string Hash = "5f70bf18a086007016e948b04aed3b82103a36bea41755b6cddfaf10ace3c6ef";

    private static Guid Ref(JsonElement response) => response.GetProperty("resultRef").GetGuid();

    [Trait("AcceptanceMfg1", "E2E-M1")]
    [Trait("AcceptanceMfg1", "MFG-04")]
    [Trait("AcceptanceMfg1", "MFG-11")]
    [Fact]
    public async Task E2EM1_recipe_standard_run_summary_curing_release_sale_settlement_and_close_over_HTTP()
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
        // Cement 15,170.00 + sand 18.375 of 110 t × 109,900.00 = 18,358.30 + admixture 750.00 = 34,278.30; standard 41,440.00.
        Assert.Equal("34278.30|41440.00|3", $"{posted.GetProperty("consumptionValue").GetString()}|{posted.GetProperty("standardValue").GetString()}|{posted.GetProperty("racks").GetInt32()}");
        var productionDay = await supervisor.GetOkAsync($"/api/v1/companies/{c}/manufacturing/production-day?plantId={plant}&businessDate={today:yyyy-MM-dd}");
        Assert.Equal("1480.000000|CEMENTO-GU:50.000000|ARENA-LAVADA:0.375000|ADITIVO-P:0.000000", string.Join('|', new[] { productionDay.GetProperty("goodUnits").GetString() }
            .Concat(productionDay.GetProperty("materials").EnumerateArray().OrderByDescending(m => m.GetProperty("materialCode").GetString(), StringComparer.Ordinal)
                .Select(m => $"{m.GetProperty("materialCode").GetString()}:{m.GetProperty("difference").GetString()}"))));

        // A customer picks up 1,000 blocks at the plant; nothing is loaded from CURADO (MFG-04).
        var prices = Ref(await controller.OkAsync(c, "sales", "prepare-price-list", new { lines = new[] { new { itemId = block, uom = "un", unitPrice = "50.00" } } }));
        await approver.OkAsync(c, "sales", "approve-price-list", new { priceListVersionId = prices });
        var customer = Ref(await seller.OkAsync(c, "sales", "create-customer", new { rnc = "131925332", legalName = "Constructora Uno" }));
        var terms = (await credit.OkAsync(c, "sales", "prepare-customer-terms", new { partyId = customer, paymentTermsDays = 30, creditLimit = "1000000.00", creditHold = false }))
            .GetProperty("result").GetProperty("termsVersionId").GetGuid();
        await controller.OkAsync(c, "sales", "approve-customer-terms", new { termsVersionId = terms });
        await credit.OkAsync(c, "sales", "activate-customer", new { partyId = customer, expectedVersion = 1 });
        var order = Ref(await seller.OkAsync(c, "sales", "create-sales-order", new
        {
            partyId = customer,
            plantId = plant,
            deliveryTermCode = "PICKUP_AT_PLANT",
            siteAddress = (string?)null,
            requestedDate = (DateOnly?)null,
            customerPoRef = (string?)null,
            lines = new[] { new { itemId = block, uom = "un", quantity = "1000" } },
        }));
        await seller.OkAsync(c, "sales", "submit-for-credit", new { salesOrderId = order, expectedVersion = 1 });
        var orderLine = (await seller.GetOkAsync($"/api/v1/companies/{c}/sales/orders/{order}")).GetProperty("lines")[0].GetProperty("salesOrderLineId").GetGuid();
        var delivery = Ref(await dispatch.OkAsync(c, "sales", "plan-delivery", new { salesOrderId = order, lines = new[] { new { salesOrderLineId = orderLine, quantity = "1000" } } }));
        var deliveryLine = (await dispatch.GetOkAsync($"/api/v1/companies/{c}/sales/deliveries/{delivery}")).GetProperty("lines")[0].GetProperty("deliveryLineId").GetGuid();
        await dispatch.OkAsync(c, "sales", "start-loading", new { deliveryId = delivery, expectedVersion = 1, vehicleId = (Guid?)null, driverId = (Guid?)null, customerVehiclePlate = "G123456", customerDriverName = "Pedro Díaz" });
        var curado = await h.ScalarAsync<Guid>("SELECT location_id FROM md.location WHERE plant_id = @p AND is_curing", ("p", plant));
        var fromCuring = await (await dispatch.CommandAsync(c, "sales", "confirm-loaded", new { deliveryId = delivery, expectedVersion = 2, lines = new[] { new { deliveryLineId = deliveryLine, sourceLocationId = curado } } }, Guid.NewGuid().ToString())).ProblemAsync();
        Assert.Equal("LOCATION_INVALID", fromCuring.Code);

        // Two days later Calidad releases the lot to PATIO-A and the truck is loaded from there.
        clock.Advance(TimeSpan.FromHours(48));
        var quality = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CALIDAD"));
        dispatch = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("DESPACHO")); // yesterday's sessions expired
        billing = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("FACTURACION"));
        await quality.OkAsync(c, "manufacturing", "release-lot", new { plantId = plant, lotId = lot, expectedVersion = 1, toLocationId = patio });
        await dispatch.OkAsync(c, "sales", "confirm-loaded", new { deliveryId = delivery, expectedVersion = 2, lines = new[] { new { deliveryLineId = deliveryLine, sourceLocationId = patio } } });
        await dispatch.OkAsync(c, "sales", "record-gate-out", new { deliveryId = delivery, expectedVersion = 3, grossKg = "26000", tareKg = "8000", weighTicketRef = "TK-1", weighTicketSha256 = Hash });
        var invoice = Ref(await billing.OkAsync(c, "sales", "create-invoice-from-deliveries", new { partyId = customer, deliveryLineIds = new[] { deliveryLine } }));
        await billing.OkAsync(c, "sales", "issue-invoice", new { invoiceId = invoice, expectedVersion = 1 });

        // After the month: the Controller settles the collector (usage 1,217.00, price 353.30) and closes OP-DAY → COST-SET → INV-MOV.
        clock.Advance(TimeSpan.FromDays(40));
        var closer = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CONTROLLER"));
        var collector = Assert.Single((await closer.GetOkAsync($"/api/v1/companies/{c}/manufacturing/cost-collectors?plantId={plant}")).GetProperty("items").EnumerateArray());
        var settled = (await closer.OkAsync(c, "manufacturing", "settle-cost-collector", new { plantId = plant, collectorId = collector.GetProperty("collectorId").GetGuid(), expectedVersion = 1 })).GetProperty("result");
        Assert.Equal("1570.30|1217.00|353.30", $"{settled.GetProperty("wipBalance").GetString()}|{settled.GetProperty("usageVariance").GetString()}|{settled.GetProperty("priceVariance").GetString()}");
        Assert.Equal("0.00|13440.00|28000.00|-50000.00|-8732.00", await h.ScalarAsync<string>(
            """
            SELECT string_agg(coalesce((SELECT sum(e.debit - e.credit) FROM fin.gl_entry e WHERE e.account_role = r.role), 0)::numeric(19,2)::text, '|' ORDER BY r.n)
            FROM (VALUES (1, 'WIP'), (2, 'FINISHED_GOODS'), (3, 'COGS'), (4, 'REVENUE_PRODUCT'), (5, 'CONVERSION_ABSORPTION')) AS r (n, role)
            """));

        await new LedgerSealer(h.Sealer, h.Clock).SealAllAsync(CancellationToken.None);
        var period = (await closer.GetOkAsync($"/api/v1/companies/{c}/reconciliation/periods?year={today.Year}")).GetProperty("items").EnumerateArray()
            .Single(p => DateOnly.Parse(p.GetProperty("startsOn").GetString()!, CultureInfo.InvariantCulture) <= today && today <= DateOnly.Parse(p.GetProperty("endsOn").GetString()!, CultureInfo.InvariantCulture))
            .GetProperty("periodId").GetGuid();
        var inventoryFirst = await (await closer.CommandAsync(c, "reconciliation", "close-component", new { periodId = period, component = "INV-MOV" }, Guid.NewGuid().ToString())).ProblemAsync();
        Assert.Equal("RECONCILIATION_ERRORS", inventoryFirst.Code);
        foreach (var component in new[] { "OP-DAY", "COST-SET", "INV-MOV" })
        {
            await closer.OkAsync(c, "reconciliation", "close-component", new { periodId = period, component });
        }

        Assert.Equal("COST-SET:CLOSED,INV-MOV:CLOSED,OP-DAY:CLOSED", await h.ScalarAsync<string>(
            $"SELECT string_agg(component || ':' || status, ',' ORDER BY component) FROM fin.close_component_state WHERE period_id = '{period}' AND component IN ('OP-DAY', 'COST-SET', 'INV-MOV')"));
    }
}
