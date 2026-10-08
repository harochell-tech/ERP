using System.Globalization;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;
using Rochell.Sales.Deliveries;

namespace Rochell.Sales.Queries;

// VS3-04: deliveries (conduces), read with sales:read.

/// <summary>
/// E-VS3-09-5: <paramref name="PartyId"/>, and <paramref name="From"/> / <paramref name="To"/> on the delivery's date — its gate-out date
/// (Dominican Republic), or the business date it was planned on while it has not left.
/// </summary>
public sealed record ListDeliveries(
    Guid CompanyId, Guid SessionId, string? Status = null, Guid? SalesOrderId = null, int Limit = 50, int Offset = 0, Guid? PartyId = null, DateOnly? From = null, DateOnly? To = null,
    Guid? VehicleId = null, Guid? DriverId = null, bool DriverReportedDifferences = false) : IQuery;

public sealed record DeliverySummary(
    Guid DeliveryId, string DeliveryNo, Guid SalesOrderId, string OrderNo, string CustomerName, string PlantCode, string DeliveryTermCode, string ControlTransfersAt, string Status,
    DateTime? GateOutAt, long Version, string? FleetCode, string? DriverName);

public sealed record DeliveryList(IReadOnlyList<DeliverySummary> Items, int Limit, int Offset);

internal static class DeliveryReading
{
    public const string Select = """
        SELECT d.delivery_id, d.delivery_no, d.sales_order_id, o.order_no, p.legal_name, pl.code, d.delivery_term_code, d.control_transfers_at, d.status, d.gate_out_at, d.version,
               (SELECT v.fleet_code FROM log.vehicle v WHERE v.vehicle_id = d.vehicle_id),
               coalesce((SELECT dr.full_name FROM log.driver dr WHERE dr.driver_id = d.driver_id), d.customer_driver_name)
        FROM log.delivery d
        JOIN sal.sales_order o ON o.sales_order_id = d.sales_order_id
        JOIN md.party p ON p.party_id = o.party_id
        JOIN md.plant pl ON pl.plant_id = d.plant_id
        """;

    public static DeliverySummary Map(System.Data.Common.DbDataReader r)
        => new(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8),
            r.IsDBNull(9) ? null : r.GetFieldValue<DateTime>(9), r.GetInt64(10), r.NullableString(11), r.NullableString(12));
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
              AND (CAST(@p AS uuid) IS NULL OR o.party_id = CAST(@p AS uuid))
              AND (CAST(@veh AS uuid) IS NULL OR d.vehicle_id = CAST(@veh AS uuid)) AND (CAST(@drv AS uuid) IS NULL OR d.driver_id = CAST(@drv AS uuid))
              AND (NOT @diff OR (d.status = 'IN_TRANSIT' AND EXISTS (
                     SELECT 1 FROM log.driver_confirmation c WHERE c.company_id = d.company_id AND c.delivery_id = d.delivery_id AND c.outcome = 'DIFFERENCES')))
              AND ((CAST(@from AS date) IS NULL AND CAST(@to AS date) IS NULL) OR coalesce((d.gate_out_at AT TIME ZONE 'America/Santo_Domingo')::date,
                     (SELECT min(e.business_date) FROM core.domain_event e WHERE e.company_id = d.company_id AND e.aggregate_id = d.delivery_id))
                   BETWEEN coalesce(CAST(@from AS date), DATE '0001-01-01') AND coalesce(CAST(@to AS date), DATE '9999-12-31'))
            ORDER BY d.delivery_no DESC
            LIMIT @limit OFFSET @offset
            """,
            DeliveryReading.Map,
            cancellationToken,
            ("c", context.CompanyId),
            ("s", query.Status),
            ("o", query.SalesOrderId),
            ("p", query.PartyId),
            ("veh", query.VehicleId),
            ("drv", query.DriverId),
            ("diff", query.DriverReportedDifferences),
            ("from", query.From),
            ("to", query.To),
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

/// <summary>ENT1-02 (E-ENT-1/2): the driver's link — its generation, state (EXPIRED once past its date) and failed PIN tries.</summary>
public sealed record DriverLinkView(int Generation, string Status, DateTime ExpiresAt, int FailedAttempts);

/// <summary>ENT1-02 (E-ENT-3, E-ENT1-01-7/10): what the driver confirmed; <paramref name="CompletedByPod"/> once a POD cites it.</summary>
public sealed record DriverConfirmationView(
    Guid ConfirmationId, string ReceiverName, string? ReceiverNationalId, string Outcome, string? Note, DateTime ConfirmedAt, DateTime? PhoneAt, DateTime ServerAt,
    decimal? Latitude, decimal? Longitude, decimal? AccuracyM, string EvidenceKind, string EvidenceSha256, bool CompletedByPod);

public sealed record DeliveryDetail(
    DeliverySummary Header, string? VehiclePlate, string? DriverName, string? CustomerVehiclePlate, string? CustomerDriverName, decimal? GrossKg, decimal? TareKg, string? WeighTicketRef,
    string? ExceptionReason, string? CancelReason, IReadOnlyList<DeliveryLineView> Lines, PodView? Pod, IReadOnlyList<ControlAssessmentView> Assessments, IReadOnlyList<StateChange> History,
    DriverLinkView? DriverLink = null, DriverConfirmationView? DriverConfirmation = null);

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
        var now = context.Clock.UtcNow;
        var link = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT generation, status, expires_at, failed_attempts FROM log.delivery_link WHERE delivery_id = @d",
            r => new DriverLinkView(r.GetInt32(0), r.GetString(1) == "ACTIVE" && r.GetFieldValue<DateTime>(2) <= now ? "EXPIRED" : r.GetString(1), r.GetFieldValue<DateTime>(2), r.GetInt32(3)),
            cancellationToken,
            ("d", query.DeliveryId)).ConfigureAwait(false);
        var confirmation = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT c.confirmation_id, c.receiver_name, c.receiver_national_id, c.outcome, c.note, c.confirmed_at, c.phone_at, c.server_at, c.latitude, c.longitude, c.accuracy_m,
                   c.evidence_kind, encode(c.evidence_sha256, 'hex'), EXISTS (SELECT 1 FROM log.pod p WHERE p.driver_confirmation_id = c.confirmation_id)
            FROM log.driver_confirmation c WHERE c.delivery_id = @d ORDER BY c.generation DESC LIMIT 1
            """,
            r => new DriverConfirmationView(
                r.GetGuid(0), r.GetString(1), r.NullableString(2), r.GetString(3), r.NullableString(4), r.GetFieldValue<DateTime>(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateTime>(6),
                r.GetFieldValue<DateTime>(7), r.IsDBNull(8) ? null : r.GetDecimal(8), r.IsDBNull(9) ? null : r.GetDecimal(9), r.IsDBNull(10) ? null : r.GetDecimal(10), r.GetString(11),
                r.GetString(12), r.GetBoolean(13)),
            cancellationToken,
            ("d", query.DeliveryId)).ConfigureAwait(false);
        return ApiJson.Serialize(new DeliveryDetail(
            header, extra.Plate, extra.Driver, extra.CustomerPlate, extra.CustomerDriver, extra.Gross, extra.Tare, extra.Ticket, extra.Exception, extra.Cancel,
            lines.Select(l => l with { Lots = lots.Where(x => x.Line == l.DeliveryLineId).Select(x => x.Lot).ToList() }).ToList(), pod, assessments, history, link, confirmation));
    }
}

/// <summary>E-UX3-7: the printable delivery note (conduce).</summary>
public sealed record GetDeliveryPrint(Guid CompanyId, Guid SessionId, Guid DeliveryId) : IQuery;

public sealed record DeliveryPrintLot(string LotCode, string SourceLocationCode, decimal BaseQuantity);

/// <summary>E-PRS-04-7: <paramref name="Freight"/> names the line's freight («Transporte de blocks — Bávaro»), same quantities, no price.</summary>
public sealed record DeliveryPrintLine(
    int LineNo, string ItemCode, string ItemDescription, string Uom, decimal QtyPlanned, decimal QtyIssued, decimal QtyDelivered, IReadOnlyList<DeliveryPrintLot> Lots,
    string? Freight = null);

/// <summary>
/// What the driver carries and the customer signs, mirroring GetQuotePrint: issuer and customer (legal name and RNC), the order's site
/// address, the plant, numbers and dates (the order date, the business date the delivery was planned on, the gate-out time), status,
/// the vehicle and driver (own fleet, or the customer's for a pickup), the weights (net = gross − tare, computed here: the screen does
/// no arithmetic) and weigh ticket, the lines with their lots, and who received it (POD).
/// </summary>
public sealed record DeliveryPrint(
    string IssuerName, string IssuerRnc, string CustomerName, string CustomerRnc, string? SiteAddress, string PlantCode, string? PlantName,
    string DeliveryNo, string OrderNo, DateOnly OrderDate, DateOnly? PlannedOn, string Status, string DeliveryTermCode, DateTime? GateOutAt,
    string? VehiclePlate, string? DriverName, string? CustomerVehiclePlate, string? CustomerDriverName, decimal? GrossKg, decimal? TareKg, decimal? NetKg,
    string? WeighTicketRef, IReadOnlyList<DeliveryPrintLine> Lines, string? ReceivedByName, DateTime? ReceivedAt, string? VehicleFleetCode, string? DriverLinkPath = null);

[RequiresPermission("sales:read")]
public sealed class GetDeliveryPrintHandler(DriverLinkKey? key = null) : IQueryHandler<GetDeliveryPrint>
{
    public string QueryType => "Sales.GetDeliveryPrint";

    private sealed record LinkGeneration(int Generation);

    public async Task<string> HandleAsync(GetDeliveryPrint query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var print = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT c.legal_name, c.rnc, p.legal_name, p.rnc, o.site_address, pl.code, pl.name, d.delivery_no, o.order_no, o.order_date,
                   (SELECT min(e.business_date) FROM core.domain_event e WHERE e.company_id = d.company_id AND e.aggregate_id = d.delivery_id),
                   d.status, d.delivery_term_code, d.gate_out_at, v.plate, dr.full_name, d.customer_vehicle_plate, d.customer_driver_name,
                   d.gross_kg, d.tare_kg, d.gross_kg - d.tare_kg, d.weigh_ticket_ref, pod.received_by_name, pod.received_at, v.fleet_code
            FROM log.delivery d
            JOIN md.company c ON c.company_id = d.company_id
            JOIN sal.sales_order o ON o.sales_order_id = d.sales_order_id
            JOIN md.party p ON p.party_id = o.party_id
            JOIN md.plant pl ON pl.plant_id = d.plant_id
            LEFT JOIN log.vehicle v ON v.vehicle_id = d.vehicle_id
            LEFT JOIN log.driver dr ON dr.driver_id = d.driver_id
            LEFT JOIN log.pod pod ON pod.delivery_id = d.delivery_id
            WHERE d.company_id = @c AND d.delivery_id = @d
            """,
            r => new DeliveryPrint(
                r.GetString(0), r.GetString(1), r.GetString(2), r.NullableString(3) ?? string.Empty, r.NullableString(4), r.GetString(5), r.NullableString(6),
                r.GetString(7), r.GetString(8), r.Date(9), r.IsDBNull(10) ? null : r.Date(10), r.GetString(11), r.GetString(12), r.NullableUtc(13),
                r.NullableString(14), r.NullableString(15), r.NullableString(16), r.NullableString(17), r.NullableDecimal(18), r.NullableDecimal(19), r.NullableDecimal(20),
                r.NullableString(21), [], r.NullableString(22), r.NullableUtc(23), r.NullableString(24)),
            cancellationToken,
            ("c", context.CompanyId),
            ("d", query.DeliveryId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The delivery does not exist.");
        var lots = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT x.delivery_line_id, l.lot_code, loc.code, x.base_quantity
            FROM log.delivery_line_lot x JOIN log.delivery_line dl ON dl.delivery_line_id = x.delivery_line_id
            JOIN inv.lot l ON l.lot_id = x.lot_id JOIN md.location loc ON loc.location_id = x.source_location_id
            WHERE dl.delivery_id = @d ORDER BY l.lot_code
            """,
            r => (Line: r.GetGuid(0), Lot: new DeliveryPrintLot(r.GetString(1), r.GetString(2), r.GetDecimal(3))),
            cancellationToken,
            ("d", query.DeliveryId)).ConfigureAwait(false))
            .ToLookup(x => x.Line, x => x.Lot);
        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT dl.delivery_line_id, dl.line_no, i.code, i.description, dl.uom, dl.qty_planned, dl.qty_issued, dl.qty_delivered,
                   CASE WHEN ol.freight_unit_price IS NOT NULL
                        THEN coalesce((SELECT f.description FROM md.item f WHERE f.company_id = dl.company_id AND f.item_category = 'TRANSPORTE'), 'Transporte') || ' — ' || z.name END
            FROM log.delivery_line dl JOIN md.item i ON i.item_id = dl.item_id
            JOIN sal.sales_order_line ol ON ol.line_id = dl.sales_order_line_id
            JOIN sal.sales_order o ON o.sales_order_id = ol.sales_order_id
            LEFT JOIN sal.delivery_zone z ON z.zone_id = o.delivery_zone_id
            WHERE dl.delivery_id = @d ORDER BY dl.line_no
            """,
            r => new DeliveryPrintLine(
                r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), lots[r.GetGuid(0)].ToList(), r.NullableString(8)),
            cancellationToken,
            ("d", query.DeliveryId)).ConfigureAwait(false);
        // E-ENT-1/8: the driver's QR while the delivery is in transit and its link usable — the page path with the link's HMAC.
        var link = key is null ? null : await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT k.generation FROM log.delivery_link k JOIN log.delivery d ON d.delivery_id = k.delivery_id WHERE k.delivery_id = @d AND k.status = 'ACTIVE' AND d.status = 'IN_TRANSIT'",
            r => new LinkGeneration(r.GetInt32(0)),
            cancellationToken,
            ("d", query.DeliveryId)).ConfigureAwait(false);
        var path = link is null ? null
            : string.Create(CultureInfo.InvariantCulture, $"/entrega/?c={context.CompanyId:N}&d={query.DeliveryId:N}&g={link.Generation}&k={key!.Mac(context.CompanyId, query.DeliveryId, link.Generation)}");
        return ApiJson.Serialize(print with { Lines = lines, DriverLinkPath = path });
    }
}
