using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Sales.Queries;

// VS3-04: deliveries (conduces), read with sales:read.

public sealed record ListDeliveries(Guid CompanyId, Guid SessionId, string? Status = null, Guid? SalesOrderId = null, int Limit = 50, int Offset = 0) : IQuery;

public sealed record DeliverySummary(
    Guid DeliveryId, string DeliveryNo, Guid SalesOrderId, string OrderNo, string CustomerName, string PlantCode, string DeliveryTermCode, string ControlTransfersAt, string Status,
    DateTime? GateOutAt, long Version);

public sealed record DeliveryList(IReadOnlyList<DeliverySummary> Items, int Limit, int Offset);

internal static class DeliveryReading
{
    public const string Select = """
        SELECT d.delivery_id, d.delivery_no, d.sales_order_id, o.order_no, p.legal_name, pl.code, d.delivery_term_code, d.control_transfers_at, d.status, d.gate_out_at, d.version
        FROM log.delivery d
        JOIN sal.sales_order o ON o.sales_order_id = d.sales_order_id
        JOIN md.party p ON p.party_id = o.party_id
        JOIN md.plant pl ON pl.plant_id = d.plant_id
        """;

    public static DeliverySummary Map(System.Data.Common.DbDataReader r)
        => new(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8),
            r.IsDBNull(9) ? null : r.GetFieldValue<DateTime>(9), r.GetInt64(10));
}

[RequiresPermission("sales:read")]
public sealed class ListDeliveriesHandler : IQueryHandler<ListDeliveries>
{
    public string QueryType => "Sales.ListDeliveries";

    public async Task<string> HandleAsync(ListDeliveries query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            DeliveryReading.Select + """
             WHERE d.company_id = @c AND (CAST(@s AS text) IS NULL OR d.status = CAST(@s AS text)) AND (CAST(@o AS uuid) IS NULL OR d.sales_order_id = CAST(@o AS uuid))
            ORDER BY d.delivery_no DESC
            LIMIT @limit OFFSET @offset
            """,
            DeliveryReading.Map,
            cancellationToken,
            ("c", context.CompanyId),
            ("s", query.Status),
            ("o", query.SalesOrderId),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new DeliveryList(items, query.Limit, query.Offset));
    }
}

public sealed record GetDelivery(Guid CompanyId, Guid SessionId, Guid DeliveryId) : IQuery;

public sealed record DeliveryLotView(string LotCode, string SourceLocationCode, decimal BaseQuantity);

public sealed record DeliveryLineView(
    Guid DeliveryLineId, int LineNo, Guid ItemId, string ItemCode, string ItemDescription, string Uom, decimal QtyPlanned, string? SourceLocationCode, decimal QtyIssued,
    decimal QtyDelivered, decimal QtyReturned, decimal QtyLost, decimal QtyInvoiced, IReadOnlyList<DeliveryLotView> Lots);

public sealed record PodView(string ReceivedByName, DateTime ReceivedAt, string EvidenceRef, string EvidenceSha256);

public sealed record ControlAssessmentView(string TriggerPoint, string Result, Guid? PolicyVersionId, DateTime AssessedAt);

public sealed record DeliveryDetail(
    DeliverySummary Header, string? VehiclePlate, string? DriverName, string? CustomerVehiclePlate, string? CustomerDriverName, decimal? GrossKg, decimal? TareKg, string? WeighTicketRef,
    string? ExceptionReason, string? CancelReason, IReadOnlyList<DeliveryLineView> Lines, PodView? Pod, IReadOnlyList<ControlAssessmentView> Assessments, IReadOnlyList<StateChange> History);

[RequiresPermission("sales:read")]
public sealed class GetDeliveryHandler : IQueryHandler<GetDelivery>
{
    public string QueryType => "Sales.GetDelivery";

    private sealed record Extra(string? Plate, string? Driver, string? CustomerPlate, string? CustomerDriver, decimal? Gross, decimal? Tare, string? Ticket, string? Exception, string? Cancel);

    public async Task<string> HandleAsync(GetDelivery query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var header = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, DeliveryReading.Select + " WHERE d.company_id = @c AND d.delivery_id = @d", DeliveryReading.Map, cancellationToken,
            ("c", context.CompanyId), ("d", query.DeliveryId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The delivery does not exist.");
        var extra = (await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT v.plate, dr.full_name, d.customer_vehicle_plate, d.customer_driver_name, d.gross_kg, d.tare_kg, d.weigh_ticket_ref, d.exception_reason, d.cancel_reason
            FROM log.delivery d LEFT JOIN log.vehicle v ON v.vehicle_id = d.vehicle_id LEFT JOIN log.driver dr ON dr.driver_id = d.driver_id
            WHERE d.delivery_id = @d
            """,
            r => new Extra(r.NullableString(0), r.NullableString(1), r.NullableString(2), r.NullableString(3), r.IsDBNull(4) ? null : r.GetDecimal(4), r.IsDBNull(5) ? null : r.GetDecimal(5),
                r.NullableString(6), r.NullableString(7), r.NullableString(8)),
            cancellationToken,
            ("d", query.DeliveryId)).ConfigureAwait(false))!;
        var lots = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT x.delivery_line_id, l.lot_code, loc.code, x.base_quantity
            FROM log.delivery_line_lot x JOIN log.delivery_line dl ON dl.delivery_line_id = x.delivery_line_id
            JOIN inv.lot l ON l.lot_id = x.lot_id JOIN md.location loc ON loc.location_id = x.source_location_id
            WHERE dl.delivery_id = @d ORDER BY l.lot_code
            """,
            r => (Line: r.GetGuid(0), Lot: new DeliveryLotView(r.GetString(1), r.GetString(2), r.GetDecimal(3))),
            cancellationToken,
            ("d", query.DeliveryId)).ConfigureAwait(false);
        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT dl.delivery_line_id, dl.line_no, dl.item_id, i.code, i.description, dl.uom, dl.qty_planned, loc.code, dl.qty_issued, dl.qty_delivered, dl.qty_returned, dl.qty_lost, dl.qty_invoiced
            FROM log.delivery_line dl JOIN md.item i ON i.item_id = dl.item_id LEFT JOIN md.location loc ON loc.location_id = dl.source_location_id
            WHERE dl.delivery_id = @d ORDER BY dl.line_no
            """,
            r => new DeliveryLineView(r.GetGuid(0), r.GetInt32(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetDecimal(6), r.NullableString(7), r.GetDecimal(8),
                r.GetDecimal(9), r.GetDecimal(10), r.GetDecimal(11), r.GetDecimal(12), []),
            cancellationToken,
            ("d", query.DeliveryId)).ConfigureAwait(false);
        var pod = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT received_by_name, received_at, evidence_ref, encode(evidence_sha256, 'hex') FROM log.pod WHERE delivery_id = @d",
            r => new PodView(r.GetString(0), r.GetFieldValue<DateTime>(1), r.GetString(2), r.GetString(3)),
            cancellationToken,
            ("d", query.DeliveryId)).ConfigureAwait(false);
        var assessments = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT trigger_point, result, policy_version_id, assessed_at FROM inv.control_assessment WHERE delivery_id = @d ORDER BY assessed_at",
            r => new ControlAssessmentView(r.GetString(0), r.GetString(1), r.NullableGuid(2), r.GetFieldValue<DateTime>(3)),
            cancellationToken,
            ("d", query.DeliveryId)).ConfigureAwait(false);
        var history = await StateHistory.ReadAsync(context, "Delivery", query.DeliveryId, cancellationToken).ConfigureAwait(false);
        return ApiJson.Serialize(new DeliveryDetail(
            header, extra.Plate, extra.Driver, extra.CustomerPlate, extra.CustomerDriver, extra.Gross, extra.Tare, extra.Ticket, extra.Exception, extra.Cancel,
            lines.Select(l => l with { Lots = lots.Where(x => x.Line == l.DeliveryLineId).Select(x => x.Lot).ToList() }).ToList(), pod, assessments, history));
    }
}
