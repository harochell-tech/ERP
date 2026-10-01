using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Sales.Deliveries;
using Rochell.Sales.Invoices;
using Rochell.Sales.Orders;
using Rochell.Sales.Proformas;
using Rochell.Sales.Queries;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>FIS1b-02: the proforma of a delivery whose order's exemption is in process (E-FIS1b-1…3, E-FIS1b-01-1…3, 6, 13).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ProformaTests(PostgresFixture postgres)
{
    private const string SalesItbis = """{"tax_code":"ITBIS","rate":"0.18","effect":"OUTPUT","exempt_item_categories":[]}""";

    internal static async Task<DeliveryTests.Setup> SetupAsync(TestHarness h, bool withRule = true)
    {
        var s = await DeliveryTests.SetupAsync(h);
        if (withRule)
        {
            await h.ActivateRuleAsync(await h.FiscalActorsAsync(), "itbis-ventas", "ITBIS_VENTAS", FiscalRuleKinds.SalesItbis, SalesItbis, new DateOnly(2026, 1, 1));
        }

        return s;
    }

    /// <summary>A confirmed order of blocks at 50.00, marked "exención en trámite" (or not, with <paramref name="collectsItbis"/> null).</summary>
    internal static async Task<(Guid Order, Guid Line)> OrderAsync(TestHarness h, DeliveryTests.Setup s, string term, decimal quantity, bool? collectsItbis, string key = "o")
    {
        var order = (await h.RunAsync(
            new CreateSalesOrder(
                h.CompanyId, s.Seller, key, s.Customer, s.Plant, term, term == DeliveryTerms.DeliveredOwnTransport ? "Obra Punta Cana" : null, null, null, [new(s.Block, "un", quantity)],
                collectsItbis.HasValue, collectsItbis),
            new CreateSalesOrderHandler())).ResultRef;
        await h.RunAsync(new SubmitForCredit(h.CompanyId, s.Seller, key + "-s", order, 1), new SubmitForCreditHandler());
        return (order, await h.ScalarAsync<Guid>("SELECT line_id FROM sal.sales_order_line WHERE sales_order_id = @o", ("o", order)));
    }

    private static Task<string?> Proforma(TestHarness h, Guid delivery)
        => h.ScalarAsync<string>(
            """
            SELECT concat_ws('|', proforma_no, status, collects_itbis::text, net_total::numeric(19,2), itbis_total::numeric(19,2), total::numeric(19,2), allocated_amount::numeric(19,2),
                              due_date - proforma_date)
            FROM sal.proforma WHERE delivery_id = @d
            """,
            ("d", delivery));

    [Trait("AcceptanceFis1b", "PRF-01")]
    [Fact]
    public async Task PRF01_a_delivery_of_a_marked_order_issues_its_proforma_without_a_journal()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        var (order, line) = await OrderAsync(h, s, DeliveryTerms.PickupAtPlant, 1000m, collectsItbis: true);
        var journalsBefore = await h.ScalarAsync<long>("SELECT count(*) FROM fin.gl_journal");

        var (delivery, deliveryLine) = await DeliveryTests.DispatchAsync(h, s, order, line, 600m, own: false, "d1");
        var (second, _) = await DeliveryTests.DispatchAsync(h, s, order, line, 400m, own: false, "d2");

        // 600 × 50.00 = 30,000.00; ITBIS 18 % = 5,400.00; due 30 days after the delivery (the customer's terms).
        Assert.Equal("PF-000001|OPEN|true|30000.00|5400.00|35400.00|0.00|30", await Proforma(h, delivery));
        Assert.Equal("PF-000002|OPEN|true|20000.00|3600.00|23600.00|0.00|30", await Proforma(h, second));
        Assert.Equal(
            $"{deliveryLine}|600.000000|50.0000|30000.00|5400.00",
            await h.ScalarAsync<string>(
                "SELECT concat_ws('|', l.delivery_line_id, l.quantity, l.unit_price, l.net_amount::numeric(19,2), l.itbis_amount::numeric(19,2)) FROM sal.proforma_line l JOIN sal.proforma p USING (proforma_id) WHERE p.delivery_id = @d",
                ("d", delivery)));
        // The proforma posts nothing: only the deliveries' own journal (P-16 at the gate of a pickup), one each.
        Assert.Equal(2L, await h.ScalarAsync<long>("SELECT count(*) FROM fin.gl_journal") - journalsBefore);
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM fin.gl_journal j JOIN core.domain_event e ON e.event_id = j.source_event_id WHERE e.event_type = 'ProformaIssued'"));
        Assert.Equal(2L, await h.ScalarAsync<long>("SELECT count(*) FROM core.state_history WHERE aggregate_type = 'Proforma' AND to_state = 'OPEN' AND command = 'Sales.RecordGateOut'"));

        var list = JsonDocument.Parse(await h.QueryAsync(new ListProformas(h.CompanyId, s.Seller, s.Customer, "OPEN"), new ListProformasHandler())).RootElement.GetProperty("items");
        var id = list[1].GetProperty("proformaId").GetGuid();
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetProforma(h.CompanyId, s.Seller, id), new GetProformaHandler())).RootElement;
        Assert.Equal("PF-000002,PF-000001", string.Join(',', list.EnumerateArray().Select(p => p.GetProperty("proformaNo").GetString())));
        Assert.Equal(
            ("PF-000001", "35400.00", "0.00", "NONE", 0, "BLOQUE-6", "5400.00"),
            (detail.GetProperty("header").GetProperty("proformaNo").GetString(), detail.GetProperty("header").GetProperty("balance").GetString(),
             detail.GetProperty("header").GetProperty("deposit").GetString(), detail.GetProperty("header").GetProperty("certification").GetString(),
             detail.GetProperty("header").GetProperty("daysOverdue").GetInt32(), detail.GetProperty("lines")[0].GetProperty("itemCode").GetString(),
             detail.GetProperty("lines")[0].GetProperty("itbis").GetString()));
    }

    [Trait("AcceptanceFis1b", "PRF-02")]
    [Fact]
    public async Task PRF02_an_order_without_the_mark_has_no_proforma_and_is_invoiced_from_its_delivery()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h, withRule: false); // an unmarked delivery needs no fiscal rule
        var (order, line) = await OrderAsync(h, s, DeliveryTerms.PickupAtPlant, 100m, collectsItbis: null);

        var (delivery, deliveryLine) = await DeliveryTests.DispatchAsync(h, s, order, line, 100m, own: false, "d1");
        var billable = JsonDocument.Parse(await h.QueryAsync(new ListBillableDeliveries(h.CompanyId, s.Seller, s.Customer), new ListBillableDeliveriesHandler())).RootElement.GetProperty("items");

        Assert.Null(await Proforma(h, delivery));
        Assert.Equal(deliveryLine, billable.EnumerateArray().Single().GetProperty("deliveryLineId").GetGuid());
    }

    [Trait("AcceptanceFis1b", "PRF-11")]
    [Fact]
    public async Task PRF11_a_delivery_with_a_proforma_is_not_invoiced_through_the_delivery_path()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        var billing = await h.SessionWithRolesAsync("FACTURACION");
        var (order, line) = await OrderAsync(h, s, DeliveryTerms.PickupAtPlant, 100m, collectsItbis: false);
        var (delivery, deliveryLine) = await DeliveryTests.DispatchAsync(h, s, order, line, 100m, own: false, "d1");

        var refused = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new CreateInvoiceFromDeliveries(h.CompanyId, billing, "i", s.Customer, [deliveryLine]), new CreateInvoiceFromDeliveriesHandler()));
        var billable = JsonDocument.Parse(await h.QueryAsync(new ListBillableDeliveries(h.CompanyId, s.Seller, s.Customer), new ListBillableDeliveriesHandler())).RootElement.GetProperty("items");
        var proforma = JsonDocument.Parse(await h.QueryAsync(new ListProformas(h.CompanyId, s.Seller), new ListProformasHandler())).RootElement.GetProperty("items")[0];

        Assert.Equal(ProformaErrors.Required, refused.Code);
        Assert.Equal(0, billable.GetArrayLength());
        // Collected without ITBIS: the balance is the net (E-FIS1b-01-1).
        Assert.Equal("PF-000001|OPEN|false|5000.00|900.00|5900.00|0.00|30", await Proforma(h, delivery));
        Assert.Equal("5000.00", proforma.GetProperty("balance").GetString());
    }

    [Fact]
    public async Task A_site_delivery_issues_its_proforma_at_the_POD_for_what_the_customer_received()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        var (order, line) = await OrderAsync(h, s, DeliveryTerms.DeliveredOwnTransport, 100m, collectsItbis: true);
        var (delivery, deliveryLine) = await DeliveryTests.DispatchAsync(h, s, order, line, 100m, own: true, "d1");
        var inTransit = await Proforma(h, delivery);
        var receivedAt = h.Clock.UtcNow.AddMinutes(-5);

        var pod = JsonDocument.Parse((await h.RunAsync(
            new RecordPod(h.CompanyId, s.Dispatch, "pod", delivery, 4, "Capataz", receivedAt, "firma.pdf", DeliveryTests.Hash, [new(deliveryLine, 90m, 5m)], "5 rotos en el camino, 5 rechazados"),
            new RecordPodHandler())).ResultPayload).RootElement;

        Assert.Null(inTransit); // control passes at the POD, and so does the proforma
        Assert.Equal("PF-000001", pod.GetProperty("proformaNo").GetString());
        // 90 received × 50.00 = 4,500.00; ITBIS 810.00.
        Assert.Equal("PF-000001|OPEN|true|4500.00|810.00|5310.00|0.00|30", await Proforma(h, delivery));
        Assert.Equal(
            BusinessCalendar.DefaultBusinessDate(receivedAt).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            await h.ScalarAsync<string>("SELECT proforma_date::text FROM sal.proforma WHERE delivery_id = @d", ("d", delivery)));
    }

    [Fact]
    public async Task Without_a_sales_ITBIS_rule_in_force_the_delivery_of_a_marked_order_is_refused()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h, withRule: false);
        var (order, line) = await OrderAsync(h, s, DeliveryTerms.PickupAtPlant, 100m, collectsItbis: true);

        var refused = await Assert.ThrowsAsync<DomainException>(() => DeliveryTests.DispatchAsync(h, s, order, line, 100m, own: false, "d1"));

        Assert.Equal(TaxErrors.FiscalGateClosed, refused.Code);
        Assert.Equal("LOADED", await h.ScalarAsync<string>("SELECT status FROM log.delivery"));
    }

    [Fact]
    public async Task The_mark_and_what_it_collects_go_together_and_change_only_in_draft()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        CreateSalesOrder Order(string key, bool pending, bool? collects)
            => new(h.CompanyId, s.Seller, key, s.Customer, s.Plant, DeliveryTerms.PickupAtPlant, null, null, null, [new(s.Block, "un", 10m)], pending, collects);

        var markWithoutChoice = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Order("a", true, null), new CreateSalesOrderHandler()));
        var choiceWithoutMark = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Order("b", false, true), new CreateSalesOrderHandler()));
        var order = (await h.RunAsync(Order("c", false, null), new CreateSalesOrderHandler())).ResultRef;
        await h.RunAsync(
            new UpdateSalesOrderDraft(h.CompanyId, s.Seller, "u", order, 1, s.Plant, DeliveryTerms.PickupAtPlant, null, null, null, [new(s.Block, "un", 10m)], true, false), new UpdateSalesOrderDraftHandler());
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetSalesOrder(h.CompanyId, s.Seller, order), new GetSalesOrderHandler())).RootElement;
        await h.RunAsync(new SubmitForCredit(h.CompanyId, s.Seller, "s", order, 2), new SubmitForCreditHandler());
        var afterDraft = await h.AdminExecuteAsync("UPDATE sal.sales_order SET exemption_pending = false, proforma_collects_itbis = NULL, version = version + 1");

        Assert.Equal((ProformaErrors.MarkInvalid, ProformaErrors.MarkInvalid), (markWithoutChoice.Code, choiceWithoutMark.Code));
        Assert.Equal((true, false), (detail.GetProperty("exemptionPending").GetBoolean(), detail.GetProperty("proformaCollectsItbis").GetBoolean()));
        Assert.Contains("only a DRAFT order changes", afterDraft?.MessageText ?? string.Empty, StringComparison.Ordinal);
    }
}
