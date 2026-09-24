using System.Globalization;
using System.Text.Json;
using Rochell.Finance;
using Rochell.Finance.Policies;
using Rochell.Finance.Posting;
using Rochell.Inventory;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Time;
using Rochell.Procurement.PurchaseOrders;

namespace Rochell.Procurement.Ledger;

// These commands post a journal and move inventory value together; under the approved module graph (E-PR08-1) only
// Procurement may depend on both Finance and Inventory, so they live here.

/// <summary>
/// R-REP. Every inventory line of generation n gets an exact REPOST reversal of its value entry, and generation n + 1 a new REPOST
/// value entry of the same amount (E-PR14-3): the valuation is unchanged and each GL inventory line keeps its own value entry (P-1).
/// Each new inventory line records the value entry it replaces, so document reversals still find their inverse (PostingEngine).
/// Both value entries belong to the JournalReposted event of this transaction: every inventory ledger row has as source an event
/// committed with it, which is what groups them for sealing (PR-15).
/// </summary>
[RequiresPermission("journal:repost", StepUp = true)]
public sealed class RepostEventHandler : ICommandHandler<RepostEvent>
{
    private readonly PostingEngine _engine = new();
    private readonly InventoryLedger _inventory = new();

    public string CommandType => "Finance.RepostEvent";

    public async Task<string> HandleAsync(RepostEvent command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = PurchaseOrderStore.RequireReason(command.Reason);

        // One repost of an event × rule at a time (journals are append-only: no row lock is available to the application).
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended('repost:' || @c::text || ':' || @e::text || ':' || @r, 0))",
            cancellationToken,
            ("c", context.CompanyId),
            ("e", command.SourceEventId),
            ("r", command.RuleCode)).ConfigureAwait(false);

        Guid journalId;
        int generation;
        await using (var current = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT j.journal_id, j.posting_generation FROM fin.gl_journal j JOIN fin.posting_rule r ON r.posting_rule_id = j.posting_rule_id
            WHERE j.company_id = @c AND j.source_event_id = @e AND r.code = @r AND j.journal_type = 'AUTO'
              AND NOT EXISTS (SELECT 1 FROM fin.gl_journal x WHERE x.reverses_journal_id = j.journal_id)
            """,
            ("c", context.CompanyId),
            ("e", command.SourceEventId),
            ("r", command.RuleCode)))
        await using (var reader = await current.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new DomainException(ProcurementErrors.NothingToRepost, $"Event {command.SourceEventId} has no posted {command.RuleCode} journal to repost.");
            }

            (journalId, generation) = (reader.GetGuid(0), reader.GetInt32(1));
        }

        DateOnly businessDate;
        DateTime eventOccurredAt;
        await using (var source = Sql.Command(context.Connection, context.Transaction, "SELECT business_date, occurred_at FROM core.domain_event WHERE event_id = @e", ("e", command.SourceEventId)))
        await using (var reader = await source.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            (businessDate, eventOccurredAt) = (reader.GetFieldValue<DateOnly>(0), reader.GetDateTime(1));
        }

        var definition = await ActiveDefinitionAsync(context, command.RuleCode, businessDate, cancellationToken).ConfigureAwait(false);
        var lines = await EntriesAsync(context, journalId, cancellationToken).ConfigureAwait(false);

        // Inventory lines: lock the valuation (N7), plan the REPOST pair of value entries.
        var valueEntries = new List<(Guid Original, Guid Reversal, Guid Replacement, Guid Area, Guid Plant, Guid Item, decimal Amount)>();
        foreach (var line in lines.Where(l => l.InvValueEntryId is not null).OrderBy(l => l.InvValueEntryId))
        {
            await using var ve = Sql.Command(
                context.Connection,
                context.Transaction,
                "SELECT valuation_area_id, plant_id, item_id, amount FROM inv.inv_value_entry WHERE value_entry_id = @v",
                ("v", line.InvValueEntryId!.Value));
            await using var reader = await ve.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            valueEntries.Add((line.InvValueEntryId!.Value, context.Ids.NewId(), context.Ids.NewId(), reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetDecimal(3)));
        }

        foreach (var key in valueEntries.Select(v => (v.Area, v.Item)).Distinct().OrderBy(k => k.Area).ThenBy(k => k.Item))
        {
            await _inventory.LockValuationAsync(context, key.Area, key.Item, cancellationToken).ConfigureAwait(false);
        }

        // Generation n + 1: same rule lines, dimensions and amounts; accounts resolved again by the engine.
        var replacement = valueEntries.ToDictionary(v => v.Original, v => v.Replacement);
        var inputs = new List<PostingLineInput>();
        foreach (var line in lines)
        {
            var ruleLine = definition.Lines.SingleOrDefault(l => l.Code == line.RuleLineCode)
                ?? throw new DomainException(ProcurementErrors.RepostRuleIncompatible, $"The {command.RuleCode} version in force has no line {line.RuleLineCode}; the journal cannot be reposted as is.");
            var lineInputs = new Dictionary<string, string>(line.Inputs);
            Guid? valueEntry = null;
            if (line.InvValueEntryId is not null)
            {
                valueEntry = replacement[line.InvValueEntryId.Value];
                lineInputs[PostingEngine.RepostsValueEntryInput] = line.InvValueEntryId.Value.ToString();
            }

            inputs.Add(new PostingLineInput(
                line.RuleLineCode,
                ruleLine.Amount,
                line.Debit + line.Credit,
                PlantId: line.PlantId,
                ItemId: line.ItemId,
                PartyId: line.PartyId,
                SubledgerRef: valueEntry ?? line.SubledgerRef,
                InvValueEntryId: valueEntry,
                Inputs: lineInputs));
        }

        // P-1: both journals validated before any write (E-PR14-2: the event's business date, late entry if closed).
        var reversalPlan = await _engine.PrepareReversalAsync(context, journalId, businessDate, cancellationToken).ConfigureAwait(false);
        var newPlan = await _engine.PrepareAsync(context, new PostingRequest(command.RuleCode, businessDate, eventOccurredAt, inputs, Generation: generation + 1), cancellationToken).ConfigureAwait(false);

        var occurredAt = context.Clock.UtcNow;
        var repostEventId = await context.AppendEventAsync(
            new EventDraft(
                "JournalReposted",
                1,
                "Repost",
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new { sourceEventId = command.SourceEventId, ruleCode = command.RuleCode, reversedJournalId = journalId, generation = generation + 1, reason }),
                Publish: true,
                OccurredAt: occurredAt,
                BusinessDate: businessDate),
            cancellationToken).ConfigureAwait(false);

        foreach (var v in valueEntries)
        {
            await _inventory.PostValueAdjustmentAsync(
                context, MovementTypes.Repost, v.Area, v.Plant, v.Item, -v.Amount, v.Reversal, v.Original,
                new MovementSource(repostEventId, "REPOST", context.ResultRef), new MovementDates(occurredAt, businessDate, reversalPlan.PostingDate), cancellationToken).ConfigureAwait(false);
            await _inventory.PostValueAdjustmentAsync(
                context, MovementTypes.Repost, v.Area, v.Plant, v.Item, v.Amount, v.Replacement, null,
                new MovementSource(repostEventId, "REPOST", context.ResultRef), new MovementDates(occurredAt, businessDate, newPlan.PostingDate), cancellationToken).ConfigureAwait(false);
        }

        var reversed = await _engine.WriteReversalAsync(context, reversalPlan, repostEventId, occurredAt, cancellationToken, valueEntries.ToDictionary(v => v.Original, v => v.Reversal)).ConfigureAwait(false);
        var posted = await _engine.WriteAsync(context, newPlan, command.SourceEventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { reversalJournalId = reversed.JournalId, journalId = posted.JournalId, generation = generation + 1, lateEntry = posted.LateEntry });
    }

    private static async Task<RuleDefinition> ActiveDefinitionAsync(CommandContext context, string ruleCode, DateOnly date, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT v.definition::text FROM fin.posting_rule_version v JOIN fin.posting_rule r ON r.posting_rule_id = v.posting_rule_id
            WHERE r.code = @r AND v.status = 'ACTIVE' AND v.effective_from <= @d AND (v.effective_to IS NULL OR v.effective_to > @d)
            """,
            ("r", ruleCode),
            ("d", date));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json
            ? RuleDefinition.Parse(json)
            : throw new DomainException(FinanceErrors.PostingPrerequisiteMissing, $"No ACTIVE version of rule {ruleCode} on {date:yyyy-MM-dd}.");
    }

    private static async Task<List<RepostLine>> EntriesAsync(CommandContext context, Guid journalId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT rule_line_code, debit, credit, plant_id, item_id, party_id, subledger_ref, inv_value_entry_id, determination_inputs::text
            FROM fin.gl_entry WHERE journal_id = @j ORDER BY line_no
            """,
            ("j", journalId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var lines = new List<RepostLine>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var doc = JsonDocument.Parse(reader.GetString(8)))
            {
                if (doc.RootElement.TryGetProperty("inputs", out var original) && original.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in original.EnumerateObject().Where(p => p.Name != PostingEngine.RepostsValueEntryInput))
                    {
                        inputs[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
                    }
                }
            }

            lines.Add(new RepostLine(
                reader.GetString(0), reader.GetDecimal(1), reader.GetDecimal(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.IsDBNull(5) ? null : reader.GetGuid(5),
                reader.IsDBNull(6) ? null : reader.GetGuid(6), reader.IsDBNull(7) ? null : reader.GetGuid(7), inputs));
        }

        return lines;
    }

    private sealed record RepostLine(string RuleLineCode, decimal Debit, decimal Credit, Guid? PlantId, Guid? ItemId, Guid? PartyId, Guid? SubledgerRef, Guid? InvValueEntryId, Dictionary<string, string> Inputs);
}

/// <summary>R-06: the orphan value of an area × item goes to zero against PURCHASE_PRICE_VARIANCE or INVENTORY_ADJUSTMENT (policy).</summary>
[RequiresPermission("valuation_residual:approve", StepUp = true)]
public sealed class ApproveValuationResidualAdjustmentHandler : ICommandHandler<ApproveValuationResidualAdjustment>
{
    private readonly PostingEngine _engine = new();
    private readonly InventoryLedger _inventory = new();

    public string CommandType => "Inventory.ApproveValuationResidualAdjustment";

    public async Task<string> HandleAsync(ApproveValuationResidualAdjustment command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = PurchaseOrderStore.RequireReason(command.Reason);
        var (quantity, value) = await _inventory.LockValuationAsync(context, command.ValuationAreaId, command.ItemId, cancellationToken).ConfigureAwait(false);
        if (quantity != 0 || value == 0)
        {
            throw new DomainException(ProcurementErrors.NotOrphanResidual, $"The position holds {quantity} with value {value}; R-06 only removes value left without quantity (E-PR14-4).");
        }

        Guid plantId;
        await using (var plant = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT plant_id FROM md.plant WHERE company_id = @c AND valuation_area_id = @a ORDER BY plant_id LIMIT 1",
            ("c", context.CompanyId),
            ("a", command.ValuationAreaId)))
        {
            plantId = (Guid)(await plant.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        var occurredAt = context.Clock.UtcNow;
        var businessDate = BusinessCalendar.DefaultBusinessDate(occurredAt);
        var policy = await PolicyResolver.ResolveAsync(context, PolicyCodes.Inventory, businessDate, cancellationToken).ConfigureAwait(false);
        var counter = policy.Text(PolicyParameters.ValuationResidualAccountRole) == "INVENTORY_ADJUSTMENT" ? "ADJ" : "PPV";
        var valueEntryId = context.Ids.NewId();
        var amount = Math.Abs(value);
        var plan = await _engine.PrepareAsync(
            context,
            new PostingRequest(
                "R-06",
                businessDate,
                occurredAt,
                value > 0
                    ?
                    [
                        new PostingLineInput("R06-CR-INV", "residual", amount, PlantId: plantId, ItemId: command.ItemId, SubledgerRef: valueEntryId, InvValueEntryId: valueEntryId),
                        new PostingLineInput($"R06-DR-{counter}", "residual", amount, PlantId: plantId, ItemId: command.ItemId),
                    ]
                    :
                    [
                        new PostingLineInput("R06-DR-INV", "residual", amount, PlantId: plantId, ItemId: command.ItemId, SubledgerRef: valueEntryId, InvValueEntryId: valueEntryId),
                        new PostingLineInput($"R06-CR-{counter}", "residual", amount, PlantId: plantId, ItemId: command.ItemId),
                    ]),
            cancellationToken).ConfigureAwait(false);

        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ValuationResidualAdjusted",
                1,
                "ValuationPosition",
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new
                {
                    valuationAreaId = command.ValuationAreaId,
                    itemId = command.ItemId,
                    residual = value.ToString(CultureInfo.InvariantCulture),
                    counterAccountRole = policy.Text(PolicyParameters.ValuationResidualAccountRole),
                    policyVersionId = policy.PolicyVersionId,
                    reason,
                }),
                Publish: true,
                OccurredAt: occurredAt,
                BusinessDate: businessDate),
            cancellationToken).ConfigureAwait(false);
        await _inventory.PostValueAdjustmentAsync(
            context, MovementTypes.ResidualAdjustment, command.ValuationAreaId, plantId, command.ItemId, -value, valueEntryId, null,
            new MovementSource(eventId, "VALUATION_RESIDUAL", context.ResultRef), new MovementDates(occurredAt, businessDate, plan.PostingDate), cancellationToken).ConfigureAwait(false);
        var journal = await _engine.WriteAsync(context, plan, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { journalId = journal.JournalId, residual = value.ToString(CultureInfo.InvariantCulture) });
    }
}
