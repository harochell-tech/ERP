using System.Globalization;
using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.Inventory;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Manufacturing.Runs;

[RequiresPermission("shift_summary:post")]
public sealed class PostShiftSummaryHandler : ICommandHandler<PostShiftSummary>
{
    private readonly PostingEngine _engine = new();
    private readonly InventoryLedger _inventory = new();

    public string CommandType => "Manufacturing.PostShiftSummary";

    private sealed record Context(string ShiftCode, TimeOnly StartsAt, TimeOnly EndsAt, decimal UnitsPerRack, int MinCuringHours, decimal UnitCost, decimal MaterialCost, string CollectorStatus);

    private sealed record StockLot(Guid LotId, string LotCode, decimal Quantity);

    public async Task<string> HandleAsync(PostShiftSummary command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var run = await Runs.LockAsync(context, command.PlantId, command.RunId, cancellationToken).ConfigureAwait(false);
        var summary = await Runs.LiveSummaryAsync(context, run.RunId, cancellationToken).ConfigureAwait(false);
        if (run.Status != "IN_PROGRESS" || summary is not { Status: "DRAFT" })
        {
            throw new DomainException(ManufacturingErrors.InvalidState, "The run has no DRAFT shift summary to post.");
        }

        if (summary.Version != command.ExpectedVersion)
        {
            throw new DomainException(ManufacturingErrors.VersionConflict, $"The summary changed (version {summary.Version}, expected {command.ExpectedVersion}); reload and retry.");
        }

        // MFG-2 (E-MFG2-8, E-MFG2-01-8): a draft whose consumption still is the recipe's placeholder waits for the batch plant or a typed one.
        var provenance = await ShiftSummaryWriter.ProvenanceAsync(context, run.RunId, cancellationToken).ConfigureAwait(false);
        if (provenance is { ConsumptionSource: "PENDING" })
        {
            throw new DomainException(
                ManufacturingErrors.ConsumptionPending, "The summary has no real consumption yet (sin consumo de dosificadora): wait for the batch plant or type it with a reason.");
        }

        var poster = await MfgSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if ((poster == summary.RecordedBy || poster == provenance?.EditedBy) && !await ControlWaiver.WaivedAsync(context, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(ManufacturingErrors.FourEyes, "A shift summary is posted by someone other than who recorded it (E-MFG1-03-5).");
        }

        var info = (await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT s.code, s.starts_at, s.ends_at, v.units_per_rack, v.min_curing_hours, c.unit_cost, c.material_cost, col.status
            FROM mfg.production_run r
            JOIN mfg.shift s ON s.shift_id = r.shift_id
            JOIN mfg.recipe_version v ON v.recipe_version_id = r.recipe_version_id
            JOIN md.standard_cost_version c ON c.cost_version_id = r.cost_version_id
            JOIN mfg.cost_collector col ON col.collector_id = r.collector_id
            WHERE r.run_id = @r
            """,
            r => new Context(r.GetString(0), r.GetFieldValue<TimeOnly>(1), r.GetFieldValue<TimeOnly>(2), r.GetDecimal(3), r.GetInt32(4), r.GetDecimal(5), r.GetDecimal(6), r.GetString(7)),
            cancellationToken,
            ("r", run.RunId)).ConfigureAwait(false))!;
        if (info.CollectorStatus != "OPEN")
        {
            throw new DomainException(ManufacturingErrors.CollectorSettled, "The run's cost collector is already settled.");
        }

        var lines = await Runs.LinesAsync(context, summary.SummaryId, cancellationToken).ConfigureAwait(false);

        // E-MFG1-03-4: lots FIFO by lot code in the chosen location; nothing is written when a material falls short.
        var issues = new List<(Runs.Line Line, StockLot Lot, decimal Qty, IssueReservation Reservation, Guid ValueEntryId)>();
        foreach (var line in lines.OrderBy(l => l.LocationId).ThenBy(l => l.MaterialItemId))
        {
            var lots = await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT b.lot_id, l.lot_code, b.quantity FROM inv.inv_stock_balance b JOIN inv.lot l ON l.lot_id = b.lot_id
                WHERE b.company_id = @c AND b.location_id = @loc AND b.item_id = @i AND b.quantity > 0
                ORDER BY l.lot_code
                FOR UPDATE OF b
                """,
                r => new StockLot(r.GetGuid(0), r.GetString(1), r.GetDecimal(2)),
                cancellationToken,
                ("c", context.CompanyId),
                ("loc", line.LocationId),
                ("i", line.MaterialItemId)).ConfigureAwait(false);
            if (lots.Sum(l => l.Quantity) < line.Qty)
            {
                throw new DomainException(InventoryErrors.InsufficientStock, $"Not enough {line.MaterialCode} in the location: {Runs.Qty(lots.Sum(l => l.Quantity))} available, {Runs.Qty(line.Qty)} consumed.");
            }

            var remaining = line.Qty;
            foreach (var lot in lots)
            {
                if (remaining == 0m)
                {
                    break;
                }

                var take = Math.Min(remaining, lot.Quantity);
                var reservation = await _inventory.ReserveIssueAsync(context, line.LocationId, line.MaterialItemId, lot.LotId, take, cancellationToken).ConfigureAwait(false);
                issues.Add((line, lot, take, reservation, context.Ids.NewId()));
                remaining -= take;
            }
        }

        if (issues.Any(i => i.Reservation.Value == 0m))
        {
            throw new DomainException(ManufacturingErrors.QuantityInvalid, "A consumed lot has no value; the moving average cost of the material is zero.");
        }

        var consumptionValue = issues.Sum(i => i.Reservation.Value);
        var standardValue = decimal.Round(summary.GoodUnits * info.UnitCost, 2, MidpointRounding.AwayFromZero);
        var materialStandard = decimal.Round(summary.GoodUnits * info.MaterialCost, 2, MidpointRounding.AwayFromZero);
        var conversionStandard = standardValue - materialStandard;
        var occurredAt = context.Clock.UtcNow;
        var receiptValueEntryId = context.Ids.NewId();

        var materialPlan = await _engine.PrepareAsync(
            context,
            new PostingRequest(
                Runs.MaterialRule,
                run.BusinessDate,
                occurredAt,
                issues.SelectMany(i =>
                {
                    var inputs = new Dictionary<string, string>
                    {
                        ["material_code"] = i.Line.MaterialCode,
                        ["run_no"] = run.RunNo,
                        ["quantity"] = Runs.Qty(i.Qty),
                        ["collector"] = run.CollectorId.ToString(),
                        ["lot_code"] = i.Lot.LotCode,
                    };
                    return new[]
                    {
                        new PostingLineInput("P08-DR-WIP", "consumption_value", i.Reservation.Value, PlantId: run.PlantId, ItemId: run.ItemId, SubledgerRef: run.CollectorId, Inputs: inputs),
                        new PostingLineInput("P08-CR-RM", "consumption_value", i.Reservation.Value, PlantId: run.PlantId, ItemId: i.Line.MaterialItemId, SubledgerRef: i.ValueEntryId, InvValueEntryId: i.ValueEntryId, Inputs: inputs),
                    };
                }).ToList()),
            cancellationToken).ConfigureAwait(false);
        if (materialPlan.PostingDate != run.BusinessDate)
        {
            throw new DomainException(ManufacturingErrors.PeriodClosed, $"INV-MOV is closed for {run.BusinessDate:yyyy-MM-dd}; production is posted only on its business date (E-MFG1-03-8).");
        }

        var lotCode = await LotCodeAsync(context, run, info.ShiftCode, cancellationToken).ConfigureAwait(false);
        var receiptInputs = new Dictionary<string, string>
        {
            ["good_units"] = Runs.Qty(summary.GoodUnits),
            ["run_no"] = run.RunNo,
            ["lot_code"] = lotCode,
            ["unit_cost"] = info.UnitCost.ToString(CultureInfo.InvariantCulture),
            ["material_cost"] = info.MaterialCost.ToString(CultureInfo.InvariantCulture),
        };
        var receiptLines = new List<PostingLineInput>
        {
            new("P10-DR-FG", "standard_value", standardValue, PlantId: run.PlantId, ItemId: run.ItemId, SubledgerRef: receiptValueEntryId, InvValueEntryId: receiptValueEntryId, Inputs: receiptInputs),
        };
        if (materialStandard > 0m)
        {
            receiptLines.Add(new("P10-CR-WIP", "material_standard", materialStandard, PlantId: run.PlantId, ItemId: run.ItemId, SubledgerRef: run.CollectorId, Inputs: receiptInputs));
        }

        if (conversionStandard > 0m)
        {
            receiptLines.Add(new("P10-CR-CONV", "conversion_standard", conversionStandard, PlantId: run.PlantId, Inputs: receiptInputs));
        }

        var receiptPlan = await _engine.PrepareAsync(context, new PostingRequest(Runs.ReceiptRule, run.BusinessDate, occurredAt, receiptLines), cancellationToken).ConfigureAwait(false);

        var version = summary.Version + 1;
        var materialEvent = await context.AppendEventAsync(
            new EventDraft(
                "MaterialConsumed",
                1,
                Runs.SummaryAggregate,
                summary.SummaryId,
                version,
                JsonSerializer.Serialize(new
                {
                    summaryId = summary.SummaryId,
                    runId = run.RunId,
                    collectorId = run.CollectorId,
                    total = Runs.Money(consumptionValue),
                    issues = issues.Select(i => new { materialItemId = i.Line.MaterialItemId, lotId = i.Lot.LotId, quantity = Runs.Qty(i.Qty), value = Runs.Money(i.Reservation.Value) }),
                }),
                Publish: true,
                OccurredAt: occurredAt,
                BusinessDate: run.BusinessDate),
            cancellationToken).ConfigureAwait(false);
        var receiptEvent = await context.AppendEventAsync(
            new EventDraft(
                "ProductionReceived",
                1,
                Runs.SummaryAggregate,
                summary.SummaryId,
                version + 1,
                JsonSerializer.Serialize(new
                {
                    summaryId = summary.SummaryId,
                    runId = run.RunId,
                    lotCode,
                    goodUnits = Runs.Qty(summary.GoodUnits),
                    standardValue = Runs.Money(standardValue),
                    materialStandard = Runs.Money(materialStandard),
                    conversionStandard = Runs.Money(conversionStandard),
                }),
                Publish: true,
                OccurredAt: occurredAt,
                BusinessDate: run.BusinessDate),
            cancellationToken).ConfigureAwait(false);

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE mfg.shift_summary SET status = 'POSTED', posted_by = @by, material_event_id = @me, receipt_event_id = @re, version = @v WHERE summary_id = @s",
            cancellationToken,
            ("by", poster),
            ("me", materialEvent),
            ("re", receiptEvent),
            ("v", version),
            ("s", summary.SummaryId)).ConfigureAwait(false);
        await context.AppendStateAsync(Runs.SummaryAggregate, summary.SummaryId, "DOCUMENT", "DRAFT", "POSTED", CommandType, materialEvent, cancellationToken).ConfigureAwait(false);
        await Runs.SetRunStatusAsync(context, run, "COMPLETED", CommandType, receiptEvent, cancellationToken).ConfigureAwait(false);

        var dates = new MovementDates(occurredAt, run.BusinessDate, materialPlan.PostingDate);
        foreach (var issue in issues)
        {
            await _inventory.WriteIssueAsync(
                context, issue.Reservation, issue.ValueEntryId, new MovementSource(materialEvent, "SHIFT_SUMMARY", summary.SummaryId), dates, cancellationToken, MovementTypes.ProductionIssue).ConfigureAwait(false);
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO mfg.consumption_issue (summary_id, company_id, material_item_id, lot_id, qty, value, value_entry_id)
                VALUES (@s, @c, @m, @l, @q, @v, @ve)
                """,
                cancellationToken,
                ("s", summary.SummaryId),
                ("c", context.CompanyId),
                ("m", issue.Line.MaterialItemId),
                ("l", issue.Lot.LotId),
                ("q", issue.Qty),
                ("v", issue.Reservation.Value),
                ("ve", issue.ValueEntryId)).ConfigureAwait(false);
        }

        var curing = await CuringLocationAsync(context, run.PlantId, cancellationToken).ConfigureAwait(false);
        var lotId = await _inventory.CreateLotAsync(context, run.ItemId, null, null, receiptEvent, run.BusinessDate, cancellationToken, lotCode).ConfigureAwait(false);
        await _inventory.ReceiveAsync(
            context,
            new ReceiptMovement(curing, run.ItemId, lotId, summary.GoodUnits, standardValue, receiptValueEntryId),
            new MovementSource(receiptEvent, "SHIFT_SUMMARY", summary.SummaryId),
            new MovementDates(occurredAt, run.BusinessDate, receiptPlan.PostingDate),
            cancellationToken,
            MovementTypes.ProductionReceipt).ConfigureAwait(false);

        // E-MFG1-03-7: curing starts when the shift ends (the next day for a night shift).
        var endDate = info.EndsAt <= info.StartsAt ? run.BusinessDate.AddDays(1) : run.BusinessDate;
        var curingFrom = Platform.Time.BusinessCalendar.DayUtcRange(endDate).StartUtc + info.EndsAt.ToTimeSpan(); // no DST in the Dominican Republic
        var releasableAt = curingFrom.AddHours(info.MinCuringHours);

        // E-LAB1-1, E-LAB1-01-4: the field code, when the item has its lot prefix and the machine its short code; the lot waits otherwise.
        await MfgSql.LockAsync(context, "lot-field-codes", cancellationToken).ConfigureAwait(false);
        var fieldCode = await Quality.FieldCodes.ForRunAsync(context, run.RunId, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "INSERT INTO mfg.fg_lot (lot_id, company_id, summary_id, run_id, curing_from, releasable_at, status, version, field_code) VALUES (@l, @c, @s, @r, @from, @at, 'CURING', 1, @field)",
            cancellationToken,
            ("l", lotId),
            ("c", context.CompanyId),
            ("s", summary.SummaryId),
            ("r", run.RunId),
            ("from", curingFrom),
            ("at", releasableAt),
            ("field", fieldCode)).ConfigureAwait(false);
        await context.AppendStateAsync(Runs.LotAggregate, lotId, "DOCUMENT", null, "CURING", CommandType, receiptEvent, cancellationToken).ConfigureAwait(false);

        // E-MFG1-6: ⌈units ÷ units per rack⌉ racks, the last one with the remainder.
        var racks = 0;
        for (var left = summary.GoodUnits; left > 0m; left -= info.UnitsPerRack)
        {
            racks++;
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "INSERT INTO mfg.rack (rack_id, company_id, lot_id, rack_no, units, status) VALUES (@id, @c, @l, @n, @u, 'CURING')",
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("l", lotId),
                ("n", racks),
                ("u", Math.Min(left, info.UnitsPerRack))).ConfigureAwait(false);
        }

        var materialJournal = await _engine.WriteAsync(context, materialPlan, materialEvent, cancellationToken).ConfigureAwait(false);
        var receiptJournal = await _engine.WriteAsync(context, receiptPlan, receiptEvent, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            summaryId = summary.SummaryId,
            status = "POSTED",
            version,
            lotId,
            lotCode,
            fieldCode,
            racks,
            consumptionValue = Runs.Money(consumptionValue),
            standardValue = Runs.Money(standardValue),
            materialJournalId = materialJournal.JournalId,
            receiptJournalId = receiptJournal.JournalId,
        });
    }

    /// <summary>E-MFG1-03-7: PT-&lt;item&gt;-&lt;yyyyMMdd&gt;-&lt;shift&gt;, with -2, -3… when a reversed summary already used the code.</summary>
    private static async Task<string> LotCodeAsync(CommandContext context, Runs.Run run, string shiftCode, CancellationToken cancellationToken)
    {
        var code = $"PT-{run.ItemCode}-{run.BusinessDate:yyyyMMdd}-{shiftCode}";
        var used = await MfgSql.ScalarAsync<long?>(
            context, "SELECT count(*) FROM inv.lot WHERE company_id = @c AND (lot_code = @code OR lot_code LIKE @code || '-%')", cancellationToken,
            ("c", context.CompanyId), ("code", code)).ConfigureAwait(false) ?? 0;
        return used == 0 ? code : $"{code}-{(used + 1).ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>E-MFG1-01-7: the plant's CURADO location, created by the first production.</summary>
    private static async Task<Guid> CuringLocationAsync(CommandContext context, Guid plantId, CancellationToken cancellationToken)
    {
        await MfgSql.LockAsync(context, $"curing:{plantId}", cancellationToken).ConfigureAwait(false);
        if (await MfgSql.ScalarAsync<Guid?>(context, "SELECT location_id FROM md.location WHERE plant_id = @p AND is_curing", cancellationToken, ("p", plantId)).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        var id = context.Ids.NewId();
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "INSERT INTO md.location (location_id, company_id, plant_id, code, is_curing) VALUES (@id, @c, @p, 'CURADO', true)",
            cancellationToken,
            ("id", id),
            ("c", context.CompanyId),
            ("p", plantId)).ConfigureAwait(false);
        return id;
    }
}

[RequiresPermission("shift_summary:post", StepUp = true)]
public sealed class ReverseShiftSummaryHandler : ICommandHandler<ReverseShiftSummary>
{
    private readonly PostingEngine _engine = new();
    private readonly InventoryLedger _inventory = new();

    public string CommandType => "Manufacturing.ReverseShiftSummary";

    private sealed record LotRow(Guid LotId, Guid LocationId, string Status, decimal Quantity, decimal Value, Guid QuantityEntryId, Guid ValueEntryId);

    private sealed record IssueRow(Guid MaterialItemId, Guid LotId, Guid LocationId, decimal Qty, decimal Value, Guid ValueEntryId);

    public async Task<string> HandleAsync(ReverseShiftSummary command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = (command.Reason ?? string.Empty).Trim();
        if (reason.Length == 0)
        {
            throw new DomainException(ManufacturingErrors.ReasonRequired, "Reversing a shift summary needs a reason.");
        }

        var run = await Runs.LockAsync(context, command.PlantId, command.RunId, cancellationToken).ConfigureAwait(false);
        var summary = await Runs.LiveSummaryAsync(context, run.RunId, cancellationToken).ConfigureAwait(false);
        if (summary is not { Status: "POSTED" })
        {
            throw new DomainException(ManufacturingErrors.InvalidState, "The run has no POSTED shift summary.");
        }

        if (summary.Version != command.ExpectedVersion)
        {
            throw new DomainException(ManufacturingErrors.VersionConflict, $"The summary changed (version {summary.Version}, expected {command.ExpectedVersion}); reload and retry.");
        }

        if (await MfgSql.ScalarAsync<string>(context, "SELECT status FROM mfg.cost_collector WHERE collector_id = @c", cancellationToken, ("c", run.CollectorId)).ConfigureAwait(false) != "OPEN")
        {
            throw new DomainException(ManufacturingErrors.CollectorSettled, "The run's cost collector is already settled.");
        }

        var lot = (await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT f.lot_id, q.location_id, f.status, q.quantity, v.amount, q.quantity_entry_id, v.value_entry_id
            FROM mfg.fg_lot f
            JOIN inv.inv_quantity_entry q ON q.lot_id = f.lot_id AND q.movement_type::text = 'PRODUCTION_RECEIPT'
            JOIN inv.inv_value_entry v ON v.quantity_entry_id = q.quantity_entry_id
            WHERE f.summary_id = @s
            FOR UPDATE OF f
            """,
            r => new LotRow(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetDecimal(3), r.GetDecimal(4), r.GetGuid(5), r.GetGuid(6)),
            cancellationToken,
            ("s", summary.SummaryId)).ConfigureAwait(false))!;
        if (lot.Status != "CURING" || await _inventory.LotHasLaterMovementsAsync(context, lot.LotId, lot.QuantityEntryId, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(ManufacturingErrors.LotMoved, "The lot has moved since production (released, blocked or scrapped); the summary cannot be reversed (E-MFG1-03-9).");
        }

        var issues = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT i.material_item_id, i.lot_id, c.location_id, i.qty, i.value, i.value_entry_id
            FROM mfg.consumption_issue i JOIN mfg.material_consumption c ON c.summary_id = i.summary_id AND c.material_item_id = i.material_item_id
            WHERE i.summary_id = @s ORDER BY c.location_id, i.material_item_id, i.lot_id
            """,
            r => new IssueRow(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetDecimal(3), r.GetDecimal(4), r.GetGuid(5)),
            cancellationToken,
            ("s", summary.SummaryId)).ConfigureAwait(false);
        await _inventory.LockStockAsync(context, lot.LocationId, run.ItemId, lot.LotId, cancellationToken).ConfigureAwait(false);
        foreach (var issue in issues)
        {
            await _inventory.LockStockAsync(context, issue.LocationId, issue.MaterialItemId, issue.LotId, cancellationToken).ConfigureAwait(false);
        }

        var plans = new List<ReversalPlan>();
        foreach (var eventId in new[] { summary.MaterialEventId!.Value, summary.ReceiptEventId!.Value })
        {
            var journal = await MfgSql.ScalarAsync<Guid?>(
                context,
                """
                SELECT j.journal_id FROM fin.gl_journal j
                WHERE j.company_id = @c AND j.source_event_id = @e AND j.journal_type = 'AUTO'
                  AND NOT EXISTS (SELECT 1 FROM fin.gl_journal r WHERE r.reverses_journal_id = j.journal_id)
                """,
                cancellationToken,
                ("c", context.CompanyId),
                ("e", eventId)).ConfigureAwait(false)
                ?? throw new DomainException(ManufacturingErrors.InvalidState, "The summary has no live journal.");
            var plan = await _engine.PrepareReversalAsync(context, journal, run.BusinessDate, cancellationToken).ConfigureAwait(false);
            if (plan.PostingDate != run.BusinessDate)
            {
                throw new DomainException(ManufacturingErrors.PeriodClosed, $"INV-MOV is closed for {run.BusinessDate:yyyy-MM-dd}; the summary can no longer be reversed.");
            }

            plans.Add(plan);
        }

        var occurredAt = context.Clock.UtcNow;
        var version = summary.Version + 1;
        var reversedEvent = await context.AppendEventAsync(
            new EventDraft(
                "ShiftSummaryReversed",
                1,
                Runs.SummaryAggregate,
                summary.SummaryId,
                await MfgSql.NextEventVersionAsync(context, Runs.SummaryAggregate, summary.SummaryId, cancellationToken).ConfigureAwait(false),
                JsonSerializer.Serialize(new { summaryId = summary.SummaryId, runId = run.RunId, lotId = lot.LotId, reason }),
                Publish: true,
                OccurredAt: occurredAt,
                BusinessDate: run.BusinessDate),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE mfg.shift_summary SET status = 'REVERSED', reversal_event_id = @e, reversal_reason = @r, version = @v WHERE summary_id = @s",
            cancellationToken,
            ("e", reversedEvent),
            ("r", reason),
            ("v", version),
            ("s", summary.SummaryId)).ConfigureAwait(false);
        await context.AppendStateAsync(Runs.SummaryAggregate, summary.SummaryId, "DOCUMENT", "POSTED", "REVERSED", CommandType, reversedEvent, cancellationToken, reason).ConfigureAwait(false);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE mfg.fg_lot SET status = 'VOIDED', version = version + 1 WHERE lot_id = @l", cancellationToken, ("l", lot.LotId)).ConfigureAwait(false);
        await context.AppendStateAsync(Runs.LotAggregate, lot.LotId, "DOCUMENT", "CURING", "VOIDED", CommandType, reversedEvent, cancellationToken, reason).ConfigureAwait(false);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE mfg.rack SET status = 'VOIDED' WHERE lot_id = @l", cancellationToken, ("l", lot.LotId)).ConfigureAwait(false);
        await Runs.SetRunStatusAsync(context, run, "IN_PROGRESS", CommandType, reversedEvent, cancellationToken, reason).ConfigureAwait(false);

        var dates = new MovementDates(occurredAt, run.BusinessDate, run.BusinessDate);
        var source = new MovementSource(reversedEvent, "SHIFT_SUMMARY_REVERSAL", summary.SummaryId);
        var valueEntryMap = new Dictionary<Guid, Guid>();
        var receiptReversal = context.Ids.NewId();
        await _inventory.PostMovementAsync(
            context, MovementTypes.ProductionReceiptReversal, lot.LocationId, run.ItemId, lot.LotId, -lot.Quantity, -lot.Value, receiptReversal, source, dates, cancellationToken).ConfigureAwait(false);
        valueEntryMap[lot.ValueEntryId] = receiptReversal;
        foreach (var issue in issues)
        {
            var reversal = context.Ids.NewId();
            await _inventory.PostMovementAsync(
                context, MovementTypes.ProductionIssueReversal, issue.LocationId, issue.MaterialItemId, issue.LotId, issue.Qty, issue.Value, reversal, source, dates, cancellationToken).ConfigureAwait(false);
            valueEntryMap[issue.ValueEntryId] = reversal;
        }

        // A new DRAFT with the same figures and the original recorder, so it can be corrected and posted again (four eyes kept).
        var draftId = context.Ids.NewId();
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO mfg.shift_summary (summary_id, company_id, run_id, batches, good_units, mix_scrap_units, fresh_scrap_units, status, recorded_by, version)
            VALUES (@id, @c, @r, @b, @g, @mix, @fresh, 'DRAFT', @by, 1)
            """,
            cancellationToken,
            ("id", draftId),
            ("c", context.CompanyId),
            ("r", run.RunId),
            ("b", summary.Batches),
            ("g", summary.GoodUnits),
            ("mix", summary.MixScrap),
            ("fresh", summary.FreshScrap),
            ("by", summary.RecordedBy)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO mfg.material_consumption (summary_id, company_id, material_item_id, location_id, entered_qty, entered_uom, qty, theoretical_qty)
            SELECT @id, company_id, material_item_id, location_id, entered_qty, entered_uom, qty, theoretical_qty FROM mfg.material_consumption WHERE summary_id = @s
            """,
            cancellationToken,
            ("id", draftId),
            ("s", summary.SummaryId)).ConfigureAwait(false);
        await context.AppendStateAsync(Runs.SummaryAggregate, draftId, "DOCUMENT", null, "DRAFT", CommandType, reversedEvent, cancellationToken).ConfigureAwait(false);

        var journals = new List<Guid>();
        foreach (var plan in plans)
        {
            journals.Add((await _engine.WriteReversalAsync(context, plan, reversedEvent, occurredAt, cancellationToken, valueEntryMap).ConfigureAwait(false)).JournalId);
        }

        return JsonSerializer.Serialize(new { summaryId = summary.SummaryId, status = "REVERSED", newSummaryId = draftId, runStatus = "IN_PROGRESS", reversalJournalIds = journals });
    }
}
