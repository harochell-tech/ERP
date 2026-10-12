using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Inventory;

/// <summary>One shortfall's share of a settlement: the quantity it takes, its standard value and its real cost from the new lot.</summary>
public sealed record ShortfallShare(Guid ShortfallId, decimal Qty, decimal StdValue, decimal ActualValue, Guid ValueEntryId);

/// <summary>A settlement taken from a lot that just entered: the reserved issue and the shortfalls it closes, oldest first.</summary>
public sealed record ShortfallSettlement(IssueReservation Reservation, string LotCode, string MaterialCode, IReadOnlyList<ShortfallShare> Shares)
{
    public decimal StdValue => Shares.Sum(s => s.StdValue);

    public decimal ActualValue => Shares.Sum(s => s.ActualValue);
}

/// <summary>
/// VS3-02c (E-VS3-02c-1/2): raw material that production consumed without stock. The stock never goes below zero: the missing part is a
/// shortfall per location and material (inv.material_shortfall); the next receipt or opening of that material in that location
/// settles the oldest shortfalls first by issuing from the new lot. Callers post the journals (P-47 / P-48) with the Posting Engine;
/// this class keeps the inventory side. Every caller takes <see cref="LockAsync"/> first.
/// </summary>
public static class MaterialShortfalls
{
    public const string RecordRule = "P-47";
    public const string SettleRule = "P-48";
    public const string DocumentType = "MATERIAL_SHORTFALL";

    /// <summary>Serializes consumption and settlement of one material in one location.</summary>
    public static async Task LockAsync(CommandContext context, Guid locationId, Guid itemId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended('shortfall:' || @l || ':' || @i, 0))",
            cancellationToken,
            ("l", locationId.ToString()),
            ("i", itemId.ToString())).ConfigureAwait(false);
    }

    /// <summary>E-VS3-02c-3: the PRODUCTION policy's raw_material_negative_until (YYYYMMDD) in force on the date covers it.</summary>
    public static async Task<bool> AllowedAsync(CommandContext context, DateOnly date, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var until = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT (x.value #>> '{}')::numeric FROM acc.accounting_policy_version v JOIN acc.accounting_policy_parameter x ON x.policy_version_id = v.policy_version_id
            WHERE v.company_id = @c AND v.policy_code = 'PRODUCTION' AND v.status = 'ACTIVE' AND x.param_code = 'raw_material_negative_until'
              AND v.effective_from <= @d AND (v.effective_to IS NULL OR v.effective_to > @d)
            ORDER BY v.effective_from DESC LIMIT 1
            """,
            r => r.IsDBNull(0) ? (decimal?)null : r.GetDecimal(0),
            cancellationToken,
            ("c", context.CompanyId),
            ("d", date)).ConfigureAwait(false)).SingleOrDefault();
        return until is { } value
            && DateOnly.TryParseExact(((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture), "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var last)
            && date <= last;
    }

    /// <summary>A posting rule has an ACTIVE version on the date (a receipt never fails because P-48 is not active yet: it settles later).</summary>
    public static async Task<bool> RuleActiveAsync(CommandContext context, string ruleCode, DateOnly date, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT EXISTS (SELECT 1 FROM fin.posting_rule r JOIN fin.posting_rule_version v ON v.posting_rule_id = r.posting_rule_id
                           WHERE r.code = @r AND v.status = 'ACTIVE' AND v.effective_from <= @d AND (v.effective_to IS NULL OR v.effective_to > @d))
            """,
            ("r", ruleCode),
            ("d", date));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    /// <summary>Writes a shortfall (E-VS3-02c-1). Its journal (P-47) is the caller's.</summary>
    public static async Task RecordAsync(
        CommandContext context, Guid shortfallId, Guid plantId, Guid locationId, Guid itemId, string sourceType, Guid sourceId, Guid? runId, DateOnly businessDate,
        decimal qty, decimal stdPrice, decimal value, Guid eventId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO inv.material_shortfall (shortfall_id, company_id, plant_id, location_id, item_id, source_type, source_id, run_id, business_date, qty, std_price, value,
                                                event_id, status, created_at, version)
            VALUES (@id, @c, @p, @l, @i, @st, @s, @r, @d, @q, @price, @v, @e, 'OPEN', @at, 1)
            """,
            cancellationToken,
            ("id", shortfallId),
            ("c", context.CompanyId),
            ("p", plantId),
            ("l", locationId),
            ("i", itemId),
            ("st", sourceType),
            ("s", sourceId),
            ("r", runId),
            ("d", businessDate),
            ("q", qty),
            ("price", stdPrice),
            ("v", value),
            ("e", eventId),
            ("at", context.Clock.UtcNow)).ConfigureAwait(false);
    }

    /// <summary>
    /// Reserves the settlement of the open shortfalls of the lot's material in its location (oldest first) from at most
    /// <paramref name="available"/> of the lot, or null when nothing is open. The real cost is the lot's moving average; each share
    /// takes its standard value (the last share of a shortfall its remainder, so a settled shortfall adds up to its value).
    /// </summary>
    public static async Task<ShortfallSettlement?> ReserveAsync(
        CommandContext context, InventoryLedger inventory, Guid locationId, Guid itemId, Guid lotId, decimal available, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(inventory);
        var open = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT shortfall_id, qty - settled_qty, value - settled_value, std_price FROM inv.material_shortfall
            WHERE company_id = @c AND location_id = @l AND item_id = @i AND status = 'OPEN' ORDER BY created_at, shortfall_id
            FOR UPDATE
            """,
            r => (Id: r.GetGuid(0), Qty: r.GetDecimal(1), Value: r.GetDecimal(2), Price: r.GetDecimal(3)),
            cancellationToken,
            ("c", context.CompanyId),
            ("l", locationId),
            ("i", itemId)).ConfigureAwait(false);
        var total = Math.Min(available, open.Sum(o => o.Qty));
        if (total <= 0m)
        {
            return null;
        }

        var reservation = await inventory.ReserveIssueAsync(context, locationId, itemId, lotId, total, cancellationToken).ConfigureAwait(false);
        var shares = new List<ShortfallShare>();
        var left = total;
        var actualLeft = reservation.Value;
        foreach (var o in open)
        {
            if (left == 0m)
            {
                break;
            }

            var take = Math.Min(left, o.Qty);
            var std = take == o.Qty ? o.Value : decimal.Round(take * o.Price, 2, MidpointRounding.AwayFromZero);
            left -= take;
            var actual = left == 0m ? actualLeft : decimal.Round(reservation.Value * take / total, 2, MidpointRounding.AwayFromZero);
            actualLeft -= actual;
            shares.Add(new ShortfallShare(o.Id, take, std, actual, context.Ids.NewId()));
        }

        var names = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT l.lot_code, i.code FROM inv.lot l JOIN md.item i ON i.item_id = l.item_id WHERE l.lot_id = @l",
            r => (Lot: r.GetString(0), Item: r.GetString(1)),
            cancellationToken,
            ("l", lotId)).ConfigureAwait(false)).Single();
        return new ShortfallSettlement(reservation, names.Lot, names.Item, shares);
    }

    /// <summary>Writes a reserved settlement: one issue per share from the new lot (each with the value entry of its P-48 line) and the shares.</summary>
    public static async Task WriteAsync(
        CommandContext context, InventoryLedger inventory, ShortfallSettlement settlement, string sourceType, Guid sourceId, Guid eventId, MovementDates dates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(settlement);
        var r = settlement.Reservation;
        foreach (var share in settlement.Shares)
        {
            await inventory.WriteIssueAsync(
                context,
                r with { Quantity = share.Qty, Value = share.ActualValue },
                share.ActualValue == 0m ? null : share.ValueEntryId,
                new MovementSource(eventId, DocumentType, share.ShortfallId),
                dates,
                cancellationToken,
                MovementTypes.ProductionIssue).ConfigureAwait(false);
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO inv.material_shortfall_settlement (settlement_id, company_id, shortfall_id, qty, std_value, actual_value, lot_id, value_entry_id, source_type, source_id, event_id, created_at)
                VALUES (@id, @c, @s, @q, @std, @act, @lot, @ve, @st, @src, @e, @at)
                """,
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("s", share.ShortfallId),
                ("q", share.Qty),
                ("std", share.StdValue),
                ("act", share.ActualValue),
                ("lot", r.LotId),
                ("ve", share.ValueEntryId),
                ("st", sourceType),
                ("src", sourceId),
                ("e", eventId),
                ("at", context.Clock.UtcNow)).ConfigureAwait(false);
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                UPDATE inv.material_shortfall SET settled_qty = settled_qty + @q, settled_value = settled_value + @std,
                  status = CASE WHEN settled_qty + @q = qty THEN 'SETTLED' ELSE status END, version = version + 1
                WHERE shortfall_id = @s
                """,
                cancellationToken,
                ("q", share.Qty),
                ("std", share.StdValue),
                ("s", share.ShortfallId)).ConfigureAwait(false);
        }
    }
}
