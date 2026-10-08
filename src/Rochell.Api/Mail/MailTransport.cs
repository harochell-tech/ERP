using System.Net.Http.Headers;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Rochell.Api.Hosting;
using Rochell.Platform.Mail;
using Rochell.Platform.Time;

namespace Rochell.Api.Mail;

/// <summary>
/// E-MAIL-1 / E-MAIL-01-3: sends through an SMTP relay (Google Workspace's, authorized by the server's address, or any relay with
/// a user and password). One connection per message: the volume is a few messages a day. <see cref="SmtpSettings.LocalDomain"/> is
/// the name given in EHLO — the Workspace relay refuses a name that is not a host name.
/// </summary>
public sealed class SmtpMailTransport(MailSettings settings) : IMailTransport
{
    public async Task SendAsync(MailEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        using var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress!));
        message.To.AddRange(envelope.To.Select(MailboxAddress.Parse));
        if (envelope.Bcc is not null)
        {
            message.Bcc.Add(MailboxAddress.Parse(envelope.Bcc));
        }

        message.Subject = envelope.Subject;
        var body = new BodyBuilder { TextBody = envelope.BodyText };
        body.Attachments.Add(envelope.FileName, envelope.Pdf, new ContentType("application", "pdf"));
        if (envelope.Extra is { } extra)
        {
            body.Attachments.Add(extra.Name, extra.Content, ContentType.Parse(extra.ContentType));
        }
        message.Body = body.ToMessageBody();

        var smtp = settings.Smtp;
        using var client = new SmtpClient { Timeout = (int)smtp.Timeout.TotalMilliseconds };
        if (!string.IsNullOrWhiteSpace(smtp.LocalDomain))
        {
            client.LocalDomain = smtp.LocalDomain;
        }

        await client.ConnectAsync(smtp.Host!, smtp.Port, smtp.StartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(smtp.User))
        {
            await client.AuthenticateAsync(smtp.User, smtp.Password ?? string.Empty, cancellationToken).ConfigureAwait(false);
        }

        await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// E-MAIL-10: renders the document's HTML to a letter-size PDF with the headless Chromium of a Gotenberg container reachable only
/// from the API's network. The HTML is self-contained (no scripts, no external resources).
/// </summary>
public sealed class GotenbergPdfRenderer(HttpClient http, MailSettings settings) : IPdfRenderer
{
    public async Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        var file = new StringContent(html);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = "utf-8" };
        form.Add(file, "files", "index.html");

        // Letter, in inches (Gotenberg's unit), with the margins in the document's own CSS.
        foreach (var (name, value) in new[] { ("paperWidth", "8.5"), ("paperHeight", "11"), ("marginTop", "0.5"), ("marginBottom", "0.5"), ("marginLeft", "0.5"), ("marginRight", "0.5"), ("printBackground", "true") })
        {
            form.Add(new StringContent(value), name);
        }

        using var response = await http.PostAsync(new Uri(new Uri(settings.RendererUrl!), "/forms/chromium/convert/html"), form, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The PDF renderer answered {(int)response.StatusCode}.");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>E-MAIL-7: dispatches the queued mail every <see cref="MailSettings.Interval"/>. It does not start when the mode is Off.</summary>
public sealed class MailService(MailSettings settings, AppDatabase database, IMailTransport transport, IPdfRenderer renderer, IClock clock, ILogger<MailService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (settings.Mode == MailMode.Off)
        {
            logger.LogInformation("Outgoing mail is off by configuration: queued messages wait.");
            return;
        }

        logger.LogInformation("Outgoing mail in {Mode} mode, from {From}.", settings.Mode, settings.FromAddress);
        var dispatcher = new MailDispatcher(database.DataSource, transport, renderer, settings.Delivery(), clock);
        using var timer = new PeriodicTimer(settings.Interval);
        do
        {
            try
            {
                await dispatcher.DispatchPendingAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Mail dispatch pass failed; retrying at the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
