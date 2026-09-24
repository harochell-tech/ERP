using System.Data.Common;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Time;

namespace Rochell.Inventory;

/// <summary>Source document of a movement (document type, id and optional line).</summary>
public sealed record MovementSource(Guid EventId, string DocumentType, Guid DocumentId, Guid? LineId = null);

/// <summary>Dates of a movement (ADR-023). PostingDate must be the posting date of the GL journal (checked at COMMIT, P-1).</summary>
public sealed record MovementDates(DateTime OccurredAt, DateOnly BusinessDate, DateOnly PostingDate);

/// <summary>A receipt into a location and lot. <paramref name="ValueEntryId"/> is pre-assigned so the GL line can reference it.</summary>
public sealed record ReceiptMovement(Guid LocationId, Guid ItemId, Guid LotId, decimal Quantity, decimal Value, Guid ValueEntryId);

/// <summary>A stock balance row (location, lot, quantity).</summary>
public sealed record StockRow(Guid LocationId, Guid LotId, decimal Quantity);

/// <summary>The receipt movements of one source line.</summary>
public sealed record ReceiptEntries(Guid QuantityEntryId, Guid PlantId, Guid LocationId, Guid ItemId, Guid LotId, decimal Quantity, Guid ValueEntryId, Guid ValuationAreaId, decimal Value);

/// <summary>Stock already taken from the balance (conditional UPDATE) and its moving-average value.</summary>
public sealed record IssueReservation(Guid PlantId, Guid ValuationAreaId, Guid LocationId, Guid ItemId, Guid LotId, decimal Quantity, decimal Value);

/// <summary>
/// Inventory ledger (ADR-016/017, E-PR07-1…8). Runs inside the command transaction; every value entry must be paired with
/// exactly one GL line by the caller (Posting Engine) — the database checks it at COMMIT (P-1) together with P-3.
/// Lock order: stock balance → valuation balance → GL period balance.
/// </summary>
public sealed class InventoryLedger
{
    /// <summary>Creates the lot of a receipt line (E-PR07-3).</summary>
    public async Task<Guid> CreateLotAsync(CommandContext context, Guid itemId, Guid? supplierPartyId, string? supplierLotNumber, Guid sourceEventId, DateOnly businessDate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lotId = context.Ids.NewId();
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO inv.lot (lot_id, company_id, item_id, lot_code, supplier_party_id, supplier_lot_number, created_event_id)
            VALUES (@id, @company, @item, @code, @supplier, @supplier_lot, @event)
            """,
            cancellationToken,
            ("id", lotId),
            ("company", context.CompanyId),
            ("item", itemId),
            ("code", $"L{businessDate:yyyyMMdd}-{lotId.ToString("N")[^8..].ToUpperInvariant()}"),
            ("supplier", supplierPartyId),
            ("supplier_lot", string.IsNullOrWhiteSpace(supplierLotNumber) ? null : supplierLotNumber.Trim()),
            ("event", sourceEventId)).ConfigureAwait(false);
        return lotId;
    }

    /// <summary>Writes a receipt: quantity entry, value entry and both balances. Returns the quantity entry id.</summary>
    public async Task<Guid> ReceiveAsync(CommandContext context, ReceiptMovement movement, MovementSource source, MovementDates dates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(movement);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dates);
        if (movement.Quantity <= 0 || decimal.Round(movement.Quantity, 6) != movement.Quantity)
        {
            throw new InvalidOperationException("Receipt quantity must be positive with at most 6 decimals (base UOM).");
        }

        if (movement.Value <= 0 || decimal.Round(movement.Value, 2) != movement.Value)
        {
            throw new InvalidOperationException("Receipt value must be positive with 2 decimals (E-PR07-1).");
        }

        var (plantId, areaId) = await PlantAndAreaAsync(context, movement.LocationId, cancellationToken).ConfigureAwait(false);
        var recordedAt = context.Clock.UtcNow;
        var quantityEntry = new QuantityEntryRow(
            context.Ids.NewId(), context.CompanyId, MovementTypes.Receipt, plantId, movement.LocationId, movement.ItemId, movement.LotId, movement.Quantity,
            source.EventId, source.DocumentType, source.DocumentId, source.LineId, null, Precision.ToMicroseconds(dates.OccurredAt), recordedAt, dates.BusinessDate, dates.PostingDate);
        await InsertQuantityAsync(context, quantityEntry, cancellationToken).ConfigureAwait(false);
        await InsertValueAsync(
            context,
            new ValueEntryRow(
                movement.ValueEntryId, context.CompanyId, MovementTypes.Receipt, areaId, plantId, movement.ItemId, quantityEntry.QuantityEntryId, movement.Value,
                source.EventId, null, quantityEntry.OccurredAt, recordedAt, dates.BusinessDate, dates.PostingDate),
            cancellationToken).ConfigureAwait(false);

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO inv.inv_stock_balance (company_id, plant_id, location_id, item_id, lot_id, quantity)
            VALUES (@company, @plant, @location, @item, @lot, @qty)
            ON CONFLICT (location_id, item_id, lot_id) DO UPDATE SET quantity = inv.inv_stock_balance.quantity + EXCLUDED.quantity
            """,
            cancellationToken,
            ("company", context.CompanyId),
            ("plant", plantId),
            ("location", movement.LocationId),
            ("item", movement.ItemId),
            ("lot", movement.LotId),
            ("qty", movement.Quantity)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO inv.inv_valuation_balance (company_id, valuation_area_id, item_id, quantity, value)
            VALUES (@company, @area, @item, @qty, @value)
            ON CONFLICT (valuation_area_id, item_id) DO UPDATE
              SET quantity = inv.inv_valuation_balance.quantity + EXCLUDED.quantity, value = inv.inv_valuation_balance.value + EXCLUDED.value
            """,
            cancellationToken,
            ("company", context.CompanyId),
            ("area", areaId),
            ("item", movement.ItemId),
            ("qty", movement.Quantity),
            ("value", movement.Value)).ConfigureAwait(false);
        return quantityEntry.QuantityEntryId;
    }

    /// <summary>
    /// CON-03: takes <paramref name="quantity"/> from the stock balance with a conditional UPDATE (never negative) and
    /// computes its moving-average value under the valuation lock (E-PR07-1: 2 decimals half-up; the last unit takes the
    /// remaining value). Nothing is written to the ledgers yet; the whole transaction rolls back on any later failure.
    /// </summary>
    public async Task<IssueReservation> ReserveIssueAsync(CommandContext context, Guid locationId, Guid itemId, Guid lotId, decimal quantity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (quantity <= 0 || decimal.Round(quantity, 6) != quantity)
        {
            throw new InvalidOperationException("Issue quantity must be positive with at most 6 decimals (base UOM).");
        }

        var (plantId, areaId) = await PlantAndAreaAsync(context, locationId, cancellationToken).ConfigureAwait(false);
        var taken = await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE inv.inv_stock_balance SET quantity = quantity - @qty WHERE location_id = @location AND item_id = @item AND lot_id = @lot AND quantity >= @qty",
            cancellationToken,
            ("qty", quantity),
            ("location", locationId),
            ("item", itemId),
            ("lot", lotId)).ConfigureAwait(false);
        if (taken != 1)
        {
            throw new DomainException(InventoryErrors.InsufficientStock, $"Not enough stock of the lot in the location to issue {quantity}.");
        }

        decimal areaQuantity;
        decimal areaValue;
        await using (var valuation = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT quantity, value FROM inv.inv_valuation_balance WHERE valuation_area_id = @area AND item_id = @item FOR UPDATE",
            ("area", areaId),
            ("item", itemId)))
        await using (var reader = await valuation.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Stock exists without a valuation balance.");
            }

            areaQuantity = reader.GetDecimal(0);
            areaValue = reader.GetDecimal(1);
        }

        var value = quantity == areaQuantity
            ? areaValue
            : decimal.Round(areaValue * quantity / areaQuantity, 2, MidpointRounding.AwayFromZero);
        return new IssueReservation(plantId, areaId, locationId, itemId, lotId, quantity, value);
    }

    /// <summary>Writes a reserved issue: negative quantity entry, negative value entry (if the value is not zero) and the valuation balance.</summary>
    public async Task<Guid> WriteIssueAsync(CommandContext context, IssueReservation reservation, Guid? valueEntryId, MovementSource source, MovementDates dates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dates);
        if ((reservation.Value != 0) != (valueEntryId is not null))
        {
            throw new InvalidOperationException("A value entry id is required exactly when the issue has a value.");
        }

        var recordedAt = context.Clock.UtcNow;
        var quantityEntry = new QuantityEntryRow(
            context.Ids.NewId(), context.CompanyId, MovementTypes.Issue, reservation.PlantId, reservation.LocationId, reservation.ItemId, reservation.LotId, -reservation.Quantity,
            source.EventId, source.DocumentType, source.DocumentId, source.LineId, null, Precision.ToMicroseconds(dates.OccurredAt), recordedAt, dates.BusinessDate, dates.PostingDate);
        await InsertQuantityAsync(context, quantityEntry, cancellationToken).ConfigureAwait(false);
        if (valueEntryId is not null)
        {
            await InsertValueAsync(
                context,
                new ValueEntryRow(
                    valueEntryId.Value, context.CompanyId, MovementTypes.Issue, reservation.ValuationAreaId, reservation.PlantId, reservation.ItemId, quantityEntry.QuantityEntryId,
                    -reservation.Value, source.EventId, null, quantityEntry.OccurredAt, recordedAt, dates.BusinessDate, dates.PostingDate),
                cancellationToken).ConfigureAwait(false);
        }

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE inv.inv_valuation_balance SET quantity = quantity - @qty, value = value - @value WHERE valuation_area_id = @area AND item_id = @item",
            cancellationToken,
            ("qty", reservation.Quantity),
            ("value", reservation.Value),
            ("area", reservation.ValuationAreaId),
            ("item", reservation.ItemId)).ConfigureAwait(false);
        return quantityEntry.QuantityEntryId;
    }

    /// <summary>
    /// A movement with explicit quantity and value (both signed), e.g. receipt corrections (E-8 §5.4). Negative quantities use
    /// the conditional UPDATE (never below zero); the value entry exists only when <paramref name="value"/> is not zero and
    /// then needs its pre-assigned <paramref name="valueEntryId"/> (P-1). Callers hold the stock and valuation locks.
    /// </summary>
    public async Task<Guid> PostMovementAsync(
        CommandContext context,
        string movementType,
        Guid locationId,
        Guid itemId,
        Guid lotId,
        decimal quantity,
        decimal value,
        Guid? valueEntryId,
        MovementSource source,
        MovementDates dates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dates);
        if (quantity == 0 || decimal.Round(quantity, 6) != quantity || decimal.Round(value, 2) != value || (value != 0) != (valueEntryId is not null))
        {
            throw new InvalidOperationException("A movement needs a non-zero quantity (6 decimals), a 2-decimal value and a value entry id exactly when the value is not zero.");
        }

        var (plantId, areaId) = await PlantAndAreaAsync(context, locationId, cancellationToken).ConfigureAwait(false);
        if (quantity < 0)
        {
            var taken = await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE inv.inv_stock_balance SET quantity = quantity + @qty WHERE location_id = @location AND item_id = @item AND lot_id = @lot AND quantity >= -@qty",
                cancellationToken,
                ("qty", quantity),
                ("location", locationId),
                ("item", itemId),
                ("lot", lotId)).ConfigureAwait(false);
            if (taken != 1)
            {
                throw new DomainException(InventoryErrors.InsufficientStock, $"Not enough stock of the lot in the location to remove {-quantity}.");
            }
        }
        else
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO inv.inv_stock_balance (company_id, plant_id, location_id, item_id, lot_id, quantity)
                VALUES (@company, @plant, @location, @item, @lot, @qty)
                ON CONFLICT (location_id, item_id, lot_id) DO UPDATE SET quantity = inv.inv_stock_balance.quantity + EXCLUDED.quantity
                """,
                cancellationToken,
                ("company", context.CompanyId),
                ("plant", plantId),
                ("location", locationId),
                ("item", itemId),
                ("lot", lotId),
                ("qty", quantity)).ConfigureAwait(false);
        }

        var recordedAt = context.Clock.UtcNow;
        var quantityEntry = new QuantityEntryRow(
            context.Ids.NewId(), context.CompanyId, movementType, plantId, locationId, itemId, lotId, quantity,
            source.EventId, source.DocumentType, source.DocumentId, source.LineId, null, Precision.ToMicroseconds(dates.OccurredAt), recordedAt, dates.BusinessDate, dates.PostingDate);
        await InsertQuantityAsync(context, quantityEntry, cancellationToken).ConfigureAwait(false);
        if (valueEntryId is not null)
        {
            await InsertValueAsync(
                context,
                new ValueEntryRow(
                    valueEntryId.Value, context.CompanyId, movementType, areaId, plantId, itemId, quantityEntry.QuantityEntryId, value,
                    source.EventId, null, quantityEntry.OccurredAt, recordedAt, dates.BusinessDate, dates.PostingDate),
                cancellationToken).ConfigureAwait(false);
        }

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO inv.inv_valuation_balance (company_id, valuation_area_id, item_id, quantity, value)
            VALUES (@company, @area, @item, @qty, @value)
            ON CONFLICT (valuation_area_id, item_id) DO UPDATE
              SET quantity = inv.inv_valuation_balance.quantity + EXCLUDED.quantity, value = inv.inv_valuation_balance.value + EXCLUDED.value
            """,
            cancellationToken,
            ("company", context.CompanyId),
            ("area", areaId),
            ("item", itemId),
            ("qty", quantity),
            ("value", value)).ConfigureAwait(false);
        return quantityEntry.QuantityEntryId;
    }

    /// <summary>
    /// Stock rows of an item in a plant with quantity &gt; 0, locked (N6) in the issue order of E-PR11-3: the preferred lot first,
    /// then the other lots from oldest to newest (UUIDv7 order), then by location.
    /// </summary>
    public async Task<IReadOnlyList<StockRow>> LockStockOfItemAsync(CommandContext context, Guid plantId, Guid itemId, Guid preferredLotId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT location_id, lot_id, quantity FROM inv.inv_stock_balance
            WHERE company_id = @c AND plant_id = @plant AND item_id = @item AND quantity > 0
            ORDER BY (lot_id = @preferred) DESC, lot_id, location_id
            FOR UPDATE
            """,
            ("c", context.CompanyId),
            ("plant", plantId),
            ("item", itemId),
            ("preferred", preferredLotId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<StockRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new StockRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetDecimal(2)));
        }

        return rows;
    }

    /// <summary>Locks one stock balance row (lock level N6) and returns its quantity (0 when absent).</summary>
    public async Task<decimal> LockStockAsync(CommandContext context, Guid locationId, Guid itemId, Guid lotId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT quantity FROM inv.inv_stock_balance WHERE location_id = @location AND item_id = @item AND lot_id = @lot FOR UPDATE",
            ("location", locationId),
            ("item", itemId),
            ("lot", lotId));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is decimal quantity ? quantity : 0;
    }

    /// <summary>Locks the valuation balance of an area × item (lock level N7) and returns its quantity and value.</summary>
    public async Task<(decimal Quantity, decimal Value)> LockValuationAsync(CommandContext context, Guid valuationAreaId, Guid itemId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT quantity, value FROM inv.inv_valuation_balance WHERE valuation_area_id = @area AND item_id = @item FOR UPDATE",
            ("area", valuationAreaId),
            ("item", itemId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetDecimal(0), reader.GetDecimal(1))
            : (0, 0);
    }

    /// <summary>The receipt movements of a source line (e.g. a goods receipt line): its quantity entry and value entry.</summary>
    public async Task<ReceiptEntries> ReceiptEntriesAsync(CommandContext context, Guid sourceLineId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT q.quantity_entry_id, q.plant_id, q.location_id, q.item_id, q.lot_id, q.quantity, v.value_entry_id, v.valuation_area_id, v.amount
            FROM inv.inv_quantity_entry q
            JOIN inv.inv_value_entry v ON v.quantity_entry_id = q.quantity_entry_id AND v.movement_type = 'RECEIPT'
            WHERE q.company_id = @c AND q.source_line_id = @line AND q.movement_type = 'RECEIPT'
            """,
            ("c", context.CompanyId),
            ("line", sourceLineId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ReceiptEntries(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetGuid(4), reader.GetDecimal(5), reader.GetGuid(6), reader.GetGuid(7), reader.GetDecimal(8))
            : throw new InvalidOperationException($"No receipt movements for source line {sourceLineId}.");
    }

    /// <summary>
    /// True when the lot has any ledger movement other than its receipt (E-8 §5.2: then the receipt cannot be reversed;
    /// use a receipt correction).
    /// </summary>
    public async Task<bool> LotHasLaterMovementsAsync(CommandContext context, Guid lotId, Guid receiptQuantityEntryId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT EXISTS (SELECT 1 FROM inv.inv_quantity_entry WHERE lot_id = @lot AND quantity_entry_id <> @receipt)",
            ("lot", lotId),
            ("receipt", receiptQuantityEntryId));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    /// <summary>
    /// Patch 1 P-4 (R-02 A): the exact inverse of a receipt — −quantity from its lot and location, −value (the original amount,
    /// never recalculated) with pre-assigned <paramref name="reversalValueEntryId"/>, and both balances.
    /// </summary>
    public async Task ReverseReceiptAsync(CommandContext context, ReceiptEntries receipt, Guid reversalValueEntryId, MovementSource source, MovementDates dates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dates);
        var taken = await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE inv.inv_stock_balance SET quantity = quantity - @qty WHERE location_id = @location AND item_id = @item AND lot_id = @lot AND quantity >= @qty",
            cancellationToken,
            ("qty", receipt.Quantity),
            ("location", receipt.LocationId),
            ("item", receipt.ItemId),
            ("lot", receipt.LotId)).ConfigureAwait(false);
        if (taken != 1)
        {
            throw new DomainException(InventoryErrors.InsufficientStock, "The received lot no longer holds the received quantity.");
        }

        var recordedAt = context.Clock.UtcNow;
        var quantityEntry = new QuantityEntryRow(
            context.Ids.NewId(), context.CompanyId, MovementTypes.ReceiptReversal, receipt.PlantId, receipt.LocationId, receipt.ItemId, receipt.LotId, -receipt.Quantity,
            source.EventId, source.DocumentType, source.DocumentId, source.LineId, receipt.QuantityEntryId, Precision.ToMicroseconds(dates.OccurredAt), recordedAt, dates.BusinessDate, dates.PostingDate);
        await InsertQuantityAsync(context, quantityEntry, cancellationToken).ConfigureAwait(false);
        await InsertValueAsync(
            context,
            new ValueEntryRow(
                reversalValueEntryId, context.CompanyId, MovementTypes.ReceiptReversal, receipt.ValuationAreaId, receipt.PlantId, receipt.ItemId, quantityEntry.QuantityEntryId,
                -receipt.Value, source.EventId, receipt.ValueEntryId, quantityEntry.OccurredAt, recordedAt, dates.BusinessDate, dates.PostingDate),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE inv.inv_valuation_balance SET quantity = quantity - @qty, value = value - @value WHERE valuation_area_id = @area AND item_id = @item",
            cancellationToken,
            ("qty", receipt.Quantity),
            ("value", receipt.Value),
            ("area", receipt.ValuationAreaId),
            ("item", receipt.ItemId)).ConfigureAwait(false);
    }

    /// <summary>Patch 1 P-4 (R-02B): a value-only reallocation of <paramref name="amount"/> (signed, 2 decimals) in an area × item.</summary>
    public async Task ReallocateValueAsync(CommandContext context, Guid valuationAreaId, Guid plantId, Guid itemId, decimal amount, Guid valueEntryId, MovementSource source, MovementDates dates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dates);
        if (amount == 0 || decimal.Round(amount, 2) != amount)
        {
            throw new InvalidOperationException("A reallocation needs a non-zero amount with 2 decimals.");
        }

        var recordedAt = context.Clock.UtcNow;
        await InsertValueAsync(
            context,
            new ValueEntryRow(
                valueEntryId, context.CompanyId, MovementTypes.ValuationReallocation, valuationAreaId, plantId, itemId, null, amount,
                source.EventId, null, Precision.ToMicroseconds(dates.OccurredAt), recordedAt, dates.BusinessDate, dates.PostingDate),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE inv.inv_valuation_balance SET value = value + @value WHERE valuation_area_id = @area AND item_id = @item",
            cancellationToken,
            ("value", amount),
            ("area", valuationAreaId),
            ("item", itemId)).ConfigureAwait(false);
    }

    private static async Task<(Guid PlantId, Guid AreaId)> PlantAndAreaAsync(CommandContext context, Guid locationId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT p.plant_id, p.valuation_area_id FROM md.location l JOIN md.plant p ON p.plant_id = l.plant_id WHERE l.company_id = @company AND l.location_id = @location",
            ("company", context.CompanyId),
            ("location", locationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetGuid(0), reader.GetGuid(1))
            : throw new DomainException(InventoryErrors.LocationNotFound, "The location does not exist in this company.");
    }

    private static Task InsertQuantityAsync(CommandContext context, QuantityEntryRow e, CancellationToken cancellationToken)
        => Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO inv.inv_quantity_entry (quantity_entry_id, company_id, movement_type, plant_id, location_id, item_id, lot_id, quantity,
              source_event_id, source_document_type, source_document_id, source_line_id, reverses_quantity_entry_id,
              occurred_at, recorded_at, business_date, posting_date, row_hash)
            VALUES (@id, @company, CAST(@type AS inv.movement_type), @plant, @location, @item, @lot, @qty,
              @event, @doc_type, @doc_id, @line_id, @reverses, @occurred, @recorded, @business, @posting, @hash)
            """,
            cancellationToken,
            ("id", e.QuantityEntryId),
            ("company", e.CompanyId),
            ("type", e.MovementType),
            ("plant", e.PlantId),
            ("location", e.LocationId),
            ("item", e.ItemId),
            ("lot", e.LotId),
            ("qty", e.Quantity),
            ("event", e.SourceEventId),
            ("doc_type", e.SourceDocumentType),
            ("doc_id", e.SourceDocumentId),
            ("line_id", e.SourceLineId),
            ("reverses", e.ReversesQuantityEntryId),
            ("occurred", e.OccurredAt),
            ("recorded", e.RecordedAt),
            ("business", e.BusinessDate),
            ("posting", e.PostingDate),
            ("hash", e.ComputeRowHash()));

    private static Task InsertValueAsync(CommandContext context, ValueEntryRow e, CancellationToken cancellationToken)
        => Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO inv.inv_value_entry (value_entry_id, company_id, movement_type, valuation_area_id, plant_id, item_id, quantity_entry_id, amount,
              source_event_id, reverses_value_entry_id, occurred_at, recorded_at, business_date, posting_date, row_hash)
            VALUES (@id, @company, CAST(@type AS inv.movement_type), @area, @plant, @item, @qty_entry, @amount,
              @event, @reverses, @occurred, @recorded, @business, @posting, @hash)
            """,
            cancellationToken,
            ("id", e.ValueEntryId),
            ("company", e.CompanyId),
            ("type", e.MovementType),
            ("area", e.ValuationAreaId),
            ("plant", e.PlantId),
            ("item", e.ItemId),
            ("qty_entry", e.QuantityEntryId),
            ("amount", e.Amount),
            ("event", e.SourceEventId),
            ("reverses", e.ReversesValueEntryId),
            ("occurred", e.OccurredAt),
            ("recorded", e.RecordedAt),
            ("business", e.BusinessDate),
            ("posting", e.PostingDate),
            ("hash", e.ComputeRowHash()));

    /// <summary>Reads inv_quantity_entry columns in declared order (tests recompute row hashes with it).</summary>
    public static QuantityEntryRow ReadQuantityEntry(DbDataReader r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new QuantityEntryRow(
            r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.GetGuid(4), r.GetGuid(5), r.GetGuid(6), r.GetDecimal(7), r.GetGuid(8), r.GetString(9),
            r.GetGuid(10), r.IsDBNull(11) ? null : r.GetGuid(11), r.IsDBNull(12) ? null : r.GetGuid(12), r.GetFieldValue<DateTime>(13), r.GetFieldValue<DateTime>(14),
            r.GetFieldValue<DateOnly>(15), r.GetFieldValue<DateOnly>(16));
    }

    /// <summary>Reads inv_value_entry columns in declared order.</summary>
    public static ValueEntryRow ReadValueEntry(DbDataReader r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new ValueEntryRow(
            r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.GetGuid(4), r.GetGuid(5), r.IsDBNull(6) ? null : r.GetGuid(6), r.GetDecimal(7), r.GetGuid(8),
            r.IsDBNull(9) ? null : r.GetGuid(9), r.GetFieldValue<DateTime>(10), r.GetFieldValue<DateTime>(11), r.GetFieldValue<DateOnly>(12), r.GetFieldValue<DateOnly>(13));
    }
}
