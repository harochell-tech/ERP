using System.Globalization;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.FixedAssets.Cards;

/// <summary>
/// The card side of the documents Procurement posts (E-AF-3, E-AF-4, E-AF1-01-1): a card per fixed-asset line when an invoice is
/// posted, cancelled with its reversal, and the cost an import settlement adds or takes back. Called inside the posting command's
/// transaction; every card change has its own event (caused by the document's) and, when its status changes, its history row.
/// </summary>
public static class FixedAssetCards
{
    public const string Aggregate = "FixedAsset";
    public const string AwaitingService = "AWAITING_SERVICE";
    public const string InService = "IN_SERVICE";
    public const string Disposed = "DISPOSED";
    public const string Cancelled = "CANCELLED";

    /// <summary>The invoice's fixed-asset lines without a live card: category of 606 type 04 whose account is an asset.</summary>
    private const string AssetLinesSql = """
        SELECT l.si_line_id, left(l.description, 200), l.qty,
               -- E-X1-02-4: a category-1 asset's ITBIS is part of its cost.
               l.net_amount + coalesce((SELECT sum(d.amount) FROM tax.tax_determination_line d
                                        WHERE d.determination_id = si.tax_determination_id AND d.subject_line_id = l.si_line_id AND d.effect = 'NON_RECOVERABLE_INPUT'), 0),
               l.expense_category_id, si.plant_id, si.doc_date
        FROM pur.supplier_invoice_line l
        JOIN pur.supplier_invoice si ON si.si_id = l.si_id
        JOIN pur.expense_category c ON c.expense_category_id = l.expense_category_id
        JOIN fin.account a ON a.account_id = c.account_id
        WHERE l.si_id = @s AND c.goods_type_606 = '04' AND a.account_class = 'ASSET'
          AND NOT EXISTS (SELECT 1 FROM fa.asset x WHERE x.si_line_id = l.si_line_id AND x.status <> 'CANCELLED')
        ORDER BY l.line_no
        """;

    private sealed record AssetLine(Guid LineId, string Description, decimal Quantity, decimal Cost, Guid CategoryId, Guid PlantId, DateOnly DocDate);

    internal sealed record CardRow(
        Guid Id, string Number, Guid CategoryId, string Status, Guid PlantId, DateOnly AcquiredOn, DateOnly? InServiceOn, decimal? ResidualPct, int MonthsDepreciated,
        decimal Cost, decimal Accumulated, long Version);

    /// <summary>E-AF-3, E-AF1-02-1/2: one AWAITING_SERVICE card per fixed-asset line of a posted invoice; returns the cards created.</summary>
    public static async Task<IReadOnlyList<Guid>> CreateForInvoiceAsync(CommandContext context, Guid siId, Guid causationEventId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            AssetLinesSql,
            r => new AssetLine(r.GetGuid(0), r.GetString(1), r.GetDecimal(2), r.GetDecimal(3), r.GetGuid(4), r.GetGuid(5), r.Date(6)),
            cancellationToken,
            ("s", siId)).ConfigureAwait(false);
        if (lines.Count == 0)
        {
            return [];
        }

        var creator = await SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var created = new List<Guid>(lines.Count);
        foreach (var line in lines)
        {
            created.Add(await CreateAsync(context, line, creator, siId, causationEventId, cancellationToken).ConfigureAwait(false));
        }

        return created;
    }

    /// <summary>
    /// E-AF1-02-8: cards for the fixed-asset lines of invoices posted before AF-1, each with the cost its posted settlements added.
    /// Returns the cards created; running it again creates nothing.
    /// </summary>
    internal static async Task<IReadOnlyList<Guid>> CreateForPostedInvoicesAsync(CommandContext context, Guid causationEventId, CancellationToken cancellationToken)
    {
        var invoices = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT DISTINCT si.si_id, si.doc_date
            FROM pur.supplier_invoice si
            JOIN pur.supplier_invoice_line l ON l.si_id = si.si_id
            JOIN pur.expense_category c ON c.expense_category_id = l.expense_category_id
            JOIN fin.account a ON a.account_id = c.account_id
            WHERE si.company_id = @c AND si.accounting_status = 'POSTED' AND c.goods_type_606 = '04' AND a.account_class = 'ASSET'
              AND NOT EXISTS (SELECT 1 FROM fa.asset x WHERE x.si_line_id = l.si_line_id AND x.status <> 'CANCELLED')
            ORDER BY si.doc_date, si.si_id
            """,
            r => r.GetGuid(0),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        var created = new List<Guid>();
        foreach (var siId in invoices)
        {
            var cards = await CreateForInvoiceAsync(context, siId, causationEventId, cancellationToken).ConfigureAwait(false);
            foreach (var cardId in cards)
            {
                var settlements = await Reading.ListAsync(
                    context.Connection,
                    context.Transaction,
                    """
                    SELECT s.settlement_id, s.settlement_date, al.added_cost
                    FROM fa.asset x
                    JOIN pur.import_settlement_allocation al ON al.target_id = x.si_line_id
                    JOIN pur.import_settlement s ON s.settlement_id = al.settlement_id
                    WHERE x.asset_id = @a AND s.status = 'POSTED' AND al.added_cost > 0
                    ORDER BY s.settlement_date, s.settlement_no
                    """,
                    r => (Id: r.GetGuid(0), Date: r.Date(1), Added: r.GetDecimal(2)),
                    cancellationToken,
                    ("a", cardId)).ConfigureAwait(false);
                foreach (var settlement in settlements)
                {
                    var card = await LockAsync(context, cardId, null, cancellationToken).ConfigureAwait(false);
                    await ChangeCostAsync(context, card, settlement.Added, "COST_ADDED", settlement.Date, settlement.Id, causationEventId, cancellationToken).ConfigureAwait(false);
                }
            }

            created.AddRange(cards);
        }

        return created;
    }

    /// <summary>
    /// E-AF1-01-5, E-AF1-02-4: the invoice's reversal cancels its live cards — refused when one was depreciated or disposed of.
    /// </summary>
    public static async Task CancelForInvoiceAsync(CommandContext context, Guid siId, Guid causationEventId, string commandType, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var cards = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            $"""
            SELECT {CardColumns} FROM fa.asset x JOIN pur.supplier_invoice_line l ON l.si_line_id = x.si_line_id
            WHERE l.si_id = @s AND x.status <> 'CANCELLED' ORDER BY x.asset_no FOR UPDATE OF x
            """,
            ReadCard,
            cancellationToken,
            ("s", siId)).ConfigureAwait(false);
        foreach (var card in cards)
        {
            if (card.Status == Disposed)
            {
                throw new DomainException(FixedAssetErrors.AssetDisposed, $"{card.Number} was disposed of; its invoice is no longer reversed.");
            }

            if (card.Accumulated != 0 || card.MonthsDepreciated != 0)
            {
                throw new DomainException(FixedAssetErrors.AssetDepreciated, $"{card.Number} has depreciation; dispose of it instead of reversing its invoice (E-AF1-02-4).");
            }
        }

        foreach (var card in cards)
        {
            var version = card.Version + 1;
            var eventId = await context.AppendEventAsync(
                new EventDraft(
                    "FixedAssetCancelled", 1, Aggregate, card.Id, version, JsonSerializer.Serialize(new { assetId = card.Id, assetNo = card.Number, siId, reason }),
                    Publish: true, CausationId: causationEventId),
                cancellationToken).ConfigureAwait(false);
            await Sql.ExecuteAsync(
                context.Connection, context.Transaction, "UPDATE fa.asset SET status = 'CANCELLED', version = @v WHERE asset_id = @a",
                cancellationToken, ("v", version), ("a", card.Id)).ConfigureAwait(false);
            await MovementAsync(context, card.Id, "CANCELLED", BusinessDate(context), null, null, siId, eventId, cancellationToken).ConfigureAwait(false);
            await context.AppendStateAsync(Aggregate, card.Id, "DOCUMENT", card.Status, Cancelled, commandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// E-AF-4, E-AF1-02-3/10: before a settlement posts — none of its goods lines has a disposed card, and none of its expense lines
    /// (whose cost it moves out of their account) is a card.
    /// </summary>
    public static async Task RequireSettleableAsync(CommandContext context, IEnumerable<Guid> goodsLineIds, IEnumerable<Guid> expenseLineIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var disposed = await Reading.ListAsync(
            context.Connection, context.Transaction,
            "SELECT asset_no FROM fa.asset WHERE si_line_id = ANY(@l) AND status = 'DISPOSED' ORDER BY asset_no",
            r => r.GetString(0), cancellationToken, ("l", goodsLineIds.ToArray())).ConfigureAwait(false);
        if (disposed.Count > 0)
        {
            throw new DomainException(FixedAssetErrors.AssetDisposed, $"{string.Join(", ", disposed)} was disposed of: the settlement has nowhere to add its cost (E-AF1-02-3).");
        }

        var moved = await Reading.ListAsync(
            context.Connection, context.Transaction,
            "SELECT asset_no FROM fa.asset WHERE si_line_id = ANY(@l) AND status <> 'CANCELLED' ORDER BY asset_no",
            r => r.GetString(0), cancellationToken, ("l", expenseLineIds.ToArray())).ConfigureAwait(false);
        if (moved.Count > 0)
        {
            throw new DomainException(
                FixedAssetErrors.AssetInSettlement,
                $"{string.Join(", ", moved)} is a fixed asset: its invoice is settled as goods, not as a cost of other goods (E-AF1-02-10).");
        }
    }

    /// <summary>E-AF-4: a posted settlement adds each goods line's share to the line's live card.</summary>
    public static async Task AddSettlementCostAsync(
        CommandContext context, Guid settlementId, DateOnly settlementDate, IReadOnlyDictionary<Guid, decimal> addedByLine, Guid causationEventId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(addedByLine);
        var cards = await Reading.ListAsync(
            context.Connection, context.Transaction,
            $"SELECT {CardColumns}, x.si_line_id FROM fa.asset x WHERE x.si_line_id = ANY(@l) AND x.status <> 'CANCELLED' ORDER BY x.asset_no FOR UPDATE",
            r => (Card: ReadCard(r), LineId: r.GetGuid(CardColumnCount)), cancellationToken, ("l", addedByLine.Keys.ToArray())).ConfigureAwait(false);
        foreach (var (card, lineId) in cards)
        {
            if (card.Status == Disposed)
            {
                throw new DomainException(FixedAssetErrors.AssetDisposed, $"{card.Number} was disposed of: the settlement has nowhere to add its cost (E-AF1-02-3).");
            }

            var added = addedByLine[lineId];
            if (added > 0)
            {
                await ChangeCostAsync(context, card, added, "COST_ADDED", settlementDate, settlementId, causationEventId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>E-AF-4, E-AF1-02-4: a settlement's reversal takes back what it added; a depreciating card spreads the change over its remaining life.</summary>
    public static async Task RemoveSettlementCostAsync(CommandContext context, Guid settlementId, Guid causationEventId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var added = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT m.asset_id, sum(m.amount) FILTER (WHERE m.kind = 'COST_ADDED') - coalesce(sum(m.amount) FILTER (WHERE m.kind = 'COST_REMOVED'), 0)
            FROM fa.asset_movement m WHERE m.source_ref = @s AND m.kind IN ('COST_ADDED', 'COST_REMOVED')
            GROUP BY m.asset_id ORDER BY m.asset_id
            """,
            r => (AssetId: r.GetGuid(0), Amount: r.GetDecimal(1)),
            cancellationToken,
            ("s", settlementId)).ConfigureAwait(false);
        foreach (var (assetId, amount) in added.Where(a => a.Amount > 0))
        {
            var card = await LockAsync(context, assetId, null, cancellationToken).ConfigureAwait(false);
            if (card.Status == Cancelled)
            {
                continue; // its invoice was reversed: the card carries nothing any more
            }

            if (card.Status == Disposed)
            {
                throw new DomainException(FixedAssetErrors.AssetDisposed, $"{card.Number} was disposed of: the settlement's cost can no longer be taken back (E-AF1-02-3).");
            }

            var cost = card.Cost - amount;
            if (cost - Residual(cost, card.ResidualPct ?? 0) < card.Accumulated)
            {
                throw new DomainException(
                    FixedAssetErrors.CostBelowDepreciation,
                    $"{card.Number} has depreciated {Text(card.Accumulated)}: without the settlement's {Text(amount)} its cost would not cover it.");
            }

            await ChangeCostAsync(context, card, -amount, "COST_REMOVED", BusinessDate(context), settlementId, causationEventId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The residual value: cost × residual % at 2 decimals.</summary>
    public static decimal Residual(decimal cost, decimal residualPct) => decimal.Round(cost * residualPct / 100m, 2, MidpointRounding.AwayFromZero);

    internal const string CardColumns =
        "x.asset_id, x.asset_no, x.expense_category_id, x.status, x.plant_id, x.acquired_on, x.in_service_on, x.residual_pct, x.months_depreciated, x.cost, x.accumulated, x.version";

    private const int CardColumnCount = 12;

    internal static CardRow ReadCard(System.Data.Common.DbDataReader r)
        => new(
            r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetGuid(4), r.Date(5), r.IsDBNull(6) ? null : r.Date(6), r.NullableDecimal(7), r.GetInt32(8),
            r.GetDecimal(9), r.GetDecimal(10), r.GetInt64(11));

    internal static async Task<CardRow> LockAsync(CommandContext context, Guid assetId, long? expectedVersion, CancellationToken cancellationToken)
    {
        var card = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, $"SELECT {CardColumns} FROM fa.asset x WHERE x.company_id = @c AND x.asset_id = @a FOR UPDATE", ReadCard, cancellationToken,
            ("c", context.CompanyId), ("a", assetId)).ConfigureAwait(false)
            ?? throw new DomainException(FixedAssetErrors.AssetNotFound, "The fixed asset does not exist.");
        return expectedVersion is null || expectedVersion == card.Version
            ? card
            : throw new DomainException(FixedAssetErrors.VersionConflict, $"The fixed asset changed (version {card.Version}, expected {expectedVersion}); reload and retry.");
    }

    internal static async Task MovementAsync(
        CommandContext context, Guid assetId, string kind, DateOnly date, decimal? amount, Guid? plantId, Guid? sourceRef, Guid eventId, CancellationToken cancellationToken)
        => await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO fa.asset_movement (movement_id, company_id, asset_id, kind, movement_date, amount, plant_id, source_ref, event_id)
            VALUES (@id, @c, @a, @k, @d, @amount, @p, @src, @e)
            """,
            cancellationToken,
            ("id", context.Ids.NewId()),
            ("c", context.CompanyId),
            ("a", assetId),
            ("k", kind),
            ("d", date),
            ("amount", (object?)amount ?? DBNull.Value),
            ("p", (object?)plantId ?? DBNull.Value),
            ("src", (object?)sourceRef ?? DBNull.Value),
            ("e", eventId)).ConfigureAwait(false);

    internal static async Task<Guid> SessionUserAsync(CommandContext context, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(context.Connection, context.Transaction, "SELECT user_id FROM iam.session WHERE session_id = @s", ("s", context.SessionId));
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    internal static DateOnly BusinessDate(CommandContext context) => Rochell.Platform.Time.BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);

    internal static string Text(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static async Task<Guid> CreateAsync(CommandContext context, AssetLine line, Guid creator, Guid siId, Guid causationEventId, CancellationToken cancellationToken)
    {
        var assetId = context.Ids.NewId();
        var number = await NextNumberAsync(context, line.DocDate.Year, cancellationToken).ConfigureAwait(false);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "FixedAssetRegistered",
                1,
                Aggregate,
                assetId,
                1,
                JsonSerializer.Serialize(new { assetId, assetNo = number, siId, siLineId = line.LineId, cost = Text(line.Cost), plantId = line.PlantId }),
                Publish: true,
                CausationId: causationEventId),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO fa.asset (asset_id, company_id, asset_no, expense_category_id, description, quantity, source_kind, si_line_id, plant_id, acquired_on, status,
                                  months_depreciated, cost, accumulated, created_by, version)
            VALUES (@id, @c, @no, @cat, @d, @q, 'INVOICE_LINE', @line, @p, @on, 'AWAITING_SERVICE', 0, @cost, 0, @by, 1)
            """,
            cancellationToken,
            ("id", assetId),
            ("c", context.CompanyId),
            ("no", number),
            ("cat", line.CategoryId),
            ("d", line.Description),
            ("q", line.Quantity),
            ("line", line.LineId),
            ("p", line.PlantId),
            ("on", line.DocDate),
            ("cost", line.Cost),
            ("by", creator)).ConfigureAwait(false);
        await MovementAsync(context, assetId, "ACQUISITION", line.DocDate, line.Cost, line.PlantId, siId, eventId, cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(Aggregate, assetId, "DOCUMENT", null, AwaitingService, "FixedAssets.RegisterFromInvoice", eventId, cancellationToken).ConfigureAwait(false);
        return assetId;
    }

    private static async Task ChangeCostAsync(
        CommandContext context, CardRow card, decimal delta, string kind, DateOnly date, Guid settlementId, Guid causationEventId, CancellationToken cancellationToken)
    {
        var version = card.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                kind == "COST_ADDED" ? "FixedAssetCostAdded" : "FixedAssetCostRemoved",
                1,
                Aggregate,
                card.Id,
                version,
                JsonSerializer.Serialize(new { assetId = card.Id, assetNo = card.Number, settlementId, amount = Text(Math.Abs(delta)), cost = Text(card.Cost + delta) }),
                Publish: true,
                CausationId: causationEventId),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE fa.asset SET cost = cost + @d, version = @v WHERE asset_id = @a",
            cancellationToken, ("d", delta), ("v", version), ("a", card.Id)).ConfigureAwait(false);
        await MovementAsync(context, card.Id, kind, date, Math.Abs(delta), null, settlementId, eventId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>E-AF1-01-6: AF-YYYY-NNNNNN per company and year, the next after the highest, under an advisory lock.</summary>
    internal static async Task<string> NextNumberAsync(CommandContext context, int year, CancellationToken cancellationToken)
    {
        var stem = string.Create(CultureInfo.InvariantCulture, $"AF-{year:D4}-");
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", cancellationToken,
            ("k", $"AF-no:{context.CompanyId}:{year}")).ConfigureAwait(false);
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT max(substring(asset_no FROM 9)::int) FROM fa.asset WHERE company_id = @c AND asset_no LIKE @stem || '%'",
            ("c", context.CompanyId),
            ("stem", stem));
        var last = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is int n ? n : 0;
        return stem + (last + 1).ToString("D6", CultureInfo.InvariantCulture);
    }
}
