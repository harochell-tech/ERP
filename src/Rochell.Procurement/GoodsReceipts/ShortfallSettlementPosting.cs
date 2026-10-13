using System.Globalization;
using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.Inventory;
using Rochell.Platform.Commands;

namespace Rochell.Procurement.GoodsReceipts;

/// <summary>
/// VS3-02c (E-VS3-02c-2): the journal of what a receipt settled (P-48) — Dr MATERIAL_SHORTFALL at the standard value with which production
/// was charged / Cr RAW_MATERIAL at the real cost of the new lot, the difference to MATERIAL_PRICE_VARIANCE — and its inventory side.
/// Sales keeps the same for the opening (the module graph keeps them apart).
/// </summary>
internal static class ShortfallSettlementPosting
{
    public static async Task<Guid> PostAsync(
        CommandContext context, PostingEngine engine, InventoryLedger inventory, IReadOnlyList<ShortfallSettlement> settlements, Guid plantId, string aggregate, Guid aggregateId,
        long eventVersion, string sourceType, Guid sourceId, DateTime occurredAt, DateOnly businessDate, CancellationToken cancellationToken)
    {
        var lines = new List<PostingLineInput>();
        foreach (var settlement in settlements)
        {
            var itemId = settlement.Reservation.ItemId;
            foreach (var share in settlement.Shares)
            {
                var inputs = new Dictionary<string, string>
                {
                    ["material_code"] = settlement.MaterialCode,
                    ["lot_code"] = settlement.LotCode,
                    ["quantity"] = share.Qty.ToString(CultureInfo.InvariantCulture),
                };
                if (share.StdValue > 0m)
                {
                    lines.Add(new PostingLineInput("P48-DR-SHORT", "std_value", share.StdValue, PlantId: plantId, ItemId: itemId, Inputs: inputs));
                }

                if (share.ActualValue > 0m)
                {
                    lines.Add(new PostingLineInput("P48-CR-RM", "actual_value", share.ActualValue, PlantId: plantId, ItemId: itemId, SubledgerRef: share.ValueEntryId, InvValueEntryId: share.ValueEntryId, Inputs: inputs));
                }

                var variance = share.ActualValue - share.StdValue;
                if (variance != 0m)
                {
                    lines.Add(new PostingLineInput(variance > 0m ? "P48-DR-PRICE" : "P48-CR-PRICE", "price_variance", Math.Abs(variance), PlantId: plantId, ItemId: itemId, Inputs: inputs));
                }
            }
        }

        var plan = await engine.PrepareAsync(context, new PostingRequest(MaterialShortfalls.SettleRule, businessDate, occurredAt, lines), cancellationToken).ConfigureAwait(false);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "MaterialShortfallSettled",
                1,
                aggregate,
                aggregateId,
                eventVersion,
                JsonSerializer.Serialize(new
                {
                    sourceType,
                    sourceId,
                    shares = settlements.SelectMany(x => x.Shares.Select(s => new
                    {
                        shortfallId = s.ShortfallId,
                        lotId = x.Reservation.LotId,
                        quantity = s.Qty.ToString(CultureInfo.InvariantCulture),
                        stdValue = s.StdValue.ToString(CultureInfo.InvariantCulture),
                        actualValue = s.ActualValue.ToString(CultureInfo.InvariantCulture),
                    })),
                }),
                Publish: true,
                OccurredAt: occurredAt,
                BusinessDate: businessDate),
            cancellationToken).ConfigureAwait(false);
        var dates = new MovementDates(occurredAt, businessDate, plan.PostingDate);
        foreach (var settlement in settlements)
        {
            await MaterialShortfalls.WriteAsync(context, inventory, settlement, sourceType, sourceId, eventId, dates, cancellationToken).ConfigureAwait(false);
        }

        return (await engine.WriteAsync(context, plan, eventId, cancellationToken).ConfigureAwait(false)).JournalId;
    }
}
