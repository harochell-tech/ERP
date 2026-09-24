using System.Globalization;
using System.Text.Json;
using Rochell.Finance.Policies;
using Rochell.Finance.Posting;
using Rochell.Inventory;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Time;
using Rochell.Procurement.PurchaseOrders;

namespace Rochell.Procurement.ReceiptCorrections;

internal static class ReceiptCorrectionStore
{
    public const string Aggregate = "ReceiptCorrection";
    public const string Draft = "DRAFT";
    public const string PendingApproval = "PENDING_APPROVAL";
    public const string Posted = "POSTED";
    public const string Rejected = "REJECTED";

    /// <summary>E-8 §5.4 guards on the PO line: Δq &lt; 0 keeps invoiced ≤ received; Δq &gt; 0 stays within tolerance plus approved over-receipt.</summary>
    public static void CheckLine(CorrectedLine line, decimal delta)
    {
        if (delta < 0 && line.QtyInvoiced > line.QtyReceived + delta)
        {
            throw new DomainException(ProcurementErrors.AlreadyInvoiced, $"Only {line.QtyReceived - line.QtyInvoiced} of this line is not invoiced; reverse the supplier invoice first.");
        }

        var maximum = line.QtyOrdered * (1 + line.ReceiptTolerancePct) + line.QtyOverReceiptApproved;
        if (delta > 0 && line.QtyReceived + delta > maximum)
        {
            throw new DomainException(ProcurementErrors.ReceiptToleranceExceeded, $"Receiving {delta} more would exceed the allowed {maximum}; approve an over-receipt first.");
        }
    }

    public static async Task<CorrectedLine?> ReadLineAsync(CommandContext context, Guid grId, Guid grLineId, bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT g.gr_id, g.document_status::text, g.po_id, po.plant_id, po.party_id, gl.gr_line_id, gl.qty, gl.lot_id,
                   pol.po_line_id, pol.item_id, pol.unit_price, pol.qty_ordered, pol.receipt_tolerance_pct, pol.qty_over_receipt_approved,
                   pol.qty_received, pol.qty_invoiced
            FROM pur.goods_receipt g
            JOIN pur.goods_receipt_line gl ON gl.gr_id = g.gr_id
            JOIN pur.purchase_order_line pol ON pol.po_line_id = gl.po_line_id
            JOIN pur.purchase_order po ON po.po_id = g.po_id
            WHERE g.company_id = @c AND g.gr_id = @g AND gl.gr_line_id = @l
            """ + (forUpdate ? " FOR UPDATE OF g" : string.Empty),
            ("c", context.CompanyId),
            ("g", grId),
            ("l", grLineId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CorrectedLine(
                reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetGuid(4), reader.GetGuid(5), reader.GetDecimal(6), reader.GetGuid(7),
                reader.GetGuid(8), reader.GetGuid(9), reader.GetDecimal(10), reader.GetDecimal(11), reader.GetDecimal(12), reader.GetDecimal(13), reader.GetDecimal(14), reader.GetDecimal(15))
            : null;
    }

    public static async Task<Correction> LockAsync(CommandContext context, Guid rcId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT rc_id, gr_id, gr_line_id, delta_qty, created_by, document_status, version FROM pur.receipt_correction WHERE company_id = @c AND rc_id = @r FOR UPDATE",
            ("c", context.CompanyId),
            ("r", rcId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new Correction(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetDecimal(3), reader.GetGuid(4), reader.GetString(5), reader.GetInt64(6))
            : throw new DomainException(ProcurementErrors.CorrectionNotFound, "The receipt correction does not exist.");
    }
}

internal sealed record CorrectedLine(
    Guid GrId, string GrStatus, Guid PoId, Guid PlantId, Guid PartyId, Guid GrLineId, decimal GrLineQty, Guid LotId,
    Guid PoLineId, Guid ItemId, decimal UnitPrice, decimal QtyOrdered, decimal ReceiptTolerancePct, decimal QtyOverReceiptApproved, decimal QtyReceived, decimal QtyInvoiced);

internal sealed record Correction(Guid Id, Guid GrId, Guid GrLineId, decimal Delta, Guid CreatedBy, string Status, long Version);

[RequiresPermission("receipt_correction:create")]
public sealed class CreateReceiptCorrectionHandler : ICommandHandler<CreateReceiptCorrection>
{
    public string CommandType => "Procurement.CreateReceiptCorrection";

    public async Task<string> HandleAsync(CreateReceiptCorrection command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = PurchaseOrderStore.RequireReason(command.Reason);
        var evidence = string.IsNullOrWhiteSpace(command.EvidenceReference)
            ? throw new DomainException(ProcurementErrors.EvidenceRequired, "A correction needs an evidence reference (corrected ticket, photo, record).")
            : command.EvidenceReference.Trim();
        if (command.DeltaQuantity == 0 || decimal.Round(command.DeltaQuantity, 6) != command.DeltaQuantity)
        {
            throw new DomainException(ProcurementErrors.QuantityInvalid, "The correction must be non-zero with at most 6 decimals.");
        }

        var line = await ReceiptCorrectionStore.ReadLineAsync(context, command.GoodsReceiptId, command.GoodsReceiptLineId, forUpdate: false, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(ProcurementErrors.ReceiptNotFound, "The goods receipt line does not exist.");
        if (line.PlantId != command.PlantId)
        {
            throw new DomainException(ProcurementErrors.PlantMismatch, "The receipt belongs to another plant.");
        }

        if (line.GrStatus is not ("POSTED" or "CORRECTED"))
        {
            throw new DomainException(ProcurementErrors.InvalidState, $"The goods receipt is {line.GrStatus}; only POSTED or CORRECTED receipts can be corrected.");
        }

        ReceiptCorrectionStore.CheckLine(line, command.DeltaQuantity);

        // E-PR11-4: above materiality the correction waits for the Controller's re-authenticated approval.
        var policy = await PolicyResolver.ResolveAsync(context, PolicyCodes.Inventory, BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow), cancellationToken).ConfigureAwait(false);
        var amount = Math.Abs(command.DeltaQuantity) * line.UnitPrice;
        var status = amount > policy.Decimal(PolicyParameters.InventoryAdjustmentMateriality) ? ReceiptCorrectionStore.PendingApproval : ReceiptCorrectionStore.Draft;

        var creator = await PurchaseOrderStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var rcId = context.ResultRef;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ReceiptCorrectionCreated",
                1,
                ReceiptCorrectionStore.Aggregate,
                rcId,
                1,
                JsonSerializer.Serialize(new
                {
                    rcId,
                    grId = line.GrId,
                    grLineId = line.GrLineId,
                    deltaQuantity = command.DeltaQuantity.ToString(CultureInfo.InvariantCulture),
                    amount = amount.ToString(CultureInfo.InvariantCulture),
                    status,
                    reason,
                    evidence,
                }),
                Publish: status == ReceiptCorrectionStore.PendingApproval),
            cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(ReceiptCorrectionStore.Aggregate, rcId, "DOCUMENT", null, status, CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO pur.receipt_correction (rc_id, company_id, gr_id, gr_line_id, delta_qty, reason, evidence_object_key, document_status, accounting_status, created_by, version)
            VALUES (@id, @c, @gr, @line, @delta, @reason, @evidence, @status, 'NOT_POSTED', @creator, 1)
            """,
            cancellationToken,
            ("id", rcId),
            ("c", context.CompanyId),
            ("gr", line.GrId),
            ("line", line.GrLineId),
            ("delta", command.DeltaQuantity),
            ("reason", reason),
            ("evidence", evidence),
            ("status", status),
            ("creator", creator)).ConfigureAwait(false);

        return JsonSerializer.Serialize(new { receiptCorrectionId = rcId, status, version = 1 });
    }
}

/// <summary>
/// T-05. Δq &gt; 0 → R-03A: +Δq to the receipt's lot (E-PR11-2), Dr RAW_MATERIAL / Cr GRNI for Δq × P.
/// Δq &lt; 0 → R-03B: q₁ = min(area quantity, |Δq|) leaves stock at the average cost, starting with the receipt's lot then the
/// oldest lots (E-PR11-3); q₂ = |Δq| − q₁ goes to MATERIAL_USAGE_VARIANCE at P; Dr GRNI |Δq| × P; the price difference of q₁ to
/// PURCHASE_PRICE_VARIANCE. Lock order: correction, receipt and order (N3) → order line (N4) → stock (N6) → valuation (N7) → GL (N9).
/// </summary>
[RequiresPermission("receipt_correction:approve")]
public sealed class ApproveReceiptCorrectionHandler : ICommandHandler<ApproveReceiptCorrection>
{
    public const string IncreaseRule = "R-03A";
    public const string DecreaseRule = "R-03B";

    private readonly PostingEngine _engine = new();
    private readonly InventoryLedger _inventory = new();

    public string CommandType => "Procurement.ApproveReceiptCorrection";

    public async Task<string> HandleAsync(ApproveReceiptCorrection command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var rc = await ReceiptCorrectionStore.LockAsync(context, command.CorrectionId, cancellationToken).ConfigureAwait(false);
        if (rc.Status is not (ReceiptCorrectionStore.Draft or ReceiptCorrectionStore.PendingApproval))
        {
            throw new DomainException(ProcurementErrors.CorrectionNotPending, $"The correction is {rc.Status}.");
        }

        var approver = await PurchaseOrderStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (approver == rc.CreatedBy)
        {
            throw new DomainException(ProcurementErrors.ApproverIsCreator, "The person who created the correction cannot approve it.");
        }

        if (rc.Status == ReceiptCorrectionStore.PendingApproval)
        {
            await context.RequireStepUpAsync(cancellationToken).ConfigureAwait(false);
        }

        // N3: the receipt, then its order; N4: the order line. Values are re-read once every lock is held.
        var locked = await ReceiptCorrectionStore.ReadLineAsync(context, rc.GrId, rc.GrLineId, forUpdate: true, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A correction without its receipt line.");
        var header = await PurchaseOrderStore.LockAsync(context, locked.PoId, locked.PlantId, null, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "SELECT 1 FROM pur.purchase_order_line WHERE po_line_id = @l FOR UPDATE", cancellationToken, ("l", locked.PoLineId)).ConfigureAwait(false);
        var line = (await ReceiptCorrectionStore.ReadLineAsync(context, rc.GrId, rc.GrLineId, forUpdate: false, cancellationToken).ConfigureAwait(false))!;
        if (line.GrStatus is not ("POSTED" or "CORRECTED"))
        {
            throw new DomainException(ProcurementErrors.InvalidState, $"The goods receipt is {line.GrStatus}; it can no longer be corrected.");
        }

        ReceiptCorrectionStore.CheckLine(line, rc.Delta);

        // Same conversion as the receipt itself (base quantity ÷ PO quantity of the line).
        var receipt = await _inventory.ReceiptEntriesAsync(context, line.GrLineId, cancellationToken).ConfigureAwait(false);
        var factor = receipt.Quantity / line.GrLineQty;
        var occurredAt = context.Clock.UtcNow;
        var businessDate = BusinessCalendar.DefaultBusinessDate(occurredAt);
        var magnitude = Math.Abs(rc.Delta);
        var baseQuantity = decimal.Round(magnitude * factor, 6, MidpointRounding.AwayFromZero);
        var grni = decimal.Round(magnitude * line.UnitPrice, 2, MidpointRounding.AwayFromZero);

        var movements = new List<(StockRow Row, decimal Quantity, decimal Value, Guid? ValueEntryId)>();
        PostingRequest request;
        if (rc.Delta > 0)
        {
            await _inventory.LockStockAsync(context, receipt.LocationId, receipt.ItemId, receipt.LotId, cancellationToken).ConfigureAwait(false);
            await _inventory.LockValuationAsync(context, receipt.ValuationAreaId, receipt.ItemId, cancellationToken).ConfigureAwait(false);
            var valueEntryId = context.Ids.NewId();
            movements.Add((new StockRow(receipt.LocationId, receipt.LotId, 0), baseQuantity, grni, valueEntryId));
            request = new PostingRequest(
                IncreaseRule,
                businessDate,
                occurredAt,
                [
                    new PostingLineInput("R03A-DR-INV", "correction_value", grni, PlantId: line.PlantId, ItemId: line.ItemId, SubledgerRef: valueEntryId, InvValueEntryId: valueEntryId),
                    new PostingLineInput("R03A-CR-GRNI", "correction_value", grni, PlantId: line.PlantId, PartyId: line.PartyId),
                ]);
        }
        else
        {
            var rows = await _inventory.LockStockOfItemAsync(context, line.PlantId, line.ItemId, line.LotId, cancellationToken).ConfigureAwait(false);
            var (areaQuantity, areaValue) = await _inventory.LockValuationAsync(context, receipt.ValuationAreaId, receipt.ItemId, cancellationToken).ConfigureAwait(false);
            var q1 = Math.Min(areaQuantity, baseQuantity);
            var stockValue = q1 == 0 ? 0 : q1 == areaQuantity ? areaValue : decimal.Round(areaValue * q1 / areaQuantity, 2, MidpointRounding.AwayFromZero);

            var remaining = q1;
            var allocated = 0m;
            foreach (var row in rows)
            {
                if (remaining == 0)
                {
                    break;
                }

                var take = Math.Min(row.Quantity, remaining);
                remaining -= take;
                var value = remaining == 0 ? stockValue - allocated : decimal.Round(areaValue * take / areaQuantity, 2, MidpointRounding.AwayFromZero);
                allocated += value;
                movements.Add((row, -take, -value, value == 0 ? null : context.Ids.NewId()));
            }

            var q1AtPrice = baseQuantity == 0 ? 0 : decimal.Round(grni * q1 / baseQuantity, 2, MidpointRounding.AwayFromZero);
            var usageVariance = grni - q1AtPrice;
            var priceVariance = q1AtPrice - stockValue;
            var lines = new List<PostingLineInput> { new("R03B-DR-GRNI", "grni_value", grni, PlantId: line.PlantId, PartyId: line.PartyId) };
            lines.AddRange(movements.Where(m => m.ValueEntryId is not null).Select(m =>
                new PostingLineInput("R03B-CR-INV", "stock_value", -m.Value, PlantId: line.PlantId, ItemId: line.ItemId, SubledgerRef: m.ValueEntryId, InvValueEntryId: m.ValueEntryId)));
            if (priceVariance != 0)
            {
                lines.Add(priceVariance > 0
                    ? new PostingLineInput("R03B-CR-PPV", "price_variance", priceVariance, PlantId: line.PlantId, ItemId: line.ItemId)
                    : new PostingLineInput("R03B-DR-PPV", "price_variance", -priceVariance, PlantId: line.PlantId, ItemId: line.ItemId));
            }

            if (usageVariance != 0)
            {
                lines.Add(new PostingLineInput("R03B-CR-MUV", "usage_variance", usageVariance, PlantId: line.PlantId, ItemId: line.ItemId));
            }

            request = new PostingRequest(DecreaseRule, businessDate, occurredAt, lines);
        }

        // P-1: every posting prerequisite before any write.
        var plan = await _engine.PrepareAsync(context, request, cancellationToken).ConfigureAwait(false);

        var newVersion = rc.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ReceiptCorrectionPosted",
                1,
                ReceiptCorrectionStore.Aggregate,
                rc.Id,
                newVersion,
                JsonSerializer.Serialize(new
                {
                    rcId = rc.Id,
                    grId = line.GrId,
                    grLineId = line.GrLineId,
                    deltaQuantity = rc.Delta.ToString(CultureInfo.InvariantCulture),
                    baseQuantity = baseQuantity.ToString(CultureInfo.InvariantCulture),
                    approvedBy = approver,
                }),
                Publish: true,
                OccurredAt: occurredAt,
                BusinessDate: businessDate),
            cancellationToken).ConfigureAwait(false);

        await context.AppendStateAsync(ReceiptCorrectionStore.Aggregate, rc.Id, "DOCUMENT", rc.Status, ReceiptCorrectionStore.Posted, CommandType, eventId, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            UPDATE pur.receipt_correction
            SET document_status = 'POSTED', accounting_status = 'POSTED', approved_by = @approver, posting_event_id = @event, version = @version
            WHERE rc_id = @id
            """,
            cancellationToken,
            ("approver", approver),
            ("event", eventId),
            ("version", newVersion),
            ("id", rc.Id)).ConfigureAwait(false);

        if (line.GrStatus == "POSTED")
        {
            await context.AppendStateAsync("GoodsReceipt", line.GrId, "DOCUMENT", "POSTED", "CORRECTED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE pur.goods_receipt SET document_status = 'CORRECTED', version = version + 1 WHERE gr_id = @g",
                cancellationToken,
                ("g", line.GrId)).ConfigureAwait(false);
        }

        var dates = new MovementDates(occurredAt, businessDate, plan.PostingDate);
        var source = new MovementSource(eventId, "RECEIPT_CORRECTION", rc.Id, line.GrLineId);
        foreach (var m in movements)
        {
            await _inventory.PostMovementAsync(context, MovementTypes.ReceiptCorrection, m.Row.LocationId, line.ItemId, m.Row.LotId, m.Quantity, m.Value, m.ValueEntryId, source, dates, cancellationToken).ConfigureAwait(false);
        }

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE pur.purchase_order_line SET qty_received = qty_received + @q, version = version + 1 WHERE po_line_id = @l",
            cancellationToken,
            ("q", rc.Delta),
            ("l", line.PoLineId)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO core.document_link (link_id, company_id, from_type, from_id, to_type, to_id, to_line_id, link_type, qty, amount, event_id)
            VALUES (@id, @c, 'ReceiptCorrection', @rc, 'GoodsReceipt', @gr, @grl, 'CORRECTS', @qty, @amount, @event)
            """,
            cancellationToken,
            ("id", context.Ids.NewId()),
            ("c", context.CompanyId),
            ("rc", rc.Id),
            ("gr", line.GrId),
            ("grl", line.GrLineId),
            ("qty", rc.Delta),
            ("amount", grni),
            ("event", eventId)).ConfigureAwait(false);

        var newStatus = await PurchaseOrderStore.StatusFromReceiptsAsync(context, header.Id, cancellationToken).ConfigureAwait(false);
        if (newStatus != header.Status)
        {
            await PurchaseOrderStore.TransitionAsync(context, header, newStatus, CommandType, "PurchaseOrderReceiptStatusChanged", new { poId = header.Id, status = newStatus, rcId = rc.Id }, publish: true, cancellationToken).ConfigureAwait(false);
        }

        var journal = await _engine.WriteAsync(context, plan, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            receiptCorrectionId = rc.Id,
            status = ReceiptCorrectionStore.Posted,
            journalId = journal.JournalId,
            purchaseOrderStatus = newStatus,
            version = newVersion,
        });
    }
}

[RequiresPermission("receipt_correction:approve")]
public sealed class RejectReceiptCorrectionHandler : ICommandHandler<RejectReceiptCorrection>
{
    public string CommandType => "Procurement.RejectReceiptCorrection";

    public async Task<string> HandleAsync(RejectReceiptCorrection command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = PurchaseOrderStore.RequireReason(command.Reason);
        var rc = await ReceiptCorrectionStore.LockAsync(context, command.CorrectionId, cancellationToken).ConfigureAwait(false);
        if (rc.Status != ReceiptCorrectionStore.PendingApproval)
        {
            throw new DomainException(ProcurementErrors.CorrectionNotPending, $"Only corrections pending approval can be rejected; this one is {rc.Status}.");
        }

        var newVersion = rc.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft("ReceiptCorrectionRejected", 1, ReceiptCorrectionStore.Aggregate, rc.Id, newVersion, JsonSerializer.Serialize(new { rcId = rc.Id, reason }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(ReceiptCorrectionStore.Aggregate, rc.Id, "DOCUMENT", rc.Status, ReceiptCorrectionStore.Rejected, CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE pur.receipt_correction SET document_status = 'REJECTED', version = @v WHERE rc_id = @id",
            cancellationToken,
            ("v", newVersion),
            ("id", rc.Id)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { receiptCorrectionId = rc.Id, status = ReceiptCorrectionStore.Rejected, version = newVersion });
    }
}
