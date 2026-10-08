using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Mail;
using Rochell.Sales.Deliveries;
using Rochell.Sales.Mail;
using Rochell.Sales.Orders;
using Rochell.Sales.Proformas;
using Rochell.Sales.Queries;
using Rochell.Sales.Quotes;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>
/// MAIL-02 (E-MAIL-3, 5, 6, E-MAIL-01-2, 7, 8, 9, 10): the documents sent by e-mail — who may send each, when a document can be
/// sent, the fixed texts, the HTML snapshot with the print view's content, the history and the retry. The world is the receipts'
/// one: Constructora Uno with FA-000001 open for 100 blocks (5,000.00 + 900.00 ITBIS).
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class DocumentMailTests(PostgresFixture postgres)
{
    private static readonly string[] To = ["compras@constructorauno.com.do"];
    private static readonly MailDelivery Live = new(MailMode.Live, null, "industrias@rochell.com.do");

    private sealed record Mail(string Type, string DocumentNo, string Recipients, string Subject, string Body, string FileName, string Html, string Status);

    private static async Task<Mail> MailAsync(TestHarness h, Guid mailId)
    {
        var row = JsonDocument.Parse((await h.ScalarAsync<string>(
            """
            SELECT json_build_object('type', document_type, 'no', document_no, 'to', array_to_string(recipients, ','), 'subject', subject, 'body', body_text, 'file', file_name, 'html', html,
                                     'status', status)::text
            FROM core.mail_message WHERE mail_id = @id
            """,
            ("id", mailId)))!).RootElement;
        string Get(string name) => row.GetProperty(name).GetString()!;
        return new Mail(Get("type"), Get("no"), Get("to"), Get("subject"), Get("body"), Get("file"), Get("html"), Get("status"));
    }

    private static Task<string?> IssuerAsync(TestHarness h) => h.ScalarAsync<string>("SELECT legal_name FROM md.company WHERE company_id = @c", ("c", h.CompanyId));

    [Fact]
    public async Task A_sent_quote_is_emailed_by_the_Vendedor_with_the_fixed_text_and_the_print_views_content()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await ReceiptTests.WorldAsync(h, 100m);
        var issuer = await IssuerAsync(h);
        var quote = (await h.RunAsync(
            new CreateQuote(h.CompanyId, w.S.Seller, "q", w.S.Customer, w.S.Plant, ReceiptTests.Today(h).AddDays(30), DeliveryTerms.PickupAtPlant, null, "OC-77", "Precios <sujetos> a disponibilidad",
                [new QuoteLineInput(w.S.Block, "un", 1000m)]),
            new CreateQuoteHandler())).ResultRef;
        var quoteNo = await h.ScalarAsync<string>("SELECT quote_no FROM sal.quote WHERE quote_id = @q", ("q", quote));

        // E-MAIL-01-7: a draft prints "BORRADOR" — it is not e-mailed. E-MAIL-01-8: Cobros does not send quotes.
        var draft = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SendQuoteByEmail(h.CompanyId, w.S.Seller, "draft", quote, To), new SendQuoteByEmailHandler()));
        await h.RunAsync(new SendQuote(h.CompanyId, w.S.Seller, "q-send", quote, 1), new SendQuoteHandler());
        var cobros = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SendQuoteByEmail(h.CompanyId, w.Cobros, "cobros", quote, To), new SendQuoteByEmailHandler()));
        var off = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SendQuoteByEmail(h.CompanyId, w.S.Seller, "off", quote, To), new SendQuoteByEmailHandler(new MailSwitch(false))));

        var sent = await h.RunAsync(
            new SendQuoteByEmail(h.CompanyId, w.S.Seller, "mail", quote, ["Compras@ConstructoraUno.com.do", "obra@constructorauno.com.do"], "Quedamos atentos a su orden de compra."),
            new SendQuoteByEmailHandler(new MailSwitch(true)));
        var mail = await MailAsync(h, sent.ResultRef);

        Assert.Equal((DocumentMailErrors.NotSendable, AuthorizationErrors.NotAuthorized, DocumentMailErrors.Disabled), (draft.Code, cobros.Code, off.Code));
        Assert.Equal(("QUOTE", quoteNo, "compras@constructorauno.com.do,obra@constructorauno.com.do", $"Cotización {quoteNo} — {issuer}", $"{quoteNo}.pdf", "QUEUED"),
            (mail.Type, mail.DocumentNo, mail.Recipients, mail.Subject, mail.FileName, mail.Status));
        Assert.StartsWith($"Estimado cliente:\n\nAdjuntamos la cotización {quoteNo}, válida hasta el {Rochell.Sales.Printing.PrintText.Date(ReceiptTests.Today(h).AddDays(30))}.\n\nQuedamos atentos a su orden de compra.\n\nAtentamente,\n", mail.Body, StringComparison.Ordinal);
        Assert.EndsWith($"\n{issuer}", mail.Body, StringComparison.Ordinal);

        // The print view's content: 1,000 blocks at 50.00 = 50,000.00 + 9,000.00 ITBIS = 59,000.00; the notes, HTML-encoded.
        foreach (var expected in new[] { $"<h1>Cotización {quoteNo}</h1>", "Constructora Uno", "BLOQUE-6", "<td class=\"num\">1,000</td>", "<td class=\"num\">50.00</td>", "<td class=\"num\">50,000.00</td>",
                     "<td class=\"num\">9,000.00</td>", "<td class=\"num\">59,000.00</td>", "Retira en planta", "OC-77", "Precios &lt;sujetos&gt; a disponibilidad", "Documento no fiscal" })
        {
            Assert.Contains(expected, mail.Html, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("<script", mail.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM core.domain_event WHERE event_type = 'DocumentEmailRequested' AND aggregate_id = @id", ("id", sent.ResultRef)));
    }

    [Fact]
    public async Task A_delivery_note_goes_after_the_gate_out_and_a_proforma_while_it_is_not_voided()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await ReceiptTests.WorldAsync(h, 100m);
        var (order, line) = await ProformaTests.OrderAsync(h, w.S, DeliveryTerms.PickupAtPlant, 300m, collectsItbis: true, "pf");
        var planned = (await h.RunAsync(new PlanDelivery(h.CompanyId, w.S.Dispatch, "plan", order, [new(line, 100m)]), new PlanDeliveryHandler())).ResultRef;
        var (delivery, _) = await DeliveryTests.DispatchAsync(h, w.S, order, line, 200m, own: false, "pf-a");
        var proforma = await h.ScalarAsync<Guid>("SELECT proforma_id FROM sal.proforma WHERE delivery_id = @d", ("d", delivery));
        var deliveryNo = await h.ScalarAsync<string>("SELECT delivery_no FROM log.delivery WHERE delivery_id = @d", ("d", delivery));

        var notOut = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SendDeliveryByEmail(h.CompanyId, w.S.Dispatch, "planned", planned, To), new SendDeliveryByEmailHandler()));
        var seller = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SendProformaByEmail(h.CompanyId, w.S.Seller, "seller", proforma, To), new SendProformaByEmailHandler()));
        var note = await MailAsync(h, (await h.RunAsync(new SendDeliveryByEmail(h.CompanyId, w.S.Dispatch, "cd", delivery, To), new SendDeliveryByEmailHandler())).ResultRef);
        var byBilling = await MailAsync(h, (await h.RunAsync(new SendDeliveryByEmail(h.CompanyId, w.Billing, "cd-billing", delivery, To), new SendDeliveryByEmailHandler())).ResultRef);
        var pf = await MailAsync(h, (await h.RunAsync(new SendProformaByEmail(h.CompanyId, w.Billing, "pf", proforma, To), new SendProformaByEmailHandler())).ResultRef);
        await h.RunAsync(new VoidProforma(h.CompanyId, w.Billing, "void", proforma, 1, "Entrega marcada por error"), new VoidProformaHandler());
        var voided = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SendProformaByEmail(h.CompanyId, w.Billing, "pf-void", proforma, To), new SendProformaByEmailHandler()));

        Assert.Equal((DocumentMailErrors.NotSendable, AuthorizationErrors.NotAuthorized, DocumentMailErrors.NotSendable), (notOut.Code, seller.Code, voided.Code));
        Assert.Equal(("DELIVERY", deliveryNo, $"{deliveryNo}.pdf", "DELIVERY"), (note.Type, note.DocumentNo, note.FileName, byBilling.Type));
        foreach (var expected in new[] { $"<h1>Conduce {deliveryNo}</h1>", "Placa A 123-456", "Pedro Cliente (del cliente)", "Bruto 18,000 kg · tara 8,000 kg · neto <span data-testid=\"conduce-net\">10,000</span> kg", "<td class=\"num\">200</td>", "Recibido por (nombre, cédula, firma)" })
        {
            Assert.Contains(expected, note.Html, StringComparison.Ordinal);
        }

        // PF-000001: 200 blocks at 50.00 = 10,000.00 + 1,800.00 ITBIS = 11,800.00.
        Assert.Equal(("PROFORMA", "PF-000001", "PF-000001.pdf"), (pf.Type, pf.DocumentNo, pf.FileName));
        Assert.Contains($"Adjuntamos la proforma PF-000001, correspondiente al conduce {deliveryNo}; vence el ", pf.Body, StringComparison.Ordinal);
        foreach (var expected in new[] { "<h1>Proforma <span class=\"mono\" data-testid=\"proforma-no\">PF-000001</span></h1>", ">10,000.00</span></td>", ">1,800.00</span></td>", ">11,800.00</span></td>", "Sello del suplidor" })
        {
            Assert.Contains(expected, pf.Html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Cobros_sends_the_statement_and_the_open_invoices_by_age_of_a_customer()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await ReceiptTests.WorldAsync(h, 100m);
        var today = ReceiptTests.Today(h);
        await ReceiptTests.Transfer(h, w, "r", 900.00m);

        var billing = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SendStatementByEmail(h.CompanyId, w.Billing, "billing", w.S.Customer, today, today, To), new SendStatementByEmailHandler()));
        var nothing = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SendArAgingByEmail(h.CompanyId, w.Cobros, "nothing", Guid.CreateVersion7(), To), new SendArAgingByEmailHandler()));
        var statement = await MailAsync(h, (await h.RunAsync(new SendStatementByEmail(h.CompanyId, w.Cobros, "ec", w.S.Customer, today, today, To), new SendStatementByEmailHandler())).ResultRef);
        var aging = await MailAsync(h, (await h.RunAsync(new SendArAgingByEmail(h.CompanyId, w.Cobros, "cxc", w.S.Customer, To), new SendArAgingByEmailHandler())).ResultRef);

        Assert.Equal((AuthorizationErrors.NotAuthorized, DocumentMailErrors.NotSendable), (billing.Code, nothing.Code));
        var day = Rochell.Sales.Printing.PrintText.Date(today);
        Assert.Equal(("STATEMENT", $"Estado de cuenta al {day} — {await IssuerAsync(h)}", $"estado-de-cuenta-{today:yyyyMMdd}.pdf"), (statement.Type, statement.Subject, statement.FileName));

        // The invoice of 5,900.00, then the receipt of 900.00 (unapplied): balance 5,000.00.
        foreach (var expected in new[] { "<h1>Estado de cuenta</h1>", "Saldo inicial", "<td>Factura</td><td class=\"mono\">FA-000001</td><td class=\"num\">5,900.00</td>", "<td>Cobro</td><td class=\"mono\">REC-000001</td>",
                     "<th colspan=\"3\">Saldo final</th><td class=\"num\">5,900.00</td><td class=\"num\">900.00</td><td class=\"num\"><span data-testid=\"statement-closing\">5,000.00</span></td>" })
        {
            Assert.Contains(expected, statement.Html, StringComparison.Ordinal);
        }

        Assert.Equal(("AR_AGING", $"Facturas pendientes al {day} — {await IssuerAsync(h)}", $"facturas-pendientes-{today:yyyyMMdd}.pdf"), (aging.Type, aging.Subject, aging.FileName));
        foreach (var expected in new[] { "<h1>Facturas pendientes</h1>", "<td class=\"mono\">FA-000001</td>", "<th colspan=\"5\">Total pendiente</th><td class=\"num\">5,900.00</td>", "A su favor (RD$)",
                     "<td class=\"num\">900.00</td><td class=\"num\">5,000.00</td>" })
        {
            Assert.Contains(expected, aging.Html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_history_of_a_document_shows_each_sending_its_PDF_and_a_failed_one_is_retried()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var w = await ReceiptTests.WorldAsync(h, 100m);
        var customerVersion = await h.ScalarAsync<long>("SELECT version FROM md.party WHERE party_id = @p", ("p", w.S.Customer));
        await h.RunAsync(
            new Customers.UpdateCustomer(h.CompanyId, w.S.Seller, "mails", w.S.Customer, customerVersion, "131925332", "Constructora Uno", null, null, null, ["pagos@constructorauno.com.do", "obra@constructorauno.com.do"]),
            new Customers.UpdateCustomerHandler());
        var first = (await h.RunAsync(new SendArAgingByEmail(h.CompanyId, w.Cobros, "a", w.S.Customer, To), new SendArAgingByEmailHandler())).ResultRef;
        var transport = new RecordingMailTransport { FailuresLeft = 1 };
        var failing = new MailDispatcher(h.App, transport, new FakePdfRenderer(), Live with { MaxAttempts = 1 }, clock);

        // Not failed yet: nothing to retry. One attempt allowed and it fails: FAILED, retried by Despacho (any sender role).
        var early = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RetryDocumentEmail(h.CompanyId, w.Cobros, "early", first), new RetryDocumentEmailHandler()));
        Assert.Equal(0, await failing.DispatchPendingAsync());
        var failed = JsonDocument.Parse(await h.QueryAsync(new ListDocumentMail(h.CompanyId, w.Cobros, "AR_AGING", w.S.Customer), new ListDocumentMailHandler())).RootElement.GetProperty("items")[0];
        var controller = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RetryDocumentEmail(h.CompanyId, w.Controller, "controller", first), new RetryDocumentEmailHandler()));
        await h.RunAsync(new RetryDocumentEmail(h.CompanyId, w.S.Dispatch, "retry", first), new RetryDocumentEmailHandler());
        Assert.Equal(1, await failing.DispatchPendingAsync());
        var second = (await h.RunAsync(new SendArAgingByEmail(h.CompanyId, w.Cobros, "b", w.S.Customer, ["obra@constructorauno.com.do"]), new SendArAgingByEmailHandler())).ResultRef;

        var history = JsonDocument.Parse(await h.QueryAsync(new ListDocumentMail(h.CompanyId, w.Cobros, "AR_AGING", w.S.Customer), new ListDocumentMailHandler())).RootElement.GetProperty("items");
        var pdf = JsonDocument.Parse(await h.QueryAsync(new GetDocumentMailPdf(h.CompanyId, w.Cobros, first), new GetDocumentMailPdfHandler())).RootElement;
        var noPdf = await Assert.ThrowsAsync<DomainException>(() => h.QueryAsync(new GetDocumentMailPdf(h.CompanyId, w.Cobros, second), new GetDocumentMailPdfHandler()));

        Assert.Equal((DocumentMailErrors.NotRetryable, AuthorizationErrors.NotAuthorized, Platform.Queries.QueryErrors.NotFound), (early.Code, controller.Code, noPdf.Code));
        Assert.Equal(("FAILED", 1, "421 4.7.0 Try again later"), (failed.GetProperty("status").GetString(), failed.GetProperty("attempts").GetInt32(), failed.GetProperty("lastError").GetString()));
        Assert.Equal($"{second}:QUEUED:False,{first}:SENT:True", string.Join(',', history.EnumerateArray().Select(m => $"{m.GetProperty("mailId").GetGuid()}:{m.GetProperty("status").GetString()}:{m.GetProperty("hasPdf").GetBoolean()}")));
        // E-MAIL-5: the customer's saved e-mails, the principal one first, are what the sender is offered.
        var offered = JsonDocument.Parse(await h.QueryAsync(new ListDocumentMail(h.CompanyId, w.Cobros, "AR_AGING", w.S.Customer), new ListDocumentMailHandler())).RootElement.GetProperty("savedEmails");
        Assert.Equal(["pagos@constructorauno.com.do", "obra@constructorauno.com.do"], offered.EnumerateArray().Select(e => e.GetString()));
        var ofInvoice = JsonDocument.Parse(await h.QueryAsync(new ListDocumentMail(h.CompanyId, w.Cobros, "QUOTE", Guid.CreateVersion7()), new ListDocumentMailHandler())).RootElement;
        Assert.Equal((0, 0), (ofInvoice.GetProperty("items").GetArrayLength(), ofInvoice.GetProperty("savedEmails").GetArrayLength()));
        Assert.Equal(("LIVE", "compras@constructorauno.com.do"), (history[1].GetProperty("deliveryMode").GetString(), history[1].GetProperty("deliveredTo")[0].GetString()));
        Assert.Equal(Convert.ToBase64String(Assert.Single(transport.Sent).Pdf), pdf.GetProperty("contentBase64").GetString());
        Assert.Equal(await h.ScalarAsync<string>("SELECT pdf_sha256 FROM core.mail_message WHERE mail_id = @id", ("id", first)), pdf.GetProperty("sha256").GetString());
    }
}
