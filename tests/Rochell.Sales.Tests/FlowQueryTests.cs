using System.Text.Json;
using Rochell.Platform.Time;
using Rochell.Sales.CreditNotes;
using Rochell.Sales.Deliveries;
using Rochell.Sales.Invoices;
using Rochell.Sales.Orders;
using Rochell.Sales.Queries;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>UX3-01: the printable delivery note (E-UX3-7) and the credit note's invoice issuer (E-UX3-9).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class FlowQueryTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_delivery_note_prints_issuer_customer_site_transport_weights_lots_and_who_received_it()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await DeliveryTests.SetupAsync(h);
        await h.AdminRequireAsync($"UPDATE md.plant SET name = 'Planta Higüey' WHERE plant_id = '{s.Plant}'");
        var (order, orderLine) = await DeliveryTests.ConfirmedOrderAsync(h, s, DeliveryTerms.DeliveredOwnTransport, 1000m);
        var (delivery, line) = await DeliveryTests.DispatchAsync(h, s, order, orderLine, 600m, own: true, "d1");
        var inTransit = JsonDocument.Parse(await h.QueryAsync(new GetDeliveryPrint(h.CompanyId, s.Dispatch, delivery), new GetDeliveryPrintHandler())).RootElement;
        await h.RunAsync(
            new RecordPod(h.CompanyId, s.Dispatch, "pod", delivery, 4, "Ing. María Gómez", h.Clock.UtcNow.AddMinutes(-5), "foto-pod-001.jpg", DeliveryTests.Hash, [new(line, 600m, 0m)], null),
            new RecordPodHandler());
        var print = JsonDocument.Parse(await h.QueryAsync(new GetDeliveryPrint(h.CompanyId, s.Seller, delivery), new GetDeliveryPrintHandler())).RootElement;
        var company = await h.ScalarAsync<string>("SELECT legal_name || '|' || rnc FROM md.company WHERE company_id = @c", ("c", h.CompanyId));
        var orderNo = await h.ScalarAsync<string>("SELECT order_no FROM sal.sales_order WHERE sales_order_id = @o", ("o", order));

        Assert.Equal(company, $"{print.GetProperty("issuerName").GetString()}|{print.GetProperty("issuerRnc").GetString()}");
        Assert.Equal(("Constructora Uno", "131925332", "Obra Punta Cana"), (print.GetProperty("customerName").GetString(), print.GetProperty("customerRnc").GetString(),
            print.GetProperty("siteAddress").GetString()));
        Assert.Equal(("HIGUEY", "Planta Higüey", "CD-000001", orderNo), (print.GetProperty("plantCode").GetString(), print.GetProperty("plantName").GetString(),
            print.GetProperty("deliveryNo").GetString(), print.GetProperty("orderNo").GetString()));
        Assert.Equal(BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), print.GetProperty("plannedOn").GetString());
        Assert.Equal(("DELIVERED", "DELIVERED_OWN_TRANSPORT"), (print.GetProperty("status").GetString(), print.GetProperty("deliveryTermCode").GetString()));
        Assert.NotEqual(JsonValueKind.Null, print.GetProperty("gateOutAt").ValueKind);
        Assert.Equal(("L123456", "Juan Pérez"), (print.GetProperty("vehiclePlate").GetString(), print.GetProperty("driverName").GetString()));
        // DispatchAsync weighs 8,000 kg + 10,000 kg net, tare 8,000 kg.
        Assert.Equal((18000m, 8000m, 10000m), (Dec(print, "grossKg"), Dec(print, "tareKg"), Dec(print, "netKg")));
        Assert.Equal("TK-d1", print.GetProperty("weighTicketRef").GetString());
        var printed = print.GetProperty("lines")[0];
        Assert.Equal(("BLOQUE-6", "un", 600m, 600m, 600m), (printed.GetProperty("itemCode").GetString(), printed.GetProperty("uom").GetString(), Dec(printed, "qtyPlanned"),
            Dec(printed, "qtyIssued"), Dec(printed, "qtyDelivered")));
        Assert.Equal(600m, printed.GetProperty("lots").EnumerateArray().Sum(l => Dec(l, "baseQuantity")));
        Assert.Equal("Ing. María Gómez", print.GetProperty("receivedByName").GetString());
        Assert.Equal((JsonValueKind.Null, "IN_TRANSIT"), (inTransit.GetProperty("receivedByName").ValueKind, inTransit.GetProperty("status").GetString()));

        var missing = await Assert.ThrowsAsync<Platform.Commands.DomainException>(() => h.QueryAsync(new GetDeliveryPrint(h.CompanyId, s.Seller, Guid.CreateVersion7()), new GetDeliveryPrintHandler()));
        Assert.Equal(Platform.Queries.QueryErrors.NotFound, missing.Code);
    }

    [Fact]
    public async Task A_credit_note_shows_who_issued_its_invoice()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await DeliveryTests.SetupAsync(h);
        var actors = await h.FiscalActorsAsync();
        await h.ActivateRuleAsync(actors, "itbis-ventas", "ITBIS_VENTAS", FiscalRuleKinds.SalesItbis, """{"tax_code":"ITBIS","rate":"0.18","effect":"OUTPUT","exempt_item_categories":[]}""", new DateOnly(2026, 1, 1));
        var (order, orderLine) = await DeliveryTests.ConfirmedOrderAsync(h, s, DeliveryTerms.PickupAtPlant, 100m);
        var (_, line) = await DeliveryTests.DispatchAsync(h, s, order, orderLine, 100m, own: false, "d1");
        var billing = await h.SessionWithRolesAsync("FACTURACION");
        var invoice = (await h.RunAsync(new CreateInvoiceFromDeliveries(h.CompanyId, billing, "i", s.Customer, [line]), new CreateInvoiceFromDeliveriesHandler())).ResultRef;
        await h.RunAsync(new IssueInvoice(h.CompanyId, billing, "issue", invoice, 1), new IssueInvoiceHandler());
        await h.RunAsync(
            new RecordExternalFiscalDocument(h.CompanyId, billing, "fisc", invoice, 2, "E310000000001", h.Clock.UtcNow.AddMinutes(-2), "A1B2C3", "e-cf-FA-000001.xml", DeliveryTests.Hash,
                "131-92533-2", 5000.00m, 900.00m, 5900.00m),
            new RecordExternalFiscalDocumentHandler());
        var invoiceLine = await h.ScalarAsync<Guid>("SELECT invoice_line_id FROM sal.invoice_line WHERE invoice_id = @i", ("i", invoice));
        var note = (await h.RunAsync(new CreateCreditNote(h.CompanyId, billing, "nc", invoice, "DESCUENTO", "Descuento comercial por volumen", [new(invoiceLine, 500m)]), new CreateCreditNoteHandler())).ResultRef;

        var detail = JsonDocument.Parse(await h.QueryAsync(new GetCreditNote(h.CompanyId, s.Seller, note), new GetCreditNoteHandler())).RootElement;

        var issuer = await h.ScalarAsync<Guid>("SELECT user_id FROM iam.session WHERE session_id = @s", ("s", billing));
        Assert.Equal(issuer, detail.GetProperty("invoiceIssuedById").GetGuid());
        Assert.Equal(issuer, await h.ScalarAsync<Guid>("SELECT issued_by FROM sal.invoice WHERE invoice_id = @i", ("i", invoice)));
    }

    private static decimal Dec(JsonElement e, string property) => decimal.Parse(e.GetProperty(property).GetString()!, System.Globalization.CultureInfo.InvariantCulture);
}
