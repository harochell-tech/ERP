using System.Globalization;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Manufacturing.Recipes;

internal static class Recipes
{
    public const string Aggregate = "Recipe";

    public static string Qty(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}

[RequiresPermission("recipe:prepare")]
public sealed class PrepareRecipeHandler : ICommandHandler<PrepareRecipe>
{
    public string CommandType => "Manufacturing.PrepareRecipe";

    private sealed record MachineRow(Guid Plant, string Status);

    public async Task<string> HandleAsync(PrepareRecipe command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var perBatch = MfgSql.Quantity(command.UnitsPerBatch, "The units per batch");
        var perCycle = MfgSql.Quantity(command.UnitsPerCycle, "The units per cycle");
        var perRack = MfgSql.Quantity(command.UnitsPerRack, "The units per rack");
        if (command.MinCuringHours < 1 || command.MaxCuringHours <= command.MinCuringHours)
        {
            throw new DomainException(ManufacturingErrors.FieldInvalid, "The curing window needs 1 ≤ minimum hours < maximum hours (E-UX4-9).");
        }

        var lines = command.Lines ?? [];
        if (lines.Count == 0)
        {
            throw new DomainException(ManufacturingErrors.LinesRequired, "A recipe has at least one material.");
        }

        if (lines.Select(l => l.MaterialItemId).Distinct().Count() != lines.Count)
        {
            throw new DomainException(ManufacturingErrors.DuplicateLine, "Each material appears once in a recipe.");
        }

        if (await MfgSql.ScalarAsync<string>(
                context, "SELECT status::text FROM md.item WHERE company_id = @c AND item_id = @i AND item_type = 'FINISHED_GOOD'", cancellationToken,
                ("c", context.CompanyId), ("i", command.ItemId)).ConfigureAwait(false) != "ACTIVE")
        {
            throw new DomainException(ManufacturingErrors.NotFinishedGood, "A recipe is of an ACTIVE finished good.");
        }

        var machine = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT plant_id, status FROM md.machine WHERE company_id = @c AND machine_id = @m",
            r => new MachineRow(r.GetGuid(0), r.GetString(1)),
            cancellationToken,
            ("c", context.CompanyId),
            ("m", command.MachineId)).ConfigureAwait(false)
            ?? throw new DomainException(ManufacturingErrors.NotFound, "The machine does not exist.");

        if (machine.Plant != command.PlantId)
        {
            throw new DomainException(ManufacturingErrors.PlantMismatch, "The machine belongs to another plant.");
        }

        if (machine.Status != "ACTIVE")
        {
            throw new DomainException(ManufacturingErrors.MachineNotActive, "The machine is INACTIVE.");
        }

        var normalized = new List<(Guid Material, decimal Qty)>();
        foreach (var line in lines)
        {
            if (await MfgSql.ScalarAsync<string>(
                    context, "SELECT status::text FROM md.item WHERE company_id = @c AND item_id = @i AND item_type = 'RAW_MATERIAL'", cancellationToken,
                    ("c", context.CompanyId), ("i", line.MaterialItemId)).ConfigureAwait(false) != "ACTIVE")
            {
                throw new DomainException(ManufacturingErrors.NotRawMaterial, $"Material {line.MaterialItemId} is not an ACTIVE raw material.");
            }

            normalized.Add((line.MaterialItemId, MfgSql.Quantity(line.QtyPerBatch, "The quantity per batch")));
        }

        await MfgSql.LockAsync(context, $"recipe:{command.ItemId}:{command.MachineId}", cancellationToken).ConfigureAwait(false);
        var preparer = await MfgSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var version = (await MfgSql.ScalarAsync<int?>(
            context, "SELECT max(version) FROM mfg.recipe_version WHERE company_id = @c AND item_id = @i AND machine_id = @m", cancellationToken,
            ("c", context.CompanyId), ("i", command.ItemId), ("m", command.MachineId)).ConfigureAwait(false) ?? 0) + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "RecipePrepared",
                1,
                Recipes.Aggregate,
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new
                {
                    recipeVersionId = context.ResultRef,
                    itemId = command.ItemId,
                    machineId = command.MachineId,
                    version,
                    unitsPerBatch = Recipes.Qty(perBatch),
                    unitsPerCycle = Recipes.Qty(perCycle),
                    unitsPerRack = Recipes.Qty(perRack),
                    minCuringHours = command.MinCuringHours,
                    maxCuringHours = command.MaxCuringHours,
                    lines = normalized.Select(l => new { materialItemId = l.Material, qtyPerBatch = Recipes.Qty(l.Qty) }),
                }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO mfg.recipe_version (recipe_version_id, company_id, item_id, machine_id, version, effective_from, units_per_batch, units_per_cycle,
                                            units_per_rack, min_curing_hours, max_curing_hours, status, prepared_by)
            VALUES (@id, @c, @i, @m, @v, @today, @batch, @cycle, @rack, @min, @max, 'DRAFT', @by)
            """,
            cancellationToken,
            ("id", context.ResultRef),
            ("c", context.CompanyId),
            ("i", command.ItemId),
            ("m", command.MachineId),
            ("v", version),
            ("today", MfgSql.Today(context)),
            ("batch", perBatch),
            ("cycle", perCycle),
            ("rack", perRack),
            ("min", command.MinCuringHours),
            ("max", command.MaxCuringHours),
            ("by", preparer)).ConfigureAwait(false);
        foreach (var (material, qty) in normalized)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "INSERT INTO mfg.recipe_line (recipe_version_id, company_id, material_item_id, qty_per_batch) VALUES (@id, @c, @m, @q)",
                cancellationToken,
                ("id", context.ResultRef),
                ("c", context.CompanyId),
                ("m", material),
                ("q", qty)).ConfigureAwait(false);
        }

        await context.AppendStateAsync(Recipes.Aggregate, context.ResultRef, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { recipeVersionId = context.ResultRef, version, status = "DRAFT", lines = normalized.Count });
    }
}

[RequiresPermission("recipe:approve")]
public sealed class ApproveRecipeHandler : ICommandHandler<ApproveRecipe>
{
    public string CommandType => "Manufacturing.ApproveRecipe";

    private sealed record Row(Guid ItemId, Guid MachineId, Guid PlantId, int Version, string Status, Guid PreparedBy);

    private static Task<Row?> ReadAsync(CommandContext context, Guid id, CancellationToken cancellationToken)
        => Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT v.item_id, v.machine_id, m.plant_id, v.version, v.status, v.prepared_by
            FROM mfg.recipe_version v JOIN md.machine m ON m.machine_id = v.machine_id
            WHERE v.company_id = @c AND v.recipe_version_id = @id
            """,
            r => new Row(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetInt32(3), r.GetString(4), r.GetGuid(5)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", id));

    public async Task<string> HandleAsync(ApproveRecipe command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var key = await ReadAsync(context, command.RecipeVersionId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(ManufacturingErrors.NotFound, "The recipe version does not exist.");
        if (key.PlantId != command.PlantId)
        {
            throw new DomainException(ManufacturingErrors.PlantMismatch, "The recipe is of a machine of another plant.");
        }

        await MfgSql.LockAsync(context, $"recipe:{key.ItemId}:{key.MachineId}", cancellationToken).ConfigureAwait(false);
        var row = (await ReadAsync(context, command.RecipeVersionId, cancellationToken).ConfigureAwait(false))!;
        if (row.Status != "DRAFT")
        {
            throw new DomainException(ManufacturingErrors.InvalidState, $"The recipe is {row.Status}.");
        }

        var approver = await MfgSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (approver == row.PreparedBy && !await ControlWaiver.WaivedAsync(context, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(ManufacturingErrors.FourEyes, "A recipe is approved by someone other than who prepared it.");
        }

        var previous = await MfgSql.ScalarAsync<Guid?>(
            context,
            "SELECT recipe_version_id FROM mfg.recipe_version WHERE company_id = @c AND item_id = @i AND machine_id = @m AND status = 'ACTIVE'",
            cancellationToken,
            ("c", context.CompanyId),
            ("i", row.ItemId),
            ("m", row.MachineId)).ConfigureAwait(false);
        var today = MfgSql.Today(context);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "RecipeApproved",
                1,
                Recipes.Aggregate,
                command.RecipeVersionId,
                await MfgSql.NextEventVersionAsync(context, Recipes.Aggregate, command.RecipeVersionId, cancellationToken).ConfigureAwait(false),
                JsonSerializer.Serialize(new { recipeVersionId = command.RecipeVersionId, itemId = row.ItemId, machineId = row.MachineId, version = row.Version, effectiveFrom = today, supersedes = previous }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        if (previous is { } old)
        {
            await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE mfg.recipe_version SET status = 'SUPERSEDED' WHERE recipe_version_id = @id", cancellationToken, ("id", old)).ConfigureAwait(false);
            await context.AppendStateAsync(Recipes.Aggregate, old, "DOCUMENT", "ACTIVE", "SUPERSEDED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        }

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE mfg.recipe_version SET status = 'ACTIVE', approved_by = @by, effective_from = @today WHERE recipe_version_id = @id",
            cancellationToken,
            ("by", approver),
            ("today", today),
            ("id", command.RecipeVersionId)).ConfigureAwait(false);
        await context.AppendStateAsync(Recipes.Aggregate, command.RecipeVersionId, "DOCUMENT", "DRAFT", "ACTIVE", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { recipeVersionId = command.RecipeVersionId, status = "ACTIVE", supersedes = previous });
    }
}
