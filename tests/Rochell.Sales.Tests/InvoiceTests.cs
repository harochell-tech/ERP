using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Sales.Invoices;
using Rochell.Sales.Orders;
using Rochell.Sales.Queries;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>VS3-05: invoice from deliveries, sales ITBIS, external e-CF and void (SAL-06, SAL-07, SAL-09; E-VS3-05-1…14).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class InvoiceTests(PostgresFixture postgres)
{
    private const string SalesItbis = """{"tax_code":"ITBIS","rate":"0.18","effect":"OUTPUT","exempt_item_categories":[]}""";

    private sealed record World(DeliveryTests.Setup S, Guid Billing, Guid Controller, Guid DeliveryLine);

    /// <summary>1,000 blocks picked up at 50.00 (revenue 50,000.00 on the contract asset), SALES_ITBIS 18 % active.</summary>
    private static async Task<World> WorldAsync(TestHarness h, bool activateItbis = true)
    {
        var s = await DeliveryTests.SetupAsync(h);
        if (activateItbis)
        {
            var actors = await h.FiscalActorsAsync();
            await h.ActivateRuleAsync(actors, "itbis-ventas", "ITBIS_VENTAS", FiscalRuleKinds.SalesItbis, SalesItbis, new DateOnly(2026, 1, 1));
        }

        var (order, orderLine) = await DeliveryTests.ConfirmedOrderAsync(h, s, DeliveryTerms.PickupAtPlant, 1000m);
        var (_, line) = await DeliveryTests.DispatchAsync(h, s, order, orderLine, 1000m, own: false, "d1");
        return new World(s, await h.SessionWithRolesAsync("FACTURACION"), await h.SessionWithRolesAsync("CONTROLLER"), line);
    }

    private static Task<CommandResult> Create(TestHarness h, World w, string key)
        => h.RunAsync(new CreateInvoiceFromDeliveries(h.CompanyId, w.Billing, key, w.S.Customer, [w.DeliveryLine]), new CreateInvoiceFromDeliveriesHandler());

    private static Task<CommandResult> Issue(TestHarness h, World w, Guid invoice, string key)
        => h.RunAsync(new IssueInvoice(h.CompanyId, w.Billing, key, invoice, 1), new IssueInvoiceHandler());

    [Trait("AcceptanceVs3", "SAL-06")]
    [Fact]
    public async Task SAL06_the_invoice_of_the_delivered_quantity_moves_the_contract_asset_to_receivables_with_ITBIS_and_never_bills_twice()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var invoice = (await Create(h, w, "i")).ResultRef;

        var issued = JsonDocument.Parse((await Issue(h, w, invoice, "issue")).ResultPayload).RootElement;
        var twice = await Assert.ThrowsAsync<DomainException>(() => Create(h, w, "i2"));

        // 50,000.00 net + 18 % = 9,000.00 → 59,000.00 receivable; the contract asset of the delivery line back to 0.
        Assert.Equal(("CONFIRMED", "POSTED", "PENDING_EXTERNAL", "9000.00", "59000.00"), (issued.GetProperty("commercialStatus").GetString(), issued.GetProperty("accountingStatus").GetString(),
            issued.GetProperty("fiscalStatus").GetString(), issued.GetProperty("taxTotal").GetString(), issued.GetProperty("total").GetString()));
        Assert.Equal(InvoiceErrors.NotBillable, twice.Code);
        Assert.Equal("AR_CONTROL=59000.00|CONTRACT_ASSET=0.00|ITBIS_PAYABLE=-9000.00|REVENUE_PRODUCT=-50000.00",
            await DeliveryTests.Balances(h, w.S, "AR_CONTROL", "CONTRACT_ASSET", "ITBIS_PAYABLE", "REVENUE_PRODUCT"));
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetInvoice(h.CompanyId, w.S.Seller, invoice), new GetInvoiceHandler())).RootElement;
        Assert.Equal(("FA-000001", "31", "59000.00", "9000.00"), (detail.GetProperty("header").GetProperty("invoiceNo").GetString(), detail.GetProperty("header").GetProperty("ecfType").GetString(),
            detail.GetProperty("header").GetProperty("openAmount").GetString(), detail.GetProperty("lines")[0].GetProperty("itbis").GetString()));
        Assert.Equal(
            (detail.GetProperty("header").GetProperty("invoiceDate").GetDateTime().AddDays(30)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            detail.GetProperty("header").GetProperty("dueDate").GetString());
        Assert.Equal("1000.000000", await h.ScalarAsync<string>("SELECT qty_invoiced::text FROM log.delivery_line WHERE delivery_line_id = @l", ("l", w.DeliveryLine)));
        var exposure = JsonDocument.Parse(await h.QueryAsync(new GetCustomerExposure(h.CompanyId, w.S.Seller, w.S.Customer), new GetCustomerExposureHandler())).RootElement;
        Assert.Equal(("59000.00", "0.00"), (exposure.GetProperty("openAr").GetString(), exposure.GetProperty("deliveredUninvoiced").GetString()));
        var billable = JsonDocument.Parse(await h.QueryAsync(new ListBillableDeliveries(h.CompanyId, w.S.Seller), new ListBillableDeliveriesHandler())).RootElement.GetProperty("items");
        Assert.Equal(0, billable.GetArrayLength());
    }

    [Trait("AcceptanceVs3", "SAL-07")]
    [Fact]
    public async Task SAL07_an_external_eCF_with_different_totals_is_refused_and_one_that_matches_the_package_fiscalizes_the_invoice()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var invoice = (await Create(h, w, "i")).ResultRef;
        await Issue(h, w, invoice, "issue");
        var package = JsonDocument.Parse(await h.QueryAsync(new GetInvoiceFiscalPackage(h.CompanyId, w.Billing, invoice), new GetInvoiceFiscalPackageHandler())).RootElement;

        Task<CommandResult> Record(string key, string encf, decimal total, long version = 2)
            => h.RunAsync(
                new RecordExternalFiscalDocument(h.CompanyId, w.Billing, key, invoice, version, encf, h.Clock.UtcNow.AddMinutes(-2), "A1B2C3", "e-cf-FA-000001.xml", DeliveryTests.Hash,
                    "131-92533-2", 50000.00m, 9000.00m, total),
                new RecordExternalFiscalDocumentHandler());

        var mismatch = await Assert.ThrowsAsync<DomainException>(() => Record("r0", "E310000000001", 59000.01m));
        var badFormat = await Assert.ThrowsAsync<DomainException>(() => Record("r1", "E320000000001", 59000.00m));
        var pending = await h.ScalarAsync<string>("SELECT fiscal_status FROM sal.invoice WHERE invoice_id = @i", ("i", invoice));
        await Record("r", "E310000000001", 59000.00m);
        var voidAccepted = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new VoidUnfiscalizedInvoice(h.CompanyId, w.Controller, "void", invoice, 3, "Error"), new VoidUnfiscalizedInvoiceHandler()));

        Assert.Equal(("131925332", "Constructora Uno", "31", "59000.00"), (package.GetProperty("receiverRnc").GetString(), package.GetProperty("receiverName").GetString(),
            package.GetProperty("ecfType").GetString(), package.GetProperty("total").GetString()));
        Assert.Equal((InvoiceErrors.FiscalDocumentMismatch, InvoiceErrors.EncfInvalid, "PENDING_EXTERNAL", InvoiceErrors.NotVoidable), (mismatch.Code, badFormat.Code, pending, voidAccepted.Code));
        Assert.Contains("total 59000.01", mismatch.Message, StringComparison.Ordinal);
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetInvoice(h.CompanyId, w.S.Seller, invoice), new GetInvoiceHandler())).RootElement;
        Assert.Equal(("ACCEPTED_EXTERNAL", "E310000000001", "A1B2C3"), (detail.GetProperty("header").GetProperty("fiscalStatus").GetString(), detail.GetProperty("header").GetProperty("encf").GetString(),
            detail.GetProperty("fiscalRecord").GetProperty("securityCode").GetString()));
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM core.document_link WHERE link_type = 'FISCALIZES'"));
    }

    [Trait("AcceptanceVs3", "SAL-09")]
    [Fact]
    public async Task SAL09_two_invoices_of_the_same_delivery_lines_issued_at_once_bill_the_delivery_only_once()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var first = (await Create(h, w, "a")).ResultRef;
        var second = (await Create(h, w, "b")).ResultRef;

        var outcomes = await Task.WhenAll(new[] { (first, "ia"), (second, "ib") }.Select(x => Task.Run(() => Record.ExceptionAsync(() => Issue(h, w, x.Item1, x.Item2)))));

        Assert.Equal(1, outcomes.Count(o => o is null));
        Assert.Equal(InvoiceErrors.QuantityExceedsDelivered, Assert.IsType<DomainException>(outcomes.Single(o => o is not null)).Code);
        Assert.Equal("1000.000000:1", await h.ScalarAsync<string>(
            "SELECT (SELECT qty_invoiced::text FROM log.delivery_line WHERE delivery_line_id = @l) || ':' || (SELECT count(*) FROM sal.invoice WHERE commercial_status = 'CONFIRMED')",
            ("l", w.DeliveryLine)));
    }

    [Fact]
    public async Task An_unfiscalized_invoice_is_voided_by_the_controller_and_its_delivery_becomes_billable_again()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var invoice = (await Create(h, w, "i")).ResultRef;
        await Issue(h, w, invoice, "issue");

        var billingVoids = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new VoidUnfiscalizedInvoice(h.CompanyId, w.Billing, "v0", invoice, 2, "Precio equivocado"), new VoidUnfiscalizedInvoiceHandler()));
        await h.RunAsync(new VoidUnfiscalizedInvoice(h.CompanyId, w.Controller, "void", invoice, 2, "Precio equivocado en el pedido"), new VoidUnfiscalizedInvoiceHandler());
        var again = (await Create(h, w, "i2")).ResultRef;

        Assert.Equal(AuthorizationErrors.NotAuthorized, billingVoids.Code);
        Assert.Equal("VOIDED:REVERSED:PENDING_EXTERNAL:0.0000", await h.ScalarAsync<string>(
            "SELECT i.commercial_status || ':' || i.accounting_status || ':' || i.fiscal_status || ':' || a.open_amount::text FROM sal.invoice i JOIN fin.ar_document a ON a.ar_doc_id = i.ar_doc_id WHERE i.invoice_id = @i",
            ("i", invoice)));
        Assert.Equal("AR_CONTROL=0.00|CONTRACT_ASSET=50000.00|ITBIS_PAYABLE=0.00", await DeliveryTests.Balances(h, w.S, "AR_CONTROL", "CONTRACT_ASSET", "ITBIS_PAYABLE"));
        Assert.Equal("0.000000", await h.ScalarAsync<string>("SELECT qty_invoiced::text FROM log.delivery_line WHERE delivery_line_id = @l", ("l", w.DeliveryLine)));
        Assert.NotEqual(invoice, again);
    }

    [Fact]
    public async Task Without_an_active_sales_ITBIS_rule_the_fiscal_gate_stops_the_invoice()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h, activateItbis: false);
        var invoice = (await Create(h, w, "i")).ResultRef;

        var closed = await Assert.ThrowsAsync<DomainException>(() => Issue(h, w, invoice, "issue"));

        Assert.Equal(TaxErrors.FiscalGateClosed, closed.Code);
        Assert.Equal("DRAFT", await h.ScalarAsync<string>("SELECT commercial_status FROM sal.invoice WHERE invoice_id = @i", ("i", invoice)));
    }
}
