using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Sales.Customers;
using Rochell.Sales.Orders;
using Rochell.Sales.Pricing;
using Rochell.Sales.Queries;
using Rochell.Sales.Quotes;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>
/// PRS-02 (PRC-02…08, E-PRC1-1…11, E-PRS-02-1…6): named price lists, the list in the customer's terms, and orders, quotes and cash sales
/// priced from it with GENERAL behind. GENERAL: BLOQUE-6 at 50.00 (the setup's) and BLOQUE-8 at 60.00; HOTELES: BLOQUE-6 at 42.00.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class CustomerPriceListTests(PostgresFixture postgres)
{
    private sealed record World(DeliveryTests.Setup S, Guid Controller, Guid Approver, Guid Block8, Guid Hoteles);

    private static decimal D(JsonElement e) => decimal.Parse(e.GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static async Task<World> WorldAsync(TestHarness h)
    {
        var s = await DeliveryTests.SetupAsync(h);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var approver = await h.SessionWithRolesAsync("APROBADOR_POLITICAS");
        var block8 = Guid.CreateVersion7();
        await h.AdminRequireAsync($"INSERT INTO md.item VALUES ('{block8}', '{h.CompanyId}', 'BLOQUE-8', 'Bloque de 8 pulgadas', 'FINISHED_GOOD', 'un', 'BLOQUE', 'ACTIVE', 1)");
        var general = (await h.RunAsync(new PreparePriceList(h.CompanyId, controller, "g2", [new(s.Block, "un", 50.00m), new(block8, "un", 60.00m)]), new PreparePriceListHandler())).ResultRef;
        await h.RunAsync(new ApprovePriceList(h.CompanyId, approver, "g2a", general), new ApprovePriceListHandler());
        var hoteles = (await h.RunAsync(new CreatePriceList(h.CompanyId, controller, "hl", "hoteles", "Hoteles"), new CreatePriceListHandler())).ResultRef;
        var version = (await h.RunAsync(new PreparePriceList(h.CompanyId, controller, "h1", [new(s.Block, "un", 42.00m)], hoteles), new PreparePriceListHandler())).ResultRef;
        await h.RunAsync(new ApprovePriceList(h.CompanyId, approver, "h1a", version), new ApprovePriceListHandler());
        return new World(s, controller, approver, block8, hoteles);
    }

    private static async Task MoveCustomerAsync(TestHarness h, World w, string key, Guid list)
    {
        var terms = JsonDocument.Parse((await h.RunAsync(new PrepareCustomerTerms(h.CompanyId, w.S.Credit, "move-" + key, w.S.Customer, 30, 1000000.00m, false, list), new PrepareCustomerTermsHandler()))
            .ResultPayload).RootElement.GetProperty("termsVersionId").GetGuid();
        await h.RunAsync(new ApproveCustomerTerms(h.CompanyId, w.Controller, "move-" + key + "-a", terms), new ApproveCustomerTermsHandler());
    }

    private static async Task<string> OrderAsync(TestHarness h, World w, string key, params SalesOrderLineInput[] lines)
    {
        var id = (await h.RunAsync(
            new CreateSalesOrder(h.CompanyId, w.S.Seller, key, w.S.Customer, w.S.Plant, DeliveryTerms.PickupAtPlant, null, null, null, lines), new CreateSalesOrderHandler())).ResultRef;
        var order = JsonDocument.Parse(await h.QueryAsync(new GetSalesOrder(h.CompanyId, w.S.Seller, id), new GetSalesOrderHandler())).RootElement;
        return string.Join(',', order.GetProperty("lines").EnumerateArray().Select(l =>
            $"{l.GetProperty("itemCode").GetString()}:{D(l.GetProperty("unitPrice")):0.00}:{l.GetProperty("priceListCode").GetString()}"));
    }

    [Trait("AcceptancePrs1", "PRC-02")]
    [Fact]
    public async Task PRC02_a_named_list_is_created_and_priced_by_the_Controller_and_approved_by_someone_else_beside_GENERAL()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var draft = (await h.RunAsync(new PreparePriceList(h.CompanyId, w.Controller, "h2", [new(w.S.Block, "un", 41.00m)], w.Hoteles), new PreparePriceListHandler())).ResultRef;
        var own = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApprovePriceList(h.CompanyId, w.Controller, "own", draft), new ApprovePriceListHandler()));
        var twice = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new CreatePriceList(h.CompanyId, w.Controller, "dup", "HOTELES", "Otra"), new CreatePriceListHandler()));
        var headers = JsonDocument.Parse(await h.QueryAsync(new ListPriceListHeaders(h.CompanyId, w.Controller), new ListPriceListHeadersHandler())).RootElement.GetProperty("items");

        // The Controller prepares and never approves (SoD); the database's four eyes cover a person holding both (PriceListFreightSchemaTests).
        Assert.Equal((AuthorizationErrors.NotAuthorized, PriceListErrors.CodeUsed), (own.Code, twice.Code));
        Assert.Equal(
            "GENERAL:ACTIVE:2:False:1,HOTELES:ACTIVE:1:True:0",
            string.Join(',', headers.EnumerateArray().Select(l =>
                $"{l.GetProperty("code").GetString()}:{l.GetProperty("status").GetString()}:{l.GetProperty("activeVersion").GetInt32()}:{l.GetProperty("hasDraft").GetBoolean()}:{l.GetProperty("customers").GetInt32()}")));
    }

    [Trait("AcceptancePrs1", "PRC-03")]
    [Trait("AcceptancePrs1", "PRC-04")]
    [Trait("AcceptancePrs1", "PRC-05")]
    [Fact]
    public async Task PRC03_05_the_customers_list_prices_its_orders_once_approved_GENERAL_fills_the_gaps_and_a_product_in_neither_is_refused()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);

        // Crédito prepares HOTELES for the customer: until the Controller approves, orders keep GENERAL.
        var terms = JsonDocument.Parse((await h.RunAsync(new PrepareCustomerTerms(h.CompanyId, w.S.Credit, "t2", w.S.Customer, 30, 1000000.00m, false, w.Hoteles), new PrepareCustomerTermsHandler()))
            .ResultPayload).RootElement.GetProperty("termsVersionId").GetGuid();
        var before = await OrderAsync(h, w, "o1", new SalesOrderLineInput(w.S.Block, "un", 10m));
        await h.RunAsync(new ApproveCustomerTerms(h.CompanyId, w.Controller, "t2a", terms), new ApproveCustomerTermsHandler());
        var after = await OrderAsync(h, w, "o2", new SalesOrderLineInput(w.S.Block, "un", 10m), new SalesOrderLineInput(w.Block8, "un", 10m));

        // Terms prepared again without naming a list keep HOTELES (E-PRS-02-3).
        var kept = JsonDocument.Parse((await h.RunAsync(new PrepareCustomerTerms(h.CompanyId, w.S.Credit, "t3", w.S.Customer, 45, 1000000.00m, false), new PrepareCustomerTermsHandler()))
            .ResultPayload).RootElement.GetProperty("termsVersionId").GetGuid();
        var block10 = Guid.CreateVersion7();
        await h.AdminRequireAsync($"INSERT INTO md.item VALUES ('{block10}', '{h.CompanyId}', 'BLOQUE-10', 'Bloque de 10 pulgadas', 'FINISHED_GOOD', 'un', 'BLOQUE', 'ACTIVE', 1)");
        var missing = await Assert.ThrowsAsync<DomainException>(() => OrderAsync(h, w, "o3", new SalesOrderLineInput(block10, "un", 10m)));
        var customer = JsonDocument.Parse(await h.QueryAsync(new GetCustomer(h.CompanyId, w.S.Credit, w.S.Customer), new GetCustomerHandler())).RootElement;

        Assert.Equal("BLOQUE-6:50.00:GENERAL", before);
        Assert.Equal("BLOQUE-6:42.00:HOTELES,BLOQUE-8:60.00:GENERAL", after);
        Assert.Equal(OrderErrors.PriceMissing, missing.Code);
        Assert.Equal(
            "HOTELES:DRAFT,HOTELES:ACTIVE,GENERAL:SUPERSEDED",
            string.Join(',', customer.GetProperty("terms").EnumerateArray().OrderByDescending(t => t.GetProperty("version").GetInt32())
                .Select(t => $"{t.GetProperty("priceListCode").GetString()}:{t.GetProperty("status").GetString()}")));
        Assert.Equal(kept, customer.GetProperty("terms").EnumerateArray().First(t => t.GetProperty("status").GetString() == "DRAFT").GetProperty("termsVersionId").GetGuid());
    }

    [Trait("AcceptancePrs1", "PRC-06")]
    [Fact]
    public async Task PRC06_an_order_keeps_its_prices_when_the_list_changes_and_a_new_order_takes_the_new_version()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        await MoveCustomerAsync(h, w, "t", w.Hoteles);
        var first = await OrderAsync(h, w, "o1", new SalesOrderLineInput(w.S.Block, "un", 10m));
        var v2 = (await h.RunAsync(new PreparePriceList(h.CompanyId, w.Controller, "h2", [new(w.S.Block, "un", 40.00m)], w.Hoteles), new PreparePriceListHandler())).ResultRef;
        await h.RunAsync(new ApprovePriceList(h.CompanyId, w.Approver, "h2a", v2), new ApprovePriceListHandler());
        var second = await OrderAsync(h, w, "o2", new SalesOrderLineInput(w.S.Block, "un", 10m));
        var firstAgain = await h.ScalarAsync<string>("SELECT string_agg(unit_price::numeric(19,2)::text, ',' ORDER BY created) FROM (SELECT l.unit_price, o.order_no AS created FROM sal.sales_order o JOIN sal.sales_order_line l USING (sales_order_id)) x");

        Assert.Equal(("BLOQUE-6:42.00:HOTELES", "BLOQUE-6:40.00:HOTELES"), (first, second));
        Assert.Equal("42.00,40.00", firstAgain);
        // GENERAL kept its own version in force: approving HOTELES v2 superseded only HOTELES v1 (E-PRS-02-2).
        Assert.Equal("GENERAL:2:ACTIVE,HOTELES:2:ACTIVE", await h.ScalarAsync<string>(
            "SELECT string_agg(l.code || ':' || v.version || ':' || v.status, ',' ORDER BY l.code) FROM sal.price_list_version v JOIN sal.price_list l USING (price_list_id) WHERE v.status = 'ACTIVE'"));
    }

    [Trait("AcceptancePrs1", "PRC-07")]
    [Trait("AcceptancePrs1", "PRC-08")]
    [Fact]
    public async Task PRC07_08_a_quote_below_the_customers_list_is_special_and_a_cash_sale_prices_from_GENERAL()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        await MoveCustomerAsync(h, w, "t", w.Hoteles);
        async Task<JsonElement> QuoteAsync(string key, decimal price)
        {
            var id = (await h.RunAsync(
                new CreateQuote(h.CompanyId, w.S.Seller, key, w.S.Customer, w.S.Plant, Today(h).AddDays(30), DeliveryTerms.PickupAtPlant, null, null, null, [new(w.S.Block, "un", 10m, price)]),
                new CreateQuoteHandler())).ResultRef;
            return JsonDocument.Parse(await h.QueryAsync(new GetQuote(h.CompanyId, w.S.Seller, id), new GetQuoteHandler())).RootElement.GetProperty("lines")[0];
        }

        var below = await QuoteAsync("q1", 41.00m);
        var above = await QuoteAsync("q2", 43.00m);
        var cash = JsonDocument.Parse(await h.QueryAsync(new PreviewCashSale(h.CompanyId, w.S.Seller, w.S.Plant, [new(w.S.Block, "un", 10m)]), new PreviewCashSaleHandler())).RootElement;

        Assert.Equal("42.0000:True:HOTELES", $"{D(below.GetProperty("listPrice")):0.0000}:{below.GetProperty("special").GetBoolean()}:{below.GetProperty("priceListCode").GetString()}");
        Assert.False(above.GetProperty("special").GetBoolean());
        Assert.Equal(50.00m, D(cash.GetProperty("lines")[0].GetProperty("unitPrice")));
    }

    [Fact]
    public async Task A_list_goes_out_of_use_only_when_no_customer_has_it_GENERAL_never_and_an_inactive_list_is_refused()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        await MoveCustomerAsync(h, w, "t", w.Hoteles);
        var general = await h.ScalarAsync<Guid>("SELECT price_list_id FROM sal.price_list WHERE code = 'GENERAL'");
        var inUse = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new DeactivatePriceList(h.CompanyId, w.Controller, "d1", w.Hoteles, 1), new DeactivatePriceListHandler()));
        var never = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new DeactivatePriceList(h.CompanyId, w.Controller, "d2", general, 1), new DeactivatePriceListHandler()));
        await MoveCustomerAsync(h, w, "t2", general);
        await h.RunAsync(new DeactivatePriceList(h.CompanyId, w.Controller, "d3", w.Hoteles, 1), new DeactivatePriceListHandler());
        var prepare = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PreparePriceList(h.CompanyId, w.Controller, "p", [new(w.S.Block, "un", 40.00m)], w.Hoteles), new PreparePriceListHandler()));
        var terms = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareCustomerTerms(h.CompanyId, w.S.Credit, "t3", w.S.Customer, 30, 1000.00m, false, w.Hoteles), new PrepareCustomerTermsHandler()));
        await h.RunAsync(new ReactivatePriceList(h.CompanyId, w.Controller, "r", w.Hoteles, 2), new ReactivatePriceListHandler());

        Assert.Equal((PriceListErrors.InUse, PriceListErrors.General), (inUse.Code, never.Code));
        Assert.Equal((PriceListErrors.Inactive, PriceListErrors.Inactive), (prepare.Code, terms.Code));
        Assert.Equal(
            "PriceListHeader:null>ACTIVE,ACTIVE>INACTIVE,INACTIVE>ACTIVE",
            "PriceListHeader:" + await h.ScalarAsync<string>(
                $"SELECT string_agg(coalesce(from_state, 'null') || '>' || to_state, ',' ORDER BY state_history_id) FROM core.state_history WHERE aggregate_id = '{w.Hoteles}'"));
    }
}
