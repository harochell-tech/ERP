using System.Globalization;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Manufacturing.Runs;

internal static class Runs
{
    public const string RunAggregate = "ProductionRun";
    public const string SummaryAggregate = "ShiftSummary";
    public const string LotAggregate = "FgLot";
    public const string CollectorAggregate = "CostCollector";
    public const string MaterialRule = "P-08";
    public const string ReceiptRule = "P-10";

    public static string Qty(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>The run as the summary commands need it.</summary>
    public sealed record Run(
        Guid RunId, string RunNo, Guid PlantId, Guid MachineId, Guid ShiftId, DateOnly BusinessDate, Guid ItemId, string ItemCode, Guid RecipeVersionId, Guid CostVersionId,
        Guid CollectorId, string Status, long Version);

    /// <summary>Locks the run (FOR UPDATE) and checks it belongs to the command's plant.</summary>
    public static async Task<Run> LockAsync(CommandContext context, Guid plantId, Guid runId, CancellationToken cancellationToken)
    {
        var run = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT r.run_id, r.run_no, r.plant_id, r.machine_id, r.shift_id, r.business_date, r.item_id, i.code, r.recipe_version_id, r.cost_version_id,
                   r.collector_id, r.status, r.version
            FROM mfg.production_run r JOIN md.item i ON i.item_id = r.item_id
            WHERE r.company_id = @c AND r.run_id = @r
            FOR UPDATE OF r
            """,
            r => new Run(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetGuid(3), r.GetGuid(4), r.Date(5), r.GetGuid(6), r.GetString(7), r.GetGuid(8), r.GetGuid(9),
                r.GetGuid(10), r.GetString(11), r.GetInt64(12)),
            cancellationToken,
            ("c", context.CompanyId),
            ("r", runId)).ConfigureAwait(false)
            ?? throw new DomainException(ManufacturingErrors.NotFound, "The production run does not exist.");
        return run.PlantId == plantId ? run : throw new DomainException(ManufacturingErrors.PlantMismatch, "The run belongs to another plant.");
    }

    public static async Task SetRunStatusAsync(CommandContext context, Run run, string to, string commandType, Guid eventId, CancellationToken cancellationToken, string? reason = null)
    {
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE mfg.production_run SET status = @s, version = version + 1 WHERE run_id = @r", cancellationToken, ("s", to), ("r", run.RunId)).ConfigureAwait(false);
        await context.AppendStateAsync(RunAggregate, run.RunId, "DOCUMENT", run.Status, to, commandType, eventId, cancellationToken, reason).ConfigureAwait(false);
    }

    /// <summary>The summary of a run as posting and reversal need it.</summary>
    public sealed record Summary(Guid SummaryId, int Batches, decimal GoodUnits, decimal MixScrap, decimal FreshScrap, string Status, Guid RecordedBy, Guid? MaterialEventId, Guid? ReceiptEventId, long Version);

    public static Task<Summary?> LiveSummaryAsync(CommandContext context, Guid runId, CancellationToken cancellationToken)
        => Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT summary_id, batches, good_units, mix_scrap_units, fresh_scrap_units, status, recorded_by, material_event_id, receipt_event_id, version
            FROM mfg.shift_summary WHERE run_id = @r AND status IN ('DRAFT', 'POSTED')
            """,
            r => new Summary(r.GetGuid(0), r.GetInt32(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4), r.GetString(5), r.GetGuid(6),
                r.IsDBNull(7) ? null : r.GetGuid(7), r.IsDBNull(8) ? null : r.GetGuid(8), r.GetInt64(9)),
            cancellationToken,
            ("r", runId));

    /// <summary>A consumption line of a summary.</summary>
    public sealed record Line(Guid MaterialItemId, string MaterialCode, Guid LocationId, decimal EnteredQty, string EnteredUom, decimal Qty, decimal TheoreticalQty);

    public static Task<List<Line>> LinesAsync(CommandContext context, Guid summaryId, CancellationToken cancellationToken)
        => Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT c.material_item_id, i.code, c.location_id, c.entered_qty, c.entered_uom, c.qty, c.theoretical_qty
            FROM mfg.material_consumption c JOIN md.item i ON i.item_id = c.material_item_id
            WHERE c.summary_id = @s ORDER BY i.code
            """,
            r => new Line(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetDecimal(3), r.GetString(4), r.GetDecimal(5), r.GetDecimal(6)),
            cancellationToken,
            ("s", summaryId));
}
