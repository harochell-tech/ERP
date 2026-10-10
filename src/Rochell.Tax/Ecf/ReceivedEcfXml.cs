using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Rochell.Tax.Ecf;

/// <summary>One line of a received e-CF as its XML states it (DGII format: DetallesItems / Item).</summary>
public sealed record ReceivedEcfLine(int LineNo, string? ItemCode, string Description, decimal Quantity, string? UnitCode, decimal UnitPrice, decimal Amount, int? BillingIndicator);

/// <summary>What Core reads from a received e-CF's XML (E-OCR1-02-6: the amounts in pesos; another currency's mirror is ignored).</summary>
public sealed record ReceivedEcf(
    string EcfType,
    string Encf,
    string IssuerRnc,
    string? IssuerName,
    string? BuyerRnc,
    DateOnly? IssueDate,
    decimal? TotalAmount,
    decimal? ItbisAmount,
    string? SecurityCode,
    DateTime? SignedAtUtc,
    IReadOnlyList<ReceivedEcfLine> Lines);

/// <summary>
/// OCR1-02: reads the DGII e-CF XML a supplier sent (elements by local name, any namespace; no DTD, no external entities). Returns null
/// when it is not an e-CF with an issuer and an e-NCF. The security code is the first six characters of the signature value, as the
/// DGII prints it.
/// </summary>
public static class ReceivedEcfXml
{
    private static readonly TimeZoneInfo DominicanRepublic = TimeZoneInfo.FindSystemTimeZoneById("America/Santo_Domingo");

    public static ReceivedEcf? Parse(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        XDocument document;
        try
        {
            using var stream = new MemoryStream(content);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        var root = document.Root;
        var header = root is null ? null : Child(root, "Encabezado");
        if (root is null || header is null)
        {
            return null;
        }

        var idDoc = Child(header, "IdDoc");
        var issuer = Child(header, "Emisor");
        var buyer = Child(header, "Comprador");
        var totals = Child(header, "Totales");
        var encf = Value(idDoc, "eNCF");
        var issuerRnc = Value(issuer, "RNCEmisor");
        if (encf is null || issuerRnc is null)
        {
            return null;
        }

        var lines = new List<ReceivedEcfLine>();
        var items = Child(root, "DetallesItems");
        foreach (var item in items?.Elements().Where(e => e.Name.LocalName == "Item") ?? [])
        {
            var name = Value(item, "NombreItem");
            var quantity = Number(Value(item, "CantidadItem"));
            var price = Number(Value(item, "PrecioUnitarioItem"));
            var amount = Number(Value(item, "MontoItem"));
            if (name is null || quantity is not > 0m || price is null || amount is null)
            {
                continue;
            }

            var code = item.Descendants().FirstOrDefault(e => e.Name.LocalName == "CodigoItem")?.Value.Trim();
            lines.Add(new ReceivedEcfLine(
                int.TryParse(Value(item, "NumeroLinea"), NumberStyles.None, CultureInfo.InvariantCulture, out var no) && no > 0 ? no : lines.Count + 1,
                string.IsNullOrEmpty(code) ? null : Cut(code, 50),
                Cut(name, 500),
                quantity.Value,
                Value(item, "UnidadMedida") is { } unit ? Cut(unit, 20) : null,
                price.Value,
                amount.Value,
                int.TryParse(Value(item, "IndicadorFacturacion"), NumberStyles.None, CultureInfo.InvariantCulture, out var indicator) ? indicator : null));
        }

        var signature = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "SignatureValue")?.Value.Trim();
        return new ReceivedEcf(
            Value(idDoc, "TipoeCF") ?? encf.Substring(1, Math.Min(2, encf.Length - 1)),
            encf,
            issuerRnc,
            Value(issuer, "RazonSocialEmisor") is { } issuerName ? Cut(issuerName, 250) : null,
            Value(buyer, "RNCComprador"),
            DateOnly.TryParseExact(Value(issuer, "FechaEmision"), "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var issued) ? issued : null,
            Number(Value(totals, "MontoTotal")),
            Number(Value(totals, "TotalITBIS")),
            signature is { Length: >= 6 } ? signature[..6] : null,
            DateTime.TryParseExact(Value(root, "FechaHoraFirma"), "dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var signedLocal)
                ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(signedLocal, DateTimeKind.Unspecified), DominicanRepublic)
                : null,
            lines);
    }

    /// <summary>The XML as Alanube gives it: the document itself, or base64 of it; null when it is neither (a link is downloaded first).</summary>
    public static byte[]? Content(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        var text = xml.TrimStart();
        if (text.StartsWith('<'))
        {
            return Encoding.UTF8.GetBytes(xml);
        }

        try
        {
            var bytes = Convert.FromBase64String(text);
            return bytes.Length > 0 && Encoding.UTF8.GetString(bytes).TrimStart('﻿').TrimStart().StartsWith('<') ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static XElement? Child(XElement? parent, string name) => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static string? Value(XElement? parent, string name)
        => Child(parent, name)?.Value.Trim() is { Length: > 0 } text ? text : null;

    private static decimal? Number(string? text)
        => decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}
