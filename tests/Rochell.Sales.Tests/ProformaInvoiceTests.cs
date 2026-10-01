using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Sales.Deliveries;
using Rochell.Sales.Invoices;
using Rochell.Sales.Orders;
using Rochell.Sales.Proformas;
using Rochell.Sales.Queries;
using Rochell.Sales.Receipts;
using Rochell.Tax;
using Rochell.Tax.Authorizations;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>FIS1b-04: the authorization that cites proformas, the invoice from proformas that inherits what was collected, and voids (E-FIS1b-5…7, 10, E-FIS1b-01-6…8, 11).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ProformaInvoiceTests(PostgresFixture postgres)
{
    private sealed record World(ReceiptTests.World W, Guid Specialist, List<Guid> Proformas);

    /// <summary>The receipts world plus a marked order of 1,000 blocks delivered in <paramref name="deliveries"/> (one proforma each).</summary>
    private static async Task<World> WorldAsync(TestHarness h, bool collectsItbis, params decimal[] deliveries)
    {
        var w = await ReceiptTests.WorldAsync(h, 100m);
        var (order, line) = await ProformaTests.OrderAsync(h, w.S, DeliveryTerms.PickupAtPlant, 1000m, collectsItbis, "pf");
        var proformas = new List<Guid>();
        for (var i = 0; i < deliveries.Length; i++)
        {
            var (delivery, _) = await DeliveryTests.DispatchAsync(h, w.S, order, line, deliveries[i], own: false, "pf-d" + i);
            proformas.Add(await h.ScalarAsync<Guid>("SELECT proforma_id FROM sal.proforma WHERE delivery_id = @d", ("d", delivery)));
        }

        return new World(w, await h.SessionWithRolesAsync("ESPECIALISTA_FISCAL"), proformas);
    }

    private static RegisterFiscalAuthorization Register(TestHarness h, World x, string key, IReadOnlyList<Guid> proformas, IReadOnlyList<AuthorizationLineInput>? lines = null)
        => new(h.CompanyId, x.W.Billing, key, x.W.S.Customer, "CERT-" + key, new DateOnly(2026, 9, 1), new DateOnly(2027, 3, 1), "Hotel Playa Bávaro", "CONFOTUR-0456-2025", null, null, lines, proformas);

    private static async Task<Guid> ActiveAuthorizationAsync(TestHarness h, World x, string key, IReadOnlyList<Guid> proformas)
    {
        var id = (await h.RunAsync(Register(h, x, key, proformas), new RegisterFiscalAuthorizationHandler())).ResultRef;
        await h.RunAsync(new AttachAuthorizationDocument(h.CompanyId, x.W.Billing, key + "-doc", id, "CERTIFICADO_DGII", "certificado.pdf", DeliveryTests.Hash), new AttachAuthorizationDocumentHandler());
        await h.RunAsync(new SubmitForVerification(h.CompanyId, x.W.Billing, key + "-sub", id, 1), new SubmitForVerificationHandler());
        await h.RunAsync(new VerifyAuthorization(h.CompanyId, x.Specialist, key + "-ver", id, 2), new VerifyAuthorizationHandler());
        return id;
    }

    private static async Task<JsonElement> InvoiceAsync(TestHarness h, World x, string key, IReadOnlyList<Guid> proformas, Guid? authorization = null)
    {
        var invoice = (await h.RunAsync(new CreateInvoiceFromProformas(h.CompanyId, x.W.Billing, key, x.W.S.Customer, proformas, authorization), new CreateInvoiceFromProformasHandler())).ResultRef;
        return JsonDocument.Parse((await h.RunAsync(new IssueInvoice(h.CompanyId, x.W.Billing, key + "-issue", invoice, 1), new IssueInvoiceHandler())).ResultPayload).RootElement;
    }

    private static Task<CommandResult> Allocate(TestHarness h, World x, string key, Guid receipt, long version, params (Guid Proforma, decimal Amount)[] to)
        => h.RunAsync(
            new AllocateReceiptToProformas(h.CompanyId, x.W.Cobros, key, receipt, version, [.. to.Select(t => new ProformaAllocationInput(t.Proforma, t.Amount))]), new AllocateReceiptToProformasHandler());

    private static Task<string?> Proformas(TestHarness h)
        => h.ScalarAsync<string>("SELECT string_agg(proforma_no || ':' || status || ':' || allocated_amount::numeric(19,2), '|' ORDER BY proforma_no) FROM sal.proforma");

    private static Task<string?> Receipt(TestHarness h, Guid receipt)
        => h.ScalarAsync<string>(
            "SELECT concat_ws('|', application_status, unapplied_amount::numeric(19,2), allocated_amount::numeric(19,2)) FROM fin.receipt WHERE receipt_id = @r", ("r", receipt));

    [Trait("AcceptanceFis1b", "PRF-06")]
    [Fact]
    public async Task PRF06_a_certification_of_two_proformas_gives_an_eCF_44_that_inherits_the_net_and_leaves_the_ITBIS_as_credit_balance()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var x = await WorldAsync(h, collectsItbis: true, 600m, 400m); // PF-000001 30,000.00 + 5,400.00; PF-000002 20,000.00 + 3,600.00
        var receipt = (await ReceiptTests.Transfer(h, x.W, "r", 59000.00m)).ResultRef;
        await Allocate(h, x, "a", receipt, 1, (x.Proformas[0], 35400.00m), (x.Proformas[1], 23600.00m));
        var both = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            Register(h, x, "x", x.Proformas, [new AuthorizationLineInput(x.W.S.Block, "un", 1000m, 50000.00m)]), new RegisterFiscalAuthorizationHandler()));
        var authorization = await ActiveAuthorizationAsync(h, x, "A1", x.Proformas);
        var citedTwice = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Register(h, x, "A2", [x.Proformas[0]]), new RegisterFiscalAuthorizationHandler()));
        var scope = await h.ScalarAsync<string>("SELECT qty_authorized || ':' || net_authorized::numeric(19,2) FROM tax.fiscal_authorization_line WHERE authorization_id = @a", ("a", authorization));
        var certified = JsonDocument.Parse(await h.QueryAsync(new ListProformas(h.CompanyId, x.W.S.Seller), new ListProformasHandler())).RootElement.GetProperty("items")[0].GetProperty("certification").GetString();

        var issued = await InvoiceAsync(h, x, "i", x.Proformas, authorization);

        Assert.Equal((TaxErrors.AuthorizationFieldInvalid, TaxErrors.AuthorizationProformaInvalid), (both.Code, citedTwice.Code));
        Assert.Equal(("1000.000000:50000.00", "CERTIFIED"), (scope, certified));
        // e-CF 44: total 50,000.00, paid by the 50,000.00 of net collected; the 9,000.00 of ITBIS advanced stay on the receipt.
        Assert.Equal(
            ("PAID", "0.00", "50000.00", "50000.00"),
            (issued.GetProperty("commercialStatus").GetString(), issued.GetProperty("taxTotal").GetString(), issued.GetProperty("total").GetString(),
             issued.GetProperty("collectedOnProformas").GetString()));
        Assert.Equal("44:PAID:0.00", await h.ScalarAsync<string>(
            "SELECT i.ecf_type || ':' || i.commercial_status || ':' || d.open_amount::numeric(19,2) FROM sal.invoice i JOIN fin.ar_document d ON d.ar_doc_id = i.ar_doc_id WHERE i.invoice_no = 'FA-000002'"));
        Assert.Equal("PF-000001:INVOICED:0.00|PF-000002:INVOICED:0.00", await Proformas(h));
        Assert.Equal("PARTIALLY_APPLIED|9000.00|0.00", await Receipt(h, receipt));
        Assert.Equal("EXHAUSTED", await h.ScalarAsync<string>("SELECT status FROM tax.fiscal_authorization WHERE authorization_id = @a", ("a", authorization)));
        // The world's own invoice (5,900.00) is the only AR left; the receipt keeps 9,000.00 unapplied; nothing is unbilled.
        Assert.Equal(
            "AR_CONTROL=5900.00|UNAPPLIED_RECEIPTS=-9000.00|CONTRACT_ASSET=0.00|ITBIS_PAYABLE=-900.00",
            await DeliveryTests.Balances(h, x.W.S, "AR_CONTROL", "UNAPPLIED_RECEIPTS", "CONTRACT_ASSET", "ITBIS_PAYABLE"));
        Assert.Equal(2L, await h.ScalarAsync<long>("SELECT count(*) FROM core.state_history WHERE aggregate_type = 'Proforma' AND to_state = 'INVOICED' AND command = 'Sales.IssueInvoice'"));
    }

    [Trait("AcceptanceFis1b", "PRF-07")]
    [Fact]
    public async Task PRF07_without_the_certification_a_proforma_collected_with_ITBIS_becomes_a_paid_eCF_31()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var x = await WorldAsync(h, collectsItbis: true, 100m); // 5,000.00 + 900.00
        var receipt = (await ReceiptTests.Transfer(h, x.W, "r", 5900.00m)).ResultRef;
        await Allocate(h, x, "a", receipt, 1, (x.Proformas[0], 5900.00m));

        var issued = await InvoiceAsync(h, x, "i", x.Proformas);

        Assert.Equal(
            ("PAID", "900.00", "5900.00", "5900.00"),
            (issued.GetProperty("commercialStatus").GetString(), issued.GetProperty("taxTotal").GetString(), issued.GetProperty("total").GetString(),
             issued.GetProperty("collectedOnProformas").GetString()));
        Assert.Equal("31", await h.ScalarAsync<string>("SELECT ecf_type FROM sal.invoice WHERE invoice_no = 'FA-000002'"));
        Assert.Equal("APPLIED|0.00|0.00", await Receipt(h, receipt));
        Assert.Equal("PF-000001:INVOICED:0.00", await Proformas(h));
    }

    [Trait("AcceptanceFis1b", "PRF-08")]
    [Fact]
    public async Task PRF08_a_proforma_collected_without_ITBIS_leaves_the_ITBIS_open_on_its_eCF_31()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var x = await WorldAsync(h, collectsItbis: false, 100m);
        var receipt = (await ReceiptTests.Transfer(h, x.W, "r", 5000.00m)).ResultRef;
        await Allocate(h, x, "a", receipt, 1, (x.Proformas[0], 5000.00m));

        var issued = await InvoiceAsync(h, x, "i", x.Proformas);

        Assert.Equal(("PARTIALLY_PAID", "5900.00", "5000.00"), (issued.GetProperty("commercialStatus").GetString(), issued.GetProperty("total").GetString(), issued.GetProperty("collectedOnProformas").GetString()));
        Assert.Equal("900.00", await h.ScalarAsync<string>("SELECT d.open_amount::numeric(19,2)::text FROM sal.invoice i JOIN fin.ar_document d ON d.ar_doc_id = i.ar_doc_id WHERE i.invoice_no = 'FA-000002'"));
    }

    [Fact]
    public async Task An_authorization_that_cites_proformas_covers_only_their_invoice_and_a_voided_invoice_reopens_its_proformas()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var x = await WorldAsync(h, collectsItbis: false, 300m, 200m);
        var authorization = await ActiveAuthorizationAsync(h, x, "A1", [x.Proformas[0]]);

        var notCited = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new CreateInvoiceFromProformas(h.CompanyId, x.W.Billing, "n", x.W.S.Customer, [x.Proformas[1]], authorization), new CreateInvoiceFromProformasHandler()));
        var otherCustomer = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new CreateInvoiceFromProformas(h.CompanyId, x.W.Billing, "o", Guid.CreateVersion7(), [x.Proformas[0]]), new CreateInvoiceFromProformasHandler()));
        var issued = await InvoiceAsync(h, x, "i", [x.Proformas[0]], authorization);
        var invoiced = await Proformas(h);
        var again = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new CreateInvoiceFromProformas(h.CompanyId, x.W.Billing, "again", x.W.S.Customer, [x.Proformas[0]]), new CreateInvoiceFromProformasHandler()));
        await h.RunAsync(new VoidUnfiscalizedInvoice(h.CompanyId, x.W.Controller, "void", issued.GetProperty("invoiceId").GetGuid(), 2, "Factura emitida por error"), new VoidUnfiscalizedInvoiceHandler());

        Assert.Equal((ProformaErrors.NotCertified, AllocationErrors.ProformaNotOpen, AllocationErrors.ProformaNotOpen), (notCited.Code, otherCustomer.Code, again.Code));
        Assert.Equal(("CONFIRMED", "0.00"), (issued.GetProperty("commercialStatus").GetString(), issued.GetProperty("collectedOnProformas").GetString()));
        Assert.Equal("PF-000001:INVOICED:0.00|PF-000002:OPEN:0.00", invoiced);
        Assert.Equal("PF-000001:OPEN:0.00|PF-000002:OPEN:0.00", await Proformas(h));
        Assert.True(await h.ScalarAsync<bool>("SELECT invoice_id IS NULL FROM sal.proforma WHERE proforma_no = 'PF-000001'"));
        Assert.Equal("ACTIVE:0.000000", await h.ScalarAsync<string>(
            "SELECT a.status || ':' || l.qty_consumed FROM tax.fiscal_authorization a JOIN tax.fiscal_authorization_line l USING (authorization_id) WHERE a.authorization_id = @a", ("a", authorization)));
    }

    [Trait("AcceptanceFis1b", "PRF-12")]
    [Fact]
    public async Task PRF12_a_proforma_without_collections_is_voided_and_its_delivery_returns_to_normal_invoicing()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var x = await WorldAsync(h, collectsItbis: true, 100m, 50m);
        var receipt = (await ReceiptTests.Transfer(h, x.W, "r", 1000.00m)).ResultRef;
        await Allocate(h, x, "a", receipt, 1, (x.Proformas[1], 1000.00m));
        VoidProforma Void(string key, Guid proforma, Guid session, string reason = "El pedido se marcó por error") => new(h.CompanyId, session, key, proforma, proforma == x.Proformas[1] ? 2 : 1, reason);

        var seller = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Void("s", x.Proformas[0], x.W.S.Seller), new VoidProformaHandler()));
        var collected = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Void("c", x.Proformas[1], x.W.Billing), new VoidProformaHandler()));
        var noReason = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Void("n", x.Proformas[0], x.W.Billing, " "), new VoidProformaHandler()));
        await h.RunAsync(Void("v", x.Proformas[0], x.W.Billing), new VoidProformaHandler());
        var billable = JsonDocument.Parse(await h.QueryAsync(new ListBillableDeliveries(h.CompanyId, x.W.S.Seller, x.W.S.Customer), new ListBillableDeliveriesHandler())).RootElement.GetProperty("items");
        var invoice = (await h.RunAsync(
            new CreateInvoiceFromDeliveries(h.CompanyId, x.W.Billing, "i", x.W.S.Customer, [billable[0].GetProperty("deliveryLineId").GetGuid()]), new CreateInvoiceFromDeliveriesHandler())).ResultRef;
        var issued = JsonDocument.Parse((await h.RunAsync(new IssueInvoice(h.CompanyId, x.W.Billing, "issue", invoice, 1), new IssueInvoiceHandler())).ResultPayload).RootElement;

        Assert.Equal((AuthorizationErrors.NotAuthorized, ProformaErrors.NotVoidable, ReceiptErrors.ReasonRequired), (seller.Code, collected.Code, noReason.Code));
        Assert.Equal("PF-000001:VOIDED:0.00|PF-000002:OPEN:1000.00", await Proformas(h));
        Assert.Equal(1, billable.GetArrayLength());
        Assert.Equal(("CONFIRMED", "5900.00"), (issued.GetProperty("commercialStatus").GetString(), issued.GetProperty("total").GetString()));
    }
}
