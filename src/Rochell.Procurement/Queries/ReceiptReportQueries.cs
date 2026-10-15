using System.Globalization;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Procurement.Queries;

// REC-01 (E-REC-1, E-REC-4): what the receiving screen offers as our transport, and the report of raw material received.

/// <summary>E-REC-1: our ACTIVE trucks and drivers, for the receiving screen (the fleet's tables are read through SQL).</summary>
public sealed record ListReceivingFleet(Guid CompanyId, Guid SessionId) : IQuery;

public sealed record ReceivingVehicleView(Guid VehicleId, string Plate, string? FleetCode);

public sealed record ReceivingDriverView(Guid DriverId, string FullName);

public sealed record ReceivingFleet(IReadOnlyList<ReceivingVehicleView> Vehicles, IReadOnlyList<ReceivingDriverView> Drivers);

[RequiresPermission("goods_receipt:read")]
public sealed class ListReceivingFleetHandler : IQueryHandler<ListReceivingFleet>
{
    public string QueryType => "Procurement.ListReceivingFleet";

    public async Task<string> HandleAsync(ListReceivingFleet query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var vehicles = await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT vehicle_id, plate, fleet_code FROM log.vehicle WHERE company_id = @c AND status = 'ACTIVE' ORDER BY fleet_code NULLS LAST, plate",
            r => new ReceivingVehicleView(r.GetGuid(0), r.GetString(1), r.NullableString(2)), cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false);
        var drivers = await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT driver_id, full_name FROM log.driver WHERE company_id = @c AND status = 'ACTIVE' ORDER BY full_name",
            r => new ReceivingDriverView(r.GetGuid(0), r.GetString(1)), cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false);
        return ApiJson.Serialize(new ReceivingFleet(vehicles, drivers));
    }
}

/// <summary>
/// E-REC-4: the raw material received from <paramref name="From"/> to <paramref name="To"/> (business dates), one row per receipt line —
/// supplier, the supplier's delivery note, our truck and driver, material, quantity and unit, location — with totals per material and
/// unit. Reversed receipts are left out. Filters by supplier, material, location and plant.
/// </summary>
public sealed record GetGoodsReceiptReport(
    Guid CompanyId, Guid SessionId, DateOnly From, DateOnly To, Guid? SupplierId = null, Guid? ItemId = null, Guid? LocationId = null, Guid? PlantId = null) : IQuery;

public sealed record GoodsReceiptReportRow(
    DateTime OccurredAt, string GrNo, string PoNo, string SupplierName, string? SupplierDeliveryNote, string? VehiclePlate, string? VehicleFleetCode, string? DriverName,
    string ItemCode, string ItemName, decimal Quantity, string Uom, string LocationCode, string? WeighTicketRef);

public sealed record GoodsReceiptReportTotal(string ItemCode, string ItemName, string Uom, decimal Quantity, int Receipts);

public sealed record GoodsReceiptReport(DateOnly From, DateOnly To, IReadOnlyList<GoodsReceiptReportRow> Rows, IReadOnlyList<GoodsReceiptReportTotal> Totals);

[RequiresPermission("goods_receipt:read")]
public sealed class GetGoodsReceiptReportHandler : IQueryHandler<GetGoodsReceiptReport>
{
    private const int MaxDays = 366; // type-limit: one report covers at most a year

    public string QueryType => "Procurement.GetGoodsReceiptReport";

    public async Task<string> HandleAsync(GetGoodsReceiptReport query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        if (query.To < query.From || query.To.DayNumber - query.From.DayNumber > MaxDays)
        {
            throw new DomainException(QueryErrors.InvalidParameter, "The report covers from a date to a later one, at most a year.");
        }

        var rows = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT gr.occurred_at, gr.gr_no, po.po_no, coalesce(pa.legal_name, ''), gr.supplier_delivery_note, v.plate, v.fleet_code, dr.full_name,
                   i.code, i.description, grl.qty, pol.uom, loc.code, gr.weigh_ticket_ref
            FROM pur.goods_receipt gr
            JOIN pur.goods_receipt_line grl ON grl.gr_id = gr.gr_id
            JOIN pur.purchase_order po ON po.po_id = gr.po_id
            JOIN pur.purchase_order_line pol ON pol.po_line_id = grl.po_line_id
            JOIN md.item i ON i.item_id = pol.item_id
            JOIN md.location loc ON loc.location_id = gr.location_id
            LEFT JOIN md.party pa ON pa.party_id = po.party_id
            LEFT JOIN log.vehicle v ON v.vehicle_id = gr.vehicle_id
            LEFT JOIN log.driver dr ON dr.driver_id = gr.driver_id
            WHERE gr.company_id = @c AND gr.document_status <> 'REVERSED'
              AND (gr.occurred_at AT TIME ZONE 'America/Santo_Domingo')::date BETWEEN @from AND @to
              AND (CAST(@supplier AS uuid) IS NULL OR po.party_id = CAST(@supplier AS uuid))
              AND (CAST(@item AS uuid) IS NULL OR pol.item_id = CAST(@item AS uuid))
              AND (CAST(@location AS uuid) IS NULL OR gr.location_id = CAST(@location AS uuid))
              AND (CAST(@plant AS uuid) IS NULL OR po.plant_id = CAST(@plant AS uuid))
            ORDER BY gr.occurred_at, gr.gr_no, i.code
            """,
            r => new GoodsReceiptReportRow(
                r.GetFieldValue<DateTime>(0), r.GetString(1), r.GetString(2), r.GetString(3), r.NullableString(4), r.NullableString(5), r.NullableString(6), r.NullableString(7),
                r.GetString(8), r.GetString(9), r.GetDecimal(10), r.GetString(11), r.GetString(12), r.NullableString(13)),
            cancellationToken,
            ("c", context.CompanyId),
            ("from", query.From),
            ("to", query.To),
            ("supplier", query.SupplierId),
            ("item", query.ItemId),
            ("location", query.LocationId),
            ("plant", query.PlantId)).ConfigureAwait(false);
        var totals = rows
            .GroupBy(r => (r.ItemCode, r.ItemName, r.Uom))
            .Select(g => new GoodsReceiptReportTotal(g.Key.ItemCode, g.Key.ItemName, g.Key.Uom, g.Sum(x => x.Quantity), g.Select(x => x.GrNo).Distinct(StringComparer.Ordinal).Count()))
            .OrderBy(t => t.ItemCode, StringComparer.Ordinal)
            .ThenBy(t => t.Uom, StringComparer.Ordinal)
            .ToList();
        return ApiJson.Serialize(new GoodsReceiptReport(query.From, query.To, rows, totals));
    }
}

/// <summary>E-REC-4: the report as CSV (Excel opens it): one row per receipt line, then the totals per material.</summary>
public static class GoodsReceiptCsv
{
    public static string Report(string json)
    {
        var report = JsonSerializer.Deserialize<GoodsReceiptReport>(json, ApiJson.Options) ?? throw new InvalidOperationException("Empty query result.");
        var csv = new Finance.Ledger.LedgerCsv.Writer(
            "Fecha", "Recepción", "Orden", "Proveedor", "Conduce del proveedor", "Camión", "Chofer", "Material", "Descripción", "Cantidad", "Unidad", "Ubicación", "Ticket de báscula");
        foreach (var r in report.Rows)
        {
            csv.Row(
                r.OccurredAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), r.GrNo, r.PoNo, r.SupplierName, r.SupplierDeliveryNote,
                r.VehicleFleetCode is null ? r.VehiclePlate : $"{r.VehicleFleetCode} · {r.VehiclePlate}", r.DriverName, r.ItemCode, r.ItemName, Q(r.Quantity), r.Uom, r.LocationCode,
                r.WeighTicketRef);
        }

        csv.Row();
        csv.Row("Totales por material", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, "Material", "Descripción", "Cantidad", "Unidad", "Recepciones");
        foreach (var t in report.Totals)
        {
            csv.Row(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, t.ItemCode, t.ItemName, Q(t.Quantity), t.Uom,
                t.Receipts.ToString(CultureInfo.InvariantCulture));
        }

        return csv.ToString();
    }

    private static string Q(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}

/// <summary>
/// REC-02 (E-REC-7, E-REC-02-2/3): the price Core proposes for a material received without an order from a supplier — the last price
/// in that unit, else the standard price — and the deviation the PURCHASING policy allows (null: every direct receipt waits for
/// approval).
/// </summary>
public sealed record GetDirectReceiptReference(Guid CompanyId, Guid SessionId, Guid PartyId, Guid ItemId, string Uom) : IQuery;

public sealed record DirectReceiptReferenceView(decimal? ReferencePrice, string? Source, string? SourcePoNo, decimal? AllowedDeviationPct);

[RequiresPermission("goods_receipt:direct")]
public sealed class GetDirectReceiptReferenceHandler : IQueryHandler<GetDirectReceiptReference>
{
    public string QueryType => "Procurement.GetDirectReceiptReference";

    public async Task<string> HandleAsync(GetDirectReceiptReference query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var reference = await GoodsReceipts.DirectReceipts.ReferenceAsync(
            context.Connection, context.Transaction, context.CompanyId, query.PartyId, query.ItemId, (query.Uom ?? string.Empty).Trim(), cancellationToken).ConfigureAwait(false);
        var policy = await Rochell.Finance.Policies.PolicyResolver.TryResolveAsync(
            context.Connection, context.Transaction, context.CompanyId, Rochell.Finance.Policies.PolicyCodes.Purchasing, Rochell.Platform.Time.BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow), cancellationToken).ConfigureAwait(false);
        decimal? allowed = policy is not null && policy.Has(GoodsReceipts.DirectReceipts.PriceDeviationParameter) ? policy.Decimal(GoodsReceipts.DirectReceipts.PriceDeviationParameter) : null;
        return ApiJson.Serialize(new DirectReceiptReferenceView(reference.Price, reference.Source, reference.SourcePoNo, allowed));
    }
}
