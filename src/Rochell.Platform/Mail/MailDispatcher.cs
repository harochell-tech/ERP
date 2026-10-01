using System.Data.Common;
using System.Security.Cryptography;
using Rochell.Platform.Data;
using Rochell.Platform.Time;

namespace Rochell.Platform.Mail;

/// <summary>E-MAIL-01-4: where queued mail goes in a deployment.</summary>
public enum MailMode
{
    /// <summary>Nothing is dispatched; queued messages wait.</summary>
    Off,

    /// <summary>Every message goes to one internal mailbox, with the intended recipients named in the subject and the body.</summary>
    Redirect,

    /// <summary>Messages go to their recipients.</summary>
    Live,
}

/// <summary>
/// How messages leave: the mode, the internal mailbox of <see cref="MailMode.Redirect"/>, the archive copy (E-MAIL-01-5) and the
/// attempts allowed before a message is FAILED (E-MAIL-01-10).
/// </summary>
public sealed record MailDelivery(MailMode Mode, string? RedirectTo, string? ArchiveBcc, int MaxAttempts = 5);

/// <summary>What the transport sends: the envelope recipients, the text and the one PDF attached.</summary>
public sealed record MailEnvelope(IReadOnlyList<string> To, string? Bcc, string Subject, string BodyText, string FileName, byte[] Pdf);

public interface IMailTransport
{
    Task SendAsync(MailEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>Renders a self-contained HTML document to PDF (E-MAIL-10).</summary>
public interface IPdfRenderer
{
    Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken);
}

/// <summary>
/// Sends the queued mail (E-MAIL-7): claims one QUEUED message at a time with FOR UPDATE SKIP LOCKED, renders its PDF once (kept
/// with its SHA-256), sends it and records the attempt. A failure leaves it QUEUED with a growing delay; after the attempts
/// allowed it is FAILED until a person retries it. Delivery is at-least-once: a crash between the SMTP acceptance and the commit
/// sends the message again.
/// </summary>
public sealed class MailDispatcher
{
    private const int MaxErrorLength = 500;

    /// <summary>Minutes to wait after the first, second… failed attempt; the last one repeats.</summary>
    private static readonly int[] BackoffMinutes = [1, 5, 15, 60];

    private readonly DbDataSource _dataSource;
    private readonly IMailTransport _transport;
    private readonly IPdfRenderer _renderer;
    private readonly MailDelivery _delivery;
    private readonly IClock _clock;

    public MailDispatcher(DbDataSource dataSource, IMailTransport transport, IPdfRenderer renderer, MailDelivery delivery, IClock clock)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(delivery.MaxAttempts);
        if (delivery.Mode == MailMode.Redirect && string.IsNullOrWhiteSpace(delivery.RedirectTo))
        {
            throw new ArgumentException("Redirect mode needs the internal mailbox (E-MAIL-01-4).", nameof(delivery));
        }
    }

    private sealed record Claimed(Guid MailId, IReadOnlyList<string> Recipients, string Subject, string BodyText, string FileName, string Html, byte[]? Pdf, int Attempts);

    /// <summary>One pass over every company: up to <paramref name="batchSize"/> due messages each. Returns how many were sent.</summary>
    public async Task<int> DispatchPendingAsync(int batchSize = 20, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        if (_delivery.Mode == MailMode.Off)
        {
            return 0;
        }

        List<Guid> companies;
        await using (var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            companies = await Reading.ListAsync(connection, null, "SELECT company_id FROM md.company ORDER BY company_id", r => r.GetGuid(0), cancellationToken).ConfigureAwait(false);
        }

        var sent = 0;
        foreach (var company in companies)
        {
            for (var i = 0; i < batchSize; i++)
            {
                var outcome = await DispatchOneAsync(company, cancellationToken).ConfigureAwait(false);
                if (outcome is null)
                {
                    break;
                }

                sent += outcome.Value ? 1 : 0;
            }
        }

        return sent;
    }

    /// <summary>Null: nothing due. True: sent. False: the attempt failed.</summary>
    private async Task<bool?> DispatchOneAsync(Guid companyId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(connection, transaction, "SELECT set_config('app.company_id', @company, true)", cancellationToken, ("company", companyId.ToString())).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var message = await Reading.SingleOrDefaultAsync(
            connection,
            transaction,
            """
            SELECT mail_id, recipients, subject, body_text, file_name, html, pdf, attempts
            FROM core.mail_message
            WHERE company_id = @c AND status = 'QUEUED' AND next_attempt_at <= @now
            ORDER BY next_attempt_at, requested_at, mail_id
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """,
            r => new Claimed(r.GetGuid(0), r.GetFieldValue<string[]>(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetFieldValue<byte[]>(6), r.GetInt32(7)),
            cancellationToken,
            ("c", companyId),
            ("now", now)).ConfigureAwait(false);
        if (message is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var attempt = message.Attempts + 1;
        string? error = null;
        byte[]? rendered = null;
        IReadOnlyList<string> deliveredTo = [];
        try
        {
            var pdf = message.Pdf;
            if (pdf is null)
            {
                pdf = await _renderer.RenderAsync(message.Html, cancellationToken).ConfigureAwait(false);
                if (pdf.Length < 5 || !pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
                {
                    throw new InvalidOperationException("The renderer did not return a PDF.");
                }

                rendered = pdf;
            }

            var envelope = Envelope(message, pdf);
            await _transport.SendAsync(envelope, cancellationToken).ConfigureAwait(false);
            deliveredTo = envelope.To;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message.Length > MaxErrorLength ? ex.Message[..MaxErrorLength] : ex.Message;
            error = string.IsNullOrWhiteSpace(error) ? ex.GetType().Name : error;
        }

        if (rendered is not null)
        {
            await Sql.ExecuteAsync(
                connection,
                transaction,
                "UPDATE core.mail_message SET pdf = @pdf, pdf_sha256 = @sha WHERE mail_id = @id",
                cancellationToken,
                ("pdf", rendered),
                ("sha", Convert.ToHexStringLower(SHA256.HashData(rendered))),
                ("id", message.MailId)).ConfigureAwait(false);
        }

        if (error is null)
        {
            await Sql.ExecuteAsync(
                connection,
                transaction,
                "UPDATE core.mail_message SET status = 'SENT', attempts = @n, sent_at = @now, delivery_mode = @mode, delivered_to = @to, last_error = NULL WHERE mail_id = @id",
                cancellationToken,
                ("n", attempt),
                ("now", now),
                ("mode", _delivery.Mode == MailMode.Live ? "LIVE" : "REDIRECT"),
                ("to", deliveredTo.ToArray()),
                ("id", message.MailId)).ConfigureAwait(false);
        }
        else
        {
            var failed = attempt >= _delivery.MaxAttempts;
            await Sql.ExecuteAsync(
                connection,
                transaction,
                "UPDATE core.mail_message SET status = @status, attempts = @n, next_attempt_at = @next, last_error = @error WHERE mail_id = @id",
                cancellationToken,
                ("status", failed ? "FAILED" : "QUEUED"),
                ("n", attempt),
                ("next", now.AddMinutes(BackoffMinutes[Math.Min(attempt, BackoffMinutes.Length) - 1])),
                ("error", error),
                ("id", message.MailId)).ConfigureAwait(false);
        }

        await Sql.ExecuteAsync(
            connection,
            transaction,
            // Numbered over the message's whole life: a retry starts the count of attempts again, not the trail.
            """
            INSERT INTO core.mail_attempt (company_id, mail_id, attempt_no, attempted_at, outcome, error)
            SELECT @c, @id, coalesce(max(attempt_no), 0) + 1, @now, @outcome, @error FROM core.mail_attempt WHERE mail_id = @id
            """,
            cancellationToken,
            ("c", companyId),
            ("id", message.MailId),
            ("now", now),
            ("outcome", error is null ? "SENT" : "FAILED"),
            ("error", error)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return error is null;
    }

    /// <summary>
    /// Live: to the recipients, with the archive copy. Redirect (E-MAIL-01-4): only to the internal mailbox, the intended recipients
    /// named in the subject and in the first line of the body; no customer address is on the envelope.
    /// </summary>
    private MailEnvelope Envelope(Claimed message, byte[] pdf)
    {
        if (_delivery.Mode == MailMode.Live)
        {
            return new MailEnvelope(message.Recipients, string.IsNullOrWhiteSpace(_delivery.ArchiveBcc) ? null : _delivery.ArchiveBcc, message.Subject, message.BodyText, message.FileName, pdf);
        }

        var others = message.Recipients.Count - 1;
        var intended = message.Recipients[0] + (others > 0 ? $" +{others}" : string.Empty);
        return new MailEnvelope(
            [_delivery.RedirectTo!],
            null,
            $"[Redirigido — para: {intended}] {message.Subject}",
            $"Correo redirigido: no se envió al cliente. Destinatarios originales: {string.Join(", ", message.Recipients)}\n\n{message.BodyText}",
            message.FileName,
            pdf);
    }
}
