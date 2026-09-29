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

// E-MFG1-03-11: production runs with their summary, consumption (real vs theoretical), lot and racks.

public sealed record ListProductionRuns(Guid CompanyId, Guid SessionId, Guid? PlantId = null, DateOnly? BusinessDate = null, string? Status = null, int Limit = 50, int Offset = 0) : IQuery;

public sealed record ProductionRunSummary(
    Guid RunId, string RunNo, Guid PlantId, string PlantCode, string MachineCode, string ShiftCode, DateOnly BusinessDate, Guid ItemId, string ItemCode, string Status,
    string? SummaryStatus, decimal? GoodUnits, string? LotCode, long Version);

public sealed record ProductionRunList(IReadOnlyList<ProductionRunSummary> Items, int Limit, int Offset);

[RequiresPermission("production:read")]
public sealed class ListProductionRunsHandler : IQueryHandler<ListProductionRuns>
{
    public string QueryType => "Manufacturing.ListProductionRuns";

    public async Task<string> HandleAsync(ListProductionRuns query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT r.run_id, r.run_no, r.plant_id, p.code, m.code, s.code, r.business_date, r.item_id, i.code, r.status,
                   ss.status, ss.good_units, l.lot_code, r.version
            FROM mfg.production_run r
            JOIN md.plant p ON p.plant_id = r.plant_id
            JOIN md.machine m ON m.machine_id = r.machine_id
            JOIN mfg.shift s ON s.shift_id = r.shift_id
            JOIN md.item i ON i.item_id = r.item_id
            LEFT JOIN mfg.shift_summary ss ON ss.run_id = r.run_id AND ss.status IN ('DRAFT', 'POSTED')
            LEFT JOIN mfg.fg_lot f ON f.summary_id = ss.summary_id
            LEFT JOIN inv.lot l ON l.lot_id = f.lot_id
            WHERE r.company_id = @c AND (CAST(@p AS uuid) IS NULL OR r.plant_id = CAST(@p AS uuid))
              AND (CAST(@d AS date) IS NULL OR r.business_date = CAST(@d AS date)) AND (CAST(@s AS text) IS NULL OR r.status = CAST(@s AS text))
            ORDER BY r.business_date DESC, r.run_no DESC
            LIMIT @limit OFFSET @offset
            """,
            r => new ProductionRunSummary(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetString(5), r.Date(6), r.GetGuid(7), r.GetString(8), r.GetString(9),
                r.NullableString(10), r.IsDBNull(11) ? null : r.GetDecimal(11), r.NullableString(12), r.GetInt64(13)),
            cancellationToken,
            ("c", context.CompanyId),
            ("p", query.PlantId),
            ("d", query.BusinessDate),
            ("s", query.Status),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new ProductionRunList(items, query.Limit, query.Offset));
    }
}

public sealed record GetProductionRun(Guid CompanyId, Guid SessionId, Guid RunId) : IQuery;

public sealed record ShiftSummaryView(Guid SummaryId, int Batches, decimal GoodUnits, decimal MixScrapUnits, decimal FreshScrapUnits, string Status, Guid RecordedBy, Guid? PostedBy, long Version);

public sealed record ConsumptionView(Guid MaterialItemId, string MaterialCode, string BaseUom, string LocationCode, decimal EnteredQty, string EnteredUom, decimal Qty, decimal TheoreticalQty, decimal Difference);

public sealed record FgLotView(Guid LotId, string LotCode, string Status, DateTime CuringFrom, DateTime ReleasableAt, int Racks);

public sealed record ProductionRunDetail(ProductionRunSummary Run, ShiftSummaryView? Summary, IReadOnlyList<ConsumptionView> Consumption, FgLotView? Lot);

[RequiresPermission("production:read")]
public sealed class GetProductionRunHandler : IQueryHandler<GetProductionRun>
{
    public string QueryType => "Manufacturing.GetProductionRun";

    public async Task<string> HandleAsync(GetProductionRun query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var run = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT r.run_id, r.run_no, r.plant_id, p.code, m.code, s.code, r.business_date, r.item_id, i.code, r.status,
                   ss.status, ss.good_units, l.lot_code, r.version
            FROM mfg.production_run r
            JOIN md.plant p ON p.plant_id = r.plant_id
            JOIN md.machine m ON m.machine_id = r.machine_id
            JOIN mfg.shift s ON s.shift_id = r.shift_id
            JOIN md.item i ON i.item_id = r.item_id
            LEFT JOIN mfg.shift_summary ss ON ss.run_id = r.run_id AND ss.status IN ('DRAFT', 'POSTED')
            LEFT JOIN mfg.fg_lot f ON f.summary_id = ss.summary_id
            LEFT JOIN inv.lot l ON l.lot_id = f.lot_id
            WHERE r.company_id = @c AND r.run_id = @r
            """,
            r => new ProductionRunSummary(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetString(5), r.Date(6), r.GetGuid(7), r.GetString(8), r.GetString(9),
                r.NullableString(10), r.IsDBNull(11) ? null : r.GetDecimal(11), r.NullableString(12), r.GetInt64(13)),
            cancellationToken,
            ("c", context.CompanyId),
            ("r", query.RunId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The production run does not exist.");
        var summary = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT summary_id, batches, good_units, mix_scrap_units, fresh_scrap_units, status, recorded_by, posted_by, version
            FROM mfg.shift_summary WHERE run_id = @r AND status IN ('DRAFT', 'POSTED')
            """,
            r => new ShiftSummaryView(r.GetGuid(0), r.GetInt32(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4), r.GetString(5), r.GetGuid(6), r.IsDBNull(7) ? null : r.GetGuid(7), r.GetInt64(8)),
            cancellationToken,
            ("r", query.RunId)).ConfigureAwait(false);
        var consumption = summary is null
            ? []
            : await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT c.material_item_id, i.code, i.base_uom, l.code, c.entered_qty, c.entered_uom, c.qty, c.theoretical_qty, c.qty - c.theoretical_qty
                FROM mfg.material_consumption c JOIN md.item i ON i.item_id = c.material_item_id JOIN md.location l ON l.location_id = c.location_id
                WHERE c.summary_id = @s ORDER BY i.code
                """,
                r => new ConsumptionView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDecimal(4), r.GetString(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8)),
                cancellationToken,
                ("s", summary.SummaryId)).ConfigureAwait(false);
        var lot = summary is null
            ? null
            : await Reading.SingleOrDefaultAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT f.lot_id, l.lot_code, f.status, f.curing_from, f.releasable_at, (SELECT count(*)::int FROM mfg.rack k WHERE k.lot_id = f.lot_id)
                FROM mfg.fg_lot f JOIN inv.lot l ON l.lot_id = f.lot_id WHERE f.summary_id = @s
                """,
                r => new FgLotView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetFieldValue<DateTime>(3), r.GetFieldValue<DateTime>(4), r.GetInt32(5)),
                cancellationToken,
                ("s", summary.SummaryId)).ConfigureAwait(false);
        return ApiJson.Serialize(new ProductionRunDetail(run, summary, consumption, lot));
    }
}

// E-MFG1-04-6: finished-goods lots with their curing window, location and quantity.

public sealed record ListFgLots(Guid CompanyId, Guid SessionId, Guid? PlantId = null, string? Status = null, int Limit = 50, int Offset = 0) : IQuery;

/// <summary><c>CuringDone</c>: the minimum curing hours have passed (at the query's time).</summary>
public sealed record FgLotSummary(
    Guid LotId, string LotCode, Guid PlantId, string PlantCode, Guid ItemId, string ItemCode, string RunNo, DateOnly BusinessDate, string Status, DateTime CuringFrom, DateTime ReleasableAt,
    bool CuringDone, string? LocationCode, decimal Quantity, int Racks, string? BlockReason, long Version);

public sealed record FgLotList(IReadOnlyList<FgLotSummary> Items, int Limit, int Offset);

[RequiresPermission("production:read")]
public sealed class ListFgLotsHandler : IQueryHandler<ListFgLots>
{
    public string QueryType => "Manufacturing.ListFgLots";

    public async Task<string> HandleAsync(ListFgLots query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT f.lot_id, l.lot_code, r.plant_id, p.code, r.item_id, i.code, r.run_no, r.business_date, f.status, f.curing_from, f.releasable_at,
                   f.releasable_at <= @now,
                   (SELECT string_agg(loc.code, ', ' ORDER BY loc.code) FROM inv.inv_stock_balance b JOIN md.location loc ON loc.location_id = b.location_id
                     WHERE b.lot_id = f.lot_id AND b.quantity > 0),
                   (SELECT coalesce(sum(b.quantity), 0) FROM inv.inv_stock_balance b WHERE b.lot_id = f.lot_id),
                   (SELECT count(*)::int FROM mfg.rack k WHERE k.lot_id = f.lot_id), f.block_reason, f.version
            FROM mfg.fg_lot f
            JOIN inv.lot l ON l.lot_id = f.lot_id
            JOIN mfg.production_run r ON r.run_id = f.run_id
            JOIN md.plant p ON p.plant_id = r.plant_id
            JOIN md.item i ON i.item_id = r.item_id
            WHERE f.company_id = @c AND (CAST(@p AS uuid) IS NULL OR r.plant_id = CAST(@p AS uuid)) AND (CAST(@s AS text) IS NULL OR f.status = CAST(@s AS text))
            ORDER BY f.releasable_at DESC, l.lot_code
            LIMIT @limit OFFSET @offset
            """,
            r => new FgLotSummary(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetGuid(4), r.GetString(5), r.GetString(6), r.Date(7), r.GetString(8),
                r.GetFieldValue<DateTime>(9), r.GetFieldValue<DateTime>(10), r.GetBoolean(11), r.NullableString(12), r.GetDecimal(13), r.GetInt32(14), r.NullableString(15), r.GetInt64(16)),
            cancellationToken,
            ("c", context.CompanyId),
            ("now", context.Clock.UtcNow),
            ("p", query.PlantId),
            ("s", query.Status),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new FgLotList(items, query.Limit, query.Offset));
    }
}
