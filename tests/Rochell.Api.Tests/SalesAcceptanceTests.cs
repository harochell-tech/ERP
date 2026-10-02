using System.Globalization;
using System.Text;
using System.Text.Json;
using Rochell.Audit;
using Rochell.Platform.Time;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>
/// VS#3 E2E-S1 through the API (E-VS3-09-7): order → own-truck delivery (loading, gate, POD) → invoice → external e-CF → transfer
/// receipt → application → bank statement → match → AR-GL, CONTRACT-ASSET, RECEIPT-APPL, BANK-GL → AR-REC and BANK-REC closed after
/// the month ends. Masters, standard cost, prices and the opening stock go through their commands; what deployment or time puts in a
/// real environment (periods, plant, account maps, policies, the active fiscal rule, the ledger seal) comes from the fixtures.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SalesAcceptanceTests(PostgresFixture postgres)
{
    private const string SalesItbis = """{"tax_code":"ITBIS","rate":"0.18","effect":"OUTPUT","exempt_item_categories":[]}""";
    private const string Hash = "5f70bf18a086007016e948b04aed3b82103a36bea41755b6cddfaf10ace3c6ef";

    private static Guid Ref(JsonElement response) => response.GetProperty("resultRef").GetGuid();

    [Trait("AcceptanceVs3", "E2E-S1")]
    [Fact]
    public async Task E2ES1_order_delivery_POD_invoice_eCF_receipt_and_match_close_AR_REC_and_BANK_REC_over_HTTP()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        using var api = new ApiHost(h, clock);
        var c = h.CompanyId;
        var today = BusinessCalendar.DefaultBusinessDate(clock.UtcNow);

        // Deployment: periods, plant and locations, account maps, policies, the active SALES_ITBIS rule.
        for (var y = today.Year - 1; y <= today.Year + 1; y++)
        {
            await h.OpenPeriodsAsync(y);
        }

        var plant = await h.CreatePlantAsync(code: "HIGUEY");
        var patio = await h.CreateLocationAsync(plant, "PATIO");
        var area = await h.ScalarAsync<Guid>("SELECT valuation_area_id FROM md.plant WHERE plant_id = @p", ("p", plant));
        foreach (var (role, code, control) in new[]
        {
            ("FINISHED_GOODS", "1350", true), ("FINISHED_GOODS_IN_TRANSIT", "1351", true), ("MIGRATION_CLEARING", "3990", false), ("COGS", "5100", false),
            ("CONTRACT_ASSET", "1240", true), ("REVENUE_PRODUCT", "4100", false), ("TRANSIT_LOSS", "6900", false), ("AR_CONTROL", "1210", true),
            ("ITBIS_PAYABLE", "2150", false), ("UNAPPLIED_RECEIPTS", "2120", true),
        })
        {
            await h.CreateActiveMapAsync(role, await h.CreateAccountAsync(code, role, control));
        }

        await h.CreateAccountAsync("1101", "Banco de prueba", isControl: true);
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
        var actors = await h.FiscalActorsAsync();
        await h.ActivateRuleAsync(actors, "itbis-ventas", "ITBIS_VENTAS", FiscalRuleKinds.SalesItbis, SalesItbis, new DateOnly(2026, 1, 1));

        // The people, each signed in through OIDC.
        var controllerSession = await h.SessionWithRolesAsync("CONTROLLER");
        var controller = await api.SignInAsSessionUserAsync(controllerSession);
        var approver = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("APROBADOR_POLITICAS"));
        var storekeeper = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("ALMACENISTA"));
        var seller = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("VENDEDOR"));
        var credit = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CREDITO"));
        var dispatch = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("DESPACHO"));
        var billing = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("FACTURACION"));
        var cobros = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("COBROS"));
        var treasurer = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("TESORERO"));

        // The rules, approved by the Controller.
        foreach (var rule in new[] { "OPEN-INV", "P-15", "P-15R", "P-16", "P-30", "P-18", "P-23", "P-25" })
        {
            await controller.OkAsync(c, "finance", "approve-posting-rule-version", new { ruleCode = rule, version = 1 });
        }

        // Masters: the product, its standard cost and price, the opening stock, the customer, the truck and its driver.
        var block = Ref(await storekeeper.OkAsync(c, "master-data", "create-finished-good", new { code = "BLOQUE-6", description = "Bloque de 6 pulgadas", baseUom = "un", itemCategory = "BLOQUE" }));
        await controller.OkAsync(c, "master-data", "activate-item", new { itemId = block, expectedVersion = 1 });
        var cost = (await controller.OkAsync(c, "sales", "prepare-standard-cost", new { itemId = block, valuationAreaId = area, unitCost = "32.75" }))
            .GetProperty("result").GetProperty("costVersionId").GetGuid();
        await approver.OkAsync(c, "sales", "approve-standard-cost", new { costVersionId = cost });
        var opening = Ref(await controller.OkAsync(c, "sales", "prepare-opening-inventory", new
        {
            fileName = "apertura.csv",
            contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("planta,ubicacion,producto,cantidad,documento\nHIGUEY,PATIO,BLOQUE-6,2000,ADM-1\n")),
            cutoverDate = new DateOnly(today.Year, today.Month, 1),
        }));
        await approver.OkAsync(c, "sales", "post-opening-inventory", new { batchId = opening, expectedVersion = 1 });
        var prices = Ref(await controller.OkAsync(c, "sales", "prepare-price-list", new { lines = new[] { new { itemId = block, uom = "un", unitPrice = "50.00" } } }));
        await approver.OkAsync(c, "sales", "approve-price-list", new { priceListVersionId = prices });
        var customer = Ref(await seller.OkAsync(c, "sales", "create-customer", new { rnc = "131925332", legalName = "Constructora Uno" }));
        var terms = (await credit.OkAsync(c, "sales", "prepare-customer-terms", new { partyId = customer, paymentTermsDays = 30, creditLimit = "1000000.00", creditHold = false }))
            .GetProperty("result").GetProperty("termsVersionId").GetGuid();
        await controller.OkAsync(c, "sales", "approve-customer-terms", new { termsVersionId = terms });
        await credit.OkAsync(c, "sales", "activate-customer", new { partyId = customer, expectedVersion = 1 });
        var truck = Ref(await dispatch.OkAsync(c, "sales", "register-vehicle", new { plate = "L123456", fleetCode = "BR 09", capacityKg = "12000" }));
        var driver = Ref(await dispatch.OkAsync(c, "sales", "register-driver", new { fullName = "Juan Pérez", nationalId = "00112345678" }));

        // Order of 1,000 blocks at 50.00, delivered at the site with our truck: credit auto-approved.
        var order = Ref(await seller.OkAsync(c, "sales", "create-sales-order", new
        {
            partyId = customer,
            plantId = plant,
            deliveryTermCode = "DELIVERED_OWN_TRANSPORT",
            siteAddress = "Obra Punta Cana",
            requestedDate = (DateOnly?)null,
            customerPoRef = (string?)null,
            lines = new[] { new { itemId = block, uom = "un", quantity = "1000" } },
        }));
        await seller.OkAsync(c, "sales", "submit-for-credit", new { salesOrderId = order, expectedVersion = 1 });
        var orderDetail = await seller.GetOkAsync($"/api/v1/companies/{c}/sales/orders/{order}");
        Assert.Equal("CONFIRMED", orderDetail.GetProperty("header").GetProperty("status").GetString());
        var orderLine = orderDetail.GetProperty("lines")[0].GetProperty("salesOrderLineId").GetGuid();

        // Delivery: plan, load, gate (P-15 to transit), POD (P-16: cost, revenue, contract asset).
        var delivery = Ref(await dispatch.OkAsync(c, "sales", "plan-delivery", new { salesOrderId = order, lines = new[] { new { salesOrderLineId = orderLine, quantity = "1000" } } }));
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
            evidenceRef = "pod-001.jpg",
            evidenceSha256 = Hash,
            lines = new[] { new { deliveryLineId = deliveryLine, qtyReceived = "1000", qtyReturned = "0" } },
            exceptionReason = (string?)null,
        });
        Assert.Equal("DELIVERED", (await dispatch.GetOkAsync($"/api/v1/companies/{c}/sales/deliveries/{delivery}")).GetProperty("header").GetProperty("status").GetString());

        // Invoice (P-18: 50,000.00 + 9,000.00 ITBIS) and its e-CF from the provider's portal.
        var invoice = Ref(await billing.OkAsync(c, "sales", "create-invoice-from-deliveries", new { partyId = customer, deliveryLineIds = new[] { deliveryLine } }));
        await billing.OkAsync(c, "sales", "issue-invoice", new { invoiceId = invoice, expectedVersion = 1 });
        var package = await billing.GetOkAsync($"/api/v1/companies/{c}/sales/invoices/{invoice}/fiscal-package");
        Assert.Equal("31|59000.00", $"{package.GetProperty("ecfType").GetString()}|{package.GetProperty("total").GetString()}");
        await billing.OkAsync(c, "sales", "record-external-fiscal-document", new
        {
            invoiceId = invoice,
            expectedVersion = 2,
            encf = "E310000000001",
            issuedAt = clock.UtcNow.AddMinutes(-1),
            securityCode = "A1B2C3",
            evidenceRef = "e-cf-FA-000001.xml",
            evidenceSha256 = Hash,
            receiverRnc = "131925332",
            netTotal = "50000.00",
            taxTotal = "9000.00",
            total = "59000.00",
        });

        // The customer pays by transfer to our account; Cobros applies it and the invoice is PAID.
        var bank = Ref(await controller.OkAsync(c, "treasury", "register-bank-account", new { bankCode = "TEST_BANK", accountNumber = "0123456789", glAccountCode = "1101" }));
        var receipt = Ref(await cobros.OkAsync(c, "sales", "record-receipt", new { partyId = customer, method = "TRANSFER", amount = "59000.00", valueDate = today, bankAccountId = bank, reference = "TRF-77" }));
        var open = await cobros.GetOkAsync($"/api/v1/companies/{c}/sales/invoices?partyId={customer}&openOnly=true");
        Assert.Equal("FA-000001|59000.00", $"{open.GetProperty("items")[0].GetProperty("invoiceNo").GetString()}|{open.GetProperty("items")[0].GetProperty("openAmount").GetString()}");
        await cobros.OkAsync(c, "sales", "apply-receipt", new { receiptId = receipt, expectedVersion = 1, applications = new[] { new { invoiceId = invoice, amount = "59000.00" } } });
        var paid = (await billing.GetOkAsync($"/api/v1/companies/{c}/sales/invoices/{invoice}")).GetProperty("header");
        Assert.Equal("PAID|ACCEPTED_EXTERNAL", $"{paid.GetProperty("commercialStatus").GetString()}|{paid.GetProperty("fiscalStatus").GetString()}");

        // The bank statement shows the transfer; the treasurer matches it to the receipt.
        var monthEnd = new DateOnly(today.Year, today.Month, 1).AddMonths(1).AddDays(-1);
        var csv = $"Fecha,Referencia,Descripcion,Debito,Credito\n{today:dd/MM/yyyy},TRF-77,Transferencia Constructora Uno,,59000.00\n";
        await treasurer.OkAsync(c, "treasury", "import-bank-statement", new
        {
            bankAccountId = bank,
            fileName = "extracto.csv",
            contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(csv)),
            periodFrom = today,
            periodTo = monthEnd,
            openingBalance = "0.00",
            closingBalance = "59000.00",
        });
        var line = Assert.Single((await treasurer.GetOkAsync($"/api/v1/companies/{c}/treasury/bank-statement-lines?status=UNMATCHED")).GetProperty("items").EnumerateArray()).GetProperty("lineId").GetGuid();
        await treasurer.OkAsync(c, "treasury", "match-bank-line-to-receipt", new { lineId = line, expectedLineVersion = 1, receiptId = receipt, expectedVersion = 2 });

        // Read side: the receipt, the aging (nothing open), the statement of account (and its CSV).
        var receiptDetail = await cobros.GetOkAsync($"/api/v1/companies/{c}/sales/receipts/{receipt}");
        Assert.Equal("RECORDED|APPLIED|MATCHED", $"{receiptDetail.GetProperty("header").GetProperty("status").GetString()}|{receiptDetail.GetProperty("header").GetProperty("applicationStatus").GetString()}|{receiptDetail.GetProperty("header").GetProperty("bankStatus").GetString()}");
        var aging = await cobros.GetOkAsync($"/api/v1/companies/{c}/sales/ar-aging?asOf={today:yyyy-MM-dd}");
        Assert.Equal("0.00|0", $"{aging.GetProperty("net").GetString()}|{aging.GetProperty("customers").GetArrayLength()}");
        var statement = await cobros.GetOkAsync($"/api/v1/companies/{c}/sales/customers/{customer}/statement?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        Assert.Equal("FACTURA:59000.00|COBRO:0.00", string.Join('|', statement.GetProperty("entries").EnumerateArray().Select(e => $"{e.GetProperty("kind").GetString()}:{e.GetProperty("balance").GetString()}")));
        var file = await cobros.GetAsync($"/api/v1/companies/{c}/sales/customers/{customer}/statement?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}&format=csv");
        Assert.Equal("text/csv", file.Content.Headers.ContentType?.MediaType);
        Assert.Contains(",FACTURA,FA-000001,59000.00,0.00,59000.00", await file.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The month reconciles.
        var run = await controller.OkAsync(c, "reconciliation", "run-reconciliation", new { reconCodes = new[] { "AR-GL", "CONTRACT-ASSET", "RECEIPT-APPL", "FISC-DOC", "BANK-GL" } });
        Assert.Equal("AR-GL:MATCHED,BANK-GL:MATCHED,CONTRACT-ASSET:MATCHED,FISC-DOC:MATCHED,RECEIPT-APPL:MATCHED", string.Join(',', run.GetProperty("result").GetProperty("runs").EnumerateArray()
            .Select(r => $"{r.GetProperty("code").GetString()}:{r.GetProperty("status").GetString()}").Order(StringComparer.Ordinal)));
        Assert.Equal("0.00|0.00|59000.00|-50000.00|-9000.00", await h.ScalarAsync<string>(
            """
            SELECT string_agg(coalesce((SELECT sum(e.debit - e.credit) FROM fin.gl_entry e WHERE e.account_role = r.role), 0)::numeric(19,2)::text, '|' ORDER BY r.n)
            FROM (VALUES (1, 'AR_CONTROL'), (2, 'CONTRACT_ASSET'), (3, 'BANK'), (4, 'REVENUE_PRODUCT'), (5, 'ITBIS_PAYABLE')) AS r (n, role)
            """));

        // After the month ends: seal, and the Controller closes AR-REC and BANK-REC of the month.
        clock.Advance(TimeSpan.FromDays(40));
        var closer = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CONTROLLER"));
        await new LedgerSealer(h.Sealer, h.Clock).SealAllAsync(CancellationToken.None);
        var period = (await closer.GetOkAsync($"/api/v1/companies/{c}/reconciliation/periods?year={today.Year}")).GetProperty("items").EnumerateArray()
            .Single(p => DateOnly.Parse(p.GetProperty("startsOn").GetString()!, CultureInfo.InvariantCulture) <= today && today <= DateOnly.Parse(p.GetProperty("endsOn").GetString()!, CultureInfo.InvariantCulture))
            .GetProperty("periodId").GetGuid();
        await closer.OkAsync(c, "reconciliation", "close-component", new { periodId = period, component = "AR-REC" });
        await closer.OkAsync(c, "reconciliation", "close-component", new { periodId = period, component = "BANK-REC" });
        Assert.Equal("AR-REC:CLOSED,BANK-REC:CLOSED", await h.ScalarAsync<string>(
            $"SELECT string_agg(component || ':' || status, ',' ORDER BY component) FROM fin.close_component_state WHERE period_id = '{period}' AND component IN ('AR-REC', 'BANK-REC')"));
    }
}
