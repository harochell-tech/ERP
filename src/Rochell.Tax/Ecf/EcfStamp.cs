using System.Globalization;
using System.Text.RegularExpressions;

namespace Rochell.Tax.Ecf;

/// <summary>What a printed e-CF's QR states (the DGII stamp link): issuer, buyer, e-NCF, dates, total and security code.</summary>
public sealed record EcfStamp(
    string Url, string IssuerRnc, string? BuyerRnc, string Encf, DateOnly? IssueDate, decimal? TotalAmount, DateTime? SignedAtUtc, string? SecurityCode);

/// <summary>
/// OCR1-03 (E-OCR1-03-7): reads the DGII stamp link a printed e-CF's QR carries — <c>https://ecf.dgii.gov.do/…/ConsultaTimbre?RncEmisor=…&amp;RncComprador=…&amp;ENCF=…&amp;FechaEmision=dd-MM-yyyy&amp;MontoTotal=…&amp;FechaFirma=dd-MM-yyyy HH:mm:ss&amp;CodigoSeguridad=…</c>
/// (consumer e-CF under the summary amount: <c>fc.dgii.gov.do</c>, without buyer). Null for anything that is not a DGII stamp link.
/// </summary>
public static partial class EcfStampUrl
{
    private static readonly TimeZoneInfo DominicanRepublic = TimeZoneInfo.FindSystemTimeZoneById("America/Santo_Domingo");

    [GeneratedRegex("^([0-9]{9}|[0-9]{11})$", RegexOptions.None, 1000)]
    private static partial Regex Rnc();

    [GeneratedRegex("^E[0-9]{12}$", RegexOptions.None, 1000)]
    private static partial Regex Encf();

    public static EcfStamp? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1000 || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host is not ("ecf.dgii.gov.do" or "fc.dgii.gov.do") || !uri.AbsolutePath.Contains("ConsultaTimbre", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = pair.IndexOf('=', StringComparison.Ordinal);
            if (at > 0)
            {
                values.TryAdd(Uri.UnescapeDataString(pair[..at]), Uri.UnescapeDataString(pair[(at + 1)..].Replace('+', ' ')).Trim());
            }
        }

        var issuer = values.GetValueOrDefault("RncEmisor");
        var encf = values.GetValueOrDefault("ENCF")?.ToUpperInvariant();
        var buyer = values.GetValueOrDefault("RncComprador");
        if (issuer is null || encf is null || !Rnc().IsMatch(issuer) || !Encf().IsMatch(encf) || (buyer is { Length: > 0 } && !Rnc().IsMatch(buyer)))
        {
            return null;
        }

        var code = values.GetValueOrDefault("CodigoSeguridad");
        return new EcfStamp(
            uri.AbsoluteUri,
            issuer,
            string.IsNullOrEmpty(buyer) ? null : buyer,
            encf,
            DateOnly.TryParseExact(values.GetValueOrDefault("FechaEmision"), "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var issued) ? issued : null,
            decimal.TryParse(values.GetValueOrDefault("MontoTotal"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var total) ? total : null,
            DateTime.TryParseExact(values.GetValueOrDefault("FechaFirma"), "dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var signed)
                ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(signed, DateTimeKind.Unspecified), DominicanRepublic)
                : null,
            code is { Length: >= 1 and <= 20 } ? code : null);
    }
}
