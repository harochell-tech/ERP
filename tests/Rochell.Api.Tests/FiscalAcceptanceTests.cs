using System.Text;
using System.Text.Json;
using Rochell.Platform.Time;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>
/// FIS-1 E2E-F1 through the API (E-FIS1-05-11): proforma → authorization registered with its documents → verified → delivery →
/// exempt invoice e-CF 44 → e-CF E44 recorded → credit note (e-CF 34 without ITBIS) → consumption and reconciliations. The deployment
/// and masters are those of E2E-S1.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class FiscalAcceptanceTests(PostgresFixture postgres)
{
    private const string SalesItbis = """{"tax_code":"ITBIS","rate":"0.18","effect":"OUTPUT","exempt_item_categories":[]}""";
    private const string Hash = "5f70bf18a086007016e948b04aed3b82103a36bea41755b6cddfaf10ace3c6ef";

    private static Guid Ref(JsonElement response) => response.GetProperty("resultRef").GetGuid();

    [Trait("AcceptanceFis1", "E2E-F1")]
    [Fact]
    public async Task E2EF1_proforma_authorization_verification_delivery_exempt_eCF_44_and_credit_note_over_HTTP()
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
            ("ITBIS_PAYABLE", "2150", false), ("SALES_DISCOUNTS", "4190", false),
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
        var specialist = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("ESPECIALISTA_FISCAL"));

        // The rules, approved by the Controller.
        foreach (var rule in new[] { "OPEN-INV", "P-15", "P-15R", "P-16", "P-30", "P-18", "P-22" })
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
        var truck = Ref(await dispatch.OkAsync(c, "sales", "register-vehicle", new { plate = "L123456", capacityKg = "12000" }));
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

        // The proforma the customer takes to the DGII: 50,000.00 + ITBIS 9,000.00.
        var proforma = await seller.GetOkAsync($"/api/v1/companies/{c}/tax/proformas/{order}");
        Assert.Equal("50000.0000|9000.00|59000.0000|131925332", $"{proforma.GetProperty("netTotal").GetString()}|{proforma.GetProperty("itbisTotal").GetString()}|" +
            $"{proforma.GetProperty("total").GetString()}|{proforma.GetProperty("customerRnc").GetString()}");

        // The DGII certificate for 1,000 blocks / 50,000.00: Facturación registers it with its documents, the Especialista fiscal verifies it.
        var authorization = Ref(await billing.OkAsync(c, "tax", "register-fiscal-authorization", new
        {
            partyId = customer,
            certificateNo = "CERT-2026-0001",
            issuedOn = today,
            validUntil = today.AddMonths(6),
            projectName = "Hotel Playa Bávaro",
            confoturResolutionNo = "CONFOTUR-0456-2025",
            projectTermEndsOn = (DateOnly?)null,
            salesOrderId = order,
            lines = new[] { new { itemId = block, uom = "un", quantity = "1000", netAmount = "50000.00" } },
        }));
        foreach (var kind in new[] { "CERTIFICADO_DGII", "RESOLUCION_CONFOTUR", "LISTA_MATERIALES", "PROFORMA" })
        {
            await billing.OkAsync(c, "tax", "attach-authorization-document", new { authorizationId = authorization, kind, evidenceRef = kind.ToLowerInvariant() + ".pdf", evidenceSha256 = Hash });
        }

        await billing.OkAsync(c, "tax", "submit-for-verification", new { authorizationId = authorization, expectedVersion = 1 });
        await specialist.OkAsync(c, "tax", "verify-authorization", new { authorizationId = authorization, expectedVersion = 2 });

        // Delivery of 600 blocks: plan, load, gate (P-15 to transit), POD (P-16: cost, revenue, contract asset).
        var delivery = Ref(await dispatch.OkAsync(c, "sales", "plan-delivery", new { salesOrderId = order, lines = new[] { new { salesOrderLineId = orderLine, quantity = "600" } } }));
        var deliveryLine = (await dispatch.GetOkAsync($"/api/v1/companies/{c}/sales/deliveries/{delivery}")).GetProperty("lines")[0].GetProperty("deliveryLineId").GetGuid();
        await dispatch.OkAsync(c, "sales", "start-loading", new { deliveryId = delivery, expectedVersion = 1, vehicleId = truck, driverId = driver, customerVehiclePlate = (string?)null, customerDriverName = (string?)null });
        await dispatch.OkAsync(c, "sales", "confirm-loaded", new { deliveryId = delivery, expectedVersion = 2, lines = new[] { new { deliveryLineId = deliveryLine, sourceLocationId = patio } } });
        await dispatch.OkAsync(c, "sales", "record-gate-out", new { deliveryId = delivery, expectedVersion = 3, grossKg = "14000", tareKg = "8000", weighTicketRef = "TK-1", weighTicketSha256 = Hash });
        await dispatch.OkAsync(c, "sales", "record-pod", new
        {
            deliveryId = delivery,
            expectedVersion = 4,
            receivedByName = "Ing. María Gómez",
            receivedAt = clock.UtcNow.AddMinutes(-5),
            evidenceRef = "pod-001.jpg",
            evidenceSha256 = Hash,
            lines = new[] { new { deliveryLineId = deliveryLine, qtyReceived = "600", qtyReturned = "0" } },
            exceptionReason = (string?)null,
        });
        Assert.Equal("DELIVERED", (await dispatch.GetOkAsync($"/api/v1/companies/{c}/sales/deliveries/{delivery}")).GetProperty("header").GetProperty("status").GetString());

        // The exempt invoice: e-CF 44, no ITBIS, 600 blocks × 50.00 = 30,000.00 consumed.
        var invoice = Ref(await billing.OkAsync(c, "sales", "create-invoice-from-deliveries", new { partyId = customer, deliveryLineIds = new[] { deliveryLine }, fiscalAuthorizationId = authorization }));
        await billing.OkAsync(c, "sales", "issue-invoice", new { invoiceId = invoice, expectedVersion = 1 });
        var package = await billing.GetOkAsync($"/api/v1/companies/{c}/sales/invoices/{invoice}/fiscal-package");
        Assert.Equal("44|0.00|30000.00|CONFOTUR|CERT-2026-0001|4", $"{package.GetProperty("ecfType").GetString()}|{package.GetProperty("taxTotal").GetString()}|" +
            $"{package.GetProperty("total").GetString()}|{package.GetProperty("exemption").GetProperty("regime").GetString()}|" +
            $"{package.GetProperty("exemption").GetProperty("certificateNo").GetString()}|{package.GetProperty("exemption").GetProperty("billingIndicator").GetString()}");
        await billing.OkAsync(c, "sales", "record-external-fiscal-document", new
        {
            invoiceId = invoice,
            expectedVersion = 2,
            encf = "E440000000001",
            issuedAt = clock.UtcNow.AddMinutes(-2),
            securityCode = "A1B2C3",
            evidenceRef = "e-cf-FA-000001.xml",
            evidenceSha256 = Hash,
            receiverRnc = "131925332",
            netTotal = "30000.00",
            taxTotal = "0.00",
            total = "30000.00",
        });

        // A price credit note of 1,000.00: e-CF 34 without ITBIS, returning 1,000.00 of net (no units).
        var invoiceLine = (await billing.GetOkAsync($"/api/v1/companies/{c}/sales/invoices/{invoice}")).GetProperty("creditable")[0].GetProperty("invoiceLineId").GetGuid();
        var note = Ref(await billing.OkAsync(c, "sales", "create-credit-note", new
        {
            invoiceId = invoice,
            reasonCategory = "DESCUENTO",
            reason = "Descuento por volumen",
            lines = new[] { new { invoiceLineId = invoiceLine, netAmount = "1000.00" } },
        }));
        var secondBilling = await api.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("FACTURACION")); // four eyes: not who issued the invoice
        await secondBilling.OkAsync(c, "sales", "issue-credit-note", new { creditNoteId = note, expectedVersion = 1 });
        await billing.OkAsync(c, "sales", "record-external-credit-note-document", new
        {
            creditNoteId = note,
            expectedVersion = 2,
            encf = "E340000000001",
            issuedAt = clock.UtcNow.AddMinutes(-1),
            securityCode = "Z9",
            evidenceRef = "nc.xml",
            evidenceSha256 = Hash,
            receiverRnc = "131925332",
            netTotal = "1000.00",
            taxTotal = "0.00",
            total = "1000.00",
        });

        // The authorization: ACTIVE, 600 blocks and 29,000.00 consumed; its detail lists the invoice and the credit.
        var detail = await specialist.GetOkAsync($"/api/v1/companies/{c}/tax/fiscal-authorizations/{authorization}");
        var line = detail.GetProperty("lines")[0];
        Assert.Equal("ACTIVE|600.000000|29000.0000|400.000000|21000.0000", $"{detail.GetProperty("header").GetProperty("status").GetString()}|{line.GetProperty("qtyConsumed").GetString()}|" +
            $"{line.GetProperty("netConsumed").GetString()}|{line.GetProperty("qtyAvailable").GetString()}|{line.GetProperty("netAvailable").GetString()}");
        Assert.Equal("FA-000001:False:30000.0000|FA-000001:True:1000.0000", string.Join('|', detail.GetProperty("consumptions").EnumerateArray()
            .Select(x => $"{x.GetProperty("invoiceNo").GetString()}:{x.GetProperty("release").GetBoolean()}:{x.GetProperty("net").GetString()}")));
        Assert.Equal(4, detail.GetProperty("documents").GetArrayLength());
        var authorizations = await billing.GetOkAsync($"/api/v1/companies/{c}/tax/fiscal-authorizations?partyId={customer}&status=ACTIVE");
        Assert.Equal("CERT-2026-0001", Assert.Single(authorizations.GetProperty("items").EnumerateArray()).GetProperty("certificateNo").GetString());

        // The ledger: AR 29,000.00, no ITBIS, revenue 30,000.00 less the 1,000.00 discount; the reconciliations match.
        Assert.Equal("29000.00|0.00|-30000.00|1000.00", await h.ScalarAsync<string>(
            """
            SELECT string_agg(coalesce((SELECT sum(e.debit - e.credit) FROM fin.gl_entry e WHERE e.account_role = r.role), 0)::numeric(19,2)::text, '|' ORDER BY r.n)
            FROM (VALUES (1, 'AR_CONTROL'), (2, 'ITBIS_PAYABLE'), (3, 'REVENUE_PRODUCT'), (4, 'SALES_DISCOUNTS')) AS r (n, role)
            """));
        var run = await controller.OkAsync(c, "reconciliation", "run-reconciliation", new { reconCodes = new[] { "AUTH-CONSUMPTION", "EXEMPT-WITHOUT-AUTH", "AR-GL", "FISC-DOC" } });
        Assert.Equal("AR-GL:MATCHED,AUTH-CONSUMPTION:MATCHED,EXEMPT-WITHOUT-AUTH:MATCHED,FISC-DOC:MATCHED", string.Join(',', run.GetProperty("result").GetProperty("runs").EnumerateArray()
            .Select(r => $"{r.GetProperty("code").GetString()}:{r.GetProperty("status").GetString()}").Order(StringComparer.Ordinal)));
    }
}
