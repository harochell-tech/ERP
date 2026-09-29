using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Manufacturing.Queries;

// E-MFG1-02-9: the production master data, read with production:read.

public sealed record ListMachines(Guid CompanyId, Guid SessionId, Guid? PlantId = null, string? Status = null) : IQuery;

public sealed record MachineView(Guid MachineId, Guid PlantId, string PlantCode, string Code, string Name, string Status, long Version);

public sealed record MachineList(IReadOnlyList<MachineView> Items);

[RequiresPermission("production:read")]
public sealed class ListMachinesHandler : IQueryHandler<ListMachines>
{
    public string QueryType => "Manufacturing.ListMachines";

    public async Task<string> HandleAsync(ListMachines query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT m.machine_id, m.plant_id, p.code, m.code, m.name, m.status, m.version
            FROM md.machine m JOIN md.plant p ON p.plant_id = m.plant_id
            WHERE m.company_id = @c AND (CAST(@p AS uuid) IS NULL OR m.plant_id = CAST(@p AS uuid)) AND (CAST(@s AS text) IS NULL OR m.status = CAST(@s AS text))
            ORDER BY p.code, m.code
            """,
            r => new MachineView(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt64(6)),
            cancellationToken,
            ("c", context.CompanyId),
            ("p", query.PlantId),
            ("s", query.Status)).ConfigureAwait(false);
        return ApiJson.Serialize(new MachineList(items));
    }
}

public sealed record ListShifts(Guid CompanyId, Guid SessionId, Guid? PlantId = null, string? Status = null) : IQuery;

/// <summary><c>StartsAt</c> / <c>EndsAt</c> as HH:mm; <c>CrossesMidnight</c> when the shift ends the next day.</summary>
public sealed record ShiftView(Guid ShiftId, Guid PlantId, string PlantCode, string Code, string StartsAt, string EndsAt, bool CrossesMidnight, string Status, long Version);

public sealed record ShiftList(IReadOnlyList<ShiftView> Items);

[RequiresPermission("production:read")]
public sealed class ListShiftsHandler : IQueryHandler<ListShifts>
{
    public string QueryType => "Manufacturing.ListShifts";

    public async Task<string> HandleAsync(ListShifts query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT s.shift_id, s.plant_id, p.code, s.code, to_char(s.starts_at, 'HH24:MI'), to_char(s.ends_at, 'HH24:MI'), s.ends_at < s.starts_at, s.status, s.version
            FROM mfg.shift s JOIN md.plant p ON p.plant_id = s.plant_id
            WHERE s.company_id = @c AND (CAST(@p AS uuid) IS NULL OR s.plant_id = CAST(@p AS uuid)) AND (CAST(@s AS text) IS NULL OR s.status = CAST(@s AS text))
            ORDER BY p.code, s.starts_at
            """,
            r => new ShiftView(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetBoolean(6), r.GetString(7), r.GetInt64(8)),
            cancellationToken,
            ("c", context.CompanyId),
            ("p", query.PlantId),
            ("s", query.Status)).ConfigureAwait(false);
        return ApiJson.Serialize(new ShiftList(items));
    }
}

public sealed record ListRecipes(Guid CompanyId, Guid SessionId, Guid? PlantId = null, Guid? ItemId = null, string? Status = null) : IQuery;

public sealed record RecipeSummary(
    Guid RecipeVersionId, Guid ItemId, string ItemCode, string ItemName, Guid MachineId, string MachineCode, Guid PlantId, int Version, DateOnly EffectiveFrom,
    decimal UnitsPerBatch, decimal UnitsPerCycle, decimal UnitsPerRack, int MinCuringHours, int MaxCuringHours, string Status, Guid PreparedBy, Guid? ApprovedBy);

public sealed record RecipeList(IReadOnlyList<RecipeSummary> Items);

internal static class RecipeSql
{
    public const string Select = """
        SELECT v.recipe_version_id, v.item_id, i.code, i.description, v.machine_id, m.code, m.plant_id, v.version, v.effective_from,
               v.units_per_batch, v.units_per_cycle, v.units_per_rack, v.min_curing_hours, v.max_curing_hours, v.status, v.prepared_by, v.approved_by
        FROM mfg.recipe_version v
        JOIN md.item i ON i.item_id = v.item_id
        JOIN md.machine m ON m.machine_id = v.machine_id
        """;

    public static RecipeSummary Map(System.Data.Common.DbDataReader r)
        => new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetGuid(4), r.GetString(5), r.GetGuid(6), r.GetInt32(7), r.Date(8),
            r.GetDecimal(9), r.GetDecimal(10), r.GetDecimal(11), r.GetInt32(12), r.GetInt32(13), r.GetString(14), r.GetGuid(15), r.IsDBNull(16) ? null : r.GetGuid(16));
}

[RequiresPermission("production:read")]
public sealed class ListRecipesHandler : IQueryHandler<ListRecipes>
{
    public string QueryType => "Manufacturing.ListRecipes";

    public async Task<string> HandleAsync(ListRecipes query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            RecipeSql.Select + """

            WHERE v.company_id = @c AND (CAST(@p AS uuid) IS NULL OR m.plant_id = CAST(@p AS uuid)) AND (CAST(@i AS uuid) IS NULL OR v.item_id = CAST(@i AS uuid))
              AND (CAST(@s AS text) IS NULL OR v.status = CAST(@s AS text))
            ORDER BY i.code, m.code, v.version DESC
            """,
            RecipeSql.Map,
            cancellationToken,
            ("c", context.CompanyId),
            ("p", query.PlantId),
            ("i", query.ItemId),
            ("s", query.Status)).ConfigureAwait(false);
        return ApiJson.Serialize(new RecipeList(items));
    }
}

public sealed record GetRecipe(Guid CompanyId, Guid SessionId, Guid RecipeVersionId) : IQuery;

public sealed record RecipeLineView(Guid MaterialItemId, string MaterialCode, string MaterialName, string BaseUom, decimal QtyPerBatch);

public sealed record RecipeDetail(RecipeSummary Recipe, IReadOnlyList<RecipeLineView> Lines);

[RequiresPermission("production:read")]
public sealed class GetRecipeHandler : IQueryHandler<GetRecipe>
{
    public string QueryType => "Manufacturing.GetRecipe";

    public async Task<string> HandleAsync(GetRecipe query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var recipe = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            RecipeSql.Select + "\nWHERE v.company_id = @c AND v.recipe_version_id = @id",
            RecipeSql.Map,
            cancellationToken,
            ("c", context.CompanyId),
            ("id", query.RecipeVersionId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The recipe does not exist.");
        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.material_item_id, i.code, i.description, i.base_uom, l.qty_per_batch
            FROM mfg.recipe_line l JOIN md.item i ON i.item_id = l.material_item_id
            WHERE l.recipe_version_id = @id ORDER BY i.code
            """,
            r => new RecipeLineView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDecimal(4)),
            cancellationToken,
            ("id", query.RecipeVersionId)).ConfigureAwait(false);
        return ApiJson.Serialize(new RecipeDetail(recipe, lines));
    }
}
