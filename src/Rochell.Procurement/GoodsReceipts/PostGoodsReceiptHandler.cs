using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.Inventory;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Time;
using Rochell.Procurement.PurchaseOrders;

namespace Rochell.Procurement.GoodsReceipts;

/// <summary>
/// T-02 / C-01. Lock order: PO header (N3) → PO lines by id (N4) → stock (N6) → valuation (N7) → GL balances (N9).
/// Every posting prerequisite is validated before any write (P-1); a gap rolls back the whole command.
/// R-01: Dr RAW_MATERIAL (plant, item, value entry) / Cr GRNI (plant, supplier) for quantity × PO price (E-PR09-1, E-PR09-4).
/// </summary>
[RequiresPermission("goods_receipt:post")]
public sealed class PostGoodsReceiptHandler : ICommandHandler<PostGoodsReceipt>
{
    public const string Aggregate = "GoodsReceipt";
    public const string RuleCode = "R-01";

    private readonly PostingEngine _engine = new();
    private readonly InventoryLedger _inventory = new();

    public string CommandType => "Procurement.PostGoodsReceipt";

    public Task<string> HandleAsync(PostGoodsReceipt command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        return PostAsync(command, context, context.ResultRef, CommandType, null, cancellationToken);
    }

    /// <summary>
    /// Posts the receipt as <paramref name="grId"/>. REC-02: a direct receipt posts through here from its own command or from the
    /// approval of its order (<paramref name="commandType"/> names the command in the history); <paramref name="directReceiptPoId"/> is
    /// that order, whose open direct receipt already holds the supplier's note.
    /// </summary>
    internal async Task<string> PostAsync(PostGoodsReceipt command, CommandContext context, Guid grId, string commandType, Guid? directReceiptPoId, CancellationToken cancellationToken)
    {
        if (command.Lines is null || command.Lines.Count == 0)
        {
            throw new DomainException(ProcurementErrors.LinesRequired, "A goods receipt needs at least one line.");
        }

        if (command.Lines.Select(l => l.PurchaseOrderLineId).Distinct().Count() != command.Lines.Count)
        {
            throw new DomainException(ProcurementErrors.DuplicateLine, "Each purchase order line can appear only once per receipt.");
        }

        var occurredAt = Precision.ToMicroseconds(command.OccurredAt);
        if (occurredAt > context.Clock.UtcNow)
        {
            throw new DomainException(ProcurementErrors.OccurredInFuture, "The receipt time cannot be in the future.");
        }

        var businessDate = BusinessCalendar.DefaultBusinessDate(occurredAt);
        var weighTicket = string.IsNullOrWhiteSpace(command.WeighTicketRef) ? null : command.WeighTicketRef.Trim();
        var deliveryNote = await ValidateTransportAsync(context, command.VehicleId, command.DriverId, command.SupplierDeliveryNote, cancellationToken).ConfigureAwait(false);

        // N3: the order.
        var header = await PurchaseOrderStore.LockAsync(context, command.PurchaseOrderId, command.PlantId, null, cancellationToken).ConfigureAwait(false);
        if (header.Status is not (PurchaseOrderStatus.Approved or PurchaseOrderStatus.PartiallyReceived) || header.DocClass != "INVENTORY") // E-GAS-05-5: an expense order is never received
        {
            throw new DomainException(ProcurementErrors.NotReceivable, $"The purchase order is {header.Status}; only APPROVED or PARTIALLY_RECEIVED orders can be received.");
        }

        await EnsureLocationInPlantAsync(context, command.LocationId, header.PlantId, cancellationToken).ConfigureAwait(false);
        if (weighTicket is not null)
        {
            await EnsureTicketUnusedAsync(context, weighTicket, cancellationToken).ConfigureAwait(false);
        }

        await EnsureDeliveryNoteUnusedAsync(context, header.PartyId, deliveryNote, directReceiptPoId, cancellationToken).ConfigureAwait(false);

        // N4: the order lines, in id order; quantities, tolerance, conversion and value.
        var poLines = await LockLinesAsync(context, header.Id, cancellationToken).ConfigureAwait(false);
        var received = new List<ReceivedLine>();
        foreach (var input in command.Lines.OrderBy(l => l.PurchaseOrderLineId))
        {
            var line = poLines.GetValueOrDefault(input.PurchaseOrderLineId)
                ?? throw new DomainException(ProcurementErrors.LineNotFound, "A receipt line does not belong to this purchase order.");
            if (input.Quantity <= 0 || decimal.Round(input.Quantity, 6) != input.Quantity)
            {
                throw new DomainException(ProcurementErrors.QuantityInvalid, "Received quantities must be positive with at most 6 decimals.");
            }

            var maximum = line.QtyOrdered * (1 + line.ReceiptTolerancePct) + line.QtyOverReceiptApproved;
            if (line.QtyReceived + input.Quantity > maximum)
            {
                throw new DomainException(
                    ProcurementErrors.ReceiptToleranceExceeded,
                    $"Line {line.LineNo}: receiving {input.Quantity} would exceed the allowed {maximum} (ordered × (1 + tolerance) + approved over-receipt); already received {line.QtyReceived}.");
            }

            var factor = line.Uom == line.BaseUom ? 1 : await ConversionFactorAsync(context, line, businessDate, cancellationToken).ConfigureAwait(false);
            var baseQuantity = decimal.Round(input.Quantity * factor, 6, MidpointRounding.AwayFromZero);
            var value = decimal.Round(input.Quantity * line.UnitPrice, 2, MidpointRounding.AwayFromZero);
            if (baseQuantity <= 0 || value <= 0)
            {
                throw new DomainException(ProcurementErrors.ValueTooSmall, $"Line {line.LineNo}: the quantity is too small to be valued.");
            }

            received.Add(new ReceivedLine(input, line, baseQuantity, value, context.Ids.NewId(), context.Ids.NewId()));
        }

        // P-1 step 7: every accounting prerequisite before any write.
        var plan = await _engine.PrepareAsync(
            context,
            new PostingRequest(
                RuleCode,
                businessDate,
                occurredAt,
                received.SelectMany(r => new[]
                {
                    new PostingLineInput("R01-DR-INV", "receipt_value", r.Value, PlantId: header.PlantId, ItemId: r.Line.ItemId, SubledgerRef: r.ValueEntryId, InvValueEntryId: r.ValueEntryId,
                        Inputs: new Dictionary<string, string> { ["gr_line_id"] = r.GrLineId.ToString(), ["po_line_id"] = r.Line.PoLineId.ToString(), ["quantity"] = r.Input.Quantity.ToString(CultureInfo.InvariantCulture), ["unit_price"] = r.Line.UnitPrice.ToString(CultureInfo.InvariantCulture) }),
                    new PostingLineInput("R01-CR-GRNI", "receipt_value", r.Value, PlantId: header.PlantId, PartyId: header.PartyId,
                        Inputs: new Dictionary<string, string> { ["gr_line_id"] = r.GrLineId.ToString(), ["po_line_id"] = r.Line.PoLineId.ToString() }),
                }).ToList()),
            cancellationToken).ConfigureAwait(false);

        var grNo = await DocumentNumbers.NextAsync(context, DocumentNumbers.GoodsReceipt, businessDate.Year, cancellationToken).ConfigureAwait(false);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "GoodsReceiptPosted",
                1,
                Aggregate,
                grId,
                1,
                JsonSerializer.Serialize(new
                {
                    grId,
                    grNo,
                    poId = header.Id,
                    poNo = header.PoNo,
                    locationId = command.LocationId,
                    weighTicketRef = weighTicket,
                    lines = received.Select(r => new
                    {
                        grLineId = r.GrLineId,
                        poLineId = r.Line.PoLineId,
                        itemId = r.Line.ItemId,
                        quantity = r.Input.Quantity.ToString(CultureInfo.InvariantCulture),
                        uom = r.Line.Uom,
                        baseQuantity = r.BaseQuantity.ToString(CultureInfo.InvariantCulture),
                        value = r.Value.ToString(CultureInfo.InvariantCulture),
                    }),
                }),
                Publish: true,
                OccurredAt: occurredAt,
                BusinessDate: businessDate),
            cancellationToken).ConfigureAwait(false);

        await context.AppendStateAsync(Aggregate, grId, "DOCUMENT", null, "POSTED", commandType, eventId, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO pur.goods_receipt (gr_id, company_id, gr_no, po_id, location_id, weigh_ticket_ref, document_status, accounting_status, posting_event_id, occurred_at, version,
                                           vehicle_id, driver_id, supplier_party_id, supplier_delivery_note)
            VALUES (@id, @c, @no, @po, @location, @ticket, 'POSTED', 'POSTED', @event, @occurred, 1, @vehicle, @driver, @supplier, @note)
            """,
            cancellationToken,
            ("id", grId),
            ("c", context.CompanyId),
            ("no", grNo),
            ("po", header.Id),
            ("location", command.LocationId),
            ("ticket", weighTicket),
            ("event", eventId),
            ("occurred", occurredAt),
            ("vehicle", command.VehicleId),
            ("driver", command.DriverId),
            ("supplier", header.PartyId),
            ("note", deliveryNote)).ConfigureAwait(false);

        var resultLines = new List<object>();
        var settlements = new List<ShortfallSettlement>();
        var settle = await MaterialShortfalls.RuleActiveAsync(context, MaterialShortfalls.SettleRule, plan.PostingDate, cancellationToken).ConfigureAwait(false);
        foreach (var r in received)
        {
            await MaterialShortfalls.LockAsync(context, command.LocationId, r.Line.ItemId, cancellationToken).ConfigureAwait(false);
            var lotId = await _inventory.CreateLotAsync(context, r.Line.ItemId, header.PartyId, r.Input.SupplierLotNumber, eventId, businessDate, cancellationToken).ConfigureAwait(false);
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "INSERT INTO pur.goods_receipt_line (gr_line_id, company_id, gr_id, po_line_id, lot_id, qty, unit_price) VALUES (@id, @c, @gr, @pol, @lot, @qty, @price)",
                cancellationToken,
                ("id", r.GrLineId),
                ("c", context.CompanyId),
                ("gr", grId),
                ("pol", r.Line.PoLineId),
                ("lot", lotId),
                ("qty", r.Input.Quantity),
                ("price", r.Line.UnitPrice)).ConfigureAwait(false);

            await _inventory.ReceiveAsync(
                context,
                new ReceiptMovement(command.LocationId, r.Line.ItemId, lotId, r.BaseQuantity, r.Value, r.ValueEntryId),
                new MovementSource(eventId, "GOODS_RECEIPT", grId, r.GrLineId),
                new MovementDates(occurredAt, businessDate, plan.PostingDate),
                cancellationToken).ConfigureAwait(false);

            // E-VS3-02c-2: what entered covers first what production consumed without stock in this location (P-48 below).
            if (settle && await MaterialShortfalls.ReserveAsync(context, _inventory, command.LocationId, r.Line.ItemId, lotId, r.BaseQuantity, cancellationToken).ConfigureAwait(false) is { } settlement)
            {
                settlements.Add(settlement);
            }

            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE pur.purchase_order_line SET qty_received = qty_received + @q, version = version + 1 WHERE po_line_id = @l",
                cancellationToken,
                ("q", r.Input.Quantity),
                ("l", r.Line.PoLineId)).ConfigureAwait(false);

            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO core.document_link (link_id, company_id, from_type, from_id, from_line_id, to_type, to_id, to_line_id, link_type, qty, amount, event_id)
                VALUES (@id, @c, 'GoodsReceipt', @gr, @grl, 'PurchaseOrder', @po, @pol, 'RECEIVES', @qty, @amount, @event)
                """,
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("gr", grId),
                ("grl", r.GrLineId),
                ("po", header.Id),
                ("pol", r.Line.PoLineId),
                ("qty", r.Input.Quantity),
                ("amount", r.Value),
                ("event", eventId)).ConfigureAwait(false);

            resultLines.Add(new { grLineId = r.GrLineId, poLineId = r.Line.PoLineId, lotId, baseQuantity = r.BaseQuantity.ToString(CultureInfo.InvariantCulture), value = r.Value.ToString(CultureInfo.InvariantCulture) });
        }

        // §11.1: the order becomes PARTIALLY_RECEIVED or RECEIVED (every line received ≥ ordered).
        var fullyReceived = await FullyReceivedAsync(context, header.Id, cancellationToken).ConfigureAwait(false);
        var newStatus = fullyReceived ? PurchaseOrderStatus.Received : PurchaseOrderStatus.PartiallyReceived;
        if (newStatus != header.Status)
        {
            await PurchaseOrderStore.TransitionAsync(
                context,
                header,
                newStatus,
                commandType,
                "PurchaseOrderReceiptStatusChanged",
                new { poId = header.Id, status = newStatus, grId },
                publish: true,
                cancellationToken).ConfigureAwait(false);
        }

        var journal = await _engine.WriteAsync(context, plan, eventId, cancellationToken).ConfigureAwait(false);
        Guid? settlementJournalId = null;
        if (settlements.Count > 0)
        {
            settlementJournalId = await ShortfallSettlementPosting.PostAsync(
                context, _engine, _inventory, settlements, header.PlantId, Aggregate, grId, 1, "GOODS_RECEIPT", grId, occurredAt, businessDate, cancellationToken).ConfigureAwait(false);
        }

        return JsonSerializer.Serialize(new
        {
            goodsReceiptId = grId,
            settlementJournalId,
            shortfallsCovered = settlements.Sum(x => x.Shares.Count),
            grNo,
            purchaseOrderStatus = newStatus,
            journalId = journal.JournalId,
            postingDate = journal.PostingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            lateEntry = journal.LateEntry,
            lines = resultLines,
        });
    }

    /// <summary>E-REC-1/2: our ACTIVE truck and driver and the supplier's delivery note (1–60 characters); returns the note trimmed.</summary>
    internal static async Task<string> ValidateTransportAsync(CommandContext context, Guid? vehicleId, Guid? driverId, string? supplierDeliveryNote, CancellationToken cancellationToken)
    {
        var deliveryNote = (supplierDeliveryNote ?? string.Empty).Trim();
        if (deliveryNote.Length is 0 or > 60) // type-limit: pur.goods_receipt supplier_delivery_note
        {
            throw new DomainException(ProcurementErrors.SupplierDeliveryNoteRequired, "The supplier's delivery note number is required (1 to 60 characters, E-REC-2).");
        }

        // E-REC-1: our truck and our driver, both ACTIVE (the fleet's tables are read through SQL: no module reference).
        await using (var fleet = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT (SELECT count(*) FROM log.vehicle WHERE company_id = @c AND vehicle_id = @v AND status = 'ACTIVE') + (SELECT count(*) FROM log.driver WHERE company_id = @c AND driver_id = @d AND status = 'ACTIVE')",
            ("c", context.CompanyId),
            ("v", (object?)vehicleId ?? DBNull.Value),
            ("d", (object?)driverId ?? DBNull.Value)))
        {
            if (vehicleId is null || driverId is null || await fleet.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not 2L)
            {
                throw new DomainException(ProcurementErrors.ReceiptTransportRequired, "The receipt names one of our ACTIVE trucks and an ACTIVE driver (E-REC-1).");
            }
        }

        return deliveryNote;
    }

    /// <summary>The location is in the order's plant; E-UX4-8: raw material is never received into CURADO nor TRANSITO.</summary>
    internal static async Task EnsureLocationInPlantAsync(CommandContext context, Guid locationId, Guid plantId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection, context.Transaction, "SELECT is_curing OR is_transit FROM md.location WHERE location_id = @l AND plant_id = @p", ("l", locationId), ("p", plantId));
        var special = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (special is not bool excluded)
        {
            throw new DomainException(ProcurementErrors.LocationNotInPlant, "The location is not in the plant of the purchase order.");
        }

        if (excluded)
        {
            throw new DomainException(ProcurementErrors.LocationNotReceivable, "Raw material is not received into the curing (CURADO) or in-transit (TRANSITO) location.");
        }
    }

    /// <summary>
    /// E-REC-2: a supplier's delivery note is received once while the receipt stands (the unique index backs it under concurrency).
    /// E-REC-02-4: an open direct receipt (waiting for approval) holds its note too, except for <paramref name="directReceiptPoId"/>'s own.
    /// </summary>
    internal static async Task EnsureDeliveryNoteUnusedAsync(CommandContext context, Guid supplier, string note, Guid? directReceiptPoId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT EXISTS (SELECT 1 FROM pur.goods_receipt WHERE company_id = @c AND supplier_party_id = @s AND upper(supplier_delivery_note) = upper(@n) AND document_status <> 'REVERSED')
                OR EXISTS (SELECT 1 FROM pur.direct_receipt WHERE company_id = @c AND supplier_party_id = @s AND upper(supplier_delivery_note) = upper(@n) AND closed_at IS NULL
                             AND po_id IS DISTINCT FROM CAST(@own AS uuid))
            """,
            ("c", context.CompanyId),
            ("s", supplier),
            ("n", note),
            ("own", (object?)directReceiptPoId ?? DBNull.Value));
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
        {
            throw new DomainException(ProcurementErrors.SupplierDeliveryNoteUsed, $"The supplier's delivery note {note} is already received (E-REC-2).");
        }
    }

    private static async Task EnsureTicketUnusedAsync(CommandContext context, string ticket, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT EXISTS (SELECT 1 FROM pur.goods_receipt WHERE company_id = @c AND weigh_ticket_ref = @t AND document_status <> 'REVERSED')",
            ("c", context.CompanyId),
            ("t", ticket));
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
        {
            throw new DomainException(ProcurementErrors.WeighTicketUsed, $"Weigh ticket {ticket} is already used by another receipt.");
        }
    }

    private static async Task<Dictionary<Guid, PoLine>> LockLinesAsync(CommandContext context, Guid poId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT l.po_line_id, l.line_no, l.item_id, l.uom, i.base_uom, l.qty_ordered, l.unit_price, l.receipt_tolerance_pct, l.qty_over_receipt_approved, l.qty_received
            FROM pur.purchase_order_line l JOIN md.item i ON i.item_id = l.item_id
            WHERE l.po_id = @p
            ORDER BY l.po_line_id
            FOR UPDATE OF l
            """,
            ("p", poId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var lines = new Dictionary<Guid, PoLine>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var line = new PoLine(reader.GetGuid(0), reader.GetInt32(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4), reader.GetDecimal(5), reader.GetDecimal(6), reader.GetDecimal(7), reader.GetDecimal(8), reader.GetDecimal(9));
            lines[line.PoLineId] = line;
        }

        return lines;
    }

    /// <summary>E-PR09-4: conversion effective on the receipt date; a missing conversion is a missing prerequisite.</summary>
    private static async Task<decimal> ConversionFactorAsync(CommandContext context, PoLine line, DateOnly date, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT factor FROM md.uom_conversion
            WHERE company_id = @c AND item_id = @i AND from_uom = @from AND to_uom = @to
              AND effective_from <= @d AND (effective_to IS NULL OR effective_to > @d)
            """,
            ("c", context.CompanyId),
            ("i", line.ItemId),
            ("from", line.Uom),
            ("to", line.BaseUom),
            ("d", date));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is decimal factor
            ? factor
            : throw new DomainException(ProcurementErrors.UomNotConvertible, $"Line {line.LineNo}: no conversion from {line.Uom} to {line.BaseUom} effective on {date:yyyy-MM-dd}.");
    }

    private static async Task<bool> FullyReceivedAsync(CommandContext context, Guid poId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(context.Connection, context.Transaction, "SELECT bool_and(qty_received >= qty_ordered) FROM pur.purchase_order_line WHERE po_id = @p", ("p", poId));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private sealed record PoLine(Guid PoLineId, int LineNo, Guid ItemId, string Uom, string BaseUom, decimal QtyOrdered, decimal UnitPrice, decimal ReceiptTolerancePct, decimal QtyOverReceiptApproved, decimal QtyReceived);

    private sealed record ReceivedLine(GoodsReceiptLineInput Input, PoLine Line, decimal BaseQuantity, decimal Value, Guid GrLineId, Guid ValueEntryId);
}
