using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Manufacturing.Runs;

/// <summary>
/// Where a shift summary came from (MFG-2, E-MFG2-01-2/8): typed (MANUAL) or prepared from the machines' portal (PORTAL, its reading), who
/// changed a portal draft, and where its consumption came from — MANUAL, BATCH_PLANT (its post) or PENDING (the recipe's theoretical as a
/// placeholder, never posted). A PORTAL summary's theoretical consumption is the recipe's per unit × units (E-MFG2-7); a MANUAL one keeps
/// MFG-1's per batch × batches.
/// </summary>
internal sealed record SummaryProvenance(string Source, Guid? ReadingId, Guid? EditedBy, string ConsumptionSource, string? ConsumptionReason, Guid? ConsumptionId)
{
    public static SummaryProvenance Manual(string? reason) => new("MANUAL", null, null, "MANUAL", string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), null);

    public bool UnitsTheoretical => Source == "PORTAL";
}

internal static class ShiftSummaryWriter
{
    private sealed record RecipeLine(Guid MaterialItemId, decimal QtyPerBatch, string BaseUom);

    public static Task<SummaryProvenance?> ProvenanceAsync(CommandContext context, Guid runId, CancellationToken cancellationToken)
        => Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT source, portal_reading_id, edited_by, consumption_source, consumption_reason, portal_consumption_id
            FROM mfg.shift_summary WHERE run_id = @r AND status IN ('DRAFT', 'POSTED')
            """,
            r => new SummaryProvenance(r.GetString(0), r.NullableGuid(1), r.NullableGuid(2), r.GetString(3), r.NullableString(4), r.NullableGuid(5)),
            cancellationToken,
            ("r", runId));

    /// <summary>Whether <paramref name="consumption"/> is exactly the live draft's (material, location, quantity, unit).</summary>
    public static async Task<bool> SameConsumptionAsync(CommandContext context, Guid runId, IReadOnlyList<ConsumptionInput> consumption, CancellationToken cancellationToken)
    {
        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT c.material_item_id, c.location_id, c.entered_qty, c.entered_uom FROM mfg.material_consumption c
            JOIN mfg.shift_summary s ON s.summary_id = c.summary_id WHERE s.run_id = @r AND s.status = 'DRAFT'
            """,
            r => (Material: r.GetGuid(0), Location: r.GetGuid(1), Qty: r.GetDecimal(2), Uom: r.GetString(3)),
            cancellationToken,
            ("r", runId)).ConfigureAwait(false);
        return lines.Count == consumption.Count
            && consumption.All(c => lines.Any(l => l.Material == c.MaterialItemId && l.Location == c.LocationId && l.Qty == c.Quantity && l.Uom == (c.Uom ?? string.Empty).Trim()));
    }

    /// <summary>Records or replaces the DRAFT summary of an IN_PROGRESS run (E-MFG1-03-2/3) with its provenance. Returns its id and version.</summary>
    public static async Task<(Guid SummaryId, long Version, bool Replaced)> WriteAsync(
        CommandContext context, Runs.Run run, int batches, decimal goodUnits, decimal mixScrapUnits, decimal freshScrapUnits, IReadOnlyList<ConsumptionInput> consumption,
        SummaryProvenance provenance, string commandType, CancellationToken cancellationToken, Guid? newSummaryId = null)
    {
        if (batches <= 0)
        {
            throw new DomainException(ManufacturingErrors.QuantityInvalid, "The batches are one or more.");
        }

        var good = MfgSql.Quantity(goodUnits, "The good units");
        var mixScrap = mixScrapUnits == 0m ? 0m : MfgSql.Quantity(mixScrapUnits, "The mix scrap");
        var freshScrap = freshScrapUnits == 0m ? 0m : MfgSql.Quantity(freshScrapUnits, "The fresh scrap");
        if (run.Status != "IN_PROGRESS")
        {
            throw new DomainException(ManufacturingErrors.InvalidState, $"The run is {run.Status}.");
        }

        var recipe = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT l.material_item_id, l.qty_per_batch, i.base_uom FROM mfg.recipe_line l JOIN md.item i ON i.item_id = l.material_item_id WHERE l.recipe_version_id = @r",
            r => new RecipeLine(r.GetGuid(0), r.GetDecimal(1), r.GetString(2)),
            cancellationToken,
            ("r", run.RecipeVersionId)).ConfigureAwait(false);
        if (consumption.Select(c => c.MaterialItemId).Distinct().Count() != consumption.Count
            || !consumption.Select(c => c.MaterialItemId).ToHashSet().SetEquals(recipe.Select(r => r.MaterialItemId)))
        {
            throw new DomainException(ManufacturingErrors.MaterialsMismatch, "Record the real consumption of each material of the recipe, once each.");
        }

        var unitsPerBatch = provenance.UnitsTheoretical
            ? await MfgSql.ScalarAsync<decimal>(context, "SELECT units_per_batch FROM mfg.recipe_version WHERE recipe_version_id = @r", cancellationToken, ("r", run.RecipeVersionId))
                .ConfigureAwait(false)
            : 0m;
        var units = good + mixScrap + freshScrap;
        var lines = new List<(Guid Material, Guid Location, decimal Entered, string Uom, decimal Qty, decimal Theoretical)>();
        foreach (var input in consumption)
        {
            var line = recipe.Single(r => r.MaterialItemId == input.MaterialItemId);
            var entered = MfgSql.Quantity(input.Quantity, "The consumed quantity");
            if (await MfgSql.ScalarAsync<Guid?>(
                    context, "SELECT location_id FROM md.location WHERE company_id = @c AND location_id = @l AND plant_id = @p AND NOT is_transit AND NOT is_curing", cancellationToken,
                    ("c", context.CompanyId), ("l", input.LocationId), ("p", run.PlantId)).ConfigureAwait(false) is null)
            {
                throw new DomainException(ManufacturingErrors.LocationInvalid, "Materials are consumed from a stock location of the run's plant.");
            }

            var uom = (input.Uom ?? string.Empty).Trim();
            var qty = await ToBaseAsync(context, input.MaterialItemId, entered, uom, line.BaseUom, run.BusinessDate, cancellationToken).ConfigureAwait(false);
            var theoretical = provenance.UnitsTheoretical
                ? decimal.Round(line.QtyPerBatch * units / unitsPerBatch, 6, MidpointRounding.AwayFromZero)
                : line.QtyPerBatch * batches;
            lines.Add((input.MaterialItemId, input.LocationId, entered, uom, qty, theoretical));
        }

        var recorder = await MfgSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var existing = await Runs.LiveSummaryAsync(context, run.RunId, cancellationToken).ConfigureAwait(false);
        if (existing is { Status: "POSTED" })
        {
            throw new DomainException(ManufacturingErrors.InvalidState, "The run's summary is already POSTED.");
        }

        // A command that writes one summary gives it its result reference; one that writes several (the portal) passes fresh ids.
        var summaryId = existing?.SummaryId ?? newSummaryId ?? context.ResultRef;
        var version = (existing?.Version ?? 0) + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ShiftSummaryRecorded",
                1,
                Runs.SummaryAggregate,
                summaryId,
                version,
                JsonSerializer.Serialize(new
                {
                    summaryId,
                    runId = run.RunId,
                    batches,
                    goodUnits = Runs.Qty(good),
                    mixScrapUnits = Runs.Qty(mixScrap),
                    freshScrapUnits = Runs.Qty(freshScrap),
                    consumption = lines.Select(l => new { materialItemId = l.Material, locationId = l.Location, quantity = Runs.Qty(l.Entered), uom = l.Uom, baseQuantity = Runs.Qty(l.Qty) }),
                    source = provenance.Source,
                    portalReadingId = provenance.ReadingId,
                    consumptionSource = provenance.ConsumptionSource,
                    consumptionReason = provenance.ConsumptionReason,
                }),
                Publish: true,
                BusinessDate: run.BusinessDate),
            cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO mfg.shift_summary (summary_id, company_id, run_id, batches, good_units, mix_scrap_units, fresh_scrap_units, status, recorded_by, version,
                                               source, portal_reading_id, edited_by, consumption_source, consumption_reason, portal_consumption_id)
                VALUES (@id, @c, @r, @b, @g, @mix, @fresh, 'DRAFT', @by, 1, @src, @reading, @edited, @csrc, @creason, @cid)
                """,
                cancellationToken,
                ("id", summaryId),
                ("c", context.CompanyId),
                ("r", run.RunId),
                ("b", batches),
                ("g", good),
                ("mix", mixScrap),
                ("fresh", freshScrap),
                ("by", recorder),
                ("src", provenance.Source),
                ("reading", (object?)provenance.ReadingId ?? DBNull.Value),
                ("edited", (object?)provenance.EditedBy ?? DBNull.Value),
                ("csrc", provenance.ConsumptionSource),
                ("creason", (object?)provenance.ConsumptionReason ?? DBNull.Value),
                ("cid", (object?)provenance.ConsumptionId ?? DBNull.Value)).ConfigureAwait(false);
            await context.AppendStateAsync(Runs.SummaryAggregate, summaryId, "DOCUMENT", null, "DRAFT", commandType, eventId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                UPDATE mfg.shift_summary SET batches = @b, good_units = @g, mix_scrap_units = @mix, fresh_scrap_units = @fresh, version = @v,
                  portal_reading_id = coalesce(@reading, portal_reading_id), edited_by = coalesce(@edited, edited_by), consumption_source = @csrc,
                  consumption_reason = @creason, portal_consumption_id = @cid
                WHERE summary_id = @id
                """,
                cancellationToken,
                ("b", batches),
                ("g", good),
                ("mix", mixScrap),
                ("fresh", freshScrap),
                ("v", version),
                ("reading", (object?)provenance.ReadingId ?? DBNull.Value),
                ("edited", (object?)provenance.EditedBy ?? DBNull.Value),
                ("csrc", provenance.ConsumptionSource),
                ("creason", (object?)provenance.ConsumptionReason ?? DBNull.Value),
                ("cid", (object?)provenance.ConsumptionId ?? DBNull.Value),
                ("id", summaryId)).ConfigureAwait(false);
            await Sql.ExecuteAsync(context.Connection, context.Transaction, "DELETE FROM mfg.material_consumption WHERE summary_id = @id", cancellationToken, ("id", summaryId)).ConfigureAwait(false);
        }

        foreach (var (material, location, entered, uom, qty, theoretical) in lines)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO mfg.material_consumption (summary_id, company_id, material_item_id, location_id, entered_qty, entered_uom, qty, theoretical_qty)
                VALUES (@s, @c, @m, @l, @e, @u, @q, @t)
                """,
                cancellationToken,
                ("s", summaryId),
                ("c", context.CompanyId),
                ("m", material),
                ("l", location),
                ("e", entered),
                ("u", uom),
                ("q", qty),
                ("t", theoretical)).ConfigureAwait(false);
        }

        return (summaryId, version, existing is not null);
    }

    /// <summary>A quantity in the material's base unit: as is, or through its conversion in force on the business date (6 decimals).</summary>
    public static async Task<decimal> ToBaseAsync(CommandContext context, Guid item, decimal entered, string uom, string baseUom, DateOnly date, CancellationToken cancellationToken)
    {
        if (uom == baseUom)
        {
            return entered;
        }

        var factor = await MfgSql.ScalarAsync<decimal?>(
            context,
            """
            SELECT factor FROM md.uom_conversion
            WHERE company_id = @c AND item_id = @i AND from_uom = @u AND to_uom = @b AND effective_from <= @d AND (effective_to IS NULL OR effective_to > @d)
            """,
            cancellationToken,
            ("c", context.CompanyId),
            ("i", item),
            ("u", uom),
            ("b", baseUom),
            ("d", date)).ConfigureAwait(false)
            ?? throw new DomainException(ManufacturingErrors.UomNotConvertible, $"Unit {uom} is neither the base unit ({baseUom}) nor has a conversion in force for the material.");
        var qty = decimal.Round(entered * factor, 6, MidpointRounding.AwayFromZero);
        return qty == 0m ? throw new DomainException(ManufacturingErrors.QuantityInvalid, "The consumed quantity rounds to zero in the base unit.") : qty;
    }
}
