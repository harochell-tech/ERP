using System.Data.Common;
using System.Globalization;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Manufacturing.Quality;

// LAB1-01 (E-LAB1-1…10, E-LAB1-01-1…15): the quality lab lives in Manufacturing with its own schema `qa`. Calidad keeps the per-item
// requirements, the lab's parameters, the failure types and the machines' short codes; the lab technician records compression tests
// (one row per specimen) and absorption tests (one row per block) on a lot, and voids them with a reason — a test is never edited.

public static class QualityErrors
{
    /// <summary>A compression or absorption test that cannot be recorded as sent (dates, measures, weights, lists).</summary>
    public const string TestInvalid = "LAB_TEST_INVALID";

    /// <summary>A specimen without measures of an item that has no requirements (nominal measures) yet.</summary>
    public const string SpecMissing = "LAB_SPEC_MISSING";

    /// <summary>Per-item requirements out of range.</summary>
    public const string SpecInvalid = "LAB_SPEC_INVALID";

    /// <summary>An unknown parameter or failure type, or a value outside its range.</summary>
    public const string ParameterInvalid = "LAB_PARAMETER_INVALID";

    /// <summary>LAB1-02 (E-LAB1-02-10): the final release needs the evaluation in force to be CUMPLE on real data.</summary>
    public const string FinalReleaseRefused = "LAB_FINAL_RELEASE_REFUSED";

    /// <summary>LAB1-03 (E-LAB1-03-2…4): a certificate that cannot be issued — no field code, no valid specimen that date, a delivery that did not take the lot.</summary>
    public const string CertificateRefused = "LAB_CERTIFICATE_REFUSED";
}

public static class BlockConditions
{
    public const string AirDry = "SECO_AL_AIRE";
    public const string Damp = "HUMEDO";
    public const string Saturated = "SATURADO";

    public static bool IsValid(string? value) => value is null or AirDry or Damp or Saturated;
}

internal static class Lab
{
    public const int MaxRows = 30;
    public const int Decimals = 6;
    public const string FirstShift = "T1";

    public sealed record LotRow(Guid LotId, Guid PlantId, Guid ItemId, DateOnly ProductionDate, string Status, string? FieldCode, string LotCode);

    public sealed record SpecRow(int Version, decimal Width, decimal Height, decimal Length, decimal? NetAreaFraction);

    /// <summary>What a specimen gives: E-LAB1-01-8 (area and strength with 6 decimals; the strength divides by the unrounded area).</summary>
    public sealed record Strength(bool NominalUsed, decimal GrossAreaCm2, decimal StrengthKgcm2, decimal StrengthMpa);

    /// <summary>A number of a result or an event, as text (ADR-015).</summary>
    public static string Text(decimal value) => value.ToString("0.########", CultureInfo.InvariantCulture);

    public static string? Note(string? value, string what)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return trimmed is { Length: > 500 } ? throw new DomainException(QualityErrors.TestInvalid, $"{what} has at most 500 characters.") : trimmed;
    }

    /// <summary>A positive measure with at most 6 decimals, or nothing.</summary>
    public static decimal? Measure(decimal? value, string what)
        => value is null || (value > 0m && decimal.Round(value.Value, Decimals) == value)
            ? value
            : throw new DomainException(QualityErrors.TestInvalid, $"{what} must be greater than zero with at most 6 decimals.");

    public static async Task<T?> ScalarAsync<T>(DbConnection connection, DbTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = Sql.Command(connection, transaction, sql, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? default : (T)value;
    }

    /// <summary>E-LAB1-01-7: the lot of a test, in any status but VOIDED; its production date is its run's business date (E-LAB1-01-5).</summary>
    public static async Task<LotRow> LotAsync(DbConnection connection, DbTransaction transaction, Guid companyId, Guid lotId, CancellationToken cancellationToken)
    {
        var lot = await Reading.SingleOrDefaultAsync(
            connection,
            transaction,
            """
            SELECT f.lot_id, r.plant_id, r.item_id, r.business_date, f.status, f.field_code, l.lot_code
            FROM mfg.fg_lot f JOIN inv.lot l ON l.lot_id = f.lot_id JOIN mfg.production_run r ON r.run_id = f.run_id
            WHERE f.company_id = @c AND f.lot_id = @l
            """,
            r => new LotRow(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.Date(3), r.GetString(4), r.NullableString(5), r.GetString(6)),
            cancellationToken,
            ("c", companyId),
            ("l", lotId)).ConfigureAwait(false)
            ?? throw new DomainException(ManufacturingErrors.NotFound, "The finished-goods lot does not exist.");
        return lot.Status == "VOIDED" ? throw new DomainException(ManufacturingErrors.InvalidState, "The lot is VOIDED: its run was reversed. Test the lot of the run that replaced it.") : lot;
    }

    /// <summary>The item's requirements in force (its highest version), if it has any.</summary>
    public static Task<SpecRow?> SpecAsync(DbConnection connection, DbTransaction transaction, Guid companyId, Guid itemId, CancellationToken cancellationToken)
        => Reading.SingleOrDefaultAsync(
            connection,
            transaction,
            """
            SELECT version, nominal_width_cm, nominal_height_cm, nominal_length_cm, net_area_fraction FROM qa.item_spec
            WHERE company_id = @c AND item_id = @i ORDER BY version DESC LIMIT 1
            """,
            r => new SpecRow(r.GetInt32(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3), r.NullableDecimal(4)),
            cancellationToken,
            ("c", companyId),
            ("i", itemId));

    public static async Task<decimal> ParameterAsync(DbConnection connection, DbTransaction transaction, Guid companyId, string code, CancellationToken cancellationToken)
        => await ScalarAsync<decimal?>(connection, transaction, "SELECT qa.parameter_number(@c, @p)", cancellationToken, ("c", companyId), ("p", code)).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The lab parameter {code} is not seeded.");

    /// <summary>E-LAB1-01-6: the age of a test; a date before the lot was produced, or one that has not come yet, is refused.</summary>
    public static int Age(LotRow lot, DateOnly date, DateOnly today, string what)
    {
        if (date < lot.ProductionDate)
        {
            throw new DomainException(QualityErrors.TestInvalid, $"{what} ({date:yyyy-MM-dd}) is before the lot was produced ({lot.ProductionDate:yyyy-MM-dd}).");
        }

        return date > today
            ? throw new DomainException(QualityErrors.TestInvalid, $"{what} ({date:yyyy-MM-dd}) has not come yet.")
            : date.DayNumber - lot.ProductionDate.DayNumber;
    }

    /// <summary>
    /// Baseline §4.1: gross area = width × length (the item's nominal measure where one is missing), gross strength = load ÷ area,
    /// MPa = kg/cm² × the conversion parameter.
    /// </summary>
    public static Strength Compute(decimal? width, decimal? height, decimal? length, decimal load, SpecRow? spec, decimal toMpa)
    {
        var nominal = width is null || height is null || length is null;
        if (nominal && spec is null)
        {
            throw new DomainException(QualityErrors.SpecMissing, "A specimen without all its measures uses the item's nominal ones, and this item has no requirements yet.");
        }

        var area = (width ?? spec!.Width) * (length ?? spec!.Length);
        var strength = decimal.Round(load / area, Decimals, MidpointRounding.AwayFromZero);
        return strength <= 0m
            ? throw new DomainException(QualityErrors.TestInvalid, "The load is too small for the specimen's area.")
            : new Strength(nominal, decimal.Round(area, Decimals, MidpointRounding.AwayFromZero), strength, decimal.Round(strength * toMpa, Decimals, MidpointRounding.AwayFromZero));
    }

    /// <summary>E-LAB1-01-11: an ACTIVE failure type — the company's own, or a shared one it has not replaced.</summary>
    public static async Task EnsureFailureTypeAsync(DbConnection connection, DbTransaction transaction, Guid companyId, string code, CancellationToken cancellationToken)
    {
        var status = await ScalarAsync<string>(
            connection,
            transaction,
            """
            SELECT coalesce((SELECT status FROM qa.failure_type WHERE company_id = @c AND code = @code),
                            (SELECT 'ACTIVE' FROM qa.failure_type_default WHERE code = @code))
            """,
            cancellationToken,
            ("c", companyId),
            ("code", code)).ConfigureAwait(false);
        if (status != "ACTIVE")
        {
            throw new DomainException(QualityErrors.TestInvalid, $"{code} is not an active failure type.");
        }
    }
}

/// <summary>
/// E-LAB1-1, E-LAB1-01-4/5: the lot's field code, <c>&lt;item's lot prefix&gt;&lt;DDMMYY of the run&gt;&lt;machine short code&gt;</c>, with
/// <c>-&lt;shift&gt;</c> for any shift but T1. A lot whose item has no prefix or whose machine has no short code waits without one.
/// </summary>
internal static class FieldCodes
{
    private const string Candidates =
        """
        SELECT f.lot_id, s.lot_prefix || to_char(r.business_date, 'DDMMYY') || m.short_code || CASE WHEN sh.code = @first THEN '' ELSE '-' || sh.code END AS code
        FROM mfg.fg_lot f
        JOIN mfg.production_run r ON r.run_id = f.run_id
        JOIN md.machine m ON m.machine_id = r.machine_id
        JOIN mfg.shift sh ON sh.shift_id = r.shift_id
        JOIN LATERAL (SELECT i.lot_prefix FROM qa.item_spec i WHERE i.company_id = f.company_id AND i.item_id = r.item_id ORDER BY i.version DESC LIMIT 1) s ON true
        WHERE f.company_id = @c AND f.field_code IS NULL AND f.status <> 'VOIDED' AND m.short_code IS NOT NULL
        """;

    /// <summary>The code of the lot a run is about to get, or nothing while the prefix or the short code is missing (or a live lot has it).</summary>
    public static Task<string?> ForRunAsync(CommandContext context, Guid runId, CancellationToken cancellationToken)
        => MfgSql.ScalarAsync<string>(
            context,
            """
            SELECT x.code FROM (
              SELECT s.lot_prefix || to_char(r.business_date, 'DDMMYY') || m.short_code || CASE WHEN sh.code = @first THEN '' ELSE '-' || sh.code END AS code
              FROM mfg.production_run r
              JOIN md.machine m ON m.machine_id = r.machine_id
              JOIN mfg.shift sh ON sh.shift_id = r.shift_id
              JOIN LATERAL (SELECT i.lot_prefix FROM qa.item_spec i WHERE i.company_id = r.company_id AND i.item_id = r.item_id ORDER BY i.version DESC LIMIT 1) s ON true
              WHERE r.company_id = @c AND r.run_id = @r AND m.short_code IS NOT NULL) x
            WHERE NOT EXISTS (SELECT 1 FROM mfg.fg_lot o WHERE o.company_id = @c AND o.field_code = x.code AND o.status <> 'VOIDED')
            """,
            cancellationToken,
            ("c", context.CompanyId),
            ("r", runId),
            ("first", Lab.FirstShift));

    /// <summary>Gives its code to every live lot that was waiting for a prefix or a short code; returns how many got one.</summary>
    public static async Task<int> AssignWaitingAsync(CommandContext context, CancellationToken cancellationToken)
    {
        await MfgSql.LockAsync(context, "lot-field-codes", cancellationToken).ConfigureAwait(false);
        return await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            $"""
            WITH candidate AS (SELECT DISTINCT ON (x.code) x.lot_id, x.code FROM ({Candidates}) x ORDER BY x.code, x.lot_id)
            UPDATE mfg.fg_lot f SET field_code = c.code, version = f.version + 1
            FROM candidate c
            WHERE f.lot_id = c.lot_id
              AND NOT EXISTS (SELECT 1 FROM mfg.fg_lot o WHERE o.company_id = f.company_id AND o.field_code = c.code AND o.status <> 'VOIDED')
            """,
            cancellationToken,
            ("c", context.CompanyId),
            ("first", Lab.FirstShift)).ConfigureAwait(false);
    }
}
