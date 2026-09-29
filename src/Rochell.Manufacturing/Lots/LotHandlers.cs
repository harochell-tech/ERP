using System.Globalization;
using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.Inventory;
using Rochell.Manufacturing.Runs;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Manufacturing.Lots;

internal static class Lots
{
    public const string ScrapRule = "P-12";

    public sealed record Lot(Guid LotId, string LotCode, Guid ItemId, Guid PlantId, string Status, DateTime ReleasableAt, long Version);

    /// <summary>Locks the finished-goods lot, checks the plant and the expected version.</summary>
    public static async Task<Lot> LockAsync(CommandContext context, Guid plantId, Guid lotId, long? expectedVersion, CancellationToken cancellationToken)
    {
        var lot = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT f.lot_id, l.lot_code, l.item_id, r.plant_id, f.status, f.releasable_at, f.version
            FROM mfg.fg_lot f JOIN inv.lot l ON l.lot_id = f.lot_id JOIN mfg.production_run r ON r.run_id = f.run_id
            WHERE f.company_id = @c AND f.lot_id = @l
            FOR UPDATE OF f
            """,
            r => new Lot(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetGuid(3), r.GetString(4), r.GetFieldValue<DateTime>(5), r.GetInt64(6)),
            cancellationToken,
            ("c", context.CompanyId),
            ("l", lotId)).ConfigureAwait(false)
            ?? throw new DomainException(ManufacturingErrors.NotFound, "The finished-goods lot does not exist.");
        if (lot.PlantId != plantId)
        {
            throw new DomainException(ManufacturingErrors.PlantMismatch, "The lot belongs to another plant.");
        }

        return expectedVersion is null || lot.Version == expectedVersion
            ? lot
            : throw new DomainException(ManufacturingErrors.VersionConflict, $"The lot changed (version {lot.Version}, expected {expectedVersion}); reload and retry.");
    }

    public static string Reason(string? reason)
    {
        var trimmed = (reason ?? string.Empty).Trim();
        return trimmed.Length is > 0 and <= 500 ? trimmed : throw new DomainException(ManufacturingErrors.ReasonRequired, "A reason of 1 to 500 characters is required.");
    }

    /// <summary>Changes the lot's status (+1 version) with its event and state history.</summary>
    public static async Task<Guid> TransitionAsync(
        CommandContext context, Lot lot, string to, string eventType, object payload, string commandType, CancellationToken cancellationToken, string? reason = null, string? blockReason = null,
        (Guid By, DateTime At, Guid To)? release = null)
    {
        var eventId = await context.AppendEventAsync(
            new EventDraft(eventType, 1, Runs.Runs.LotAggregate, lot.LotId, await MfgSql.NextEventVersionAsync(context, Runs.Runs.LotAggregate, lot.LotId, cancellationToken).ConfigureAwait(false),
                JsonSerializer.Serialize(payload), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            UPDATE mfg.fg_lot SET status = @s, block_reason = @b, version = version + 1,
                   released_by = coalesce(CAST(@by AS uuid), released_by), released_at = coalesce(CAST(@at AS timestamptz), released_at), released_to = coalesce(CAST(@to AS uuid), released_to)
            WHERE lot_id = @l
            """,
            cancellationToken,
            ("s", to),
            ("b", blockReason),
            ("by", release?.By),
            ("at", release?.At),
            ("to", release?.To),
            ("l", lot.LotId)).ConfigureAwait(false);
        await context.AppendStateAsync(Runs.Runs.LotAggregate, lot.LotId, "DOCUMENT", lot.Status, to, commandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        return eventId;
    }
}

[RequiresPermission("fg_lot:release")]
public sealed class ReleaseLotHandler : ICommandHandler<ReleaseLot>
{
    private readonly InventoryLedger _inventory = new();

    public string CommandType => "Manufacturing.ReleaseLot";

    public async Task<string> HandleAsync(ReleaseLot command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var lot = await Lots.LockAsync(context, command.PlantId, command.LotId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (lot.Status != "CURING")
        {
            throw new DomainException(ManufacturingErrors.InvalidState, $"The lot is {lot.Status}; only a CURING lot is released.");
        }

        var now = context.Clock.UtcNow;
        if (now < lot.ReleasableAt)
        {
            throw new DomainException(ManufacturingErrors.CuringNotDone, $"The lot's minimum curing ends at {lot.ReleasableAt:yyyy-MM-dd HH:mm} UTC.");
        }

        if (await MfgSql.ScalarAsync<Guid?>(
                context, "SELECT location_id FROM md.location WHERE company_id = @c AND location_id = @l AND plant_id = @p AND NOT is_transit AND NOT is_curing", cancellationToken,
                ("c", context.CompanyId), ("l", command.ToLocationId), ("p", lot.PlantId)).ConfigureAwait(false) is null)
        {
            throw new DomainException(ManufacturingErrors.LocationInvalid, "A lot is released to a stock location of its plant.");
        }

        var curing = (await MfgSql.ScalarAsync<Guid?>(context, "SELECT location_id FROM md.location WHERE plant_id = @p AND is_curing", cancellationToken, ("p", lot.PlantId)).ConfigureAwait(false))!.Value;
        var quantity = await _inventory.LockStockAsync(context, curing, lot.ItemId, lot.LotId, cancellationToken).ConfigureAwait(false);
        await _inventory.LockStockAsync(context, command.ToLocationId, lot.ItemId, lot.LotId, cancellationToken).ConfigureAwait(false);
        if (quantity <= 0m)
        {
            throw new DomainException(ManufacturingErrors.InvalidState, "The lot has no stock in CURADO.");
        }

        var releaser = await MfgSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var eventId = await Lots.TransitionAsync(
            context, lot, "RELEASED", "LotReleased", new { lotId = lot.LotId, lotCode = lot.LotCode, toLocationId = command.ToLocationId, quantity = Runs.Runs.Qty(quantity) }, CommandType, cancellationToken,
            release: (releaser, now, command.ToLocationId)).ConfigureAwait(false);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE mfg.rack SET status = 'RELEASED' WHERE lot_id = @l AND status = 'CURING'", cancellationToken, ("l", lot.LotId)).ConfigureAwait(false);

        // E-MFG1-04-2: quantity only, the area's value does not change and there is no journal.
        var today = MfgSql.Today(context);
        var dates = new MovementDates(now, today, today);
        var source = new MovementSource(eventId, "LOT_RELEASE", lot.LotId);
        await _inventory.PostMovementAsync(context, MovementTypes.Transfer, curing, lot.ItemId, lot.LotId, -quantity, 0m, null, source, dates, cancellationToken).ConfigureAwait(false);
        await _inventory.PostMovementAsync(context, MovementTypes.Transfer, command.ToLocationId, lot.ItemId, lot.LotId, quantity, 0m, null, source, dates, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { lotId = lot.LotId, status = "RELEASED", version = lot.Version + 1, quantity = Runs.Runs.Qty(quantity) });
    }
}

[RequiresPermission("fg_lot:release")]
public sealed class BlockLotHandler : ICommandHandler<BlockLot>
{
    public string CommandType => "Manufacturing.BlockLot";

    public async Task<string> HandleAsync(BlockLot command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = Lots.Reason(command.Reason);
        var lot = await Lots.LockAsync(context, command.PlantId, command.LotId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (lot.Status != "CURING")
        {
            throw new DomainException(ManufacturingErrors.InvalidState, $"The lot is {lot.Status}; only a CURING lot is blocked.");
        }

        await Lots.TransitionAsync(context, lot, "BLOCKED", "LotBlocked", new { lotId = lot.LotId, reason }, CommandType, cancellationToken, reason, reason).ConfigureAwait(false);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE mfg.rack SET status = 'BLOCKED' WHERE lot_id = @l AND status = 'CURING'", cancellationToken, ("l", lot.LotId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { lotId = lot.LotId, status = "BLOCKED", version = lot.Version + 1 });
    }
}

[RequiresPermission("fg_lot:release")]
public sealed class UnblockLotHandler : ICommandHandler<UnblockLot>
{
    public string CommandType => "Manufacturing.UnblockLot";

    public async Task<string> HandleAsync(UnblockLot command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = Lots.Reason(command.Reason);
        var lot = await Lots.LockAsync(context, command.PlantId, command.LotId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (lot.Status != "BLOCKED")
        {
            throw new DomainException(ManufacturingErrors.InvalidState, $"The lot is {lot.Status}; only a BLOCKED lot is unblocked.");
        }

        await Lots.TransitionAsync(context, lot, "CURING", "LotUnblocked", new { lotId = lot.LotId, reason }, CommandType, cancellationToken, reason).ConfigureAwait(false);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE mfg.rack SET status = 'CURING' WHERE lot_id = @l AND status = 'BLOCKED'", cancellationToken, ("l", lot.LotId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { lotId = lot.LotId, status = "CURING", version = lot.Version + 1 });
    }
}

[RequiresPermission("fg_lot:scrap", StepUp = true)]
public sealed class ScrapLotHandler : ICommandHandler<ScrapLot>
{
    private readonly PostingEngine _engine = new();
    private readonly InventoryLedger _inventory = new();

    public string CommandType => "Manufacturing.ScrapLot";

    private sealed record Location(bool IsCuring);

    public async Task<string> HandleAsync(ScrapLot command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = Lots.Reason(command.Reason);
        var quantity = MfgSql.Quantity(command.Quantity, "The scrapped quantity");
        var lot = await Lots.LockAsync(context, command.PlantId, command.LotId, null, cancellationToken).ConfigureAwait(false);
        if (lot.Status is not ("CURING" or "BLOCKED" or "RELEASED"))
        {
            throw new DomainException(ManufacturingErrors.InvalidState, $"The lot is {lot.Status}.");
        }

        var location = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT is_curing FROM md.location WHERE company_id = @c AND location_id = @l AND plant_id = @p AND NOT is_transit",
            r => new Location(r.GetBoolean(0)),
            cancellationToken,
            ("c", context.CompanyId),
            ("l", command.LocationId),
            ("p", lot.PlantId)).ConfigureAwait(false)
            ?? throw new DomainException(ManufacturingErrors.LocationInvalid, "Scrap is recorded from a (non-transit) location of the lot's plant.");
        var point = location.IsCuring ? "CURING" : "YARD";
        var reservation = await _inventory.ReserveIssueAsync(context, command.LocationId, lot.ItemId, lot.LotId, quantity, cancellationToken).ConfigureAwait(false);
        if (reservation.Value <= 0m)
        {
            throw new DomainException(ManufacturingErrors.QuantityInvalid, "The scrapped quantity has no value at 2 decimals.");
        }

        var today = MfgSql.Today(context);
        var occurredAt = context.Clock.UtcNow;
        var valueEntryId = context.Ids.NewId();
        var inputs = new Dictionary<string, string>
        {
            ["quantity"] = Runs.Runs.Qty(quantity),
            ["lot_code"] = lot.LotCode,
            ["point"] = point,
            ["reason"] = reason,
        };
        var plan = await _engine.PrepareAsync(
            context,
            new PostingRequest(
                Lots.ScrapRule,
                today,
                occurredAt,
                [
                    new PostingLineInput("P12-DR-SCRAP", "scrap_value", reservation.Value, PlantId: lot.PlantId, Inputs: inputs),
                    new PostingLineInput("P12-CR-FG", "scrap_value", reservation.Value, PlantId: lot.PlantId, ItemId: lot.ItemId, SubledgerRef: valueEntryId, InvValueEntryId: valueEntryId, Inputs: inputs),
                ]),
            cancellationToken).ConfigureAwait(false);
        if (plan.PostingDate != today)
        {
            throw new DomainException(ManufacturingErrors.PeriodClosed, $"INV-MOV is closed for {today:yyyy-MM-dd}.");
        }

        var scrapper = await MfgSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ScrapRecorded",
                1,
                Runs.Runs.LotAggregate,
                lot.LotId,
                await MfgSql.NextEventVersionAsync(context, Runs.Runs.LotAggregate, lot.LotId, cancellationToken).ConfigureAwait(false),
                JsonSerializer.Serialize(new { lotId = lot.LotId, locationId = command.LocationId, point, quantity = Runs.Runs.Qty(quantity), value = Runs.Runs.Money(reservation.Value), reason }),
                Publish: true,
                OccurredAt: occurredAt,
                BusinessDate: today),
            cancellationToken).ConfigureAwait(false);
        await _inventory.WriteIssueAsync(
            context, reservation, valueEntryId, new MovementSource(eventId, "LOT_SCRAP", lot.LotId), new MovementDates(occurredAt, today, plan.PostingDate), cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO mfg.lot_scrap (scrap_id, company_id, lot_id, location_id, point, qty, value, reason, event_id, scrapped_by)
            VALUES (@id, @c, @l, @loc, @point, @q, @v, @r, @e, @by)
            """,
            cancellationToken,
            ("id", context.ResultRef),
            ("c", context.CompanyId),
            ("l", lot.LotId),
            ("loc", command.LocationId),
            ("point", point),
            ("q", quantity),
            ("v", reservation.Value),
            ("r", reason),
            ("e", eventId),
            ("by", scrapper)).ConfigureAwait(false);

        var left = await MfgSql.ScalarAsync<decimal?>(context, "SELECT sum(quantity) FROM inv.inv_stock_balance WHERE lot_id = @l", cancellationToken, ("l", lot.LotId)).ConfigureAwait(false) ?? 0m;
        var status = lot.Status;
        if (left == 0m)
        {
            await Lots.TransitionAsync(context, lot, "SCRAPPED", "LotScrapped", new { lotId = lot.LotId, reason }, CommandType, cancellationToken, reason).ConfigureAwait(false);
            await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE mfg.rack SET status = 'SCRAPPED' WHERE lot_id = @l", cancellationToken, ("l", lot.LotId)).ConfigureAwait(false);
            status = "SCRAPPED";
        }

        var journal = await _engine.WriteAsync(context, plan, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            scrapId = context.ResultRef,
            lotId = lot.LotId,
            point,
            quantity = Runs.Runs.Qty(quantity),
            value = reservation.Value.ToString("0.00", CultureInfo.InvariantCulture),
            lotStatus = status,
            journalId = journal.JournalId,
        });
    }
}
