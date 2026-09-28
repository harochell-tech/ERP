using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Sales.Customers;
using Rochell.Sales.Orders;
using Rochell.Sales.Pricing;
using Rochell.Sales.Queries;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>VS3-03: sales order and credit check (SAL-01, SAL-02; E-VS3-14, E-VS3-03-1…10).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SalesOrderTests(PostgresFixture postgres)
{
    private sealed record Setup(Guid Seller, Guid Credit, Guid Controller, Guid Customer, Guid Plant, Guid Block, Guid Paver);

    /// <summary>An ACTIVE customer with limit 100,000.00; the list prices the 6" block at 50.00 per unit and the paver at 2,000.00 per m3.</summary>
    private static async Task<Setup> SetupAsync(TestHarness h, decimal limit = 100000.00m, bool hold = false, int overdueBlock = 30)
    {
        var plant = await h.CreatePlantAsync();
        var block = Guid.CreateVersion7();
        var paver = Guid.CreateVersion7();
        await h.AdminRequireAsync(
            $"""
            INSERT INTO md.item VALUES ('{block}', '{h.CompanyId}', 'BLOQUE-6', 'Bloque de 6 pulgadas', 'FINISHED_GOOD', 'un', 'BLOQUE', 'ACTIVE', 1);
            INSERT INTO md.item VALUES ('{paver}', '{h.CompanyId}', 'ADOQUIN-H', 'Adoquín holandés', 'FINISHED_GOOD', 'un', 'ADOQUIN', 'ACTIVE', 1);
            INSERT INTO md.uom_conversion VALUES ('{h.CompanyId}', '{paver}', 'm3', 'un', 40, current_date - 1, NULL);
            """);
        await h.CreateActivePolicyAsync("CREDIT", new Dictionary<string, string> { ["overdue_days_block"] = overdueBlock.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        var seller = await h.SessionWithRolesAsync("VENDEDOR");
        var credit = await h.SessionWithRolesAsync("CREDITO");
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var approver = await h.SessionWithRolesAsync("APROBADOR_POLITICAS");
        var list = (await h.RunAsync(new PreparePriceList(h.CompanyId, controller, "pl", [new(block, "un", 50.00m), new(paver, "m3", 2000.00m)]), new PreparePriceListHandler())).ResultRef;
        await h.RunAsync(new ApprovePriceList(h.CompanyId, approver, "pl-a", list), new ApprovePriceListHandler());
        var customer = (await h.RunAsync(new CreateCustomer(h.CompanyId, seller, "c", "131925332", "Constructora Uno"), new CreateCustomerHandler())).ResultRef;
        var terms = JsonDocument.Parse((await h.RunAsync(new PrepareCustomerTerms(h.CompanyId, credit, "t", customer, 30, limit, hold), new PrepareCustomerTermsHandler())).ResultPayload)
            .RootElement.GetProperty("termsVersionId").GetGuid();
        await h.RunAsync(new ApproveCustomerTerms(h.CompanyId, controller, "t-a", terms), new ApproveCustomerTermsHandler());
        await h.RunAsync(new ActivateCustomer(h.CompanyId, credit, "act", customer, 1), new ActivateCustomerHandler());
        return new Setup(seller, credit, controller, customer, plant, block, paver);
    }

    private static Task<CommandResult> Create(TestHarness h, Setup s, string key, params SalesOrderLineInput[] lines)
        => h.RunAsync(new CreateSalesOrder(h.CompanyId, s.Seller, key, s.Customer, s.Plant, DeliveryTerms.PickupAtPlant, null, null, "OC-77", lines), new CreateSalesOrderHandler());

    private static async Task<JsonElement> Submit(TestHarness h, Setup s, Guid order, string key, long version = 1)
        => JsonDocument.Parse((await h.RunAsync(new SubmitForCredit(h.CompanyId, s.Seller, key, order, version), new SubmitForCreditHandler())).ResultPayload).RootElement;

    [Trait("AcceptanceVs3", "SAL-01")]
    [Fact]
    public async Task SAL01_an_order_within_the_limit_is_auto_approved_and_confirmed()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        var order = await Create(h, s, "o", new SalesOrderLineInput(s.Block, "un", 1000m)); // 50,000.00

        var result = await Submit(h, s, order.ResultRef, "submit");

        Assert.Equal(("CONFIRMED", "AUTO_APPROVED", "0.00", "100000.00"), (result.GetProperty("status").GetString(), result.GetProperty("decision").GetString(),
            result.GetProperty("exposure").GetString(), result.GetProperty("creditLimit").GetString()));
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetSalesOrder(h.CompanyId, s.Credit, order.ResultRef), new GetSalesOrderHandler())).RootElement;
        Assert.Equal(("PV-000001", "50000.00", "OC-77"), (detail.GetProperty("header").GetProperty("orderNo").GetString(), detail.GetProperty("header").GetProperty("totalNet").GetString(),
            detail.GetProperty("customerPoRef").GetString()));
        Assert.Equal("AUTO_APPROVED:", string.Join(':', detail.GetProperty("creditChecks")[0].GetProperty("decision").GetString(), detail.GetProperty("creditChecks")[0].GetProperty("outcome").GetString()));
        Assert.Equal("DRAFT,CONFIRMED", string.Join(',', detail.GetProperty("history").EnumerateArray().Select(x => x.GetProperty("to").GetString())));
        var exposure = JsonDocument.Parse(await h.QueryAsync(new GetCustomerExposure(h.CompanyId, s.Seller, s.Customer), new GetCustomerExposureHandler())).RootElement;
        Assert.Equal(("50000.00", "50000.00"), (exposure.GetProperty("exposure").GetString(), exposure.GetProperty("available").GetString()));
    }

    [Trait("AcceptanceVs3", "SAL-02")]
    [Fact]
    public async Task SAL02_an_order_above_the_available_credit_waits_for_credit_which_the_seller_cannot_give()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        var first = await Create(h, s, "o1", new SalesOrderLineInput(s.Paver, "m3", 45m)); // 90,000.00
        await Submit(h, s, first.ResultRef, "s1");
        var second = await Create(h, s, "o2", new SalesOrderLineInput(s.Block, "un", 400m)); // 20,000.00 → exposure 90,000 + 20,000 > 100,000

        var pending = await Submit(h, s, second.ResultRef, "s2");
        var sellerApproves = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveCredit(h.CompanyId, s.Seller, "self", second.ResultRef, 2), new ApproveCreditHandler()));
        var noReason = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RejectCredit(h.CompanyId, s.Credit, "r0", second.ResultRef, 2, ""), new RejectCreditHandler()));
        await h.RunAsync(new ApproveCredit(h.CompanyId, s.Credit, "approve", second.ResultRef, 2), new ApproveCreditHandler());

        Assert.Equal(("PENDING_CREDIT", "NEEDS_APPROVAL", "90000.00"), (pending.GetProperty("status").GetString(), pending.GetProperty("decision").GetString(), pending.GetProperty("exposure").GetString()));
        Assert.Equal((AuthorizationErrors.NotAuthorized, OrderErrors.ReasonRequired), (sellerApproves.Code, noReason.Code));
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetSalesOrder(h.CompanyId, s.Seller, second.ResultRef), new GetSalesOrderHandler())).RootElement;
        Assert.Equal("CONFIRMED", detail.GetProperty("header").GetProperty("status").GetString());
        Assert.Equal("NEEDS_APPROVAL:APPROVED:90000.00:20000.00", string.Join(':', detail.GetProperty("creditChecks")[0].GetProperty("decision").GetString(),
            detail.GetProperty("creditChecks")[0].GetProperty("outcome").GetString(), detail.GetProperty("creditChecks")[0].GetProperty("exposureOrders").GetString(),
            detail.GetProperty("creditChecks")[0].GetProperty("orderAmount").GetString()));
    }

    [Fact]
    public async Task A_rejected_order_returns_to_draft_is_corrected_and_resubmitted_and_a_credit_hold_always_needs_credit()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h, hold: true);
        var order = await Create(h, s, "o", new SalesOrderLineInput(s.Block, "un", 10m));

        var held = await Submit(h, s, order.ResultRef, "s1");
        await h.RunAsync(new RejectCredit(h.CompanyId, s.Credit, "reject", order.ResultRef, 2, "Cliente con cheques devueltos"), new RejectCreditHandler());
        await h.RunAsync(
            new UpdateSalesOrderDraft(h.CompanyId, s.Seller, "update", order.ResultRef, 3, s.Plant, DeliveryTerms.DeliveredOwnTransport, "Obra Punta Cana, lote 12", null, null,
                [new(s.Block, "un", 20m), new(s.Paver, "m3", 1.5m)]),
            new UpdateSalesOrderDraftHandler());
        var resubmitted = await Submit(h, s, order.ResultRef, "s2", 4);

        Assert.Equal(("PENDING_CREDIT", "PENDING_CREDIT"), (held.GetProperty("status").GetString(), resubmitted.GetProperty("status").GetString()));
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetSalesOrder(h.CompanyId, s.Seller, order.ResultRef), new GetSalesOrderHandler())).RootElement;
        Assert.Equal(("4000.00", 2, "Obra Punta Cana, lote 12"), (detail.GetProperty("header").GetProperty("totalNet").GetString(), detail.GetProperty("lines").GetArrayLength(),
            detail.GetProperty("siteAddress").GetString()));
        Assert.Equal("REJECTED:Cliente con cheques devueltos|", string.Join('|', detail.GetProperty("creditChecks").EnumerateArray().Select(c =>
            c.GetProperty("outcome").GetString() is { } o ? $"{o}:{c.GetProperty("reason").GetString()}" : "")));
        Assert.Equal("DRAFT,PENDING_CREDIT,DRAFT,PENDING_CREDIT", string.Join(',', detail.GetProperty("history").EnumerateArray().Select(x => x.GetProperty("to").GetString())));
    }

    [Fact]
    public async Task Orders_need_list_prices_a_site_for_site_delivery_an_active_customer_and_cancel_only_without_deliveries()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);

        var noPrice = await Assert.ThrowsAsync<DomainException>(() => Create(h, s, "p", new SalesOrderLineInput(s.Block, "m3", 1m)));
        var duplicate = await Assert.ThrowsAsync<DomainException>(() => Create(h, s, "d", new SalesOrderLineInput(s.Block, "un", 1m), new SalesOrderLineInput(s.Block, "un", 2m)));
        var noSite = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new CreateSalesOrder(h.CompanyId, s.Seller, "site", s.Customer, s.Plant, DeliveryTerms.DeliveredOwnTransport, " ", null, null, [new(s.Block, "un", 1m)]), new CreateSalesOrderHandler()));
        var draftCustomer = (await h.RunAsync(new CreateCustomer(h.CompanyId, s.Seller, "c2", "101000001", "Cliente nuevo"), new CreateCustomerHandler())).ResultRef;
        var draftOrder = (await h.RunAsync(new CreateSalesOrder(h.CompanyId, s.Seller, "o2", draftCustomer, s.Plant, DeliveryTerms.PickupAtPlant, null, null, null, [new(s.Block, "un", 1m)]), new CreateSalesOrderHandler())).ResultRef;
        var notActive = await Assert.ThrowsAsync<DomainException>(() => Submit(h, s, draftOrder, "s-draft"));
        var confirmed = await Create(h, s, "o", new SalesOrderLineInput(s.Block, "un", 1m));
        await Submit(h, s, confirmed.ResultRef, "s");
        var edit = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new UpdateSalesOrderDraft(h.CompanyId, s.Seller, "edit", confirmed.ResultRef, 2, s.Plant, DeliveryTerms.PickupAtPlant, null, null, null, [new(s.Block, "un", 5m)]), new UpdateSalesOrderDraftHandler()));
        await h.RunAsync(new CancelSalesOrder(h.CompanyId, s.Seller, "cancel", confirmed.ResultRef, 2, "El cliente desistió"), new CancelSalesOrderHandler());

        Assert.Equal(
            (OrderErrors.PriceMissing, SalesErrors.DuplicateLine, OrderErrors.SiteRequired, OrderErrors.CustomerNotActive, SalesErrors.InvalidState),
            (noPrice.Code, duplicate.Code, noSite.Code, notActive.Code, edit.Code));
        var list = JsonDocument.Parse(await h.QueryAsync(new ListSalesOrders(h.CompanyId, s.Seller, "CANCELLED"), new ListSalesOrdersHandler())).RootElement.GetProperty("items");
        Assert.Equal("PV-000002", list[0].GetProperty("orderNo").GetString());
    }

    [Fact]
    public async Task Concurrent_submissions_of_one_customer_never_auto_approve_above_the_limit()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        var orders = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            orders.Add((await Create(h, s, $"o{i}", new SalesOrderLineInput(s.Block, "un", 500m))).ResultRef); // 25,000.00 each, 150,000.00 in total
        }

        var results = await Task.WhenAll(orders.Select((o, i) => Task.Run(() => Submit(h, s, o, $"s{i}"))));

        Assert.Equal(4, results.Count(r => r.GetProperty("decision").GetString() == "AUTO_APPROVED")); // 4 × 25,000 = the whole 100,000 limit
        Assert.Equal("100000.00", await h.ScalarAsync<string>("SELECT sum(total_net)::numeric(19,2)::text FROM sal.sales_order WHERE status = 'CONFIRMED'"));
    }
}
