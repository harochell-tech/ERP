using System.Globalization;
using System.Text.Json;
using Rochell.Finance.Configuration;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Reconciliation;
using Rochell.Sales.CashSales;
using Rochell.Sales.CreditNotes;
using Rochell.Sales.Customers;
using Rochell.Sales.Deliveries;
using Rochell.Sales.Invoices;
using Rochell.Sales.Orders;
using Rochell.Sales.Pricing;
using Rochell.Sales.Queries;
using Rochell.Sales.Quotes;
using Rochell.Sales.Zones;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>
/// PRS-04 (SRV-03…13, E-SRV1-1…19, E-PRS-04-1…10): freight on orders, quotes and cash sales, its revenue at control transfer, its exempt
/// invoice line, credit notes and reconciliations. GENERAL: BLOQUE-6 at 50.00, BLOQUE-8 at 60.00, freight of BLOQUE-8 to Bávaro 5.00;
/// HOTELES (the customer's list): BLOQUE-6 at 42.00 and its freight to Bávaro 3.00 per block. Sales ITBIS 18 % with TRANSPORTE exempt.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class FreightFlowTests(PostgresFixture postgres)
{
    private const string SalesItbis = """{"tax_code":"ITBIS","rate":"0.18","effect":"OUTPUT","exempt_item_categories":["TRANSPORTE"]}""";

    private sealed record World(DeliveryTests.Setup S, Guid Controller, Guid Approver, Guid Billing, Guid Block8, Guid Bavaro, Guid Freight);

    private static decimal D(JsonElement e) => decimal.Parse(e.GetString()!, CultureInfo.InvariantCulture);

    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static async Task<World> WorldAsync(TestHarness h, bool postingReady = true)
    {
        var s = await DeliveryTests.SetupAsync(h);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var approver = await h.SessionWithRolesAsync("APROBADOR_POLITICAS");
        await h.ActivateRuleAsync(await h.FiscalActorsAsync(), "itbis-ventas", "ITBIS_VENTAS", FiscalRuleKinds.SalesItbis, SalesItbis, new DateOnly(2026, 1, 1));
        var block8 = Guid.CreateVersion7();
        var freight = Guid.CreateVersion7();
        await h.AdminRequireAsync(
            $"""
            INSERT INTO md.item VALUES ('{block8}', '{h.CompanyId}', 'BLOQUE-8', 'Bloque de 8 pulgadas', 'FINISHED_GOOD', 'un', 'BLOQUE', 'ACTIVE', 1);
            INSERT INTO md.item VALUES ('{freight}', '{h.CompanyId}', 'TRANSPORTE', 'Transporte de blocks', 'SERVICE', 'un', 'TRANSPORTE', 'ACTIVE', 1);
            """);
        var bavaro = (await h.RunAsync(new CreateDeliveryZone(h.CompanyId, s.Credit, "frf-z", "Bávaro"), new CreateDeliveryZoneHandler())).ResultRef;
        var general = (await h.RunAsync(
            new PreparePriceList(h.CompanyId, controller, "frf-g", [new(s.Block, "un", 50.00m), new(block8, "un", 60.00m)], null, [new(block8, "un", bavaro, 5.00m)]),
            new PreparePriceListHandler())).ResultRef;
        await h.RunAsync(new ApprovePriceList(h.CompanyId, approver, "frf-ga", general), new ApprovePriceListHandler());
        var hoteles = (await h.RunAsync(new CreatePriceList(h.CompanyId, controller, "frf-h", "HOTELES", "Hoteles"), new CreatePriceListHandler())).ResultRef;
        var version = (await h.RunAsync(
            new PreparePriceList(h.CompanyId, controller, "frf-h1", [new(s.Block, "un", 42.00m)], hoteles, [new(s.Block, "un", bavaro, 3.00m)]), new PreparePriceListHandler())).ResultRef;
        await h.RunAsync(new ApprovePriceList(h.CompanyId, approver, "frf-h1a", version), new ApprovePriceListHandler());
        var terms = JsonDocument.Parse((await h.RunAsync(
                new PrepareCustomerTerms(h.CompanyId, s.Credit, "frf-t", s.Customer, 30, 1000000.00m, false, hoteles), new PrepareCustomerTermsHandler())).ResultPayload)
            .RootElement.GetProperty("termsVersionId").GetGuid();
        await h.RunAsync(new ApproveCustomerTerms(h.CompanyId, controller, "frf-ta", terms), new ApproveCustomerTermsHandler());
        if (postingReady)
        {
            await PostingReadyAsync(h, s, controller);
        }

        return new World(s, controller, approver, await h.SessionWithRolesAsync("FACTURACION"), block8, bavaro, freight);
    }

    /// <summary>A-01: the Controller approves P-16 version 2 and maps FREIGHT_REVENUE to 40500.</summary>
    private static async Task PostingReadyAsync(TestHarness h, DeliveryTests.Setup s, Guid controller)
    {
        s.Accounts["FREIGHT_REVENUE"] = await h.CreateAccountAsync("40500", "Ingresos por transporte", isControl: false);
        await h.CreateActiveMapAsync("FREIGHT_REVENUE", s.Accounts["FREIGHT_REVENUE"]);
        await h.RunAsync(new ApprovePostingRuleVersion(h.CompanyId, controller, "frf-p16", "P-16", 2), new ApprovePostingRuleVersionHandler());
    }

    private static async Task<(Guid Order, JsonElement Result)> OrderAsync(
        TestHarness h, World w, string key, string term, Guid? zone, bool exemption = false, params SalesOrderLineInput[] lines)
    {
        var result = await h.RunAsync(
            new CreateSalesOrder(h.CompanyId, w.S.Seller, key, w.S.Customer, w.S.Plant, term, term == DeliveryTerms.DeliveredOwnTransport ? "Hotel en Bávaro" : null, null, null, lines,
                exemption, exemption ? false : null, zone),
            new CreateSalesOrderHandler());
        return (result.ResultRef, JsonDocument.Parse(result.ResultPayload).RootElement);
    }

    private static async Task<string> LinesAsync(TestHarness h, World w, Guid order)
        => string.Join(',', JsonDocument.Parse(await h.QueryAsync(new GetSalesOrder(h.CompanyId, w.S.Seller, order), new GetSalesOrderHandler())).RootElement
            .GetProperty("lines").EnumerateArray().Select(l =>
                $"{l.GetProperty("itemCode").GetString()}:{D(l.GetProperty("netAmount")):0.00}:" +
                (l.GetProperty("freightAmount").ValueKind == JsonValueKind.Null ? "-" : D(l.GetProperty("freightAmount")).ToString("0.00", CultureInfo.InvariantCulture))));

    [Trait("AcceptancePrs1", "SRV-03")]
    [Trait("AcceptancePrs1", "SRV-04")]
    [Trait("AcceptancePrs1", "SRV-05")]
    [Trait("AcceptancePrs1", "SRV-06")]
    [Fact]
    public async Task SRV03_06_freight_comes_from_the_customers_own_list_for_the_zone_never_on_a_pickup_nor_an_exempt_order()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);

        var (own, created) = await OrderAsync(h, w, "o1", DeliveryTerms.DeliveredOwnTransport, w.Bavaro, false, new SalesOrderLineInput(w.S.Block, "un", 1000m));
        var (eight, _) = await OrderAsync(h, w, "o2", DeliveryTerms.DeliveredOwnTransport, w.Bavaro, false, new SalesOrderLineInput(w.Block8, "un", 100m));
        var (pickup, _) = await OrderAsync(h, w, "o3", DeliveryTerms.PickupAtPlant, null, false, new SalesOrderLineInput(w.S.Block, "un", 100m));
        var (exempt, _) = await OrderAsync(h, w, "o4", DeliveryTerms.DeliveredOwnTransport, w.Bavaro, true, new SalesOrderLineInput(w.S.Block, "un", 100m));
        var zoneOnPickup = await Assert.ThrowsAsync<DomainException>(() => OrderAsync(h, w, "o5", DeliveryTerms.PickupAtPlant, w.Bavaro, false, new SalesOrderLineInput(w.S.Block, "un", 1m)));
        var noZone = await Assert.ThrowsAsync<DomainException>(() => OrderAsync(h, w, "o6", DeliveryTerms.DeliveredOwnTransport, null, false, new SalesOrderLineInput(w.S.Block, "un", 1m)));

        // 1,000 × 42.00 + 1,000 × 3.00 = 45,000.00 (SRV-03).
        Assert.Equal("45000.00", created.GetProperty("totalNet").GetString());
        Assert.Equal("BLOQUE-6:42000.00:3000.00", await LinesAsync(h, w, own));
        // HOTELES has no freight for BLOQUE-8; GENERAL's is not used for the customer (SRV-04).
        Assert.Equal("BLOQUE-8:6000.00:-", await LinesAsync(h, w, eight));
        Assert.Equal(("BLOQUE-6:4200.00:-", "BLOQUE-6:4200.00:-"), (await LinesAsync(h, w, pickup), await LinesAsync(h, w, exempt)));
        Assert.Equal((SalesErrors.FieldInvalid, OrderErrors.ZoneRequired), (zoneOnPickup.Code, noZone.Code));
    }

    [Fact]
    public async Task Freight_is_withheld_with_its_reason_until_it_can_be_posted_and_invoiced_exempt()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h, postingReady: false);
        var (before, withheld) = await OrderAsync(h, w, "o1", DeliveryTerms.DeliveredOwnTransport, w.Bavaro, false, new SalesOrderLineInput(w.S.Block, "un", 100m));
        await PostingReadyAsync(h, w.S, w.Controller);
        // The freight item taken out of use (as the owner could only through a fixture): orders go without freight and say why.
        await h.AdminRequireAsync($"SET LOCAL session_replication_role = replica; UPDATE md.item SET status = 'DRAFT', version = 2 WHERE item_id = '{w.Freight}'");
        var (_, noItem) = await OrderAsync(h, w, "o2", DeliveryTerms.DeliveredOwnTransport, w.Bavaro, false, new SalesOrderLineInput(w.S.Block, "un", 100m));

        Assert.Equal(FreightReasons.PostingMissing, withheld.GetProperty("freightWithheld").GetString());
        Assert.Equal("BLOQUE-6:4200.00:-", await LinesAsync(h, w, before));
        Assert.Equal(FreightReasons.ItemMissing, noItem.GetProperty("freightWithheld").GetString());
    }

    [Trait("AcceptancePrs1", "SRV-07")]
    [Trait("AcceptancePrs1", "SRV-08")]
    [Trait("AcceptancePrs1", "SRV-10")]
    [Trait("AcceptancePrs1", "SRV-12")]
    [Fact]
    public async Task SRV07_08_10_12_freight_is_revenue_at_the_POD_an_exempt_invoice_line_creditable_and_the_month_reconciles()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var (order, _) = await OrderAsync(h, w, "o", DeliveryTerms.DeliveredOwnTransport, w.Bavaro, false, new SalesOrderLineInput(w.S.Block, "un", 1000m));
        await h.RunAsync(new SubmitForCredit(h.CompanyId, w.S.Seller, "o-s", order, 1), new SubmitForCreditHandler());
        var orderLine = await h.ScalarAsync<Guid>("SELECT line_id FROM sal.sales_order_line WHERE sales_order_id = @o", ("o", order));
        var (delivery, line) = await DeliveryTests.DispatchAsync(h, w.S, order, orderLine, 600m, own: true, "d1");
        await h.RunAsync(
            new RecordPod(h.CompanyId, w.S.Dispatch, "pod", delivery, 4, "Ing. María Gómez", h.Clock.UtcNow.AddMinutes(-5), "foto-pod.jpg", DeliveryTests.Hash, [new(line, 600m, 0m)], null),
            new RecordPodHandler());
        var afterPod = await DeliveryTests.Balances(h, w.S, "CONTRACT_ASSET", "REVENUE_PRODUCT", "FREIGHT_REVENUE");
        var print = JsonDocument.Parse(await h.QueryAsync(new GetDeliveryPrint(h.CompanyId, w.S.Dispatch, delivery), new GetDeliveryPrintHandler())).RootElement;
        var midMonth = JsonDocument.Parse((await h.RunAsync(new RunReconciliation(h.CompanyId, w.Controller, "r1", ["CONTRACT-ASSET"]), new RunReconciliationHandler())).ResultPayload).RootElement;

        var invoice = (await h.RunAsync(new CreateInvoiceFromDeliveries(h.CompanyId, w.Billing, "i", w.S.Customer, [line]), new CreateInvoiceFromDeliveriesHandler())).ResultRef;
        var issued = JsonDocument.Parse((await h.RunAsync(new IssueInvoice(h.CompanyId, w.Billing, "issue", invoice, 1), new IssueInvoiceHandler())).ResultPayload).RootElement;
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetInvoice(h.CompanyId, w.Billing, invoice), new GetInvoiceHandler())).RootElement;
        var afterInvoice = await DeliveryTests.Balances(h, w.S, "CONTRACT_ASSET", "AR_CONTROL", "ITBIS_PAYABLE");
        await h.RunAsync(
            new RecordExternalFiscalDocument(h.CompanyId, w.Billing, "fisc", invoice, 2, "E310000000001", h.Clock.UtcNow.AddMinutes(-2), "A1B2C3", "e-cf-FA-000001.xml", DeliveryTests.Hash,
                "131-92533-2", 27000.00m, 4536.00m, 31536.00m),
            new RecordExternalFiscalDocumentHandler());
        var freightLine = await h.ScalarAsync<Guid>("SELECT invoice_line_id FROM sal.invoice_line WHERE invoice_id = @i AND line_kind = 'FREIGHT'", ("i", invoice));
        var note = (await h.RunAsync(
            new CreateCreditNote(h.CompanyId, w.Billing, "nc", invoice, "DESCUENTO", "Rebaja del flete por retraso", [new(freightLine, 300.00m)]), new CreateCreditNoteHandler())).ResultRef;
        await h.RunAsync(new IssueCreditNote(h.CompanyId, await h.SessionWithRolesAsync("FACTURACION"), "nc-i", note, 1), new IssueCreditNoteHandler());
        var recon = JsonDocument.Parse((await h.RunAsync(new RunReconciliation(h.CompanyId, w.Controller, "r2", ["CONTRACT-ASSET", "AR-GL"]), new RunReconciliationHandler())).ResultPayload).RootElement;

        // SRV-07: 600 × 42.00 = 25,200.00 of revenue and 600 × 3.00 = 1,800.00 of freight, both on the delivery line's contract asset.
        Assert.Equal("CONTRACT_ASSET=27000.00|REVENUE_PRODUCT=-25200.00|FREIGHT_REVENUE=-1800.00", afterPod);
        Assert.Equal("Transporte de blocks — Bávaro:600", $"{print.GetProperty("lines")[0].GetProperty("freight").GetString()}:{D(print.GetProperty("lines")[0].GetProperty("qtyDelivered")):0}");
        Assert.Equal("MATCHED", midMonth.GetProperty("runs")[0].GetProperty("status").GetString());
        // SRV-08: 25,200.00 + ITBIS 4,536.00 + freight 1,800.00 exempt = 31,536.00; the contract asset is emptied.
        Assert.Equal("4536.00|31536.00", $"{issued.GetProperty("taxTotal").GetString()}|{issued.GetProperty("total").GetString()}");
        Assert.Equal(
            "PRODUCT:BLOQUE-6:25200.00:4536.00,FREIGHT:TRANSPORTE:1800.00:0.00",
            string.Join(',', detail.GetProperty("lines").EnumerateArray().Select(l =>
                $"{l.GetProperty("lineKind").GetString()}:{l.GetProperty("itemCode").GetString()}:{D(l.GetProperty("netAmount")):0.00}:{D(l.GetProperty("itbis")):0.00}")));
        Assert.Equal("CONTRACT_ASSET=0.00|AR_CONTROL=31536.00|ITBIS_PAYABLE=-4536.00", afterInvoice);
        // SRV-10: 300.00 off the freight, without ITBIS.
        Assert.Equal("SALES_DISCOUNTS=300.00|AR_CONTROL=31236.00|ITBIS_PAYABLE=-4536.00", await DeliveryTests.Balances(h, w.S, "SALES_DISCOUNTS", "AR_CONTROL", "ITBIS_PAYABLE"));
        Assert.Equal(1000m - 600m, await h.ScalarAsync<decimal>("SELECT qty_ordered - qty_invoiced FROM sal.sales_order_line WHERE line_id = @l", ("l", orderLine)));
        Assert.All(recon.GetProperty("runs").EnumerateArray(), r => Assert.Equal("MATCHED", r.GetProperty("status").GetString()));
    }

    [Trait("AcceptancePrs1", "SRV-09")]
    [Fact]
    public async Task SRV09_a_cash_sale_with_our_truck_takes_GENERALs_freight_into_its_total_without_ITBIS()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var caja = await h.SessionWithRolesAsync("CAJA");
        var preview = JsonDocument.Parse(await h.QueryAsync(
            new PreviewCashSale(h.CompanyId, caja, w.S.Plant, [new(w.Block8, "un", 100m)], w.Bavaro), new PreviewCashSaleHandler())).RootElement;
        var sale = JsonDocument.Parse((await h.RunAsync(
                new CreateCashSale(h.CompanyId, caja, "cs", w.S.Plant, DeliveryTerms.DeliveredOwnTransport, "Villa en Bávaro", null, [new(w.Block8, "un", 100m)], "María Pérez", DeliveryZoneId: w.Bavaro),
                new CreateCashSaleHandler())).ResultPayload).RootElement;

        // 100 × 60.00 = 6,000.00 + ITBIS 1,080.00 + freight 100 × 5.00 = 500.00 → 7,580.00.
        Assert.Equal("6000.00|500.00|1080.00|7580.00", string.Join('|', new[] { "netTotal", "freightTotal", "itbisTotal", "total" }.Select(n => D(preview.GetProperty(n)).ToString("0.00", CultureInfo.InvariantCulture))));
        Assert.Equal("6500.00", sale.GetProperty("totalNet").GetString());
    }

    [Trait("AcceptancePrs1", "SRV-13")]
    [Trait("AcceptancePrs1", "SRV-11")]
    [Fact]
    public async Task SRV11_13_a_quote_with_a_zone_shows_the_freight_and_its_order_keeps_it_when_the_list_changes_and_an_exempt_order_has_none()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var quote = (await h.RunAsync(
            new CreateQuote(h.CompanyId, w.S.Seller, "q", w.S.Customer, w.S.Plant, Today(h).AddDays(30), DeliveryTerms.DeliveredOwnTransport, "Hotel en Bávaro", null, null,
                [new(w.S.Block, "un", 1000m)], w.Bavaro),
            new CreateQuoteHandler())).ResultRef;
        await h.RunAsync(new SendQuote(h.CompanyId, w.S.Seller, "q-send", quote, 1), new SendQuoteHandler());
        var quoted = JsonDocument.Parse(await h.QueryAsync(new GetQuote(h.CompanyId, w.S.Seller, quote), new GetQuoteHandler())).RootElement;

        // The freight of HOTELES goes up to 4.00; the converted order keeps the quoted 3.00 (E-PRS-04-8).
        var hoteles = await h.ScalarAsync<Guid>("SELECT price_list_id FROM sal.price_list WHERE code = 'HOTELES'");
        var v2 = (await h.RunAsync(
            new PreparePriceList(h.CompanyId, w.Controller, "frf-h2", [new(w.S.Block, "un", 42.00m)], hoteles, [new(w.S.Block, "un", w.Bavaro, 4.00m)]), new PreparePriceListHandler())).ResultRef;
        await h.RunAsync(new ApprovePriceList(h.CompanyId, w.Approver, "frf-h2a", v2), new ApprovePriceListHandler());
        var order = (await h.RunAsync(new ConvertQuote(h.CompanyId, w.S.Seller, "convert", quote, 2), new ConvertQuoteHandler())).ResultRef;
        var (fresh, _) = await OrderAsync(h, w, "o-new", DeliveryTerms.DeliveredOwnTransport, w.Bavaro, false, new SalesOrderLineInput(w.S.Block, "un", 1000m));

        Assert.Equal("3.0000:3000.00:Bávaro", $"{D(quoted.GetProperty("lines")[0].GetProperty("freightUnitPrice")):0.0000}:{D(quoted.GetProperty("lines")[0].GetProperty("freightAmount")):0.00}:{quoted.GetProperty("deliveryZoneName").GetString()}");
        Assert.Equal("BLOQUE-6:42000.00:3000.00", await LinesAsync(h, w, order));
        Assert.Equal("BLOQUE-6:42000.00:4000.00", await LinesAsync(h, w, fresh));
    }

    [Fact]
    public async Task A_CONFOTUR_authorization_cannot_cite_an_order_that_carries_freight()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var (order, _) = await OrderAsync(h, w, "o", DeliveryTerms.DeliveredOwnTransport, w.Bavaro, false, new SalesOrderLineInput(w.S.Block, "un", 1000m));
        var refused = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new Rochell.Tax.Authorizations.RegisterFiscalAuthorization(
                h.CompanyId, w.Billing, "auth", w.S.Customer, "CERT-2026-0001", new DateOnly(2026, 9, 1), new DateOnly(2027, 3, 1), "Hotel Playa Bávaro", "CONFOTUR-0456-2025", null, order,
                [new Rochell.Tax.Authorizations.AuthorizationLineInput(w.S.Block, "un", 1000m, 42000.00m)]),
            new Rochell.Tax.Authorizations.RegisterFiscalAuthorizationHandler()));

        Assert.Equal(TaxErrors.AuthorizationOrderHasFreight, refused.Code);
    }
}
