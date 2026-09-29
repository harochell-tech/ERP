using System.Globalization;
using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Manufacturing.Costing;

[RequiresPermission("cost_collector:settle", StepUp = true)]
public sealed class SettleCostCollectorHandler : ICommandHandler<SettleCostCollector>
{
    private readonly PostingEngine _engine = new();

    public string CommandType => "Manufacturing.SettleCostCollector";

    private sealed record Collector(Guid PlantId, Guid ItemId, DateOnly Month, string Status, long Version);

    private sealed record UsageLine(decimal Qty, decimal StdQtyPerUnit, decimal GoodUnits, decimal StdPrice);

    public async Task<string> HandleAsync(SettleCostCollector command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var collector = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT plant_id, item_id, period_month, status, version FROM mfg.cost_collector WHERE company_id = @c AND collector_id = @id FOR UPDATE",
            r => new Collector(r.GetGuid(0), r.GetGuid(1), r.Date(2), r.GetString(3), r.GetInt64(4)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", command.CollectorId)).ConfigureAwait(false)
            ?? throw new DomainException(ManufacturingErrors.NotFound, "The cost collector does not exist.");
        if (collector.PlantId != command.PlantId)
        {
            throw new DomainException(ManufacturingErrors.PlantMismatch, "The collector belongs to another plant.");
        }

        if (collector.Version != command.ExpectedVersion)
        {
            throw new DomainException(ManufacturingErrors.VersionConflict, $"The collector changed (version {collector.Version}, expected {command.ExpectedVersion}); reload and retry.");
        }

        if (collector.Status != "OPEN")
        {
            throw new DomainException(ManufacturingErrors.CollectorSettled, "The collector is already settled.");
        }

        var monthEnd = collector.Month.AddMonths(1).AddDays(-1);
        if (MfgSql.Today(context) <= monthEnd)
        {
            throw new DomainException(ManufacturingErrors.MonthNotEnded, $"The collector's month ends on {monthEnd:yyyy-MM-dd}; it is settled afterwards.");
        }

        // The runs' locks: no summary can be posted or reversed into the collector while it settles.
        var open = await MfgSql.ScalarAsync<long?>(
            context,
            "SELECT count(*) FILTER (WHERE status = 'IN_PROGRESS') FROM (SELECT status FROM mfg.production_run WHERE collector_id = @id ORDER BY run_id FOR UPDATE) r",
            cancellationToken,
            ("id", command.CollectorId)).ConfigureAwait(false) ?? 0;
        if (open > 0)
        {
            throw new DomainException(ManufacturingErrors.RunsOpen, $"{open.ToString(CultureInfo.InvariantCulture)} run(s) of the collector are still IN_PROGRESS.");
        }

        var wip = await MfgSql.ScalarAsync<decimal?>(
            context, "SELECT sum(debit - credit) FROM fin.gl_entry WHERE company_id = @c AND account_role = 'WIP' AND subledger_ref = @id", cancellationToken,
            ("c", context.CompanyId), ("id", command.CollectorId)).ConfigureAwait(false) ?? 0m;
        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT c.qty, m.std_qty_per_unit, ss.good_units, m.std_price
            FROM mfg.material_consumption c
            JOIN mfg.shift_summary ss ON ss.summary_id = c.summary_id AND ss.status = 'POSTED'
            JOIN mfg.production_run r ON r.run_id = ss.run_id
            JOIN md.standard_cost_material m ON m.cost_version_id = r.cost_version_id AND m.material_item_id = c.material_item_id
            WHERE r.collector_id = @id
            """,
            r => new UsageLine(r.GetDecimal(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3)),
            cancellationToken,
            ("id", command.CollectorId)).ConfigureAwait(false);
        var usage = lines.Sum(l => decimal.Round((l.Qty - (l.StdQtyPerUnit * l.GoodUnits)) * l.StdPrice, 2, MidpointRounding.AwayFromZero));
        var price = wip - usage;

        var occurredAt = context.Clock.UtcNow;
        var month = collector.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var inputs = new Dictionary<string, string> { ["collector"] = command.CollectorId.ToString(), ["month"] = month };
        var postingLines = new List<PostingLineInput>();
        // Unfavourable (positive): Dr variance / Cr WIP; favourable (negative): Dr WIP / Cr variance. WIP lines carry the collector.
        void Add(decimal amount, string source, string unfavourableDr, string unfavourableCr, string favourableDr, string favourableCr)
        {
            if (amount == 0m)
            {
                return;
            }

            var value = Math.Abs(amount);
            var (dr, cr) = amount > 0m ? (unfavourableDr, unfavourableCr) : (favourableDr, favourableCr);
            var wipIsDebit = amount < 0m;
            postingLines.Add(new PostingLineInput(dr, source, value, PlantId: collector.PlantId, ItemId: collector.ItemId, SubledgerRef: wipIsDebit ? command.CollectorId : null, Inputs: inputs));
            postingLines.Add(new PostingLineInput(cr, source, value, PlantId: collector.PlantId, ItemId: collector.ItemId, SubledgerRef: wipIsDebit ? null : command.CollectorId, Inputs: inputs));
        }

        Add(usage, "usage_variance", "P13-DR-USAGE", "P13-CR-WIP-USAGE", "P13-DR-WIP-USAGE", "P13-CR-USAGE");
        Add(price, "price_variance", "P13-DR-PRICE", "P13-CR-WIP-PRICE", "P13-DR-WIP-PRICE", "P13-CR-PRICE");
        PostingPlan? plan = null;
        if (postingLines.Count > 0)
        {
            plan = await _engine.PrepareAsync(context, new PostingRequest("P-13", monthEnd, occurredAt, postingLines), cancellationToken).ConfigureAwait(false);
            if (plan.PostingDate != monthEnd)
            {
                throw new DomainException(ManufacturingErrors.PeriodClosed, $"COST-SET is closed for {monthEnd:yyyy-MM-dd}; the collector is settled only into its own month.");
            }
        }

        var settler = await MfgSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var next = collector.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "CostCollectorSettled",
                1,
                Runs.Runs.CollectorAggregate,
                command.CollectorId,
                await MfgSql.NextEventVersionAsync(context, Runs.Runs.CollectorAggregate, command.CollectorId, cancellationToken).ConfigureAwait(false),
                JsonSerializer.Serialize(new
                {
                    collectorId = command.CollectorId,
                    month,
                    wipBalance = Runs.Runs.Money(wip),
                    usageVariance = Runs.Runs.Money(usage),
                    priceVariance = Runs.Runs.Money(price),
                }),
                Publish: true,
                OccurredAt: occurredAt,
                BusinessDate: monthEnd),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            UPDATE mfg.cost_collector SET status = 'SETTLED', settled_by = @by, usage_variance = @u, price_variance = @p, settlement_event_id = @e, settled_at = @at, version = @v
            WHERE collector_id = @id
            """,
            cancellationToken,
            ("by", settler),
            ("u", usage),
            ("p", price),
            ("e", eventId),
            ("at", occurredAt),
            ("v", next),
            ("id", command.CollectorId)).ConfigureAwait(false);
        await context.AppendStateAsync(Runs.Runs.CollectorAggregate, command.CollectorId, "DOCUMENT", "OPEN", "SETTLED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        Guid? journalId = plan is null ? null : (await _engine.WriteAsync(context, plan, eventId, cancellationToken).ConfigureAwait(false)).JournalId;
        return JsonSerializer.Serialize(new
        {
            collectorId = command.CollectorId,
            status = "SETTLED",
            version = next,
            wipBalance = Runs.Runs.Money(wip),
            usageVariance = Runs.Runs.Money(usage),
            priceVariance = Runs.Runs.Money(price),
            journalId,
        });
    }
}
