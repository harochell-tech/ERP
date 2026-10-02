using System.Globalization;
using System.Text.Json;
using Rochell.Finance.Policies;
using Rochell.Finance.Posting;
using Rochell.Inventory;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Time;
using Rochell.Sales.Orders;
using Rochell.Sales.Proformas;

namespace Rochell.Sales.Deliveries;

public static class DeliveryErrors
{
    public const string QuantityExceedsOpen = "QUANTITY_EXCEEDS_OPEN";
    public const string TransportRequired = "TRANSPORT_REQUIRED";
    public const string LocationInvalid = "LOCATION_INVALID";
    public const string InsufficientStock = "INSUFFICIENT_STOCK";
    public const string WeighingInvalid = "WEIGHING_INVALID";
    public const string OverCapacity = "VEHICLE_OVER_CAPACITY";
    public const string EvidenceInvalid = "EVIDENCE_INVALID";
    public const string PodQuantitiesInvalid = "POD_QUANTITIES_INVALID";
    public const string ExceptionReasonRequired = "EXCEPTION_REASON_REQUIRED";
    public const string OpenDeliveries = "OPEN_DELIVERIES";
    public const string UomNotConvertible = "UOM_NOT_CONVERTIBLE";
}

internal static class Deliveries
{
    public const string Aggregate = "Delivery";
    public const string DocumentType = "DELIVERY";
    public const string RevenuePolicy = "REVENUE_ACCOUNTING";
    public const string Presentation = "unbilled_delivery_presentation";

    public sealed record Row(Guid SalesOrderId, Guid PlantId, string Term, int TermVersion, string Control, string Status, Guid? VehicleId, string DeliveryNo, long Version);

    public sealed record Line(
        Guid Id, int LineNo, Guid OrderLineId, Guid ItemId, string Uom, decimal Planned, decimal? Factor, Guid? Source, decimal Issued, decimal Delivered, decimal Returned, decimal Lost,
        decimal UnitPrice);

    public sealed record Portion(Guid LineId, Guid LotId, Guid FromLocation, decimal BaseQuantity);

    public static async Task<Guid> OrderOfAsync(CommandContext context, Guid deliveryId, CancellationToken cancellationToken)
        => await SalesSql.ScalarAsync<Guid?>(
               context, "SELECT sales_order_id FROM log.delivery WHERE company_id = @c AND delivery_id = @d", cancellationToken, ("c", context.CompanyId), ("d", deliveryId)).ConfigureAwait(false)
           ?? throw new DomainException(SalesErrors.NotFound, "The delivery does not exist.");

    public static async Task<Row> LockAsync(CommandContext context, Guid deliveryId, long expectedVersion, CancellationToken cancellationToken)
    {
        var row = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT sales_order_id, plant_id, delivery_term_code, term_version, control_transfers_at, status, vehicle_id, delivery_no, version
            FROM log.delivery WHERE company_id = @c AND delivery_id = @d FOR UPDATE
            """,
            r => new Row(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetString(5), r.NullableGuid(6), r.GetString(7), r.GetInt64(8)),
            cancellationToken,
            ("c", context.CompanyId),
            ("d", deliveryId)).ConfigureAwait(false)
            ?? throw new DomainException(SalesErrors.NotFound, "The delivery does not exist.");
        return row.Version == expectedVersion
            ? row
            : throw new DomainException(SalesErrors.VersionConflict, $"The delivery changed (version {row.Version}, expected {expectedVersion}); reload and retry.");
    }

    /// <summary>Locks the order first, then the delivery (the global lock order of VS#3 §5).</summary>
    public static async Task<(Orders.Orders.Row Order, Row Delivery)> LockWithOrderAsync(CommandContext context, Guid deliveryId, long expectedVersion, CancellationToken cancellationToken)
    {
        var orderId = await OrderOfAsync(context, deliveryId, cancellationToken).ConfigureAwait(false);
        var order = await Orders.Orders.LockCurrentAsync(context, orderId, cancellationToken).ConfigureAwait(false);
        return (order, await LockAsync(context, deliveryId, expectedVersion, cancellationToken).ConfigureAwait(false));
    }

    public static Task<List<Line>> LinesAsync(CommandContext context, Guid deliveryId, CancellationToken cancellationToken)
        => Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT d.delivery_line_id, d.line_no, d.sales_order_line_id, d.item_id, d.uom, d.qty_planned, d.base_factor, d.source_location_id, d.qty_issued, d.qty_delivered,
                   d.qty_returned, d.qty_lost, o.unit_price
            FROM log.delivery_line d JOIN sal.sales_order_line o ON o.line_id = d.sales_order_line_id
            WHERE d.delivery_id = @d ORDER BY d.line_no FOR UPDATE OF d
            """,
            r => new Line(r.GetGuid(0), r.GetInt32(1), r.GetGuid(2), r.GetGuid(3), r.GetString(4), r.GetDecimal(5), r.IsDBNull(6) ? null : r.GetDecimal(6), r.NullableGuid(7),
                r.GetDecimal(8), r.GetDecimal(9), r.GetDecimal(10), r.GetDecimal(11), r.GetDecimal(12)),
            cancellationToken,
            ("d", deliveryId));

    public static async Task<Guid> AppendAsync(CommandContext context, Guid deliveryId, long version, string eventType, object payload, DateTime occurredAt, DateOnly businessDate, CancellationToken cancellationToken)
        => await context.AppendEventAsync(
            new EventDraft(eventType, 1, Aggregate, deliveryId, version, JsonSerializer.Serialize(payload), Publish: true, OccurredAt: occurredAt, BusinessDate: businessDate),
            cancellationToken).ConfigureAwait(false);

    public static decimal Base(decimal quantity, decimal factor) => decimal.Round(quantity * factor, 6, MidpointRounding.AwayFromZero);

    /// <summary>E-VS3-04-1: the plant's TRANSITO location, created the first time the plant dispatches in its own truck.</summary>
    public static async Task<Guid> TransitLocationAsync(CommandContext context, Guid plantId, CancellationToken cancellationToken)
    {
        await SalesSql.LockAsync(context, "transit-location:" + plantId.ToString(), cancellationToken).ConfigureAwait(false);
        var existing = await SalesSql.ScalarAsync<Guid?>(context, "SELECT location_id FROM md.location WHERE plant_id = @p AND is_transit", cancellationToken, ("p", plantId)).ConfigureAwait(false);
        if (existing is { } id)
        {
            return id;
        }

        var location = context.Ids.NewId();
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "INSERT INTO md.location (location_id, company_id, plant_id, code, is_transit) VALUES (@id, @c, @p, 'TRANSITO', true)",
            cancellationToken,
            ("id", location),
            ("c", context.CompanyId),
            ("p", plantId)).ConfigureAwait(false);
        return location;
    }

    /// <summary>The lots of an item in a location, oldest code first (E-VS3-04-5), covering <paramref name="baseQuantity"/>.</summary>
    public static async Task<List<(Guid LotId, decimal Quantity)>> FifoAsync(CommandContext context, Guid locationId, Guid itemId, decimal baseQuantity, CancellationToken cancellationToken)
    {
        var lots = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT s.lot_id, s.quantity FROM inv.inv_stock_balance s JOIN inv.lot l ON l.lot_id = s.lot_id
            WHERE s.location_id = @loc AND s.item_id = @i AND s.quantity > 0 ORDER BY l.lot_code, l.lot_id
            """,
            r => (r.GetGuid(0), r.GetDecimal(1)),
            cancellationToken,
            ("loc", locationId),
            ("i", itemId)).ConfigureAwait(false);
        var picked = new List<(Guid, decimal)>();
        var remaining = baseQuantity;
        foreach (var (lot, available) in lots)
        {
            if (remaining <= 0m)
            {
                break;
            }

            var take = Math.Min(available, remaining);
            picked.Add((lot, take));
            remaining -= take;
        }

        return remaining <= 0m
            ? picked
            : throw new DomainException(DeliveryErrors.InsufficientStock, $"The location holds less than the {baseQuantity} base units to dispatch.");
    }

    /// <summary>Splits <paramref name="quantity"/> over a line's lots (in their FIFO order), consuming what earlier splits took.</summary>
    public static List<Portion> Take(List<Portion> available, decimal quantity)
    {
        var taken = new List<Portion>();
        for (var i = 0; i < available.Count && quantity > 0m; i++)
        {
            var p = available[i];
            var q = Math.Min(p.BaseQuantity, quantity);
            if (q <= 0m)
            {
                continue;
            }

            taken.Add(p with { BaseQuantity = q });
            available[i] = p with { BaseQuantity = p.BaseQuantity - q };
            quantity -= q;
        }

        return taken;
    }

    public static byte[] Sha256(string? hex)
    {
        var value = (hex ?? string.Empty).Trim();
        return value.Length == 64 && value.All(Uri.IsHexDigit)
            ? Convert.FromHexString(value)
            : throw new DomainException(DeliveryErrors.EvidenceInvalid, "The evidence is a reference and the SHA-256 of its file (64 hex characters, E-VS3-6).");
    }

    public static string M(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    public static Task InsertAssessmentAsync(
        CommandContext context, Guid deliveryId, Row row, string trigger, Guid triggerEventId, Guid? policyVersionId, object inputs, CancellationToken cancellationToken)
        => Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO inv.control_assessment (assessment_id, company_id, delivery_id, trigger_point, trigger_event_id, term_code, term_version, control_transfers_at,
              policy_version_id, result, inputs, assessed_at)
            VALUES (@id, @c, @d, @t, @e, @term, @tv, @ctrl, @pv, @r, CAST(@inputs AS jsonb), @at)
            """,
            cancellationToken,
            ("id", context.Ids.NewId()),
            ("c", context.CompanyId),
            ("d", deliveryId),
            ("t", trigger),
            ("e", triggerEventId),
            ("term", row.Term),
            ("tv", row.TermVersion),
            ("ctrl", row.Control),
            ("pv", policyVersionId),
            ("r", policyVersionId is null ? "RETAINED" : "TRANSFERRED"),
            ("inputs", JsonSerializer.Serialize(inputs)),
            ("at", context.Clock.UtcNow));
}

/// <summary>Builds the journals of a delivery event (P-15, P-15R, P-16, P-30) from the written movements.</summary>
internal sealed class DeliveryPosting(Guid plantId, Guid partyId, string deliveryNo, string orderNo)
{
    private readonly List<PostingLineInput> _p15 = [];
    private readonly List<PostingLineInput> _p15r = [];
    private readonly List<PostingLineInput> _p16 = [];
    private readonly List<PostingLineInput> _p30 = [];

    public ResolvedPolicy? Revenue { get; set; }

    private Dictionary<string, string> Inputs(Guid? deliveryLineId = null)
    {
        var inputs = new Dictionary<string, string> { ["delivery_no"] = deliveryNo, ["order_no"] = orderNo };
        if (deliveryLineId is { } l)
        {
            inputs["delivery_line_id"] = l.ToString();
        }

        if (Revenue is not null)
        {
            inputs["policy_version_id"] = Revenue.PolicyVersionId.ToString();
        }

        return inputs;
    }

    public void Transfer(Guid itemId, decimal value, Guid outEntry, Guid inEntry, bool toTransit)
    {
        var list = toTransit ? _p15 : _p15r;
        var (dr, cr) = toTransit ? ("P15-DR-TRANSIT", "P15-CR-FG") : ("P15R-DR-FG", "P15R-CR-TRANSIT");
        list.Add(new PostingLineInput(dr, "transfer_value", value, PlantId: plantId, ItemId: itemId, SubledgerRef: inEntry, InvValueEntryId: inEntry, Inputs: Inputs()));
        list.Add(new PostingLineInput(cr, "transfer_value", value, PlantId: plantId, ItemId: itemId, SubledgerRef: outEntry, InvValueEntryId: outEntry, Inputs: Inputs()));
    }

    public void Cost(Guid itemId, decimal value, Guid valueEntry, bool fromTransit)
    {
        _p16.Add(new PostingLineInput("P16-DR-COGS", "cost_value", value, PlantId: plantId, ItemId: itemId, Inputs: Inputs()));
        _p16.Add(new PostingLineInput(fromTransit ? "P16-CR-TRANSIT" : "P16-CR-FG", "cost_value", value, PlantId: plantId, ItemId: itemId, SubledgerRef: valueEntry, InvValueEntryId: valueEntry, Inputs: Inputs()));
    }

    public void Revenue_(Guid deliveryLineId, Guid itemId, decimal revenue)
    {
        var code = Revenue!.Text(Deliveries.Presentation) == "UNBILLED_RECEIVABLE" ? "P16-DR-UR" : "P16-DR-CA";
        _p16.Add(new PostingLineInput(code, "revenue", revenue, PartyId: partyId, SubledgerRef: deliveryLineId, Inputs: Inputs(deliveryLineId)));
        _p16.Add(new PostingLineInput("P16-CR-REV", "revenue", revenue, PlantId: plantId, ItemId: itemId, PartyId: partyId, Inputs: Inputs(deliveryLineId)));
    }

    public void Loss(Guid itemId, decimal value, Guid valueEntry)
    {
        _p30.Add(new PostingLineInput("P30-DR-LOSS", "loss_value", value, PlantId: plantId, ItemId: itemId, Inputs: Inputs()));
        _p30.Add(new PostingLineInput("P30-CR-TRANSIT", "loss_value", value, PlantId: plantId, ItemId: itemId, SubledgerRef: valueEntry, InvValueEntryId: valueEntry, Inputs: Inputs()));
    }

    public bool HasTransfers => _p15.Count > 0;

    public bool HasReturns => _p15r.Count > 0;

    public bool HasControl => _p16.Count > 0;

    public bool HasLosses => _p30.Count > 0;

    /// <summary>Prepares and writes one rule's journal for its event; the plan's date must be the date the movements used.</summary>
    public async Task<Guid?> WriteAsync(PostingEngine engine, CommandContext context, string rule, Guid eventId, DateOnly businessDate, DateTime occurredAt, DateOnly postingDate, CancellationToken cancellationToken)
    {
        var lines = rule switch { "P-15" => _p15, "P-15R" => _p15r, "P-16" => _p16, _ => _p30 };
        if (lines.Count == 0)
        {
            return null;
        }

        var plan = await engine.PrepareAsync(context, new PostingRequest(rule, businessDate, occurredAt, lines), cancellationToken).ConfigureAwait(false);
        if (plan.PostingDate != postingDate)
        {
            throw new InvalidOperationException($"{rule} resolved {plan.PostingDate} but the movements were dated {postingDate}.");
        }

        return (await engine.WriteAsync(context, plan, eventId, cancellationToken).ConfigureAwait(false)).JournalId;
    }
}

[RequiresPermission("delivery:manage")]
public sealed class PlanDeliveryHandler : ICommandHandler<PlanDelivery>
{
    public string CommandType => "Sales.PlanDelivery";

    private sealed record OpenLine(Guid LineId, Guid ItemId, string Uom, decimal Ordered, decimal Delivered, decimal Planned);

    public async Task<string> HandleAsync(PlanDelivery command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var input = command.Lines ?? [];
        if (input.Count == 0 || input.Select(l => l.SalesOrderLineId).Distinct().Count() != input.Count)
        {
            throw new DomainException(SalesErrors.LinesRequired, "A delivery has at least one line and each order line once.");
        }

        var order = await Orders.Orders.LockCurrentAsync(context, command.SalesOrderId, cancellationToken).ConfigureAwait(false);

        // E-CF1-05-14: a cash sale still waiting for its payment is confirmed here once the money that counts covers it (a cheque
        // whose deposit Treasury matched with the bank), so nobody has to press «Verificar pago» before the first delivery.
        if (order.CashSale && order.Status == "PENDING_PAYMENT")
        {
            if (!await CashSales.CashSaleStore.ConfirmIfPaidAsync(context, command.SalesOrderId, CommandType, cancellationToken).ConfigureAwait(false))
            {
                await CashSales.CashSaleStore.EnsureCoveredAsync(context, command.SalesOrderId, order, cancellationToken).ConfigureAwait(false);
            }

            order = await Orders.Orders.LockCurrentAsync(context, command.SalesOrderId, cancellationToken).ConfigureAwait(false);
        }

        if (order.Status is not ("CONFIRMED" or "PARTIALLY_DELIVERED"))
        {
            throw new DomainException(SalesErrors.InvalidState, $"The order is {order.Status}; only CONFIRMED or PARTIALLY_DELIVERED orders are delivered.");
        }

        // E-CF1-4, E-CF1-02-2: a cash sale is planned only while the money that counts covers it.
        await CashSales.CashSaleStore.EnsureCoveredAsync(context, command.SalesOrderId, order, cancellationToken).ConfigureAwait(false);

        var head = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT o.plant_id, o.delivery_term_code, p.version, p.control_transfers_at FROM sal.sales_order o JOIN log.delivery_term_policy p ON p.term_code = o.delivery_term_code WHERE o.sales_order_id = @o ORDER BY p.version DESC LIMIT 1",
            r => new[] { r.GetGuid(0).ToString(), r.GetString(1), r.GetInt32(2).ToString(CultureInfo.InvariantCulture), r.GetString(3) },
            cancellationToken,
            ("o", command.SalesOrderId)).ConfigureAwait(false)
            ?? throw new DomainException(SalesErrors.NotFound, "The order's delivery term has no policy.");
        var open = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.line_id, l.item_id, l.uom, l.qty_ordered, l.qty_delivered,
                   coalesce((SELECT sum(dl.qty_planned) FROM log.delivery_line dl JOIN log.delivery d ON d.delivery_id = dl.delivery_id
                             WHERE dl.sales_order_line_id = l.line_id AND d.status IN ('PLANNED', 'LOADING', 'LOADED', 'IN_TRANSIT')), 0)
            FROM sal.sales_order o JOIN sal.sales_order_line l ON l.sales_order_id = o.sales_order_id AND l.lines_version = o.lines_version
            WHERE o.sales_order_id = @o
            """,
            r => new OpenLine(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5)),
            cancellationToken,
            ("o", command.SalesOrderId)).ConfigureAwait(false);
        foreach (var line in input)
        {
            var o = open.SingleOrDefault(x => x.LineId == line.SalesOrderLineId)
                ?? throw new DomainException(SalesErrors.NotFound, "A delivery line does not belong to the order.");
            if (line.Quantity <= 0m || decimal.Round(line.Quantity, 6) != line.Quantity)
            {
                throw new DomainException(SalesErrors.AmountInvalid, "Delivery quantities are positive with at most 6 decimals.");
            }

            if (line.Quantity > o.Ordered - o.Delivered - o.Planned)
            {
                throw new DomainException(DeliveryErrors.QuantityExceedsOpen, $"Order line of {o.Uom}: {line.Quantity} exceeds the open {o.Ordered - o.Delivered - o.Planned} (ordered − delivered − planned).");
            }
        }

        var creator = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        await SalesSql.LockAsync(context, "delivery-no", cancellationToken).ConfigureAwait(false);
        var last = await SalesSql.ScalarAsync<int?>(
            context, "SELECT max(substring(delivery_no from 4)::int) FROM log.delivery WHERE company_id = @c", cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false) ?? 0;
        var deliveryNo = "CD-" + (last + 1).ToString("D6", CultureInfo.InvariantCulture);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "DeliveryPlanned",
                1,
                Deliveries.Aggregate,
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new { deliveryId = context.ResultRef, deliveryNo, salesOrderId = command.SalesOrderId, orderNo = order.OrderNo, term = head[1], controlTransfersAt = head[3], lines = input.Select(l => new { salesOrderLineId = l.SalesOrderLineId, quantity = Deliveries.M(l.Quantity) }) }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO log.delivery (delivery_id, company_id, delivery_no, sales_order_id, plant_id, delivery_term_code, term_version, control_transfers_at, status, created_by, version)
            VALUES (@id, @c, @no, @o, @plant, @term, @tv, @ctrl, 'PLANNED', @by, 1)
            """,
            cancellationToken,
            ("id", context.ResultRef),
            ("c", context.CompanyId),
            ("no", deliveryNo),
            ("o", command.SalesOrderId),
            ("plant", Guid.Parse(head[0])),
            ("term", head[1]),
            ("tv", int.Parse(head[2], CultureInfo.InvariantCulture)),
            ("ctrl", head[3]),
            ("by", creator)).ConfigureAwait(false);
        var no = 0;
        foreach (var line in input)
        {
            var o = open.Single(x => x.LineId == line.SalesOrderLineId);
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "INSERT INTO log.delivery_line (delivery_line_id, company_id, delivery_id, line_no, sales_order_line_id, item_id, uom, qty_planned) VALUES (@id, @c, @d, @no, @ol, @i, @u, @q)",
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("d", context.ResultRef),
                ("no", ++no),
                ("ol", o.LineId),
                ("i", o.ItemId),
                ("u", o.Uom),
                ("q", line.Quantity)).ConfigureAwait(false);
        }

        await context.AppendStateAsync(Deliveries.Aggregate, context.ResultRef, "DOCUMENT", null, "PLANNED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { deliveryId = context.ResultRef, deliveryNo, status = "PLANNED", version = 1 });
    }
}

[RequiresPermission("delivery:manage")]
public sealed class StartLoadingHandler : ICommandHandler<StartLoading>
{
    public string CommandType => "Sales.StartLoading";

    public async Task<string> HandleAsync(StartLoading command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await Deliveries.LockAsync(context, command.DeliveryId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "PLANNED")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The delivery is {row.Status}.");
        }

        string? plate = null;
        string? driverName = null;
        if (row.Term == DeliveryTerms.DeliveredOwnTransport)
        {
            // E-VS3-04-6: our vehicle and driver, both ACTIVE.
            var active = command.VehicleId is { } v && command.DriverId is { } dr && await SalesSql.ScalarAsync<long?>(
                context,
                "SELECT (SELECT count(*) FROM log.vehicle WHERE company_id = @c AND vehicle_id = @v AND status = 'ACTIVE') + (SELECT count(*) FROM log.driver WHERE company_id = @c AND driver_id = @d AND status = 'ACTIVE')",
                cancellationToken,
                ("c", context.CompanyId),
                ("v", v),
                ("d", dr)).ConfigureAwait(false) == 2;
            if (!active)
            {
                throw new DomainException(DeliveryErrors.TransportRequired, "A site delivery leaves in one of our ACTIVE vehicles with an ACTIVE driver.");
            }
        }
        else
        {
            plate = SalesSql.Optional(command.CustomerVehiclePlate, 20, "The customer's plate")?.ToUpperInvariant();
            driverName = SalesSql.Optional(command.CustomerDriverName, 200, "The customer's driver");
            if (plate is null || driverName is null)
            {
                throw new DomainException(DeliveryErrors.TransportRequired, "A pickup records the customer's plate and driver.");
            }
        }

        var version = row.Version + 1;
        var eventId = await Deliveries.AppendAsync(
            context, command.DeliveryId, version, "DeliveryLoadingStarted",
            new { deliveryId = command.DeliveryId, vehicleId = command.VehicleId, driverId = command.DriverId, customerVehiclePlate = plate, customerDriverName = driverName },
            context.Clock.UtcNow, SalesSql.Today(context), cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            UPDATE log.delivery SET status = 'LOADING', vehicle_id = @v, driver_id = @dr, customer_vehicle_plate = @plate, customer_driver_name = @name, version = @ver
            WHERE delivery_id = @d
            """,
            cancellationToken,
            ("v", row.Term == DeliveryTerms.DeliveredOwnTransport ? command.VehicleId : null),
            ("dr", row.Term == DeliveryTerms.DeliveredOwnTransport ? command.DriverId : null),
            ("plate", plate),
            ("name", driverName),
            ("ver", version),
            ("d", command.DeliveryId)).ConfigureAwait(false);
        await context.AppendStateAsync(Deliveries.Aggregate, command.DeliveryId, "DOCUMENT", "PLANNED", "LOADING", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { deliveryId = command.DeliveryId, status = "LOADING", version });
    }
}

[RequiresPermission("delivery:manage")]
public sealed class ConfirmLoadedHandler : ICommandHandler<ConfirmLoaded>
{
    public string CommandType => "Sales.ConfirmLoaded";

    public async Task<string> HandleAsync(ConfirmLoaded command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await Deliveries.LockAsync(context, command.DeliveryId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "LOADING")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The delivery is {row.Status}.");
        }

        var lines = await Deliveries.LinesAsync(context, command.DeliveryId, cancellationToken).ConfigureAwait(false);
        var input = command.Lines ?? [];
        if (input.Count != lines.Count || lines.Any(l => input.Count(i => i.DeliveryLineId == l.Id) != 1))
        {
            throw new DomainException(SalesErrors.LinesRequired, "Every delivery line gets its source location, once.");
        }

        var today = SalesSql.Today(context);
        foreach (var line in lines)
        {
            var location = input.Single(i => i.DeliveryLineId == line.Id).SourceLocationId;
            if (await SalesSql.ScalarAsync<Guid?>(
                    context, "SELECT location_id FROM md.location WHERE company_id = @c AND location_id = @l AND plant_id = @p AND NOT is_transit AND NOT is_curing", cancellationToken,
                    ("c", context.CompanyId), ("l", location), ("p", row.PlantId)).ConfigureAwait(false) is null)
            {
                throw new DomainException(DeliveryErrors.LocationInvalid, "The source location is a (non-transit) location of the delivery's plant.");
            }

            var baseUom = await SalesSql.ScalarAsync<string>(context, "SELECT base_uom FROM md.item WHERE item_id = @i", cancellationToken, ("i", line.ItemId)).ConfigureAwait(false);
            var factor = line.Uom == baseUom ? 1m : await SalesSql.ScalarAsync<decimal?>(
                context,
                """
                SELECT factor FROM md.uom_conversion WHERE company_id = @c AND item_id = @i AND from_uom = @u AND to_uom = @b
                  AND effective_from <= @d AND (effective_to IS NULL OR effective_to > @d)
                """,
                cancellationToken,
                ("c", context.CompanyId),
                ("i", line.ItemId),
                ("u", line.Uom),
                ("b", baseUom),
                ("d", today)).ConfigureAwait(false)
                ?? throw new DomainException(DeliveryErrors.UomNotConvertible, $"Unit {line.Uom} has no conversion in force to {baseUom}.");
            var available = await SalesSql.ScalarAsync<decimal?>(
                context, "SELECT sum(quantity) FROM inv.inv_stock_balance WHERE location_id = @l AND item_id = @i", cancellationToken, ("l", location), ("i", line.ItemId)).ConfigureAwait(false) ?? 0m;
            var needed = Deliveries.Base(line.Planned, factor);
            if (available < needed)
            {
                throw new DomainException(DeliveryErrors.InsufficientStock, $"Line {line.LineNo}: the location holds {available} of the {needed} base units to load (E-VS3-04-4).");
            }

            await Sql.ExecuteAsync(
                context.Connection, context.Transaction, "UPDATE log.delivery_line SET source_location_id = @l, base_factor = @f WHERE delivery_line_id = @id", cancellationToken,
                ("l", location), ("f", factor), ("id", line.Id)).ConfigureAwait(false);
        }

        var version = row.Version + 1;
        var eventId = await Deliveries.AppendAsync(
            context, command.DeliveryId, version, "DeliveryLoaded",
            new { deliveryId = command.DeliveryId, lines = input.Select(i => new { deliveryLineId = i.DeliveryLineId, sourceLocationId = i.SourceLocationId }) },
            context.Clock.UtcNow, today, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE log.delivery SET status = 'LOADED', version = @v WHERE delivery_id = @d", cancellationToken, ("v", version), ("d", command.DeliveryId)).ConfigureAwait(false);
        await context.AppendStateAsync(Deliveries.Aggregate, command.DeliveryId, "DOCUMENT", "LOADING", "LOADED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { deliveryId = command.DeliveryId, status = "LOADED", version });
    }
}

[RequiresPermission("delivery:manage")]
public sealed class RecordGateOutHandler : ICommandHandler<RecordGateOut>
{
    private readonly PostingEngine _engine = new();
    private readonly InventoryLedger _inventory = new();

    public string CommandType => "Sales.RecordGateOut";

    public async Task<string> HandleAsync(RecordGateOut command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var ticket = SalesSql.Optional(command.WeighTicketRef, 100, "The weigh ticket")
            ?? throw new DomainException(DeliveryErrors.WeighingInvalid, "The weigh ticket reference is required.");
        var ticketHash = Deliveries.Sha256(command.WeighTicketSha256);
        if (command.TareKg < 0m || command.GrossKg <= command.TareKg || decimal.Round(command.GrossKg, 6) != command.GrossKg || decimal.Round(command.TareKg, 6) != command.TareKg)
        {
            throw new DomainException(DeliveryErrors.WeighingInvalid, "Gross weight above tare, both in kg with at most 6 decimals.");
        }

        var (order, row) = await Deliveries.LockWithOrderAsync(context, command.DeliveryId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "LOADED")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The delivery is {row.Status}.");
        }

        // E-CF1-4, E-CF1-02-2: a cash sale leaves the gate only while the money that counts covers it (a bounced cheque stops it).
        await CashSales.CashSaleStore.EnsureCoveredAsync(context, row.SalesOrderId, order, cancellationToken).ConfigureAwait(false);

        if (row.VehicleId is { } vehicle && await SalesSql.ScalarAsync<decimal?>(context, "SELECT capacity_kg FROM log.vehicle WHERE vehicle_id = @v", cancellationToken, ("v", vehicle)).ConfigureAwait(false) is { } capacity
            && command.GrossKg - command.TareKg > capacity)
        {
            throw new DomainException(DeliveryErrors.OverCapacity, $"Net weight {command.GrossKg - command.TareKg} kg exceeds the vehicle's {capacity} kg (E-VS3-04-7).");
        }

        var lines = await Deliveries.LinesAsync(context, command.DeliveryId, cancellationToken).ConfigureAwait(false);
        var party = await SalesSql.ScalarAsync<Guid?>(context, "SELECT party_id FROM sal.sales_order WHERE sales_order_id = @o", cancellationToken, ("o", row.SalesOrderId)).ConfigureAwait(false);
        var occurredAt = context.Clock.UtcNow;
        var businessDate = SalesSql.Today(context);
        var atGate = row.Control == "GATE_OUT";
        var posting = new DeliveryPosting(row.PlantId, party!.Value, row.DeliveryNo, order.OrderNo);
        if (atGate)
        {
            posting.Revenue = await PolicyResolver.ResolveAsync(context, Deliveries.RevenuePolicy, businessDate, cancellationToken).ConfigureAwait(false);
        }

        var postingDate = await _engine.PostingDateAsync(context, atGate ? "P-16" : "P-15", businessDate, cancellationToken).ConfigureAwait(false);
        var transit = atGate ? (Guid?)null : await Deliveries.TransitLocationAsync(context, row.PlantId, cancellationToken).ConfigureAwait(false);
        var version = row.Version + 1;
        var issued = await Deliveries.AppendAsync(
            context, command.DeliveryId, version, "GoodsIssued",
            new { deliveryId = command.DeliveryId, deliveryNo = row.DeliveryNo, grossKg = Deliveries.M(command.GrossKg), tareKg = Deliveries.M(command.TareKg), weighTicketRef = ticket, controlTransfersAt = row.Control },
            occurredAt, businessDate, cancellationToken).ConfigureAwait(false);
        var control = atGate
            ? await Deliveries.AppendAsync(context, command.DeliveryId, version, "ControlTransferred", new { deliveryId = command.DeliveryId, deliveryNo = row.DeliveryNo, trigger = "GATE_OUT" }, occurredAt, businessDate, cancellationToken).ConfigureAwait(false)
            : (Guid?)null;
        var movementEvent = control ?? issued;
        var dates = new MovementDates(occurredAt, businessDate, postingDate);
        var delivered = new Dictionary<Guid, decimal>();
        var reached = new List<DeliveredLine>();
        foreach (var line in lines)
        {
            var baseQuantity = Deliveries.Base(line.Planned, line.Factor!.Value);
            var source = new MovementSource(movementEvent, Deliveries.DocumentType, command.DeliveryId, line.Id);
            foreach (var (lot, quantity) in await Deliveries.FifoAsync(context, line.Source!.Value, line.ItemId, baseQuantity, cancellationToken).ConfigureAwait(false))
            {
                var reservation = await _inventory.ReserveIssueAsync(context, line.Source.Value, line.ItemId, lot, quantity, cancellationToken).ConfigureAwait(false);
                if (atGate)
                {
                    var entry = context.Ids.NewId();
                    await _inventory.WriteIssueAsync(context, reservation, entry, source, dates, cancellationToken).ConfigureAwait(false);
                    posting.Cost(line.ItemId, reservation.Value, entry, fromTransit: false);
                }
                else
                {
                    var (outEntry, inEntry) = (context.Ids.NewId(), context.Ids.NewId());
                    await _inventory.WriteTransferAsync(context, reservation, transit!.Value, outEntry, inEntry, source, dates, cancellationToken).ConfigureAwait(false);
                    posting.Transfer(line.ItemId, reservation.Value, outEntry, inEntry, toTransit: true);
                }

                await Sql.ExecuteAsync(
                    context.Connection,
                    context.Transaction,
                    "INSERT INTO log.delivery_line_lot (delivery_line_id, company_id, lot_id, source_location_id, base_quantity) VALUES (@l, @c, @lot, @loc, @q)",
                    cancellationToken,
                    ("l", line.Id),
                    ("c", context.CompanyId),
                    ("lot", lot),
                    ("loc", line.Source.Value),
                    ("q", quantity)).ConfigureAwait(false);
            }

            if (atGate)
            {
                var revenue = decimal.Round(line.Planned * line.UnitPrice, 2, MidpointRounding.AwayFromZero);
                posting.Revenue_(line.Id, line.ItemId, revenue);
                delivered[line.OrderLineId] = line.Planned;
                reached.Add(new DeliveredLine(line.Id, line.ItemId, line.Uom, line.Planned, line.UnitPrice, revenue));
            }

            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE log.delivery_line SET qty_issued = qty_planned, qty_delivered = @d WHERE delivery_line_id = @id",
                cancellationToken,
                ("d", atGate ? line.Planned : 0m),
                ("id", line.Id)).ConfigureAwait(false);
        }

        await Deliveries.InsertAssessmentAsync(
            context, command.DeliveryId, row, "GATE_OUT", issued, posting.Revenue?.PolicyVersionId,
            new { term = row.Term, controlTransfersAt = row.Control, presentation = posting.Revenue?.Text(Deliveries.Presentation) }, cancellationToken).ConfigureAwait(false);
        var to = atGate ? "DELIVERED" : "IN_TRANSIT";
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            UPDATE log.delivery SET gross_kg = @g, tare_kg = @t, weigh_ticket_ref = @ref, weigh_ticket_sha256 = @hash, gate_out_at = @at, status = @s, version = @v
            WHERE delivery_id = @d
            """,
            cancellationToken,
            ("g", command.GrossKg),
            ("t", command.TareKg),
            ("ref", ticket),
            ("hash", ticketHash),
            ("at", occurredAt),
            ("s", to),
            ("v", version),
            ("d", command.DeliveryId)).ConfigureAwait(false);
        await context.AppendStateAsync(Deliveries.Aggregate, command.DeliveryId, "DOCUMENT", "LOADED", to, CommandType, issued, cancellationToken).ConfigureAwait(false);
        if (atGate)
        {
            await Orders.Orders.AddDeliveredAsync(context, order, row.SalesOrderId, delivered, CommandType, cancellationToken).ConfigureAwait(false);
        }

        await posting.WriteAsync(_engine, context, "P-15", issued, businessDate, occurredAt, postingDate, cancellationToken).ConfigureAwait(false);
        if (control is { } c)
        {
            await posting.WriteAsync(_engine, context, "P-16", c, businessDate, occurredAt, postingDate, cancellationToken).ConfigureAwait(false);
        }

        // E-FIS1b-1: a pickup of an order whose exemption is in process is delivered here, and so is its proforma.
        var proforma = atGate
            ? await Proformas.Proformas.IssueOnDeliveryAsync(context, CommandType, row.SalesOrderId, command.DeliveryId, row.DeliveryNo, party.Value, businessDate, reached, cancellationToken).ConfigureAwait(false)
            : null;
        return JsonSerializer.Serialize(new { deliveryId = command.DeliveryId, status = to, controlTransferred = atGate, version, proformaId = proforma?.ProformaId, proformaNo = proforma?.ProformaNo });
    }
}

internal static class TransitMoves
{
    /// <summary>The lots of each line at TRANSITO, in FIFO order (base units still in transit).</summary>
    public static async Task<List<Deliveries.Portion>> LotsAsync(CommandContext context, Guid deliveryLineId, CancellationToken cancellationToken)
        => await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT dl.delivery_line_id, dl.lot_id, dl.source_location_id, dl.base_quantity
            FROM log.delivery_line_lot dl JOIN inv.lot l ON l.lot_id = dl.lot_id
            WHERE dl.delivery_line_id = @l ORDER BY l.lot_code, l.lot_id
            """,
            r => new Deliveries.Portion(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetDecimal(3)),
            cancellationToken,
            ("l", deliveryLineId)).ConfigureAwait(false);

    /// <summary>Returns portions from TRANSITO to the locations they left (P-15R).</summary>
    public static async Task ReturnAsync(
        InventoryLedger inventory, CommandContext context, DeliveryPosting posting, Guid transit, Deliveries.Line line, IEnumerable<Deliveries.Portion> portions, MovementSource source, MovementDates dates,
        CancellationToken cancellationToken)
    {
        foreach (var p in portions)
        {
            var reservation = await inventory.ReserveIssueAsync(context, transit, line.ItemId, p.LotId, p.BaseQuantity, cancellationToken).ConfigureAwait(false);
            var (outEntry, inEntry) = (context.Ids.NewId(), context.Ids.NewId());
            await inventory.WriteTransferAsync(context, reservation, p.FromLocation, outEntry, inEntry, source, dates, cancellationToken).ConfigureAwait(false);
            posting.Transfer(line.ItemId, reservation.Value, outEntry, inEntry, toTransit: false);
        }
    }
}

[RequiresPermission("delivery:manage")]
public sealed class RecordPodHandler : ICommandHandler<RecordPod>
{
    private readonly PostingEngine _engine = new();
    private readonly InventoryLedger _inventory = new();

    public string CommandType => "Sales.RecordPod";

    public async Task<string> HandleAsync(RecordPod command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var receivedBy = SalesSql.Optional(command.ReceivedByName, 200, "The receiver's name")
            ?? throw new DomainException(DeliveryErrors.EvidenceInvalid, "The POD names who received.");
        var evidenceRef = SalesSql.Optional(command.EvidenceRef, 200, "The evidence reference")
            ?? throw new DomainException(DeliveryErrors.EvidenceInvalid, "The POD needs its evidence reference.");
        var evidenceHash = Deliveries.Sha256(command.EvidenceSha256);
        if (command.ReceivedAt > context.Clock.UtcNow)
        {
            throw new DomainException(DeliveryErrors.EvidenceInvalid, "The reception cannot be in the future.");
        }

        var (order, row) = await Deliveries.LockWithOrderAsync(context, command.DeliveryId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "IN_TRANSIT")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The delivery is {row.Status}; a POD is recorded for a delivery in transit.");
        }

        var lines = await Deliveries.LinesAsync(context, command.DeliveryId, cancellationToken).ConfigureAwait(false);
        var input = command.Lines ?? [];
        if (input.Count != lines.Count || lines.Any(l => input.Count(i => i.DeliveryLineId == l.Id) != 1)
            || input.Any(i => i.QtyReceived < 0m || i.QtyReturned < 0m || decimal.Round(i.QtyReceived, 6) != i.QtyReceived || decimal.Round(i.QtyReturned, 6) != i.QtyReturned)
            || lines.Any(l => { var i = input.Single(x => x.DeliveryLineId == l.Id); return i.QtyReceived + i.QtyReturned > l.Issued; })
            || input.Sum(i => i.QtyReceived) <= 0m)
        {
            throw new DomainException(DeliveryErrors.PodQuantitiesInvalid, "Every line once; received + returned ≤ issued; something received (a total rejection is a return trip).");
        }

        var exceptions = lines.Any(l => { var i = input.Single(x => x.DeliveryLineId == l.Id); return i.QtyReceived < l.Issued; });
        var reason = SalesSql.Optional(command.ExceptionReason, 500, "The exception reason");
        if (exceptions && reason is null)
        {
            throw new DomainException(DeliveryErrors.ExceptionReasonRequired, "A POD with returns or shortages needs the exception reason (E-VS3-04-8).");
        }

        var party = await SalesSql.ScalarAsync<Guid?>(context, "SELECT party_id FROM sal.sales_order WHERE sales_order_id = @o", cancellationToken, ("o", row.SalesOrderId)).ConfigureAwait(false);
        var occurredAt = context.Clock.UtcNow;
        var businessDate = SalesSql.Today(context);
        var posting = new DeliveryPosting(row.PlantId, party!.Value, row.DeliveryNo, order.OrderNo)
        {
            Revenue = await PolicyResolver.ResolveAsync(context, Deliveries.RevenuePolicy, businessDate, cancellationToken).ConfigureAwait(false),
        };
        var postingDate = await _engine.PostingDateAsync(context, "P-16", businessDate, cancellationToken).ConfigureAwait(false);
        var transit = await Deliveries.TransitLocationAsync(context, row.PlantId, cancellationToken).ConfigureAwait(false);
        var version = row.Version + 1;
        var pod = await Deliveries.AppendAsync(
            context, command.DeliveryId, version, "PodRecorded",
            new { deliveryId = command.DeliveryId, deliveryNo = row.DeliveryNo, receivedBy, receivedAt = command.ReceivedAt, evidenceRef, exceptionReason = reason, lines = input.Select(i => new { deliveryLineId = i.DeliveryLineId, received = Deliveries.M(i.QtyReceived), returned = Deliveries.M(i.QtyReturned) }) },
            occurredAt, businessDate, cancellationToken).ConfigureAwait(false);
        var control = await Deliveries.AppendAsync(context, command.DeliveryId, version, "ControlTransferred", new { deliveryId = command.DeliveryId, deliveryNo = row.DeliveryNo, trigger = "POD" }, occurredAt, businessDate, cancellationToken).ConfigureAwait(false);
        var returnedEvent = input.Any(i => i.QtyReturned > 0m)
            ? await Deliveries.AppendAsync(context, command.DeliveryId, version, "GoodsReturnedFromTransit", new { deliveryId = command.DeliveryId, deliveryNo = row.DeliveryNo }, occurredAt, businessDate, cancellationToken).ConfigureAwait(false)
            : (Guid?)null;
        var lossEvent = lines.Any(l => { var i = input.Single(x => x.DeliveryLineId == l.Id); return i.QtyReceived + i.QtyReturned < l.Issued; })
            ? await Deliveries.AppendAsync(context, command.DeliveryId, version, "TransitLossRecognized", new { deliveryId = command.DeliveryId, deliveryNo = row.DeliveryNo, reason }, occurredAt, businessDate, cancellationToken).ConfigureAwait(false)
            : (Guid?)null;
        var dates = new MovementDates(occurredAt, businessDate, postingDate);
        var delivered = new Dictionary<Guid, decimal>();
        var reached = new List<DeliveredLine>();
        foreach (var line in lines)
        {
            var i = input.Single(x => x.DeliveryLineId == line.Id);
            var lots = await TransitMoves.LotsAsync(context, line.Id, cancellationToken).ConfigureAwait(false);
            var issuedBase = lots.Sum(l => l.BaseQuantity);
            var receivedBase = i.QtyReceived == line.Issued ? issuedBase : Deliveries.Base(i.QtyReceived, line.Factor!.Value);
            var returnedBase = Math.Min(Deliveries.Base(i.QtyReturned, line.Factor!.Value), issuedBase - receivedBase);
            var lostBase = issuedBase - receivedBase - returnedBase;
            foreach (var p in Deliveries.Take(lots, receivedBase))
            {
                var reservation = await _inventory.ReserveIssueAsync(context, transit, line.ItemId, p.LotId, p.BaseQuantity, cancellationToken).ConfigureAwait(false);
                var entry = context.Ids.NewId();
                await _inventory.WriteIssueAsync(context, reservation, entry, new MovementSource(control, Deliveries.DocumentType, command.DeliveryId, line.Id), dates, cancellationToken).ConfigureAwait(false);
                posting.Cost(line.ItemId, reservation.Value, entry, fromTransit: true);
            }

            if (i.QtyReceived > 0m)
            {
                var revenue = decimal.Round(i.QtyReceived * line.UnitPrice, 2, MidpointRounding.AwayFromZero);
                posting.Revenue_(line.Id, line.ItemId, revenue);
                delivered[line.OrderLineId] = i.QtyReceived;
                reached.Add(new DeliveredLine(line.Id, line.ItemId, line.Uom, i.QtyReceived, line.UnitPrice, revenue));
            }

            await TransitMoves.ReturnAsync(
                _inventory, context, posting, transit, line, Deliveries.Take(lots, returnedBase), new MovementSource(returnedEvent ?? pod, Deliveries.DocumentType, command.DeliveryId, line.Id), dates,
                cancellationToken).ConfigureAwait(false);
            foreach (var p in Deliveries.Take(lots, lostBase))
            {
                var reservation = await _inventory.ReserveIssueAsync(context, transit, line.ItemId, p.LotId, p.BaseQuantity, cancellationToken).ConfigureAwait(false);
                var entry = context.Ids.NewId();
                await _inventory.WriteIssueAsync(context, reservation, entry, new MovementSource(lossEvent!.Value, Deliveries.DocumentType, command.DeliveryId, line.Id), dates, cancellationToken).ConfigureAwait(false);
                posting.Loss(line.ItemId, reservation.Value, entry);
            }

            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE log.delivery_line SET qty_delivered = @d, qty_returned = @r, qty_lost = @l WHERE delivery_line_id = @id",
                cancellationToken,
                ("d", i.QtyReceived),
                ("r", i.QtyReturned),
                ("l", line.Issued - i.QtyReceived - i.QtyReturned),
                ("id", line.Id)).ConfigureAwait(false);
        }

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO log.pod (pod_id, company_id, delivery_id, received_by_name, received_at, evidence_ref, evidence_sha256, recorded_event_id)
            VALUES (@id, @c, @d, @by, @at, @ref, @hash, @e)
            """,
            cancellationToken,
            ("id", context.Ids.NewId()),
            ("c", context.CompanyId),
            ("d", command.DeliveryId),
            ("by", receivedBy),
            ("at", command.ReceivedAt),
            ("ref", evidenceRef),
            ("hash", evidenceHash),
            ("e", pod)).ConfigureAwait(false);
        await Deliveries.InsertAssessmentAsync(
            context, command.DeliveryId, row, "POD", pod, posting.Revenue.PolicyVersionId,
            new { term = row.Term, controlTransfersAt = row.Control, presentation = posting.Revenue.Text(Deliveries.Presentation), receivedBy }, cancellationToken).ConfigureAwait(false);
        var to = exceptions ? "DELIVERED_WITH_EXCEPTIONS" : "DELIVERED";
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE log.delivery SET status = @s, exception_reason = @r, version = @v WHERE delivery_id = @d", cancellationToken,
            ("s", to), ("r", exceptions ? reason : null), ("v", version), ("d", command.DeliveryId)).ConfigureAwait(false);
        await context.AppendStateAsync(Deliveries.Aggregate, command.DeliveryId, "DOCUMENT", "IN_TRANSIT", to, CommandType, pod, cancellationToken, exceptions ? reason : null).ConfigureAwait(false);
        await Orders.Orders.AddDeliveredAsync(context, order, row.SalesOrderId, delivered, CommandType, cancellationToken).ConfigureAwait(false);
        await posting.WriteAsync(_engine, context, "P-16", control, businessDate, occurredAt, postingDate, cancellationToken).ConfigureAwait(false);
        if (returnedEvent is { } re)
        {
            await posting.WriteAsync(_engine, context, "P-15R", re, businessDate, occurredAt, postingDate, cancellationToken).ConfigureAwait(false);
        }

        if (lossEvent is { } le)
        {
            await posting.WriteAsync(_engine, context, "P-30", le, businessDate, occurredAt, postingDate, cancellationToken).ConfigureAwait(false);
        }

        // E-FIS1b-01-2: the proforma of a site delivery carries the day the customer received it.
        var proforma = await Proformas.Proformas.IssueOnDeliveryAsync(
            context, CommandType, row.SalesOrderId, command.DeliveryId, row.DeliveryNo, party.Value, BusinessCalendar.DefaultBusinessDate(command.ReceivedAt), reached, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { deliveryId = command.DeliveryId, status = to, version, proformaId = proforma?.ProformaId, proformaNo = proforma?.ProformaNo });
    }
}

[RequiresPermission("delivery:manage")]
public sealed class RecordReturnTripHandler : ICommandHandler<RecordReturnTrip>
{
    private readonly PostingEngine _engine = new();
    private readonly InventoryLedger _inventory = new();

    public string CommandType => "Sales.RecordReturnTrip";

    public async Task<string> HandleAsync(RecordReturnTrip command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = SalesSql.Optional(command.Reason, 500, "The reason") ?? throw new DomainException(OrderErrors.ReasonRequired, "A return trip needs its reason.");
        var (order, row) = await Deliveries.LockWithOrderAsync(context, command.DeliveryId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "IN_TRANSIT")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The delivery is {row.Status}.");
        }

        var lines = await Deliveries.LinesAsync(context, command.DeliveryId, cancellationToken).ConfigureAwait(false);
        var party = await SalesSql.ScalarAsync<Guid?>(context, "SELECT party_id FROM sal.sales_order WHERE sales_order_id = @o", cancellationToken, ("o", row.SalesOrderId)).ConfigureAwait(false);
        var occurredAt = context.Clock.UtcNow;
        var businessDate = SalesSql.Today(context);
        var posting = new DeliveryPosting(row.PlantId, party!.Value, row.DeliveryNo, order.OrderNo);
        var postingDate = await _engine.PostingDateAsync(context, "P-15R", businessDate, cancellationToken).ConfigureAwait(false);
        var transit = await Deliveries.TransitLocationAsync(context, row.PlantId, cancellationToken).ConfigureAwait(false);
        var version = row.Version + 1;
        var returned = await Deliveries.AppendAsync(
            context, command.DeliveryId, version, "GoodsReturnedFromTransit", new { deliveryId = command.DeliveryId, deliveryNo = row.DeliveryNo, reason, totalRejection = true },
            occurredAt, businessDate, cancellationToken).ConfigureAwait(false);
        var dates = new MovementDates(occurredAt, businessDate, postingDate);
        foreach (var line in lines)
        {
            var lots = await TransitMoves.LotsAsync(context, line.Id, cancellationToken).ConfigureAwait(false);
            await TransitMoves.ReturnAsync(_inventory, context, posting, transit, line, lots, new MovementSource(returned, Deliveries.DocumentType, command.DeliveryId, line.Id), dates, cancellationToken).ConfigureAwait(false);
            await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE log.delivery_line SET qty_returned = qty_issued WHERE delivery_line_id = @id", cancellationToken, ("id", line.Id)).ConfigureAwait(false);
        }

        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE log.delivery SET status = 'RETURNED', exception_reason = @r, version = @v WHERE delivery_id = @d", cancellationToken,
            ("r", reason), ("v", version), ("d", command.DeliveryId)).ConfigureAwait(false);
        await context.AppendStateAsync(Deliveries.Aggregate, command.DeliveryId, "DOCUMENT", "IN_TRANSIT", "RETURNED", CommandType, returned, cancellationToken, reason).ConfigureAwait(false);
        await posting.WriteAsync(_engine, context, "P-15R", returned, businessDate, occurredAt, postingDate, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { deliveryId = command.DeliveryId, status = "RETURNED", version });
    }
}

[RequiresPermission("delivery:manage")]
public sealed class CancelDeliveryHandler : ICommandHandler<CancelDelivery>
{
    public string CommandType => "Sales.CancelDelivery";

    public async Task<string> HandleAsync(CancelDelivery command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = SalesSql.Optional(command.Reason, 500, "The reason") ?? throw new DomainException(OrderErrors.ReasonRequired, "Cancelling a delivery needs a reason.");
        var row = await Deliveries.LockAsync(context, command.DeliveryId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status is not ("PLANNED" or "LOADING" or "LOADED"))
        {
            throw new DomainException(SalesErrors.InvalidState, $"The delivery is {row.Status}; it is cancelled only before the gate.");
        }

        var version = row.Version + 1;
        var eventId = await Deliveries.AppendAsync(context, command.DeliveryId, version, "DeliveryCancelled", new { deliveryId = command.DeliveryId, deliveryNo = row.DeliveryNo, reason }, context.Clock.UtcNow, SalesSql.Today(context), cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE log.delivery SET status = 'CANCELLED', cancel_reason = @r, version = @v WHERE delivery_id = @d", cancellationToken,
            ("r", reason), ("v", version), ("d", command.DeliveryId)).ConfigureAwait(false);
        await context.AppendStateAsync(Deliveries.Aggregate, command.DeliveryId, "DOCUMENT", row.Status, "CANCELLED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { deliveryId = command.DeliveryId, status = "CANCELLED", version });
    }
}

[RequiresPermission("sales_order:close")]
public sealed class CloseShortSalesOrderHandler : ICommandHandler<CloseShortSalesOrder>
{
    public string CommandType => "Sales.CloseShortSalesOrder";

    public async Task<string> HandleAsync(CloseShortSalesOrder command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = SalesSql.Optional(command.Reason, 500, "The reason") ?? throw new DomainException(OrderErrors.ReasonRequired, "Closing an order short needs a reason.");
        var row = await Orders.Orders.LockAsync(context, command.SalesOrderId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "PARTIALLY_DELIVERED")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The order is {row.Status}; only a PARTIALLY_DELIVERED order is closed short.");
        }

        if (await SalesSql.ScalarAsync<bool?>(
                context, "SELECT EXISTS (SELECT 1 FROM log.delivery WHERE sales_order_id = @o AND status IN ('PLANNED', 'LOADING', 'LOADED', 'IN_TRANSIT'))", cancellationToken,
                ("o", command.SalesOrderId)).ConfigureAwait(false) == true)
        {
            throw new DomainException(DeliveryErrors.OpenDeliveries, "The order has open deliveries; finish or cancel them first.");
        }

        var closer = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (closer == row.CreatedBy && !await ControlWaiver.WaivedAsync(context, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(SalesErrors.FourEyes, "An order is closed short by someone other than who created it (E-VS3-04-13).");
        }

        await Orders.Orders.TransitionAsync(
            context, command.SalesOrderId, row, "CLOSED", "SalesOrderClosed", new { salesOrderId = command.SalesOrderId, orderNo = row.OrderNo, reason }, CommandType, cancellationToken, reason, closeReason: reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { salesOrderId = command.SalesOrderId, status = "CLOSED", version = row.Version + 1 });
    }
}
