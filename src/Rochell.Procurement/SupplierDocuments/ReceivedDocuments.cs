using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Time;
using Rochell.Procurement.PurchaseOrders;
using Rochell.Tax.Ecf;

namespace Rochell.Procurement.SupplierDocuments;

// OCR1-02 (E-OCR-2, E-OCR1-01-5/6/9, E-OCR1-02-1/2/5/6/8): the hourly reading of the e-CF suppliers sent through Alanube. Each new one
// becomes a captured supplier document with its XML and lines, joins the document already captured from its QR or photo, or links the
// invoice already registered with that e-NCF; the ones known are refreshed (reception status, a response given in Alanube's portal).

/// <summary>E-OCR1-02-1: one reading of Alanube's received documents, by the daily process (<c>ecf:process</c>).</summary>
public sealed record ImportReceivedDocuments(Guid CompanyId, Guid SessionId, string IdempotencyKey) : ICommand;

[RequiresPermission("ecf:process")]
public sealed partial class ImportReceivedDocumentsHandler(IEcfReception reception) : ICommandHandler<ImportReceivedDocuments>
{
    /// <summary>Documents per page asked of Alanube, and pages per listing.</summary>
    public const int PageSize = 100;

    public const int MaxPages = 20;

    /// <summary>New documents read (one call each) per reading; the next reading carries on (its result says <c>more</c>).</summary>
    public const int MaxReads = 50;

    /// <summary>E-OCR1-01-9: the first reading goes back this many days; the next ones start a day before the last good one.</summary>
    public const int FirstWindowDays = 30;

    /// <summary>E-OCR1-02-2: every received document, answered or not, and the ones Alanube did not receive.</summary>
    private static readonly (string Status, string Response)[] Listings =
    [
        (ReceivedStatuses.Received, ReceivedStatuses.NotDeclared), (ReceivedStatuses.Received, ReceivedStatuses.Accepted), (ReceivedStatuses.Received, ReceivedStatuses.Rejected),
        (ReceivedStatuses.NotReceived, ReceivedStatuses.NotDeclared),
    ];

    public string CommandType => "Procurement.ImportReceivedDocuments";

    [GeneratedRegex("^([0-9]{9}|[0-9]{11})$", RegexOptions.None, 1000)]
    private static partial Regex RncFormat();

    [GeneratedRegex("^E[0-9]{12}$", RegexOptions.None, 1000)]
    private static partial Regex EncfFormat();

    public async Task<string> HandleAsync(ImportReceivedDocuments command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var now = context.Clock.UtcNow;
        var today = BusinessCalendar.DefaultBusinessDate(now);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "INSERT INTO pur.received_document_sync (company_id, last_attempt_at) VALUES (@c, @now) ON CONFLICT (company_id) DO NOTHING",
            cancellationToken, ("c", context.CompanyId), ("now", now)).ConfigureAwait(false);
        var lastSuccess = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT last_success_at FROM pur.received_document_sync WHERE company_id = @c FOR UPDATE", r => r.NullableUtc(0), cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false)).Single();
        var start = lastSuccess is { } last ? BusinessCalendar.DefaultBusinessDate(last).AddDays(-1) : today.AddDays(-FirstWindowDays);
        var companyRnc = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT rnc FROM md.company WHERE company_id = @c", r => r.GetString(0), cancellationToken, ("c", context.CompanyId))
            .ConfigureAwait(false)).Single();
        var user = await PurchaseOrderStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);

        var listed = new Dictionary<string, ReceivedDocument>(StringComparer.Ordinal);
        var more = false;
        foreach (var (status, response) in Listings)
        {
            for (var page = 1; page <= MaxPages; page++)
            {
                var watch = Stopwatch.StartNew();
                var outcome = await reception.ListReceivedAsync(new ReceivedListRequest(status, response, start, today, page, PageSize), cancellationToken).ConfigureAwait(false);
                await EcfCallLog.RecordAsync(
                    context, reception.Mode, EcfCallLog.ReceivedList, outcome.HttpStatus, outcome.Ok ? "OK" : outcome.HttpStatus is null ? "TIMEOUT" : "ERROR", outcome.Code,
                    outcome.Ok ? $"{status}/{response} página {page}: {outcome.Items.Count}" : outcome.Message, watch, cancellationToken).ConfigureAwait(false);
                if (!outcome.Ok)
                {
                    return await FinishAsync(context, now, failure: $"Alanube no respondió la lista de recibidos: {outcome.Code} {outcome.Message}".Trim(), more: true, new Counts(), cancellationToken)
                        .ConfigureAwait(false);
                }

                foreach (var item in outcome.Items)
                {
                    listed.TryAdd(item.Id, item);
                }

                if (outcome.Items.Count < PageSize)
                {
                    break;
                }

                more |= page == MaxPages;
            }
        }

        var counts = new Counts { Listed = listed.Count };
        var reads = 0;
        foreach (var item in listed.Values.OrderBy(d => d.SignatureDate).ThenBy(d => d.Id, StringComparer.Ordinal))
        {
            var known = await SupplierDocumentStore.LockAsync(context, "provider_id = @p", cancellationToken, ("p", item.Id)).ConfigureAwait(false);
            if (known is not null)
            {
                counts.Refreshed += await RefreshAsync(context, known, item.Status, item.CommercialResponse, user, CommandType, cancellationToken).ConfigureAwait(false) ? 1 : 0;
                continue;
            }

            if (reads >= MaxReads)
            {
                more = true;
                continue;
            }

            reads++;
            var watch = Stopwatch.StartNew();
            var detail = await reception.GetReceivedAsync(item.Id, cancellationToken).ConfigureAwait(false);
            await EcfCallLog.RecordAsync(
                context, reception.Mode, EcfCallLog.ReceivedGet, detail.HttpStatus, detail.Kind switch { QueryKind.Found => "OK", QueryKind.NotFound => "REJECTED", _ => detail.HttpStatus is null ? "TIMEOUT" : "ERROR" },
                detail.Code, detail.Kind == QueryKind.Found ? item.Id : $"{item.Id}: {detail.Message}", watch, cancellationToken).ConfigureAwait(false);
            if (detail.Document is not { } document)
            {
                more |= detail.Kind == QueryKind.Transient;
                counts.Skipped++;
                continue;
            }

            var xml = ReceivedEcfXml.Content(document.Xml);
            if (xml is null && document.Xml is { } link && Uri.TryCreate(link.Trim(), UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps)
            {
                var download = Stopwatch.StartNew();
                xml = await reception.DownloadAsync(url.ToString(), cancellationToken).ConfigureAwait(false);
                await EcfCallLog.RecordAsync(context, reception.Mode, "DOWNLOAD", null, xml is null ? "ERROR" : "OK", "XML", item.Id, download, cancellationToken).ConfigureAwait(false);
            }

            var parsed = xml is null ? null : ReceivedEcfXml.Parse(xml);
            var issuer = parsed?.IssuerRnc ?? document.IssuerRnc ?? item.IssuerRnc;
            var number = parsed?.Encf ?? document.DocumentNumber ?? item.DocumentNumber;
            if (issuer is null || number is null || !RncFormat().IsMatch(issuer) || !EncfFormat().IsMatch(number))
            {
                counts.Skipped++;
                continue;
            }

            // E-OCR1-01-6: only what was sent to this company.
            var buyer = parsed?.BuyerRnc ?? document.BuyerRnc;
            if (buyer is not null && buyer != companyRnc)
            {
                counts.Refused++;
                continue;
            }

            await CaptureAsync(context, document with { Status = document.Status ?? item.Status, CommercialResponse = document.CommercialResponse ?? item.CommercialResponse },
                issuer, number, buyer, parsed, xml, user, counts, cancellationToken).ConfigureAwait(false);
        }

        return await FinishAsync(context, now, failure: null, more, counts, cancellationToken).ConfigureAwait(false);
    }

    private sealed class Counts
    {
        public int Listed { get; set; }

        public int Created { get; set; }

        public int Attached { get; set; }

        public int Linked { get; set; }

        public int Refreshed { get; set; }

        public int Refused { get; set; }

        public int Skipped { get; set; }
    }

    private async Task CaptureAsync(
        CommandContext context, ReceivedDocument document, string issuer, string number, string? buyer, ReceivedEcf? parsed, byte[]? xml, Guid user, Counts counts,
        CancellationToken cancellationToken)
    {
        var received = document.Status is ReceivedStatuses.Received or ReceivedStatuses.NotReceived ? document.Status : null;
        var live = await SupplierDocumentStore.LockAsync(context, "issuer_rnc = @i AND fiscal_number = @n AND status <> 'DISCARDED'", cancellationToken, ("i", issuer), ("n", number))
            .ConfigureAwait(false);
        Guid id;
        if (live is not null)
        {
            if (live.ProviderId is not null)
            {
                // Alanube holds this e-NCF under another id: the first one stays.
                counts.Skipped++;
                return;
            }

            // E-OCR1-02-8: the XML joins the document captured from its QR or photo; while it waits, the XML's header and lines replace what was read.
            id = live.Id;
            var header = live.Status == SupplierDocumentStatus.Captured && parsed is not null;
            await SupplierDocumentStore.ChangeAsync(
                context,
                live,
                CommandType,
                "SupplierDocumentXmlReceived",
                new { supplierDocumentId = id, providerId = document.Id, fiscalNumber = number },
                header
                    ? """
                      provider_id = @p, received_status = @rs, issuer_name = coalesce(@name, issuer_name), buyer_rnc = coalesce(@buyer, buyer_rnc), doc_date = coalesce(@date, doc_date),
                      total_amount = coalesce(@total, total_amount), itbis_amount = coalesce(@itbis, itbis_amount), security_code = coalesce(@code, security_code),
                      signature_at = coalesce(@signed, signature_at), ai_fields = '{}'
                      """
                    : "provider_id = @p, received_status = @rs",
                cancellationToken,
                parameters: [.. HeaderParameters(document, parsed, buyer), ("rs", received)]).ConfigureAwait(false);
            counts.Attached++;
        }
        else
        {
            id = context.Ids.NewId();
            var party = (await Reading.ListAsync(
                context.Connection, context.Transaction, "SELECT party_id FROM md.party WHERE company_id = @c AND rnc = @r AND is_supplier AND status = 'ACTIVE'", r => (Guid?)r.GetGuid(0),
                cancellationToken, ("c", context.CompanyId), ("r", issuer)).ConfigureAwait(false)).SingleOrDefault();
            var eventId = await context.AppendEventAsync(
                new EventDraft(
                    "SupplierDocumentCaptured", 1, SupplierDocumentStore.Aggregate, id, 1,
                    JsonSerializer.Serialize(new { supplierDocumentId = id, source = "ECF_RECEIVED", providerId = document.Id, issuerRnc = issuer, fiscalNumber = number }), Publish: true),
                cancellationToken).ConfigureAwait(false);
            await context.AppendStateAsync(SupplierDocumentStore.Aggregate, id, "DOCUMENT", null, SupplierDocumentStatus.Captured, CommandType, eventId, cancellationToken)
                .ConfigureAwait(false);
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO pur.supplier_document (supplier_document_id, company_id, issuer_rnc, issuer_name, buyer_rnc, fiscal_number, doc_date, total_amount, itbis_amount, security_code,
                  signature_at, party_id, status, provider_id, received_status, created_by, created_at, version)
                VALUES (@id, @c, @i, @name, @buyer, @n, @date, @total, @itbis, @code, @signed, @party, 'CAPTURED', @p, @rs, @u, @now, 1)
                """,
                cancellationToken,
                [.. HeaderParameters(document, parsed, buyer), ("id", id), ("c", context.CompanyId), ("i", issuer), ("n", number), ("party", party), ("rs", received), ("u", user),
                 ("now", context.Clock.UtcNow)]).ConfigureAwait(false);
            counts.Created++;
        }

        if (xml is not null)
        {
            await SupplierDocumentStore.AddXmlAsync(context, id, xml, user, cancellationToken).ConfigureAwait(false);
        }

        if (parsed is { Lines.Count: > 0 })
        {
            await SupplierDocumentStore.AddLinesAsync(context, id, "XML", parsed.Lines, cancellationToken).ConfigureAwait(false);
        }

        var row = await SupplierDocumentStore.LockAsync(context, id, null, cancellationToken).ConfigureAwait(false);
        if (await LinkInvoiceAsync(context, row, CommandType, cancellationToken).ConfigureAwait(false))
        {
            counts.Linked++;
            row = await SupplierDocumentStore.LockAsync(context, id, null, cancellationToken).ConfigureAwait(false);
        }

        await RefreshAsync(context, row, null, document.CommercialResponse, user, CommandType, cancellationToken).ConfigureAwait(false);
    }

    private static (string Name, object? Value)[] HeaderParameters(ReceivedDocument document, ReceivedEcf? parsed, string? buyer)
        =>
        [
            ("p", document.Id),
            ("name", parsed?.IssuerName),
            ("buyer", buyer is not null && RncFormat().IsMatch(buyer) ? buyer : null),
            ("date", parsed?.IssueDate),
            ("total", parsed?.TotalAmount ?? (decimal.TryParse(document.TotalAmount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var total) ? total : null)),
            ("itbis", parsed?.ItbisAmount),
            ("code", parsed?.SecurityCode),
            ("signed", parsed?.SignedAtUtc ?? document.SignatureDate?.UtcDateTime),
        ];

    /// <summary>E-OCR1-01-5: an invoice registered with this document's e-NCF (and no document of its own) is this document's. Whether it linked one.</summary>
    internal static async Task<bool> LinkInvoiceAsync(CommandContext context, SupplierDocumentRow row, string commandType, CancellationToken cancellationToken)
    {
        if (row.Status != SupplierDocumentStatus.Captured)
        {
            return false;
        }

        var invoice = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT si.si_id FROM pur.supplier_invoice si JOIN md.party p ON p.party_id = si.party_id
            WHERE si.company_id = @c AND p.rnc = @i AND si.supplier_fiscal_number = @n AND si.document_status NOT IN ('VOIDED', 'REVERSED')
              AND NOT EXISTS (SELECT 1 FROM pur.supplier_document d WHERE d.company_id = @c AND d.si_id = si.si_id)
            """,
            r => (Guid?)r.GetGuid(0),
            cancellationToken,
            ("c", context.CompanyId),
            ("i", row.IssuerRnc),
            ("n", row.FiscalNumber)).ConfigureAwait(false)).SingleOrDefault();
        if (invoice is not { } siId)
        {
            return false;
        }

        await SupplierDocumentStore.ChangeAsync(
            context, row, commandType, "SupplierDocumentRegistered", new { supplierDocumentId = row.Id, supplierInvoiceId = siId }, "status = 'REGISTERED', si_id = @si",
            cancellationToken, SupplierDocumentStatus.Registered, "Factura ya registrada con este e-NCF", ("si", siId)).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// A known document follows Alanube: the invoice registered since with its e-NCF, its reception status, and a commercial response
    /// given in Alanube's portal (E-OCR1-02-2); then the acceptance owed by a posted invoice (E-OCR1-02-4). Whether anything changed.
    /// </summary>
    internal static async Task<bool> RefreshAsync(
        CommandContext context, SupplierDocumentRow row, string? status, string? response, Guid user, string commandType, CancellationToken cancellationToken)
    {
        var changed = await LinkInvoiceAsync(context, row, commandType, cancellationToken).ConfigureAwait(false);
        if (changed)
        {
            row = await SupplierDocumentStore.LockAsync(context, row.Id, null, cancellationToken).ConfigureAwait(false);
        }

        if (status is ReceivedStatuses.Received or ReceivedStatuses.NotReceived && status != row.ReceivedStatus)
        {
            await SupplierDocumentStore.ChangeAsync(
                context, row, commandType, "SupplierDocumentReceptionChanged", new { supplierDocumentId = row.Id, receivedStatus = status }, "received_status = @rs", cancellationToken,
                parameters: ("rs", status)).ConfigureAwait(false);
            row = await SupplierDocumentStore.LockAsync(context, row.Id, null, cancellationToken).ConfigureAwait(false);
            changed = true;
        }

        if (row.Response == ReceivedStatuses.NotDeclared && response is ReceivedStatuses.Accepted or ReceivedStatuses.Rejected)
        {
            var reason = response == ReceivedStatuses.Rejected ? "Rechazado en el portal de Alanube" : null;
            await SupplierDocumentStore.ChangeAsync(
                context, row, commandType, "SupplierDocumentAnswered", new { supplierDocumentId = row.Id, commercialResponse = response, reason, source = "ALANUBE" },
                "commercial_response = @r, response_reason = @reason, responded_by = @u, responded_at = @now, response_sent_at = @now", cancellationToken,
                parameters: [("r", response), ("reason", reason), ("u", user), ("now", context.Clock.UtcNow)]).ConfigureAwait(false);
            return true;
        }

        return await SupplierDocumentLinks.AcceptIfPostedAsync(context, row.Id, user, commandType, cancellationToken).ConfigureAwait(false) || changed;
    }

    private static async Task<string> FinishAsync(CommandContext context, DateTime now, string? failure, bool more, Counts counts, CancellationToken cancellationToken)
    {
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            UPDATE pur.received_document_sync SET last_attempt_at = @now, last_error = CAST(@error AS text),
              last_success_at = CASE WHEN CAST(@error AS text) IS NULL AND NOT @more THEN @now ELSE last_success_at END
            WHERE company_id = @c
            """,
            cancellationToken,
            ("now", now),
            ("error", failure),
            ("more", more),
            ("c", context.CompanyId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            listed = counts.Listed,
            created = counts.Created,
            attached = counts.Attached,
            linked = counts.Linked,
            refreshed = counts.Refreshed,
            refused = counts.Refused,
            skipped = counts.Skipped,
            more,
            error = failure,
        });
    }
}
