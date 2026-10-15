using System.Globalization;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Sales.Queries;

// REC-03 (E-REC-5, E-REC-03-1…4): the dispatch report and the sales report, each with totals and CSV.

internal static class ReportPeriod
{
    private const int MaxDays = 366; // type-limit: one report covers at most a year

    public static void Ensure(DateOnly from, DateOnly to)
    {
        if (to < from || to.DayNumber - from.DayNumber > MaxDays)
        {
            throw new DomainException(QueryErrors.InvalidParameter, "The report covers from a date to a later one, at most a year.");
        }
    }

    public static string Q(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    public static string M(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}

/// <summary>
/// E-REC-03-1/2: the deliveries that left between <paramref name="From"/> and <paramref name="To"/> (Dominican gate-out date), one row
/// per line and lot in base units, with the truck (our fleet code and plate, or the customer's plate at a pickup) and the driver; totals
/// per product and per customer. Cancelled deliveries never left, so they are not here.
/// </summary>
public sealed record GetDispatchReport(
    Guid CompanyId, Guid SessionId, DateOnly From, DateOnly To, Guid? PartyId = null, Guid? ItemId = null, Guid? VehicleId = null, Guid? DriverId = null, Guid? PlantId = null) : IQuery;

public sealed record DispatchReportRow(
    DateTime GateOutAt, string DeliveryNo, string OrderNo, string CustomerName, string ItemCode, string ItemDescription, string LotCode, decimal Quantity, string Uom,
    string? VehicleFleetCode, string? VehiclePlate, string? DriverName, string DeliveryTerm, string Status, string PlantCode);

public sealed record DispatchProductTotal(string ItemCode, string ItemDescription, string Uom, decimal Quantity, int Deliveries);

public sealed record DispatchCustomerTotal(string CustomerName, int Deliveries);

public sealed record DispatchReport(
    DateOnly From, DateOnly To, IReadOnlyList<DispatchReportRow> Rows, IReadOnlyList<DispatchProductTotal> ProductTotals, IReadOnlyList<DispatchCustomerTotal> CustomerTotals);

[RequiresPermission("sales:read")]
public sealed class GetDispatchReportHandler : IQueryHandler<GetDispatchReport>
{
    public string QueryType => "Sales.GetDispatchReport";

    public async Task<string> HandleAsync(GetDispatchReport query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        ReportPeriod.Ensure(query.From, query.To);
        var rows = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT d.gate_out_at, d.delivery_no, o.order_no, p.legal_name, i.code, i.description, coalesce(f.field_code, l.lot_code), x.base_quantity, i.base_uom,
                   v.fleet_code, coalesce(v.plate, d.customer_vehicle_plate), coalesce(dr.full_name, d.customer_driver_name), d.delivery_term_code, d.status, pl.code
            FROM log.delivery d
            JOIN log.delivery_line dl ON dl.delivery_id = d.delivery_id
            JOIN log.delivery_line_lot x ON x.delivery_line_id = dl.delivery_line_id
            JOIN inv.lot l ON l.lot_id = x.lot_id
            LEFT JOIN mfg.fg_lot f ON f.lot_id = x.lot_id
            JOIN md.item i ON i.item_id = dl.item_id
            JOIN sal.sales_order o ON o.sales_order_id = d.sales_order_id
            JOIN md.party p ON p.party_id = o.party_id
            JOIN md.plant pl ON pl.plant_id = d.plant_id
            LEFT JOIN log.vehicle v ON v.vehicle_id = d.vehicle_id
            LEFT JOIN log.driver dr ON dr.driver_id = d.driver_id
            WHERE d.company_id = @c AND d.gate_out_at IS NOT NULL
              AND (d.gate_out_at AT TIME ZONE 'America/Santo_Domingo')::date BETWEEN @from AND @to
              AND (CAST(@party AS uuid) IS NULL OR o.party_id = CAST(@party AS uuid))
              AND (CAST(@item AS uuid) IS NULL OR dl.item_id = CAST(@item AS uuid))
              AND (CAST(@vehicle AS uuid) IS NULL OR d.vehicle_id = CAST(@vehicle AS uuid))
              AND (CAST(@driver AS uuid) IS NULL OR d.driver_id = CAST(@driver AS uuid))
              AND (CAST(@plant AS uuid) IS NULL OR d.plant_id = CAST(@plant AS uuid))
            ORDER BY d.gate_out_at, d.delivery_no, dl.line_no, l.lot_code
            """,
            r => new DispatchReportRow(
                r.GetFieldValue<DateTime>(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetDecimal(7), r.GetString(8),
                r.NullableString(9), r.NullableString(10), r.NullableString(11), r.GetString(12), r.GetString(13), r.GetString(14)),
            cancellationToken,
            ("c", context.CompanyId),
            ("from", query.From),
            ("to", query.To),
            ("party", query.PartyId),
            ("item", query.ItemId),
            ("vehicle", query.VehicleId),
            ("driver", query.DriverId),
            ("plant", query.PlantId)).ConfigureAwait(false);
        var products = rows
            .GroupBy(r => (r.ItemCode, r.ItemDescription, r.Uom))
            .Select(g => new DispatchProductTotal(g.Key.ItemCode, g.Key.ItemDescription, g.Key.Uom, g.Sum(x => x.Quantity), g.Select(x => x.DeliveryNo).Distinct(StringComparer.Ordinal).Count()))
            .OrderBy(t => t.ItemCode, StringComparer.Ordinal)
            .ToList();
        var customers = rows
            .GroupBy(r => r.CustomerName)
            .Select(g => new DispatchCustomerTotal(g.Key, g.Select(x => x.DeliveryNo).Distinct(StringComparer.Ordinal).Count()))
            .OrderBy(t => t.CustomerName, StringComparer.Ordinal)
            .ToList();
        return ApiJson.Serialize(new DispatchReport(query.From, query.To, rows, products, customers));
    }
}

/// <summary>
/// E-REC-03-3/4: the invoices issued between <paramref name="From"/> and <paramref name="To"/> (invoice date, voided ones left out), one
/// row per line with its net, ITBIS and total; the confirmed commercial credit notes of the period as negative rows of their own; totals
/// per product and per customer, the invoices' total, the credit notes' total and the net sale. Proformas are not a fiscal sale.
/// </summary>
public sealed record GetSalesReport(Guid CompanyId, Guid SessionId, DateOnly From, DateOnly To, Guid? PartyId = null, Guid? ItemId = null, Guid? PlantId = null) : IQuery;

/// <remarks><c>Kind</c>: INVOICE or CREDIT_NOTE (amounts negative); <c>LineKind</c>: PRODUCT or FREIGHT.</remarks>
public sealed record SalesReportRow(
    DateOnly Date, string Kind, string DocumentNo, string? Encf, string CustomerName, string? CustomerRnc, string ItemCode, string ItemDescription, string LineKind,
    decimal? Quantity, string? Uom, decimal? UnitPrice, decimal Net, decimal Itbis, decimal Total);

public sealed record SalesTotal(string Key, string Name, decimal Net, decimal Itbis, decimal Total);

public sealed record SalesReport(
    DateOnly From, DateOnly To, IReadOnlyList<SalesReportRow> Rows, IReadOnlyList<SalesTotal> ProductTotals, IReadOnlyList<SalesTotal> CustomerTotals,
    SalesTotal Invoices, SalesTotal CreditNotes, SalesTotal NetSales);

[RequiresPermission("sales:read")]
public sealed class GetSalesReportHandler : IQueryHandler<GetSalesReport>
{
    public string QueryType => "Sales.GetSalesReport";

    public async Task<string> HandleAsync(GetSalesReport query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        ReportPeriod.Ensure(query.From, query.To);
        var parameters = new (string, object?)[]
        {
            ("c", context.CompanyId), ("from", query.From), ("to", query.To), ("party", query.PartyId), ("item", query.ItemId), ("plant", query.PlantId),
        };
        var invoices = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT inv.invoice_date, inv.invoice_no, inv.encf, p.legal_name, p.rnc, it.code, it.description, il.line_kind, il.quantity, il.uom, il.unit_price,
                   il.net_amount::numeric(19,2),
                   coalesce((SELECT sum(t.amount) FROM tax.tax_determination_line t
                             WHERE t.determination_id = inv.tax_determination_id AND t.subject_line_id = il.invoice_line_id AND t.effect = 'OUTPUT'), 0)::numeric(19,2)
            FROM sal.invoice inv
            JOIN sal.invoice_line il ON il.invoice_id = inv.invoice_id
            JOIN md.item it ON it.item_id = il.item_id
            JOIN md.party p ON p.party_id = inv.party_id
            LEFT JOIN log.delivery_line dl ON dl.delivery_line_id = il.delivery_line_id
            LEFT JOIN log.delivery d ON d.delivery_id = dl.delivery_id
            WHERE inv.company_id = @c AND inv.commercial_status IN ('CONFIRMED', 'PARTIALLY_PAID', 'PAID')
              AND inv.invoice_date BETWEEN @from AND @to
              AND (CAST(@party AS uuid) IS NULL OR inv.party_id = CAST(@party AS uuid))
              AND (CAST(@item AS uuid) IS NULL OR il.item_id = CAST(@item AS uuid))
              AND (CAST(@plant AS uuid) IS NULL OR d.plant_id = CAST(@plant AS uuid))
            ORDER BY inv.invoice_date, inv.invoice_no, il.line_no
            """,
            r =>
            {
                var net = r.GetDecimal(11);
                var itbis = r.GetDecimal(12);
                return new SalesReportRow(
                    r.Date(0), "INVOICE", r.GetString(1), r.NullableString(2), r.GetString(3), r.NullableString(4), r.GetString(5), r.GetString(6), r.GetString(7),
                    r.GetDecimal(8), r.GetString(9), r.GetDecimal(10), net, itbis, net + itbis);
            },
            cancellationToken,
            parameters).ConfigureAwait(false);
        var credits = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT n.credit_date, n.credit_note_no, n.encf, p.legal_name, p.rnc, it.code, it.description, il.line_kind, cl.net_amount::numeric(19,2), cl.itbis::numeric(19,2)
            FROM sal.credit_note n
            JOIN sal.credit_note_line cl ON cl.credit_note_id = n.credit_note_id
            JOIN sal.invoice_line il ON il.invoice_line_id = cl.invoice_line_id
            JOIN md.item it ON it.item_id = il.item_id
            JOIN md.party p ON p.party_id = n.party_id
            LEFT JOIN log.delivery_line dl ON dl.delivery_line_id = il.delivery_line_id
            LEFT JOIN log.delivery d ON d.delivery_id = dl.delivery_id
            WHERE n.company_id = @c AND n.commercial_status = 'CONFIRMED'
              AND n.credit_date BETWEEN @from AND @to
              AND (CAST(@party AS uuid) IS NULL OR n.party_id = CAST(@party AS uuid))
              AND (CAST(@item AS uuid) IS NULL OR il.item_id = CAST(@item AS uuid))
              AND (CAST(@plant AS uuid) IS NULL OR d.plant_id = CAST(@plant AS uuid))
            ORDER BY n.credit_date, n.credit_note_no, cl.line_no
            """,
            r =>
            {
                var net = -r.GetDecimal(8);
                var itbis = -r.GetDecimal(9);
                return new SalesReportRow(
                    r.Date(0), "CREDIT_NOTE", r.GetString(1), r.NullableString(2), r.GetString(3), r.NullableString(4), r.GetString(5), r.GetString(6), r.GetString(7),
                    null, null, null, net, itbis, net + itbis);
            },
            cancellationToken,
            parameters).ConfigureAwait(false);
        var rows = invoices.Concat(credits).OrderBy(r => r.Date).ThenBy(r => r.Kind == "INVOICE" ? 0 : 1).ThenBy(r => r.DocumentNo, StringComparer.Ordinal).ToList();
        static SalesTotal Sum(string key, string name, IEnumerable<SalesReportRow> of)
        {
            var list = of.ToList();
            return new SalesTotal(key, name, list.Sum(x => x.Net), list.Sum(x => x.Itbis), list.Sum(x => x.Total));
        }

        var products = rows.GroupBy(r => (r.ItemCode, r.ItemDescription)).Select(g => Sum(g.Key.ItemCode, g.Key.ItemDescription, g)).OrderBy(t => t.Key, StringComparer.Ordinal).ToList();
        var customers = rows.GroupBy(r => (r.CustomerRnc ?? string.Empty, r.CustomerName)).Select(g => Sum(g.Key.Item1, g.Key.CustomerName, g)).OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        return ApiJson.Serialize(new SalesReport(
            query.From, query.To, rows, products, customers,
            Sum("INVOICES", "Facturas", invoices), Sum("CREDIT_NOTES", "Notas de crédito", credits), Sum("NET", "Venta neta", rows)));
    }
}

/// <summary>E-REC-03-2/4: the two reports as CSV (Excel opens them).</summary>
public static class SalesReportCsv
{
    public static string Dispatch(string json)
    {
        var report = JsonSerializer.Deserialize<DispatchReport>(json, ApiJson.Options) ?? throw new InvalidOperationException("Empty query result.");
        var csv = new Finance.Ledger.LedgerCsv.Writer(
            "Fecha", "Conduce", "Pedido", "Cliente", "Producto", "Descripción", "Lote", "Cantidad", "Unidad", "Camión", "Chofer", "Entrega", "Estado", "Planta");
        foreach (var r in report.Rows)
        {
            csv.Row(
                r.GateOutAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), r.DeliveryNo, r.OrderNo, r.CustomerName, r.ItemCode, r.ItemDescription, r.LotCode,
                ReportPeriod.Q(r.Quantity), r.Uom, r.VehicleFleetCode is null ? r.VehiclePlate : $"{r.VehicleFleetCode} · {r.VehiclePlate}", r.DriverName,
                r.DeliveryTerm == "PICKUP_AT_PLANT" ? "Retiro en planta" : "Entrega en obra", r.Status, r.PlantCode);
        }

        csv.Row();
        csv.Row("Totales por producto", string.Empty, string.Empty, string.Empty, "Producto", "Descripción", string.Empty, "Cantidad", "Unidad", "Conduces");
        foreach (var t in report.ProductTotals)
        {
            csv.Row(string.Empty, string.Empty, string.Empty, string.Empty, t.ItemCode, t.ItemDescription, string.Empty, ReportPeriod.Q(t.Quantity), t.Uom, t.Deliveries.ToString(CultureInfo.InvariantCulture));
        }

        csv.Row();
        csv.Row("Totales por cliente", string.Empty, string.Empty, "Cliente", "Conduces");
        foreach (var t in report.CustomerTotals)
        {
            csv.Row(string.Empty, string.Empty, string.Empty, t.CustomerName, t.Deliveries.ToString(CultureInfo.InvariantCulture));
        }

        return csv.ToString();
    }

    public static string Sales(string json)
    {
        var report = JsonSerializer.Deserialize<SalesReport>(json, ApiJson.Options) ?? throw new InvalidOperationException("Empty query result.");
        var csv = new Finance.Ledger.LedgerCsv.Writer(
            "Fecha", "Tipo", "Documento", "e-NCF", "Cliente", "RNC", "Producto", "Descripción", "Línea", "Cantidad", "Unidad", "Precio", "Neto", "ITBIS", "Total");
        foreach (var r in report.Rows)
        {
            csv.Row(
                r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.Kind == "INVOICE" ? "Factura" : "Nota de crédito", r.DocumentNo, r.Encf, r.CustomerName, r.CustomerRnc,
                r.ItemCode, r.ItemDescription, r.LineKind == "FREIGHT" ? "Flete" : "Producto", r.Quantity is { } q ? ReportPeriod.Q(q) : null, r.Uom,
                r.UnitPrice is { } u ? ReportPeriod.Q(u) : null, ReportPeriod.M(r.Net), ReportPeriod.M(r.Itbis), ReportPeriod.M(r.Total));
        }

        void Totals(string title, IEnumerable<SalesTotal> totals)
        {
            csv.Row();
            csv.Row(title, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, "Neto", "ITBIS", "Total");
            foreach (var t in totals)
            {
                csv.Row(string.Empty, string.Empty, string.Empty, string.Empty, t.Name, t.Key, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                    ReportPeriod.M(t.Net), ReportPeriod.M(t.Itbis), ReportPeriod.M(t.Total));
            }
        }

        Totals("Totales por producto", report.ProductTotals);
        Totals("Totales por cliente", report.CustomerTotals);
        Totals("Resumen", [report.Invoices, report.CreditNotes, report.NetSales]);
        return csv.ToString();
    }
}
