using System.Text.Json;
using Rochell.Identity;
using Rochell.Platform.Commands;
using Rochell.Sales.CreditNotes;
using Rochell.Sales.Ecf;
using Rochell.Sales.Invoices;
using Rochell.Sales.Orders;
using Rochell.Tax;
using Rochell.Tax.Ecf;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>
/// VS4-03 (E-VS4-03-1…11): invoices and credit notes through the e-CF gateway against the simulated Alanube — the channel, the e-CF 31 and 34
/// built from the documents, the answer back on the invoice, a rejection resent with another e-NCF or voided, data Alanube refuses.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class EcfIssuingTests(PostgresFixture postgres)
{
    private const string SalesItbis = """{"tax_code":"ITBIS","rate":"0.18","effect":"OUTPUT","exempt_item_categories":[]}""";

    private sealed class World
    {
        private int _keys;

        public required TestHarness H { get; init; }

        public required DeliveryTests.Setup S { get; init; }

        public required Guid Billing { get; init; }

        public required Guid SecondBilling { get; init; }

        public required Guid Line { get; init; }

        public SimulatedEcfProvider Provider { get; } = new();

        public EcfSwitch On { get; } = new(true);

        public async Task<Guid> InvoiceAsync()
            => (await H.RunAsync(new CreateInvoiceFromDeliveries(H.CompanyId, Billing, "i", S.Customer, [Line]), new CreateInvoiceFromDeliveriesHandler())).ResultRef;

        public async Task<JsonElement> IssueAsync(Guid invoice, EcfSwitch? gateway = null)
            => Json(await H.RunAsync(new IssueInvoice(H.CompanyId, Billing, "issue", invoice, 1), new IssueInvoiceHandler(gateway ?? On)));

        /// <summary>One gateway step of the source's live attempt, as the daily process.</summary>
        public async Task<string> AdvanceAsync(Guid source)
        {
            var document = await H.ScalarAsync<Guid>(
                "SELECT document_id FROM tax.ecf_document WHERE source_id = @s ORDER BY attempt_no DESC LIMIT 1", ("s", source));
            var session = await H.Sessions.StartServiceSessionAsync(IdentityConstants.DailyProcessUserId);
            var result = await H.RunAsync(
                new AdvanceEcfDocument(H.CompanyId, session, $"adv-{Interlocked.Increment(ref _keys)}", document),
                new AdvanceEcfDocumentHandler(Provider, [new InvoiceEcfUpdater(), new CreditNoteEcfUpdater()]));
            return Json(result).GetProperty("outcome").GetString()!.Split(';')[0];
        }

        public async Task<JsonElement> PayloadAsync(Guid source, int attempt = 1)
            => JsonDocument.Parse((await H.ScalarAsync<string>(
                "SELECT payload::text FROM tax.ecf_document WHERE source_id = @s AND attempt_no = @a", ("s", source), ("a", attempt)))!).RootElement;

        public Task<string?> FiscalAsync(string table, string key, Guid id)
            => H.ScalarAsync<string>($"SELECT fiscal_status || ':' || coalesce(encf, '-') FROM {table} WHERE {key} = @i", ("i", id));
    }

    /// <summary>1,000 blocks picked up at 50.00 (50,000.00 + 18 % ITBIS), ranges 31 and 34 active, the company's address set.</summary>
    private static async Task<World> WorldAsync(TestHarness h, bool address = true, bool ranges = true)
    {
        var s = await DeliveryTests.SetupAsync(h);
        var actors = await h.FiscalActorsAsync();
        await h.ActivateRuleAsync(actors, "itbis-ventas", "ITBIS_VENTAS", FiscalRuleKinds.SalesItbis, SalesItbis, new DateOnly(2026, 1, 1));
        var (order, orderLine) = await DeliveryTests.ConfirmedOrderAsync(h, s, DeliveryTerms.PickupAtPlant, 1000m);
        var (_, line) = await DeliveryTests.DispatchAsync(h, s, order, orderLine, 1000m, own: false, "d1");
        await h.GrantAsync(h.CompanyId, IdentityConstants.DailyProcessUserId, "PROCESO_DIARIO");
        if (address)
        {
            await h.AdminRequireAsync($"UPDATE md.company SET address = 'Carretera Higüey–La Romana km 3, Higüey', phone = '809-554-0000' WHERE company_id = '{h.CompanyId}'");
        }

        if (ranges)
        {
            await RangesAsync(h, "31", "34");
        }

        return new World
        {
            H = h,
            S = s,
            Billing = await h.SessionWithRolesAsync("FACTURACION"),
            SecondBilling = await h.SessionWithRolesAsync("FACTURACION"),
            Line = line,
        };
    }

    /// <summary>ACTIVE ranges 1…100 of <paramref name="types"/>, due 2027-12-31.</summary>
    private static async Task RangesAsync(TestHarness h, params string[] types)
    {
        var specialist = await h.SessionWithRolesAsync("ESPECIALISTA_FISCAL");
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        foreach (var type in types)
        {
            var series = (await h.RunAsync(new PrepareEcfSeries(h.CompanyId, specialist, $"p{type}", type, 1, 100, new DateOnly(2027, 12, 31)), new PrepareEcfSeriesHandler())).ResultRef;
            await h.RunAsync(new ApproveEcfSeries(h.CompanyId, controller, $"a{type}", series, 1), new ApproveEcfSeriesHandler());
        }
    }

    private static Task SetAddressAsync(TestHarness h)
        => h.AdminRequireAsync($"UPDATE md.company SET address = 'Carretera Higüey–La Romana km 3, Higüey' WHERE company_id = '{h.CompanyId}'");

    private static async Task<JsonElement> PayloadOfAsync(TestHarness h, Guid source)
        => JsonDocument.Parse((await h.ScalarAsync<string>("SELECT payload::text FROM tax.ecf_document WHERE source_id = @s", ("s", source)))!).RootElement;

    [Trait("AcceptanceVs4", "ECF-02")]
    [Fact]
    public async Task A_paid_cash_sale_is_an_eCF_32_naming_its_identified_buyer_paid_by_the_assigned_receipts()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await CashSaleInvoiceTests.WorldAsync(h);
        await SetAddressAsync(h);
        await RangesAsync(h, "32");
        var (order, line, _) = await CashSaleInvoiceTests.PaidAsync(h, w, "s", 100m, 5900.00m, "CEDULA", "40212345678");
        var (_, delivery) = await DeliveryTests.DispatchAsync(h, w.W.S, order, line, 100m, own: false, "cs-d1");
        var invoice = (await h.RunAsync(new CreateInvoiceFromDeliveries(h.CompanyId, w.W.Billing, "cs-i", w.Consumer, [delivery]), new CreateInvoiceFromDeliveriesHandler())).ResultRef;

        var issued = Json(await h.RunAsync(new IssueInvoice(h.CompanyId, w.W.Billing, "cs-issue", invoice, 1), new IssueInvoiceHandler(new EcfSwitch(true))));
        var payload = await PayloadOfAsync(h, invoice);

        Assert.Equal("E320000000001", issued.GetProperty("ecfNumber").GetString());
        var idDoc = payload.GetProperty("idDoc");
        Assert.False(idDoc.TryGetProperty("sequenceDueDate", out _));
        Assert.False(idDoc.TryGetProperty("paymentDeadline", out _));
        Assert.Equal((1, 2, "5900.00"), (idDoc.GetProperty("paymentType").GetInt32(), idDoc.GetProperty("paymentFormsTable")[0].GetProperty("paymentMethod").GetInt32(),
            idDoc.GetProperty("paymentFormsTable")[0].GetProperty("paymentAmount").GetRawText()));
        Assert.Equal(("40212345678", "María Pérez"), (payload.GetProperty("buyer").GetProperty("rnc").GetString(), payload.GetProperty("buyer").GetProperty("companyName").GetString()));
        // ECF-02: under the summary amount Alanube answers the e-CF 32 in the same response — one step accepts it.
        await h.GrantAsync(h.CompanyId, IdentityConstants.DailyProcessUserId, "PROCESO_DIARIO");
        var document = await h.ScalarAsync<Guid>("SELECT document_id FROM tax.ecf_document WHERE source_id = @s", ("s", invoice));
        var step = Json(await h.RunAsync(
            new AdvanceEcfDocument(h.CompanyId, await h.Sessions.StartServiceSessionAsync(IdentityConstants.DailyProcessUserId), "adv-32", document),
            new AdvanceEcfDocumentHandler(new SimulatedEcfProvider(), [new InvoiceEcfUpdater()])));
        Assert.StartsWith("ACCEPTED", step.GetProperty("outcome").GetString());
        Assert.Equal("ECF_ACCEPTED", await h.ScalarAsync<string>("SELECT fiscal_status FROM sal.invoice WHERE invoice_id = @i", ("i", invoice)));
    }

    [Trait("AcceptanceVs4", "ECF-04")]
    [Fact]
    public async Task An_exempt_CONFOTUR_invoice_is_an_eCF_44_with_every_line_exempt_and_the_certification()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await ExemptInvoiceTests.WorldAsync(h);
        await SetAddressAsync(h);
        await RangesAsync(h, "44");
        var line = await ExemptInvoiceTests.DeliveredAsync(h, w, 600m, "d1");
        var invoice = (await ExemptInvoiceTests.Create(h, w, line, "i")).ResultRef;

        var issued = Json(await h.RunAsync(new IssueInvoice(h.CompanyId, w.Billing, "issue", invoice, 1), new IssueInvoiceHandler(new EcfSwitch(true))));
        var payload = await PayloadOfAsync(h, invoice);

        Assert.Equal("E440000000001", issued.GetProperty("ecfNumber").GetString());
        Assert.False(payload.GetProperty("idDoc").TryGetProperty("taxAmountIndicator", out _));
        Assert.Equal(4, payload.GetProperty("itemDetails")[0].GetProperty("billingIndicator").GetInt32());
        var totals = payload.GetProperty("totals");
        Assert.Equal(("30000.00", "30000.00"), (totals.GetProperty("exemptAmount").GetRawText(), totals.GetProperty("totalAmount").GetRawText()));
        Assert.False(totals.TryGetProperty("itbisTotal", out _));
        Assert.Equal("CONFOTUR certificación CERT-2026-0001", payload.GetProperty("buyer").GetProperty("additionalInformation").GetString());
        Assert.Contains("CERT-2026-0001", payload.GetProperty("config").GetProperty("pdf").GetProperty("note").GetString());
    }

    private static JsonElement Json(CommandResult result) => JsonDocument.Parse(result.ResultPayload).RootElement;

    [Trait("AcceptanceVs4", "ECF-01")]
    [Fact]
    public async Task An_invoice_through_the_gateway_takes_the_next_eNCF_and_its_eCF_31_matches_it_to_the_cent()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var invoice = await w.InvoiceAsync();

        var issued = await w.IssueAsync(invoice);
        var payload = await w.PayloadAsync(invoice);

        Assert.Equal(("ECF_SENDING", "E310000000001"), (issued.GetProperty("fiscalStatus").GetString(), issued.GetProperty("ecfNumber").GetString()));
        var idDoc = payload.GetProperty("idDoc");
        Assert.Equal(("E310000000001", "2027-12-31", 1, 2, 4, "59000.00"), (idDoc.GetProperty("encf").GetString(), idDoc.GetProperty("sequenceDueDate").GetString(),
            idDoc.GetProperty("incomeType").GetInt32(), idDoc.GetProperty("paymentType").GetInt32(), idDoc.GetProperty("paymentFormsTable")[0].GetProperty("paymentMethod").GetInt32(),
            idDoc.GetProperty("paymentFormsTable")[0].GetProperty("paymentAmount").GetRawText()));
        Assert.Equal("30 días", idDoc.GetProperty("paymentTerm").GetString());
        Assert.Equal(0, idDoc.GetProperty("taxAmountIndicator").GetInt32());
        Assert.Equal("Carretera Higüey–La Romana km 3, Higüey", payload.GetProperty("sender").GetProperty("address").GetString());
        Assert.Equal("FA-000001", payload.GetProperty("sender").GetProperty("internalInvoiceNumber").GetString());
        Assert.False(payload.GetProperty("buyer").TryGetProperty("mail", out _));
        var totals = payload.GetProperty("totals");
        Assert.Equal(("50000.00", "50000.00", 18, "9000.00", "9000.00", "59000.00"), (totals.GetProperty("totalTaxedAmount").GetRawText(), totals.GetProperty("i1AmountTaxed").GetRawText(),
            totals.GetProperty("itbisS1").GetInt32(), totals.GetProperty("itbisTotal").GetRawText(), totals.GetProperty("itbis1Total").GetRawText(), totals.GetProperty("totalAmount").GetRawText()));
        var item = payload.GetProperty("itemDetails")[0];
        Assert.Equal((1, 1, "1000.00", "50.0000", "50000.00"), (item.GetProperty("billingIndicator").GetInt32(), item.GetProperty("goodServiceIndicator").GetInt32(),
            item.GetProperty("quantityItem").GetRawText(), item.GetProperty("unitPriceItem").GetRawText(), item.GetProperty("itemAmount").GetRawText()));
        Assert.Equal("SUBMITTED", await w.AdvanceAsync(invoice));
        Assert.Equal("ACCEPTED", await w.AdvanceAsync(invoice));
        Assert.Equal("ECF_ACCEPTED:E310000000001", await w.FiscalAsync("sal.invoice", "invoice_id", invoice));
        Assert.Equal("PDF,XML", await h.ScalarAsync<string>(
            "SELECT string_agg(f.kind, ',' ORDER BY f.kind) FROM tax.ecf_file f JOIN tax.ecf_document d ON d.document_id = f.document_id WHERE d.source_id = @i", ("i", invoice)));
        var detail = JsonDocument.Parse(await h.QueryAsync(new Rochell.Sales.Queries.GetInvoice(h.CompanyId, w.Billing, invoice), new Rochell.Sales.Queries.GetInvoiceHandler())).RootElement;
        Assert.Equal("ACCEPTED", detail.GetProperty("ecf").GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(detail.GetProperty("ecf").GetProperty("securityCode").GetString()));
        Assert.StartsWith("https://", detail.GetProperty("ecf").GetProperty("stampUrl").GetString());
    }

    [Trait("AcceptanceVs4", "E2E-ECF")]
    [Fact]
    public async Task An_accepted_invoice_is_emailed_with_the_PDF_carrying_the_QR_and_the_signed_XML_and_a_sending_one_is_not()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var invoice = await w.InvoiceAsync();
        await w.IssueAsync(invoice);
        var cobros = await h.SessionWithRolesAsync("COBROS");
        var mail = new Rochell.Platform.Mail.MailSwitch(true);

        var early = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new Rochell.Sales.Mail.SendInvoiceByEmail(h.CompanyId, cobros, "m0", invoice, ["compras@cliente.do"]), new Rochell.Sales.Mail.SendInvoiceByEmailHandler(mail)));
        await w.AdvanceAsync(invoice);
        await w.AdvanceAsync(invoice);
        var sent = Json(await h.RunAsync(
            new Rochell.Sales.Mail.SendInvoiceByEmail(h.CompanyId, cobros, "m1", invoice, ["compras@cliente.do"], "Gracias por su compra."), new Rochell.Sales.Mail.SendInvoiceByEmailHandler(mail)));

        Assert.Equal(Rochell.Sales.Mail.DocumentMailErrors.NotSendable, early.Code);
        Assert.Equal("QUEUED", sent.GetProperty("status").GetString());
        Assert.Equal("INVOICE|E310000000001.pdf|E310000000001.xml|application/xml|Factura E310000000001", await h.ScalarAsync<string>(
            "SELECT document_type || '|' || file_name || '|' || attachment_name || '|' || attachment_type || '|' || split_part(subject, ' — ', 1) FROM core.mail_message WHERE document_id = @i",
            ("i", invoice)));
        var html = await h.ScalarAsync<string>("SELECT html FROM core.mail_message WHERE document_id = @i", ("i", invoice));
        Assert.Contains("data:image/png;base64,", html);
        Assert.Contains("E310000000001", html);
    }

    [Fact]
    public async Task Without_an_active_range_or_with_the_gateway_off_the_invoice_takes_the_manual_channel()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h, ranges: false);
        var invoice = await w.InvoiceAsync();

        var issued = await w.IssueAsync(invoice);

        Assert.Equal("PENDING_EXTERNAL", issued.GetProperty("fiscalStatus").GetString());
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM tax.ecf_document"));
    }

    [Fact]
    public async Task Without_the_company_address_the_gateway_refuses_to_issue()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h, address: false);
        var invoice = await w.InvoiceAsync();

        var ex = await Assert.ThrowsAsync<DomainException>(() => w.IssueAsync(invoice));

        Assert.Equal(EcfErrors.IssuerIncomplete, ex.Code);
        Assert.Equal("DRAFT", await h.ScalarAsync<string>("SELECT commercial_status FROM sal.invoice WHERE invoice_id = @i", ("i", invoice)));
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT next_number FROM tax.ecf_series WHERE ecf_type = '31'"));
    }

    [Trait("AcceptanceVs4", "ECF-05")]
    [Fact]
    public async Task A_rejected_invoice_is_resent_with_another_eNCF_once_its_data_is_corrected()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        await h.AdminRequireAsync($"UPDATE md.company SET trade_name = 'Rochell [SIM:REJECT]' WHERE company_id = '{h.CompanyId}'");
        var invoice = await w.InvoiceAsync();
        await w.IssueAsync(invoice);

        await w.AdvanceAsync(invoice);
        var rejected = await w.AdvanceAsync(invoice);
        var fiscal = await w.FiscalAsync("sal.invoice", "invoice_id", invoice);
        await h.AdminRequireAsync($"UPDATE md.company SET trade_name = 'Rochell' WHERE company_id = '{h.CompanyId}'");
        var version = await h.ScalarAsync<long>("SELECT version FROM sal.invoice WHERE invoice_id = @i", ("i", invoice));
        var resent = Json(await h.RunAsync(new ResendInvoiceEcf(h.CompanyId, w.Billing, "resend", invoice, version), new ResendInvoiceEcfHandler(w.On)));
        await w.AdvanceAsync(invoice);
        var accepted = await w.AdvanceAsync(invoice);

        Assert.Equal("REJECTED", rejected);
        Assert.Equal("ECF_REJECTED:-", fiscal);
        Assert.Equal(("E310000000002", 2), (resent.GetProperty("ecfNumber").GetString(), resent.GetProperty("attempt").GetInt32()));
        Assert.Equal("Rochell", (await w.PayloadAsync(invoice, 2)).GetProperty("sender").GetProperty("tradename").GetString());
        Assert.Equal("ACCEPTED", accepted);
        Assert.Equal("ECF_ACCEPTED:E310000000002", await w.FiscalAsync("sal.invoice", "invoice_id", invoice));
    }

    [Fact]
    public async Task A_rejected_invoice_can_be_voided_and_a_sending_one_cannot()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        await h.AdminRequireAsync($"UPDATE md.company SET trade_name = 'Rochell [SIM:REJECT]' WHERE company_id = '{h.CompanyId}'");
        var invoice = await w.InvoiceAsync();
        await w.IssueAsync(invoice);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");

        var whileSending = await Assert.ThrowsAsync<DomainException>(
            () => h.RunAsync(new VoidUnfiscalizedInvoice(h.CompanyId, controller, "v0", invoice, 2, "Precio equivocado"), new VoidUnfiscalizedInvoiceHandler()));
        await w.AdvanceAsync(invoice);
        await w.AdvanceAsync(invoice);
        var version = await h.ScalarAsync<long>("SELECT version FROM sal.invoice WHERE invoice_id = @i", ("i", invoice));
        var voided = Json(await h.RunAsync(new VoidUnfiscalizedInvoice(h.CompanyId, controller, "v1", invoice, version, "Precio equivocado"), new VoidUnfiscalizedInvoiceHandler()));

        Assert.Equal(InvoiceErrors.NotVoidable, whileSending.Code);
        Assert.Equal("VOIDED", voided.GetProperty("commercialStatus").GetString());
    }

    [Fact]
    public async Task Content_Alanube_refuses_puts_the_invoice_in_attention()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        await h.AdminRequireAsync($"UPDATE md.company SET trade_name = 'Rochell [SIM:INVALID]' WHERE company_id = '{h.CompanyId}'");
        var invoice = await w.InvoiceAsync();
        await w.IssueAsync(invoice);

        var outcome = await w.AdvanceAsync(invoice);

        Assert.Equal("REQUIRES_ACTION", outcome);
        Assert.Equal("ECF_ACTION:-", await w.FiscalAsync("sal.invoice", "invoice_id", invoice));
    }

    [Trait("AcceptanceVs4", "ECF-03")]
    [Fact]
    public async Task A_credit_note_of_an_accepted_invoice_is_an_eCF_34_citing_it_with_code_3()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var invoice = await w.InvoiceAsync();
        await w.IssueAsync(invoice);
        await w.AdvanceAsync(invoice);
        await w.AdvanceAsync(invoice);
        var invoiceLine = await h.ScalarAsync<Guid>("SELECT invoice_line_id FROM sal.invoice_line WHERE invoice_id = @i", ("i", invoice));
        var note = (await h.RunAsync(
            new CreateCreditNote(h.CompanyId, w.Billing, "n", invoice, "DESCUENTO", "Descuento comercial por volumen", [new(invoiceLine, 5000m)]), new CreateCreditNoteHandler())).ResultRef;

        var issued = Json(await h.RunAsync(new IssueCreditNote(h.CompanyId, w.SecondBilling, "ni", note, 1), new IssueCreditNoteHandler(w.On)));
        var payload = await w.PayloadAsync(note);
        await w.AdvanceAsync(note);
        await w.AdvanceAsync(note);

        Assert.Equal(("ECF_SENDING", "E340000000001"), (issued.GetProperty("fiscalStatus").GetString(), issued.GetProperty("ecfNumber").GetString()));
        var reference = payload.GetProperty("informationReference");
        Assert.Equal(("E310000000001", 3, "Descuento comercial por volumen"), (reference.GetProperty("ncfModified").GetString(), reference.GetProperty("modificationCode").GetInt32(),
            reference.GetProperty("reasonForModification").GetString()));
        Assert.Equal(0, payload.GetProperty("idDoc").GetProperty("creditNoteIndicator").GetInt32());
        Assert.False(payload.GetProperty("idDoc").TryGetProperty("sequenceDueDate", out _));
        // 5,000.00 credited of the 50,000.00 line: quantity 1 at 5,000.00, ITBIS 900.00, total 5,900.00.
        var item = payload.GetProperty("itemDetails")[0];
        Assert.Equal(("1.00", "5000.0000", "5000.00"), (item.GetProperty("quantityItem").GetRawText(), item.GetProperty("unitPriceItem").GetRawText(), item.GetProperty("itemAmount").GetRawText()));
        Assert.Equal(("900.00", "5900.00"), (payload.GetProperty("totals").GetProperty("itbisTotal").GetRawText(), payload.GetProperty("totals").GetProperty("totalAmount").GetRawText()));
        Assert.Equal("ECF_ACCEPTED:E340000000001", await w.FiscalAsync("sal.credit_note", "credit_note_id", note));
    }
}
