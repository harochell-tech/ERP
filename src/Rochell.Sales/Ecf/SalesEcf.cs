using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Tax.Ecf;

namespace Rochell.Sales.Ecf;

// VS4-03 (E-VS4-3, E-VS4-5/6, E-VS4-03-1/3): invoices and credit notes through the e-CF gateway. The issuing command queues the e-CF in
// its own transaction (fiscal ECF_SENDING); the gateway's answer comes back through the updaters below — accepted: ECF_ACCEPTED with the
// e-NCF; rejected: ECF_REJECTED (resent with another e-NCF, or voided); needs attention: ECF_ACTION.

public static class SalesEcf
{
    public const string InvoiceKind = "INVOICE";
    public const string CreditNoteKind = "CREDIT_NOTE";
    public const string CreditNoteType = "34";

    internal static async Task<EcfEnqueued> QueueInvoiceAsync(
        CommandContext context, Guid invoiceId, string ecfType, DateOnly issueDate, string commandType, CancellationToken cancellationToken)
    {
        var build = await EcfPayloads.InvoiceAsync(context, invoiceId, cancellationToken).ConfigureAwait(false);
        return await EcfQueue.EnqueueAsync(context, InvoiceKind, invoiceId, ecfType, issueDate, build, commandType, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<EcfEnqueued> QueueCreditNoteAsync(CommandContext context, Guid creditNoteId, DateOnly issueDate, string commandType, CancellationToken cancellationToken)
    {
        var build = await EcfPayloads.CreditNoteAsync(context, creditNoteId, cancellationToken).ConfigureAwait(false);
        return await EcfQueue.EnqueueAsync(context, CreditNoteKind, creditNoteId, CreditNoteType, issueDate, build, commandType, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The fiscal status of the document for the gateway's status; null when the e-CF has no answer yet.</summary>
    internal static string? FiscalOf(string ecfStatus) => ecfStatus switch
    {
        EcfStatuses.Accepted or EcfStatuses.AcceptedConditional => "ECF_ACCEPTED",
        EcfStatuses.Rejected => "ECF_REJECTED",
        EcfStatuses.RequiresAction => "ECF_ACTION",
        _ => null,
    };
}

/// <summary>E-VS4-5/6: the invoice follows its e-CF.</summary>
public sealed class InvoiceEcfUpdater : IEcfSourceUpdater
{
    public string SourceKind => SalesEcf.InvoiceKind;

    public Task OnStatusAsync(CommandContext context, EcfDocumentSnapshot document, CancellationToken cancellationToken)
        => EcfFollow.ApplyAsync(context, document, "sal.invoice", "invoice_id", "Invoice", "InvoiceFiscalStatusChanged", cancellationToken);
}

/// <summary>E-VS4-7: the credit note follows its e-CF 34.</summary>
public sealed class CreditNoteEcfUpdater : IEcfSourceUpdater
{
    public string SourceKind => SalesEcf.CreditNoteKind;

    public Task OnStatusAsync(CommandContext context, EcfDocumentSnapshot document, CancellationToken cancellationToken)
        => EcfFollow.ApplyAsync(context, document, "sal.credit_note", "credit_note_id", "CreditNote", "CreditNoteFiscalStatusChanged", cancellationToken);
}

internal static class EcfFollow
{
    public static async Task ApplyAsync(
        CommandContext context, EcfDocumentSnapshot document, string table, string key, string aggregate, string eventType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(document);
        if (SalesEcf.FiscalOf(document.Status) is not { } fiscal)
        {
            return;
        }

        var row = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            $"SELECT fiscal_status, version FROM {table} WHERE company_id = @c AND {key} = @id FOR UPDATE",
            r => (Fiscal: r.GetString(0), Version: r.GetInt64(1)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", document.SourceId)).ConfigureAwait(false)).Single();
        if (row.Fiscal is not ("ECF_SENDING" or "ECF_ACTION") || row.Fiscal == fiscal)
        {
            return; // an older attempt's answer, or nothing new
        }

        var version = row.Version + 1;
        await context.AppendEventAsync(
            new EventDraft(
                eventType, 1, aggregate, document.SourceId, version,
                JsonSerializer.Serialize(new { id = document.SourceId, from = row.Fiscal, to = fiscal, encf = document.Encf, ecfDocumentId = document.DocumentId, reason = document.Reason }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            $"UPDATE {table} SET fiscal_status = @f, encf = CASE WHEN @f = 'ECF_ACCEPTED' THEN @e ELSE encf END, version = @v WHERE {key} = @id",
            cancellationToken,
            ("f", fiscal),
            ("e", document.Encf),
            ("v", version),
            ("id", document.SourceId)).ConfigureAwait(false);
    }
}

/// <summary>E-VS4-03-3: a rejected invoice is sent again — a new attempt with another e-NCF, the same amounts and the customer's current data.</summary>
public sealed record ResendInvoiceEcf(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid InvoiceId, long ExpectedVersion) : ICommand;

/// <summary>E-VS4-03-3: a rejected credit note is sent again with another e-NCF.</summary>
public sealed record ResendCreditNoteEcf(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid CreditNoteId, long ExpectedVersion) : ICommand;

[RequiresPermission("invoice:issue", StepUp = true)]
public sealed class ResendInvoiceEcfHandler(EcfSwitch? gateway = null) : ICommandHandler<ResendInvoiceEcf>
{
    private readonly EcfSwitch _gateway = gateway ?? EcfSwitch.Off;

    public string CommandType => "Sales.ResendInvoiceEcf";

    public async Task<string> HandleAsync(ResendInvoiceEcf command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var (type, version) = await Resending.PrepareAsync(
            context, _gateway, "sal.invoice", "invoice_id", "ecf_type", "Invoice", "InvoiceEcfResent", command.InvoiceId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        var queued = await SalesEcf.QueueInvoiceAsync(context, command.InvoiceId, type, SalesSql.Today(context), CommandType, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { invoiceId = command.InvoiceId, fiscalStatus = "ECF_SENDING", ecfNumber = queued.Encf, attempt = queued.Attempt, version });
    }
}

[RequiresPermission("credit_note:issue", StepUp = true)]
public sealed class ResendCreditNoteEcfHandler(EcfSwitch? gateway = null) : ICommandHandler<ResendCreditNoteEcf>
{
    private readonly EcfSwitch _gateway = gateway ?? EcfSwitch.Off;

    public string CommandType => "Sales.ResendCreditNoteEcf";

    public async Task<string> HandleAsync(ResendCreditNoteEcf command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var (_, version) = await Resending.PrepareAsync(
            context, _gateway, "sal.credit_note", "credit_note_id", "'34'", "CreditNote", "CreditNoteEcfResent", command.CreditNoteId, command.ExpectedVersion, cancellationToken)
            .ConfigureAwait(false);
        var queued = await SalesEcf.QueueCreditNoteAsync(context, command.CreditNoteId, SalesSql.Today(context), CommandType, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { creditNoteId = command.CreditNoteId, fiscalStatus = "ECF_SENDING", ecfNumber = queued.Encf, attempt = queued.Attempt, version });
    }
}

internal static class Resending
{
    public static async Task<(string Type, long Version)> PrepareAsync(
        CommandContext context, EcfSwitch gateway, string table, string key, string typeColumn, string aggregate, string eventType, Guid id, long expectedVersion,
        CancellationToken cancellationToken)
    {
        var row = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            $"SELECT commercial_status, fiscal_status, {typeColumn}, version FROM {table} WHERE company_id = @c AND {key} = @id FOR UPDATE",
            r => (Commercial: r.GetString(0), Fiscal: r.GetString(1), Type: r.GetString(2), Version: r.GetInt64(3)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", id)).ConfigureAwait(false)).SingleOrDefault();
        if (row == default)
        {
            throw new DomainException(SalesErrors.NotFound, "The document does not exist.");
        }

        if (row.Version != expectedVersion)
        {
            throw new DomainException(SalesErrors.VersionConflict, $"The document changed (version {row.Version}, expected {expectedVersion}); reload and retry.");
        }

        if (row.Commercial is "DRAFT" or "VOIDED" || row.Fiscal != "ECF_REJECTED")
        {
            throw new DomainException(SalesErrors.InvalidState, $"Only an e-CF the DGII rejected is sent again (it is {row.Commercial} / {row.Fiscal}).");
        }

        if (!await EcfQueue.UsesGatewayAsync(context, gateway, row.Type, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(EcfErrors.SeriesMissing, $"The gateway is off or e-CF {row.Type} has no ACTIVE range; it cannot be sent again.");
        }

        var version = row.Version + 1;
        await context.AppendEventAsync(
            new EventDraft(eventType, 1, aggregate, id, version, JsonSerializer.Serialize(new { id, from = row.Fiscal, to = "ECF_SENDING" }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, $"UPDATE {table} SET fiscal_status = 'ECF_SENDING', version = @v WHERE {key} = @id", cancellationToken, ("v", version), ("id", id))
            .ConfigureAwait(false);
        return (row.Type, version);
    }
}
