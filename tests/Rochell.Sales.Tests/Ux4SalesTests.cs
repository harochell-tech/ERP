using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Queries;
using Rochell.Sales.Customers;
using Rochell.Sales.Invoices;
using Rochell.Sales.Orders;
using Rochell.Sales.Queries;
using Rochell.Sales.Quotes;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Rochell.Treasury.BankAccounts;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>UX4-01: order and quote previews (E-UX4-3), credit preview (E-UX4-4), receipt suggestion (E-UX4-10), AR aging totals and bank alias (E-UX4-2/6).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class Ux4SalesTests(PostgresFixture postgres)
{
    private static async Task<JsonElement> Query<TQuery>(TestHarness h, TQuery query, IQueryHandler<TQuery> handler)
        where TQuery : IQuery
        => JsonDocument.Parse(await h.QueryAsync(query, handler)).RootElement;

    [Fact]
    public async Task Order_and_quote_previews_price_the_draft_and_estimate_ITBIS_without_writing()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await DeliveryTests.SetupAsync(h); // list: BLOQUE-6 at 50.00 per unit; no sales ITBIS rule yet
        var closed = await Query(h, new PreviewSalesOrder(h.CompanyId, s.Seller, s.Plant, [new(s.Block, "un", 10m)]), new PreviewSalesOrderHandler());
        var actors = await h.FiscalActorsAsync();
        await h.ActivateRuleAsync(actors, "itbis-ventas", "ITBIS_VENTAS", FiscalRuleKinds.SalesItbis, """{"tax_code":"ITBIS","rate":"0.18","effect":"OUTPUT","exempt_item_categories":[]}""", new DateOnly(2026, 1, 1));
        var (orders, quotes) = (await h.CountAsync("sal.sales_order"), await h.CountAsync("sal.quote"));

        var order = await Query(h, new PreviewSalesOrder(h.CompanyId, s.Seller, s.Plant, [new(s.Block, "un", 10.5m)]), new PreviewSalesOrderHandler());
        var quote = await Query(h, new PreviewQuote(h.CompanyId, s.Seller, s.Plant, [new(s.Block, "un", 100m, 45.00m)]), new PreviewQuoteHandler());
        var missing = await Assert.ThrowsAsync<DomainException>(() => h.QueryAsync(new PreviewSalesOrder(h.CompanyId, s.Seller, s.Plant, [new(s.Block, "m3", 1m)]), new PreviewSalesOrderHandler()));
        var dispatch = await Assert.ThrowsAsync<DomainException>(() => h.QueryAsync(new PreviewQuote(h.CompanyId, s.Dispatch, s.Plant, [new(s.Block, "un", 1m)]), new PreviewQuoteHandler()));

        // Closed gate: 10 × 50.00 = 500.00 net, no ITBIS and why.
        Assert.Equal(("500.00", JsonValueKind.Null, JsonValueKind.Null, TaxErrors.FiscalGateClosed), (closed.GetProperty("netTotal").GetString(), closed.GetProperty("itbisTotal").ValueKind,
            closed.GetProperty("total").ValueKind, closed.GetProperty("itbisUnavailableCode").GetString()));

        // 10.5 × 50.00 = 525.00; ITBIS 18 % = 94.50; total 619.50.
        Assert.Equal(("525.00", "94.50", "619.50"), (order.GetProperty("netTotal").GetString(), order.GetProperty("itbisTotal").GetString(), order.GetProperty("total").GetString()));

        // A quoted 45.00 below the list's 50.00 is a special price: 100 × 45.00 = 4 500.00; ITBIS 810.00.
        var line = quote.GetProperty("lines")[0];
        Assert.Equal(("50.0000", "45.00", true, "4500.00", "810.00"), (line.GetProperty("listPrice").GetString(), line.GetProperty("unitPrice").GetString(), line.GetProperty("specialPrice").GetBoolean(),
            line.GetProperty("netAmount").GetString(), line.GetProperty("itbis").GetString()));
        Assert.Equal(OrderErrors.PriceMissing, missing.Code);
        Assert.Equal("NOT_AUTHORIZED", dispatch.Code); // quote:manage, like the quote form
        Assert.Equal((orders, quotes), (await h.CountAsync("sal.sales_order"), await h.CountAsync("sal.quote")));
    }

    [Fact]
    public async Task The_credit_preview_follows_the_credit_check_rule_without_recording_it()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await ReceiptTests.WorldAsync(h); // limit 1 000 000.00; FA-000001 open 59 000.00, due in 30 days; overdue block 30 days
        var prospect = (await h.RunAsync(new CreateCustomer(h.CompanyId, w.S.Seller, "c2", "101000011", "Prospecto, S.R.L."), new CreateCustomerHandler())).ResultRef;
        GetCreditPreview Preview(decimal amount, Guid? party = null) => new(h.CompanyId, w.S.Seller, party ?? w.S.Customer, amount);

        var fits = await Query(h, Preview(941000.00m), new GetCreditPreviewHandler());
        var over = await Query(h, Preview(941000.01m), new GetCreditPreviewHandler());
        var noTerms = await Query(h, Preview(1m, prospect), new GetCreditPreviewHandler());
        await h.AdminRequireAsync(
            $"""
            BEGIN; SET LOCAL session_replication_role = replica;
            UPDATE fin.ar_document SET doc_date = '{ReceiptTests.Today(h).AddDays(-61):yyyy-MM-dd}', due_date = '{ReceiptTests.Today(h).AddDays(-31):yyyy-MM-dd}' WHERE ar_doc_id = '{w.ArDoc}';
            COMMIT;
            """);
        var overdue = await Query(h, Preview(1m), new GetCreditPreviewHandler());
        var unknown = await Assert.ThrowsAsync<DomainException>(() => h.QueryAsync(Preview(1m, Guid.CreateVersion7()), new GetCreditPreviewHandler()));

        // Exposure 59 000.00: 59 000 + 941 000.00 = the limit (fits); one cent more does not.
        Assert.Equal(("59000.00", "941000.00", "0.00", true), (fits.GetProperty("exposure").GetString(), fits.GetProperty("available").GetString(),
            fits.GetProperty("availableAfter").GetString(), fits.GetProperty("fits").GetBoolean()));
        Assert.Equal(("-0.01", false, "CREDIT_LIMIT_EXCEEDED"), (over.GetProperty("availableAfter").GetString(), over.GetProperty("fits").GetBoolean(), Reasons(over)));
        Assert.Equal((JsonValueKind.Null, "CUSTOMER_TERMS_REQUIRED"), (noTerms.GetProperty("creditLimit").ValueKind, Reasons(noTerms)));
        Assert.Equal((31, 30, "OVERDUE_DAYS_EXCEEDED"), (overdue.GetProperty("overdueDays").GetInt32(), overdue.GetProperty("overdueDaysBlock").GetInt32(), Reasons(overdue)));
        Assert.Equal(QueryErrors.NotFound, unknown.Code);
        Assert.Equal(1L, await h.CountAsync("sal.credit_check")); // only the fixture order's own check

        static string Reasons(JsonElement e) => string.Join(',', e.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task A_receipt_is_suggested_to_the_oldest_open_invoices_first()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await ReceiptTests.WorldAsync(h); // FA-000001: 1 000 blocks, 59 000.00
        var (order, orderLine) = await DeliveryTests.ConfirmedOrderAsync(h, w.S, DeliveryTerms.PickupAtPlant, 100m, "o2");
        var (_, deliveryLine) = await DeliveryTests.DispatchAsync(h, w.S, order, orderLine, 100m, own: false, "d2");
        var second = (await h.RunAsync(new CreateInvoiceFromDeliveries(h.CompanyId, w.Billing, "i2", w.S.Customer, [deliveryLine]), new CreateInvoiceFromDeliveriesHandler())).ResultRef;
        await h.RunAsync(new IssueInvoice(h.CompanyId, w.Billing, "issue-2", second, 1), new IssueInvoiceHandler()); // FA-000002: 100 blocks, 5 900.00
        await h.AdminRequireAsync(
            $"""
            BEGIN; SET LOCAL session_replication_role = replica;
            UPDATE fin.ar_document SET due_date = due_date - 10 WHERE ar_doc_id = (SELECT ar_doc_id FROM sal.invoice WHERE invoice_id = '{second}');
            COMMIT;
            """);

        var part = await Query(h, new SuggestReceiptApplication(h.CompanyId, w.Cobros, w.S.Customer, 8000.00m), new SuggestReceiptApplicationHandler());
        var more = await Query(h, new SuggestReceiptApplication(h.CompanyId, w.Cobros, w.S.Customer, 70000.00m), new SuggestReceiptApplicationHandler());
        var zero = await Assert.ThrowsAsync<DomainException>(() => h.QueryAsync(new SuggestReceiptApplication(h.CompanyId, w.Cobros, w.S.Customer, 0m), new SuggestReceiptApplicationHandler()));

        // FA-000002 falls due first: 8 000.00 = 5 900.00 to it + 2 100.00 to FA-000001; 70 000.00 covers both (64 900.00) and leaves 5 100.00.
        Assert.Equal("FA-000002:5900.00,FA-000001:2100.00", Suggested(part));
        Assert.Equal(("64900.00", "8000.00", "0.00"), (part.GetProperty("totalOpen").GetString(), part.GetProperty("applied").GetString(), part.GetProperty("unapplied").GetString()));
        Assert.Equal("FA-000002:5900.00,FA-000001:59000.00", Suggested(more));
        Assert.Equal(("64900.00", "5100.00"), (more.GetProperty("applied").GetString(), more.GetProperty("unapplied").GetString()));
        Assert.Equal(QueryErrors.InvalidParameter, zero.Code);

        static string Suggested(JsonElement e)
            => string.Join(',', e.GetProperty("invoices").EnumerateArray().Select(i => $"{i.GetProperty("invoiceNo").GetString()}:{i.GetProperty("suggested").GetString()}"));
    }

    [Fact]
    public async Task A_fiscal_authorization_says_how_many_days_it_has_left()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await DeliveryTests.SetupAsync(h);
        var (order, _) = await DeliveryTests.ConfirmedOrderAsync(h, s, DeliveryTerms.PickupAtPlant, 1000m);
        var billing = await h.SessionWithRolesAsync("FACTURACION");
        var today = ReceiptTests.Today(h);
        var authorization = (await h.RunAsync(
            new Tax.Authorizations.RegisterFiscalAuthorization(h.CompanyId, billing, "reg", s.Customer, "CERT-2026-0001", today.AddDays(-20), today.AddDays(10), "Hotel Playa Bávaro",
                "CONFOTUR-0456-2025", new DateOnly(2040, 12, 31), order, [new Tax.Authorizations.AuthorizationLineInput(s.Block, "un", 1000m, 50000.00m)]),
            new Tax.Authorizations.RegisterFiscalAuthorizationHandler())).ResultRef;

        var list = (await Query(h, new Tax.Authorizations.ListFiscalAuthorizations(h.CompanyId, s.Seller), new Tax.Authorizations.ListFiscalAuthorizationsHandler())).GetProperty("items")[0];
        var detail = await Query(h, new Tax.Authorizations.GetFiscalAuthorization(h.CompanyId, s.Seller, authorization), new Tax.Authorizations.GetFiscalAuthorizationHandler());

        Assert.Equal((10, 10), (list.GetProperty("daysToExpiry").GetInt32(), detail.GetProperty("header").GetProperty("daysToExpiry").GetInt32()));
    }

    [Fact]
    public async Task The_AR_aging_totals_each_bucket_and_receipts_show_the_bank_alias()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await ReceiptTests.WorldAsync(h); // FA-000001 open 59 000.00, due in 30 days
        await h.RunAsync(new SetBankAccountAlias(h.CompanyId, w.Controller, "alias", w.Bank, 1, "  Cuenta operativa  "), new SetBankAccountAliasHandler());
        await ReceiptTests.Transfer(h, w, "r", 1000.00m);
        var today = ReceiptTests.Today(h);

        var now = await Query(h, new GetArAging(h.CompanyId, w.Cobros, today), new GetArAgingHandler());
        var late = await Query(h, new GetArAging(h.CompanyId, w.Cobros, today.AddDays(45)), new GetArAgingHandler());
        var receipt = (await Query(h, new ListReceipts(h.CompanyId, w.Cobros), new ListReceiptsHandler())).GetProperty("items")[0];
        var accounts = (await Query(h, new ListSalesBankAccounts(h.CompanyId, w.Cobros), new ListSalesBankAccountsHandler())).GetProperty("items")[0];

        Assert.Equal("59000.00|0.00|0.00|0.00|0.00|59000.00", Buckets(now));
        Assert.Equal("0.00|59000.00|0.00|0.00|0.00|59000.00", Buckets(late)); // 15 days overdue
        Assert.Equal(("Cuenta operativa", "TEST_BANK", "••••6789"), (receipt.GetProperty("bankAccountAlias").GetString(), receipt.GetProperty("bankCode").GetString(),
            receipt.GetProperty("bankAccountNumber").GetString()));
        Assert.Equal("Cuenta operativa", accounts.GetProperty("alias").GetString());

        static string Buckets(JsonElement aging)
        {
            var t = aging.GetProperty("bucketTotals");
            return string.Join('|', new[] { "current", "bucket1", "bucket2", "bucket3", "over", "total" }.Select(p => t.GetProperty(p).GetString()));
        }
    }
}
