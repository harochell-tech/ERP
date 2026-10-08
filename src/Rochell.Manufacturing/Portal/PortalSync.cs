using System.Globalization;
using System.Text.Json;
using Rochell.Manufacturing.Runs;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Manufacturing.Portal;

/// <summary>
/// E-MFG2-4/5/6/8/11, E-MFG2-01-1…8: brings one group up to date — the machines a batch plant feeds (or an offline machine alone) in a
/// date and portal shift. Per machine and product (mould) with blocks: the run's DRAFT summary with units = blocks; batches from the
/// batch plant's post split by units, else units ÷ the recipe's units per batch rounded up; consumption from the batch plant's latest
/// post split among the group's runs by their theoretical consumption once every machine's shift has ended, else the theoretical as a
/// PENDING placeholder. A draft a person changed, or a posted summary, is left alone. Runs that do not exist yet are returned for the
/// caller to open (StartProductionRun) before calling again.
/// </summary>
public sealed record SyncPortalShift(Guid CompanyId, Guid SessionId, string IdempotencyKey, DateOnly Date, int ShiftNo, string Group) : ICommand;

public sealed record PortalRunNeeded(Guid PlantId, Guid MachineId, Guid ShiftId, DateOnly Date, Guid ItemId);

public sealed record PortalSyncResult(int Written, int Unchanged, int Kept, IReadOnlyList<PortalRunNeeded> RunsNeeded, IReadOnlyList<string> Warnings);

[RequiresPermission("shift_summary:record")]
public sealed class SyncPortalShiftHandler : ICommandHandler<SyncPortalShift>
{
    public string CommandType => "Manufacturing.SyncPortalShift";

    private sealed record Machine(string Code, Guid MachineId, Guid PlantId, string? BatchPlant);

    private sealed record ShiftReading(Guid ReadingId, bool Closed, IReadOnlyList<PortalMould> Moulds);

    private sealed record Target(Machine Machine, Guid ReadingId, Guid ItemId, decimal Units, Runs.Runs.Run Run, decimal UnitsPerBatch, List<(Guid Item, decimal PerUnit, string BaseUom)> Recipe);

    public async Task<string> HandleAsync(SyncPortalShift command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var group = command.Group.Trim().ToLowerInvariant();
        var warnings = new List<string>();
        var label = $"{command.Date:yyyy-MM-dd} turno {command.ShiftNo}";
        var machines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT p.portal_code, p.machine_id, m.plant_id, p.batch_plant FROM mfg.portal_machine p JOIN md.machine m ON m.machine_id = p.machine_id
            WHERE p.company_id = @c AND (p.batch_plant = @g OR (p.batch_plant IS NULL AND p.portal_code = @g)) ORDER BY p.portal_code
            """,
            r => new Machine(r.GetString(0), r.GetGuid(1), r.GetGuid(2), r.NullableString(3)),
            cancellationToken,
            ("c", context.CompanyId),
            ("g", group)).ConfigureAwait(false);
        var shiftId = await MfgSql.ScalarAsync<Guid?>(
            context, "SELECT shift_id FROM mfg.portal_shift WHERE company_id = @c AND shift_no = @n", cancellationToken, ("c", context.CompanyId), ("n", (short)command.ShiftNo))
            .ConfigureAwait(false);
        if (machines.Count == 0 || shiftId is null)
        {
            warnings.Add(machines.Count == 0 ? $"«{group}» no tiene máquinas emparejadas." : $"El turno {command.ShiftNo} del portal no está emparejado con un turno de Core.");
            await SyncState.AddWarningsAsync(context, warnings, cancellationToken).ConfigureAwait(false);
            return Result(0, 0, 0, [], warnings);
        }

        // The group's latest readings, products and runs.
        var targets = new List<Target>();
        var needed = new List<PortalRunNeeded>();
        var allClosed = true;
        foreach (var machine in machines)
        {
            var reading = (await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                "SELECT reading_id, closed, moulds::text FROM mfg.portal_reading WHERE company_id = @c AND portal_code = @p AND shift_date = @d AND shift_no = @n ORDER BY fetched_at DESC LIMIT 1",
                r => new ShiftReading(r.GetGuid(0), r.GetBoolean(1), JsonSerializer.Deserialize<List<PortalMould>>(r.GetString(2), PortalJson.Options)!),
                cancellationToken,
                ("c", context.CompanyId),
                ("p", machine.Code),
                ("d", command.Date),
                ("n", (short)command.ShiftNo)).ConfigureAwait(false)).SingleOrDefault();
            if (reading is null)
            {
                allClosed = false;
                continue;
            }

            allClosed &= reading.Closed;
            foreach (var mould in reading.Moulds.Where(m => m.Blocks > 0))
            {
                var item = await MfgSql.ScalarAsync<Guid?>(
                    context, "SELECT item_id FROM mfg.portal_mould WHERE company_id = @c AND mould = @m", cancellationToken, ("c", context.CompanyId), ("m", mould.Mould)).ConfigureAwait(false);
                if (item is null)
                {
                    warnings.Add($"El molde {mould.Mould}\" del portal no está emparejado con un producto: {mould.Blocks} bloques de {machine.Code} ({label}) sin importar.");
                    continue;
                }

                var runId = await MfgSql.ScalarAsync<Guid?>(
                    context,
                    "SELECT run_id FROM mfg.production_run WHERE company_id = @c AND machine_id = @m AND shift_id = @s AND business_date = @d AND item_id = @i AND status <> 'CANCELLED'",
                    cancellationToken,
                    ("c", context.CompanyId),
                    ("m", machine.MachineId),
                    ("s", shiftId.Value),
                    ("d", command.Date),
                    ("i", item.Value)).ConfigureAwait(false);
                if (runId is null)
                {
                    needed.Add(new PortalRunNeeded(machine.PlantId, machine.MachineId, shiftId.Value, command.Date, item.Value));
                    continue;
                }

                var run = await Runs.Runs.LockAsync(context, machine.PlantId, runId.Value, cancellationToken).ConfigureAwait(false);
                var unitsPerBatch = await MfgSql.ScalarAsync<decimal>(
                    context, "SELECT units_per_batch FROM mfg.recipe_version WHERE recipe_version_id = @r", cancellationToken, ("r", run.RecipeVersionId)).ConfigureAwait(false);
                var recipe = await Reading.ListAsync(
                    context.Connection,
                    context.Transaction,
                    "SELECT l.material_item_id, l.qty_per_batch, i.base_uom FROM mfg.recipe_line l JOIN md.item i ON i.item_id = l.material_item_id WHERE l.recipe_version_id = @r",
                    r => (Item: r.GetGuid(0), PerUnit: r.GetDecimal(1) / unitsPerBatch, BaseUom: r.GetString(2)),
                    cancellationToken,
                    ("r", run.RecipeVersionId)).ConfigureAwait(false);
                targets.Add(new Target(machine, reading.ReadingId, item.Value, mould.Blocks, run, unitsPerBatch, recipe));
            }
        }

        // The batch plant's latest post for the shift, usable once every machine of the group ended its shift (E-MFG2-5).
        var batchPlant = machines[0].BatchPlant;
        (Guid Id, int? Batches, List<PortalMaterial> Materials)? post = null;
        if (batchPlant is not null && allClosed)
        {
            post = (await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT consumption_id, batches, materials::text FROM mfg.portal_consumption
                WHERE company_id = @c AND batch_plant = @bp AND shift_date = @d AND shift_no = @n ORDER BY portal_id DESC LIMIT 1
                """,
                r => (r.GetGuid(0), r.IsDBNull(1) ? (int?)null : r.GetInt32(1), JsonSerializer.Deserialize<List<PortalMaterial>>(r.GetString(2), PortalJson.Options)!),
                cancellationToken,
                ("c", context.CompanyId),
                ("bp", batchPlant),
                ("d", command.Date),
                ("n", (short)command.ShiftNo)).ConfigureAwait(false)).Cast<(Guid, int?, List<PortalMaterial>)?>().SingleOrDefault();
        }

        // Real consumption per run and material from the post (base unit), split by each run's theoretical consumption.
        var real = new Dictionary<(Guid Run, Guid Item), decimal>();
        var splitOk = post is not null;
        if (post is { } p)
        {
            var mapped = await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                "SELECT material_code, item_id, uom FROM mfg.portal_material WHERE company_id = @c AND batch_plant = @bp",
                r => (Code: r.GetString(0), Item: r.GetGuid(1), Uom: r.GetString(2)),
                cancellationToken,
                ("c", context.CompanyId),
                ("bp", batchPlant!)).ConfigureAwait(false);
            foreach (var material in p.Materials.Where(m => mapped.All(x => x.Code != m.Code)))
            {
                warnings.Add($"El material «{material.Code}» de {batchPlant} no está emparejado con un artículo: no se usa.");
            }

            foreach (var item in targets.SelectMany(t => t.Recipe.Select(r => r.Item)).Distinct())
            {
                var map = mapped.FirstOrDefault(m => m.Item == item);
                var material = map == default ? null : p.Materials.FirstOrDefault(m => m.Code == map.Code);
                if (material is null)
                {
                    warnings.Add($"{batchPlant} ({label}) no envió un material de la receta: el consumo queda pendiente.");
                    splitOk = false;
                    break;
                }

                if (!string.Equals(material.Unit, map.Uom, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"{batchPlant} envió {material.Code} en «{material.Unit}» y está emparejado en «{map.Uom}»: el consumo queda pendiente.");
                    splitOk = false;
                    break;
                }

                var total = decimal.Parse(material.Quantity, NumberStyles.Number, CultureInfo.InvariantCulture);
                var users = targets.Where(t => t.Recipe.Any(r => r.Item == item)).ToList();
                var theoretical = users.Select(t => t.Units * t.Recipe.Single(r => r.Item == item).PerUnit).ToList();
                var sum = theoretical.Sum();
                var left = total;
                for (var i = 0; i < users.Count; i++)
                {
                    var share = i == users.Count - 1 ? left : decimal.Round(sum == 0m ? 0m : total * theoretical[i] / sum, 6, MidpointRounding.AwayFromZero);
                    left -= share;
                    var baseUom = users[i].Recipe.Single(r => r.Item == item).BaseUom;
                    var qty = share <= 0m ? 0m : await ShiftSummaryWriter.ToBaseAsync(context, item, share, map.Uom, baseUom, command.Date, cancellationToken).ConfigureAwait(false);
                    real[(users[i].Run.RunId, item)] = qty;
                }
            }

            if (splitOk && real.Values.Any(q => q <= 0m))
            {
                warnings.Add($"{batchPlant} ({label}) envió un material en cero: el consumo queda pendiente.");
                splitOk = false;
            }
        }

        // E-MFG3-2, E-MFG3-01-4: the daily report's broken blocks of a machine with a single shift that day, split among its products by
        // units (the last takes the remainder); with two shifts they are split by hand in Core.
        var broken = new Dictionary<Guid, decimal>();
        foreach (var machine in targets.Select(t => t.Machine).DistinctBy(m => m.Code))
        {
            var reported = (await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                "SELECT broken_units FROM mfg.portal_daily_report WHERE company_id = @c AND portal_code = @p AND report_date = @d ORDER BY fetched_at DESC LIMIT 1",
                r => r.IsDBNull(0) ? (int?)null : r.GetInt32(0),
                cancellationToken,
                ("c", context.CompanyId),
                ("p", machine.Code),
                ("d", command.Date)).ConfigureAwait(false)).SingleOrDefault();
            if (reported is not { } units || units == 0)
            {
                continue;
            }

            var shifts = await MfgSql.ScalarAsync<long>(
                context, "SELECT count(DISTINCT shift_no) FROM mfg.portal_reading WHERE company_id = @c AND portal_code = @p AND shift_date = @d", cancellationToken,
                ("c", context.CompanyId), ("p", machine.Code), ("d", command.Date)).ConfigureAwait(false);
            var own = targets.Where(t => t.Machine.Code == machine.Code).ToList();
            var produced = own.Sum(t => t.Units);
            if (shifts > 1)
            {
                warnings.Add($"{machine.Code} ({command.Date:yyyy-MM-dd}): {units} rotos del reporte con dos turnos ese día; repártalos a mano en Core.");
                continue;
            }

            if (units >= produced)
            {
                warnings.Add($"{machine.Code} ({command.Date:yyyy-MM-dd}): el reporte dice {units} rotos, más que los {produced} bloques del día; no se pasan al borrador.");
                continue;
            }

            var left = (decimal)units;
            for (var k = 0; k < own.Count; k++)
            {
                var share = k == own.Count - 1 ? left : decimal.Round(units * own[k].Units / produced, 0, MidpointRounding.AwayFromZero);
                left -= share;
                broken[own[k].Run.RunId] = share;
            }
        }

        // The drafts.
        var written = 0;
        var unchanged = 0;
        var kept = 0;
        var totalUnits = targets.Sum(t => t.Units);
        var batchesLeft = post is { Batches: { } b } ? b : 0;
        for (var i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            var current = await ShiftSummaryWriter.ProvenanceAsync(context, t.Run.RunId, cancellationToken).ConfigureAwait(false);
            var live = await Runs.Runs.LiveSummaryAsync(context, t.Run.RunId, cancellationToken).ConfigureAwait(false);
            if (live is { Status: "POSTED" } || current?.EditedBy is not null || current?.Source == "MANUAL" || t.Run.Status != "IN_PROGRESS")
            {
                kept++;
                continue;
            }

            int batches;
            if (splitOk && post is { Batches: { } postBatches } && postBatches > 0)
            {
                batches = i == targets.Count - 1 ? batchesLeft : (int)decimal.Round(postBatches * t.Units / totalUnits, 0, MidpointRounding.AwayFromZero);
                batchesLeft -= batches;
                batches = Math.Max(1, batches);
            }
            else
            {
                batches = (int)Math.Ceiling(t.Units / t.UnitsPerBatch);
            }

            var consumption = new List<ConsumptionInput>();
            var missingLocation = false;
            foreach (var (item, perUnit, baseUom) in t.Recipe)
            {
                var location = await MfgSql.ScalarAsync<Guid?>(
                    context,
                    """
                    SELECT pm.location_id FROM mfg.portal_material pm JOIN md.location l ON l.location_id = pm.location_id
                    WHERE pm.company_id = @c AND pm.item_id = @i AND l.plant_id = @p ORDER BY (pm.batch_plant = @bp) DESC, pm.batch_plant LIMIT 1
                    """,
                    cancellationToken,
                    ("c", context.CompanyId),
                    ("i", item),
                    ("p", t.Run.PlantId),
                    ("bp", (object?)batchPlant ?? DBNull.Value)).ConfigureAwait(false);
                if (location is null)
                {
                    missingLocation = true;
                    break;
                }

                var qty = splitOk ? real[(t.Run.RunId, item)] : decimal.Round(perUnit * t.Units, 6, MidpointRounding.AwayFromZero);
                consumption.Add(new ConsumptionInput(item, location.Value, qty, baseUom));
            }

            if (missingLocation || consumption.Any(c => c.Quantity <= 0m))
            {
                warnings.Add($"{t.Machine.Code} ({label}): un material de la receta no tiene ubicación de descarga emparejada en Producción › Portal.");
                continue;
            }

            var fresh = broken.GetValueOrDefault(t.Run.RunId);
            var provenance = new SummaryProvenance(
                "PORTAL", t.ReadingId, null, splitOk ? "BATCH_PLANT" : "PENDING", null, splitOk ? post!.Value.Id : null);
            if (live is { Status: "DRAFT" } && current is not null && current.ReadingId == t.ReadingId && current.ConsumptionSource == provenance.ConsumptionSource
                && current.ConsumptionId == provenance.ConsumptionId && live.Batches == batches && live.GoodUnits == t.Units - fresh && live.FreshScrap == fresh
                && await ShiftSummaryWriter.SameConsumptionAsync(context, t.Run.RunId, consumption, cancellationToken).ConfigureAwait(false))
            {
                unchanged++;
                continue;
            }

            // Scrap: the report's broken blocks (above); a draft a person changed is never refreshed, so theirs stays.
            await ShiftSummaryWriter.WriteAsync(context, t.Run, batches, t.Units - fresh, 0m, fresh, consumption, provenance, CommandType, cancellationToken, context.Ids.NewId())
                .ConfigureAwait(false);
            written++;
        }

        await SyncState.AddWarningsAsync(context, warnings, cancellationToken).ConfigureAwait(false);
        return Result(written, unchanged, kept, needed, warnings);
    }

    private static string Result(int written, int unchanged, int kept, IReadOnlyList<PortalRunNeeded> needed, IReadOnlyList<string> warnings)
        => JsonSerializer.Serialize(new PortalSyncResult(written, unchanged, kept, needed, warnings), PortalJson.Options);
}
