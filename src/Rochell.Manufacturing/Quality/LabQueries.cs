using System.Globalization;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Manufacturing.Quality;

// LAB1-01 (E-LAB1-01-13/14): what the lab's screens read, with lab:read.

/// <summary>The lots the lab can test (every status but VOIDED), newest first; <c>Search</c> matches the field code or the internal code.</summary>
public sealed record ListLabLots(Guid CompanyId, Guid SessionId, string? Search = null, int Limit = 100) : IQuery;

/// <remarks>
/// <c>FieldCodeWaitsFor</c> (E-LAB1-01-4), when the lot has no field code yet: ITEM_PREFIX (the item has no requirements),
/// MACHINE_SHORT_CODE, or DUPLICATE (another live lot has the code it would get).
/// </remarks>
public sealed record LabLotView(
    Guid LotId, Guid PlantId, string? FieldCode, string LotCode, Guid ItemId, string ItemCode, string ItemDescription, string MachineCode, string? MachineShortCode, string ShiftCode,
    DateOnly ProductionDate, string Status, string? FieldCodeWaitsFor, int CompressionTests, int AbsorptionTests, DateOnly? LastBreakDate);

public sealed record LabLotList(IReadOnlyList<LabLotView> Items, int WithoutFieldCode);

public sealed record GetLabLot(Guid CompanyId, Guid SessionId, Guid LotId) : IQuery;

/// <remarks>
/// Measures are the ones used (the item's nominal ones where the specimen had none, <c>NominalUsed</c>). Gross area and gross strength
/// are the ones kept when recorded; MPa and the net-area strength are computed now, with today's conversion and the item's net-area
/// fraction (E-LAB1-01-8). <c>AgeZero</c>: broken the day it was produced — it never enters an estimate (E-LAB1-01-6).
/// </remarks>
public sealed record CompressionTestView(
    Guid TestId, DateOnly BreakDate, int AgeDays, bool AgeZero, decimal WidthCm, decimal HeightCm, decimal LengthCm, bool NominalUsed, decimal? WeightKg, decimal LoadKg, decimal GrossAreaCm2,
    decimal StrengthKgcm2, decimal StrengthMpa, decimal? NetStrengthKgcm2, string? BlockCondition, string? FailureType, string? FailureTypeName, string? Notes, string TestedBy,
    DateTime RecordedAt, string Status, string? VoidReason, string? VoidedBy);

/// <remarks>
/// Baseline §4.4: absorption (kg/m³ and fraction of the dry weight), density, the density's class (LIVIANO, MEDIANO, NORMAL) with its
/// absorption limit, and <c>Guide</c> OK / ALTA per block — the standard's limit applies to the lot's average.
/// </remarks>
public sealed record AbsorptionTestView(
    Guid TestId, DateOnly TestDate, decimal WsKg, decimal WiKg, decimal WdKg, decimal AbsorptionKgm3, decimal AbsorptionFraction, decimal DensityKgm3, string DensityClass, decimal AbsorptionLimitKgm3,
    string Guide, string? Notes, string TestedBy, DateTime RecordedAt, string Status, string? VoidReason, string? VoidedBy);

/// <summary>The averages of the lot's valid absorption blocks, the class of the average density and its limit.</summary>
public sealed record AbsorptionSummary(int Blocks, decimal AbsorptionKgm3, decimal AbsorptionFraction, decimal DensityKgm3, string DensityClass, decimal AbsorptionLimitKgm3, bool AboveLimit);

public sealed record LabLotDetail(
    LabLotView Lot, ItemSpecView? Spec, IReadOnlyList<CompressionTestView> Compression, IReadOnlyList<AbsorptionTestView> Absorption, AbsorptionSummary? AbsorptionSummary);

/// <summary>Every finished good with its requirements in force (<c>Version</c> null while it has none).</summary>
public sealed record ListItemSpecs(Guid CompanyId, Guid SessionId) : IQuery;

public sealed record ItemSpecView(
    Guid ItemId, string ItemCode, string ItemDescription, int? Version, string? LotPrefix, decimal? NominalWidthCm, decimal? NominalHeightCm, decimal? NominalLengthCm, decimal? NetAreaFraction,
    decimal? MinAvg28d, decimal? MinIndividual28d, string? SetBy, DateTime? SetAt);

public sealed record ItemSpecList(IReadOnlyList<ItemSpecView> Items);

/// <summary>E-LAB1-01-3/11: the lab's parameters with who changed what, the failure types and the block conditions.</summary>
public sealed record GetLabSettings(Guid CompanyId, Guid SessionId) : IQuery;

/// <remarks><c>Kind</c> NUMBER or TEXT; <c>Own</c> when the company replaced the shared starting value (<c>Default*</c>).</remarks>
public sealed record LabParameterView(
    string Code, string Name, string Kind, decimal? Number, string? Text, decimal? DefaultNumber, string? DefaultText, decimal? Min, decimal? Max, bool Whole, bool Own, string? SetBy, DateTime? SetAt);

public sealed record LabParameterChange(string Code, string Name, string Value, string SetBy, DateTime SetAt);

/// <remarks><c>Shared</c>: one of the starting seven the company has not touched.</remarks>
public sealed record FailureTypeView(string Code, string Name, string Status, bool Shared);

public sealed record LabSettings(IReadOnlyList<LabParameterView> Parameters, IReadOnlyList<LabParameterChange> History, IReadOnlyList<FailureTypeView> FailureTypes, IReadOnlyList<string> BlockConditions);

/// <summary>E-LAB1-01-14: what the specimens would give, before saving — the same computation the record uses.</summary>
public sealed record PreviewCompressionTests(Guid CompanyId, Guid SessionId, Guid LotId, DateOnly BreakDate, IReadOnlyList<CompressionSpecimen> Specimens) : IQuery;

public sealed record PreviewCompressionRequest(Guid LotId, DateOnly BreakDate, IReadOnlyList<CompressionSpecimen> Specimens);

/// <remarks><c>Error</c> says why that specimen cannot be recorded as typed; its numbers are then null.</remarks>
public sealed record CompressionPreviewLine(int Line, bool? NominalUsed, decimal? GrossAreaCm2, decimal? StrengthKgcm2, decimal? StrengthMpa, string? Error);

public sealed record CompressionPreview(int AgeDays, bool AgeZero, IReadOnlyList<CompressionPreviewLine> Lines);

internal static class LabReads
{
    public const string LotColumns =
        """
        f.lot_id, r.plant_id, f.field_code, l.lot_code, r.item_id, i.code, i.description, m.code, m.short_code, sh.code, r.business_date, f.status,
        CASE WHEN f.field_code IS NOT NULL THEN NULL
             WHEN NOT EXISTS (SELECT 1 FROM qa.item_spec s WHERE s.company_id = f.company_id AND s.item_id = r.item_id) THEN 'ITEM_PREFIX'
             WHEN m.short_code IS NULL THEN 'MACHINE_SHORT_CODE' ELSE 'DUPLICATE' END,
        (SELECT count(*) FROM qa.compression_test t WHERE t.lot_id = f.lot_id AND t.status = 'RECORDED')::int,
        (SELECT count(*) FROM qa.absorption_test t WHERE t.lot_id = f.lot_id AND t.status = 'RECORDED')::int,
        (SELECT max(t.break_date) FROM qa.compression_test t WHERE t.lot_id = f.lot_id AND t.status = 'RECORDED')
        """;

    public const string LotFrom =
        """
        FROM mfg.fg_lot f JOIN inv.lot l ON l.lot_id = f.lot_id JOIN mfg.production_run r ON r.run_id = f.run_id JOIN md.item i ON i.item_id = r.item_id
        JOIN md.machine m ON m.machine_id = r.machine_id JOIN mfg.shift sh ON sh.shift_id = r.shift_id
        """;

    public const string SpecColumns =
        """
        i.item_id, i.code, i.description, s.version, s.lot_prefix, s.nominal_width_cm, s.nominal_height_cm, s.nominal_length_cm, s.net_area_fraction, s.min_avg_28d, s.min_individual_28d,
        coalesce(u.display_name, u.email), s.set_at
        """;

    public const string SpecFrom =
        """
        FROM md.item i
        LEFT JOIN LATERAL (SELECT x.* FROM qa.item_spec x WHERE x.company_id = i.company_id AND x.item_id = i.item_id ORDER BY x.version DESC LIMIT 1) s ON true
        LEFT JOIN iam.user u ON u.user_id = s.set_by
        """;

    public static LabLotView Lot(System.Data.Common.DbDataReader r)
        => new(r.GetGuid(0), r.GetGuid(1), r.NullableString(2), r.GetString(3), r.GetGuid(4), r.GetString(5), r.GetString(6), r.GetString(7), r.NullableString(8), r.GetString(9), r.Date(10),
            r.GetString(11), r.NullableString(12), r.GetInt32(13), r.GetInt32(14), r.IsDBNull(15) ? null : r.Date(15));

    public static ItemSpecView Spec(System.Data.Common.DbDataReader r)
        => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt32(3), r.NullableString(4), r.NullableDecimal(5), r.NullableDecimal(6), r.NullableDecimal(7),
            r.NullableDecimal(8), r.NullableDecimal(9), r.NullableDecimal(10), r.NullableString(11), r.NullableUtc(12));
}

[RequiresPermission("lab:read")]
public sealed class ListLabLotsHandler : IQueryHandler<ListLabLots>
{
    public string QueryType => "Manufacturing.ListLabLots";

    public async Task<string> HandleAsync(ListLabLots query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            $"""
            SELECT {LabReads.LotColumns}
            {LabReads.LotFrom}
            WHERE f.company_id = @c AND f.status <> 'VOIDED'
              AND (CAST(@q AS text) IS NULL OR f.field_code ILIKE '%' || CAST(@q AS text) || '%' OR l.lot_code ILIKE '%' || CAST(@q AS text) || '%')
            ORDER BY r.business_date DESC, f.field_code, l.lot_code
            LIMIT @n
            """,
            LabReads.Lot,
            cancellationToken,
            ("c", context.CompanyId),
            ("q", search),
            ("n", Math.Clamp(query.Limit, 1, 500))).ConfigureAwait(false);
        var waiting = await Lab.ScalarAsync<long?>(
            context.Connection, context.Transaction, "SELECT count(*) FROM mfg.fg_lot WHERE company_id = @c AND status <> 'VOIDED' AND field_code IS NULL", cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false) ?? 0;
        return ApiJson.Serialize(new LabLotList(items, (int)waiting));
    }
}

[RequiresPermission("lab:read")]
public sealed class GetLabLotHandler : IQueryHandler<GetLabLot>
{
    private const string Class =
        "CASE WHEN {0} < qa.parameter_number(@c, 'DENSITY_MEDIUM_FROM') THEN 'LIVIANO' WHEN {0} < qa.parameter_number(@c, 'DENSITY_NORMAL_FROM') THEN 'MEDIANO' ELSE 'NORMAL' END";

    private const string Limit =
        """
        CASE WHEN {0} < qa.parameter_number(@c, 'DENSITY_MEDIUM_FROM') THEN qa.parameter_number(@c, 'ABSORPTION_MAX_LIGHT')
             WHEN {0} < qa.parameter_number(@c, 'DENSITY_NORMAL_FROM') THEN qa.parameter_number(@c, 'ABSORPTION_MAX_MEDIUM') ELSE qa.parameter_number(@c, 'ABSORPTION_MAX_NORMAL') END
        """;

    public string QueryType => "Manufacturing.GetLabLot";

    public async Task<string> HandleAsync(GetLabLot query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var lot = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, $"SELECT {LabReads.LotColumns} {LabReads.LotFrom} WHERE f.company_id = @c AND f.lot_id = @l", LabReads.Lot, cancellationToken,
            ("c", context.CompanyId), ("l", query.LotId)).ConfigureAwait(false)
            ?? throw new DomainException(ManufacturingErrors.NotFound, "The finished-goods lot does not exist.");
        var spec = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, $"SELECT {LabReads.SpecColumns} {LabReads.SpecFrom} WHERE i.company_id = @c AND i.item_id = @i AND s.version IS NOT NULL", LabReads.Spec,
            cancellationToken, ("c", context.CompanyId), ("i", lot.ItemId)).ConfigureAwait(false);
        var compression = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT t.test_id, t.break_date, t.age_days, coalesce(t.width_cm, n.nominal_width_cm), coalesce(t.height_cm, n.nominal_height_cm), coalesce(t.length_cm, n.nominal_length_cm), t.nominal_used,
                   t.weight_kg, t.load_kg, t.gross_area_cm2, t.strength_kgcm2, round(t.strength_kgcm2 * qa.parameter_number(@c, 'KGCM2_TO_MPA'), 6),
                   round(t.strength_kgcm2 / cur.net_area_fraction, 6), t.block_condition, t.failure_type,
                   coalesce((SELECT ft.name FROM qa.failure_type ft WHERE ft.company_id = t.company_id AND ft.code = t.failure_type), (SELECT d.name FROM qa.failure_type_default d WHERE d.code = t.failure_type)),
                   t.notes, coalesce(u.display_name, u.email), t.recorded_at, t.status, t.void_reason, coalesce(v.display_name, v.email)
            FROM qa.compression_test t
            JOIN mfg.fg_lot f ON f.lot_id = t.lot_id JOIN mfg.production_run r ON r.run_id = f.run_id
            JOIN iam.user u ON u.user_id = t.tested_by
            LEFT JOIN iam.user v ON v.user_id = t.voided_by
            LEFT JOIN qa.item_spec n ON n.company_id = t.company_id AND n.item_id = r.item_id AND n.version = t.spec_version
            LEFT JOIN LATERAL (SELECT x.net_area_fraction FROM qa.item_spec x WHERE x.company_id = t.company_id AND x.item_id = r.item_id ORDER BY x.version DESC LIMIT 1) cur ON true
            WHERE t.company_id = @c AND t.lot_id = @l
            ORDER BY t.break_date, t.recorded_at, t.line_no
            """,
            r => new CompressionTestView(
                r.GetGuid(0), r.Date(1), r.GetInt32(2), r.GetInt32(2) == 0, r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5), r.GetBoolean(6), r.NullableDecimal(7), r.GetDecimal(8), r.GetDecimal(9),
                r.GetDecimal(10), r.GetDecimal(11), r.NullableDecimal(12), r.NullableString(13), r.NullableString(14), r.NullableString(15), r.NullableString(16), r.GetString(17), r.Utc(18),
                r.GetString(19), r.NullableString(20), r.NullableString(21)),
            cancellationToken,
            ("c", context.CompanyId),
            ("l", query.LotId)).ConfigureAwait(false);
        var absorption = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            $"""
            SELECT t.test_id, t.test_date, t.ws_kg, t.wi_kg, t.wd_kg, t.absorption_kgm3, t.absorption_fraction, t.density_kgm3,
                   {string.Format(CultureInfo.InvariantCulture, Class, "t.density_kgm3")}, {string.Format(CultureInfo.InvariantCulture, Limit, "t.density_kgm3")},
                   t.notes, coalesce(u.display_name, u.email), t.recorded_at, t.status, t.void_reason, coalesce(v.display_name, v.email)
            FROM qa.absorption_test t JOIN iam.user u ON u.user_id = t.tested_by LEFT JOIN iam.user v ON v.user_id = t.voided_by
            WHERE t.company_id = @c AND t.lot_id = @l
            ORDER BY t.test_date, t.recorded_at, t.line_no
            """,
            r => new AbsorptionTestView(
                r.GetGuid(0), r.Date(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.GetString(8), r.GetDecimal(9),
                r.GetDecimal(5) > r.GetDecimal(9) ? "ALTA" : "OK", r.NullableString(10), r.GetString(11), r.Utc(12), r.GetString(13), r.NullableString(14), r.NullableString(15)),
            cancellationToken,
            ("c", context.CompanyId),
            ("l", query.LotId)).ConfigureAwait(false);
        var summary = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            $"""
            SELECT a.blocks, a.absorption, a.fraction, a.density, {string.Format(CultureInfo.InvariantCulture, Class, "a.density")}, {string.Format(CultureInfo.InvariantCulture, Limit, "a.density")}
            FROM (SELECT count(*)::int AS blocks, round(avg(absorption_kgm3), 6) AS absorption, round(avg(absorption_fraction), 6) AS fraction, round(avg(density_kgm3), 6) AS density
                  FROM qa.absorption_test WHERE company_id = @c AND lot_id = @l AND status = 'RECORDED') a
            WHERE a.blocks > 0
            """,
            r => new AbsorptionSummary(r.GetInt32(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3), r.GetString(4), r.GetDecimal(5), r.GetDecimal(1) > r.GetDecimal(5)),
            cancellationToken,
            ("c", context.CompanyId),
            ("l", query.LotId)).ConfigureAwait(false);
        return ApiJson.Serialize(new LabLotDetail(lot, spec, compression, absorption, summary));
    }
}

[RequiresPermission("lab:read")]
public sealed class ListItemSpecsHandler : IQueryHandler<ListItemSpecs>
{
    public string QueryType => "Manufacturing.ListItemSpecs";

    public async Task<string> HandleAsync(ListItemSpecs query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection, context.Transaction,
            $"SELECT {LabReads.SpecColumns} {LabReads.SpecFrom} WHERE i.company_id = @c AND i.item_type = 'FINISHED_GOOD' AND (i.status::text = 'ACTIVE' OR s.version IS NOT NULL) ORDER BY i.code",
            LabReads.Spec, cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false);
        return ApiJson.Serialize(new ItemSpecList(items));
    }
}

[RequiresPermission("lab:read")]
public sealed class GetLabSettingsHandler : IQueryHandler<GetLabSettings>
{
    public string QueryType => "Manufacturing.GetLabSettings";

    public async Task<string> HandleAsync(GetLabSettings query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var parameters = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT d.code, d.name, d.kind, coalesce(v.number_value, d.number_value), coalesce(v.text_value, d.text_value), d.number_value, d.text_value, d.min_value, d.max_value, d.whole,
                   v.value_id IS NOT NULL, coalesce(u.display_name, u.email), v.set_at
            FROM qa.parameter_default d
            LEFT JOIN LATERAL (SELECT x.* FROM qa.parameter_value x WHERE x.company_id = @c AND x.code = d.code ORDER BY x.set_at DESC, x.value_id DESC LIMIT 1) v ON true
            LEFT JOIN iam.user u ON u.user_id = v.set_by
            ORDER BY d.sort_order
            """,
            r => new LabParameterView(
                r.GetString(0), r.GetString(1), r.GetString(2), r.NullableDecimal(3), r.NullableString(4), r.NullableDecimal(5), r.NullableString(6), r.NullableDecimal(7), r.NullableDecimal(8),
                r.GetBoolean(9), r.GetBoolean(10), r.NullableString(11), r.NullableUtc(12)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        var history = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT v.code, d.name, coalesce(trim_scale(v.number_value)::text, v.text_value), coalesce(u.display_name, u.email), v.set_at
            FROM qa.parameter_value v JOIN qa.parameter_default d ON d.code = v.code JOIN iam.user u ON u.user_id = v.set_by
            WHERE v.company_id = @c ORDER BY v.set_at DESC, v.value_id DESC LIMIT 100
            """,
            r => new LabParameterChange(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.Utc(4)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        var failures = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT x.code, x.name, x.status, x.shared FROM (
              SELECT t.code, t.name, t.status, false AS shared, coalesce((SELECT d.sort_order FROM qa.failure_type_default d WHERE d.code = t.code), 1000) AS sort_order
              FROM qa.failure_type t WHERE t.company_id = @c
              UNION ALL
              SELECT d.code, d.name, 'ACTIVE', true, d.sort_order FROM qa.failure_type_default d
              WHERE NOT EXISTS (SELECT 1 FROM qa.failure_type t WHERE t.company_id = @c AND t.code = d.code)) x
            ORDER BY x.sort_order, x.code
            """,
            r => new FailureTypeView(r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false);
        return ApiJson.Serialize(new LabSettings(parameters, history, failures, [BlockConditions.AirDry, BlockConditions.Damp, BlockConditions.Saturated]));
    }
}

[RequiresPermission("lab:read")]
public sealed class PreviewCompressionTestsHandler : IQueryHandler<PreviewCompressionTests>
{
    public string QueryType => "Manufacturing.PreviewCompressionTests";

    public async Task<string> HandleAsync(PreviewCompressionTests query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var lot = await Lab.LotAsync(context.Connection, context.Transaction, context.CompanyId, query.LotId, cancellationToken).ConfigureAwait(false);
        var age = Lab.Age(lot, query.BreakDate, Platform.Time.BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow), "The break date");
        var spec = await Lab.SpecAsync(context.Connection, context.Transaction, context.CompanyId, lot.ItemId, cancellationToken).ConfigureAwait(false);
        var toMpa = await Lab.ParameterAsync(context.Connection, context.Transaction, context.CompanyId, "KGCM2_TO_MPA", cancellationToken).ConfigureAwait(false);
        var lines = new List<CompressionPreviewLine>();
        foreach (var s in query.Specimens ?? [])
        {
            var line = lines.Count + 1;
            try
            {
                var result = Lab.Compute(
                    Lab.Measure(s.WidthCm, "The width"), Lab.Measure(s.HeightCm, "The height"), Lab.Measure(s.LengthCm, "The length"), Lab.Measure(s.LoadKg, "The load")!.Value, spec, toMpa);
                lines.Add(new CompressionPreviewLine(line, result.NominalUsed, result.GrossAreaCm2, result.StrengthKgcm2, result.StrengthMpa, null));
            }
            catch (DomainException ex)
            {
                lines.Add(new CompressionPreviewLine(line, null, null, null, null, ex.Code));
            }
        }

        return ApiJson.Serialize(new CompressionPreview(age, age == 0, lines));
    }
}
