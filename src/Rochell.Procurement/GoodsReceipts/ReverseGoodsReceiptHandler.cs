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
/// T-03. Guards (E-8 §5.2): receipt POSTED; nothing invoiced beyond what remains received; every lot of the receipt untouched
/// since the receipt (otherwise: receipt correction). Effects (Patch 1 P-4):
/// R-02 (A) exact inverse of the R-01 journal and of each receipt value entry; R-02B only when, after A, an area × item is left
/// with quantity 0 and value ≠ 0, or quantity &gt; 0 and value ≤ 0 — reallocated to target (0, or quantity × avg₀ with avg₀ the
/// average immediately before the reversal, E-PR10-1) against PURCHASE_PRICE_VARIANCE.
/// Lock order: receipt and order (N3) → order lines (N4) → stock (N6) → valuation (N7) → GL balances (N9).
/// </summary>
[RequiresPermission("goods_receipt:reverse", StepUp = true)]
public sealed class ReverseGoodsReceiptHandler : ICommandHandler<ReverseGoodsReceipt>
{
    public const string Aggregate = "GoodsReceiptReversal";
    public const string ReallocationRule = "R-02B";

    private readonly PostingEngine _engine = new();
    private readonly InventoryLedger _inventory = new();

    public string CommandType => "Procurement.ReverseGoodsReceipt";

    public async Task<string> HandleAsync(ReverseGoodsReceipt command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = PurchaseOrderStore.RequireReason(command.Reason);

        // N3: the receipt, then its order.
        var receipt = await LockReceiptAsync(context, command.GoodsReceiptId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(ProcurementErrors.ReceiptNotFound, "The goods receipt does not exist.");
        if (receipt.DocumentStatus != "POSTED" || receipt.AccountingStatus != "POSTED")
        {
            throw new DomainException(ProcurementErrors.ReceiptNotReversible, $"The goods receipt is {receipt.DocumentStatus}; only POSTED receipts without corrections can be reversed.");
        }

        var header = await PurchaseOrderStore.LockAsync(context, receipt.PoId, receipt.PlantId, null, cancellationToken).ConfigureAwait(false);

        // N4: the order lines received by this receipt; nothing invoiced beyond what remains received.
        var lines = await LockReceiptLinesAsync(context, receipt.GrId, cancellationToken).ConfigureAwait(false);
        foreach (var line in lines.Where(l => l.QtyInvoiced > l.QtyReceived - l.Qty))
        {
            throw new DomainException(ProcurementErrors.AlreadyInvoiced, $"PO line {line.PoLineId} has been invoiced; reverse the supplier invoice first.");
        }

        // The receipt movements of each line; lots must be untouched since the receipt.
        var movements = new List<(ReceiptLine Line, ReceiptEntries Entries, Guid ReversalValueEntryId)>();
        foreach (var line in lines)
        {
            var entries = await _inventory.ReceiptEntriesAsync(context, line.GrLineId, cancellationToken).ConfigureAwait(false);
            if (await _inventory.LotHasLaterMovementsAsync(context, entries.LotId, entries.QuantityEntryId, cancellationToken).ConfigureAwait(false))
            {
                throw new DomainException(ProcurementErrors.UseReceiptCorrection, "A lot of this receipt has moved since it was received; use a receipt correction instead.");
            }

            movements.Add((line, entries, context.Ids.NewId()));
        }

        // N6 then N7: stock rows, then valuation balances; compute R-02B needs (E-PR10-1).
        foreach (var m in movements.OrderBy(m => m.Entries.LocationId).ThenBy(m => m.Entries.ItemId).ThenBy(m => m.Entries.LotId))
        {
            await _inventory.LockStockAsync(context, m.Entries.LocationId, m.Entries.ItemId, m.Entries.LotId, cancellationToken).ConfigureAwait(false);
        }

        var reallocations = new List<Reallocation>();
        foreach (var group in movements.GroupBy(m => (m.Entries.ValuationAreaId, m.Entries.ItemId)).OrderBy(g => g.Key.ValuationAreaId).ThenBy(g => g.Key.ItemId))
        {
            var (quantityBefore, valueBefore) = await _inventory.LockValuationAsync(context, group.Key.ValuationAreaId, group.Key.ItemId, cancellationToken).ConfigureAwait(false);
            var quantityAfter = quantityBefore - group.Sum(m => m.Entries.Quantity);
            var valueAfter = valueBefore - group.Sum(m => m.Entries.Value);
            if ((quantityAfter == 0 && valueAfter != 0) || (quantityAfter > 0 && valueAfter <= 0))
            {
                var target = quantityAfter == 0 ? 0 : decimal.Round(quantityAfter * valueBefore / quantityBefore, 2, MidpointRounding.AwayFromZero);
                var amount = target - valueAfter;
                if (amount != 0)
                {
                    reallocations.Add(new Reallocation(group.Key.ValuationAreaId, group.First().Entries.PlantId, group.Key.ItemId, amount, context.Ids.NewId()));
                }
            }
        }

        // P-1: every posting prerequisite before any write.
        var occurredAt = context.Clock.UtcNow;
        var businessDate = BusinessCalendar.DefaultBusinessDate(occurredAt);
        var originalJournal = await OriginalJournalAsync(context, receipt.PostingEventId, cancellationToken).ConfigureAwait(false);
        var reversalPlan = await _engine.PrepareReversalAsync(context, originalJournal, businessDate, cancellationToken).ConfigureAwait(false);
        PostingPlan? reallocationPlan = null;
        if (reallocations.Count > 0)
        {
            reallocationPlan = await _engine.PrepareAsync(
                context,
                new PostingRequest(
                    ReallocationRule,
                    businessDate,
                    occurredAt,
                    reallocations.SelectMany(r => r.Amount > 0
                        ? new[]
                        {
                            new PostingLineInput("R02B-DR-INV", "reallocation", r.Amount, PlantId: r.PlantId, ItemId: r.ItemId, SubledgerRef: r.ValueEntryId, InvValueEntryId: r.ValueEntryId),
                            new PostingLineInput("R02B-CR-PPV", "reallocation", r.Amount, PlantId: r.PlantId, ItemId: r.ItemId),
                        }
                        : new[]
                        {
                            new PostingLineInput("R02B-CR-INV", "reallocation", -r.Amount, PlantId: r.PlantId, ItemId: r.ItemId, SubledgerRef: r.ValueEntryId, InvValueEntryId: r.ValueEntryId),
                            new PostingLineInput("R02B-DR-PPV", "reallocation", -r.Amount, PlantId: r.PlantId, ItemId: r.ItemId),
                        }).ToList(),
                    JournalType: "VALUATION_REALLOCATION"),
                cancellationToken).ConfigureAwait(false);
        }

        // Writes.
        var grrId = context.ResultRef;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "GoodsReceiptReversed",
                1,
                Aggregate,
                grrId,
                1,
                JsonSerializer.Serialize(new
                {
                    grrId,
                    grId = receipt.GrId,
                    grNo = receipt.GrNo,
                    poId = header.Id,
                    reason,
                    reallocations = reallocations.Select(r => new { itemId = r.ItemId, amount = r.Amount.ToString(CultureInfo.InvariantCulture) }),
                }),
                Publish: true,
                OccurredAt: occurredAt,
                BusinessDate: businessDate),
            cancellationToken).ConfigureAwait(false);

        await context.AppendStateAsync(Aggregate, grrId, "DOCUMENT", null, "POSTED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        await context.AppendStateAsync(PostGoodsReceiptHandler.Aggregate, receipt.GrId, "DOCUMENT", "POSTED", "REVERSED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE pur.goods_receipt SET document_status = 'REVERSED', accounting_status = 'REVERSED', version = version + 1 WHERE gr_id = @g",
            cancellationToken,
            ("g", receipt.GrId)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO pur.goods_receipt_reversal (grr_id, company_id, reversed_gr_id, reason, accounting_status, posting_event_id, version)
            VALUES (@id, @c, @gr, @reason, 'POSTED', @event, 1)
            """,
            cancellationToken,
            ("id", grrId),
            ("c", context.CompanyId),
            ("gr", receipt.GrId),
            ("reason", reason),
            ("event", eventId)).ConfigureAwait(false);

        var dates = new MovementDates(occurredAt, businessDate, reversalPlan.PostingDate);
        var valueEntryMap = new Dictionary<Guid, Guid>();
        foreach (var m in movements)
        {
            await _inventory.ReverseReceiptAsync(context, m.Entries, m.ReversalValueEntryId, new MovementSource(eventId, "GOODS_RECEIPT_REVERSAL", grrId, m.Line.GrLineId), dates, cancellationToken).ConfigureAwait(false);
            valueEntryMap[m.Entries.ValueEntryId] = m.ReversalValueEntryId;
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE pur.purchase_order_line SET qty_received = qty_received - @q, version = version + 1 WHERE po_line_id = @l",
                cancellationToken,
                ("q", m.Line.Qty),
                ("l", m.Line.PoLineId)).ConfigureAwait(false);
        }

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO core.document_link (link_id, company_id, from_type, from_id, to_type, to_id, link_type, event_id)
            VALUES (@id, @c, 'GoodsReceiptReversal', @grr, 'GoodsReceipt', @gr, 'REVERSES', @event)
            """,
            cancellationToken,
            ("id", context.Ids.NewId()),
            ("c", context.CompanyId),
            ("grr", grrId),
            ("gr", receipt.GrId),
            ("event", eventId)).ConfigureAwait(false);

        var newStatus = await PurchaseOrderStore.StatusFromReceiptsAsync(context, header.Id, cancellationToken).ConfigureAwait(false);
        if (newStatus != header.Status)
        {
            await PurchaseOrderStore.TransitionAsync(context, header, newStatus, CommandType, "PurchaseOrderReceiptStatusChanged", new { poId = header.Id, status = newStatus, grrId }, publish: true, cancellationToken).ConfigureAwait(false);
        }

        var reversalJournal = await _engine.WriteReversalAsync(context, reversalPlan, eventId, occurredAt, cancellationToken, valueEntryMap).ConfigureAwait(false);
        Guid? reallocationJournal = null;
        if (reallocationPlan is not null)
        {
            var reallocationDates = new MovementDates(occurredAt, businessDate, reallocationPlan.PostingDate);
            foreach (var r in reallocations)
            {
                await _inventory.ReallocateValueAsync(context, r.ValuationAreaId, r.PlantId, r.ItemId, r.Amount, r.ValueEntryId, new MovementSource(eventId, "GOODS_RECEIPT_REVERSAL", grrId), reallocationDates, cancellationToken).ConfigureAwait(false);
            }

            reallocationJournal = (await _engine.WriteAsync(context, reallocationPlan, eventId, cancellationToken).ConfigureAwait(false)).JournalId;
        }

        return JsonSerializer.Serialize(new
        {
            goodsReceiptReversalId = grrId,
            goodsReceiptId = receipt.GrId,
            purchaseOrderStatus = newStatus,
            reversalJournalId = reversalJournal.JournalId,
            reallocationJournalId = reallocationJournal,
        });
    }

    private static async Task<ReceiptHeader?> LockReceiptAsync(CommandContext context, Guid grId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT g.gr_id, g.gr_no, g.po_id, po.plant_id, g.document_status::text, g.accounting_status::text, g.posting_event_id
            FROM pur.goods_receipt g JOIN pur.purchase_order po ON po.po_id = g.po_id
            WHERE g.company_id = @c AND g.gr_id = @g
            FOR UPDATE OF g
            """,
            ("c", context.CompanyId),
            ("g", grId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ReceiptHeader(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetString(4), reader.GetString(5), reader.GetGuid(6))
            : null;
    }

    private static async Task<List<ReceiptLine>> LockReceiptLinesAsync(CommandContext context, Guid grId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT gl.gr_line_id, gl.po_line_id, gl.qty, pol.qty_received, pol.qty_invoiced
            FROM pur.goods_receipt_line gl JOIN pur.purchase_order_line pol ON pol.po_line_id = gl.po_line_id
            WHERE gl.gr_id = @g
            ORDER BY pol.po_line_id
            FOR UPDATE OF pol
            """,
            ("g", grId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var lines = new List<ReceiptLine>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lines.Add(new ReceiptLine(reader.GetGuid(0), reader.GetGuid(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetDecimal(4)));
        }

        return lines;
    }

    private static async Task<Guid> OriginalJournalAsync(CommandContext context, Guid postingEventId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT j.journal_id FROM fin.gl_journal j
            WHERE j.company_id = @c AND j.source_event_id = @e AND j.journal_type = 'AUTO'
              AND NOT EXISTS (SELECT 1 FROM fin.gl_journal r WHERE r.reverses_journal_id = j.journal_id)
            ORDER BY j.posting_generation DESC
            LIMIT 1
            """,
            ("c", context.CompanyId),
            ("e", postingEventId));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid journal
            ? journal
            : throw new InvalidOperationException("A POSTED goods receipt without an unreversed journal (K-25 should prevent this).");
    }

    private sealed record ReceiptHeader(Guid GrId, string GrNo, Guid PoId, Guid PlantId, string DocumentStatus, string AccountingStatus, Guid PostingEventId);

    private sealed record ReceiptLine(Guid GrLineId, Guid PoLineId, decimal Qty, decimal QtyReceived, decimal QtyInvoiced);

    private sealed record Reallocation(Guid ValuationAreaId, Guid PlantId, Guid ItemId, decimal Amount, Guid ValueEntryId);
}
