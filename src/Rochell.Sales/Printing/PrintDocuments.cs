using System.Globalization;
using System.Text;
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

/// <summary>PRT-01 (E-PRT-2): the documents that print. PRT-03 adds the credit note, the receipt, the refund and the purchase order.</summary>
public static class PrintDocumentTypes
{
    public const string DeliveryNote = "DELIVERY_NOTE";
    public const string Invoice = "INVOICE";
    public const string Quote = "QUOTE";
    public const string Proforma = "PROFORMA";
    public const string OrderProforma = "ORDER_PROFORMA";
    public const string Statement = "STATEMENT";
    public const string ArAging = "AR_AGING";

    public static readonly IReadOnlyList<string> All = [DeliveryNote, Invoice, Quote, Proforma, OrderProforma, Statement, ArAging];
}

/// <summary>
/// PRT-01 (E-PRT-01-1…4, 7, E-ENT-9): a document as it prints — the company's ACTIVE format of its type, or the built-in «Rochell»
/// one — drawn by the server from the same print queries the screens and the e-mails use. The QR codes are drawn here too.
/// </summary>
/// <remarks><paramref name="BaseUrl"/> is the address the screen was opened at; without it (the e-mail) no driver's QR prints.</remarks>
public sealed record GetPrintDocument(Guid CompanyId, Guid SessionId, string DocumentType, Guid Id, DateOnly? From = null, DateOnly? To = null, string? BaseUrl = null) : IQuery;

public sealed record PrintedDocument(string DocumentType, int FormatVersion, string Title, string Html, string Css, string Body);

[RequiresPermission("sales:read")]
public sealed class GetPrintDocumentHandler(DriverLinkKey? key = null) : IQueryHandler<GetPrintDocument>
{
    public string QueryType => "Sales.GetPrintDocument";

    public async Task<string> HandleAsync(GetPrintDocument query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var printed = await PrintDocuments.RenderAsync(context, key, query, cancellationToken).ConfigureAwait(false);
        return ApiJson.Serialize(printed);
    }
}

public static class PrintDocuments
{
    /// <summary>A format to render with: its body, its own CSS, its settings (JSON) and its version (0: built-in).</summary>
    public sealed record Format(string Body, string Css, string Settings, int Version);

    private sealed record Logo(byte[] Content, string ContentType);

    private sealed record Company(string LegalName, string Rnc);

    private sealed record LinkRow(Guid DeliveryId, string DeliveryNo, int Generation);

    private static readonly Dictionary<string, string> DeliveryStatuses = new(StringComparer.Ordinal)
    {
        ["PICKUP_AT_PLANT"] = "Retira en planta",
        ["DELIVERED_OWN_TRANSPORT"] = "Entregado en obra (camión propio)",
    };

    private static readonly Dictionary<string, string> EcfTitles = new(StringComparer.Ordinal)
    {
        ["31"] = "Factura de Crédito fiscal",
        ["32"] = "Factura de Consumo",
        ["34"] = "Nota de crédito",
        ["44"] = "Factura de Régimen especial",
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

    /// <summary>Renders a document inside a command (the e-mail's snapshot), with the command's transaction.</summary>
    public static Task<PrintedDocument> RenderAsync(CommandContext context, GetPrintDocument query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return RenderAsync(new QueryContext(context.Connection, context.Transaction, context.CompanyId, context.SessionId, context.Clock), null, query, cancellationToken);
    }

    public static async Task<PrintedDocument> RenderAsync(
        QueryContext context, DriverLinkKey? key, GetPrintDocument query, CancellationToken cancellationToken, Format? format = null, string? watermark = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        var (title, model) = query.DocumentType switch
        {
            PrintDocumentTypes.DeliveryNote => await DeliveryAsync(context, key, query, cancellationToken).ConfigureAwait(false),
            PrintDocumentTypes.Quote => await QuoteAsync(context, query, cancellationToken).ConfigureAwait(false),
            PrintDocumentTypes.Invoice => await InvoiceAsync(context, key, query, cancellationToken).ConfigureAwait(false),
            PrintDocumentTypes.Proforma => await ProformaAsync(context, query, cancellationToken).ConfigureAwait(false),
            PrintDocumentTypes.OrderProforma => await OrderProformaAsync(context, query, cancellationToken).ConfigureAwait(false),
            PrintDocumentTypes.Statement => await StatementAsync(context, query, cancellationToken).ConfigureAwait(false),
            PrintDocumentTypes.ArAging => await AgingAsync(context, query, cancellationToken).ConfigureAwait(false),
            _ => throw new DomainException(QueryErrors.InvalidParameter, $"Unknown document type {query.DocumentType}."),
        };
        format ??= await FormatAsync(context, query.DocumentType, cancellationToken).ConfigureAwait(false);
        return Render(query.DocumentType, title, model, format, await LogoAsync(context, cancellationToken).ConfigureAwait(false), watermark);
    }

    /// <summary>E-PRT-8: the company's logo as a data URI, or null.</summary>
    public static async Task<string?> LogoAsync(QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var logo = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, "SELECT content, content_type FROM md.company_logo WHERE company_id = @c", r => new Logo(r.GetFieldValue<byte[]>(0), r.GetString(1)),
            cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false);
        return logo is null ? null : $"data:{logo.ContentType};base64,{Convert.ToBase64String(logo.Content)}";
    }

    /// <summary>
    /// E-PRT-02-1: a model drawn with a format — the settings add the visible columns (title, alignment, width), the fixed texts and the
    /// logo; their CSS goes before the format's own. <paramref name="watermark"/> replaces the document's (the preview's «VISTA PREVIA»).
    /// </summary>
    public static PrintedDocument Render(string documentType, string title, IReadOnlyDictionary<string, object?> model, Format format, string? logoDataUri, string? watermark = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(format);
        var settings = PrintFormatRules.Parse(documentType, format.Settings);
        var catalogue = PrintFormatRules.Catalogue(documentType);
        var columns = settings.Columns.Where(c => c.Show && catalogue.Any(k => k.Key == c.Key)).Select(c => (object?)new Dictionary<string, object?>
        {
            ["clave"] = c.Key,
            ["titulo"] = c.Title,
            ["clase"] = catalogue.First(k => k.Key == c.Key).Numeric ? "num" : string.Empty,
            ["estilo"] = (c.WidthPercent is { } w ? string.Create(CultureInfo.InvariantCulture, $"width:{w}%;") : string.Empty)
                + $"text-align:{c.Align switch { "DERECHA" => "right", "CENTRO" => "center", _ => "left" }}",
        }).ToList();
        var decorated = new Dictionary<string, object?>(model, StringComparer.Ordinal)
        {
            ["formato"] = new Dictionary<string, object?>
            {
                ["columnas"] = columns,
                ["colspan_totales"] = I(Math.Max(1, columns.Count - 1)),
                ["papel"] = settings.Paper,
                ["textos"] = new Dictionary<string, object?>
                {
                    ["encabezado"] = settings.Texts.Header,
                    ["pie"] = settings.Texts.Footer,
                    ["condiciones"] = settings.Texts.Conditions,
                    ["cuentas"] = settings.Texts.BankAccounts,
                },
            },
            ["logo"] = settings.ShowLogo && logoDataUri is not null ? new Dictionary<string, object?> { ["src"] = logoDataUri } : null,
        };
        if (watermark is not null)
        {
            decorated["marca_agua"] = watermark;
        }

        var rendered = PrintRenderer.Render(new PrintTemplate(format.Body, PrintFormatRules.Css(settings) + "\n" + format.Css), title, decorated);
        return new PrintedDocument(documentType, format.Version, rendered.Title, rendered.Html, rendered.Css, rendered.Body);
    }

    /// <summary>E-PRT-01-4: the company's ACTIVE version, else the built-in «Rochell» format (version 0).</summary>
    public static async Task<Format> FormatAsync(QueryContext context, string documentType, CancellationToken cancellationToken)
        => await Reading.SingleOrDefaultAsync(
               context.Connection,
               context.Transaction,
               "SELECT body, css, settings::text, version FROM md.print_format WHERE company_id = @c AND document_type = @t AND status = 'ACTIVE'",
               r => new Format(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3)),
               cancellationToken,
               ("c", context.CompanyId),
               ("t", documentType)).ConfigureAwait(false)
           ?? new Format(BuiltIn(documentType), string.Empty, "{}", 0);

    /// <summary>The built-in «Rochell» format of a document type (E-PRT-01-4).</summary>
    public static string BuiltIn(string documentType)
    {
        using var stream = typeof(PrintDocuments).Assembly.GetManifestResourceStream($"Rochell.Sales.Printing.Templates.{documentType}.liquid")
            ?? throw new DomainException(QueryErrors.InvalidParameter, $"Unknown document type {documentType}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static async Task<TResult> ReadAsync<TQuery, TResult>(QueryContext context, IQueryHandler<TQuery> handler, TQuery query, CancellationToken cancellationToken)
        where TQuery : IQuery
        => JsonSerializer.Deserialize<TResult>(await handler.HandleAsync(query, context, cancellationToken).ConfigureAwait(false), ApiJson.Options)!;

    private static async Task<Company> CompanyAsync(QueryContext context, CancellationToken cancellationToken)
        => (await Reading.SingleOrDefaultAsync(
               context.Connection, context.Transaction, "SELECT legal_name, rnc FROM md.company WHERE company_id = @c", r => new Company(r.GetString(0), r.GetString(1)), cancellationToken,
               ("c", context.CompanyId)).ConfigureAwait(false))!;

    private static Dictionary<string, object?> Issuer(string name, string rnc) => new() { ["nombre"] = name, ["rnc"] = rnc };

    private static string M(decimal value) => PrintText.Money(value);

    private static string Q(decimal value) => PrintText.Quantity(value);

    private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A QR as inline SVG (E-PRT-01-7): the driver's link or the DGII stamp.</summary>
    public static string QrSvg(string url)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M);
        return new QRCoder.SvgQRCode(data).GetGraphic(new System.Drawing.Size(120, 120), "#000000", "#ffffff", drawQuietZones: true, sizingMode: QRCoder.SvgQRCode.SizingMode.ViewBoxAttribute);
    }

    private static Dictionary<string, object?>? DriverQr(string? baseUrl, string? path)
    {
        if (baseUrl is null || path is null)
        {
            return null;
        }

        var url = baseUrl.TrimEnd('/') + path;
        return new Dictionary<string, object?> { ["url"] = url, ["svg"] = QrSvg(url) };
    }

    private static async Task<(string, IReadOnlyDictionary<string, object?>)> DeliveryAsync(QueryContext context, DriverLinkKey? key, GetPrintDocument query, CancellationToken cancellationToken)
    {
        var d = await ReadAsync<GetDeliveryPrint, DeliveryPrint>(context, new GetDeliveryPrintHandler(key), new GetDeliveryPrint(context.CompanyId, context.SessionId, query.Id), cancellationToken)
            .ConfigureAwait(false);
        return ($"Conduce {d.DeliveryNo}", DeliveryModel(d, query.BaseUrl));
    }

    private static Dictionary<string, object?> Total(string value, string testId) => new() { ["valor"] = value, ["testid"] = testId };

    /// <summary>The conduce's model (E-UX3-7, ENT-1); the driver's QR only with the screen's address.</summary>
    public static IReadOnlyDictionary<string, object?> DeliveryModel(DeliveryPrint d, string? baseUrl)
    {
        ArgumentNullException.ThrowIfNull(d);
        var lines = new List<object?>();
        foreach (var l in d.Lines)
        {
            lines.Add(new Dictionary<string, object?>
            {
                ["linea"] = I(l.LineNo),
                ["producto"] = $"{l.ItemCode} — {l.ItemDescription}",
                ["unidad"] = l.Uom,
                ["planificado"] = Q(l.QtyPlanned),
                ["despachado"] = Q(l.QtyIssued),
                ["entregado"] = Q(l.QtyDelivered),
                ["lotes"] = l.Lots.Count == 0 ? "—" : string.Join(" · ", l.Lots.Select(x => $"{x.LotCode} ({x.SourceLocationCode}): {Q(x.BaseQuantity)}")),
            });
            if (l.Freight is { } freight)
            {
                // PRS-05 (E-PRS-04-7): the line's freight, same quantities, no price.
                lines.Add(new Dictionary<string, object?>
                {
                    ["testid"] = $"conduce-freight:{I(l.LineNo)}",
                    ["linea"] = string.Empty,
                    ["producto"] = freight,
                    ["unidad"] = l.Uom,
                    ["planificado"] = Q(l.QtyPlanned),
                    ["despachado"] = Q(l.QtyIssued),
                    ["entregado"] = Q(l.QtyDelivered),
                    ["lotes"] = "—",
                });
            }
        }

        return new Dictionary<string, object?>
        {
            ["marca_agua"] = d.GateOutAt is null ? "BORRADOR – NO DESPACHADO" : null,
            ["emisor"] = Issuer(d.IssuerName, d.IssuerRnc),
            ["cliente"] = new Dictionary<string, object?> { ["nombre"] = d.CustomerName, ["rnc"] = d.CustomerRnc },
            ["conduce"] = new Dictionary<string, object?>
            {
                ["numero"] = d.DeliveryNo,
                ["pedido"] = d.OrderNo,
                ["fecha_pedido"] = PrintText.Date(d.OrderDate),
                ["planta"] = d.PlantName is null ? d.PlantCode : $"{d.PlantName} ({d.PlantCode})",
                ["termino"] = DeliveryStatuses.GetValueOrDefault(d.DeliveryTermCode, d.DeliveryTermCode),
                ["obra"] = d.SiteAddress,
                ["planificado"] = PrintText.Date(d.PlannedOn),
                ["salida"] = d.GateOutAt is { } g ? PrintText.DateTime(g) : "Todavía no ha salido",
            },
            ["transporte"] = new Dictionary<string, object?>
            {
                ["ficha"] = d.VehicleFleetCode,
                ["placa"] = d.VehiclePlate ?? d.CustomerVehiclePlate,
                ["chofer"] = d.DriverName ?? d.CustomerDriverName,
                ["del_cliente"] = d.CustomerVehiclePlate is not null,
            },
            ["pesada"] = d.GrossKg is { } gross
                ? new Dictionary<string, object?> { ["bruto"] = Q(gross), ["tara"] = Q(d.TareKg ?? 0m), ["neto"] = Q(d.NetKg ?? 0m), ["ticket"] = d.WeighTicketRef }
                : null,
            ["lineas"] = lines,
            ["recibido"] = d.ReceivedByName is null ? null : new Dictionary<string, object?> { ["nombre"] = d.ReceivedByName, ["fecha"] = PrintText.DateTime(d.ReceivedAt) },
            ["qr_chofer"] = DriverQr(baseUrl, d.DriverLinkPath),
        };
    }

    private static async Task<(string, IReadOnlyDictionary<string, object?>)> QuoteAsync(QueryContext context, GetPrintDocument query, CancellationToken cancellationToken)
    {
        var q = await ReadAsync<GetQuotePrint, QuotePrint>(context, new GetQuotePrintHandler(), new GetQuotePrint(context.CompanyId, context.SessionId, query.Id), cancellationToken).ConfigureAwait(false);
        return ($"Cotización {q.QuoteNo}", QuoteModel(q));
    }

    /// <summary>A priced line as the quote and both proformas print it.</summary>
    private static Dictionary<string, object?> PricedLine(int no, string code, string name, string uom, decimal quantity, decimal price, decimal net, decimal itbis, decimal total, bool exempt = false)
        => new()
        {
            ["linea"] = I(no),
            ["producto"] = $"{code} — {name}{(exempt ? " (exento)" : string.Empty)}",
            ["unidad"] = uom,
            ["cantidad"] = Q(quantity),
            ["precio"] = M(price),
            ["neto"] = M(net),
            ["itbis"] = M(itbis),
            ["total"] = M(total),
        };

    /// <summary>The quote's model (QUO1-04, E-UX3-10).</summary>
    public static IReadOnlyDictionary<string, object?> QuoteModel(QuotePrint q)
    {
        ArgumentNullException.ThrowIfNull(q);
        return new Dictionary<string, object?>
        {
            // E-UX3-10: BORRADOR until sent, VENCIDA once past its validity, PERDIDA or CANCELADA when closed.
            ["marca_agua"] = q.Status switch
            {
                "DRAFT" or "PENDING_APPROVAL" => "BORRADOR",
                "LOST" => "PERDIDA",
                "CANCELLED" => "CANCELADA",
                "CONVERTED" => null,
                _ => q.Expired ? "VENCIDA" : null,
            },
            ["emisor"] = Issuer(q.IssuerName, q.IssuerRnc),
            ["cliente"] = new Dictionary<string, object?> { ["nombre"] = q.CustomerName, ["rnc"] = string.IsNullOrEmpty(q.CustomerRnc) ? null : q.CustomerRnc },
            ["cotizacion"] = new Dictionary<string, object?>
            {
                ["numero"] = q.QuoteNo,
                ["fecha"] = PrintText.Date(q.QuoteDate),
                ["valida_hasta"] = PrintText.Date(q.ValidUntil),
                ["referencia"] = q.CustomerRef,
                ["termino"] = DeliveryStatuses.GetValueOrDefault(q.DeliveryTermCode, q.DeliveryTermCode),
                ["obra"] = q.SiteAddress,
                ["notas"] = q.Notes,
            },
            ["lineas"] = q.Lines.Select(l => (object?)PricedLine(l.LineNo, l.ItemCode, l.ItemName, l.Uom, l.Quantity, l.UnitPrice, l.Net, l.Itbis, l.Total, l.Exempt)).ToList(),
            ["totales_etiqueta"] = "Totales (RD$)",
            ["totales_fila"] = new Dictionary<string, object?>
            {
                ["neto"] = Total(M(q.NetTotal), "print-net"),
                ["itbis"] = Total(M(q.ItbisTotal), "print-itbis"),
                ["total"] = Total(M(q.Total), "print-total"),
            },
        };
    }

    private static async Task<(string, IReadOnlyDictionary<string, object?>)> InvoiceAsync(QueryContext context, DriverLinkKey? key, GetPrintDocument query, CancellationToken cancellationToken)
    {
        var detail = await ReadAsync<GetInvoice, InvoiceDetail>(context, new GetInvoiceHandler(), new GetInvoice(context.CompanyId, context.SessionId, query.Id), cancellationToken).ConfigureAwait(false);
        var pkg = await ReadAsync<GetInvoiceFiscalPackage, InvoiceFiscalPackage>(
            context, new GetInvoiceFiscalPackageHandler(), new GetInvoiceFiscalPackage(context.CompanyId, context.SessionId, query.Id), cancellationToken).ConfigureAwait(false);

        // E-ENT-9: the driver's QR of each delivery of the invoice still in transit with its link usable.
        var qrs = new List<object?>();
        if (key is not null && query.BaseUrl is not null)
        {
            var links = await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT DISTINCT d.delivery_id, d.delivery_no, k.generation
                FROM sal.invoice_line il JOIN log.delivery_line dl ON dl.delivery_line_id = il.delivery_line_id
                JOIN log.delivery d ON d.delivery_id = dl.delivery_id JOIN log.delivery_link k ON k.delivery_id = d.delivery_id
                WHERE il.invoice_id = @i AND d.status = 'IN_TRANSIT' AND k.status = 'ACTIVE'
                ORDER BY d.delivery_no
                """,
                r => new LinkRow(r.GetGuid(0), r.GetString(1), r.GetInt32(2)),
                cancellationToken,
                ("i", query.Id)).ConfigureAwait(false);
            foreach (var link in links)
            {
                var path = string.Create(
                    CultureInfo.InvariantCulture,
                    $"/entrega/?c={context.CompanyId:N}&d={link.DeliveryId:N}&g={link.Generation}&k={key.Mac(context.CompanyId, link.DeliveryId, link.Generation)}");
                var qr = DriverQr(query.BaseUrl, path)!;
                qr["conduce"] = link.DeliveryNo;
                qrs.Add(qr);
            }
        }

        return ($"Factura {detail.Header.InvoiceNo}", InvoiceModel(detail, pkg, qrs));
    }

    /// <summary>The invoice's model (E-VS4-04-4, E-ENT-9): «SIN VALIDEZ FISCAL» until the DGII accepts its e-CF, then the stamp and its QR.</summary>
    public static IReadOnlyDictionary<string, object?> InvoiceModel(InvoiceDetail detail, InvoiceFiscalPackage pkg, IReadOnlyList<object?> driverQrs)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(pkg);
        var h = detail.Header;
        var ecf = detail.Ecf;
        var issuer = detail.Issuer;
        var accepted = ecf is not null ? ecf.Status is "ACCEPTED" or "ACCEPTED_CONDITIONAL" : h.FiscalStatus == "ACCEPTED_EXTERNAL";
        var encf = ecf is not null && accepted ? ecf.Encf : h.Encf;
        var contact = string.Join(" · ", new[] { issuer?.Phone, issuer?.Email }.Where(x => !string.IsNullOrEmpty(x)));
        return new Dictionary<string, object?>
        {
            ["marca_agua"] = h.CommercialStatus == "VOIDED" ? "ANULADA" : accepted ? null : "SIN VALIDEZ FISCAL",
            ["emisor"] = new Dictionary<string, object?>
            {
                ["nombre"] = issuer?.TradeName ?? issuer?.LegalName ?? pkg.IssuerName,
                ["nombre_comercial"] = issuer?.TradeName,
                ["razon_social"] = issuer?.LegalName ?? pkg.IssuerName,
                ["rnc"] = issuer?.Rnc ?? pkg.IssuerRnc,
                ["direccion"] = issuer?.Address,
                ["contacto"] = contact.Length == 0 ? null : contact,
            },
            ["factura"] = new Dictionary<string, object?>
            {
                ["titulo"] = EcfTitles.GetValueOrDefault(h.EcfType, "Factura"),
                ["encf"] = encf ?? "—",
                ["numero"] = h.InvoiceNo,
                ["fecha"] = PrintText.Date(h.InvoiceDate),
                ["vence"] = h.DueDate is { } due && due != h.InvoiceDate ? PrintText.Date(due) : null,
            },
            ["cliente"] = new Dictionary<string, object?>
            {
                ["nombre"] = pkg.ReceiverName,
                ["rnc"] = string.IsNullOrEmpty(pkg.ReceiverRnc) ? null : pkg.ReceiverRnc,
                ["pasaporte"] = pkg.ReceiverPassport,
            },
            ["exencion"] = pkg.Exemption is { } x
                ? new Dictionary<string, object?> { ["regimen"] = x.Regime, ["certificado"] = x.CertificateNo, ["proyecto"] = x.ProjectName }
                : null,
            ["lineas"] = detail.Lines.Select(l => (object?)new Dictionary<string, object?>
            {
                ["linea"] = I(l.LineNo),
                ["descripcion"] = $"{l.ItemCode} — {l.ItemDescription}",
                ["conduce"] = l.DeliveryNo,
                ["unidad"] = l.Uom,
                ["cantidad"] = Q(l.Quantity),
                ["precio"] = M(l.UnitPrice),
                ["itbis"] = M(l.Itbis),
                ["importe"] = M(l.NetAmount),
            }).ToList(),
            ["totales"] = new Dictionary<string, object?> { ["neto"] = M(h.NetTotal), ["itbis"] = M(h.TaxTotal ?? 0m), ["total"] = M(h.Total ?? 0m) },
            ["ecf"] = ecf is not null && accepted && ecf.StampUrl is { } stamp
                ? new Dictionary<string, object?> { ["qr"] = QrSvg(stamp), ["codigo_seguridad"] = ecf.SecurityCode, ["fecha_firma"] = PrintText.DateTime(ecf.SignatureDate) }
                : null,
            ["qrs_chofer"] = driverQrs,
        };
    }

    private static async Task<(string, IReadOnlyDictionary<string, object?>)> ProformaAsync(QueryContext context, GetPrintDocument query, CancellationToken cancellationToken)
    {
        var p = await ReadAsync<GetProforma, ProformaDetail>(context, new GetProformaHandler(), new GetProforma(context.CompanyId, context.SessionId, query.Id), cancellationToken).ConfigureAwait(false);
        return ($"Proforma {p.Header.ProformaNo}", ProformaModel(p));
    }

    /// <summary>The delivery proforma's model (E-FIS1b-01-13).</summary>
    public static IReadOnlyDictionary<string, object?> ProformaModel(ProformaDetail p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var f = p.Header;
        return new Dictionary<string, object?>
        {
            ["marca_agua"] = f.Status == "VOIDED" ? "ANULADA" : null,
            ["emisor"] = Issuer(p.IssuerName, p.IssuerRnc),
            ["cliente"] = new Dictionary<string, object?> { ["nombre"] = f.CustomerName, ["rnc"] = f.CustomerRnc ?? "—" },
            ["proforma"] = new Dictionary<string, object?>
            {
                ["numero"] = f.ProformaNo,
                ["conduce"] = f.DeliveryNo,
                ["pedido"] = f.OrderNo,
                ["fecha"] = PrintText.Date(f.ProformaDate),
                ["vence"] = PrintText.Date(f.DueDate),
                ["obra"] = p.SiteAddress,
            },
            ["lineas"] = p.Lines.Select(l => (object?)PricedLine(l.LineNo, l.ItemCode, l.ItemName, l.Uom, l.Quantity, l.UnitPrice, l.Net, l.Itbis, l.Total)).ToList(),
            ["totales_etiqueta"] = "Totales (RD$)",
            ["totales_fila"] = new Dictionary<string, object?>
            {
                ["neto"] = Total(M(f.Net), "print-proforma-net"),
                ["itbis"] = Total(M(f.Itbis), "print-proforma-itbis"),
                ["total"] = Total(M(f.Total), "print-proforma-total"),
            },
        };
    }

    private static async Task<(string, IReadOnlyDictionary<string, object?>)> OrderProformaAsync(QueryContext context, GetPrintDocument query, CancellationToken cancellationToken)
    {
        var p = await ReadAsync<GetSalesOrderProforma, SalesOrderProforma>(
            context, new GetSalesOrderProformaHandler(), new GetSalesOrderProforma(context.CompanyId, context.SessionId, query.Id), cancellationToken).ConfigureAwait(false);
        var status = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, "SELECT status FROM sal.sales_order WHERE company_id = @c AND sales_order_id = @o", r => r.GetString(0), cancellationToken,
            ("c", context.CompanyId), ("o", query.Id)).ConfigureAwait(false);
        return ($"Proforma del pedido {p.OrderNo}", OrderProformaModel(p, status));
    }

    /// <summary>The order proforma's model (E-FIS1-05-7); BORRADOR before the order is confirmed, CANCELADO once cancelled (V-20).</summary>
    public static IReadOnlyDictionary<string, object?> OrderProformaModel(SalesOrderProforma p, string? orderStatus)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new Dictionary<string, object?>
        {
            ["marca_agua"] = orderStatus switch { "DRAFT" or "PENDING_CREDIT" => "BORRADOR", "CANCELLED" => "CANCELADO", _ => null },
            ["emisor"] = Issuer(p.IssuerName, p.IssuerRnc),
            ["cliente"] = new Dictionary<string, object?> { ["nombre"] = p.CustomerName, ["rnc"] = p.CustomerRnc },
            ["proforma"] = new Dictionary<string, object?>
            {
                ["pedido"] = p.OrderNo,
                ["fecha_pedido"] = PrintText.Date(p.OrderDate),
                ["fecha"] = PrintText.Date(p.ProformaDate),
                ["obra"] = p.SiteAddress,
            },
            ["lineas"] = p.Lines.Select(l => (object?)PricedLine(l.LineNo, l.ItemCode, l.ItemName, l.Uom, l.Quantity, l.UnitPrice, l.Net, l.Itbis, l.Total)).ToList(),
            ["totales_etiqueta"] = "Totales (RD$)",
            ["totales_fila"] = new Dictionary<string, object?>
            {
                ["neto"] = Total(M(p.NetTotal), "proforma-net"),
                ["itbis"] = Total(M(p.ItbisTotal), "proforma-itbis"),
                ["total"] = Total(M(p.Total), "proforma-total"),
            },
        };
    }

    private static async Task<(string, IReadOnlyDictionary<string, object?>)> StatementAsync(QueryContext context, GetPrintDocument query, CancellationToken cancellationToken)
    {
        if (query.From is not { } from || query.To is not { } to)
        {
            throw new DomainException(QueryErrors.InvalidParameter, "A statement prints for a period: from and to.");
        }

        var s = await ReadAsync<GetCustomerStatement, CustomerStatement>(
            context, new GetCustomerStatementHandler(), new GetCustomerStatement(context.CompanyId, context.SessionId, query.Id, from, to), cancellationToken).ConfigureAwait(false);
        var company = await CompanyAsync(context, cancellationToken).ConfigureAwait(false);
        return ($"Estado de cuenta {s.CustomerName}", StatementModel(s, company.LegalName, company.Rnc, Rochell.Platform.Time.BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow)));
    }

    /// <summary>The statement's model (UX4-03 V-19): the opening balance as the first line, the period's lines, the closing totals, open proformas apart.</summary>
    public static IReadOnlyDictionary<string, object?> StatementModel(CustomerStatement s, string issuerName, string issuerRnc, DateOnly issuedOn)
    {
        ArgumentNullException.ThrowIfNull(s);
        var lines = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["fecha"] = PrintText.Date(s.From), ["tipo"] = "Saldo inicial", ["documento"] = string.Empty, ["debito"] = string.Empty, ["credito"] = string.Empty, ["saldo"] = M(s.Opening),
            },
        };
        lines.AddRange(s.Entries.Select(e => (object?)new Dictionary<string, object?>
        {
            ["fecha"] = PrintText.Date(e.PostingDate),
            ["tipo"] = StatementKinds.GetValueOrDefault(e.Kind, e.Kind),
            ["documento"] = e.DocumentNo ?? "—",
            ["debito"] = M(e.Debit),
            ["credito"] = M(e.Credit),
            ["saldo"] = M(e.Balance),
        }));
        return new Dictionary<string, object?>
        {
            ["emisor"] = Issuer(issuerName, issuerRnc),
            ["cliente"] = new Dictionary<string, object?> { ["nombre"] = s.CustomerName, ["rnc"] = s.Rnc },
            ["periodo"] = new Dictionary<string, object?> { ["desde"] = PrintText.Date(s.From), ["hasta"] = PrintText.Date(s.To), ["emitido"] = PrintText.Date(issuedOn) },
            ["lineas"] = lines,
            ["totales_etiqueta"] = "Saldo final",
            ["totales_fila"] = new Dictionary<string, object?>
            {
                ["debito"] = Total(M(s.TotalDebit), "statement-debit"),
                ["credito"] = Total(M(s.TotalCredit), "statement-credit"),
                ["saldo"] = Total(M(s.Closing), "statement-closing"),
            },
            ["saldos"] = new Dictionary<string, object?> { ["inicial"] = M(s.Opening), ["final"] = M(s.Closing), ["proformas"] = M(s.ProformaBalance) },
            ["proformas"] = s.OpenProformas.Select(f => (object?)new Dictionary<string, object?>
            {
                ["numero"] = f.ProformaNo,
                ["conduce"] = f.DeliveryNo,
                ["fecha"] = PrintText.Date(f.ProformaDate),
                ["vence"] = PrintText.Date(f.DueDate),
                ["total"] = M(f.Total),
                ["cobrado"] = M(f.Allocated),
                ["saldo"] = M(f.Balance),
                ["deposito"] = M(f.Deposit),
            }).ToList(),
        };
    }

    private static async Task<(string, IReadOnlyDictionary<string, object?>)> AgingAsync(QueryContext context, GetPrintDocument query, CancellationToken cancellationToken)
    {
        var aging = await ReadAsync<GetArAging, ArAging>(context, new GetArAgingHandler(), new GetArAging(context.CompanyId, context.SessionId), cancellationToken).ConfigureAwait(false);
        var c = aging.Customers.SingleOrDefault(x => x.CustomerId == query.Id)
            ?? new ArAgingCustomer(query.Id, await CustomerNameAsync(context, query.Id, cancellationToken).ConfigureAwait(false), 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, [], 0m, 0m, []);
        var company = await CompanyAsync(context, cancellationToken).ConfigureAwait(false);
        return ($"Facturas pendientes {c.CustomerName}", AgingModel(c, aging.AsOf, aging.Buckets, company.LegalName, company.Rnc));
    }

    /// <summary>The open invoices and proformas of a customer by age (E-VS3-09-1, E-FIS1b-3).</summary>
    public static IReadOnlyDictionary<string, object?> AgingModel(ArAgingCustomer c, DateOnly asOf, ArAgingBuckets b, string issuerName, string issuerRnc)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(b);
        string Days(int d) => d > 0 ? I(d) : "Al día";
        return new Dictionary<string, object?>
        {
            ["emisor"] = Issuer(issuerName, issuerRnc),
            ["cliente"] = new Dictionary<string, object?> { ["nombre"] = c.CustomerName },
            ["corte"] = PrintText.Date(asOf),
            ["lineas"] = c.Documents.Select(d => (object?)new Dictionary<string, object?>
            {
                ["numero"] = d.InvoiceNo,
                ["encf"] = d.Encf ?? "—",
                ["fecha"] = PrintText.Date(d.DocDate),
                ["vence"] = PrintText.Date(d.DueDate),
                ["dias"] = Days(d.DaysOverdue),
                ["pendiente"] = M(d.OpenAmount),
            }).ToList(),
            ["totales_etiqueta"] = "Total pendiente",
            ["totales_fila"] = new Dictionary<string, object?> { ["pendiente"] = Total(M(c.Total), "aging-total") },
            ["tramos"] = new List<object?>
            {
                $"1–{I(b.Bucket1Days)} días", $"{I(b.Bucket1Days + 1)}–{I(b.Bucket2Days)} días", $"{I(b.Bucket2Days + 1)}–{I(b.Bucket3Days)} días", $"Más de {I(b.Bucket3Days)} días",
            },
            ["totales"] = new Dictionary<string, object?>
            {
                ["al_dia"] = M(c.Current),
                ["por_tramo"] = new List<object?> { M(c.Bucket1), M(c.Bucket2), M(c.Bucket3), M(c.Over) },
                ["a_favor"] = M(c.Unapplied),
                ["neto"] = M(c.Net),
                ["proformas"] = M(c.Proformas),
            },
            ["proformas"] = c.ProformaDocuments.Select(f => (object?)new Dictionary<string, object?>
            {
                ["numero"] = f.ProformaNo,
                ["conduce"] = f.DeliveryNo,
                ["fecha"] = PrintText.Date(f.ProformaDate),
                ["vence"] = PrintText.Date(f.DueDate),
                ["dias"] = Days(f.DaysOverdue),
                ["saldo"] = M(f.Balance),
            }).ToList(),
        };
    }

    private static async Task<string> CustomerNameAsync(QueryContext context, Guid partyId, CancellationToken cancellationToken)
        => await Reading.SingleOrDefaultAsync(
               context.Connection, context.Transaction, "SELECT legal_name FROM md.party WHERE company_id = @c AND party_id = @p", r => r.GetString(0), cancellationToken,
               ("c", context.CompanyId), ("p", partyId)).ConfigureAwait(false)
           ?? throw new DomainException(QueryErrors.NotFound, "The customer does not exist.");
}
