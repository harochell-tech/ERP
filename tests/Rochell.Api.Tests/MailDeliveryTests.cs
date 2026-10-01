using System.Security.Cryptography;
using Rochell.Api.Hosting;
using Rochell.Api.Mail;
using Rochell.Platform.Mail;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>
/// MAIL-01 against the real things (E-MAIL-1, E-MAIL-10): an SMTP server and the Chromium renderer of production. The host's mail
/// service takes a queued message, renders its PDF and sends it; in Redirect mode only the internal mailbox receives it.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class MailDeliveryTests(PostgresFixture postgres, MailFixture mail) : IClassFixture<MailFixture>
{
    private const string Sender = "industrias@rochell.com.do";

    private MailSettings Settings(MailMode mode) => new()
    {
        Mode = mode,
        FromAddress = Sender,
        RedirectTo = Sender,
        ArchiveBcc = Sender,
        RendererUrl = mail.RendererUrl,
        Smtp = new SmtpSettings { Host = mail.SmtpHost, Port = mail.SmtpPortOnHost, StartTls = false, LocalDomain = "staging.industriasrochell.com.do" },
    };

    [Fact]
    public async Task The_host_renders_the_PDF_and_sends_a_queued_message_only_to_the_internal_mailbox_in_redirect_mode()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var subject = $"Cotización {Guid.NewGuid():N}";
        using var api = new ApiHost(h, settings: new Dictionary<string, string?>
        {
            ["Rochell:Mail:Mode"] = "Redirect",
            ["Rochell:Mail:FromAddress"] = Sender,
            ["Rochell:Mail:RedirectTo"] = Sender,
            ["Rochell:Mail:Interval"] = "00:00:01",
            ["Rochell:Mail:RendererUrl"] = mail.RendererUrl,
            ["Rochell:Mail:Smtp:Host"] = mail.SmtpHost,
            ["Rochell:Mail:Smtp:Port"] = mail.SmtpPortOnHost.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Rochell:Mail:Smtp:StartTls"] = "false",
            ["Rochell:Mail:Smtp:LocalDomain"] = "staging.industriasrochell.com.do",
        });
        using var client = api.CreateClient(); // starts the host and its services
        await h.RunAsync(new QueueTestMail(h.CompanyId, h.SessionId, "m1", ["compras@cliente.com.do", "obra@cliente.com.do"], subject), new QueueTestMailHandler());

        string? state = null;
        for (var i = 0; i < 240 && state != "SENT"; i++)
        {
            await Task.Delay(250);
            state = await h.ScalarAsync<string>("SELECT status FROM core.mail_message");
        }

        Assert.True(state == "SENT", $"The message is {state}: {await h.ScalarAsync<string>("SELECT coalesce(last_error, '-') FROM core.mail_message")}");
        var received = Assert.Single(await mail.ReceivedAsync(subject));
        Assert.Equal($"[Redirigido — para: compras@cliente.com.do +1] {subject}", received.GetProperty("Subject").GetString());
        Assert.Equal(Sender, Assert.Single(received.GetProperty("To").EnumerateArray()).GetProperty("Address").GetString());
        Assert.Equal(("Industrias Rochell", Sender), (received.GetProperty("From").GetProperty("Name").GetString(), received.GetProperty("From").GetProperty("Address").GetString()));
        var message = await mail.MessageAsync(received.GetProperty("ID").GetString()!);
        Assert.StartsWith("Correo redirigido: no se envió al cliente. Destinatarios originales: compras@cliente.com.do, obra@cliente.com.do", message.GetProperty("Text").GetString(), StringComparison.Ordinal);
        var attachment = Assert.Single(message.GetProperty("Attachments").EnumerateArray());
        Assert.Equal(("COT-000001.pdf", "application/pdf"), (attachment.GetProperty("FileName").GetString(), attachment.GetProperty("ContentType").GetString()));
        var pdf = await mail.AttachmentAsync(received.GetProperty("ID").GetString()!, attachment.GetProperty("PartID").GetString()!);
        Assert.True(pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8) && pdf.Length > 1000, "The attachment is not a rendered PDF.");
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(pdf)), await h.ScalarAsync<string>("SELECT pdf_sha256 FROM core.mail_message"));
    }

    [Fact]
    public async Task A_live_message_reaches_its_recipients_and_the_archive_copy()
    {
        var subject = $"Estado de cuenta {Guid.NewGuid():N}";
        using var http = new HttpClient();
        var settings = Settings(MailMode.Live);
        var pdf = await new GotenbergPdfRenderer(http, settings).RenderAsync(QueueTestMailHandler.Html, CancellationToken.None);

        await new SmtpMailTransport(settings).SendAsync(
            new MailEnvelope(["compras@cliente.com.do", "obra@cliente.com.do"], Sender, subject, "Adjuntamos su estado de cuenta.\nGracias.", "EC-131925332.pdf", pdf), CancellationToken.None);

        var received = Assert.Single(await mail.ReceivedAsync(subject));
        Assert.Equal(["compras@cliente.com.do", "obra@cliente.com.do"], received.GetProperty("To").EnumerateArray().Select(a => a.GetProperty("Address").GetString()));
        Assert.Equal(Sender, Assert.Single(received.GetProperty("Bcc").EnumerateArray()).GetProperty("Address").GetString());
        Assert.Equal(1, received.GetProperty("Attachments").GetInt32());
    }

    [Fact]
    public async Task The_production_renderer_turns_a_document_into_a_letter_size_PDF()
    {
        using var http = new HttpClient();
        var quote = new Rochell.Sales.Queries.QuotePrint(
            "COT-000001", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), "SENT", false, "131925332", "BLOCK ROCHELL SRL", "101010101", "Constructora Uno & Hijos", "PICKUP_AT_PLANT", null, "OC-77",
            "Precios sujetos a disponibilidad", [new(1, "BLOQUE-6", "Bloque de 6 pulgadas", "un", 1000m, 50.00m, 50000.00m, 9000.00m, 59000.00m)], 50000.00m, 9000.00m, 59000.00m);

        var pdf = await new GotenbergPdfRenderer(http, Settings(MailMode.Live)).RenderAsync(Rochell.Sales.Mail.DocumentHtml.Quote(quote), CancellationToken.None);

        // One letter page (612 × 792 points) with embedded text: Chromium laid the document out.
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        Assert.StartsWith("%PDF-", text, StringComparison.Ordinal);
        Assert.Matches(@"/MediaBox\s*\[\s*0 0 612 792\s*\]", text);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, @"/Type\s*/Page\b"));
    }

    [Fact]
    public async Task Outside_Off_the_host_does_not_start_without_the_sender_the_relay_and_the_renderer()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h, settings: new Dictionary<string, string?> { ["Rochell:Mail:Mode"] = "Live", ["Rochell:Mail:Smtp:Host"] = mail.SmtpHost });

        var error = Assert.ThrowsAny<Exception>(() => api.CreateClient());

        Assert.Contains("Mail:FromAddress", error.ToString(), StringComparison.Ordinal);
    }
}
