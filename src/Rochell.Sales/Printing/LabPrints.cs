using System.Globalization;
using System.Text.Json.Nodes;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Sales.Printing;

/// <summary>
/// LAB1-03 (E-LAB1-03-1, 7…10, 13): the lab's two printable documents. The certificate prints the snapshot the lab kept when it issued it
/// (`qa.certificate.snapshot`); the rack label reads the lot and its racks. Both are read through SQL — no module reference, like the
/// gate-out reads a lot's status (E-LAB1-02-11).
/// </summary>
public sealed record GetLabCertificatePrint(Guid CompanyId, Guid SessionId, Guid CertificateId, string? BaseUrl = null) : IQuery;

/// <summary>E-LAB1-03-9: the labels of a lot's racks, one per page; <paramref name="RackNo"/> prints only that rack's.</summary>
public sealed record GetRackLabelPrint(Guid CompanyId, Guid SessionId, Guid LotId, int? RackNo = null, string? BaseUrl = null) : IQuery;

[RequiresPermission("lab:read")]
public sealed class GetLabCertificatePrintHandler : IQueryHandler<GetLabCertificatePrint>
{
    public string QueryType => "Sales.GetLabCertificatePrint";

    public async Task<string> HandleAsync(GetLabCertificatePrint query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var printed = await PrintDocuments.RenderAsync(
            context, null, new GetPrintDocument(query.CompanyId, query.SessionId, PrintDocumentTypes.LabCertificate, query.CertificateId, BaseUrl: query.BaseUrl), cancellationToken).ConfigureAwait(false);
        return ApiJson.Serialize(printed);
    }
}

[RequiresPermission("production:read")]
public sealed class GetRackLabelPrintHandler : IQueryHandler<GetRackLabelPrint>
{
    public string QueryType => "Sales.GetRackLabelPrint";

    public async Task<string> HandleAsync(GetRackLabelPrint query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var printed = await PrintDocuments.RenderAsync(
            context, null, new GetPrintDocument(query.CompanyId, query.SessionId, PrintDocumentTypes.RackLabel, query.LotId, BaseUrl: query.BaseUrl, RackNo: query.RackNo), cancellationToken)
            .ConfigureAwait(false);
        return ApiJson.Serialize(printed);
    }
}

public static class LabPrints
{
    /// <summary>E-LAB1-03-8: the public page a certificate's QR opens (no sign-in).</summary>
    public static string VerifyPath(Guid companyId, string publicCode) => $"/verificar/certificado/?c={companyId:D}&k={publicCode}";

    /// <summary>
    /// E-LAB1-03-10: the lot in Core with its rack — the phone opens it (with a session); Dispatch's scanner takes the lot from it, and the
    /// code to show what was scanned.
    /// </summary>
    public static string LotPath(Guid lotId, int rackNo, string code)
        => string.Create(CultureInfo.InvariantCulture, $"/calidad/lotes/?lote={lotId:D}&rack={rackNo}&codigo={Uri.EscapeDataString(code)}");

    private static readonly Dictionary<string, string> Conditions = new(StringComparer.Ordinal)
    {
        ["SECO_AL_AIRE"] = "Seco al aire",
        ["HUMEDO"] = "Húmedo",
        ["SATURADO"] = "Saturado",
    };

    public sealed record CertificateRow(string Snapshot, string Status, string? VoidReason, string PublicCode);

    public sealed record RackLabelLot(Guid LotId, string Code, string ItemCode, string Item, DateOnly ProductionDate, string Machine, string? MachineShortCode, string Shift);

    public sealed record RackLabelRack(int RackNo, decimal Units);

    public static async Task<(string, IReadOnlyDictionary<string, object?>)> CertificateAsync(QueryContext context, GetPrintDocument query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        var row = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, "SELECT snapshot::text, status, void_reason, public_code FROM qa.certificate WHERE company_id = @c AND certificate_id = @id",
            r => new CertificateRow(r.GetString(0), r.GetString(1), r.NullableString(2), r.GetString(3)), cancellationToken, ("c", context.CompanyId), ("id", query.Id)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The certificate does not exist.");
        var snapshot = (JsonObject)JsonNode.Parse(row.Snapshot)!;
        return ($"Certificado {S(snapshot["certificateNo"])}", CertificateModel(snapshot, row.Status, Url(query.BaseUrl, VerifyPath(context.CompanyId, row.PublicCode))));
    }

    /// <summary>Baseline §4.6: the certificate's model from its snapshot; a voided one prints «ANULADO».</summary>
    public static IReadOnlyDictionary<string, object?> CertificateModel(JsonObject s, string status, string? verifyUrl)
    {
        ArgumentNullException.ThrowIfNull(s);
        var lot = (JsonObject)s["lot"]!;
        var delivery = s["delivery"] as JsonObject;
        var summary = (JsonObject)s["summary"]!;
        var equipment = (JsonObject)s["equipment"]!;
        var signer = (JsonObject)s["signer"]!;
        var produced = Date(S(lot["productionDate"]));
        var broken = Date(S(s["breakDate"]));
        var deliveryNo = delivery is null ? "—" : S(delivery["deliveryNo"]);
        string Measures(JsonObject sp) => $"{S(sp["widthCm"])} × {S(sp["heightCm"])} × {S(sp["lengthCm"])}" + (sp["nominalUsed"]?.GetValue<bool>() == true ? " (nominales)" : string.Empty);
        return new Dictionary<string, object?>
        {
            ["marca_agua"] = status == "VOIDED" ? "ANULADO" : null,
            ["emisor"] = new Dictionary<string, object?> { ["nombre"] = S(s["issuer"]?["name"]), ["rnc"] = S(s["issuer"]?["rnc"]) },
            ["certificado"] = new Dictionary<string, object?>
            {
                ["numero"] = S(s["certificateNo"]),
                ["emitido"] = DateTime.TryParse(S(s["issuedAt"]), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at) ? PrintText.DateTime(at) : "—",
                ["rotura"] = broken,
                ["estado"] = status == "VOIDED" ? "Anulado" : "Vigente",
            },
            ["lote"] = new Dictionary<string, object?>
            {
                ["codigo"] = S(lot["fieldCode"]),
                ["interno"] = S(lot["lotCode"]),
                ["producto"] = $"{S(lot["itemCode"])} — {S(lot["item"])}",
                ["maquina"] = lot["machineShortCode"] is { } sc ? $"{S(lot["machine"])} ({S(sc)})" : S(lot["machine"]),
                ["turno"] = S(lot["shift"]),
                ["produccion"] = produced,
                ["planta"] = S(lot["plant"]),
            },
            ["conduce"] = delivery is null ? null : new Dictionary<string, object?>
            {
                ["numero"] = deliveryNo,
                ["cliente"] = S(delivery["customer"]),
                ["rnc"] = delivery["customerRnc"] is { } rnc ? S(rnc) : null,
                ["obra"] = delivery["site"] is { } site ? S(site) : null,
            },
            ["firmante"] = new Dictionary<string, object?> { ["nombre"] = S(signer["name"]), ["cargo"] = signer["title"] is { } t ? S(t) : null },
            ["equipo"] = $"{S(equipment["brand"])} {S(equipment["model"])}, serie {S(equipment["serial"])}",
            ["emitido_por"] = S(s["issuedBy"]),
            ["lineas"] = ((JsonArray)s["specimens"]!).Select(n => (JsonObject)n!).Select(sp => (object?)new Dictionary<string, object?>
            {
                ["linea"] = S(sp["line"]),
                ["conduce"] = deliveryNo,
                ["medidas"] = Measures(sp),
                ["area"] = S(sp["areaCm2"]),
                ["tipo"] = S(lot["item"]),
                ["produccion"] = produced,
                ["rotura"] = broken,
                ["edad"] = S(sp["ageDays"]),
                ["peso"] = sp["weightKg"] is { } w ? S(w) : "—",
                ["carga"] = S(sp["loadKg"]),
                ["kgcm2"] = S(sp["strengthKgcm2"]),
                ["mpa"] = S(sp["strengthMpa"]),
            }).ToList(),
            ["resumen"] = new Dictionary<string, object?>
            {
                ["probetas"] = S(summary["specimens"]),
                ["promedio_kgcm2"] = S(summary["avgKgcm2"]),
                ["promedio_mpa"] = S(summary["avgMpa"]),
                ["minimo_kgcm2"] = S(summary["minKgcm2"]),
                ["minimo_mpa"] = S(summary["minMpa"]),
                ["cv"] = summary["cvPercent"] is { } cv ? S(cv) : null,
                ["condicion"] = summary["condition"] is { } c ? Conditions.GetValueOrDefault(S(c), S(c)) : "—",
                ["falla"] = summary["failure"] is { } f ? S(f) : "—",
            },
            ["absorcion"] = s["absorption"] is JsonObject a
                ? new Dictionary<string, object?> { ["bloques"] = S(a["blocks"]), ["absorcion"] = S(a["absorptionKgm3"]), ["densidad"] = S(a["densityKgm3"]) }
                : null,
            ["qr"] = verifyUrl is null ? null : new Dictionary<string, object?> { ["url"] = verifyUrl, ["svg"] = PrintDocuments.QrSvg(verifyUrl) },
        };
    }

    public static async Task<(string, IReadOnlyDictionary<string, object?>)> RackLabelAsync(QueryContext context, GetPrintDocument query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        var lot = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT f.lot_id, coalesce(f.field_code, l.lot_code), i.code, i.description, r.business_date, m.name, m.short_code, sh.code
            FROM mfg.fg_lot f JOIN inv.lot l ON l.lot_id = f.lot_id JOIN mfg.production_run r ON r.run_id = f.run_id JOIN md.item i ON i.item_id = r.item_id
            JOIN md.machine m ON m.machine_id = r.machine_id JOIN mfg.shift sh ON sh.shift_id = r.shift_id
            WHERE f.company_id = @c AND f.lot_id = @l AND f.status <> 'VOIDED'
            """,
            r => new RackLabelLot(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.Date(4), r.GetString(5), r.NullableString(6), r.GetString(7)),
            cancellationToken,
            ("c", context.CompanyId),
            ("l", query.Id)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The finished-goods lot does not exist.");
        var racks = await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT rack_no, units FROM mfg.rack WHERE company_id = @c AND lot_id = @l AND status <> 'VOIDED' ORDER BY rack_no",
            r => new RackLabelRack(r.GetInt32(0), r.GetDecimal(1)), cancellationToken, ("c", context.CompanyId), ("l", query.Id)).ConfigureAwait(false);
        if (query.RackNo is { } only && racks.All(r => r.RackNo != only))
        {
            throw new DomainException(QueryErrors.NotFound, "The lot has no such rack.");
        }

        var issuer = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, "SELECT legal_name FROM md.company WHERE company_id = @c", r => r.GetString(0), cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false);
        return ($"Etiquetas del lote {lot.Code}", RackLabelModel(issuer ?? string.Empty, lot, racks, query.RackNo, query.BaseUrl ?? string.Empty));
    }

    /// <summary>E-LAB1-03-9/10: one label per rack (or only <paramref name="rackNo"/>'s), each with the QR of the lot and its rack.</summary>
    public static IReadOnlyDictionary<string, object?> RackLabelModel(string issuer, RackLabelLot lot, IReadOnlyList<RackLabelRack> racks, int? rackNo, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(lot);
        ArgumentNullException.ThrowIfNull(racks);
        return new Dictionary<string, object?>
        {
            ["emisor"] = new Dictionary<string, object?> { ["nombre"] = issuer },
            ["etiquetas"] = racks.Where(r => rackNo is null || r.RackNo == rackNo).Select(r =>
            {
                var url = Url(baseUrl, LotPath(lot.LotId, r.RackNo, lot.Code))!;
                return (object?)new Dictionary<string, object?>
                {
                    ["codigo"] = lot.Code,
                    ["producto"] = $"{lot.ItemCode} — {lot.Item}",
                    ["fecha"] = PrintText.Date(lot.ProductionDate),
                    ["maquina"] = lot.MachineShortCode is null ? lot.Machine : $"{lot.Machine} ({lot.MachineShortCode})",
                    ["turno"] = lot.Shift,
                    ["rack"] = r.RackNo.ToString(CultureInfo.InvariantCulture),
                    ["racks"] = racks.Count.ToString(CultureInfo.InvariantCulture),
                    ["unidades"] = PrintText.Quantity(r.Units),
                    ["qr"] = new Dictionary<string, object?> { ["url"] = url, ["svg"] = PrintDocuments.QrSvg(url) },
                };
            }).ToList(),
        };
    }

    private static string? Url(string? baseUrl, string path) => baseUrl is null ? null : baseUrl.TrimEnd('/') + path;

    private static string Date(string iso) => PrintText.Date(DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture));

    private static string S(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue v when v.TryGetValue<string>(out var text) => text,
        _ => node.ToJsonString(),
    };
}
