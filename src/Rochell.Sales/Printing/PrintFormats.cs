using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;
using Rochell.Sales.Deliveries;
using Rochell.Sales.Proformas;
using Rochell.Sales.Queries;
using Rochell.Tax.Authorizations;

namespace Rochell.Sales.Printing;

// PRT-02 (E-PRT-3…10, E-PRT-02-1…7): Configuración › Formatos de impresión. The Director (print_format:manage) keeps one draft per
// document type, previews it with a real document or an example, and activates it after the mandatory content is checked; versions
// stay, and an earlier one can be restored into the draft. One logo per company.

/// <summary>E-PRT-02-1/2: saves the type's draft — the simple settings over the built-in template, or an advanced template and CSS.</summary>
public sealed record SavePrintFormatDraft(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, string DocumentType, PrintSettings Settings, string? Body = null, string? Css = null, string? Note = null) : ICommand;

/// <summary>E-PRT-6, E-PRT-02-6: the draft becomes ACTIVE (the previous ACTIVE is RETIRED) once its test document carries what is mandatory.</summary>
public sealed record ActivatePrintFormat(Guid CompanyId, Guid SessionId, string IdempotencyKey, string DocumentType, int Version) : ICommand;

/// <summary>E-PRT-6: an earlier version — or the built-in format, version 0 — copied into the type's draft.</summary>
public sealed record RestorePrintFormat(Guid CompanyId, Guid SessionId, string IdempotencyKey, string DocumentType, int Version) : ICommand;

/// <summary>E-PRT-8: the company's logo, PNG or JPEG of at most 1 MB (base64).</summary>
public sealed record SetCompanyLogo(Guid CompanyId, Guid SessionId, string IdempotencyKey, string ContentBase64) : ICommand;

internal static class PrintFormatStore
{
    public const string Aggregate = "PrintFormat";

    public static void EnsureType(string documentType)
    {
        if (!PrintDocumentTypes.All.Contains(documentType))
        {
            throw new DomainException(PrintErrors.SettingsInvalid, $"Unknown document type {documentType}.");
        }
    }

    /// <summary>Writes the type's draft: updates the DRAFT there is, or inserts one as the next version. Returns the version.</summary>
    public static async Task<int> WriteDraftAsync(CommandContext context, string documentType, string settings, string body, string css, string? note, CancellationToken cancellationToken)
    {
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "SELECT pg_advisory_xact_lock(hashtext('print_format:' || @c::text || ':' || @t))", cancellationToken,
            ("c", context.CompanyId), ("t", documentType)).ConfigureAwait(false);
        var by = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var draft = await SalesSql.ScalarAsync<int?>(
            context, "SELECT version FROM md.print_format WHERE company_id = @c AND document_type = @t AND status = 'DRAFT'", cancellationToken,
            ("c", context.CompanyId), ("t", documentType)).ConfigureAwait(false);
        int version;
        if (draft is { } v)
        {
            version = v;
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE md.print_format SET settings = @s::jsonb, body = @b, css = @css, note = @n WHERE company_id = @c AND document_type = @t AND version = @v",
                cancellationToken,
                ("s", settings), ("b", body), ("css", css), ("n", note), ("c", context.CompanyId), ("t", documentType), ("v", version)).ConfigureAwait(false);
        }
        else
        {
            version = 1 + (await SalesSql.ScalarAsync<int?>(
                context, "SELECT max(version) FROM md.print_format WHERE company_id = @c AND document_type = @t", cancellationToken,
                ("c", context.CompanyId), ("t", documentType)).ConfigureAwait(false) ?? 0);
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO md.print_format (company_id, document_type, version, status, settings, body, css, note, created_by, created_at)
                VALUES (@c, @t, @v, 'DRAFT', @s::jsonb, @b, @css, @n, @by, @at)
                """,
                cancellationToken,
                ("c", context.CompanyId), ("t", documentType), ("v", version), ("s", settings), ("b", body), ("css", css), ("n", note), ("by", by), ("at", context.Clock.UtcNow))
                .ConfigureAwait(false);
        }

        await context.AppendEventAsync(
            new EventDraft("PrintFormatDraftSaved", 1, Aggregate, context.Ids.NewId(), 1, JsonSerializer.Serialize(new { documentType, version }), Publish: false),
            cancellationToken).ConfigureAwait(false);
        return version;
    }
}

[RequiresPermission("print_format:manage")]
public sealed class SavePrintFormatDraftHandler : ICommandHandler<SavePrintFormatDraft>
{
    public string CommandType => "Sales.SavePrintFormatDraft";

    public async Task<string> HandleAsync(SavePrintFormatDraft command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        PrintFormatStore.EnsureType(command.DocumentType);
        var settings = PrintFormatRules.Validate(command.DocumentType, command.Settings ?? throw new DomainException(PrintErrors.SettingsInvalid, "The settings are missing."));
        var note = SalesSql.Optional(command.Note, 500, "The note");
        string body;
        string css;
        if (settings.Mode == PrintFormatRules.Simple)
        {
            body = PrintDocuments.BuiltIn(command.DocumentType);
            css = string.Empty;
        }
        else
        {
            body = command.Body ?? string.Empty;
            css = command.Css ?? string.Empty;
            if (body.Trim().Length == 0 || body.Length > 200000 || css.Length > 100000)
            {
                throw new DomainException(PrintErrors.TemplateInvalid, "An advanced format needs its template (at most 200,000 characters) and CSS (at most 100,000).");
            }

            PrintFormatRules.EnsureSafe(body, css);
            PrintRenderer.Parse(body);
        }

        var version = await PrintFormatStore.WriteDraftAsync(context, command.DocumentType, PrintFormatRules.Serialize(settings), body, css, note, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { documentType = command.DocumentType, version, status = "DRAFT" });
    }
}

[RequiresPermission("print_format:manage")]
public sealed class RestorePrintFormatHandler : ICommandHandler<RestorePrintFormat>
{
    private sealed record Saved(string Settings, string Body, string Css);

    public string CommandType => "Sales.RestorePrintFormat";

    public async Task<string> HandleAsync(RestorePrintFormat command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        PrintFormatStore.EnsureType(command.DocumentType);
        var source = command.Version == 0
            ? new Saved(PrintFormatRules.Serialize(PrintFormatRules.Default(command.DocumentType)), PrintDocuments.BuiltIn(command.DocumentType), string.Empty)
            : await Reading.SingleOrDefaultAsync(
                  context.Connection,
                  context.Transaction,
                  "SELECT settings::text, body, css FROM md.print_format WHERE company_id = @c AND document_type = @t AND version = @v AND status <> 'DRAFT'",
                  r => new Saved(r.GetString(0), r.GetString(1), r.GetString(2)),
                  cancellationToken,
                  ("c", context.CompanyId), ("t", command.DocumentType), ("v", command.Version)).ConfigureAwait(false)
              ?? throw new DomainException(SalesErrors.NotFound, "There is no such activated version to restore.");
        var note = command.Version == 0 ? "Formato incluido «Rochell»" : string.Create(CultureInfo.InvariantCulture, $"Copia de la versión {command.Version}");
        var version = await PrintFormatStore.WriteDraftAsync(context, command.DocumentType, source.Settings, source.Body, source.Css, note, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { documentType = command.DocumentType, version, status = "DRAFT", restoredFrom = command.Version });
    }
}

[RequiresPermission("print_format:manage", StepUp = true)]
public sealed class ActivatePrintFormatHandler : ICommandHandler<ActivatePrintFormat>
{
    private sealed record Draft(string Settings, string Body, string Css);

    public string CommandType => "Sales.ActivatePrintFormat";

    public async Task<string> HandleAsync(ActivatePrintFormat command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        PrintFormatStore.EnsureType(command.DocumentType);
        var draft = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT settings::text, body, css FROM md.print_format WHERE company_id = @c AND document_type = @t AND version = @v AND status = 'DRAFT'",
            r => new Draft(r.GetString(0), r.GetString(1), r.GetString(2)),
            cancellationToken,
            ("c", context.CompanyId), ("t", command.DocumentType), ("v", command.Version)).ConfigureAwait(false)
            ?? throw new DomainException(SalesErrors.NotFound, "Only the type's draft is activated.");

        // E-PRT-5, E-PRT-02-6: the test documents must carry what the law and ENT-1 require.
        PrintSamples.EnsureMandatory(command.DocumentType, new PrintDocuments.Format(draft.Body, draft.Css, draft.Settings, command.Version));

        var by = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE md.print_format SET status = 'RETIRED' WHERE company_id = @c AND document_type = @t AND status = 'ACTIVE'", cancellationToken,
            ("c", context.CompanyId), ("t", command.DocumentType)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE md.print_format SET status = 'ACTIVE', activated_by = @by, activated_at = @at WHERE company_id = @c AND document_type = @t AND version = @v",
            cancellationToken,
            ("by", by), ("at", context.Clock.UtcNow), ("c", context.CompanyId), ("t", command.DocumentType), ("v", command.Version)).ConfigureAwait(false);
        await context.AppendEventAsync(
            new EventDraft("PrintFormatActivated", 1, PrintFormatStore.Aggregate, context.Ids.NewId(), 1,
                JsonSerializer.Serialize(new { documentType = command.DocumentType, version = command.Version }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { documentType = command.DocumentType, version = command.Version, status = "ACTIVE" });
    }
}

[RequiresPermission("print_format:manage")]
public sealed class SetCompanyLogoHandler : ICommandHandler<SetCompanyLogo>
{
    public string CommandType => "Sales.SetCompanyLogo";

    public async Task<string> HandleAsync(SetCompanyLogo command, CommandContext context, CancellationToken cancellationToken)
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
            throw new DomainException(PrintErrors.LogoInvalid, "The logo is not valid base64.");
        }

        // E-PRT-8: PNG or JPEG, recognised by their first bytes; never SVG.
        var type = Rochell.Platform.Files.EvidenceImages.ContentType(content);
        if (type is null || content.Length > 1024 * 1024) // type-limit: E-PRT-8
        {
            throw new DomainException(PrintErrors.LogoInvalid, "The logo is a PNG or JPEG image of at most 1 MB.");
        }

        var by = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var version = await SalesSql.ScalarAsync<long>(
            context,
            """
            INSERT INTO md.company_logo (company_id, content, content_type, sha256, set_by, set_at, version) VALUES (@c, @b, @t, @h, @by, @at, 1)
            ON CONFLICT (company_id) DO UPDATE SET content = EXCLUDED.content, content_type = EXCLUDED.content_type, sha256 = EXCLUDED.sha256, set_by = EXCLUDED.set_by,
                set_at = EXCLUDED.set_at, version = md.company_logo.version + 1
            RETURNING version
            """,
            cancellationToken,
            ("c", context.CompanyId), ("b", content), ("t", type), ("h", SHA256.HashData(content)), ("by", by), ("at", context.Clock.UtcNow)).ConfigureAwait(false);
        await context.AppendEventAsync(
            new EventDraft("CompanyLogoSet", 1, "CompanyLogo", context.CompanyId, version, JsonSerializer.Serialize(new { contentType = type, bytes = content.Length }), Publish: false),
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { contentType = type, version });
    }
}

/// <summary>E-PRT-02-7: every document type's formats — the ACTIVE and the draft, the history — and the logo.</summary>
public sealed record ListPrintFormats(Guid CompanyId, Guid SessionId) : IQuery;

public sealed record PrintFormatVersionView(int Version, string Status, string Mode, string? Note, string? CreatedBy, DateTime CreatedAt, string? ActivatedBy, DateTime? ActivatedAt);

public sealed record PrintFormatTypeView(string DocumentType, string Label, IReadOnlyList<string> Papers, int ActiveVersion, int? DraftVersion, IReadOnlyList<PrintFormatVersionView> Versions);

public sealed record CompanyLogoView(string ContentType, string ContentBase64, DateTime SetAt, string? SetBy);

public sealed record PrintFormatList(IReadOnlyList<PrintFormatTypeView> Types, CompanyLogoView? Logo);

[RequiresPermission("configuration:read")]
public sealed class ListPrintFormatsHandler : IQueryHandler<ListPrintFormats>
{
    private sealed record Row(string Type, PrintFormatVersionView View);

    public string QueryType => "Sales.ListPrintFormats";

    public async Task<string> HandleAsync(ListPrintFormats query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var rows = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT f.document_type, f.version, f.status, coalesce(f.settings ->> 'modo', 'SENCILLO'), f.note, coalesce(c.display_name, c.email), f.created_at,
                   coalesce(a.display_name, a.email), f.activated_at
            FROM md.print_format f JOIN iam.user c ON c.user_id = f.created_by LEFT JOIN iam.user a ON a.user_id = f.activated_by
            WHERE f.company_id = @c ORDER BY f.document_type, f.version DESC
            """,
            r => new Row(r.GetString(0), new PrintFormatVersionView(r.GetInt32(1), r.GetString(2), r.GetString(3), r.NullableString(4), r.NullableString(5), r.GetFieldValue<DateTime>(6),
                r.NullableString(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateTime>(8))),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        var types = PrintDocumentTypes.All.Select(t =>
        {
            var versions = rows.Where(r => r.Type == t).Select(r => r.View).ToList();
            return new PrintFormatTypeView(
                t, PrintSamples.Label(t), PrintFormatRules.Papers(t), versions.FirstOrDefault(v => v.Status == "ACTIVE")?.Version ?? 0, versions.FirstOrDefault(v => v.Status == "DRAFT")?.Version, versions);
        }).ToList();
        var logo = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT l.content_type, l.content, l.set_at, coalesce(u.display_name, u.email) FROM md.company_logo l JOIN iam.user u ON u.user_id = l.set_by WHERE l.company_id = @c",
            r => new CompanyLogoView(r.GetString(0), Convert.ToBase64String(r.GetFieldValue<byte[]>(1)), r.GetFieldValue<DateTime>(2), r.NullableString(3)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        return ApiJson.Serialize(new PrintFormatList(types, logo));
    }
}

/// <summary>One version of a type's format (0: the built-in «Rochell» one), with its settings, template and CSS.</summary>
public sealed record GetPrintFormat(Guid CompanyId, Guid SessionId, string DocumentType, int Version) : IQuery;

public sealed record PrintFormatDetail(
    string DocumentType, int Version, string Status, PrintSettings Settings, string Body, string Css, IReadOnlyList<PrintColumnSetting> Catalogue, string BuiltInBody);

[RequiresPermission("configuration:read")]
public sealed class GetPrintFormatHandler : IQueryHandler<GetPrintFormat>
{
    private sealed record Row(string Status, string Settings, string Body, string Css);

    public string QueryType => "Sales.GetPrintFormat";

    public async Task<string> HandleAsync(GetPrintFormat query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        if (!PrintDocumentTypes.All.Contains(query.DocumentType))
        {
            throw new DomainException(QueryErrors.InvalidParameter, $"Unknown document type {query.DocumentType}.");
        }

        var builtIn = PrintDocuments.BuiltIn(query.DocumentType);
        var row = query.Version == 0
            ? new Row("BUILT_IN", "{}", builtIn, string.Empty)
            : await Reading.SingleOrDefaultAsync(
                  context.Connection,
                  context.Transaction,
                  "SELECT status, settings::text, body, css FROM md.print_format WHERE company_id = @c AND document_type = @t AND version = @v",
                  r => new Row(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)),
                  cancellationToken,
                  ("c", context.CompanyId), ("t", query.DocumentType), ("v", query.Version)).ConfigureAwait(false)
              ?? throw new DomainException(QueryErrors.NotFound, "There is no such version.");
        var defaults = PrintFormatRules.Default(query.DocumentType);
        return ApiJson.Serialize(new PrintFormatDetail(
            query.DocumentType, query.Version, row.Status, PrintFormatRules.Parse(query.DocumentType, row.Settings), row.Body, row.Css, defaults.Columns, builtIn));
    }
}

/// <summary>
/// E-PRT-02-5: what a format would print — with the document of that number, or the latest of the type, or an example when there is
/// none — watermarked «VISTA PREVIA». Nothing is saved.
/// </summary>
public sealed record PreviewPrintFormat(
    Guid CompanyId, Guid SessionId, string DocumentType, PrintSettings Settings, string? Body = null, string? Css = null, string? DocumentNo = null, string? BaseUrl = null) : IQuery;

public sealed record PrintFormatPreview(PrintedDocument Document, string? DocumentNo, bool Example);

/// <summary>The preview's body over HTTP.</summary>
public sealed record PrintFormatPreviewRequest(string DocumentType, PrintSettings Settings, string? Body = null, string? Css = null, string? DocumentNo = null);

[RequiresPermission("configuration:read")]
public sealed class PreviewPrintFormatHandler(DriverLinkKey? key = null) : IQueryHandler<PreviewPrintFormat>
{
    public string QueryType => "Sales.PreviewPrintFormat";

    public async Task<string> HandleAsync(PreviewPrintFormat query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        if (!PrintDocumentTypes.All.Contains(query.DocumentType))
        {
            throw new DomainException(QueryErrors.InvalidParameter, $"Unknown document type {query.DocumentType}.");
        }

        var settings = PrintFormatRules.Validate(query.DocumentType, query.Settings ?? PrintFormatRules.Default(query.DocumentType));
        var simple = settings.Mode == PrintFormatRules.Simple;
        var body = simple ? PrintDocuments.BuiltIn(query.DocumentType) : query.Body ?? string.Empty;
        var css = simple ? string.Empty : query.Css ?? string.Empty;
        if (!simple)
        {
            PrintFormatRules.EnsureSafe(body, css);
        }

        var format = new PrintDocuments.Format(body, css, PrintFormatRules.Serialize(settings), 0);
        var target = await PrintSamples.FindAsync(context, query.DocumentType, query.DocumentNo, cancellationToken).ConfigureAwait(false);
        PrintedDocument printed;
        if (target is null)
        {
            var (title, model) = PrintSamples.Model(query.DocumentType, query.BaseUrl);
            printed = PrintDocuments.Render(query.DocumentType, title, model, format, await PrintDocuments.LogoAsync(context, cancellationToken).ConfigureAwait(false), "VISTA PREVIA");
        }
        else
        {
            printed = await PrintDocuments.RenderAsync(
                context, key, new GetPrintDocument(context.CompanyId, context.SessionId, query.DocumentType, target.Id, target.From, target.To, query.BaseUrl), cancellationToken, format,
                "VISTA PREVIA").ConfigureAwait(false);
        }

        return ApiJson.Serialize(new PrintFormatPreview(printed, target?.Number, target is null));
    }
}

/// <summary>Example documents for previews and for the mandatory-content check (E-PRT-02-5/6).</summary>
public static class PrintSamples
{
    public sealed record Target(Guid Id, string Number, DateOnly? From = null, DateOnly? To = null);

    private const string IssuerRnc = "131925332";
    private const string BuyerRnc = "101000001";
    private const string SampleEncf = "E310000000123";
    private const string SecurityCode = "SEG123";

    /// <summary>The examples' amounts are made-up figures, not thresholds: written as text.</summary>
    private static decimal D(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    public static string Label(string documentType) => documentType switch
    {
        PrintDocumentTypes.DeliveryNote => "Conduce",
        PrintDocumentTypes.Invoice => "Factura",
        PrintDocumentTypes.Quote => "Cotización",
        PrintDocumentTypes.Proforma => "Proforma de entrega",
        PrintDocumentTypes.OrderProforma => "Proforma de pedido (DGII)",
        PrintDocumentTypes.Statement => "Estado de cuenta",
        PrintDocumentTypes.ArAging => "Facturas pendientes",
        _ => documentType,
    };

    /// <summary>The document a preview uses: the one of that number, else the latest of the type; null when the company has none.</summary>
    public static async Task<Target?> FindAsync(QueryContext context, string documentType, string? number, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var no = string.IsNullOrWhiteSpace(number) ? null : number.Trim();
        var today = Rochell.Platform.Time.BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);
        string? sql = documentType switch
        {
            PrintDocumentTypes.DeliveryNote => "SELECT delivery_id, delivery_no FROM log.delivery WHERE company_id = @c AND (@n::text IS NULL OR delivery_no = @n) ORDER BY delivery_no DESC LIMIT 1",
            PrintDocumentTypes.Invoice => "SELECT invoice_id, invoice_no FROM sal.invoice WHERE company_id = @c AND (@n::text IS NULL OR invoice_no = @n) ORDER BY invoice_no DESC LIMIT 1",
            PrintDocumentTypes.Quote => "SELECT quote_id, quote_no FROM sal.quote WHERE company_id = @c AND (@n::text IS NULL OR quote_no = @n) ORDER BY quote_no DESC LIMIT 1",
            PrintDocumentTypes.Proforma => "SELECT proforma_id, proforma_no FROM sal.proforma WHERE company_id = @c AND (@n::text IS NULL OR proforma_no = @n) ORDER BY proforma_no DESC LIMIT 1",
            PrintDocumentTypes.OrderProforma => "SELECT sales_order_id, order_no FROM sal.sales_order WHERE company_id = @c AND (@n::text IS NULL OR order_no = @n) ORDER BY order_no DESC LIMIT 1",
            // The customer of the invoice of that number (or the latest one), this month so far.
            PrintDocumentTypes.Statement or PrintDocumentTypes.ArAging =>
                "SELECT party_id, invoice_no FROM sal.invoice WHERE company_id = @c AND (@n::text IS NULL OR invoice_no = @n) ORDER BY invoice_no DESC LIMIT 1",
            _ => null,
        };
        if (sql is null)
        {
            return null;
        }

        var found = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, sql, r => new Target(r.GetGuid(0), r.GetString(1)), cancellationToken, ("c", context.CompanyId), ("n", no)).ConfigureAwait(false);
        return found is not null && documentType == PrintDocumentTypes.Statement ? found with { From = new DateOnly(today.Year, today.Month, 1), To = today } : found;
    }

    /// <summary>An example of the type: every field filled, the e-CF accepted and the conduce in transit with its driver's QR.</summary>
    public static (string Title, IReadOnlyDictionary<string, object?> Model) Model(string documentType, string? baseUrl = null, bool draft = false)
    {
        var day = new DateOnly(2026, 10, 8);
        var at = new DateTime(2026, 10, 8, 14, 30, 0, DateTimeKind.Utc);
        const string issuer = "BLOCK ROCHELL SRL";
        const string customer = "Constructora de Ejemplo SRL";
        switch (documentType)
        {
            case PrintDocumentTypes.DeliveryNote:
                var delivery = new DeliveryPrint(
                    issuer, IssuerRnc, customer, BuyerRnc, "Obra Punta Cana", "MATILLA", "La Matilla", "CD-000123", "PV-000045", day, day, draft ? "PLANNED" : "IN_TRANSIT",
                    "DELIVERED_OWN_TRANSPORT", draft ? null : at, "L123456", "Juan Pérez", null, null, draft ? null : D("18000"), draft ? null : D("8000"), draft ? null : D("10000"),
                    draft ? null : "TK-1001", [new(1, "BLOQUE-6", "Bloque de 6 pulgadas", "un", D("600"), draft ? 0m : D("600"), 0m, [new("AP-2026-1", "PATIO", D("600"))])], null, null, "BR 09",
                    draft ? null : "/entrega/?c=0&d=0&g=1&k=ejemplo");
                return ("Conduce CD-000123", PrintDocuments.DeliveryModel(delivery, baseUrl ?? "https://ejemplo.rochell"));
            case PrintDocumentTypes.Invoice:
                var line = new InvoiceLineView(1, Guid.Empty, "CD-000123", Guid.Empty, "BLOQUE-6", "Bloque de 6 pulgadas", "un", D("600"), D("50.00"), D("30000.00"), D("5400.00"));
                var detail = new InvoiceDetail(
                    new InvoiceSummary(Guid.Empty, "FA-000123", day, day.AddDays(30), Guid.Empty, customer, "31", SampleEncf, "ISSUED", "POSTED", "ECF_ACCEPTED", D("30000.00"), D("5400.00"),
                        D("35400.00"), D("35400.00"), 1),
                    null, null, null, [line], null, [], [], [], [],
                    new EcfStampView(Guid.Empty, "ACCEPTED", SampleEncf, 1, SecurityCode, at, "https://ecf.dgii.gov.do/ConsultaTimbre?RncEmisor=131925332&ENCF=E310000000123", null, 1),
                    new DocumentIssuer(IssuerRnc, issuer, "Industrias Rochell", "Carretera Higüey – La Romana, La Matilla", "809-555-0100", "industrias@rochell.com.do"));
                var package = new InvoiceFiscalPackage("FA-000123", "31", day, day.AddDays(30), IssuerRnc, issuer, BuyerRnc, customer, [line], D("30000.00"), D("5400.00"), D("35400.00"), "ECF_ACCEPTED");
                return ("Factura FA-000123", PrintDocuments.InvoiceModel(detail, package, []));
            case PrintDocumentTypes.Quote:
                return ("Cotización COT-000123", PrintDocuments.QuoteModel(new QuotePrint(
                    "COT-000123", day, day.AddDays(30), "SENT", false, IssuerRnc, issuer, BuyerRnc, customer, "DELIVERED_OWN_TRANSPORT", "Obra Punta Cana", "OC-77", "Precios sujetos a disponibilidad",
                    [new(1, "BLOQUE-6", "Bloque de 6 pulgadas", "un", D("1000"), D("50.00"), D("50000.00"), D("9000.00"), D("59000.00"))], D("50000.00"), D("9000.00"), D("59000.00"))));
            case PrintDocumentTypes.Proforma:
                return ("Proforma PF-000123", PrintDocuments.ProformaModel(new ProformaDetail(
                    new ProformaSummary(Guid.Empty, "PF-000123", Guid.Empty, customer, BuyerRnc, Guid.Empty, "PV-000045", Guid.Empty, "CD-000123", day, day.AddDays(30), 0, true, D("30000.00"),
                        D("5400.00"), D("35400.00"), 0m, 0m, D("35400.00"), "OPEN", "NONE", null, null, 1),
                    IssuerRnc, issuer, "Obra Punta Cana", null, [new(1, "BLOQUE-6", "Bloque de 6 pulgadas", "un", D("600"), D("50.00"), D("30000.00"), D("5400.00"), D("35400.00"))], [], [])));
            case PrintDocumentTypes.OrderProforma:
                return ("Proforma del pedido PV-000045", PrintDocuments.OrderProformaModel(new SalesOrderProforma(
                    "PV-000045", day, day, IssuerRnc, issuer, BuyerRnc, customer, "Obra Punta Cana",
                    [new(1, "BLOQUE-6", "Bloque de 6 pulgadas", "un", D("600"), D("50.00"), D("30000.00"), D("5400.00"), D("35400.00"))], D("30000.00"), D("5400.00"), D("35400.00")), "CONFIRMED"));
            case PrintDocumentTypes.Statement:
                return ("Estado de cuenta", PrintDocuments.StatementModel(new CustomerStatement(
                    Guid.Empty, customer, BuyerRnc, new DateOnly(2026, 10, 1), day, 0m,
                    [new(day, "FACTURA", "FA-000123", "InvoicePosted", D("35400.00"), 0m, D("35400.00")), new(day, "COBRO", "REC-000010", "ReceiptRecorded", 0m, D("5400.00"), D("30000.00"))],
                    D("35400.00"), D("5400.00"), D("30000.00"), [], 0m), issuer, IssuerRnc, day));
            case PrintDocumentTypes.ArAging:
                return ("Facturas pendientes", PrintDocuments.AgingModel(new ArAgingCustomer(
                    Guid.Empty, customer, D("30000.00"), 0m, 0m, 0m, 0m, D("30000.00"), 0m, D("30000.00"),
                    [new(Guid.Empty, Guid.Empty, "FA-000123", SampleEncf, day, day.AddDays(30), D("30000.00"), 0, "CURRENT")], 0m, 0m, []), day, new ArAgingBuckets(30, 60, 90), issuer, IssuerRnc));
            default:
                throw new DomainException(QueryErrors.InvalidParameter, $"Unknown document type {documentType}.");
        }
    }

    /// <summary>
    /// E-PRT-5, E-PRT-02-6: renders the type's examples with the format and refuses it when something mandatory is missing — the
    /// invoice's e-NCF, RNCs, QR, security code and signature date; the conduce's driver's QR and its draft watermark.
    /// </summary>
    public static void EnsureMandatory(string documentType, PrintDocuments.Format format)
    {
        ArgumentNullException.ThrowIfNull(format);
        var (title, model) = Model(documentType);
        var body = PrintDocuments.Render(documentType, title, model, format, null).Body;
        var missing = new List<string>();
        void Need(bool present, string what)
        {
            if (!present)
            {
                missing.Add(what);
            }
        }

        if (documentType == PrintDocumentTypes.Invoice)
        {
            Need(body.Contains(SampleEncf, StringComparison.Ordinal), "el e-NCF");
            Need(body.Contains(IssuerRnc, StringComparison.Ordinal), "el RNC del emisor");
            Need(body.Contains(BuyerRnc, StringComparison.Ordinal), "el RNC del comprador");
            Need(body.Contains("<svg", StringComparison.Ordinal), "el código QR del e-CF");
            Need(body.Contains(SecurityCode, StringComparison.Ordinal), "el código de seguridad");
            Need(body.Contains(PrintText.DateTime(new DateTime(2026, 10, 8, 14, 30, 0, DateTimeKind.Utc)), StringComparison.Ordinal), "la fecha de firma digital");
        }
        else if (documentType == PrintDocumentTypes.DeliveryNote)
        {
            Need(body.Contains("<svg", StringComparison.Ordinal), "el QR del chofer");
            var (draftTitle, draftModel) = Model(documentType, draft: true);
            Need(PrintDocuments.Render(documentType, draftTitle, draftModel, format, null).Body.Contains("BORRADOR – NO DESPACHADO", StringComparison.Ordinal), "la marca de agua «BORRADOR – NO DESPACHADO» antes del portón");
        }

        if (missing.Count > 0)
        {
            throw new DomainException(PrintErrors.MandatoryMissing, $"The format lacks what is mandatory: {string.Join(", ", missing)} (E-PRT-5).");
        }
    }
}
