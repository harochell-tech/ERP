using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Procurement.PurchaseOrders;
using Rochell.Tax.Ecf;

namespace Rochell.Procurement.SupplierDocuments;

// OCR1-03 (E-OCR-4, E-OCR1-01-1/4/5/6, E-OCR1-03-4/5/7): what a person does in the inbox — captures a printed e-CF by its QR, discards
// a document, and registers the invoice of one (the link is made in the same command that registers it).

/// <summary>E-OCR-4, E-OCR1-03-7: the DGII stamp link read from a printed e-CF's QR (camera, photo or pasted).</summary>
public sealed record CaptureSupplierDocumentFromQr(Guid CompanyId, Guid SessionId, string IdempotencyKey, string QrUrl) : ICommand;

/// <summary>E-OCR1-03-5: a document that does not go (a duplicate, not ours), with its reason.</summary>
public sealed record DiscardSupplierDocument(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SupplierDocumentId, long ExpectedVersion, string Reason) : ICommand;

[RequiresPermission("supplier_document:capture")]
public sealed class CaptureSupplierDocumentFromQrHandler : ICommandHandler<CaptureSupplierDocumentFromQr>
{
    public string CommandType => "Procurement.CaptureSupplierDocumentFromQr";

    public async Task<string> HandleAsync(CaptureSupplierDocumentFromQr command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var stamp = EcfStampUrl.Parse(command.QrUrl)
            ?? throw new DomainException(ProcurementErrors.SupplierDocumentQrInvalid, "This is not the QR of an e-CF: its link is not the DGII's stamp page.");
        var companyRnc = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT rnc FROM md.company WHERE company_id = @c", r => r.GetString(0), cancellationToken, ("c", context.CompanyId))
            .ConfigureAwait(false)).Single();
        if (stamp.BuyerRnc != companyRnc)
        {
            throw new DomainException(ProcurementErrors.SupplierDocumentNotOurs, "This e-CF was not issued to the company (the buyer's RNC is another, or it has none).");
        }

        var user = await PurchaseOrderStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var live = await SupplierDocumentStore.LockAsync(context, "issuer_rnc = @i AND fiscal_number = @n AND status <> 'DISCARDED'", cancellationToken, ("i", stamp.IssuerRnc), ("n", stamp.Encf))
            .ConfigureAwait(false);
        if (live is not null)
        {
            // E-OCR1-01-5: the QR joins the document already captured; what the AI read of the header gives way to it.
            var version = await SupplierDocumentStore.ChangeAsync(
                context,
                live,
                CommandType,
                "SupplierDocumentQrScanned",
                new { supplierDocumentId = live.Id, fiscalNumber = stamp.Encf },
                live.Status == SupplierDocumentStatus.Captured
                    ? """
                      qr_url = @url, qr_total_amount = @total, qr_scanned_at = @now,
                      doc_date = CASE WHEN doc_date IS NULL OR 'doc_date' = ANY (ai_fields) THEN coalesce(@date, doc_date) ELSE doc_date END,
                      total_amount = CASE WHEN total_amount IS NULL OR 'total_amount' = ANY (ai_fields) THEN coalesce(@total, total_amount) ELSE total_amount END,
                      buyer_rnc = coalesce(buyer_rnc, @buyer), security_code = coalesce(security_code, @code), signature_at = coalesce(signature_at, @signed),
                      ai_fields = array_remove(array_remove(array_remove(ai_fields, 'doc_date'), 'total_amount'), 'buyer_rnc')
                      """
                    : "qr_url = @url, qr_total_amount = @total, qr_scanned_at = @now",
                cancellationToken,
                parameters: [.. StampParameters(stamp), ("now", context.Clock.UtcNow)]).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { supplierDocumentId = live.Id, created = false, status = live.Status, version });
        }

        // E-OCR1-01-5: a fiscal number already on an invoice is not captured again.
        var invoiced = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT si.si_id FROM pur.supplier_invoice si JOIN md.party p ON p.party_id = si.party_id
            WHERE si.company_id = @c AND p.rnc = @i AND si.supplier_fiscal_number = @n AND si.document_status NOT IN ('VOIDED', 'REVERSED')
            """,
            r => r.GetGuid(0),
            cancellationToken,
            ("c", context.CompanyId),
            ("i", stamp.IssuerRnc),
            ("n", stamp.Encf)).ConfigureAwait(false)).FirstOrDefault();
        if (invoiced != Guid.Empty)
        {
            throw new DomainException(ProcurementErrors.SupplierDocumentAlreadyInvoiced, $"The e-CF {stamp.Encf} is already registered as a supplier invoice ({invoiced}).");
        }

        var id = context.ResultRef;
        var party = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT party_id FROM md.party WHERE company_id = @c AND rnc = @r AND is_supplier AND status = 'ACTIVE'", r => (Guid?)r.GetGuid(0),
            cancellationToken, ("c", context.CompanyId), ("r", stamp.IssuerRnc)).ConfigureAwait(false)).SingleOrDefault();
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "SupplierDocumentCaptured", 1, SupplierDocumentStore.Aggregate, id, 1,
                JsonSerializer.Serialize(new { supplierDocumentId = id, source = "QR", issuerRnc = stamp.IssuerRnc, fiscalNumber = stamp.Encf }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(SupplierDocumentStore.Aggregate, id, "DOCUMENT", null, SupplierDocumentStatus.Captured, CommandType, eventId, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO pur.supplier_document (supplier_document_id, company_id, issuer_rnc, issuer_name, buyer_rnc, fiscal_number, doc_date, total_amount, security_code, signature_at, party_id,
              status, qr_url, qr_total_amount, qr_scanned_at, created_by, created_at, version)
            VALUES (@id, @c, @i, (SELECT legal_name FROM md.rnc_registry WHERE rnc = @i), @buyer, @n, @date, @total, @code, @signed, @party, 'CAPTURED', @url, @total, @now, @u, @now, 1)
            """,
            cancellationToken,
            [.. StampParameters(stamp), ("id", id), ("c", context.CompanyId), ("i", stamp.IssuerRnc), ("n", stamp.Encf), ("party", party), ("u", user), ("now", context.Clock.UtcNow)])
            .ConfigureAwait(false);
        return JsonSerializer.Serialize(new { supplierDocumentId = id, created = true, status = SupplierDocumentStatus.Captured, version = 1 });
    }

    private static (string Name, object? Value)[] StampParameters(EcfStamp stamp)
        =>
        [
            ("url", stamp.Url),
            ("buyer", stamp.BuyerRnc),
            ("date", stamp.IssueDate),
            ("total", stamp.TotalAmount),
            ("code", stamp.SecurityCode),
            ("signed", stamp.SignedAtUtc),
        ];
}

[RequiresPermission("supplier_document:capture")]
public sealed class DiscardSupplierDocumentHandler : ICommandHandler<DiscardSupplierDocument>
{
    public string CommandType => "Procurement.DiscardSupplierDocument";

    public async Task<string> HandleAsync(DiscardSupplierDocument command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = PurchaseOrderStore.RequireReason(command.Reason);
        var row = await SupplierDocumentStore.LockAsync(context, command.SupplierDocumentId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != SupplierDocumentStatus.Captured)
        {
            throw new DomainException(ProcurementErrors.InvalidState, "Only a document waiting to be registered can be discarded; void or reverse its invoice first.");
        }

        var version = await SupplierDocumentStore.ChangeAsync(
            context, row, CommandType, "SupplierDocumentDiscarded", new { supplierDocumentId = row.Id, reason }, "status = 'DISCARDED', discard_reason = @reason", cancellationToken,
            SupplierDocumentStatus.Discarded, reason, ("reason", reason)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { supplierDocumentId = row.Id, status = SupplierDocumentStatus.Discarded, version });
    }
}

/// <summary>E-OCR1-03-4: the document an invoice is registered from — checked, then linked in the same command.</summary>
internal static class SupplierDocumentRegistration
{
    /// <summary>Refuses a document that cannot become this invoice: not waiting, another supplier or number, not received, a note.</summary>
    public static async Task<SupplierDocumentRow?> CheckAsync(CommandContext context, Guid? documentId, Guid partyId, string fiscalNumber, CancellationToken cancellationToken)
    {
        if (documentId is not { } id)
        {
            return null;
        }

        var row = await SupplierDocumentStore.LockAsync(context, id, null, cancellationToken).ConfigureAwait(false);
        if (row.Status != SupplierDocumentStatus.Captured)
        {
            throw new DomainException(ProcurementErrors.SupplierDocumentNotRegistrable, "The document is not waiting to be registered.");
        }

        if (!SupplierDocumentRules.Registrable(row.FiscalNumber) || row.ReceivedStatus == ReceivedStatuses.NotReceived || row.Response == ReceivedStatuses.Rejected)
        {
            throw new DomainException(
                ProcurementErrors.SupplierDocumentNotRegistrable, "This document cannot become an invoice: it is a credit or debit note, Alanube did not receive it, or it was rejected.");
        }

        var rnc = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT rnc FROM md.party WHERE company_id = @c AND party_id = @p", r => r.NullableString(0), cancellationToken,
            ("c", context.CompanyId), ("p", partyId)).ConfigureAwait(false)).SingleOrDefault();
        return rnc == row.IssuerRnc && fiscalNumber == row.FiscalNumber
            ? row
            : throw new DomainException(ProcurementErrors.SupplierDocumentMismatch, $"The supplier and the NCF must be the document's: RNC {row.IssuerRnc}, {row.FiscalNumber}.");
    }

    public static Task LinkAsync(CommandContext context, SupplierDocumentRow? row, Guid siId, string commandType, CancellationToken cancellationToken)
        => row is null
            ? Task.CompletedTask
            : SupplierDocumentStore.ChangeAsync(
                context, row, commandType, "SupplierDocumentRegistered", new { supplierDocumentId = row.Id, supplierInvoiceId = siId }, "status = 'REGISTERED', si_id = @si",
                cancellationToken, SupplierDocumentStatus.Registered, null, ("si", siId));
}

/// <summary>E-OCR1-01-3: credit (B04 / E34) and debit notes (B03 / E33) of suppliers have no registration in Core yet.</summary>
public static class SupplierDocumentRules
{
    public static string TypeOf(string fiscalNumber) => fiscalNumber is { Length: >= 3 } ? fiscalNumber.Substring(1, 2) : string.Empty;

    public static bool Registrable(string fiscalNumber) => TypeOf(fiscalNumber) is not ("33" or "34" or "03" or "04");
}
