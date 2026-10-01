using System.Globalization;
using System.Text;
using Rochell.Platform.Time;
using Rochell.Sales.Proformas;
using Rochell.Sales.Queries;

namespace Rochell.Sales.Mail;

/// <summary>The issuer printed at the top of every document.</summary>
public sealed record Issuer(string Name, string Rnc);

/// <summary>
/// E-MAIL-3, E-MAIL-01-11: the documents sent by e-mail as self-contained HTML — no scripts, no external resources — with the same
/// content as the browser's print views. The renderer turns it into the letter-size PDF that is attached. Every value is
/// HTML-encoded; amounts and quantities are the server's, formatted as the screens do (1,234.00; dd/mm/yyyy).
/// </summary>
public static class DocumentHtml
{
    private const string Css =
        """
        *{box-sizing:border-box}body{font-family:Helvetica,Arial,sans-serif;font-size:11pt;color:#1a1a1a;margin:0}
        header{display:flex;justify-content:space-between;align-items:flex-start;border-bottom:2px solid #0a3d73;padding-bottom:10px;margin-bottom:14px}
        header .issuer{font-size:13pt;font-weight:bold}header .doc{text-align:right}h1{font-size:16pt;margin:0 0 4px;color:#0a3d73}h2{font-size:12pt;margin:18px 0 6px}
        dl{display:grid;grid-template-columns:150px 1fr;gap:3px 10px;margin:0 0 14px}dt{color:#555}dd{margin:0}
        table{width:100%;border-collapse:collapse;margin:6px 0 12px}th,td{border-bottom:1px solid #ccc;padding:5px 6px;text-align:left;vertical-align:top}
        thead th{background:#eef3fa;border-bottom:1px solid #0a3d73;font-size:10pt}.num{text-align:right;white-space:nowrap}.mono{font-family:Courier,monospace}
        tr.total th,tr.total td{border-top:1px solid #0a3d73;font-weight:bold}.muted{color:#555;font-size:9.5pt}
        .signature{display:flex;gap:40px;margin-top:46px}.signature div{flex:1;text-align:center;font-size:10pt}.signature .line{border-top:1px solid #1a1a1a;margin-bottom:4px}
        .signature .box{border:1px solid #1a1a1a;height:70px;margin-bottom:4px}tr{page-break-inside:avoid}
        """;

    private static readonly Dictionary<string, string> DeliveryTerms = new(StringComparer.Ordinal)
    {
        ["PICKUP_AT_PLANT"] = "Retira en planta",
        ["DELIVERED_OWN_TRANSPORT"] = "Entregado en obra (camión propio)",
    };

    private static readonly Dictionary<string, string> StatementKinds = new(StringComparer.Ordinal)
    {
        ["FACTURA"] = "Factura",
        ["FACTURA_ANULADA"] = "Factura anulada",
        ["NOTA_DE_CREDITO"] = "Nota de crédito",
        ["COBRO"] = "Cobro",
        ["COBRO_ANULADO"] = "Cobro anulado",
        ["CHEQUE_DEVUELTO"] = "Cheque devuelto",
        ["RETENCION"] = "Retención",
        ["RETENCION_REVERSADA"] = "Retención reversada",
        ["DEVOLUCION"] = "Devolución al cliente",
        ["OTRO"] = "Otro",
    };

    public static string Money(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);

    public static string Quantity(decimal value) => value.ToString("#,##0.######", CultureInfo.InvariantCulture);

    public static string Date(DateOnly value) => value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>A UTC instant as the business day and time of America/Santo_Domingo.</summary>
    public static string LocalDateTime(DateTime utc)
    {
        var day = BusinessCalendar.DefaultBusinessDate(utc);
        var time = utc - BusinessCalendar.DayUtcRange(day).StartUtc;
        return $"{Date(day)} {(int)time.TotalHours:00}:{time.Minutes:00}";
    }

    /// <summary>Escapes what HTML reserves; accents stay as they are (the document is UTF-8).</summary>
    private static string E(string? value)
        => (value ?? string.Empty).Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal).Replace("'", "&#39;", StringComparison.Ordinal);

    private static string Term(string code, string? site) => E(DeliveryTerms.GetValueOrDefault(code, code)) + (string.IsNullOrWhiteSpace(site) ? string.Empty : " · obra: " + E(site));

    private static StringBuilder Open(string title, Issuer issuer, string heading, string under)
        => new StringBuilder()
            .Append("<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\"><title>").Append(E(title)).Append("</title><style>").Append(Css).Append("</style></head><body>")
            .Append("<header><div><div class=\"issuer\">").Append(E(issuer.Name)).Append("</div><div>RNC <span class=\"mono\">").Append(E(issuer.Rnc)).Append("</span></div></div>")
            .Append("<div class=\"doc\"><h1>").Append(E(heading)).Append("</h1><div>").Append(under).Append("</div></div></header>");

    private static string Close(StringBuilder html) => html.Append("</body></html>").ToString();

    private static StringBuilder Fact(this StringBuilder html, string label, string valueHtml) => html.Append("<dt>").Append(E(label)).Append("</dt><dd>").Append(valueHtml).Append("</dd>");

    private static StringBuilder Head(this StringBuilder html, params (string Label, bool Numeric)[] columns)
    {
        html.Append("<table><thead><tr>");
        foreach (var (label, numeric) in columns)
        {
            html.Append(numeric ? "<th class=\"num\">" : "<th>").Append(E(label)).Append("</th>");
        }

        return html.Append("</tr></thead><tbody>");
    }

    private static StringBuilder Cell(this StringBuilder html, string valueHtml, bool numeric = false, bool mono = false)
        => html.Append(numeric ? "<td class=\"num\">" : mono ? "<td class=\"mono\">" : "<td>").Append(valueHtml).Append("</td>");

    private static StringBuilder Signature(this StringBuilder html, string left, string right, bool boxes)
    {
        var mark = boxes ? "box" : "line";
        return html.Append("<div class=\"signature\"><div><div class=\"").Append(mark).Append("\"></div>").Append(E(left)).Append("</div><div><div class=\"box\"></div>").Append(E(right)).Append("</div></div>");
    }

    private static StringBuilder PricedLines(this StringBuilder html, IEnumerable<(int No, string Code, string Name, string Uom, decimal Quantity, decimal Price, decimal Net, decimal Itbis, decimal Total)> lines, decimal net, decimal itbis, decimal total)
    {
        html.Head(("#", true), ("Producto", false), ("Unidad", false), ("Cantidad", true), ("Precio (RD$)", true), ("Neto (RD$)", true), ("ITBIS (RD$)", true), ("Total (RD$)", true));
        foreach (var l in lines)
        {
            html.Append("<tr>").Cell(l.No.ToString(CultureInfo.InvariantCulture), numeric: true).Cell(E(l.Code) + " — " + E(l.Name)).Cell(E(l.Uom)).Cell(Quantity(l.Quantity), numeric: true)
                .Cell(Money(l.Price), numeric: true).Cell(Money(l.Net), numeric: true).Cell(Money(l.Itbis), numeric: true).Cell(Money(l.Total), numeric: true).Append("</tr>");
        }

        return html.Append("<tr class=\"total\"><th colspan=\"5\">Totales (RD$)</th>").Cell(Money(net), numeric: true).Cell(Money(itbis), numeric: true).Cell(Money(total), numeric: true)
            .Append("</tr></tbody></table>");
    }

    /// <summary>The quote the customer receives (E-QUO1-12), as its print view.</summary>
    public static string Quote(QuotePrint q)
    {
        ArgumentNullException.ThrowIfNull(q);
        var html = Open($"Cotización {q.QuoteNo}", new Issuer(q.IssuerName, q.IssuerRnc), $"Cotización {q.QuoteNo}", $"Fecha {Date(q.QuoteDate)} · válida hasta el {Date(q.ValidUntil)}");
        html.Append("<dl>").Fact("Cliente", E(q.CustomerName) + (string.IsNullOrWhiteSpace(q.CustomerRnc) ? string.Empty : " · RNC <span class=\"mono\">" + E(q.CustomerRnc) + "</span>"));
        if (!string.IsNullOrWhiteSpace(q.CustomerRef))
        {
            html.Fact("Su referencia", E(q.CustomerRef));
        }

        html.Fact("Entrega", Term(q.DeliveryTermCode, q.SiteAddress)).Append("</dl>");
        html.PricedLines(q.Lines.Select(l => (l.LineNo, l.ItemCode, l.ItemName, l.Uom, l.Quantity, l.UnitPrice, l.Net, l.Itbis, l.Total)), q.NetTotal, q.ItbisTotal, q.Total);
        if (!string.IsNullOrWhiteSpace(q.Notes))
        {
            html.Append("<p><strong>Notas:</strong> ").Append(E(q.Notes)).Append("</p>");
        }

        html.Append("<h2>Condiciones generales</h2><p class=\"muted\">Precios en pesos dominicanos, sin ITBIS salvo indicación; el ITBIS mostrado es informativo, calculado con las reglas vigentes a la fecha de la cotización. Sujeto a disponibilidad. Documento no fiscal.</p>");
        return Close(html.Signature("Por el suplidor", "Aceptado por el cliente (nombre, firma y fecha)", boxes: false));
    }

    /// <summary>The proforma of a delivery (E-FIS1b-01-13): the document the customer takes to the DGII and pays against.</summary>
    public static string Proforma(ProformaDetail p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var f = p.Header;
        var html = Open($"Proforma {f.ProformaNo}", new Issuer(p.IssuerName, p.IssuerRnc), $"Proforma {f.ProformaNo}", $"Entregado el {Date(f.ProformaDate)} · vence el {Date(f.DueDate)}");
        html.Append("<dl>").Fact("Cliente", E(f.CustomerName) + " · RNC <span class=\"mono\">" + E(f.CustomerRnc ?? "—") + "</span>")
            .Fact("Conduce", "<span class=\"mono\">" + E(f.DeliveryNo) + "</span> · pedido <span class=\"mono\">" + E(f.OrderNo) + "</span>");
        if (!string.IsNullOrWhiteSpace(p.SiteAddress))
        {
            html.Fact("Obra", E(p.SiteAddress));
        }

        html.Append("</dl>");
        html.PricedLines(p.Lines.Select(l => (l.LineNo, l.ItemCode, l.ItemName, l.Uom, l.Quantity, l.UnitPrice, l.Net, l.Itbis, l.Total)), f.Net, f.Itbis, f.Total);
        html.Append("<p class=\"muted\">Documento no fiscal. El ITBIS se calculó con la regla vigente el día de la entrega; el comprobante fiscal se emite cuando la DGII resuelva la exención.</p>");
        return Close(html.Signature("Firma del suplidor", "Sello del suplidor", boxes: false));
    }

    /// <summary>The delivery note (E-UX3-7), after the gate-out.</summary>
    public static string Delivery(DeliveryPrint d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var html = Open($"Conduce {d.DeliveryNo}", new Issuer(d.IssuerName, d.IssuerRnc), $"Conduce {d.DeliveryNo}", $"Pedido <span class=\"mono\">{E(d.OrderNo)}</span> del {Date(d.OrderDate)}");
        var plate = d.VehiclePlate ?? d.CustomerVehiclePlate;
        var driver = d.DriverName ?? d.CustomerDriverName;
        html.Append("<dl>").Fact("Cliente", E(d.CustomerName) + " · RNC <span class=\"mono\">" + E(d.CustomerRnc) + "</span>")
            .Fact("Planta", E(d.PlantName is null ? d.PlantCode : $"{d.PlantName} ({d.PlantCode})"))
            .Fact("Entrega", Term(d.DeliveryTermCode, d.SiteAddress))
            .Fact("Salida por portería", d.GateOutAt is { } gate ? LocalDateTime(gate) : "Todavía no ha salido")
            .Fact("Vehículo y chofer", (plate is null ? "—" : "Placa " + E(plate)) + (driver is null ? string.Empty : " · " + E(driver)) + (d.CustomerVehiclePlate is null ? string.Empty : " (del cliente)"))
            .Fact("Pesada", d.GrossKg is { } gross
                ? $"Bruto {Quantity(gross)} kg · tara {Quantity(d.TareKg ?? 0m)} kg · neto {Quantity(d.NetKg ?? 0m)} kg" + (d.WeighTicketRef is null ? string.Empty : " · ticket " + E(d.WeighTicketRef))
                : "Sin pesar")
            .Append("</dl>");
        html.Head(("#", true), ("Producto", false), ("Unidad", false), ("Planificado", true), ("Despachado", true), ("Entregado", true), ("Lotes", false));
        foreach (var l in d.Lines)
        {
            html.Append("<tr>").Cell(l.LineNo.ToString(CultureInfo.InvariantCulture), numeric: true).Cell(E(l.ItemCode) + " — " + E(l.ItemDescription)).Cell(E(l.Uom))
                .Cell(Quantity(l.QtyPlanned), numeric: true).Cell(Quantity(l.QtyIssued), numeric: true).Cell(Quantity(l.QtyDelivered), numeric: true)
                .Cell(l.Lots.Count == 0 ? "—" : string.Join(" · ", l.Lots.Select(lot => $"{E(lot.LotCode)} ({E(lot.SourceLocationCode)}): {Quantity(lot.BaseQuantity)}"))).Append("</tr>");
        }

        html.Append("</tbody></table>");
        if (d.ReceivedByName is not null && d.ReceivedAt is { } received)
        {
            html.Append("<p>Recibido por ").Append(E(d.ReceivedByName)).Append(" el ").Append(LocalDateTime(received)).Append(".</p>");
        }

        return Close(html.Signature("Despachado por", "Recibido por (nombre, cédula, firma)", boxes: true));
    }

    /// <summary>The customer's statement of account (E-VS3-09-3), with today's open proformas apart (E-FIS1b-3).</summary>
    public static string Statement(CustomerStatement s, Issuer issuer)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(issuer);
        var html = Open($"Estado de cuenta {s.CustomerName}", issuer, "Estado de cuenta", $"Del {Date(s.From)} al {Date(s.To)}");
        html.Append("<dl>").Fact("Cliente", E(s.CustomerName) + " · RNC <span class=\"mono\">" + E(s.Rnc ?? "—") + "</span>").Append("</dl>");
        html.Head(("Fecha", false), ("Tipo", false), ("Documento", false), ("Débito (RD$)", true), ("Crédito (RD$)", true), ("Saldo (RD$)", true));
        html.Append("<tr>").Cell(Date(s.From)).Append("<td colspan=\"4\">Saldo inicial</td>").Cell(Money(s.Opening), numeric: true).Append("</tr>");
        foreach (var e in s.Entries)
        {
            html.Append("<tr>").Cell(Date(e.PostingDate)).Cell(E(StatementKinds.GetValueOrDefault(e.Kind, e.Kind))).Cell(E(e.DocumentNo ?? "—"), mono: true)
                .Cell(Money(e.Debit), numeric: true).Cell(Money(e.Credit), numeric: true).Cell(Money(e.Balance), numeric: true).Append("</tr>");
        }

        html.Append("<tr class=\"total\"><th colspan=\"3\">Saldo final</th>").Cell(Money(s.TotalDebit), numeric: true).Cell(Money(s.TotalCredit), numeric: true).Cell(Money(s.Closing), numeric: true)
            .Append("</tr></tbody></table>");
        if (s.OpenProformas.Count > 0)
        {
            html.Append("<h2>Proformas abiertas</h2><p class=\"muted\">Entregas que esperan su comprobante fiscal. No forman parte del saldo de arriba; se cobran contra la proforma.</p>");
            html.Head(("Proforma", false), ("Conduce", false), ("Fecha", false), ("Vence", false), ("Total (RD$)", true), ("Cobrado (RD$)", true), ("Saldo (RD$)", true));
            foreach (var f in s.OpenProformas)
            {
                html.Append("<tr>").Cell(E(f.ProformaNo), mono: true).Cell(E(f.DeliveryNo), mono: true).Cell(Date(f.ProformaDate)).Cell(Date(f.DueDate))
                    .Cell(Money(f.Total), numeric: true).Cell(Money(f.Allocated), numeric: true).Cell(Money(f.Balance), numeric: true).Append("</tr>");
            }

            html.Append("<tr class=\"total\"><th colspan=\"6\">Saldo en proformas</th>").Cell(Money(s.ProformaBalance), numeric: true).Append("</tr></tbody></table>");
        }

        return Close(html.Append("<p class=\"muted\">Si encuentra alguna diferencia con sus registros, responda a este correo.</p>"));
    }

    /// <summary>The customer's open invoices and proformas by age (E-VS3-09-1, E-FIS1b-3): what is due and since when.</summary>
    public static string Aging(ArAgingCustomer c, DateOnly asOf, ArAgingBuckets buckets, Issuer issuer)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(buckets);
        ArgumentNullException.ThrowIfNull(issuer);
        var html = Open($"Cuentas por cobrar {c.CustomerName}", issuer, "Facturas pendientes", $"Al {Date(asOf)}");
        html.Append("<dl>").Fact("Cliente", E(c.CustomerName)).Append("</dl>");
        html.Head(("Factura", false), ("e-NCF", false), ("Fecha", false), ("Vence", false), ("Días vencida", true), ("Pendiente (RD$)", true));
        foreach (var d in c.Documents)
        {
            html.Append("<tr>").Cell(E(d.InvoiceNo), mono: true).Cell(E(d.Encf ?? "—"), mono: true).Cell(Date(d.DocDate)).Cell(Date(d.DueDate))
                .Cell(d.DaysOverdue > 0 ? d.DaysOverdue.ToString(CultureInfo.InvariantCulture) : "Al día", numeric: true).Cell(Money(d.OpenAmount), numeric: true).Append("</tr>");
        }

        if (c.Documents.Count == 0)
        {
            html.Append("<tr><td colspan=\"6\">Sin facturas pendientes.</td></tr>");
        }

        html.Append("<tr class=\"total\"><th colspan=\"5\">Total pendiente</th>").Cell(Money(c.Total), numeric: true).Append("</tr></tbody></table>");
        html.Head(("Al día (RD$)", true), ($"1–{buckets.Bucket1Days} días", true), ($"{buckets.Bucket1Days + 1}–{buckets.Bucket2Days} días", true), ($"{buckets.Bucket2Days + 1}–{buckets.Bucket3Days} días", true),
            ($"Más de {buckets.Bucket3Days} días", true), ("A su favor (RD$)", true), ("Neto (RD$)", true));
        html.Append("<tr>").Cell(Money(c.Current), numeric: true).Cell(Money(c.Bucket1), numeric: true).Cell(Money(c.Bucket2), numeric: true).Cell(Money(c.Bucket3), numeric: true)
            .Cell(Money(c.Over), numeric: true).Cell(Money(c.Unapplied), numeric: true).Cell(Money(c.Net), numeric: true).Append("</tr></tbody></table>");
        if (c.ProformaDocuments.Count > 0)
        {
            html.Append("<h2>Proformas abiertas</h2><p class=\"muted\">Entregas que esperan su comprobante fiscal; se cobran contra la proforma.</p>");
            html.Head(("Proforma", false), ("Conduce", false), ("Fecha", false), ("Vence", false), ("Días vencida", true), ("Saldo (RD$)", true));
            foreach (var f in c.ProformaDocuments)
            {
                html.Append("<tr>").Cell(E(f.ProformaNo), mono: true).Cell(E(f.DeliveryNo), mono: true).Cell(Date(f.ProformaDate)).Cell(Date(f.DueDate))
                    .Cell(f.DaysOverdue > 0 ? f.DaysOverdue.ToString(CultureInfo.InvariantCulture) : "Al día", numeric: true).Cell(Money(f.Balance), numeric: true).Append("</tr>");
            }

            html.Append("<tr class=\"total\"><th colspan=\"5\">Saldo en proformas</th>").Cell(Money(c.Proformas), numeric: true).Append("</tr></tbody></table>");
        }

        return Close(html.Append("<p class=\"muted\">Si ya realizó el pago, responda a este correo con el comprobante.</p>"));
    }
}
