using System.Text.Json;
using Rochell.Sales.CreditNotes;
using Rochell.Sales.Invoices;
using Rochell.Sales.Receipts;
using Rochell.Tax.Reports;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>FIS2-02 (E-FIS2-02-6): the IT-1 summary of a month with a taxed e-CF 31, a credit note and a customer withholding.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class It1SummaryTests(PostgresFixture postgres)
{
    [Trait("AcceptanceFis2", "F2-04")]
    [Fact]
    public async Task F204_the_IT1_summary_has_sales_by_eCF_type_credit_notes_and_withholdings_and_its_ITBIS_matches_the_ledger()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await ReceiptTests.WorldAsync(h); // FA-000001: 1,000 blocks × 50.00 = 50,000.00 + ITBIS 9,000.00, e-CF 31
        await h.RunAsync(
            new RecordExternalFiscalDocument(h.CompanyId, w.Billing, "fisc", w.Invoice, 2, "E310000000001", h.Clock.UtcNow.AddMinutes(-2), "A1B2C3", "e-cf.xml", DeliveryTests.Hash,
                "131925332", 50000.00m, 9000.00m, 59000.00m),
            new RecordExternalFiscalDocumentHandler());
        var invoiceLine = await h.ScalarAsync<Guid>("SELECT invoice_line_id FROM sal.invoice_line WHERE invoice_id = @i", ("i", w.Invoice));
        var note = (await h.RunAsync(new CreateCreditNote(h.CompanyId, w.Billing, "nc", w.Invoice, "DESCUENTO", "Descuento por volumen", [new(invoiceLine, 5000.00m)]), new CreateCreditNoteHandler())).ResultRef;
        await h.RunAsync(new IssueCreditNote(h.CompanyId, await h.SessionWithRolesAsync("FACTURACION"), "nc-i", note, 1), new IssueCreditNoteHandler());
        await h.RunAsync(
            new RecordCustomerWithholding(h.CompanyId, w.Cobros, "isr", w.Invoice, "ISR", 1000.00m, ReceiptTests.Today(h), "ISR-1", "isr-1.pdf", DeliveryTests.Hash),
            new RecordCustomerWithholdingHandler());

        var summary = JsonDocument.Parse(await h.QueryAsync(
            new GetIt1Summary(h.CompanyId, await h.SessionWithRolesAsync("CONTADOR"), ReceiptTests.Today(h).ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture)),
            new GetIt1SummaryHandler())).RootElement;

        Assert.Equal("31:1:50000.00:0.00:9000.00", string.Join('|', summary.GetProperty("sales").EnumerateArray().Select(s =>
            $"{s.GetProperty("ecfType").GetString()}:{s.GetProperty("invoices").GetInt32()}:{s.GetProperty("taxedNet").GetString()}:{s.GetProperty("exemptNet").GetString()}:{s.GetProperty("itbis").GetString()}")));
        Assert.Equal("1|5000.00|900.00|0.00", $"{summary.GetProperty("creditNotes").GetInt32()}|{summary.GetProperty("creditNotesNet").GetString()}|" +
            $"{summary.GetProperty("creditNotesItbis").GetString()}|{summary.GetProperty("purchaseItbisBilled").GetString()}");
        Assert.Equal("ISR:1:1000.00", string.Join('|', summary.GetProperty("customerWithholdings").EnumerateArray().Select(x =>
            $"{x.GetProperty("kind").GetString()}:{x.GetProperty("count").GetInt32()}:{x.GetProperty("amount").GetString()}")));

        // 9,000.00 − 900.00 = 8,100.00 is what ITBIS_PAYABLE holds for the month.
        Assert.Equal("8100.00", await h.ScalarAsync<string>("SELECT sum(credit - debit)::numeric(19,2)::text FROM fin.gl_entry WHERE account_role = 'ITBIS_PAYABLE'"));
    }
}
