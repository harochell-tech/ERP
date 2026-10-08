using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Fluid;
using Fluid.Values;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;

namespace Rochell.Sales.Printing;

/// <summary>A document's format: its Liquid body and its own CSS on top of the base (E-PRT-01-1).</summary>
public sealed record PrintTemplate(string Body, string Css);

/// <summary>
/// What a printed document is: the full HTML (with the fonts, for the PDF of the e-mail, E-PRT-01-6), and its CSS and body apart for the
/// screen, which shows them in an isolated block of the page (E-PRT-01-2).
/// </summary>
public sealed record RenderedDocument(string Title, string Html, string Css, string Body);

public static class PrintErrors
{
    public const string TemplateInvalid = "PRINT_TEMPLATE_INVALID";

    /// <summary>E-PRT-02-3: a template with scripts, event attributes, javascript: links or external files.</summary>
    public const string TemplateUnsafe = "PRINT_TEMPLATE_UNSAFE";

    public const string SettingsInvalid = "PRINT_SETTINGS_INVALID";

    /// <summary>E-PRT-5, E-PRT-02-6: the test document lacks what the law or ENT-1 require.</summary>
    public const string MandatoryMissing = "PRINT_MANDATORY_MISSING";

    public const string LogoInvalid = "PRINT_LOGO_INVALID";
}

/// <summary>
/// PRT-01 (E-PRT-01-1…3, 6): renders a document's format with Fluid. A template sees only the model it is given — strings, booleans,
/// lists and dictionaries already formatted by the server — never files, the database or the network; output is HTML-encoded.
/// </summary>
public static class PrintRenderer
{
    private static readonly FluidParser Parser = new();
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(System.Text.Unicode.UnicodeRanges.All); // accents stay readable
    private static readonly ConcurrentDictionary<string, IFluidTemplate> Cache = new(StringComparer.Ordinal);
    private static readonly Lazy<string> Base = new(() => Asset("base.css"));
    private static readonly Lazy<string> Fonts = new(FontFaces);

    private static readonly TemplateOptions Options = new()
    {
        MaxSteps = 200000, // type-limit: a runaway loop in a hand-edited template stops instead of hanging the server
        MaxRecursion = 20, // type-limit
        CultureInfo = CultureInfo.InvariantCulture,
    };

    /// <summary>Parses a template, telling what is wrong (line and column) when it does not parse.</summary>
    public static IFluidTemplate Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Cache.GetOrAdd(source, s => Parser.TryParse(s, out var template, out var error)
            ? template
            : throw new DomainException(PrintErrors.TemplateInvalid, $"The print template does not parse: {error}"));
    }

    public static RenderedDocument Render(PrintTemplate template, string title, IReadOnlyDictionary<string, object?> model)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(model);
        var context = new TemplateContext(Options);
        foreach (var (key, value) in model)
        {
            context.SetValue(key, ToFluid(value));
        }

        string body;
        try
        {
            body = Parse(template.Body).Render(context, Encoder);
        }
        catch (InvalidOperationException ex)
        {
            throw new DomainException(PrintErrors.TemplateInvalid, $"The print template failed: {ex.Message}");
        }

        var css = Base.Value + "\n" + template.Css;
        var html = new StringBuilder()
            .Append("<!doctype html><html lang=\"es-DO\"><head><meta charset=\"utf-8\"><title>").Append(Encoder.Encode(title)).Append("</title><style>")
            .Append(Fonts.Value).Append('\n').Append(css).Append("</style></head><body style=\"margin:0\"><div class=\"doc\">").Append(body).Append("</div></body></html>")
            .ToString();
        return new RenderedDocument(title, html, css, body);
    }

    /// <summary>The model's values as Fluid sees them: dictionaries and lists recursively, strings, booleans and whole numbers.</summary>
    private static FluidValue ToFluid(object? value) => value switch
    {
        null => NilValue.Instance,
        string s => new StringValue(s),
        bool b => BooleanValue.Create(b),
        int i => NumberValue.Create(i),
        IReadOnlyDictionary<string, object?> d => new DictionaryValue(new FluidValueDictionaryFluidIndexable(d.ToDictionary(kv => kv.Key, kv => ToFluid(kv.Value), StringComparer.Ordinal))),
        System.Collections.IEnumerable list => new ArrayValue(list.Cast<object?>().Select(ToFluid).ToArray()),
        _ => throw new ArgumentException($"A print model holds strings, booleans, whole numbers, dictionaries and lists, not {value.GetType().Name}.", nameof(value)),
    };

    private static string Asset(string name)
    {
        using var stream = typeof(PrintRenderer).Assembly.GetManifestResourceStream($"Rochell.Sales.Printing.Assets.{name}")
            ?? throw new InvalidOperationException($"Missing print asset {name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string FontFaces()
    {
        var css = new StringBuilder();
        foreach (var (file, family, weight) in new[] { ("plex-sans-400", "IBM Plex Sans", 400), ("plex-sans-600", "IBM Plex Sans", 600), ("plex-sans-700", "IBM Plex Sans", 700), ("plex-mono-400", "IBM Plex Mono", 400) })
        {
            using var stream = typeof(PrintRenderer).Assembly.GetManifestResourceStream($"Rochell.Sales.Printing.Assets.{file}.woff2")
                ?? throw new InvalidOperationException($"Missing font {file}.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            css.Append(CultureInfo.InvariantCulture, $"@font-face{{font-family:\"{family}\";font-weight:{weight};font-style:normal;src:url(data:font/woff2;base64,{Convert.ToBase64String(buffer.ToArray())}) format(\"woff2\");}}");
        }

        return css.ToString();
    }
}

/// <summary>E-PRT-01-3: how the server writes amounts, quantities and dates on paper — as the screen does (formatDecimal, formatDate, formatDateTime).</summary>
public static class PrintText
{
    /// <summary>Amounts: thousands with commas, at least 2 decimals, more only when they are not zeros (12.3450 → 12.345).</summary>
    public static string Money(decimal value) => Grouped(value, 2);

    /// <summary>Quantities: no forced decimals (40.000000 → 40).</summary>
    public static string Quantity(decimal value) => Grouped(value, 0);

    public static string Date(DateOnly value) => value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    public static string Date(DateOnly? value) => value is { } d ? Date(d) : "—";

    /// <summary>A UTC instant in Dominican time, «29/09/2026 11:15 p. m.».</summary>
    public static string DateTime(System.DateTime? utc)
    {
        if (utc is not { } at)
        {
            return "—";
        }

        var day = BusinessCalendar.DefaultBusinessDate(at);
        var time = at - BusinessCalendar.DayUtcRange(day).StartUtc;
        var hour = (int)time.TotalHours;
        var twelve = hour % 12 == 0 ? 12 : hour % 12;
        return string.Create(CultureInfo.InvariantCulture, $"{Date(day)} {twelve:00}:{time.Minutes:00} {(hour < 12 ? "a. m." : "p. m.")}");
    }

    private static string Grouped(decimal value, int minFraction)
    {
        var text = Math.Abs(value).ToString(CultureInfo.InvariantCulture);
        var point = text.IndexOf('.', StringComparison.Ordinal);
        var integer = point < 0 ? text : text[..point];
        var fraction = point < 0 ? string.Empty : text[(point + 1)..].TrimEnd('0');
        if (fraction.Length < minFraction)
        {
            fraction = fraction.PadRight(minFraction, '0');
        }

        var grouped = new StringBuilder();
        for (var i = 0; i < integer.Length; i++)
        {
            if (i > 0 && (integer.Length - i) % 3 == 0)
            {
                grouped.Append(',');
            }

            grouped.Append(integer[i]);
        }

        return (value < 0m && (integer + fraction).Any(c => c is >= '1' and <= '9') ? "-" : string.Empty) + grouped + (fraction.Length > 0 ? "." + fraction : string.Empty);
    }
}
