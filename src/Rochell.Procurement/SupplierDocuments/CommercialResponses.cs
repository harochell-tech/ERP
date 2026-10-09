using System.Diagnostics;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Procurement.PurchaseOrders;
using Rochell.Tax.Ecf;

namespace Rochell.Procurement.SupplierDocuments;

// OCR1-02 (E-OCR-3, E-OCR1-01-2/3/8, E-OCR1-02-3/4/9): the commercial response to the DGII. A person accepts or rejects (with a reason);
// posting the invoice accepts; the response is kept at once and the worker sends it to Alanube, again at each pass until Alanube takes it.

/// <summary>E-OCR-3: accepts or rejects a received e-CF before the DGII (<c>supplier_document:respond</c>, step-up).</summary>
public sealed record RespondToSupplierDocument(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SupplierDocumentId, long ExpectedVersion, bool Accept, string? Reason = null) : ICommand;

/// <summary>E-OCR1-02-3: sends a kept response to Alanube (the daily process).</summary>
public sealed record SendSupplierDocumentResponse(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SupplierDocumentId) : ICommand;

[RequiresPermission("supplier_document:respond", StepUp = true)]
public sealed class RespondToSupplierDocumentHandler : ICommandHandler<RespondToSupplierDocument>
{
    public string CommandType => "Procurement.RespondToSupplierDocument";

    public async Task<string> HandleAsync(RespondToSupplierDocument command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await SupplierDocumentStore.LockAsync(context, command.SupplierDocumentId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.ProviderId is null || row.ReceivedStatus != ReceivedStatuses.Received)
        {
            throw new DomainException(ProcurementErrors.SupplierDocumentNotAnswerable, "Only an e-CF that Alanube received can be accepted or rejected before the DGII.");
        }

        if (row.Response != ReceivedStatuses.NotDeclared)
        {
            throw new DomainException(ProcurementErrors.SupplierDocumentNotAnswerable, "This e-CF was already answered before the DGII; the answer is given once.");
        }

        string? reason = null;
        if (!command.Accept)
        {
            reason = PurchaseOrderStore.RequireReason(command.Reason);
            if (reason.Length > 250)
            {
                throw new DomainException(ProcurementErrors.ReasonRequired, "The reason for rejecting has at most 250 characters.");
            }

            // A posted invoice stands on this e-CF: it is reversed first.
            if (await SupplierDocumentLinks.InvoicePostedAsync(context, row.SiId, cancellationToken).ConfigureAwait(false))
            {
                throw new DomainException(ProcurementErrors.SupplierDocumentPosted, "The invoice of this e-CF is posted; reverse it before rejecting the e-CF.");
            }
        }

        var user = await PurchaseOrderStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var response = command.Accept ? ReceivedStatuses.Accepted : ReceivedStatuses.Rejected;
        var version = await SupplierDocumentStore.ChangeAsync(
            context, row, CommandType, "SupplierDocumentAnswered", new { supplierDocumentId = row.Id, commercialResponse = response, reason, source = "CORE" },
            "commercial_response = @r, response_reason = @reason, responded_by = @u, responded_at = @now", cancellationToken,
            parameters: [("r", response), ("reason", reason), ("u", user), ("now", context.Clock.UtcNow)]).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { supplierDocumentId = row.Id, commercialResponse = response, version });
    }
}

[RequiresPermission("ecf:process")]
public sealed class SendSupplierDocumentResponseHandler(IEcfReception reception) : ICommandHandler<SendSupplierDocumentResponse>
{
    public string CommandType => "Procurement.SendSupplierDocumentResponse";

    public async Task<string> HandleAsync(SendSupplierDocumentResponse command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await SupplierDocumentStore.LockAsync(context, command.SupplierDocumentId, null, cancellationToken).ConfigureAwait(false);
        if (row.Response == ReceivedStatuses.NotDeclared || row.ResponseSentAt is not null || row.ProviderId is null)
        {
            return JsonSerializer.Serialize(new { supplierDocumentId = row.Id, outcome = "nothing to send" });
        }

        var accept = row.Response == ReceivedStatuses.Accepted;
        var watch = Stopwatch.StartNew();
        var result = await reception.RespondAsync(row.ProviderId, accept, row.ResponseReason, cancellationToken).ConfigureAwait(false);
        var outcome = result.Kind switch
        {
            SubmitKind.Registered => "OK",
            SubmitKind.Transient => result.HttpStatus is null ? "TIMEOUT" : "ERROR",
            _ => "REJECTED",
        };
        await EcfCallLog.RecordAsync(context, reception.Mode, EcfCallLog.CommercialResponse, result.HttpStatus, outcome, result.Code, $"{row.FiscalNumber}: {result.Message}", watch, cancellationToken)
            .ConfigureAwait(false);
        if (result.Kind != SubmitKind.Registered)
        {
            // Kept for the next pass; Inicio and the inbox show it after a day (E-OCR1-02-3).
            return JsonSerializer.Serialize(new { supplierDocumentId = row.Id, outcome = "not sent", code = result.Code, message = result.Message });
        }

        var version = await SupplierDocumentStore.ChangeAsync(
            context, row, CommandType, "SupplierDocumentResponseSent", new { supplierDocumentId = row.Id, commercialResponse = row.Response, approvalId = result.ProviderId },
            "response_sent_at = @now", cancellationToken, parameters: ("now", context.Clock.UtcNow)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { supplierDocumentId = row.Id, outcome = "sent", version });
    }
}

/// <summary>
/// What the supplier invoice commands do to the document they came from: posting is refused on a rejected e-CF and accepts one not yet
/// answered (E-OCR1-02-4); voiding or reversing the invoice returns the document to the inbox, its answer kept (E-OCR1-02-9).
/// </summary>
internal static class SupplierDocumentLinks
{
    public static async Task<bool> InvoicePostedAsync(CommandContext context, Guid? siId, CancellationToken cancellationToken)
        => siId is { } id && (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT accounting_status::text FROM pur.supplier_invoice WHERE si_id = @s", r => r.GetString(0), cancellationToken, ("s", id))
            .ConfigureAwait(false)).SingleOrDefault() == "POSTED";

    /// <summary>E-OCR1-02-4: an invoice whose e-CF was rejected before the DGII is not posted.</summary>
    public static async Task EnsureNotRejectedAsync(CommandContext context, Guid siId, CancellationToken cancellationToken)
    {
        var rejected = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT fiscal_number FROM pur.supplier_document WHERE company_id = @c AND si_id = @s AND commercial_response = 'REJECTED'",
            r => r.GetString(0), cancellationToken, ("c", context.CompanyId), ("s", siId)).ConfigureAwait(false)).SingleOrDefault();
        if (rejected is not null)
        {
            throw new DomainException(ProcurementErrors.SupplierDocumentRejected, $"The e-CF {rejected} was rejected before the DGII; this invoice cannot be posted.");
        }
    }

    /// <summary>E-OCR1-02-4: the posted invoice's e-CF, received and not yet answered, is accepted on behalf of <paramref name="user"/>.</summary>
    public static async Task<bool> AcceptIfPostedAsync(CommandContext context, Guid documentId, Guid user, string commandType, CancellationToken cancellationToken)
    {
        var row = await SupplierDocumentStore.LockAsync(context, documentId, null, cancellationToken).ConfigureAwait(false);
        if (row.Response != ReceivedStatuses.NotDeclared || row.ProviderId is null || row.ReceivedStatus != ReceivedStatuses.Received
            || !await InvoicePostedAsync(context, row.SiId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await SupplierDocumentStore.ChangeAsync(
            context, row, commandType, "SupplierDocumentAnswered", new { supplierDocumentId = row.Id, commercialResponse = ReceivedStatuses.Accepted, source = "POSTED_INVOICE" },
            "commercial_response = 'ACCEPTED', responded_by = @u, responded_at = @now", cancellationToken, parameters: [("u", user), ("now", context.Clock.UtcNow)]).ConfigureAwait(false);
        return true;
    }

    public static async Task AcceptOnPostAsync(CommandContext context, Guid siId, string commandType, CancellationToken cancellationToken)
    {
        var document = await DocumentOfAsync(context, siId, cancellationToken).ConfigureAwait(false);
        if (document is { } id)
        {
            var user = await PurchaseOrderStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
            await AcceptIfPostedAsync(context, id, user, commandType, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>E-OCR1-02-9: the document of a voided or reversed invoice waits to be registered again.</summary>
    public static async Task ReleaseAsync(CommandContext context, Guid siId, string commandType, string reason, CancellationToken cancellationToken)
    {
        if (await DocumentOfAsync(context, siId, cancellationToken).ConfigureAwait(false) is not { } id)
        {
            return;
        }

        var row = await SupplierDocumentStore.LockAsync(context, id, null, cancellationToken).ConfigureAwait(false);
        await SupplierDocumentStore.ChangeAsync(
            context, row, commandType, "SupplierDocumentReleased", new { supplierDocumentId = row.Id, supplierInvoiceId = siId, reason }, "status = 'CAPTURED', si_id = NULL",
            cancellationToken, SupplierDocumentStatus.Captured, reason).ConfigureAwait(false);
    }

    private static async Task<Guid?> DocumentOfAsync(CommandContext context, Guid siId, CancellationToken cancellationToken)
        => (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT supplier_document_id FROM pur.supplier_document WHERE company_id = @c AND si_id = @s", r => (Guid?)r.GetGuid(0),
            cancellationToken, ("c", context.CompanyId), ("s", siId)).ConfigureAwait(false)).SingleOrDefault();
}
