using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Rochell.Platform.Commands;

namespace Rochell.Sales.Printing;

/// <summary>A column of a document's table as the simple screen sets it (E-PRT-02-1).</summary>
public sealed record PrintColumnSetting(
    [property: JsonPropertyName("clave")] string Key,
    [property: JsonPropertyName("titulo")] string Title,
    [property: JsonPropertyName("mostrar")] bool Show,
    [property: JsonPropertyName("ancho")] int? WidthPercent,
    [property: JsonPropertyName("alineacion")] string Align);

/// <summary>The fixed texts of a format (E-PRT-3): above the document, at its foot, the quote's conditions, the bank accounts.</summary>
public sealed record PrintTexts(
    [property: JsonPropertyName("encabezado")] string? Header,
    [property: JsonPropertyName("pie")] string? Footer,
    [property: JsonPropertyName("condiciones")] string? Conditions,
    [property: JsonPropertyName("cuentas")] string? BankAccounts);

/// <summary>
/// E-PRT-02-1/2/4: what the simple screen chooses. <see cref="Mode"/> SENCILLO renders the built-in template with these settings;
/// AVANZADO renders the version's own template and CSS (the settings keep only the paper).
/// </summary>
public sealed record PrintSettings(
    [property: JsonPropertyName("modo")] string Mode,
    [property: JsonPropertyName("papel")] string Paper,
    [property: JsonPropertyName("margen_mm")] int MarginMm,
    [property: JsonPropertyName("letra_px")] int FontPx,
    [property: JsonPropertyName("alto_fila_px")] int RowPaddingPx,
    [property: JsonPropertyName("color")] string Accent,
    [property: JsonPropertyName("logo")] bool ShowLogo,
    [property: JsonPropertyName("logo_ancho_mm")] int LogoWidthMm,
    [property: JsonPropertyName("logo_posicion")] string LogoPosition,
    [property: JsonPropertyName("columnas")] IReadOnlyList<PrintColumnSetting> Columns,
    [property: JsonPropertyName("textos")] PrintTexts Texts);

public static partial class PrintFormatRules
{
    public const string Simple = "SENCILLO";
    public const string Advanced = "AVANZADO";
    public const string Letter = "CARTA";
    public const string HalfLetter = "MEDIA_CARTA";
    public const string Ticket80 = "TICKET_80";

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    /// <summary>The columns each table offers, in their built-in order: key, title, numeric.</summary>
    public static IReadOnlyList<(string Key, string Title, bool Numeric)> Catalogue(string documentType) => documentType switch
    {
        PrintDocumentTypes.DeliveryNote =>
            [("linea", "#", true), ("producto", "Producto", false), ("unidad", "Unidad", false), ("planificado", "Planificado", true), ("despachado", "Despachado", true),
             ("entregado", "Entregado", true), ("lotes", "Lotes", false)],
        PrintDocumentTypes.Invoice =>
            [("linea", "#", true), ("descripcion", "Descripción", false), ("unidad", "Unidad", false), ("cantidad", "Cantidad", true), ("precio", "Precio (RD$)", true),
             ("itbis", "ITBIS (RD$)", true), ("importe", "Importe (RD$)", true)],
        PrintDocumentTypes.Quote or PrintDocumentTypes.Proforma or PrintDocumentTypes.OrderProforma =>
            [("linea", "#", true), ("producto", "Producto", false), ("unidad", "Unidad", false), ("cantidad", "Cantidad", true), ("precio", "Precio (RD$)", true),
             ("neto", "Neto (RD$)", true), ("itbis", "ITBIS (RD$)", true), ("total", "Total (RD$)", true)],
        PrintDocumentTypes.Statement =>
            [("fecha", "Fecha", false), ("tipo", "Tipo", false), ("documento", "Documento", false), ("debito", "Débito (RD$)", true), ("credito", "Crédito (RD$)", true),
             ("saldo", "Saldo (RD$)", true)],
        PrintDocumentTypes.ArAging =>
            [("numero", "Factura", false), ("encf", "e-NCF", false), ("fecha", "Fecha", false), ("vence", "Vence", false), ("dias", "Días vencida", true),
             ("pendiente", "Pendiente (RD$)", true)],
        _ => [],
    };

    /// <summary>E-PRT-02-4: the papers a document may use — letter and half letter for all; the 80 mm ticket for the consumer invoice and the receipt.</summary>
    public static IReadOnlyList<string> Papers(string documentType) => documentType is PrintDocumentTypes.Invoice ? [Letter, HalfLetter, Ticket80] : [Letter, HalfLetter];

    /// <summary>The built-in settings: what PRT-01 printed (E-PRT-01-4).</summary>
    public static PrintSettings Default(string documentType)
        => new(
            Simple, Letter, 12, 15, 10, "#1d1d1f", false, 40, "IZQUIERDA",
            [.. Catalogue(documentType).Select(c => new PrintColumnSetting(c.Key, c.Title, true, null, c.Numeric ? "DERECHA" : "IZQUIERDA"))],
            new PrintTexts(null, null, null, null));

    public static PrintSettings Parse(string documentType, string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
        {
            return Default(documentType);
        }

        try
        {
            return JsonSerializer.Deserialize<PrintSettings>(json, Json) ?? Default(documentType);
        }
        catch (JsonException)
        {
            return Default(documentType);
        }
    }

    public static string Serialize(PrintSettings settings) => JsonSerializer.Serialize(settings, Json);

    /// <summary>
    /// Checks what the simple screen sent: a known mode and paper, sensible sizes, a #rrggbb colour, the columns of the catalogue only
    /// (each once; the missing ones are appended hidden), texts of at most 1,000 characters.
    /// </summary>
    public static PrintSettings Validate(string documentType, PrintSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var catalogue = Catalogue(documentType);
        if (settings.Mode is not (Simple or Advanced) || !Papers(documentType).Contains(settings.Paper)
            || settings.Paper == Ticket80 && documentType != PrintDocumentTypes.Invoice
            || settings.MarginMm is < 3 or > 30 || settings.FontPx is < 9 or > 20 || settings.RowPaddingPx is < 2 or > 20
            || !Colour().IsMatch(settings.Accent ?? string.Empty) || settings.LogoWidthMm is < 10 or > 120 || settings.LogoPosition is not ("IZQUIERDA" or "CENTRO" or "DERECHA"))
        {
            throw new DomainException(PrintErrors.SettingsInvalid, "Paper, margins (3–30 mm), font (9–20 px), row height (2–20 px), colour (#rrggbb) or logo (10–120 mm) not valid.");
        }

        var columns = settings.Columns ?? [];
        if (columns.Any(c => !catalogue.Any(k => k.Key == c.Key)) || columns.Select(c => c.Key).Distinct().Count() != columns.Count
            || columns.Any(c => c.Align is not ("IZQUIERDA" or "CENTRO" or "DERECHA") || c.WidthPercent is < 3 or > 80 || string.IsNullOrWhiteSpace(c.Title) || c.Title.Length > 60))
        {
            throw new DomainException(PrintErrors.SettingsInvalid, "Columns: only the document's, each once, with a title (60 characters at most), alignment and a width of 3–80 %.");
        }

        if (catalogue.Count > 0 && !columns.Any(c => c.Show))
        {
            throw new DomainException(PrintErrors.SettingsInvalid, "Show at least one column.");
        }

        var texts = settings.Texts ?? new PrintTexts(null, null, null, null);
        if (new[] { texts.Header, texts.Footer, texts.Conditions, texts.BankAccounts }.Any(t => t is { Length: > 1000 }))
        {
            throw new DomainException(PrintErrors.SettingsInvalid, "Each fixed text takes at most 1,000 characters.");
        }

        var complete = columns.Concat(catalogue.Where(k => columns.All(c => c.Key != k.Key)).Select(k => new PrintColumnSetting(k.Key, k.Title, false, null, k.Numeric ? "DERECHA" : "IZQUIERDA")))
            .Select(c => c with { Title = c.Title.Trim() })
            .ToList();
        return settings with { Columns = complete, Texts = new PrintTexts(Trim(texts.Header), Trim(texts.Footer), Trim(texts.Conditions), Trim(texts.BankAccounts)) };
    }

    /// <summary>E-PRT-02-3: an advanced template runs nothing and fetches nothing — only the logo and the document's data.</summary>
    public static void EnsureSafe(string body, string css)
    {
        foreach (var (text, what) in new[] { (body, "plantilla"), (css, "CSS") })
        {
            var match = Unsafe().Match(text ?? string.Empty);
            if (match.Success)
            {
                throw new DomainException(PrintErrors.TemplateUnsafe, $"The {what} contains «{match.Value}»: scripts, event attributes, javascript: links and external files are not allowed (E-PRT-02-3).");
            }
        }
    }

    /// <summary>The CSS the settings make: paper and margins, font, row height, accent colour, column widths and alignment.</summary>
    public static string Css(PrintSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var css = new StringBuilder();
        var (page, width) = s.Paper switch
        {
            HalfLetter => ("5.5in 8.5in", "5.5in"),
            Ticket80 => ("80mm auto", "80mm"),
            _ => ("letter", "8.5in"),
        };
        css.Append(CultureInfo.InvariantCulture, $"@page{{size:{page};margin:{s.MarginMm}mm}}");
        css.Append(CultureInfo.InvariantCulture, $".doc{{font-size:{s.FontPx}px;max-width:calc({width} - {2 * s.MarginMm}mm)}}");
        css.Append(CultureInfo.InvariantCulture, $".doc td,.doc th{{padding-top:{s.RowPaddingPx}px;padding-bottom:{s.RowPaddingPx}px}}");
        css.Append(CultureInfo.InvariantCulture, $".doc h1{{color:{s.Accent}}}.doc thead th{{border-bottom:2px solid {s.Accent}}}");
        css.Append(CultureInfo.InvariantCulture, $".doc .logo{{display:block;width:{s.LogoWidthMm}mm;margin:0 0 8px}}");
        if (s.LogoPosition == "CENTRO")
        {
            css.Append(".doc .logo{margin-left:auto;margin-right:auto}");
        }
        else if (s.LogoPosition == "DERECHA")
        {
            css.Append(".doc .logo{margin-left:auto}");
        }

        if (s.Paper == Ticket80)
        {
            css.Append(".doc dl.facts{display:block;padding:8px}.doc dl.facts dd{margin:0 0 4px}.doc .print-head{display:block}.doc .print-number{text-align:left}");
        }

        return css.ToString();
    }

    private static string? Trim(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex Colour();

    [GeneratedRegex(@"<\s*(script|iframe|object|embed|link|meta|base|form)\b|\bon[a-z]+\s*=|javascript\s*:|@import|url\s*\(\s*['""]?\s*(https?:|//)|\b(src|href)\s*=\s*['""]?\s*(https?:|//)", RegexOptions.IgnoreCase)]
    private static partial Regex Unsafe();
}
