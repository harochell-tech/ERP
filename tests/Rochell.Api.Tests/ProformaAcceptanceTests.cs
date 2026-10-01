using System.Text;
using System.Text.Json;
using Rochell.Platform.Time;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>
/// FIS-1b E2E-P1 through the API (E-FIS1b-01-14): an order marked "exemption in process, collected with ITBIS" → two deliveries, each
/// with its proforma → the customer pays both with ITBIS and the receipt is allocated → the DGII certification cites the two
/// proformas → one e-CF 44 invoice from them, paid by what was allocated → the ITBIS advanced is refunded (Cobros prepares, the
/// Controller releases) and matched with the bank statement. The deployment and masters are those of E2E-S1.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ProformaAcceptanceTests(PostgresFixture postgres)
{
    private const string SalesItbis = """{"tax_code":"ITBIS","rate":"0.18","effect":"OUTPUT","exempt_item_categories":[]}""";
    private const string Hash = "5f70bf18a086007016e948b04aed3b82103a36bea41755b6cddfaf10ace3c6ef";

    private static Guid Ref(JsonElement response) => response.GetProperty("resultRef").GetGuid();

    private static string Money(JsonElement row, params string[] names) => string.Join('|', names.Select(n => row.GetProperty(n).GetString()));

    [Trait("AcceptanceFis1b", "E2E-P1")]
    [Fact]
    public async Task E2EP1_proformas_collected_with_ITBIS_certified_invoiced_as_eCF_44_and_the_ITBIS_refunded_over_HTTP()
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
        var controller = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CONTROLLER"));
        var approver = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("APROBADOR_POLITICAS"));
        var storekeeper = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("ALMACENISTA"));
        var seller = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("VENDEDOR"));
        var credit = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("CREDITO"));
        var dispatch = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("DESPACHO"));
        var billing = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("FACTURACION"));
        var cobros = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("COBROS"));
        var treasurer = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("TESORERO"));
        var specialist = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("ESPECIALISTA_FISCAL"));

        // The rules, approved by the Controller (P-36: the customer refund).
        foreach (var rule in new[] { "OPEN-INV", "P-15", "P-15R", "P-16", "P-30", "P-18", "P-23", "P-25", "P-36" })
        {
            await controller.OkAsync(c, "finance", "approve-posting-rule-version", new { ruleCode = rule, version = 1 });
        }

        // Masters: the product, its standard cost and price, the opening stock, the customer (30 days) and the receipts' bank account.
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
        var bank = Ref(await controller.OkAsync(c, "treasury", "register-bank-account", new { bankCode = "TEST_BANK", accountNumber = "0123456789", glAccountCode = "1101" }));

        // The order: 1,000 blocks at 50.00 picked up at the plant, the exemption in process, its proformas collected with ITBIS.
        var order = Ref(await seller.OkAsync(c, "sales", "create-sales-order", new
        {
            partyId = customer,
            plantId = plant,
            deliveryTermCode = "PICKUP_AT_PLANT",
            siteAddress = (string?)null,
            requestedDate = (DateOnly?)null,
            customerPoRef = (string?)null,
            lines = new[] { new { itemId = block, uom = "un", quantity = "1000" } },
            exemptionPending = true,
            proformaCollectsItbis = true,
        }));
        await seller.OkAsync(c, "sales", "submit-for-credit", new { salesOrderId = order, expectedVersion = 1 });
        var orderDetail = await seller.GetOkAsync($"/api/v1/companies/{c}/sales/orders/{order}");
        Assert.Equal("CONFIRMED|True|True", $"{orderDetail.GetProperty("header").GetProperty("status").GetString()}|{orderDetail.GetProperty("exemptionPending").GetBoolean()}|" +
            $"{orderDetail.GetProperty("proformaCollectsItbis").GetBoolean()}");
        var orderLine = orderDetail.GetProperty("lines")[0].GetProperty("salesOrderLineId").GetGuid();

        // Two deliveries (600 and 400 blocks), each out through the gate: each issues its proforma, nothing is billable by delivery.
        foreach (var (quantity, ticket) in new[] { ("600", "TK-1"), ("400", "TK-2") })
        {
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
            await dispatch.OkAsync(c, "sales", "record-gate-out", new { deliveryId = delivery, expectedVersion = 3, grossKg = "14000", tareKg = "8000", weighTicketRef = ticket, weighTicketSha256 = Hash });
        }

        Assert.Equal(0, (await billing.GetOkAsync($"/api/v1/companies/{c}/sales/billable-deliveries")).GetProperty("items").GetArrayLength());
        var proformas = (await billing.GetOkAsync($"/api/v1/companies/{c}/sales/proformas?partyId={customer}&status=OPEN")).GetProperty("items").EnumerateArray()
            .OrderBy(f => f.GetProperty("proformaNo").GetString(), StringComparer.Ordinal).ToList();
        Assert.Equal("PF-000001:30000.00|5400.00|35400.00|35400.00:NONE,PF-000002:20000.00|3600.00|23600.00|23600.00:NONE", string.Join(',', proformas.Select(f =>
            $"{f.GetProperty("proformaNo").GetString()}:{Money(f, "net", "itbis", "total", "balance")}:{f.GetProperty("certification").GetString()}")));
        var first = proformas[0].GetProperty("proformaId").GetGuid();
        var second = proformas[1].GetProperty("proformaId").GetGuid();
        Assert.Equal(today.AddDays(30).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), proformas[0].GetProperty("dueDate").GetString());

        // The customer pays both proformas with their ITBIS: 59,000.00 by transfer, allocated — no journal beyond the receipt's.
        var receipt = Ref(await cobros.OkAsync(c, "sales", "record-receipt", new { partyId = customer, method = "TRANSFER", amount = "59000.00", valueDate = today, bankAccountId = bank, reference = "TRF-77" }));
        var journals = await h.ScalarAsync<long>("SELECT count(*) FROM fin.gl_journal");
        await cobros.OkAsync(c, "sales", "allocate-receipt-to-proformas", new
        {
            receiptId = receipt,
            expectedVersion = 1,
            allocations = new[] { new { proformaId = first, amount = "35400.00" }, new { proformaId = second, amount = "23600.00" } },
        });
        Assert.Equal(journals, await h.ScalarAsync<long>("SELECT count(*) FROM fin.gl_journal"));
        var aging = await cobros.GetOkAsync($"/api/v1/companies/{c}/sales/ar-aging?asOf={today:yyyy-MM-dd}");
        Assert.Equal("0.00|0.00|9000.00", Money(aging, "total", "proformas", "deposits"));
        var collected = (await cobros.GetOkAsync($"/api/v1/companies/{c}/sales/proformas/{first}")).GetProperty("header");
        Assert.Equal("35400.00|5400.00|0.00", Money(collected, "allocated", "deposit", "balance"));

        // The DGII certification cites the two proformas: its scope is theirs (1,000 blocks / 50,000.00). Verified by the specialist.
        var authorization = Ref(await billing.OkAsync(c, "tax", "register-fiscal-authorization", new
        {
            partyId = customer,
            certificateNo = "CERT-2026-0009",
            issuedOn = today,
            validUntil = today.AddMonths(6),
            projectName = "Hotel Playa Bávaro",
            confoturResolutionNo = "CONFOTUR-0456-2025",
            projectTermEndsOn = (DateOnly?)null,
            salesOrderId = order,
            lines = (object?)null,
            proformaIds = new[] { first, second },
        }));
        await billing.OkAsync(c, "tax", "attach-authorization-document", new { authorizationId = authorization, kind = "CERTIFICADO_DGII", evidenceRef = "certificado.pdf", evidenceSha256 = Hash });
        await billing.OkAsync(c, "tax", "submit-for-verification", new { authorizationId = authorization, expectedVersion = 1 });
        await specialist.OkAsync(c, "tax", "verify-authorization", new { authorizationId = authorization, expectedVersion = 2 });
        var certified = await billing.GetOkAsync($"/api/v1/companies/{c}/tax/fiscal-authorizations/{authorization}");
        Assert.Equal("ACTIVE|1000.000000|50000.0000|PF-000001,PF-000002", $"{certified.GetProperty("header").GetProperty("status").GetString()}|" +
            $"{certified.GetProperty("lines")[0].GetProperty("qtyAuthorized").GetString()}|{certified.GetProperty("lines")[0].GetProperty("netAuthorized").GetString()}|" +
            string.Join(',', certified.GetProperty("proformas").EnumerateArray().Select(f => f.GetProperty("proformaNo").GetString())));
        Assert.Equal("CERTIFIED", (await billing.GetOkAsync($"/api/v1/companies/{c}/sales/proformas/{first}")).GetProperty("header").GetProperty("certification").GetString());

        // One invoice from the two proformas under the certification: e-CF 44 without ITBIS, paid by what was allocated; the ITBIS the
        // customer advanced (9,000.00) stays as the receipt's credit balance.
        var invoice = Ref(await billing.OkAsync(c, "sales", "create-invoice-from-proformas", new { partyId = customer, proformaIds = new[] { first, second }, fiscalAuthorizationId = authorization }));
        await billing.OkAsync(c, "sales", "issue-invoice", new { invoiceId = invoice, expectedVersion = 1 });
        var package = await billing.GetOkAsync($"/api/v1/companies/{c}/sales/invoices/{invoice}/fiscal-package");
        Assert.Equal("44|0.00|50000.00|CERT-2026-0009", $"{package.GetProperty("ecfType").GetString()}|{package.GetProperty("taxTotal").GetString()}|{package.GetProperty("total").GetString()}|" +
            package.GetProperty("exemption").GetProperty("certificateNo").GetString());
        var issued = await billing.GetOkAsync($"/api/v1/companies/{c}/sales/invoices/{invoice}");
        await billing.OkAsync(c, "sales", "record-external-fiscal-document", new
        {
            invoiceId = invoice,
            expectedVersion = issued.GetProperty("header").GetProperty("version").GetInt64(),
            encf = "E440000000009",
            issuedAt = clock.UtcNow.AddMinutes(-2),
            securityCode = "A1B2C3",
            evidenceRef = "e-cf-FA-000001.xml",
            evidenceSha256 = Hash,
            receiverRnc = "131925332",
            netTotal = "50000.00",
            taxTotal = "0.00",
            total = "50000.00",
        });
        var paid = (await billing.GetOkAsync($"/api/v1/companies/{c}/sales/invoices/{invoice}")).GetProperty("header");
        Assert.Equal("PAID|ACCEPTED_EXTERNAL", $"{paid.GetProperty("commercialStatus").GetString()}|{paid.GetProperty("fiscalStatus").GetString()}");
        var invoicedProforma = (await billing.GetOkAsync($"/api/v1/companies/{c}/sales/proformas/{second}")).GetProperty("header");
        Assert.Equal("INVOICED|FA-000001", $"{invoicedProforma.GetProperty("status").GetString()}|{invoicedProforma.GetProperty("invoiceNo").GetString()}");
        var afterInvoice = (await cobros.GetOkAsync($"/api/v1/companies/{c}/sales/receipts/{receipt}")).GetProperty("header");
        Assert.Equal("9000.00|0.00|9000.00", Money(afterInvoice, "unapplied", "allocated", "available"));

        // The refund of the ITBIS advanced: Cobros prepares it, cannot release it; the Controller releases it (P-36).
        var refund = Ref(await cobros.OkAsync(c, "sales", "prepare-customer-refund", new
        {
            receiptId = receipt,
            bankAccountId = bank,
            method = "TRANSFER",
            amount = "9000.00",
            reason = "ITBIS adelantado en proformas certificadas (CERT-2026-0009)",
            reference = "TRF-DEV-1",
        }));
        var ownRelease = await cobros.CommandAsync(c, "sales", "release-customer-refund", new { refundId = refund, expectedVersion = 1 }, "cobros-libera");
        Assert.Equal((System.Net.HttpStatusCode.Forbidden, "NOT_AUTHORIZED"), await ownRelease.ProblemAsync());
        await controller.OkAsync(c, "sales", "release-customer-refund", new { refundId = refund, expectedVersion = 1 });

        // The bank statement shows the transfer in and the refund out; the treasurer matches both.
        var monthEnd = new DateOnly(today.Year, today.Month, 1).AddMonths(1).AddDays(-1);
        var csv = "Fecha,Referencia,Descripcion,Debito,Credito\n" +
            $"{today:dd/MM/yyyy},TRF-77,Transferencia Constructora Uno,,59000.00\n{today:dd/MM/yyyy},TRF-DEV-1,Devolucion Constructora Uno,9000.00,\n";
        await treasurer.OkAsync(c, "treasury", "import-bank-statement", new
        {
            bankAccountId = bank,
            fileName = "extracto.csv",
            contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(csv)),
            periodFrom = today,
            periodTo = monthEnd,
            openingBalance = "0.00",
            closingBalance = "50000.00",
        });
        var lines = (await treasurer.GetOkAsync($"/api/v1/companies/{c}/treasury/bank-statement-lines?status=UNMATCHED")).GetProperty("items").EnumerateArray().ToList();
        var credited = lines.Single(l => l.GetProperty("direction").GetString() == "CREDIT").GetProperty("lineId").GetGuid();
        var debited = lines.Single(l => l.GetProperty("direction").GetString() == "DEBIT").GetProperty("lineId").GetGuid();
        var receiptVersion = (await cobros.GetOkAsync($"/api/v1/companies/{c}/sales/receipts/{receipt}")).GetProperty("header").GetProperty("version").GetInt64();
        await treasurer.OkAsync(c, "treasury", "match-bank-line-to-receipt", new { lineId = credited, expectedLineVersion = 1, receiptId = receipt, expectedVersion = receiptVersion });
        var toMatch = Assert.Single((await treasurer.GetOkAsync($"/api/v1/companies/{c}/treasury/refunds-to-match")).GetProperty("items").EnumerateArray());
        Assert.Equal("DEV-000001|9000.00|Constructora Uno", $"{toMatch.GetProperty("refundNo").GetString()}|{toMatch.GetProperty("amount").GetString()}|{toMatch.GetProperty("customerName").GetString()}");
        await treasurer.OkAsync(c, "treasury", "match-bank-line-to-refund", new { lineId = debited, expectedLineVersion = 1, refundId = refund, expectedVersion = toMatch.GetProperty("version").GetInt64() });
        Assert.Equal("CLEARED", (await cobros.GetOkAsync($"/api/v1/companies/{c}/sales/customer-refunds/{refund}")).GetProperty("header").GetProperty("status").GetString());

        // Read side: nothing open in the aging, and the statement of account tells the story — receipt, invoice, refund — ending at 0.
        var closing = await cobros.GetOkAsync($"/api/v1/companies/{c}/sales/ar-aging?asOf={today:yyyy-MM-dd}");
        Assert.Equal("0.00|0.00|0.00|0", $"{Money(closing, "net", "proformas", "deposits")}|{closing.GetProperty("customers").GetArrayLength()}");
        var statement = await cobros.GetOkAsync($"/api/v1/companies/{c}/sales/customers/{customer}/statement?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        Assert.Equal("COBRO:REC-000001:-59000.00|FACTURA:FA-000001:-9000.00|DEVOLUCION:DEV-000001:0.00", string.Join('|', statement.GetProperty("entries").EnumerateArray()
            .Select(e => $"{e.GetProperty("kind").GetString()}:{e.GetProperty("documentNo").GetString()}:{e.GetProperty("balance").GetString()}")));
        Assert.Equal(0, statement.GetProperty("openProformas").GetArrayLength());

        // The ledger: the bank keeps the net sale, no ITBIS was ever owed, nothing stays in receivables or in unapplied receipts.
        Assert.Equal("0.00|0.00|50000.00|-50000.00|0.00|0.00", await h.ScalarAsync<string>(
            """
            SELECT string_agg(coalesce((SELECT sum(e.debit - e.credit) FROM fin.gl_entry e WHERE e.account_role = r.role), 0)::numeric(19,2)::text, '|' ORDER BY r.n)
            FROM (VALUES (1, 'AR_CONTROL'), (2, 'CONTRACT_ASSET'), (3, 'BANK'), (4, 'REVENUE_PRODUCT'), (5, 'ITBIS_PAYABLE'), (6, 'UNAPPLIED_RECEIPTS')) AS r (n, role)
            """));
        var run = await controller.OkAsync(c, "reconciliation", "run-reconciliation", new
        {
            reconCodes = new[] { "AR-GL", "AUTH-CONSUMPTION", "BANK-GL", "CONTRACT-ASSET", "EXEMPT-WITHOUT-AUTH", "FISC-DOC", "PROFORMA-ASIG", "RECEIPT-APPL" },
        });
        Assert.Equal(
            "AR-GL:MATCHED,AUTH-CONSUMPTION:MATCHED,BANK-GL:MATCHED,CONTRACT-ASSET:MATCHED,EXEMPT-WITHOUT-AUTH:MATCHED,FISC-DOC:MATCHED,PROFORMA-ASIG:MATCHED,RECEIPT-APPL:MATCHED",
            string.Join(',', run.GetProperty("result").GetProperty("runs").EnumerateArray().Select(r => $"{r.GetProperty("code").GetString()}:{r.GetProperty("status").GetString()}").Order(StringComparer.Ordinal)));
    }
}
