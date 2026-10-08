using System.Text.RegularExpressions;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Platform.Mail;

/// <summary>
/// Whether this deployment sends mail (E-MAIL-01-4). When it does not, the commands that send documents refuse instead of queueing
/// messages that would leave, stale, the day the mode changes.
/// </summary>
public sealed record MailSwitch(bool Enabled);

public static class MailErrors
{
    public const string RecipientInvalid = "MAIL_RECIPIENT_INVALID";
    public const string FieldInvalid = "MAIL_FIELD_INVALID";
}

/// <summary>
/// A document to send by e-mail (E-MAIL-3): the snapshot of the document as self-contained HTML — the dispatcher renders it to the
/// PDF that is attached — with who receives it, the subject and the plain-text body. <c>DocumentId</c> is the document's own id
/// (for a statement of account, the customer).
/// </summary>
public sealed record MailDraft(
    string DocumentType, Guid DocumentId, string DocumentNo, Guid? PartyId, IReadOnlyList<string> Recipients, string Subject, string BodyText, string FileName, string Html,
    MailAttachment? Extra = null);

/// <summary>VS4-05 (E-VS4-05-1): a second file attached as it is (the signed XML of an e-CF).</summary>
public sealed record MailAttachment(string Name, byte[] Content, string ContentType);

/// <summary>Queues outgoing mail inside the command that asks for it (E-MAIL-7): the message exists only if the command commits.</summary>
public static partial class MailOutbox
{
    /// <summary>E-MAIL-01-9: at most ten recipients per message.</summary>
    public const int MaxRecipients = 10;

    private const int MaxAddressLength = 200;

    /// <summary>Trimmed, lower-cased and without repetitions, in the order given. Refuses an empty list, more than ten or a malformed address.</summary>
    public static IReadOnlyList<string> NormalizeRecipients(IReadOnlyList<string>? recipients)
    {
        var addresses = (recipients ?? []).Select(r => (r ?? string.Empty).Trim().ToLowerInvariant()).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (addresses.Count == 0)
        {
            throw new DomainException(MailErrors.RecipientInvalid, "Give at least one e-mail address.");
        }

        if (addresses.Count > MaxRecipients)
        {
            throw new DomainException(MailErrors.RecipientInvalid, $"A message takes at most {MaxRecipients} recipients (E-MAIL-01-9).");
        }

        var invalid = addresses.FirstOrDefault(a => a.Length > MaxAddressLength || !Address().IsMatch(a));
        if (invalid is not null)
        {
            throw new DomainException(MailErrors.RecipientInvalid, $"{invalid} is not a valid e-mail address.");
        }

        return addresses;
    }

    /// <summary>
    /// Queues the message <paramref name="mailId"/> for the dispatcher. <paramref name="requestEventId"/> is the event of the command
    /// that asked for it.
    /// </summary>
    public static async Task EnqueueAsync(CommandContext context, Guid mailId, MailDraft draft, Guid requestEventId, Guid requestedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(draft);
        var recipients = NormalizeRecipients(draft.Recipients);
        var subject = (draft.Subject ?? string.Empty).Trim();
        var body = (draft.BodyText ?? string.Empty).Trim();
        if (subject.Length is 0 or > 200 || subject.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            throw new DomainException(MailErrors.FieldInvalid, "The subject is one line of 1 to 200 characters.");
        }

        if (body.Length is 0 or > 5000)
        {
            throw new DomainException(MailErrors.FieldInvalid, "The message is 1 to 5,000 characters.");
        }

        if (string.IsNullOrWhiteSpace(draft.Html) || !FileName().IsMatch(draft.FileName ?? string.Empty))
        {
            throw new DomainException(MailErrors.FieldInvalid, "The document needs its HTML and a .pdf file name of letters, digits, dots, dashes and underscores.");
        }

        var now = context.Clock.UtcNow;
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO core.mail_message (mail_id, company_id, document_type, document_id, document_no, party_id, recipients, subject, body_text, file_name, html, status,
                                           next_attempt_at, request_event_id, requested_by, requested_at, attachment_name, attachment, attachment_type)
            VALUES (@id, @c, @type, @doc, @no, @party, @to, @subject, @body, @file, @html, 'QUEUED', @now, @event, @by, @now, @an, @ab, @at)
            """,
            cancellationToken,
            ("an", (object?)draft.Extra?.Name ?? DBNull.Value),
            ("ab", (object?)draft.Extra?.Content ?? DBNull.Value),
            ("at", (object?)draft.Extra?.ContentType ?? DBNull.Value),
            ("id", mailId),
            ("c", context.CompanyId),
            ("type", draft.DocumentType),
            ("doc", draft.DocumentId),
            ("no", draft.DocumentNo),
            ("party", draft.PartyId),
            ("to", recipients.ToArray()),
            ("subject", subject),
            ("body", body),
            ("file", draft.FileName),
            ("html", draft.Html),
            ("now", now),
            ("event", requestEventId),
            ("by", requestedBy)).ConfigureAwait(false);
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex Address();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,95}\.pdf$")]
    private static partial Regex FileName();
}
