using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rochell.Finance.Policies;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Files;
using Rochell.Platform.Time;
using Rochell.Procurement.PurchaseOrders;
using Rochell.Tax.Ecf;

namespace Rochell.Procurement.SupplierDocuments;

// OCR1-04 (E-OCR-1/5/6/7, E-OCR1-01-10, E-OCR1-04-1…4): a photo, scan or PDF of a supplier invoice read by AI into a captured document.
// Only the file goes to the reader; what it read is marked «leído por IA» and checked when shown; the same file is never read twice;
// the month's readings are limited by PURCHASING ocr_monthly_readings.

/// <summary>What the AI read of an invoice; any field may be missing. Amounts are decimals (never floating point).</summary>
public sealed record InvoiceReading(
    string? IssuerRnc, string? IssuerName, string? BuyerRnc, string? FiscalNumber, DateOnly? Date, decimal? Total, decimal? Itbis, IReadOnlyList<ReceivedEcfLine> Lines);

/// <summary>The reader's answer: a reading, or the file did not show an invoice (unreadable), or the reader failed (no answer).</summary>
public sealed record ReaderOutcome(bool Answered, InvoiceReading? Reading, string Model, int? InputTokens, int? OutputTokens, string? Message);

/// <summary>E-OCR-5, E-OCR1-01-10: the service that reads an invoice from its image (Anthropic's API on the server, simulated in tests).</summary>
public interface ISupplierDocumentReader
{
    Task<ReaderOutcome> ReadAsync(byte[] content, string contentType, CancellationToken cancellationToken);
}

/// <summary>OCR1-04: the instructions and the shape the reader is asked for, and how its answer becomes an <see cref="InvoiceReading"/>.</summary>
public static partial class InvoiceReadingFormat
{
    public const string Instructions =
        "Lee esta factura de un proveedor de la República Dominicana y llama a la herramienta record_invoice con lo que veas impreso. "
        + "RNC o cédula sin guiones; NCF o e-NCF tal como está (B0100000001, E310000000001); fecha en AAAA-MM-DD; montos como texto con punto decimal y "
        + "sin separador de miles (\"1180.00\"); una línea por artículo o servicio con su cantidad, precio unitario y monto sin ITBIS. "
        + "Deja vacío lo que no se lea con claridad: no adivines.";

    /// <summary>The JSON schema of the record_invoice tool.</summary>
    public static JsonObject Schema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["issuer_rnc"] = Text("RNC o cédula del emisor (proveedor)"),
                ["issuer_name"] = Text("Razón social del emisor"),
                ["buyer_rnc"] = Text("RNC del comprador"),
                ["ncf"] = Text("NCF o e-NCF de la factura"),
                ["date"] = Text("Fecha de emisión, AAAA-MM-DD"),
                ["total"] = Text("Total a pagar"),
                ["itbis"] = Text("ITBIS total"),
                ["lines"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["description"] = Text("Descripción"),
                            ["quantity"] = Text("Cantidad"),
                            ["unit_price"] = Text("Precio unitario sin ITBIS"),
                            ["amount"] = Text("Monto sin ITBIS"),
                        },
                        ["required"] = new JsonArray("description"),
                    },
                },
            },
        };

    private static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };

    [GeneratedRegex("^([0-9]{9}|[0-9]{11})$", RegexOptions.None, 1000)]
    private static partial Regex Rnc();

    [GeneratedRegex("^(B[0-9]{10}|E[0-9]{12})$", RegexOptions.None, 1000)]
    private static partial Regex Ncf();

    /// <summary>The tool input as an <see cref="InvoiceReading"/>: RNC and NCF only in their formats, amounts only as non-negative decimals.</summary>
    public static InvoiceReading Parse(JsonObject input)
    {
        ArgumentNullException.ThrowIfNull(input);
        string? S(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        decimal? D(JsonNode? node)
            => S(node)?.Replace(",", string.Empty, StringComparison.Ordinal) is { } text
               && decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && value >= 0m
                ? value
                : null;
        string? Digits(JsonNode? node) => S(node) is { } t ? new string(t.Where(char.IsDigit).ToArray()) : null;

        var rnc = Digits(input["issuer_rnc"]);
        var buyer = Digits(input["buyer_rnc"]);
        var ncf = S(input["ncf"])?.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        var lines = new List<ReceivedEcfLine>();
        foreach (var line in (input["lines"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var description = S(line["description"]);
            var quantity = D(line["quantity"]) ?? 1m;
            var amount = D(line["amount"]);
            var price = D(line["unit_price"]) ?? (amount is { } a && quantity > 0m ? decimal.Round(a / quantity, 4) : null);
            if (description is null || quantity <= 0m || price is null || amount is null)
            {
                continue;
            }

            lines.Add(new ReceivedEcfLine(lines.Count + 1, null, description.Length > 500 ? description[..500] : description, quantity, null, price.Value, amount.Value, null));
        }

        return new InvoiceReading(
            rnc is not null && Rnc().IsMatch(rnc) ? rnc : null,
            S(input["issuer_name"]) is { } name ? (name.Length > 250 ? name[..250] : name) : null,
            buyer is not null && Rnc().IsMatch(buyer) ? buyer : null,
            ncf is not null && Ncf().IsMatch(ncf) ? ncf : null,
            DateOnly.TryParseExact(S(input["date"]), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null,
            D(input["total"]),
            D(input["itbis"]),
            lines);
    }

    public static JsonObject ToJson(InvoiceReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        static string? M(decimal? d) => d?.ToString(CultureInfo.InvariantCulture);
        return new JsonObject
        {
            ["issuer_rnc"] = reading.IssuerRnc,
            ["issuer_name"] = reading.IssuerName,
            ["buyer_rnc"] = reading.BuyerRnc,
            ["ncf"] = reading.FiscalNumber,
            ["date"] = reading.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["total"] = M(reading.Total),
            ["itbis"] = M(reading.Itbis),
            ["lines"] = new JsonArray([.. reading.Lines.Select(l => (JsonNode)new JsonObject
            {
                ["description"] = l.Description,
                ["quantity"] = M(l.Quantity),
                ["unit_price"] = M(l.UnitPrice),
                ["amount"] = M(l.Amount),
            })]),
        };
    }
}

/// <summary>
/// The simulated reader of the tests and the local stack: <see cref="Next"/> when set, else a cement invoice of «Agregados del Este»
/// whose NCF comes from the file's fingerprint (so every file is another invoice).
/// </summary>
public sealed class SimulatedSupplierDocumentReader : ISupplierDocumentReader
{
    public const string ModelName = "simulated";

    public Queue<ReaderOutcome> Next { get; } = new();

    public int Calls { get; private set; }

    public Task<ReaderOutcome> ReadAsync(byte[] content, string contentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        Calls++;
        if (Next.TryDequeue(out var queued))
        {
            return Task.FromResult(queued);
        }

        var digits = string.Concat(SHA256.HashData(content).Take(5).Select(b => (b % 100).ToString("D2", CultureInfo.InvariantCulture)));
        static decimal D(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);
        var reading = new InvoiceReading(
            "101000011", "Agregados del Este, S.R.L.", null, "B01" + digits[..8], null, D("2360.00"), D("360.00"),
            [new ReceivedEcfLine(1, null, "Arena lavada", D("2"), null, D("1000.00"), D("2000.00"), null)]);
        return Task.FromResult(new ReaderOutcome(true, reading, ModelName, 1000, 200, null));
    }
}

/// <summary>
/// E-OCR1-04-1/4: captures a supplier document from a photo, scan or PDF (base64, ≤ 10 MB; a PDF of at most 3 pages). The RNC and the NCF
/// may be typed when the AI could not read them (E-OCR1-04-2): the file is then not read again.
/// </summary>
public sealed record CaptureSupplierDocumentFromImage(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, string ContentBase64, string? IssuerRnc = null, string? FiscalNumber = null) : ICommand;

[RequiresPermission("supplier_document:capture")]
public sealed class CaptureSupplierDocumentFromImageHandler(ISupplierDocumentReader? reader = null, IEvidenceStore? store = null) : ICommandHandler<CaptureSupplierDocumentFromImage>
{
    public const int MaxBytes = 10 * 1024 * 1024; // type-limit: E-OCR1-01-7
    public const int MaxPdfPages = 3; // type-limit: E-OCR1-04-1
    public const string MonthlyReadings = "ocr_monthly_readings";

    public string CommandType => "Procurement.CaptureSupplierDocumentFromImage";

    public async Task<string> HandleAsync(CaptureSupplierDocumentFromImage command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        byte[] content;
        try
        {
            content = Convert.FromBase64String(command.ContentBase64 ?? string.Empty);
        }
        catch (FormatException)
        {
            throw new DomainException(ProcurementErrors.SupplierDocumentFileInvalid, "The file is not valid base64.");
        }

        var contentType = TypeOf(content);
        if (content.Length is 0 or > MaxBytes || contentType is null)
        {
            throw new DomainException(ProcurementErrors.SupplierDocumentFileInvalid, "Send a JPG or PNG photo or a PDF of up to 10 MB.");
        }

        if (contentType == "application/pdf" && Pages(content) > MaxPdfPages)
        {
            throw new DomainException(ProcurementErrors.SupplierDocumentFileInvalid, "A PDF of at most 3 pages: send only the invoice.");
        }

        if (store is null)
        {
            throw new DomainException(ProcurementErrors.SupplierDocumentStoreMissing, "The evidence store is not configured on this server.");
        }

        var sha = SHA256.HashData(content);
        var user = await PurchaseOrderStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);

        // E-OCR1-04-2: the same file is never read twice.
        var previous = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT result::text FROM pur.supplier_document_reading WHERE company_id = @c AND file_sha256 = @sha ORDER BY read_at DESC LIMIT 1",
            r => r.IsDBNull(0) ? string.Empty : r.GetString(0),
            cancellationToken,
            ("c", context.CompanyId),
            ("sha", sha)).ConfigureAwait(false)).FirstOrDefault();
        InvoiceReading? reading;
        if (previous is not null)
        {
            reading = previous.Length == 0 ? null : InvoiceReadingFormat.Parse((JsonObject)JsonNode.Parse(previous)!);
        }
        else
        {
            reading = await ReadAsync(context, content, contentType, sha, user, cancellationToken).ConfigureAwait(false);
        }

        // What the person typed wins over what the AI read; those fields are not «leído por IA».
        var typedRnc = Clean(command.IssuerRnc, digitsOnly: true);
        var typedNcf = Clean(command.FiscalNumber, digitsOnly: false)?.ToUpperInvariant();
        var rnc = typedRnc ?? reading?.IssuerRnc;
        var ncf = typedNcf ?? reading?.FiscalNumber;
        if (rnc is null || ncf is null || !SupplierInvoiceStoreFormats.Rnc().IsMatch(rnc) || !SupplierInvoiceStoreFormats.Ncf().IsMatch(ncf))
        {
            return JsonSerializer.Serialize(new
            {
                supplierDocumentId = (Guid?)null,
                needsInput = new[] { rnc is null || !SupplierInvoiceStoreFormats.Rnc().IsMatch(rnc) ? "issuerRnc" : null, ncf is null || !SupplierInvoiceStoreFormats.Ncf().IsMatch(ncf) ? "fiscalNumber" : null }
                    .OfType<string>().ToArray(),
                read = reading is null ? null : InvoiceReadingFormat.ToJson(reading),
            });
        }

        var companyRnc = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT rnc FROM md.company WHERE company_id = @c", r => r.GetString(0), cancellationToken, ("c", context.CompanyId))
            .ConfigureAwait(false)).Single();
        if (reading?.BuyerRnc is { } buyer && buyer != companyRnc)
        {
            throw new DomainException(ProcurementErrors.SupplierDocumentNotOurs, "This invoice was not issued to the company (the buyer's RNC is another).");
        }

        var aiFields = new List<string>();
        if (typedRnc is null)
        {
            aiFields.Add("issuer_rnc");
        }

        if (typedNcf is null)
        {
            aiFields.Add("fiscal_number");
        }

        foreach (var (field, present) in new[]
        {
            ("issuer_name", reading?.IssuerName is not null), ("buyer_rnc", reading?.BuyerRnc is not null), ("doc_date", reading?.Date is not null),
            ("total_amount", reading?.Total is not null), ("itbis_amount", reading?.Itbis is not null), ("lines", reading is { Lines.Count: > 0 }),
        })
        {
            if (present)
            {
                aiFields.Add(field);
            }
        }

        var extension = contentType switch { "image/png" => "png", "application/pdf" => "pdf", _ => "jpg" };
        var key = $"supplier-documents/{context.CompanyId:N}/{Convert.ToHexStringLower(sha)}.{extension}";
        await store.PutAsync(key, content, contentType, cancellationToken).ConfigureAwait(false);

        var live = await SupplierDocumentStore.LockAsync(context, "issuer_rnc = @i AND fiscal_number = @n AND status <> 'DISCARDED'", cancellationToken, ("i", rnc), ("n", ncf))
            .ConfigureAwait(false);
        Guid id;
        var created = live is null;
        if (live is not null)
        {
            id = live.Id;
            var hasXml = (await Reading.ListAsync(
                context.Connection, context.Transaction, "SELECT 1 FROM pur.supplier_document_file WHERE supplier_document_id = @d AND kind = 'XML'", r => r.GetInt32(0), cancellationToken,
                ("d", id)).ConfigureAwait(false)).Count > 0;
            if (live.Status == SupplierDocumentStatus.Captured && !hasXml && reading is not null)
            {
                // The photo completes what is missing; the XML or the QR, when there, stay as they are.
                await SupplierDocumentStore.ChangeAsync(
                    context,
                    live,
                    CommandType,
                    "SupplierDocumentImageRead",
                    new { supplierDocumentId = id, fiscalNumber = ncf },
                    """
                    issuer_name = coalesce(issuer_name, @name), doc_date = coalesce(doc_date, @date), total_amount = coalesce(total_amount, @total),
                    itbis_amount = coalesce(itbis_amount, @itbis),
                    ai_fields = ARRAY(SELECT DISTINCT unnest(ai_fields || CAST(@fields AS text[])) ORDER BY 1)
                    """,
                    cancellationToken,
                    parameters: [("name", reading.IssuerName), ("date", reading.Date), ("total", reading.Total), ("itbis", reading.Itbis),
                        ("fields", aiFields.Where(f => f is "issuer_name" or "doc_date" or "total_amount" or "itbis_amount" or "lines").ToArray())]).ConfigureAwait(false);
            }
        }
        else
        {
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
                ("i", rnc),
                ("n", ncf)).ConfigureAwait(false)).FirstOrDefault();
            if (invoiced != Guid.Empty)
            {
                throw new DomainException(ProcurementErrors.SupplierDocumentAlreadyInvoiced, $"The invoice {ncf} is already registered as a supplier invoice ({invoiced}).");
            }

            id = context.ResultRef;
            var party = (await Reading.ListAsync(
                context.Connection, context.Transaction, "SELECT party_id FROM md.party WHERE company_id = @c AND rnc = @r AND is_supplier AND status = 'ACTIVE'", r => (Guid?)r.GetGuid(0),
                cancellationToken, ("c", context.CompanyId), ("r", rnc)).ConfigureAwait(false)).SingleOrDefault();
            var eventId = await context.AppendEventAsync(
                new EventDraft(
                    "SupplierDocumentCaptured", 1, SupplierDocumentStore.Aggregate, id, 1,
                    JsonSerializer.Serialize(new { supplierDocumentId = id, source = "AI", issuerRnc = rnc, fiscalNumber = ncf }), Publish: true),
                cancellationToken).ConfigureAwait(false);
            await context.AppendStateAsync(SupplierDocumentStore.Aggregate, id, "DOCUMENT", null, SupplierDocumentStatus.Captured, CommandType, eventId, cancellationToken).ConfigureAwait(false);
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO pur.supplier_document (supplier_document_id, company_id, issuer_rnc, issuer_name, buyer_rnc, fiscal_number, doc_date, total_amount, itbis_amount, party_id,
                  status, ai_fields, created_by, created_at, version)
                VALUES (@id, @c, @i, coalesce(@name, (SELECT legal_name FROM md.rnc_registry WHERE rnc = @i)), @buyer, @n, @date, @total, @itbis, @party, 'CAPTURED', @fields, @u, @now, 1)
                """,
                cancellationToken,
                ("id", id),
                ("c", context.CompanyId),
                ("i", rnc),
                ("name", reading?.IssuerName),
                ("buyer", reading?.BuyerRnc),
                ("n", ncf),
                ("date", reading?.Date),
                ("total", reading?.Total),
                ("itbis", reading?.Itbis),
                ("party", party),
                ("fields", aiFields.ToArray()),
                ("u", user),
                ("now", context.Clock.UtcNow)).ConfigureAwait(false);
        }

        if (reading is { Lines.Count: > 0 })
        {
            await SupplierDocumentStore.AddLinesAsync(context, id, "AI", reading.Lines, cancellationToken).ConfigureAwait(false);
        }

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO pur.supplier_document_file (file_id, company_id, supplier_document_id, kind, content_type, content, storage_key, size_bytes, sha256, added_by, added_at)
            SELECT @id, @c, @d, @kind, @type, NULL, @key, @size, @sha, @u, @now
            WHERE NOT EXISTS (SELECT 1 FROM pur.supplier_document_file WHERE supplier_document_id = @d AND sha256 = @sha)
            """,
            cancellationToken,
            ("id", context.Ids.NewId()),
            ("c", context.CompanyId),
            ("d", id),
            ("kind", contentType == "application/pdf" ? "PDF" : "IMAGE"),
            ("type", contentType),
            ("key", key),
            ("size", content.Length),
            ("sha", sha),
            ("u", user),
            ("now", context.Clock.UtcNow)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { supplierDocumentId = id, created, needsInput = Array.Empty<string>() });
    }

    /// <summary>One reading of the AI, within the month's limit; recorded whatever it answered (E-OCR1-04-2/3).</summary>
    private async Task<InvoiceReading?> ReadAsync(CommandContext context, byte[] content, string contentType, byte[] sha, Guid user, CancellationToken cancellationToken)
    {
        if (reader is null)
        {
            throw new DomainException(ProcurementErrors.SupplierDocumentReaderOff, "Reading invoices by AI is switched off on this server; capture it by its QR or by hand.");
        }

        var today = BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);
        var policy = await PolicyResolver.ResolveAsync(context, PolicyCodes.Purchasing, today, cancellationToken).ConfigureAwait(false);
        var limit = policy.Integer(MonthlyReadings);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var used = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT count(*) FROM pur.supplier_document_reading WHERE company_id = @c AND (read_at AT TIME ZONE 'America/Santo_Domingo')::date >= @from",
            r => r.GetInt64(0),
            cancellationToken,
            ("c", context.CompanyId),
            ("from", monthStart)).ConfigureAwait(false)).Single();
        if (used >= limit)
        {
            throw new DomainException(
                ProcurementErrors.SupplierDocumentReadingLimit, $"The month's {limit} readings by AI are used; capture the invoice by its QR or by hand (E-OCR1-04-3).");
        }

        var outcome = await reader.ReadAsync(content, contentType, cancellationToken).ConfigureAwait(false);
        if (!outcome.Answered)
        {
            // No answer at all (network, the service down): nothing to record or charge; the person may try again.
            throw new DomainException(ProcurementErrors.SupplierDocumentReadFailed, $"The AI service did not answer: {outcome.Message}");
        }

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO pur.supplier_document_reading (reading_id, company_id, file_sha256, content_type, size_bytes, outcome, result, message, model, input_tokens, output_tokens,
              read_by, read_at)
            VALUES (@id, @c, @sha, @type, @size, @outcome, CAST(@result AS jsonb), @message, @model, @in, @out, @u, @now)
            """,
            cancellationToken,
            ("id", context.Ids.NewId()),
            ("c", context.CompanyId),
            ("sha", sha),
            ("type", contentType),
            ("size", content.Length),
            ("outcome", outcome.Reading is null ? "UNREADABLE" : "READ"),
            ("result", outcome.Reading is null ? null : InvoiceReadingFormat.ToJson(outcome.Reading).ToJsonString()),
            ("message", outcome.Message is { Length: > 2000 } m ? m[..2000] : outcome.Message),
            ("model", outcome.Model),
            ("in", outcome.InputTokens),
            ("out", outcome.OutputTokens),
            ("u", user),
            ("now", context.Clock.UtcNow)).ConfigureAwait(false);
        return outcome.Reading;
    }

    private static string? TypeOf(byte[] content)
        => EvidenceImages.ContentType(content) ?? (content.Length > 5 && Encoding.ASCII.GetString(content, 0, 5) == "%PDF-" ? "application/pdf" : null);

    /// <summary>A PDF's page objects («/Type /Page», not «/Pages»), counted on its text.</summary>
    private static int Pages(byte[] content)
        => SupplierInvoiceStoreFormats.PdfPage().Count(Encoding.Latin1.GetString(content));

    private static string? Clean(string? value, bool digitsOnly)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : digitsOnly ? new string(text.Where(char.IsDigit).ToArray()) : text.Replace(" ", string.Empty, StringComparison.Ordinal);
    }
}

internal static partial class SupplierInvoiceStoreFormats
{
    [GeneratedRegex("^([0-9]{9}|[0-9]{11})$", RegexOptions.None, 1000)]
    public static partial Regex Rnc();

    [GeneratedRegex("^(B[0-9]{10}|E[0-9]{12})$", RegexOptions.None, 1000)]
    public static partial Regex Ncf();

    [GeneratedRegex(@"/Type\s*/Page(?!s)", RegexOptions.None, 1000)]
    public static partial Regex PdfPage();
}
