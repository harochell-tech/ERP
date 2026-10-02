using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Mail;
using Rochell.Platform.Queries;
using Rochell.Sales.Proformas;
using Rochell.Sales.Queries;

namespace Rochell.Sales.Mail;

public static class DocumentMailErrors
{
    /// <summary>E-MAIL-01-7: only a document that prints without a watermark is sent.</summary>
    public const string NotSendable = "MAIL_DOCUMENT_NOT_SENDABLE";

    /// <summary>E-MAIL-01-4: this deployment does not send mail.</summary>
    public const string Disabled = "MAIL_DISABLED";

    public const string NotRetryable = "MAIL_NOT_RETRYABLE";
}

/// <summary>E-MAIL-6: sends a quote already sent or converted, and not expired, as a PDF to the recipients given (E-MAIL-5).</summary>
public sealed record SendQuoteByEmail(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid QuoteId, IReadOnlyList<string> Recipients, string? Message = null) : ICommand;

/// <summary>Sends a proforma that is not voided.</summary>
public sealed record SendProformaByEmail(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ProformaId, IReadOnlyList<string> Recipients, string? Message = null) : ICommand;

/// <summary>Sends a delivery note once the truck has gone out of the gate.</summary>
public sealed record SendDeliveryByEmail(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid DeliveryId, IReadOnlyList<string> Recipients, string? Message = null) : ICommand;

/// <summary>Sends a customer's statement of account of a period (at most a year and a day, as on screen).</summary>
public sealed record SendStatementByEmail(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PartyId, DateOnly From, DateOnly To, IReadOnlyList<string> Recipients, string? Message = null) : ICommand;

/// <summary>Sends a customer's open invoices and proformas by age, as of today.</summary>
public sealed record SendArAgingByEmail(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PartyId, IReadOnlyList<string> Recipients, string? Message = null) : ICommand;

/// <summary>E-MAIL-01-10: queues a FAILED message again — the same content to the same recipients.</summary>
public sealed record RetryDocumentEmail(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid MailId) : ICommand;

/// <summary>What the send commands share: the fixed Spanish texts (E-MAIL-01-9), the snapshot through the print queries, and the queue.</summary>
internal static partial class DocumentMail
{
    public const string Aggregate = "DocumentMail";

    private const int MaxMessageLength = 2000;

    /// <summary>The print query's own result, read inside the command's transaction: the PDF carries exactly what the print view shows.</summary>
    public static async Task<TResult> ReadAsync<TQuery, TResult>(CommandContext context, IQueryHandler<TQuery> handler, TQuery query, CancellationToken cancellationToken)
        where TQuery : IQuery
    {
        var json = await handler.HandleAsync(query, new QueryContext(context.Connection, context.Transaction, context.CompanyId, context.SessionId, context.Clock), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TResult>(json, ApiJson.Options)!;
    }

    public static async Task<Issuer> IssuerAsync(CommandContext context, CancellationToken cancellationToken)
        => (await Reading.SingleOrDefaultAsync(
               context.Connection, context.Transaction, "SELECT legal_name, rnc FROM md.company WHERE company_id = @c", r => new Issuer(r.GetString(0), r.GetString(1)), cancellationToken,
               ("c", context.CompanyId)).ConfigureAwait(false))!;

    public static string FileName(string name) => Unsafe().Replace(name, "-") + ".pdf";

    /// <summary>
    /// Queues the message as the command's result (its id is the mail's). <paramref name="lead"/> is the sentence that names the
    /// document; the sender's optional message follows it.
    /// </summary>
    public static async Task<string> QueueAsync(
        CommandContext context, MailSwitch? mail, string documentType, Guid documentId, string documentNo, Guid partyId, IReadOnlyList<string> recipients, string? message, string subject,
        string lead, string issuerName, string fileName, string html, CancellationToken cancellationToken)
    {
        if (mail is { Enabled: false })
        {
            throw new DomainException(DocumentMailErrors.Disabled, "This deployment does not send mail (E-MAIL-01-4).");
        }

        var note = (message ?? string.Empty).Trim();
        if (note.Length > MaxMessageLength)
        {
            throw new DomainException(MailErrors.FieldInvalid, $"The message takes at most {MaxMessageLength} characters.");
        }

        var to = MailOutbox.NormalizeRecipients(recipients);
        var sender = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT u.user_id, coalesce(u.display_name, u.email, '') FROM iam.session s JOIN iam.user u ON u.user_id = s.user_id WHERE s.session_id = @s",
            r => new Sender(r.GetGuid(0), r.GetString(1)),
            cancellationToken,
            ("s", context.SessionId)).ConfigureAwait(false);
        var body = $"Estimado cliente:\n\n{lead}\n\n{(note.Length == 0 ? string.Empty : note + "\n\n")}Atentamente,\n{(sender!.Name.Length == 0 ? string.Empty : sender.Name + "\n")}{issuerName}";
        var id = context.ResultRef;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "DocumentEmailRequested",
                1,
                Aggregate,
                id,
                1,
                JsonSerializer.Serialize(new { mailId = id, documentType, documentId, documentNo, recipients = to, subject }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await MailOutbox.EnqueueAsync(context, id, new MailDraft(documentType, documentId, documentNo, partyId, to, subject, body, fileName, html), eventId, sender.UserId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { mailId = id, status = "QUEUED", recipients = to.Count });
    }

    private sealed record Sender(Guid UserId, string Name);

    [GeneratedRegex("[^A-Za-z0-9._-]+")]
    private static partial Regex Unsafe();
}

[RequiresPermission("quote:email")]
public sealed class SendQuoteByEmailHandler(MailSwitch? mail = null) : ICommandHandler<SendQuoteByEmail>
{
    public string CommandType => "Sales.SendQuoteByEmail";

    public async Task<string> HandleAsync(SendQuoteByEmail command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var quote = await DocumentMail.ReadAsync<GetQuotePrint, QuotePrint>(context, new GetQuotePrintHandler(), new GetQuotePrint(context.CompanyId, context.SessionId, command.QuoteId), cancellationToken).ConfigureAwait(false);

        // E-MAIL-01-7: what prints without a watermark — sent and still valid, or already converted into an order.
        if (!(quote.Status == "CONVERTED" || (quote.Status == "SENT" && !quote.Expired)))
        {
            throw new DomainException(DocumentMailErrors.NotSendable, $"Quote {quote.QuoteNo} is {quote.Status}{(quote.Expired ? " and expired" : string.Empty)}: only a sent, valid quote is e-mailed.");
        }

        var party = await SalesSql.ScalarAsync<Guid?>(context, "SELECT party_id FROM sal.quote WHERE company_id = @c AND quote_id = @q", cancellationToken, ("c", context.CompanyId), ("q", command.QuoteId))
            .ConfigureAwait(false);
        return await DocumentMail.QueueAsync(
            context, mail, "QUOTE", command.QuoteId, quote.QuoteNo, party!.Value, command.Recipients, command.Message, $"Cotización {quote.QuoteNo} — {quote.IssuerName}",
            $"Adjuntamos la cotización {quote.QuoteNo}, válida hasta el {DocumentHtml.Date(quote.ValidUntil)}.", quote.IssuerName, DocumentMail.FileName(quote.QuoteNo), DocumentHtml.Quote(quote),
            cancellationToken).ConfigureAwait(false);
    }
}

[RequiresPermission("proforma:email")]
public sealed class SendProformaByEmailHandler(MailSwitch? mail = null) : ICommandHandler<SendProformaByEmail>
{
    public string CommandType => "Sales.SendProformaByEmail";

    public async Task<string> HandleAsync(SendProformaByEmail command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var proforma = await DocumentMail.ReadAsync<GetProforma, ProformaDetail>(context, new GetProformaHandler(), new GetProforma(context.CompanyId, context.SessionId, command.ProformaId), cancellationToken)
            .ConfigureAwait(false);
        var f = proforma.Header;
        if (f.Status == "VOIDED")
        {
            throw new DomainException(DocumentMailErrors.NotSendable, $"Proforma {f.ProformaNo} is voided.");
        }

        return await DocumentMail.QueueAsync(
            context, mail, "PROFORMA", command.ProformaId, f.ProformaNo, f.PartyId, command.Recipients, command.Message, $"Proforma {f.ProformaNo} — {proforma.IssuerName}",
            $"Adjuntamos la proforma {f.ProformaNo}, correspondiente al conduce {f.DeliveryNo}; vence el {DocumentHtml.Date(f.DueDate)}.", proforma.IssuerName, DocumentMail.FileName(f.ProformaNo),
            DocumentHtml.Proforma(proforma), cancellationToken).ConfigureAwait(false);
    }
}

[RequiresPermission("delivery:email")]
public sealed class SendDeliveryByEmailHandler(MailSwitch? mail = null) : ICommandHandler<SendDeliveryByEmail>
{
    public string CommandType => "Sales.SendDeliveryByEmail";

    public async Task<string> HandleAsync(SendDeliveryByEmail command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var delivery = await DocumentMail.ReadAsync<GetDeliveryPrint, DeliveryPrint>(
            context, new GetDeliveryPrintHandler(), new GetDeliveryPrint(context.CompanyId, context.SessionId, command.DeliveryId), cancellationToken).ConfigureAwait(false);
        if (delivery.GateOutAt is null || delivery.Status == "CANCELLED")
        {
            throw new DomainException(DocumentMailErrors.NotSendable, $"Delivery {delivery.DeliveryNo} has not gone out of the gate: its note is still a draft.");
        }

        var party = await SalesSql.ScalarAsync<Guid?>(
            context,
            "SELECT o.party_id FROM log.delivery d JOIN sal.sales_order o ON o.sales_order_id = d.sales_order_id WHERE d.company_id = @c AND d.delivery_id = @d",
            cancellationToken,
            ("c", context.CompanyId),
            ("d", command.DeliveryId)).ConfigureAwait(false);
        return await DocumentMail.QueueAsync(
            context, mail, "DELIVERY", command.DeliveryId, delivery.DeliveryNo, party!.Value, command.Recipients, command.Message, $"Conduce {delivery.DeliveryNo} — {delivery.IssuerName}",
            $"Adjuntamos el conduce {delivery.DeliveryNo} de su pedido {delivery.OrderNo}.", delivery.IssuerName, DocumentMail.FileName(delivery.DeliveryNo), DocumentHtml.Delivery(delivery),
            cancellationToken).ConfigureAwait(false);
    }
}

[RequiresPermission("statement:email")]
public sealed class SendStatementByEmailHandler(MailSwitch? mail = null) : ICommandHandler<SendStatementByEmail>
{
    public string CommandType => "Sales.SendStatementByEmail";

    public async Task<string> HandleAsync(SendStatementByEmail command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var statement = await DocumentMail.ReadAsync<GetCustomerStatement, CustomerStatement>(
            context, new GetCustomerStatementHandler(), new GetCustomerStatement(context.CompanyId, context.SessionId, command.PartyId, command.From, command.To), cancellationToken).ConfigureAwait(false);
        var issuer = await DocumentMail.IssuerAsync(context, cancellationToken).ConfigureAwait(false);
        var to = DocumentHtml.Date(statement.To);
        return await DocumentMail.QueueAsync(
            context, mail, "STATEMENT", command.PartyId, $"EC {to}", command.PartyId, command.Recipients, command.Message, $"Estado de cuenta al {to} — {issuer.Name}",
            $"Adjuntamos su estado de cuenta del {DocumentHtml.Date(statement.From)} al {to}.", issuer.Name,
            DocumentMail.FileName("estado-de-cuenta-" + statement.To.ToString("yyyyMMdd", CultureInfo.InvariantCulture)), DocumentHtml.Statement(statement, issuer), cancellationToken).ConfigureAwait(false);
    }
}

[RequiresPermission("statement:email")]
public sealed class SendArAgingByEmailHandler(MailSwitch? mail = null) : ICommandHandler<SendArAgingByEmail>
{
    public string CommandType => "Sales.SendArAgingByEmail";

    public async Task<string> HandleAsync(SendArAgingByEmail command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var aging = await DocumentMail.ReadAsync<GetArAging, ArAging>(context, new GetArAgingHandler(), new GetArAging(context.CompanyId, context.SessionId), cancellationToken).ConfigureAwait(false);
        var customer = aging.Customers.SingleOrDefault(c => c.CustomerId == command.PartyId)
            ?? throw new DomainException(DocumentMailErrors.NotSendable, "The customer has no open invoices or proformas today.");
        var issuer = await DocumentMail.IssuerAsync(context, cancellationToken).ConfigureAwait(false);
        var asOf = DocumentHtml.Date(aging.AsOf);
        return await DocumentMail.QueueAsync(
            context, mail, "AR_AGING", command.PartyId, $"CxC {asOf}", command.PartyId, command.Recipients, command.Message, $"Facturas pendientes al {asOf} — {issuer.Name}",
            $"Adjuntamos el detalle de sus facturas pendientes al {asOf}.", issuer.Name, DocumentMail.FileName("facturas-pendientes-" + aging.AsOf.ToString("yyyyMMdd", CultureInfo.InvariantCulture)),
            DocumentHtml.Aging(customer, aging.AsOf, aging.Buckets, issuer), cancellationToken).ConfigureAwait(false);
    }
}

[RequiresPermission("mail:retry")]
public sealed class RetryDocumentEmailHandler(MailSwitch? mail = null) : ICommandHandler<RetryDocumentEmail>
{
    public string CommandType => "Sales.RetryDocumentEmail";

    public async Task<string> HandleAsync(RetryDocumentEmail command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        if (mail is { Enabled: false })
        {
            throw new DomainException(DocumentMailErrors.Disabled, "This deployment does not send mail (E-MAIL-01-4).");
        }

        var queued = await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE core.mail_message SET status = 'QUEUED', attempts = 0, next_attempt_at = @now WHERE company_id = @c AND mail_id = @id AND status = 'FAILED'",
            cancellationToken,
            ("now", context.Clock.UtcNow),
            ("c", context.CompanyId),
            ("id", command.MailId)).ConfigureAwait(false);
        if (queued != 1)
        {
            throw new DomainException(DocumentMailErrors.NotRetryable, "Only a failed message is retried.");
        }

        var version = await SalesSql.NextEventVersionAsync(context, DocumentMail.Aggregate, command.MailId, cancellationToken).ConfigureAwait(false);
        await context.AppendEventAsync(
            new EventDraft("DocumentEmailRetried", 1, DocumentMail.Aggregate, command.MailId, version, JsonSerializer.Serialize(new { mailId = command.MailId }), Publish: true), cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.Serialize(new { mailId = command.MailId, status = "QUEUED" });
    }
}
