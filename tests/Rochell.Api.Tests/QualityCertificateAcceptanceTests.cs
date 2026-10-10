using System.Text.Json;
using Rochell.Platform.Time;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>
/// LAB1-03 through the API (E-LAB1-03-2…13, 16): two lots of the same block in the yard. A truck whose loader scanned the newer lot's rack
/// leaves with that lot; one without a scan leaves FIFO; one whose scanned lot was blocked meanwhile does not leave. The rack labels
/// print one per rack with the lot's QR; Calidad issues the certificate of a break date with a delivery's customer, a second one is -2,
/// and voiding one of its specimens voids both.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class QualityCertificateAcceptanceTests(PostgresFixture postgres)
{
    private const string SalesItbis = """{"tax_code":"ITBIS","rate":"0.18","effect":"OUTPUT","exempt_item_categories":[]}""";
    private const string Hash = "5f70bf18a086007016e948b04aed3b82103a36bea41755b6cddfaf10ace3c6ef";

    private static Guid Ref(JsonElement response) => response.GetProperty("resultRef").GetGuid();

    [Trait("AcceptanceLab1", "LAB-13")]
    [Trait("AcceptanceLab1", "LAB-15")]
    [Fact]
    public async Task A_scanned_rack_chooses_the_lot_at_the_gate_and_the_certificate_prints_what_the_lab_tested_over_HTTP()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        using var api = new ApiHost(h, clock);
        var c = h.CompanyId;
        var day0 = BusinessCalendar.DefaultBusinessDate(clock.UtcNow);

        var stock = await h.CreateStockSetupAsync();
        var plant = stock.PlantId;
        var patio = stock.LocationA;
        var sand = stock.ItemId;
        var cement = await h.CreateActiveItemAsync("CEMENTO-GU", "kg", "CEMENTO");
        await h.RunAsync(new TestReceiveStock(c, h.SessionId, "r-cement", patio, cement, 20000m, 164000.00m, day0), new TestReceiveStockHandler());
        await h.RunAsync(new TestReceiveStock(c, h.SessionId, "r-sand", patio, sand, 110m, 109900.00m, day0), new TestReceiveStockHandler());
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

        var controller = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CONTROLLER"));
        var approver = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("APROBADOR_POLITICAS"));
        var storekeeper = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("ALMACENISTA"));
        var manager = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("GERENTE_PLANTA"));
        var supervisor = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("SUPERVISOR_PRODUCCION"));
        var seller = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("VENDEDOR"));
        var credit = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CREDITO"));
        foreach (var rule in new[] { "P-08", "P-10", "P-13", "P-15", "P-15R", "P-16", "P-30" })
        {
            await controller.OkAsync(c, "finance", "approve-posting-rule-version", new { ruleCode = rule, version = 1 });
        }

        var machine = Ref(await manager.OkAsync(c, "manufacturing", "create-machine", new { plantId = plant, code = "BESSER-1", name = "Besser V3-12" }));
        var shift = Ref(await manager.OkAsync(c, "manufacturing", "define-shift", new { plantId = plant, code = "DIA", startsAt = "07:00:00", endsAt = "19:00:00" }));
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
            lines = new[] { new { materialItemId = cement, qtyPerBatch = "180" }, new { materialItemId = sand, qtyPerBatch = "1.8" } },
        }));
        await manager.OkAsync(c, "manufacturing", "approve-recipe", new { plantId = plant, recipeVersionId = recipe });
        var cost = (await controller.OkAsync(c, "sales", "prepare-standard-cost-from-recipe", new
        {
            recipeVersionId = recipe,
            materialPrices = new[] { new { materialItemId = cement, stdPrice = "8.00" }, new { materialItemId = sand, stdPrice = "1000.00" } },
            conversionCost = "6.40",
        })).GetProperty("result");
        await approver.OkAsync(c, "sales", "approve-standard-cost", new { costVersionId = cost.GetProperty("costVersionId").GetGuid() });

        var prices = Ref(await controller.OkAsync(c, "sales", "prepare-price-list", new { lines = new[] { new { itemId = block, uom = "un", unitPrice = "50.00" } } }));
        await approver.OkAsync(c, "sales", "approve-price-list", new { priceListVersionId = prices });
        var customer = Ref(await seller.OkAsync(c, "sales", "create-customer", new { rnc = "131925332", legalName = "Constructora Uno" }));
        var terms = (await credit.OkAsync(c, "sales", "prepare-customer-terms", new { partyId = customer, paymentTermsDays = 30, creditLimit = "1000000.00", creditHold = false }))
            .GetProperty("result").GetProperty("termsVersionId").GetGuid();
        await controller.OkAsync(c, "sales", "approve-customer-terms", new { termsVersionId = terms });
        await credit.OkAsync(c, "sales", "activate-customer", new { partyId = customer, expectedVersion = 1 });

        var quality = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CALIDAD"));
        await quality.OkAsync(c, "manufacturing", "set-item-spec", new
        {
            itemId = block,
            lotPrefix = "6",
            nominalWidthCm = "19.5",
            nominalHeightCm = "19.5",
            nominalLengthCm = "39.5",
            netAreaFraction = (string?)null,
            minAvg28d = "50",
            minIndividual28d = (string?)null,
        });
        await quality.OkAsync(c, "manufacturing", "set-machine-short-code", new { plantId = plant, machineId = machine, expectedVersion = 1, shortCode = "P1" });

        // Two runs on two days: lot A (older) and lot B, 1,480 blocks each — three racks of 600, 600 and 280.
        async Task<Guid> ProduceAsync(DateOnly date)
        {
            var run = Ref(await supervisor.OkAsync(c, "manufacturing", "start-production-run", new { plantId = plant, machineId = machine, shiftId = shift, businessDate = date, itemId = block }));
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
                    new { materialItemId = cement, locationId = patio, quantity = "1800", uom = "kg" },
                    new { materialItemId = sand, locationId = patio, quantity = "18", uom = "t" },
                },
            });
            return (await manager.OkAsync(c, "manufacturing", "post-shift-summary", new { plantId = plant, runId = run, expectedVersion = 1 })).GetProperty("result").GetProperty("lotId").GetGuid();
        }

        var lotA = await ProduceAsync(day0);
        clock.Advance(TimeSpan.FromHours(24));
        supervisor = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("SUPERVISOR_PRODUCCION"));
        manager = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("GERENTE_PLANTA"));
        var lotB = await ProduceAsync(day0.AddDays(1));
        var (codeA, codeB) = ($"6{day0:ddMMyy}P1-DIA", $"6{day0.AddDays(1):ddMMyy}P1-DIA");

        clock.Advance(TimeSpan.FromHours(48));
        quality = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CALIDAD"));
        seller = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("VENDEDOR"));
        var dispatch = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("DESPACHO"));
        supervisor = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("SUPERVISOR_PRODUCCION"));
        await quality.OkAsync(c, "manufacturing", "release-lot", new { plantId = plant, lotId = lotA, expectedVersion = 1, toLocationId = patio });
        await quality.OkAsync(c, "manufacturing", "release-lot", new { plantId = plant, lotId = lotB, expectedVersion = 1, toLocationId = patio });

        // E-LAB1-03-9/10: the labels of lot B, one per rack, each with the lot's address and its rack (production:read).
        var labels = await supervisor.GetOkAsync($"/api/v1/companies/{c}/manufacturing/lots/{lotB}/rack-labels");
        var oneLabel = await supervisor.GetOkAsync($"/api/v1/companies/{c}/manufacturing/lots/{lotB}/rack-labels?rack=2");
        var labelBody = labels.GetProperty("body").GetString()!;

        async Task<(Guid Delivery, Guid Line)> PlanAsync(string quantity, string plate)
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
            return (delivery, line);
        }

        object Loaded(Guid delivery, Guid line, object[] scans) => new { deliveryId = delivery, expectedVersion = 2, lines = new[] { new { deliveryLineId = line, sourceLocationId = patio, scans } } };
        async Task<(Guid Delivery, Guid Line)> LoadAsync(string quantity, string plate, object[] scans)
        {
            var (delivery, line) = await PlanAsync(quantity, plate);
            await dispatch.OkAsync(c, "sales", "confirm-loaded", Loaded(delivery, line, scans));
            return (delivery, line);
        }

        object GateOut(Guid delivery, string ticket) => new { deliveryId = delivery, expectedVersion = 3, grossKg = "16000", tareKg = "8000", weighTicketRef = ticket, weighTicketSha256 = Hash };
        async Task<string> LotsOfAsync(Guid delivery) => string.Join(',', (await dispatch.GetOkAsync($"/api/v1/companies/{c}/sales/deliveries/{delivery}")).GetProperty("lines")[0].GetProperty("lots")
            .EnumerateArray().Select(l => $"{l.GetProperty("lotCode").GetString()}={l.GetProperty("baseQuantity").GetString()}"));

        // LAB-13: the first truck scanned lot B's rack 2 (twice: the lot counts once) — it leaves with lot B, though lot A is older.
        var (scannedTruck, scannedLine) = await LoadAsync("600", "G100001", [new { lotId = lotB, rackNo = 2 }, new { lotId = lotB, rackNo = 3 }]);
        await dispatch.OkAsync(c, "sales", "record-gate-out", GateOut(scannedTruck, "TK-1"));
        // Without a scan, FIFO: lot A.
        var (fifoTruck, _) = await LoadAsync("300", "G100002", []);
        await dispatch.OkAsync(c, "sales", "record-gate-out", GateOut(fifoTruck, "TK-2"));
        // E-LAB1-03-16: a truck loaded with lot A by scan; Calidad blocks lot A before it leaves — the gate refuses, it does not swap the lot.
        var (blockedTruck, _) = await LoadAsync("100", "G100003", [new { lotId = lotA }]);
        var versionA = (await quality.GetOkAsync($"/api/v1/companies/{c}/manufacturing/lab/lots/{lotA}")).GetProperty("lot").GetProperty("version").GetInt64();
        await quality.OkAsync(c, "manufacturing", "block-lot", new { plantId = plant, lotId = lotA, expectedVersion = versionA, reason = "Revisión de fisuras" });
        var refused = await (await dispatch.CommandAsync(c, "sales", "record-gate-out", GateOut(blockedTruck, "TK-3"), Guid.NewGuid().ToString())).ProblemAsync();
        // Nor can a blocked lot be scanned onto another truck.
        var (fourth, fourthLine) = await PlanAsync("100", "G100004");
        var scanBlocked = await (await dispatch.CommandAsync(c, "sales", "confirm-loaded", Loaded(fourth, fourthLine, [new { lotId = lotA }]), Guid.NewGuid().ToString())).ProblemAsync();

        Assert.Equal($"{codeB}=600.000000", await LotsOfAsync(scannedTruck));
        Assert.Equal($"{codeA}=300.000000", await LotsOfAsync(fifoTruck));
        Assert.Equal("STOCK_BLOCKED_BY_QUALITY", refused.Code);
        Assert.Equal("STOCK_BLOCKED_BY_QUALITY", scanBlocked.Code);
        Assert.Equal("1:2", await h.ScalarAsync<string>($"SELECT string_agg(seq || ':' || rack_no, ',' ORDER BY seq) FROM log.delivery_line_scan WHERE delivery_line_id = '{scannedLine}'"));
        Assert.Equal(3, labels.GetProperty("body").GetString()!.Split("data-testid=\"rack-label\"").Length - 1);
        Assert.Contains(codeB, labelBody, StringComparison.Ordinal);
        Assert.Contains($"/calidad/lotes/?lote={lotB:D}&amp;rack=3", labelBody, StringComparison.Ordinal);
        Assert.Contains("<svg", labelBody, StringComparison.Ordinal);
        Assert.Equal(1, oneLabel.GetProperty("body").GetString()!.Split("data-testid=\"rack-label\"").Length - 1);
        Assert.Equal("RACK_LABEL", labels.GetProperty("documentType").GetString());

        // LAB-15: 28 days after lot B was made the lab breaks three specimens; Calidad issues the certificate with the scanned truck's customer.
        clock.Advance(TimeSpan.FromDays(26));
        var breakDate = day0.AddDays(29);
        var lab = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("LABORATORIO"));
        quality = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CALIDAD"));
        var tested = (await lab.OkAsync(c, "manufacturing", "record-compression-tests", new
        {
            plantId = plant,
            lotId = lotB,
            breakDate,
            specimens = new object[]
            {
                new { loadKg = "60000", widthCm = "19.5", heightCm = "19.5", lengthCm = "39.5", blockCondition = "SECO_AL_AIRE", failureType = "CONICA" },
                new { loadKg = "62000", widthCm = "19.5", heightCm = "19.5", lengthCm = "39.5", blockCondition = "SECO_AL_AIRE", failureType = "CONICA" },
                new { loadKg = "58000", blockCondition = "HUMEDO", failureType = "COLUMNAR" },
            },
        })).GetProperty("result");
        var issued = (await quality.OkAsync(c, "manufacturing", "issue-lab-certificate", new { plantId = plant, lotId = lotB, breakDate, deliveryId = scannedTruck })).GetProperty("result");
        var again = (await quality.OkAsync(c, "manufacturing", "issue-lab-certificate", new { plantId = plant, lotId = lotB, breakDate })).GetProperty("result");
        var wrongTruck = await (await quality.CommandAsync(c, "manufacturing", "issue-lab-certificate", new { plantId = plant, lotId = lotB, breakDate, deliveryId = fifoTruck }, Guid.NewGuid().ToString())).ProblemAsync();
        var noSpecimens = await (await quality.CommandAsync(c, "manufacturing", "issue-lab-certificate", new { plantId = plant, lotId = lotB, breakDate = breakDate.AddDays(-1) }, Guid.NewGuid().ToString())).ProblemAsync();
        var certificate = issued.GetProperty("certificateId").GetGuid();
        // E-LAB1-03-8, 15: the QR's page, without sign-in, answered as «Verificación pública».
        await h.GrantAsync(c, Rochell.Identity.IdentityConstants.PublicVerificationUserId, "VERIFICACION_PUBLICA");
        var publicCode = issued.GetProperty("publicCode").GetString()!;
        var anonymous = api.Browser();
        var verified = JsonDocument.Parse(await (await anonymous.GetAsync($"/api/v1/public/lab-certificates/{c}/{publicCode}")).Content.ReadAsStringAsync()).RootElement;
        var unknown = await anonymous.GetAsync($"/api/v1/public/lab-certificates/{c}/{new string('a', 24)}");
        var printed = await lab.GetOkAsync($"/api/v1/companies/{c}/manufacturing/lab/certificates/{certificate}/print");
        var body = printed.GetProperty("body").GetString()!;

        var number = $"CR-{codeB}-{breakDate:ddMMyy}";
        Assert.Equal($"{number}:{number}-2:ISSUED", $"{issued.GetProperty("certificateNo").GetString()}:{again.GetProperty("certificateNo").GetString()}:{issued.GetProperty("status").GetString()}");
        Assert.Equal(("LAB_CERTIFICATE_REFUSED", "LAB_CERTIFICATE_REFUSED"), (wrongTruck.Code, noSpecimens.Code));
        Assert.Equal($"{number}:{codeB}:BLOQUE-6 — Bloque de 6 pulgadas:3:77.90:7.64:75.30:3.33:ISSUED", string.Join(':',
            verified.GetProperty("certificateNo").GetString(), verified.GetProperty("lot").GetString(), verified.GetProperty("product").GetString(), verified.GetProperty("specimens").GetInt32(),
            verified.GetProperty("avgKgcm2").GetString(), verified.GetProperty("avgMpa").GetString(), verified.GetProperty("minKgcm2").GetString(), verified.GetProperty("cvPercent").GetString(),
            verified.GetProperty("status").GetString()));
        Assert.DoesNotContain("Constructora Uno", verified.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains(number, body, StringComparison.Ordinal);
        Assert.Contains("Constructora Uno", body, StringComparison.Ordinal);
        Assert.Contains("Resistencia calculada sobre área bruta. Resultados válidos solo para las unidades ensayadas.", body, StringComparison.Ordinal);
        Assert.Contains("TEST MARK CM-2500-iD, serie 220808", body, StringComparison.Ordinal);
        Assert.Contains("Ing. Alexander Rochell", body, StringComparison.Ordinal);
        Assert.Contains("/verificar/certificado/?c=", body, StringComparison.Ordinal);
        Assert.Equal(3, body.Split("data-testid=\"certificate-specimen\"").Length - 1);
        // 60,000 / 62,000 / 58,000 kg ÷ 770.25 cm² = 77.90 / 80.49 / 75.30 kg/cm²; average 77.90, × 0.0980665 = 7.64 MPa; CV = 3.33 %.
        Assert.Contains("77.90 kg/cm² · 7.64 MPa", body, StringComparison.Ordinal);
        Assert.Contains("75.30 kg/cm²", body, StringComparison.Ordinal);
        Assert.Contains("3.33 %", body, StringComparison.Ordinal);
        Assert.Contains("19.5 × 19.5 × 39.5 (nominales)", body, StringComparison.Ordinal);
        Assert.Contains("Seco al aire", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ANULADO", body, StringComparison.Ordinal);

        // E-LAB1-03-5: voiding the third specimen voids both certificates; the print says ANULADO.
        var third = tested.GetProperty("tests")[2].GetProperty("testId").GetGuid();
        var voided = (await lab.OkAsync(c, "manufacturing", "void-compression-test", new { plantId = plant, testId = third, reason = "Probeta mal refrentada" })).GetProperty("result");
        var detail = await quality.GetOkAsync($"/api/v1/companies/{c}/manufacturing/lab/lots/{lotB}");
        var reprinted = (await lab.GetOkAsync($"/api/v1/companies/{c}/manufacturing/lab/certificates/{certificate}/print")).GetProperty("body").GetString()!;

        Assert.Equal($"{number},{number}-2", string.Join(',', voided.GetProperty("certificatesVoided").EnumerateArray().Select(x => x.GetString())));
        Assert.Equal($"{number}:VOIDED:SPECIMEN_VOIDED:3:Constructora Uno|{number}-2:VOIDED:SPECIMEN_VOIDED:3:", string.Join('|', detail.GetProperty("certificates").EnumerateArray().Select(x => string.Join(':',
            x.GetProperty("certificateNo").GetString(), x.GetProperty("status").GetString(), x.GetProperty("voidCause").GetString(), x.GetProperty("specimens").GetInt32(),
            x.GetProperty("customerName").GetString()))));
        Assert.Contains("ANULADO", reprinted, StringComparison.Ordinal);
        var afterVoid = JsonDocument.Parse(await (await anonymous.GetAsync($"/api/v1/public/lab-certificates/{c}/{publicCode}")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("VOIDED", afterVoid.GetProperty("status").GetString());
    }
}
