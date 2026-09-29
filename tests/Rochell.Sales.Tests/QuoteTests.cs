using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Sales.Customers;
using Rochell.Sales.Orders;
using Rochell.Sales.Queries;
using Rochell.Sales.Quotes;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>QUO1-02: quote commands, price approval, copy, closing and the queries (E-QUO1-02-1…12).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class QuoteTests(PostgresFixture postgres)
{
    private const string SalesItbis = """{"tax_code":"ITBIS","rate":"0.18","effect":"OUTPUT","exempt_item_categories":[]}""";

    private sealed record World(DeliveryTests.Setup S, Guid Approver);

    /// <summary>The delivery setup (BLOQUE-6 at 50.00 on the list in force, an ACTIVE customer) and SALES_ITBIS 18 % in force.</summary>
    private static async Task<World> WorldAsync(TestHarness h)
    {
        var s = await DeliveryTests.SetupAsync(h);
        var actors = await h.FiscalActorsAsync();
        await h.ActivateRuleAsync(actors, "itbis-ventas", "ITBIS_VENTAS", FiscalRuleKinds.SalesItbis, SalesItbis, new DateOnly(2026, 1, 1));
        return new World(s, await h.SessionWithRolesAsync("APROBADOR_POLITICAS"));
    }

    private static DateOnly Today(TestHarness h) => ReceiptTests.Today(h);

    private static CreateQuote Create(TestHarness h, World w, string key, decimal? price = null, DateOnly? validUntil = null, Guid? party = null, Guid? session = null)
        => new(h.CompanyId, session ?? w.S.Seller, key, party ?? w.S.Customer, w.S.Plant, validUntil ?? Today(h).AddDays(30), DeliveryTerms.PickupAtPlant, null, "OC-77",
            "Precios sujetos a disponibilidad", [new QuoteLineInput(w.S.Block, "un", 1000m, price)]);

    private static async Task<Guid> CreateAsync(TestHarness h, World w, string key, decimal? price = null, DateOnly? validUntil = null)
        => (await h.RunAsync(Create(h, w, key, price, validUntil), new CreateQuoteHandler())).ResultRef;

    private static async Task<JsonElement> DetailAsync(TestHarness h, World w, Guid quote)
        => JsonDocument.Parse(await h.QueryAsync(new GetQuote(h.CompanyId, w.S.Seller, quote), new GetQuoteHandler())).RootElement;

    [Trait("AcceptanceQuo1", "QUO-01")]
    [Fact]
    public async Task QUO01_a_quote_is_priced_from_the_list_in_force_and_prints_with_informative_ITBIS()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var supplier = await h.CreateActiveSupplierAsync("101000001", "Solo proveedor");

        var created = await h.RunAsync(Create(h, w, "q"), new CreateQuoteHandler());
        var pastValidity = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Create(h, w, "q-past", validUntil: Today(h).AddDays(-1)), new CreateQuoteHandler()));
        var notCustomer = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Create(h, w, "q-sup", party: supplier), new CreateQuoteHandler()));
        var notPriced = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            Create(h, w, "q-m3") with { Lines = [new QuoteLineInput(w.S.Block, "m3", 10m)] }, new CreateQuoteHandler()));
        var detail = await DetailAsync(h, w, created.ResultRef);
        var print = JsonDocument.Parse(await h.QueryAsync(new GetQuotePrint(h.CompanyId, w.S.Seller, created.ResultRef), new GetQuotePrintHandler())).RootElement;

        Assert.Equal((QuoteErrors.ValidityInvalid, SalesErrors.NotCustomer, OrderErrors.PriceMissing), (pastValidity.Code, notCustomer.Code, notPriced.Code));
        var header = detail.GetProperty("header");
        Assert.Equal("COT-000001|DRAFT|50000.00|False|False", $"{header.GetProperty("quoteNo").GetString()}|{header.GetProperty("status").GetString()}|" +
            $"{header.GetProperty("totalNet").GetString()}|{header.GetProperty("specialPrices").GetBoolean()}|{header.GetProperty("expired").GetBoolean()}");
        Assert.Equal("BLOQUE-6:1000.000000:50.0000:50.0000:50000.00:False", string.Join('|', detail.GetProperty("lines").EnumerateArray().Select(l =>
            $"{l.GetProperty("itemCode").GetString()}:{l.GetProperty("quantity").GetString()}:{l.GetProperty("listPrice").GetString()}:{l.GetProperty("unitPrice").GetString()}:" +
            $"{l.GetProperty("netAmount").GetString()}:{l.GetProperty("special").GetBoolean()}")));
        Assert.Equal("OC-77|Precios sujetos a disponibilidad", $"{detail.GetProperty("customerRef").GetString()}|{detail.GetProperty("notes").GetString()}");

        // 1,000 blocks × 50.00 = 50,000.00; ITBIS 18 % = 9,000.00 (informative, E-QUO1-5).
        Assert.Equal("50000.00|9000.00|59000.00|131925332", $"{print.GetProperty("netTotal").GetString()}|{print.GetProperty("itbisTotal").GetString()}|" +
            $"{print.GetProperty("total").GetString()}|{print.GetProperty("customerRnc").GetString()}");
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM tax.tax_determination WHERE subject_type = 'Quote'"));
    }

    [Trait("AcceptanceQuo1", "QUO-02")]
    [Fact]
    public async Task QUO02_a_price_below_the_list_is_sent_only_after_the_policy_approver_approves_the_current_lines()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var quote = await CreateAsync(h, w, "q", 45.00m);
        var listPriced = await CreateAsync(h, w, "q-list");

        var unapproved = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SendQuote(h.CompanyId, w.S.Seller, "send-0", quote, 1), new SendQuoteHandler()));
        var nothing = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SubmitQuoteForApproval(h.CompanyId, w.S.Seller, "sub-0", listPriced, 1), new SubmitQuoteForApprovalHandler()));
        await h.RunAsync(new SubmitQuoteForApproval(h.CompanyId, w.S.Seller, "sub", quote, 1), new SubmitQuoteForApprovalHandler());
        var edit = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new UpdateDraftQuote(h.CompanyId, w.S.Seller, "upd-0", quote, 2, w.S.Plant, Today(h).AddDays(30), DeliveryTerms.PickupAtPlant, null, null, null, [new QuoteLineInput(w.S.Block, "un", 900m, 45.00m)]),
            new UpdateDraftQuoteHandler()));
        var sellerApproves = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveQuotePrices(h.CompanyId, w.S.Seller, "apr-0", quote, 2), new ApproveQuotePricesHandler()));
        await h.RunAsync(new ReturnQuoteToDraft(h.CompanyId, w.Approver, "ret", quote, 2, "Revisar el volumen"), new ReturnQuoteToDraftHandler());
        await h.RunAsync(new SubmitQuoteForApproval(h.CompanyId, w.S.Seller, "sub-2", quote, 3), new SubmitQuoteForApprovalHandler());
        await h.RunAsync(new ApproveQuotePrices(h.CompanyId, w.Approver, "apr", quote, 4), new ApproveQuotePricesHandler());
        var approved = await DetailAsync(h, w, quote);

        // New lines after the approval (900 at 45.00) need a new one; approved again, the quote goes out and a list-priced one needs none.
        await h.RunAsync(
            new UpdateDraftQuote(h.CompanyId, w.S.Seller, "upd", quote, 5, w.S.Plant, Today(h).AddDays(30), DeliveryTerms.PickupAtPlant, null, null, null, [new QuoteLineInput(w.S.Block, "un", 900m, 45.00m)]),
            new UpdateDraftQuoteHandler());
        var stale = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SendQuote(h.CompanyId, w.S.Seller, "send-1", quote, 6), new SendQuoteHandler()));
        await h.RunAsync(new SubmitQuoteForApproval(h.CompanyId, w.S.Seller, "sub-3", quote, 6), new SubmitQuoteForApprovalHandler());
        await h.RunAsync(new ApproveQuotePrices(h.CompanyId, w.Approver, "apr-2", quote, 7), new ApproveQuotePricesHandler());
        await h.RunAsync(new SendQuote(h.CompanyId, w.S.Seller, "send", quote, 8), new SendQuoteHandler());
        await h.RunAsync(new SendQuote(h.CompanyId, w.S.Seller, "send-list", listPriced, 1), new SendQuoteHandler());

        Assert.Equal((QuoteErrors.PriceApprovalRequired, QuoteErrors.NothingToApprove, QuoteErrors.InvalidState, AuthorizationErrors.NotAuthorized, QuoteErrors.PriceApprovalRequired),
            (unapproved.Code, nothing.Code, edit.Code, sellerApproves.Code, stale.Code));
        Assert.Equal("DRAFT|True|True", $"{approved.GetProperty("header").GetProperty("status").GetString()}|{approved.GetProperty("priceApprovalCurrent").GetBoolean()}|" +
            $"{approved.GetProperty("header").GetProperty("specialPrices").GetBoolean()}");
        var sent = await DetailAsync(h, w, quote);
        Assert.Equal("SENT|40500.00|True", $"{sent.GetProperty("header").GetProperty("status").GetString()}|{sent.GetProperty("header").GetProperty("totalNet").GetString()}|" +
            $"{sent.GetProperty("priceApprovalCurrent").GetBoolean()}");
        Assert.Equal("DRAFT,PENDING_APPROVAL,DRAFT,PENDING_APPROVAL,DRAFT,PENDING_APPROVAL,DRAFT,SENT",
            string.Join(',', sent.GetProperty("history").EnumerateArray().Select(x => x.GetProperty("to").GetString())));
        Assert.Equal("SENT", await h.ScalarAsync<string>("SELECT status FROM sal.quote WHERE quote_id = @q", ("q", listPriced)));
    }

    [Trait("AcceptanceQuo1", "QUO-03")]
    [Fact]
    public async Task QUO03_a_sent_quote_is_frozen_and_a_copy_carries_its_lines_and_prices_into_a_new_draft()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var quote = await CreateAsync(h, w, "q", 55.00m);
        await h.RunAsync(new SendQuote(h.CompanyId, w.S.Seller, "send", quote, 1), new SendQuoteHandler());

        var edit = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new UpdateDraftQuote(h.CompanyId, w.S.Seller, "upd", quote, 2, w.S.Plant, Today(h).AddDays(30), DeliveryTerms.PickupAtPlant, null, null, null, [new QuoteLineInput(w.S.Block, "un", 10m)]),
            new UpdateDraftQuoteHandler()));
        var copy = (await h.RunAsync(new CopyQuote(h.CompanyId, w.S.Seller, "copy", quote, Today(h).AddDays(45)), new CopyQuoteHandler())).ResultRef;
        await h.RunAsync(new CancelQuote(h.CompanyId, w.S.Seller, "cancel", quote, 2, "Reemplazada por la copia"), new CancelQuoteHandler());
        var copied = await DetailAsync(h, w, copy);
        var original = await DetailAsync(h, w, quote);

        Assert.Equal(QuoteErrors.InvalidState, edit.Code);
        Assert.Equal($"COT-000002|DRAFT|55000.00|{Today(h).AddDays(45):yyyy-MM-dd}|COT-000001|OC-77", $"{copied.GetProperty("header").GetProperty("quoteNo").GetString()}|" +
            $"{copied.GetProperty("header").GetProperty("status").GetString()}|{copied.GetProperty("header").GetProperty("totalNet").GetString()}|" +
            $"{copied.GetProperty("header").GetProperty("validUntil").GetString()}|{copied.GetProperty("copiedFrom").GetProperty("quoteNo").GetString()}|{copied.GetProperty("customerRef").GetString()}");
        Assert.Equal("1000.000000:50.0000:55.0000", string.Join('|', copied.GetProperty("lines").EnumerateArray().Select(l =>
            $"{l.GetProperty("quantity").GetString()}:{l.GetProperty("listPrice").GetString()}:{l.GetProperty("unitPrice").GetString()}")));
        Assert.Equal("CANCELLED|Reemplazada por la copia|COT-000002", $"{original.GetProperty("header").GetProperty("status").GetString()}|" +
            $"{original.GetProperty("closingReason").GetString()}|{Assert.Single(original.GetProperty("copies").EnumerateArray()).GetProperty("quoteNo").GetString()}");
    }

    [Trait("AcceptanceQuo1", "QUO-07")]
    [Fact]
    public async Task QUO07_a_sent_quote_is_lost_only_with_a_reason_and_an_expired_one_is_listed_and_not_sent()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var w = await WorldAsync(h);
        var lost = await CreateAsync(h, w, "q-lost");
        var aging = await CreateAsync(h, w, "q-aging", validUntil: Today(h).AddDays(1));
        var late = await CreateAsync(h, w, "q-late", validUntil: Today(h).AddDays(1));
        await h.RunAsync(new SendQuote(h.CompanyId, w.S.Seller, "send-lost", lost, 1), new SendQuoteHandler());
        await h.RunAsync(new SendQuote(h.CompanyId, w.S.Seller, "send-aging", aging, 1), new SendQuoteHandler());

        var noReason = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new MarkQuoteLost(h.CompanyId, w.S.Seller, "lost-0", lost, 2, " "), new MarkQuoteLostHandler()));
        await h.RunAsync(new MarkQuoteLost(h.CompanyId, w.S.Seller, "lost", lost, 2, "El cliente compró a otro suplidor"), new MarkQuoteLostHandler());
        var lostAgain = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new CancelQuote(h.CompanyId, w.S.Seller, "cancel-lost", lost, 3, "Error"), new CancelQuoteHandler()));
        clock.Advance(TimeSpan.FromDays(3));
        var seller = await h.SessionWithRolesAsync("VENDEDOR");
        var expiredSend = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SendQuote(h.CompanyId, seller, "send-late", late, 1), new SendQuoteHandler()));
        var expired = JsonDocument.Parse(await h.QueryAsync(new ListQuotes(h.CompanyId, seller, ExpiredOnly: true), new ListQuotesHandler())).RootElement;
        var all = JsonDocument.Parse(await h.QueryAsync(new ListQuotes(h.CompanyId, seller), new ListQuotesHandler())).RootElement;

        Assert.Equal((QuoteErrors.ReasonRequired, QuoteErrors.InvalidState, QuoteErrors.Expired), (noReason.Code, lostAgain.Code, expiredSend.Code));
        Assert.Equal("COT-000002:SENT", string.Join('|', expired.GetProperty("items").EnumerateArray().Select(q => $"{q.GetProperty("quoteNo").GetString()}:{q.GetProperty("status").GetString()}")));
        Assert.Equal("COT-000003:DRAFT:False|COT-000002:SENT:True|COT-000001:LOST:False", string.Join('|', all.GetProperty("items").EnumerateArray()
            .Select(q => $"{q.GetProperty("quoteNo").GetString()}:{q.GetProperty("status").GetString()}:{q.GetProperty("expired").GetBoolean()}")));
        Assert.Equal("El cliente compró a otro suplidor", await h.ScalarAsync<string>("SELECT closing_reason FROM sal.quote WHERE quote_id = @q", ("q", lost)));
    }

    private static async Task<Guid> SentAsync(TestHarness h, World w, string key, decimal? price = null, DateOnly? validUntil = null, Guid? party = null)
    {
        var quote = (await h.RunAsync(Create(h, w, key, price, validUntil, party), new CreateQuoteHandler())).ResultRef;
        var version = 1L;
        if (price < 50.00m)
        {
            await h.RunAsync(new SubmitQuoteForApproval(h.CompanyId, w.S.Seller, key + "-sub", quote, 1), new SubmitQuoteForApprovalHandler());
            await h.RunAsync(new ApproveQuotePrices(h.CompanyId, w.Approver, key + "-apr", quote, 2), new ApproveQuotePricesHandler());
            version = 3;
        }

        await h.RunAsync(new SendQuote(h.CompanyId, w.S.Seller, key + "-send", quote, version), new SendQuoteHandler());
        return quote;
    }

    [Trait("AcceptanceQuo1", "QUO-04")]
    [Fact]
    public async Task QUO04_a_sent_quote_becomes_a_draft_order_at_its_quoted_prices_that_keeps_them_and_goes_through_credit()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var quote = await SentAsync(h, w, "q", 45.00m);

        var converted = await h.RunAsync(new ConvertQuote(h.CompanyId, w.S.Seller, "convert", quote, 4), new ConvertQuoteHandler());
        var order = converted.ResultRef;
        var draft = JsonDocument.Parse(await h.QueryAsync(new GetSalesOrder(h.CompanyId, w.S.Seller, order), new GetSalesOrderHandler())).RootElement;
        var quoteDetail = await DetailAsync(h, w, quote);

        // E-QUO1-03-4: 800 instead of 1,000 keeps the quoted 45.00 (the list says 50.00): 800 × 45.00 = 36,000.00; then credit confirms it.
        await h.RunAsync(
            new UpdateSalesOrderDraft(h.CompanyId, w.S.Seller, "upd", order, 1, w.S.Plant, DeliveryTerms.PickupAtPlant, null, null, "OC-77", [new SalesOrderLineInput(w.S.Block, "un", 800m)]),
            new UpdateSalesOrderDraftHandler());
        await h.RunAsync(new SubmitForCredit(h.CompanyId, w.S.Seller, "credit", order, 2), new SubmitForCreditHandler());
        var confirmed = JsonDocument.Parse(await h.QueryAsync(new GetSalesOrder(h.CompanyId, w.S.Seller, order), new GetSalesOrderHandler())).RootElement;

        Assert.Equal("PV-000001|DRAFT|45000.00|COT-000001|OC-77", $"{draft.GetProperty("header").GetProperty("orderNo").GetString()}|{draft.GetProperty("header").GetProperty("status").GetString()}|" +
            $"{draft.GetProperty("header").GetProperty("totalNet").GetString()}|{draft.GetProperty("header").GetProperty("quoteNo").GetString()}|{draft.GetProperty("customerPoRef").GetString()}");
        Assert.Equal("1000.000000:45.0000", string.Join('|', draft.GetProperty("lines").EnumerateArray().Select(l => $"{l.GetProperty("qtyOrdered").GetString()}:{l.GetProperty("unitPrice").GetString()}")));
        Assert.Equal($"CONVERTED|PV-000001|{order}", $"{quoteDetail.GetProperty("header").GetProperty("status").GetString()}|{quoteDetail.GetProperty("orderNo").GetString()}|" +
            $"{quoteDetail.GetProperty("salesOrderId").GetString()}");
        Assert.Equal("CONFIRMED|36000.00|800.000000:45.0000", $"{confirmed.GetProperty("header").GetProperty("status").GetString()}|{confirmed.GetProperty("header").GetProperty("totalNet").GetString()}|" +
            string.Join('|', confirmed.GetProperty("lines").EnumerateArray().Select(l => $"{l.GetProperty("qtyOrdered").GetString()}:{l.GetProperty("unitPrice").GetString()}")));
        Assert.Equal("SalesOrderCreated:" + quote, await h.ScalarAsync<string>(
            "SELECT event_type || ':' || (payload ->> 'quoteId') FROM core.domain_event WHERE aggregate_id = @o AND event_type = 'SalesOrderCreated'", ("o", order)));
    }

    [Trait("AcceptanceQuo1", "QUO-05")]
    [Fact]
    public async Task QUO05_an_expired_quote_or_one_of_a_customer_not_yet_active_is_not_converted()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var w = await WorldAsync(h);
        var prospect = (await h.RunAsync(new CreateCustomer(h.CompanyId, w.S.Seller, "prospect", "101000001", "Hotel en proyecto"), new CreateCustomerHandler())).ResultRef;
        var ofProspect = await SentAsync(h, w, "q-prospect", party: prospect);
        var expiring = await SentAsync(h, w, "q-expiring", validUntil: Today(h).AddDays(1));
        var draft = await CreateAsync(h, w, "q-draft");

        var notActive = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ConvertQuote(h.CompanyId, w.S.Seller, "c-prospect", ofProspect, 2), new ConvertQuoteHandler()));
        var notSent = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ConvertQuote(h.CompanyId, w.S.Seller, "c-draft", draft, 1), new ConvertQuoteHandler()));
        clock.Advance(TimeSpan.FromDays(3));
        var seller = await h.SessionWithRolesAsync("VENDEDOR");
        var expired = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ConvertQuote(h.CompanyId, seller, "c-expired", expiring, 2), new ConvertQuoteHandler()));

        Assert.Equal((OrderErrors.CustomerNotActive, QuoteErrors.InvalidState, QuoteErrors.Expired), (notActive.Code, notSent.Code, expired.Code));
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM sal.sales_order WHERE quote_id IS NOT NULL"));
    }

    [Trait("AcceptanceQuo1", "QUO-06")]
    [Fact]
    public async Task QUO06_two_conversions_at_once_create_one_order()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var quote = await SentAsync(h, w, "q");
        var seller2 = await h.SessionWithRolesAsync("VENDEDOR");

        var outcomes = await Task.WhenAll(new[] { (w.S.Seller, "a"), (seller2, "b") }.Select(x => Task.Run(async () =>
        {
            try
            {
                await h.RunAsync(new ConvertQuote(h.CompanyId, x.Item1, "convert-" + x.Item2, quote, 2), new ConvertQuoteHandler());
                return (string?)null;
            }
            catch (DomainException ex)
            {
                return ex.Code;
            }
        })));

        Assert.Equal(1, outcomes.Count(o => o is null));
        Assert.Equal(QuoteErrors.VersionConflict, Assert.Single(outcomes, o => o is not null));
        Assert.Equal("1:CONVERTED", await h.ScalarAsync<string>(
            "SELECT (SELECT count(*) FROM sal.sales_order WHERE quote_id = @q)::text || ':' || status FROM sal.quote WHERE quote_id = @q", ("q", quote)));
    }
}
