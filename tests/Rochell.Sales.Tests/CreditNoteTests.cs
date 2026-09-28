using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Sales.CreditNotes;
using Rochell.Sales.Invoices;
using Rochell.Sales.Orders;
using Rochell.Sales.Queries;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>VS3-06: commercial credit note on a fiscalized invoice, P-22 and the e-CF type 34 (SAL-08; E-VS3-06-1…10).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class CreditNoteTests(PostgresFixture postgres)
{
    private const string SalesItbis = """{"tax_code":"ITBIS","rate":"0.18","effect":"OUTPUT","exempt_item_categories":[]}""";

    private sealed record World(DeliveryTests.Setup S, Guid Billing, Guid SecondBilling, Guid Invoice, Guid InvoiceLine);

    /// <summary>FA-000001: 1,000 blocks at 50.00 = 50,000.00 + 9,000.00 ITBIS, issued by <see cref="World.Billing"/>, fiscalized as E310000000001.</summary>
    private static async Task<World> WorldAsync(TestHarness h, bool fiscalize = true)
    {
        var s = await DeliveryTests.SetupAsync(h);
        var actors = await h.FiscalActorsAsync();
        await h.ActivateRuleAsync(actors, "itbis-ventas", "ITBIS_VENTAS", FiscalRuleKinds.SalesItbis, SalesItbis, new DateOnly(2026, 1, 1));
        var (order, orderLine) = await DeliveryTests.ConfirmedOrderAsync(h, s, DeliveryTerms.PickupAtPlant, 1000m);
        var (_, line) = await DeliveryTests.DispatchAsync(h, s, order, orderLine, 1000m, own: false, "d1");
        var billing = await h.SessionWithRolesAsync("FACTURACION");
        var invoice = (await h.RunAsync(new CreateInvoiceFromDeliveries(h.CompanyId, billing, "i", s.Customer, [line]), new CreateInvoiceFromDeliveriesHandler())).ResultRef;
        await h.RunAsync(new IssueInvoice(h.CompanyId, billing, "issue", invoice, 1), new IssueInvoiceHandler());
        if (fiscalize)
        {
            await h.RunAsync(
                new RecordExternalFiscalDocument(h.CompanyId, billing, "fisc", invoice, 2, "E310000000001", h.Clock.UtcNow.AddMinutes(-2), "A1B2C3", "e-cf-FA-000001.xml", DeliveryTests.Hash,
                    "131-92533-2", 50000.00m, 9000.00m, 59000.00m),
                new RecordExternalFiscalDocumentHandler());
        }

        var invoiceLine = await h.ScalarAsync<Guid>("SELECT invoice_line_id FROM sal.invoice_line WHERE invoice_id = @i", ("i", invoice));
        return new World(s, billing, await h.SessionWithRolesAsync("FACTURACION"), invoice, invoiceLine);
    }

    private static Task<CommandResult> Create(TestHarness h, World w, string key, decimal net, Guid? session = null)
        => h.RunAsync(new CreateCreditNote(h.CompanyId, session ?? w.Billing, key, w.Invoice, "DESCUENTO", "Descuento comercial por volumen", [new(w.InvoiceLine, net)]), new CreateCreditNoteHandler());

    private static Task<CommandResult> Issue(TestHarness h, World w, Guid note, string key, Guid? session = null)
        => h.RunAsync(new IssueCreditNote(h.CompanyId, session ?? w.SecondBilling, key, note, 1), new IssueCreditNoteHandler());

    private static Task<string?> InvoiceState(TestHarness h, World w)
        => h.ScalarAsync<string>(
            "SELECT i.commercial_status || ':' || a.open_amount::numeric(19,2)::text FROM sal.invoice i JOIN fin.ar_document a ON a.ar_doc_id = i.ar_doc_id WHERE i.invoice_id = @i", ("i", w.Invoice));

    [Trait("AcceptanceVs3", "SAL-08")]
    [Fact]
    public async Task SAL08_a_partial_credit_note_posts_P22_lowers_the_receivable_and_references_the_original_eNCF()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var created = JsonDocument.Parse((await Create(h, w, "nc", 5000.00m)).ResultPayload).RootElement;
        var note = created.GetProperty("creditNoteId").GetGuid();

        var issued = JsonDocument.Parse((await Issue(h, w, note, "nc-issue")).ResultPayload).RootElement;
        var package = JsonDocument.Parse(await h.QueryAsync(new GetCreditNoteFiscalPackage(h.CompanyId, w.Billing, note), new GetCreditNoteFiscalPackageHandler())).RootElement;
        await h.RunAsync(
            new RecordExternalCreditNoteDocument(h.CompanyId, w.Billing, "nc-fisc", note, 2, "E340000000001", h.Clock.UtcNow.AddMinutes(-1), "Z9Y8X7", "e-cf-NC-000001.xml", DeliveryTests.Hash,
                "131-92533-2", 5000.00m, 900.00m, 5900.00m),
            new RecordExternalCreditNoteDocumentHandler());

        // 5,000.00 net × 18 % = 900.00 → 5,900.00 off the 59,000.00 receivable.
        Assert.Equal(("NC-000001", "DRAFT", "900.00", "5900.00"), (created.GetProperty("creditNoteNo").GetString(), created.GetProperty("commercialStatus").GetString(),
            created.GetProperty("taxTotal").GetString(), created.GetProperty("total").GetString()));
        Assert.Equal(("CONFIRMED", "POSTED", "PENDING_EXTERNAL", "CONFIRMED"), (issued.GetProperty("commercialStatus").GetString(), issued.GetProperty("accountingStatus").GetString(),
            issued.GetProperty("fiscalStatus").GetString(), issued.GetProperty("invoiceStatus").GetString()));
        Assert.Equal("AR_CONTROL=53100.00|ITBIS_PAYABLE=-8100.00|SALES_DISCOUNTS=5000.00|REVENUE_PRODUCT=-50000.00",
            await DeliveryTests.Balances(h, w.S, "AR_CONTROL", "ITBIS_PAYABLE", "SALES_DISCOUNTS", "REVENUE_PRODUCT"));
        Assert.Equal("CONFIRMED:53100.00", await InvoiceState(h, w));
        Assert.Equal(("34", "E310000000001", "FA-000001", "DESCUENTO", "131925332", "5900.00"), (package.GetProperty("ecfType").GetString(), package.GetProperty("modifiedEncf").GetString(),
            package.GetProperty("invoiceNo").GetString(), package.GetProperty("reasonCategory").GetString(), package.GetProperty("receiverRnc").GetString(), package.GetProperty("total").GetString()));

        var detail = JsonDocument.Parse(await h.QueryAsync(new GetCreditNote(h.CompanyId, w.S.Seller, note), new GetCreditNoteHandler())).RootElement;
        Assert.Equal(("ACCEPTED_EXTERNAL", "E340000000001", "E310000000001", "Z9Y8X7"), (detail.GetProperty("header").GetProperty("fiscalStatus").GetString(),
            detail.GetProperty("header").GetProperty("encf").GetString(), detail.GetProperty("header").GetProperty("invoiceEncf").GetString(),
            detail.GetProperty("fiscalRecord").GetProperty("securityCode").GetString()));
        Assert.Equal("DOCUMENT:DRAFT|DOCUMENT:CONFIRMED|ACCOUNTING:POSTED", string.Join('|', detail.GetProperty("history").EnumerateArray()
            .Select(c => $"{c.GetProperty("statusKind").GetString()}:{c.GetProperty("to").GetString()}")));
        var invoice = JsonDocument.Parse(await h.QueryAsync(new GetInvoice(h.CompanyId, w.S.Seller, w.Invoice), new GetInvoiceHandler())).RootElement;
        Assert.Equal(("NC-000001", "5000.00", "45000.00"), (invoice.GetProperty("creditNotes")[0].GetProperty("creditNoteNo").GetString(),
            invoice.GetProperty("creditable")[0].GetProperty("creditedNet").GetString(), invoice.GetProperty("creditable")[0].GetProperty("remainingNet").GetString()));
        Assert.Equal(2L, await h.ScalarAsync<long>("SELECT count(*) FROM core.document_link WHERE link_type = 'FISCALIZES'"));
    }

    [Fact]
    public async Task Crediting_the_rest_takes_the_remaining_ITBIS_marks_the_invoice_CREDITED_and_nothing_more_can_be_credited()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        await Issue(h, w, (await Create(h, w, "a1", 0.03m)).ResultRef, "a1-issue"); // 0.03 × 18 % = 0.0054 → 0.01
        await Issue(h, w, (await Create(h, w, "a2", 0.03m)).ResultRef, "a2-issue");

        var tooMuch = await Assert.ThrowsAsync<DomainException>(() => Create(h, w, "b0", 49999.95m));
        var rest = JsonDocument.Parse((await Create(h, w, "b", 49999.94m)).ResultPayload).RootElement;
        var issued = JsonDocument.Parse((await Issue(h, w, rest.GetProperty("creditNoteId").GetGuid(), "b-issue")).ResultPayload).RootElement;
        var afterCredited = await Assert.ThrowsAsync<DomainException>(() => Create(h, w, "c", 1.00m));

        Assert.Equal(CreditNoteErrors.ExceedsCreditable, tooMuch.Code);
        Assert.Equal("8999.98", rest.GetProperty("taxTotal").GetString()); // 9,000.00 − 0.02, not round(49,999.94 × 18 %) = 8,999.99: the rest wins
        Assert.Equal("CREDITED", issued.GetProperty("invoiceStatus").GetString());
        Assert.Equal("CREDITED:0.00", await InvoiceState(h, w));
        Assert.Equal(CreditNoteErrors.InvoiceNotCreditable, afterCredited.Code);
        Assert.Equal("AR_CONTROL=0.00|ITBIS_PAYABLE=0.00|SALES_DISCOUNTS=50000.00", await DeliveryTests.Balances(h, w.S, "AR_CONTROL", "ITBIS_PAYABLE", "SALES_DISCOUNTS"));
        Assert.Equal("0.01|0.01|8999.98", await h.ScalarAsync<string>("SELECT string_agg(itbis::numeric(19,2)::text, '|' ORDER BY itbis) FROM sal.credit_note_line"));
    }

    [Fact]
    public async Task A_credit_note_needs_a_fiscalized_invoice_a_different_issuer_and_the_credit_note_permission()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var unfiscalized = await WorldAsync(h, fiscalize: false);
        var pending = await Assert.ThrowsAsync<DomainException>(() => Create(h, unfiscalized, "p", 100m));
        await h.RunAsync(
            new RecordExternalFiscalDocument(h.CompanyId, unfiscalized.Billing, "fisc", unfiscalized.Invoice, 2, "E310000000001", h.Clock.UtcNow.AddMinutes(-2), "A1B2C3", "e-cf.xml", DeliveryTests.Hash,
                "131925332", 50000.00m, 9000.00m, 59000.00m),
            new RecordExternalFiscalDocumentHandler());
        var w = unfiscalized;

        var seller = await Assert.ThrowsAsync<DomainException>(() => Create(h, w, "s", 100m, w.S.Seller));
        var noReason = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new CreateCreditNote(h.CompanyId, w.Billing, "r", w.Invoice, "REGALO", "x", [new(w.InvoiceLine, 100m)]), new CreateCreditNoteHandler()));
        var note = (await Create(h, w, "n", 100m)).ResultRef;
        var sameIssuer = await Assert.ThrowsAsync<DomainException>(() => Issue(h, w, note, "same", w.Billing));
        var badEncf = await Record(h, w, note, "r1", "E340000000002", version: 1);
        await Issue(h, w, note, "ok");
        var badType = await Record(h, w, note, "r2", "E310000000002");

        Assert.Equal(CreditNoteErrors.InvoiceNotCreditable, pending.Code);
        Assert.Equal(AuthorizationErrors.NotAuthorized, seller.Code);
        Assert.Equal(CreditNoteErrors.ReasonInvalid, noReason.Code);
        Assert.Equal(SalesErrors.FourEyes, sameIssuer.Code);
        Assert.Equal(SalesErrors.InvalidState, badEncf.Code); // still DRAFT (version 1)
        Assert.Equal(InvoiceErrors.EncfInvalid, badType.Code);
        Assert.Equal("CONFIRMED:POSTED:PENDING_EXTERNAL", await h.ScalarAsync<string>(
            "SELECT commercial_status || ':' || accounting_status || ':' || fiscal_status FROM sal.credit_note WHERE credit_note_id = @n", ("n", note)));
    }

    private static Task<DomainException> Record(TestHarness h, World w, Guid note, string key, string encf, long version = 2)
        => Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new RecordExternalCreditNoteDocument(h.CompanyId, w.Billing, key, note, version, encf, h.Clock.UtcNow.AddMinutes(-1), "S", "nc.xml", DeliveryTests.Hash, "131925332",
                100.00m, 18.00m, 118.00m),
            new RecordExternalCreditNoteDocumentHandler()));

    [Fact]
    public async Task Two_drafts_that_together_exceed_the_line_are_caught_when_the_second_one_is_issued()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var first = (await Create(h, w, "a", 30000m)).ResultRef;
        var second = (await Create(h, w, "b", 30000m)).ResultRef;

        await Issue(h, w, first, "a-issue");
        var late = await Assert.ThrowsAsync<DomainException>(() => Issue(h, w, second, "b-issue"));

        Assert.Equal(CreditNoteErrors.ExceedsCreditable, late.Code);
        Assert.Equal("CONFIRMED:23600.00", await InvoiceState(h, w)); // 59,000.00 − 35,400.00
        Assert.Equal("DRAFT", await h.ScalarAsync<string>("SELECT commercial_status FROM sal.credit_note WHERE credit_note_id = @n", ("n", second)));
    }
}
