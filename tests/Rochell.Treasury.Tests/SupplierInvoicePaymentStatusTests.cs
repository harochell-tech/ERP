using System.Globalization;
using System.Text.Json;
using Rochell.Platform.Time;
using Rochell.Procurement.Queries;
using Rochell.Procurement.SupplierInvoices;
using Rochell.TestInfrastructure;
using Rochell.Treasury.Payments;
using Xunit;

namespace Rochell.Treasury.Tests;

/// <summary>UX3-01 (E-UX3-6): a supplier invoice's ITBIS, gross, open amount, payment status and payments, as accounts payable sees them.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SupplierInvoicePaymentStatusTests(PostgresFixture postgres)
{
    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static decimal? Amount(JsonElement e, string property)
        => e.GetProperty(property).ValueKind == JsonValueKind.Null ? null : decimal.Parse(e.GetProperty(property).GetString()!, CultureInfo.InvariantCulture);

    private static (decimal? Itbis, decimal? Gross, decimal? Open, string? Status) State(JsonElement e)
        => (Amount(e, "itbisTotal"), Amount(e, "grossTotal"), Amount(e, "openAmount"), e.GetProperty("paymentStatus").GetString());

    private static async Task<JsonElement> DetailAsync(TestHarness h, Guid session, Guid si)
        => JsonDocument.Parse(await h.QueryAsync(new GetSupplierInvoice(h.CompanyId, session, si), new GetSupplierInvoiceHandler())).RootElement;

    private static async Task<JsonElement> SummaryAsync(TestHarness h, Guid session, Guid si)
        => JsonDocument.Parse(await h.QueryAsync(new ListSupplierInvoices(h.CompanyId, session), new ListSupplierInvoicesHandler())).RootElement
            .GetProperty("items").EnumerateArray().Single(i => i.GetProperty("supplierInvoiceId").GetGuid() == si);

    [Fact]
    public async Task An_invoice_is_not_posted_until_posted_then_open_with_its_ITBIS_and_gross()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await h.EnableInvoicePostingAsync();
        var si = (await h.RunAsync(
            new RegisterSupplierInvoice(h.CompanyId, s.Clerk, "r", s.Purchasing.SupplierId, "B0100000001", Today(h), Today(h).AddDays(30), [new(s.PoLineId, 6m, 1500m)]),
            new RegisterSupplierInvoiceHandler())).ResultRef;
        await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, s.Clerk, "m", si, 1), new MatchSupplierInvoiceHandler());

        var matched = await SummaryAsync(h, s.Clerk, si);
        var matchedDetail = await DetailAsync(h, s.Clerk, si);
        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, s.Clerk, "p", si, 2), new PostSupplierInvoiceHandler());
        var posted = await SummaryAsync(h, s.Clerk, si);
        var postedDetail = await DetailAsync(h, s.Clerk, si);

        // Not determined yet: no ITBIS, no gross, no AP document. Posted: 6 t × 1,500.00 = 9,000.00 net, 18 % = 1,620.00, 10,620.00 open.
        Assert.Equal((null, null, null, "NOT_POSTED"), State(matched));
        Assert.Equal((null, null, null, "NOT_POSTED"), State(matchedDetail));
        Assert.Empty(matchedDetail.GetProperty("payments").EnumerateArray());
        Assert.Equal((1620m, 10620m, 10620m, "OPEN"), State(posted));
        Assert.Equal((1620m, 10620m, 10620m, "OPEN"), State(postedDetail));
        Assert.Equal(9000m, Amount(posted, "totalAmount"));
    }

    [Fact]
    public async Task Payments_make_an_invoice_partial_then_paid_and_a_reversal_reopens_it()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePaymentSetupAsync();
        var si = await h.ScalarAsync<Guid>("SELECT source_doc_id FROM fin.ap_document WHERE ap_doc_id = @d", ("d", p.ApDocs[0]));
        var clerk = p.Invoicing.Clerk;

        async Task<Guid> Pay(string key, decimal amount)
        {
            var payment = (await h.RunAsync(
                new PrepareSupplierPayment(h.CompanyId, p.Treasurer, key, p.Supplier, p.BankAccount, p.PartyBankAccount, Today(h), null, [new(p.ApDocs[0], amount)]),
                new PrepareSupplierPaymentHandler())).ResultRef;
            await h.RunAsync(new ReleaseSupplierPayment(h.CompanyId, p.Controller, key + "-r", payment, 1), new ReleaseSupplierPaymentHandler());
            return payment;
        }

        var first = await Pay("first", 1000.00m);
        var partial = await DetailAsync(h, clerk, si);
        await Pay("second", 9620.00m);
        var paid = await DetailAsync(h, clerk, si);
        var paidSummary = await SummaryAsync(h, clerk, si);
        await h.RunAsync(new ReversePayment(h.CompanyId, p.Controller, "reverse", first, 2, "Pago duplicado por error"), new ReversePaymentHandler());
        var reopened = await DetailAsync(h, clerk, si);

        // 10,620.00 − 1,000.00 = 9,620.00 open; then 0; reversing the first payment reopens its 1,000.00.
        Assert.Equal((1620m, 10620m, 9620m, "PARTIAL"), State(partial));
        Assert.Equal("PAG-000001:1000.00:RELEASED", string.Join('|', partial.GetProperty("payments").EnumerateArray().Select(Payment)));
        Assert.Equal((1620m, 10620m, 0m, "PAID"), State(paid));
        Assert.Equal((1620m, 10620m, 0m, "PAID"), State(paidSummary));
        Assert.Equal("PAG-000001:1000.00:RELEASED|PAG-000002:9620.00:RELEASED", string.Join('|', paid.GetProperty("payments").EnumerateArray().Select(Payment)));
        Assert.Equal((1620m, 10620m, 1000m, "PARTIAL"), State(reopened));
        Assert.Equal("PAG-000001:0.00:REVERSED|PAG-000002:9620.00:RELEASED", string.Join('|', reopened.GetProperty("payments").EnumerateArray().Select(Payment)));
    }

    private static string Payment(JsonElement payment)
        => $"{payment.GetProperty("paymentNo").GetString()}:{decimal.Parse(payment.GetProperty("amountApplied").GetString()!, CultureInfo.InvariantCulture).ToString("0.00", CultureInfo.InvariantCulture)}:{payment.GetProperty("status").GetString()}";
}
