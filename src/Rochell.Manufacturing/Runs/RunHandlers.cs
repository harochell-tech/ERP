using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Manufacturing.Runs;

[RequiresPermission("production_run:manage")]
public sealed class StartProductionRunHandler : ICommandHandler<StartProductionRun>
{
    public string CommandType => "Manufacturing.StartProductionRun";

    private sealed record Master(Guid PlantId, string Status);

    public async Task<string> HandleAsync(StartProductionRun command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var machine = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, "SELECT plant_id, status FROM md.machine WHERE company_id = @c AND machine_id = @m",
            r => new Master(r.GetGuid(0), r.GetString(1)), cancellationToken, ("c", context.CompanyId), ("m", command.MachineId)).ConfigureAwait(false)
            ?? throw new DomainException(ManufacturingErrors.NotFound, "The machine does not exist.");
        var shift = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, "SELECT plant_id, status FROM mfg.shift WHERE company_id = @c AND shift_id = @s",
            r => new Master(r.GetGuid(0), r.GetString(1)), cancellationToken, ("c", context.CompanyId), ("s", command.ShiftId)).ConfigureAwait(false)
            ?? throw new DomainException(ManufacturingErrors.NotFound, "The shift does not exist.");
        if (machine.PlantId != command.PlantId || shift.PlantId != command.PlantId)
        {
            throw new DomainException(ManufacturingErrors.PlantMismatch, "The machine and the shift must belong to the run's plant.");
        }

        if (machine.Status != "ACTIVE")
        {
            throw new DomainException(ManufacturingErrors.MachineNotActive, "The machine is INACTIVE.");
        }

        if (shift.Status != "ACTIVE")
        {
            throw new DomainException(ManufacturingErrors.ShiftNotActive, "The shift is INACTIVE.");
        }

        var recipe = await MfgSql.ScalarAsync<Guid?>(
            context, "SELECT recipe_version_id FROM mfg.recipe_version WHERE company_id = @c AND item_id = @i AND machine_id = @m AND status = 'ACTIVE'", cancellationToken,
            ("c", context.CompanyId), ("i", command.ItemId), ("m", command.MachineId)).ConfigureAwait(false)
            ?? throw new DomainException(ManufacturingErrors.RecipeNotActive, "The product has no ACTIVE recipe on this machine.");
        var cost = await MfgSql.ScalarAsync<Guid?>(
            context,
            """
            SELECT c.cost_version_id FROM md.standard_cost_version c JOIN md.plant p ON p.valuation_area_id = c.valuation_area_id
            WHERE c.company_id = @c AND c.item_id = @i AND p.plant_id = @p AND c.status = 'ACTIVE' AND c.material_cost IS NOT NULL
            """,
            cancellationToken,
            ("c", context.CompanyId),
            ("i", command.ItemId),
            ("p", command.PlantId)).ConfigureAwait(false)
            ?? throw new DomainException(ManufacturingErrors.StandardCostMissing, "The product has no ACTIVE standard cost with breakdown in the plant (E-MFG1-03-1).");

        var month = new DateOnly(command.BusinessDate.Year, command.BusinessDate.Month, 1);
        await MfgSql.LockAsync(context, $"collector:{command.PlantId}:{command.ItemId}:{month:yyyy-MM}", cancellationToken).ConfigureAwait(false);
        var collector = await MfgSql.ScalarAsync<Guid?>(
            context, "SELECT collector_id FROM mfg.cost_collector WHERE company_id = @c AND plant_id = @p AND item_id = @i AND period_month = @m", cancellationToken,
            ("c", context.CompanyId), ("p", command.PlantId), ("i", command.ItemId), ("m", month)).ConfigureAwait(false);
        var newCollector = collector is null;
        var collectorId = collector ?? context.Ids.NewId();

        await MfgSql.LockAsync(context, "run-no", cancellationToken).ConfigureAwait(false);
        var last = await MfgSql.ScalarAsync<int?>(
            context, "SELECT max(substring(run_no from 4)::int) FROM mfg.production_run WHERE company_id = @c", cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false) ?? 0;
        var runNo = "PR-" + (last + 1).ToString("D6", CultureInfo.InvariantCulture);
        var starter = await MfgSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ProductionRunStarted",
                1,
                Runs.RunAggregate,
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new
                {
                    runId = context.ResultRef,
                    runNo,
                    plantId = command.PlantId,
                    machineId = command.MachineId,
                    shiftId = command.ShiftId,
                    businessDate = command.BusinessDate,
                    itemId = command.ItemId,
                    recipeVersionId = recipe,
                    costVersionId = cost,
                    collectorId,
                }),
                Publish: true,
                BusinessDate: command.BusinessDate),
            cancellationToken).ConfigureAwait(false);
        if (newCollector)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "INSERT INTO mfg.cost_collector (collector_id, company_id, plant_id, item_id, period_month, status, version) VALUES (@id, @c, @p, @i, @m, 'OPEN', 1)",
                cancellationToken,
                ("id", collectorId),
                ("c", context.CompanyId),
                ("p", command.PlantId),
                ("i", command.ItemId),
                ("m", month)).ConfigureAwait(false);
            await context.AppendStateAsync(Runs.CollectorAggregate, collectorId, "DOCUMENT", null, "OPEN", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO mfg.production_run (run_id, company_id, run_no, plant_id, machine_id, shift_id, business_date, item_id, recipe_version_id, cost_version_id,
                                                collector_id, status, started_by, version)
                VALUES (@id, @c, @no, @p, @m, @s, @d, @i, @r, @cost, @col, 'IN_PROGRESS', @by, 1)
                """,
                cancellationToken,
                ("id", context.ResultRef),
                ("c", context.CompanyId),
                ("no", runNo),
                ("p", command.PlantId),
                ("m", command.MachineId),
                ("s", command.ShiftId),
                ("d", command.BusinessDate),
                ("i", command.ItemId),
                ("r", recipe),
                ("cost", cost),
                ("col", collectorId),
                ("by", starter)).ConfigureAwait(false);
        }
        catch (DbException ex) when (ex.SqlState == SqlStates.UniqueViolation)
        {
            throw new DomainException(ManufacturingErrors.RunExists, "The machine already has a run of this product for the shift and date.");
        }

        await context.AppendStateAsync(Runs.RunAggregate, context.ResultRef, "DOCUMENT", null, "IN_PROGRESS", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { runId = context.ResultRef, runNo, status = "IN_PROGRESS", version = 1, collectorId });
    }
}

[RequiresPermission("production_run:manage")]
public sealed class CancelProductionRunHandler : ICommandHandler<CancelProductionRun>
{
    public string CommandType => "Manufacturing.CancelProductionRun";

    public async Task<string> HandleAsync(CancelProductionRun command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = (command.Reason ?? string.Empty).Trim();
        if (reason.Length == 0)
        {
            throw new DomainException(ManufacturingErrors.ReasonRequired, "Cancelling a run needs a reason.");
        }

        var run = await Runs.LockAsync(context, command.PlantId, command.RunId, cancellationToken).ConfigureAwait(false);
        if (run.Version != command.ExpectedVersion)
        {
            throw new DomainException(ManufacturingErrors.VersionConflict, $"The run changed (version {run.Version}, expected {command.ExpectedVersion}); reload and retry.");
        }

        if (run.Status != "IN_PROGRESS")
        {
            throw new DomainException(ManufacturingErrors.InvalidState, $"The run is {run.Status}.");
        }

        if (await MfgSql.ScalarAsync<Guid?>(context, "SELECT summary_id FROM mfg.shift_summary WHERE run_id = @r LIMIT 1", cancellationToken, ("r", run.RunId)).ConfigureAwait(false) is not null)
        {
            throw new DomainException(ManufacturingErrors.SummaryExists, "The run has a shift summary; it cannot be cancelled.");
        }

        var next = run.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft("ProductionRunCancelled", 1, Runs.RunAggregate, run.RunId, next, JsonSerializer.Serialize(new { runId = run.RunId, runNo = run.RunNo, reason }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Runs.SetRunStatusAsync(context, run, "CANCELLED", CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { runId = run.RunId, status = "CANCELLED", version = next });
    }
}

[RequiresPermission("shift_summary:record")]
public sealed class RecordShiftSummaryHandler : ICommandHandler<RecordShiftSummary>
{
    public string CommandType => "Manufacturing.RecordShiftSummary";

    public async Task<string> HandleAsync(RecordShiftSummary command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var run = await Runs.LockAsync(context, command.PlantId, command.RunId, cancellationToken).ConfigureAwait(false);
        var recorder = await MfgSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);

        // MFG-2 (E-MFG2-01-2/8, E-MFG2-8): a person's change to a draft the portal prepared stops the portal from replacing it; a typed
        // consumption for a machine whose batch plant is online needs a reason; the same consumption keeps where it came from.
        var current = await ShiftSummaryWriter.ProvenanceAsync(context, run.RunId, cancellationToken).ConfigureAwait(false);
        var provenance = SummaryProvenance.Manual(command.ConsumptionReason);
        if (current is { Source: "PORTAL" } portal)
        {
            var sameConsumption = await ShiftSummaryWriter.SameConsumptionAsync(context, run.RunId, command.Consumption ?? [], cancellationToken).ConfigureAwait(false);
            var online = await MfgSql.ScalarAsync<string?>(
                context, "SELECT batch_plant FROM mfg.portal_machine WHERE company_id = @c AND machine_id = @m", cancellationToken, ("c", context.CompanyId), ("m", run.MachineId))
                .ConfigureAwait(false) is not null;
            provenance = sameConsumption
                ? portal with { EditedBy = recorder }
                : portal with { EditedBy = recorder, ConsumptionSource = "MANUAL", ConsumptionId = null, ConsumptionReason = Reason(command.ConsumptionReason, online) };
        }

        var (summaryId, version, replaced) = await ShiftSummaryWriter.WriteAsync(
            context, run, command.Batches, command.GoodUnits, command.MixScrapUnits, command.FreshScrapUnits, command.Consumption ?? [], provenance, CommandType, cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.Serialize(new { summaryId, runId = run.RunId, status = "DRAFT", version, replaced, consumptionSource = provenance.ConsumptionSource });
    }

    private static string? Reason(string? reason, bool required)
    {
        var text = (reason ?? string.Empty).Trim();
        return text.Length switch
        {
            0 when !required => null,
            >= 10 and <= 300 => text,
            _ => throw new DomainException(ManufacturingErrors.ReasonRequired, "The batch plant sends this machine's consumption: typing it needs a reason of 10 to 300 characters (E-MFG2-8)."),
        };
    }
}
