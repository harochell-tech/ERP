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
/// CF-1 E2E-C1 through the API (E-CF1-05-11): Caja sells 100 blocks to a final consumer → the sale is sent to payment for the net
/// plus the ITBIS of the day → the transfer is recorded and assigned, which confirms it → two pickups → each invoiced as an e-CF 32,
/// born paid from what was assigned to the order → the bank statement matches the transfer → the month reconciles (CASH-SALE among
/// the others) and AR-REC / BANK-REC close. The deployment and masters are those of E2E-S1.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class CashSaleAcceptanceTests(PostgresFixture postgres)
{
    private const string SalesItbis = """{"tax_code":"ITBIS","rate":"0.18","effect":"OUTPUT","exempt_item_categories":[]}""";
    private const string Hash = "5f70bf18a086007016e948b04aed3b82103a36bea41755b6cddfaf10ace3c6ef";

    private static Guid Ref(JsonElement response) => response.GetProperty("resultRef").GetGuid();

    private static string Money(JsonElement row, params string[] names) => string.Join('|', names.Select(n => row.GetProperty(n).GetString()));

    [Trait("AcceptanceCf1", "E2E-C1")]
    [Fact]
    public async Task E2EC1_cash_sale_paid_picked_up_twice_invoiced_as_eCF_32_and_the_month_closed_over_HTTP()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        using var api = new ApiHost(h, clock);
        var c = h.CompanyId;
        var today = BusinessCalendar.DefaultBusinessDate(clock.UtcNow);

        // Deployment: periods, plant and locations, account maps, policies, the active SALES_ITBIS and consumer identification rules.
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
            ["cash_deposit_alert_days"] = "2",
        });
        var actors = await h.FiscalActorsAsync();
        await h.ActivateRuleAsync(actors, "itbis-ventas", "ITBIS_VENTAS", FiscalRuleKinds.SalesItbis, SalesItbis, new DateOnly(2026, 1, 1));
        await h.ActivateRuleAsync(actors, "consumidor-id", "CONSUMIDOR_ID", FiscalRuleKinds.ConsumerIdThreshold, """{"amount":"250000.00"}""", new DateOnly(2026, 1, 1));

        // The people, each signed in through OIDC. Caja sells and collects; Despacho, Facturación and Tesorería do their own part.
        var controller = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CONTROLLER"));
        var approver = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("APROBADOR_POLITICAS"));
        var storekeeper = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("ALMACENISTA"));
        var caja = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CAJA"));
        var dispatch = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("DESPACHO"));
        var billing = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("FACTURACION"));
        var treasurer = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("TESORERO"));
        foreach (var rule in new[] { "OPEN-INV", "P-15", "P-15R", "P-16", "P-30", "P-18", "P-23", "P-25" })
        {
            await controller.OkAsync(c, "finance", "approve-posting-rule-version", new { ruleCode = rule, version = 1 });
        }

        // Masters: the product, its standard cost and price, the opening stock and the receipts' bank account. No customer: the
        // final consumer is created by the first sale.
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
        var bank = Ref(await controller.OkAsync(c, "treasury", "register-bank-account", new { bankCode = "TEST_BANK", accountNumber = "0123456789", glAccountCode = "1101" }));

        // What the cashier sees before selling: the identification amount and the sale priced by the server (100 × 50.00 + 18 %).
        var setup = await caja.GetOkAsync($"/api/v1/companies/{c}/sales/cash-sale-setup");
        Assert.Equal("250000.00", setup.GetProperty("buyerIdRequiredFrom").GetString());
        var preview = await caja.PostAsync(
            $"/api/v1/companies/{c}/sales/cash-sales/preview",
            new StringContent($$"""{"plantId":"{{plant}}","lines":[{"itemId":"{{block}}","uom":"un","quantity":"100"}]}""", Encoding.UTF8, "application/json"));
        var previewText = await preview.Content.ReadAsStringAsync();
        Assert.True(preview.IsSuccessStatusCode, previewText);
        Assert.Contains("\"netTotal\":\"5000.00\",\"itbisTotal\":\"900.00\",\"total\":\"5900.00\"", previewText, StringComparison.Ordinal);

        // The sale: 100 blocks picked up at the plant by María Pérez, without identification (5,900.00 is below the rule's amount).
        var order = Ref(await caja.OkAsync(c, "sales", "create-cash-sale", new
        {
            plantId = plant,
            deliveryTermCode = "PICKUP_AT_PLANT",
            siteAddress = (string?)null,
            requestedDate = (DateOnly?)null,
            lines = new[] { new { itemId = block, uom = "un", quantity = "100" } },
            buyerName = "María Pérez",
            buyerPhone = "809-555-0101",
            buyerIdKind = (string?)null,
            buyerId = (string?)null,
        }));
        await caja.OkAsync(c, "sales", "submit-cash-sale-for-payment", new { salesOrderId = order, expectedVersion = 1 });
        var pending = await caja.GetOkAsync($"/api/v1/companies/{c}/sales/orders/{order}");
        var consumer = pending.GetProperty("header").GetProperty("partyId").GetGuid();
        Assert.Equal("PENDING_PAYMENT|True|María Pérez", $"{pending.GetProperty("header").GetProperty("status").GetString()}|{pending.GetProperty("header").GetProperty("cashSale").GetBoolean()}|" +
            $"{pending.GetProperty("header").GetProperty("buyerName").GetString()}");
        Assert.Equal("900.00|5900.00|0.00|0.00|0.00|5900.00:False", $"{Money(pending.GetProperty("cashSale"), "itbis", "paymentTotal", "assigned", "invoiced", "counted", "stillToPay")}:" +
            $"{pending.GetProperty("cashSale").GetProperty("covered").GetBoolean()}");
        var orderLine = pending.GetProperty("lines")[0].GetProperty("salesOrderLineId").GetGuid();

        // Nothing leaves before it is paid (CF-05).
        var early = await dispatch.CommandAsync(c, "sales", "plan-delivery", new { salesOrderId = order, lines = new[] { new { salesOrderLineId = orderLine, quantity = "60" } } }, Guid.NewGuid().ToString());
        Assert.False(early.IsSuccessStatusCode);

        // Caja records the transfer and assigns it: the sale is confirmed at once, with no journal beyond the receipt's.
        var receipt = Ref(await caja.OkAsync(c, "sales", "record-receipt", new { partyId = consumer, method = "TRANSFER", amount = "5900.00", valueDate = today, bankAccountId = bank, reference = "TRF-90" }));
        var journals = await h.ScalarAsync<long>("SELECT count(*) FROM fin.gl_journal");
        var assigned = await caja.OkAsync(c, "sales", "allocate-receipt-to-order", new { receiptId = receipt, expectedVersion = 1, salesOrderId = order, amount = "5900.00" });
        Assert.Equal("CONFIRMED|0.00", $"{assigned.GetProperty("result").GetProperty("orderStatus").GetString()}|{assigned.GetProperty("result").GetProperty("stillToPay").GetString()}");
        Assert.Equal(journals, await h.ScalarAsync<long>("SELECT count(*) FROM fin.gl_journal"));
        var paid = (await caja.GetOkAsync($"/api/v1/companies/{c}/sales/orders/{order}")).GetProperty("cashSale");
        Assert.Equal("5900.00|0.00|5900.00|0.00:True:TRANSFER:True", $"{Money(paid, "assigned", "invoiced", "counted", "stillToPay")}:{paid.GetProperty("covered").GetBoolean()}:" +
            $"{paid.GetProperty("payments")[0].GetProperty("method").GetString()}:{paid.GetProperty("payments")[0].GetProperty("counts").GetBoolean()}");
        var listed = (await caja.GetOkAsync($"/api/v1/companies/{c}/sales/orders?cashSale=true")).GetProperty("items");
        Assert.Equal("1:María Pérez:5900.00", $"{listed.GetArrayLength()}:{listed[0].GetProperty("buyerName").GetString()}:{listed[0].GetProperty("paymentTotal").GetString()}");
        Assert.Equal(0, (await caja.GetOkAsync($"/api/v1/companies/{c}/sales/orders?cashSale=false")).GetProperty("items").GetArrayLength());

        // Two pickups (60 and 40 blocks), each invoiced as an e-CF 32 that is born paid from what was assigned to the order.
        var n = 0;
        foreach (var (quantity, net, tax, total) in new[] { ("60", "3000.00", "540.00", "3540.00"), ("40", "2000.00", "360.00", "2360.00") })
        {
            n++;
            var delivery = Ref(await dispatch.OkAsync(c, "sales", "plan-delivery", new { salesOrderId = order, lines = new[] { new { salesOrderLineId = orderLine, quantity } } }));
            var deliveryLine = (await dispatch.GetOkAsync($"/api/v1/companies/{c}/sales/deliveries/{delivery}")).GetProperty("lines")[0].GetProperty("deliveryLineId").GetGuid();
            await dispatch.OkAsync(c, "sales", "start-loading", new
            {
                deliveryId = delivery,
                expectedVersion = 1,
                vehicleId = (Guid?)null,
                driverId = (Guid?)null,
                customerVehiclePlate = "G654321",
                customerDriverName = "Pedro Díaz",
            });
            await dispatch.OkAsync(c, "sales", "confirm-loaded", new { deliveryId = delivery, expectedVersion = 2, lines = new[] { new { deliveryLineId = deliveryLine, sourceLocationId = patio } } });
            await dispatch.OkAsync(c, "sales", "record-gate-out", new { deliveryId = delivery, expectedVersion = 3, grossKg = "9000", tareKg = "8000", weighTicketRef = $"TK-{n}", weighTicketSha256 = Hash });

            var invoice = Ref(await billing.OkAsync(c, "sales", "create-invoice-from-deliveries", new { partyId = consumer, deliveryLineIds = new[] { deliveryLine } }));
            var issued = await billing.OkAsync(c, "sales", "issue-invoice", new { invoiceId = invoice, expectedVersion = 1 });
            Assert.Equal(total, issued.GetProperty("result").GetProperty("collectedOnOrder").GetString());
            var package = await billing.GetOkAsync($"/api/v1/companies/{c}/sales/invoices/{invoice}/fiscal-package");
            Assert.Equal($"32|{total}", $"{package.GetProperty("ecfType").GetString()}|{package.GetProperty("total").GetString()}");
            var header = (await billing.GetOkAsync($"/api/v1/companies/{c}/sales/invoices/{invoice}")).GetProperty("header");
            Assert.Equal("PAID", header.GetProperty("commercialStatus").GetString());
            await billing.OkAsync(c, "sales", "record-external-fiscal-document", new
            {
                invoiceId = invoice,
                expectedVersion = header.GetProperty("version").GetInt64(),
                encf = $"E32000000000{n}",
                issuedAt = clock.UtcNow.AddMinutes(-1),
                securityCode = "A1B2C3",
                evidenceRef = $"e-cf-{n}.xml",
                evidenceSha256 = Hash,
                receiverRnc = (string?)null,
                netTotal = net,
                taxTotal = tax,
                total,
            });
        }

        var done = await caja.GetOkAsync($"/api/v1/companies/{c}/sales/orders/{order}");
        Assert.Equal("DELIVERED|0.00|5900.00|5900.00|0.00", $"{done.GetProperty("header").GetProperty("status").GetString()}|{Money(done.GetProperty("cashSale"), "assigned", "invoiced", "counted", "stillToPay")}");

        // The bank statement shows the transfer; the treasurer matches it to the receipt.
        var monthEnd = new DateOnly(today.Year, today.Month, 1).AddMonths(1).AddDays(-1);
        var csv = $"Fecha,Referencia,Descripcion,Debito,Credito\n{today:dd/MM/yyyy},TRF-90,Transferencia Maria Perez,,5900.00\n";
        await treasurer.OkAsync(c, "treasury", "import-bank-statement", new
        {
            bankAccountId = bank,
            fileName = "extracto.csv",
            contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(csv)),
            periodFrom = today,
            periodTo = monthEnd,
            openingBalance = "0.00",
            closingBalance = "5900.00",
        });
        var line = Assert.Single((await treasurer.GetOkAsync($"/api/v1/companies/{c}/treasury/bank-statement-lines?status=UNMATCHED")).GetProperty("items").EnumerateArray()).GetProperty("lineId").GetGuid();
        var receiptVersion = (await caja.GetOkAsync($"/api/v1/companies/{c}/sales/receipts/{receipt}")).GetProperty("header").GetProperty("version").GetInt64();
        await treasurer.OkAsync(c, "treasury", "match-bank-line-to-receipt", new { lineId = line, expectedLineVersion = 1, receiptId = receipt, expectedVersion = receiptVersion });

        // The month reconciles, the cash sales among the rest.
        var run = await controller.OkAsync(c, "reconciliation", "run-reconciliation", new { reconCodes = new[] { "AR-GL", "CASH-SALE", "CONTRACT-ASSET", "RECEIPT-APPL", "PROFORMA-ASIG", "FISC-DOC", "BANK-GL" } });
        Assert.Equal(
            "AR-GL:MATCHED,BANK-GL:MATCHED,CASH-SALE:MATCHED,CONTRACT-ASSET:MATCHED,FISC-DOC:MATCHED,PROFORMA-ASIG:MATCHED,RECEIPT-APPL:MATCHED",
            string.Join(',', run.GetProperty("result").GetProperty("runs").EnumerateArray().Select(r => $"{r.GetProperty("code").GetString()}:{r.GetProperty("status").GetString()}").Order(StringComparer.Ordinal)));
        Assert.Equal("0.00|0.00|5900.00|-5000.00|-900.00", await h.ScalarAsync<string>(
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
