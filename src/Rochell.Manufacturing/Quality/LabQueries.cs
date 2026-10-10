using System.Globalization;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Manufacturing.Quality;

// LAB1-01 (E-LAB1-01-13/14): what the lab's screens read, with lab:read.

/// <summary>The lots the lab can test (every status but VOIDED), newest first; <c>Search</c> matches the field code or the internal code.</summary>
/// <remarks><c>View</c> (LAB1-02): BLOCKED_BY_LAB or READY_FINAL (RELEASED with CUMPLE on real data) narrows the list; anything else lists all.</remarks>
public sealed record ListLabLots(Guid CompanyId, Guid SessionId, string? Search = null, int Limit = 100, string? View = null) : IQuery;

/// <remarks>
/// <c>FieldCodeWaitsFor</c> (E-LAB1-01-4), when the lot has no field code yet: ITEM_PREFIX (the item has no requirements),
/// MACHINE_SHORT_CODE, or DUPLICATE (another live lot has the code it would get).
/// </remarks>
public sealed record LabLotView(
    Guid LotId, Guid PlantId, string? FieldCode, string LotCode, Guid ItemId, string ItemCode, string ItemDescription, string MachineCode, string? MachineShortCode, string ShiftCode,
    DateOnly ProductionDate, string Status, string? FieldCodeWaitsFor, int CompressionTests, int AbsorptionTests, DateOnly? LastBreakDate,
    long Version, string? Verdict, string? Basis, decimal? Strength28d, IReadOnlyList<string> Alerts, string? BlockCause, string? BlockReason, bool ReadyForFinalRelease);

/// <remarks><c>BlockedByLab</c> and <c>ReadyForFinalRelease</c> count every lot of the company (Inicio, E-LAB1-02-15), not only the page.</remarks>
public sealed record LabLotList(IReadOnlyList<LabLotView> Items, int WithoutFieldCode, int BlockedByLab, int ReadyForFinalRelease);

/// <remarks>
/// LAB1-02 (baseline §4.2, E-LAB1-02-1…5): the evaluation in force. <c>Verdict</c> COMPLIES, FAILS, NO_SPEC or NO_DATA; <c>Basis</c> REAL
/// (breaks at the 28-day age) or ESTIMATED (early average ÷ <c>FactorUsed</c>, OWN or INITIAL); <c>Cv</c> a fraction; <c>Alerts</c>
/// NO_TESTS, FEW_SPECIMENS, HIGH_CV, HIGH_ABSORPTION.
/// </remarks>
public sealed record LotEvaluationView(
    Guid EvaluationId, int Specimens, int? AgeMin, int? AgeMax, decimal? AvgStrength, decimal? MinStrength, decimal? MaxStrength, decimal? StdDev, decimal? Cv, int? EarlyAge, decimal? EarlyAvg,
    decimal? RealAvg28d, decimal? FactorUsed, string? FactorSource, decimal? Strength28d, decimal? Min28d, string? Basis, int? SpecVersion, decimal? MinAvgRequired,
    decimal? MinIndividualRequired, string Verdict, IReadOnlyList<string> Alerts, DateTime EvaluatedAt);

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

/// <remarks>
/// LAB1-03 (E-LAB1-03-2…5): a certificate of the lot — ISSUED or VOIDED (<c>VoidCause</c> MANUAL, or SPECIMEN_VOIDED when one of its specimens
/// was voided); <c>PublicCode</c> is the key its QR carries.
/// </remarks>
public sealed record LabCertificateView(
    Guid CertificateId, string CertificateNo, DateOnly BreakDate, int Specimens, string? DeliveryNo, string? CustomerName, string Status, DateTime IssuedAt, string IssuedBy,
    string? VoidCause, string? VoidReason, DateTime? VoidedAt, string PublicCode);

public sealed record LabLotDetail(
    LabLotView Lot, ItemSpecView? Spec, IReadOnlyList<CompressionTestView> Compression, IReadOnlyList<AbsorptionTestView> Absorption, AbsorptionSummary? AbsorptionSummary,
    LotEvaluationView? Evaluation, IReadOnlyList<LabCertificateView> Certificates);

/// <summary>LAB1-02 (baseline §4.7, E-LAB1-02-14): where a lot went — its deliveries with customer, site and invoice — and what is left of it.</summary>
public sealed record GetLotRecall(Guid CompanyId, Guid SessionId, Guid LotId) : IQuery;

/// <remarks><c>GateOutAt</c> is null while the truck has not left; <c>InvoiceNos</c> the invoices of that delivery line, comma separated.</remarks>
public sealed record RecallDelivery(
    Guid DeliveryId, string DeliveryNo, string Status, DateTime? GateOutAt, Guid CustomerId, string CustomerName, string OrderNo, string? SiteAddress, string ItemCode, decimal BaseQuantity,
    string? InvoiceNos);

public sealed record RecallStock(string PlantCode, string LocationCode, decimal Quantity);

public sealed record LotRecall(LabLotView Lot, IReadOnlyList<RecallDelivery> Deliveries, IReadOnlyList<RecallStock> Stock, decimal Dispatched, decimal InStock, int Customers);

/// <summary>Backward (E-LAB1-02-14): the lots a delivery took, each with its run, shift, machine, recipe, the shift's consumption and its verdict.</summary>
public sealed record GetDeliveryRecall(Guid CompanyId, Guid SessionId, Guid DeliveryId) : IQuery;

public sealed record RecallConsumption(string MaterialCode, string MaterialDescription, string Uom, decimal Qty, decimal TheoreticalQty);

public sealed record RecallLot(LabLotView Lot, decimal BaseQuantity, string RunNo, int RecipeVersion, IReadOnlyList<RecallConsumption> Consumption);

/// <remarks><c>OtherLots</c>: lots of the delivery without a production record (opening stock), by their code.</remarks>
public sealed record DeliveryRecall(
    Guid DeliveryId, string DeliveryNo, string Status, string CustomerName, string OrderNo, string? SiteAddress, IReadOnlyList<RecallLot> Lots, IReadOnlyList<string> OtherLots);

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
        (SELECT max(t.break_date) FROM qa.compression_test t WHERE t.lot_id = f.lot_id AND t.status = 'RECORDED'),
        f.version, ev.verdict, ev.basis, ev.strength_28d, coalesce(ev.alerts, ARRAY[]::text[]), f.block_cause, f.block_reason,
        coalesce(f.status = 'RELEASED' AND ev.verdict = 'COMPLIES' AND ev.basis = 'REAL', false)
        """;

    public const string LotFrom =
        """
        FROM mfg.fg_lot f JOIN inv.lot l ON l.lot_id = f.lot_id JOIN mfg.production_run r ON r.run_id = f.run_id JOIN md.item i ON i.item_id = r.item_id
        JOIN md.machine m ON m.machine_id = r.machine_id JOIN mfg.shift sh ON sh.shift_id = r.shift_id
        LEFT JOIN LATERAL (SELECT e.* FROM qa.lot_evaluation e WHERE e.company_id = f.company_id AND e.lot_id = f.lot_id ORDER BY e.seq DESC LIMIT 1) ev ON true
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
            r.GetString(11), r.NullableString(12), r.GetInt32(13), r.GetInt32(14), r.IsDBNull(15) ? null : r.Date(15),
            r.GetInt64(16), r.NullableString(17), r.NullableString(18), r.NullableDecimal(19), r.GetFieldValue<string[]>(20), r.NullableString(21), r.NullableString(22), r.GetBoolean(23));

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
              AND (CAST(@view AS text) IS DISTINCT FROM 'BLOCKED_BY_LAB' OR (f.status = 'BLOCKED' AND f.block_cause = 'LAB'))
              AND (CAST(@view AS text) IS DISTINCT FROM 'READY_FINAL' OR (f.status = 'RELEASED' AND ev.verdict = 'COMPLIES' AND ev.basis = 'REAL'))
            ORDER BY r.business_date DESC, f.field_code, l.lot_code
            LIMIT @n
            """,
            LabReads.Lot,
            cancellationToken,
            ("c", context.CompanyId),
            ("q", search),
            ("view", query.View),
            ("n", Math.Clamp(query.Limit, 1, 500))).ConfigureAwait(false);
        var waiting = await Lab.ScalarAsync<long?>(
            context.Connection, context.Transaction, "SELECT count(*) FROM mfg.fg_lot WHERE company_id = @c AND status <> 'VOIDED' AND field_code IS NULL", cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false) ?? 0;
        var blocked = await Lab.ScalarAsync<long?>(
            context.Connection, context.Transaction, "SELECT count(*) FROM mfg.fg_lot WHERE company_id = @c AND status = 'BLOCKED' AND block_cause = 'LAB'", cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false) ?? 0;
        var ready = await Lab.ScalarAsync<long?>(
            context.Connection,
            context.Transaction,
            """
            SELECT count(*) FROM mfg.fg_lot f
            WHERE f.company_id = @c AND f.status = 'RELEASED'
              AND (SELECT e.verdict = 'COMPLIES' AND e.basis = 'REAL' FROM qa.lot_evaluation e WHERE e.lot_id = f.lot_id ORDER BY e.seq DESC LIMIT 1)
            """,
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false) ?? 0;
        return ApiJson.Serialize(new LabLotList(items, (int)waiting, (int)blocked, (int)ready));
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
        var evaluation = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT evaluation_id, specimens, age_min, age_max, avg_strength, min_strength, max_strength, std_dev, cv, early_age, early_avg, real_avg_28d, factor_used, factor_source, strength_28d,
                   min_28d, basis, spec_version, min_avg_required, min_individual_required, verdict, alerts, evaluated_at
            FROM qa.lot_evaluation WHERE company_id = @c AND lot_id = @l ORDER BY seq DESC LIMIT 1
            """,
            r => new LotEvaluationView(
                r.GetGuid(0), r.GetInt32(1), Int(r, 2), Int(r, 3), r.NullableDecimal(4), r.NullableDecimal(5), r.NullableDecimal(6), r.NullableDecimal(7), r.NullableDecimal(8), Int(r, 9),
                r.NullableDecimal(10), r.NullableDecimal(11), r.NullableDecimal(12), r.NullableString(13), r.NullableDecimal(14), r.NullableDecimal(15), r.NullableString(16), Int(r, 17),
                r.NullableDecimal(18), r.NullableDecimal(19), r.GetString(20), r.GetFieldValue<string[]>(21), r.Utc(22)),
            cancellationToken,
            ("c", context.CompanyId),
            ("l", query.LotId)).ConfigureAwait(false);
        var certificates = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT c.certificate_id, c.certificate_no, c.break_date, (SELECT count(*) FROM qa.certificate_test x WHERE x.certificate_id = c.certificate_id)::int,
                   c.snapshot -> 'delivery' ->> 'deliveryNo', c.snapshot -> 'delivery' ->> 'customer', c.status, c.issued_at, coalesce(u.display_name, u.email),
                   c.void_cause, c.void_reason, c.voided_at, c.public_code
            FROM qa.certificate c JOIN iam.user u ON u.user_id = c.issued_by
            WHERE c.company_id = @c AND c.lot_id = @l
            ORDER BY c.break_date, c.seq
            """,
            r => new LabCertificateView(
                r.GetGuid(0), r.GetString(1), r.Date(2), r.GetInt32(3), r.NullableString(4), r.NullableString(5), r.GetString(6), r.Utc(7), r.GetString(8), r.NullableString(9),
                r.NullableString(10), r.NullableUtc(11), r.GetString(12)),
            cancellationToken,
            ("c", context.CompanyId),
            ("l", query.LotId)).ConfigureAwait(false);
        return ApiJson.Serialize(new LabLotDetail(lot, spec, compression, absorption, summary, evaluation, certificates));
    }

    private static int? Int(System.Data.Common.DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
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

[RequiresPermission("lab:read")]
public sealed class GetLotRecallHandler : IQueryHandler<GetLotRecall>
{
    public string QueryType => "Manufacturing.GetLotRecall";

    public async Task<string> HandleAsync(GetLotRecall query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var lot = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, $"SELECT {LabReads.LotColumns} {LabReads.LotFrom} WHERE f.company_id = @c AND f.lot_id = @l", LabReads.Lot, cancellationToken,
            ("c", context.CompanyId), ("l", query.LotId)).ConfigureAwait(false)
            ?? throw new DomainException(ManufacturingErrors.NotFound, "The finished-goods lot does not exist.");

        // E-LAB1-01-1: Sales' tables are read here, read-only; the module graph does not change.
        var deliveries = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT d.delivery_id, d.delivery_no, d.status, d.gate_out_at, p.party_id, p.legal_name, o.order_no, o.site_address, i.code, dl.base_quantity,
                   (SELECT string_agg(DISTINCT v.invoice_no, ', ' ORDER BY v.invoice_no) FROM sal.invoice_line il JOIN sal.invoice v ON v.invoice_id = il.invoice_id
                    WHERE il.delivery_line_id = l.delivery_line_id AND v.commercial_status <> 'VOIDED')
            FROM log.delivery_line_lot dl
            JOIN log.delivery_line l ON l.delivery_line_id = dl.delivery_line_id
            JOIN log.delivery d ON d.delivery_id = l.delivery_id
            JOIN sal.sales_order o ON o.sales_order_id = d.sales_order_id
            JOIN md.party p ON p.party_id = o.party_id
            JOIN md.item i ON i.item_id = l.item_id
            WHERE dl.company_id = @c AND dl.lot_id = @l
            ORDER BY d.gate_out_at, d.delivery_no
            """,
            r => new RecallDelivery(
                r.GetGuid(0), r.GetString(1), r.GetString(2), r.NullableUtc(3), r.GetGuid(4), r.GetString(5), r.GetString(6), r.NullableString(7), r.GetString(8), r.GetDecimal(9), r.NullableString(10)),
            cancellationToken,
            ("c", context.CompanyId),
            ("l", query.LotId)).ConfigureAwait(false);
        var stock = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT p.code, l.code, b.quantity FROM inv.inv_stock_balance b JOIN md.location l ON l.location_id = b.location_id JOIN md.plant p ON p.plant_id = l.plant_id
            WHERE b.lot_id = @l AND b.quantity > 0 ORDER BY p.code, l.code
            """,
            r => new RecallStock(r.GetString(0), r.GetString(1), r.GetDecimal(2)),
            cancellationToken,
            ("l", query.LotId)).ConfigureAwait(false);
        return ApiJson.Serialize(new LotRecall(
            lot, deliveries, stock, deliveries.Sum(d => d.BaseQuantity), stock.Sum(x => x.Quantity), deliveries.Select(d => d.CustomerId).Distinct().Count()));
    }
}

[RequiresPermission("lab:read")]
public sealed class GetDeliveryRecallHandler : IQueryHandler<GetDeliveryRecall>
{
    private sealed record Header(Guid DeliveryId, string DeliveryNo, string Status, string CustomerName, string OrderNo, string? SiteAddress);

    private sealed record Taken(Guid LotId, string LotCode, decimal BaseQuantity, Guid? SummaryId, string? RunNo, int? RecipeVersion);

    public string QueryType => "Manufacturing.GetDeliveryRecall";

    public async Task<string> HandleAsync(GetDeliveryRecall query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var header = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT d.delivery_id, d.delivery_no, d.status, p.legal_name, o.order_no, o.site_address
            FROM log.delivery d JOIN sal.sales_order o ON o.sales_order_id = d.sales_order_id JOIN md.party p ON p.party_id = o.party_id
            WHERE d.company_id = @c AND d.delivery_id = @d
            """,
            r => new Header(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.NullableString(5)),
            cancellationToken,
            ("c", context.CompanyId),
            ("d", query.DeliveryId)).ConfigureAwait(false)
            ?? throw new DomainException(ManufacturingErrors.NotFound, "The delivery does not exist.");
        var taken = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT dl.lot_id, il.lot_code, sum(dl.base_quantity), f.summary_id, r.run_no, rv.version
            FROM log.delivery_line_lot dl
            JOIN log.delivery_line l ON l.delivery_line_id = dl.delivery_line_id
            JOIN inv.lot il ON il.lot_id = dl.lot_id
            LEFT JOIN mfg.fg_lot f ON f.lot_id = dl.lot_id
            LEFT JOIN mfg.production_run r ON r.run_id = f.run_id
            LEFT JOIN mfg.recipe_version rv ON rv.recipe_version_id = r.recipe_version_id
            WHERE dl.company_id = @c AND l.delivery_id = @d
            GROUP BY dl.lot_id, il.lot_code, f.summary_id, r.run_no, rv.version
            ORDER BY il.lot_code
            """,
            r => new Taken(r.GetGuid(0), r.GetString(1), r.GetDecimal(2), r.NullableGuid(3), r.NullableString(4), r.IsDBNull(5) ? null : r.GetInt32(5)),
            cancellationToken,
            ("c", context.CompanyId),
            ("d", query.DeliveryId)).ConfigureAwait(false);
        var lots = new List<RecallLot>();
        foreach (var t in taken.Where(x => x.SummaryId is not null))
        {
            var lot = (await Reading.SingleOrDefaultAsync(
                context.Connection, context.Transaction, $"SELECT {LabReads.LotColumns} {LabReads.LotFrom} WHERE f.company_id = @c AND f.lot_id = @l", LabReads.Lot, cancellationToken,
                ("c", context.CompanyId), ("l", t.LotId)).ConfigureAwait(false))!;
            var consumption = await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT i.code, i.description, i.base_uom, c.qty, c.theoretical_qty FROM mfg.material_consumption c JOIN md.item i ON i.item_id = c.material_item_id
                WHERE c.company_id = @c AND c.summary_id = @s ORDER BY i.code
                """,
                r => new RecallConsumption(r.GetString(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetDecimal(4)),
                cancellationToken,
                ("c", context.CompanyId),
                ("s", t.SummaryId)).ConfigureAwait(false);
            lots.Add(new RecallLot(lot, t.BaseQuantity, t.RunNo!, t.RecipeVersion!.Value, consumption));
        }

        return ApiJson.Serialize(new DeliveryRecall(
            header.DeliveryId, header.DeliveryNo, header.Status, header.CustomerName, header.OrderNo, header.SiteAddress, lots,
            [.. taken.Where(x => x.SummaryId is null).Select(x => x.LotCode)]));
    }
}

/// <summary>
/// LAB1-03c (E-LAB1-03-8, 15): what the certificate's public QR page shows, read by its public code — never the customer nor the site.
/// Only the service identity «Verificación pública» holds the permission.
/// </summary>
public sealed record VerifyLabCertificate(Guid CompanyId, Guid SessionId, string PublicCode) : IQuery;

/// <remarks><c>Status</c> ISSUED (in force) or VOIDED, with when it was voided.</remarks>
public sealed record LabCertificateVerification(
    string CertificateNo, string Issuer, string Product, string Lot, DateOnly BreakDate, int Specimens, string AvgKgcm2, string AvgMpa, string MinKgcm2, string? CvPercent,
    DateTime IssuedAt, string Status, DateTime? VoidedAt);

[RequiresPermission("lab_certificate:verify")]
public sealed class VerifyLabCertificateHandler : IQueryHandler<VerifyLabCertificate>
{
    public string QueryType => "Manufacturing.VerifyLabCertificate";

    public async Task<string> HandleAsync(VerifyLabCertificate query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var code = (query.PublicCode ?? string.Empty).Trim();
        var found = code.Length == 24 && code.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            ? await Reading.SingleOrDefaultAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT c.certificate_no, c.snapshot -> 'issuer' ->> 'name', (c.snapshot -> 'lot' ->> 'itemCode') || ' — ' || (c.snapshot -> 'lot' ->> 'item'), c.snapshot -> 'lot' ->> 'fieldCode',
                       c.break_date, (c.snapshot -> 'summary' ->> 'specimens')::int, c.snapshot -> 'summary' ->> 'avgKgcm2', c.snapshot -> 'summary' ->> 'avgMpa',
                       c.snapshot -> 'summary' ->> 'minKgcm2', c.snapshot -> 'summary' ->> 'cvPercent', c.issued_at, c.status, c.voided_at
                FROM qa.certificate c WHERE c.company_id = @c AND c.public_code = @k
                """,
                r => new LabCertificateVerification(
                    r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.Date(4), r.GetInt32(5), r.GetString(6), r.GetString(7), r.GetString(8), r.NullableString(9), r.Utc(10),
                    r.GetString(11), r.NullableUtc(12)),
                cancellationToken,
                ("c", context.CompanyId),
                ("k", code)).ConfigureAwait(false)
            : null;
        return found is null ? throw new DomainException(QueryErrors.NotFound, "No certificate has that code.") : ApiJson.Serialize(found);
    }
}
