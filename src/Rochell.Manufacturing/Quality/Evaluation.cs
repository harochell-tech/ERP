using System.Globalization;
using System.Text.Json;
using Rochell.Manufacturing.Lots;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Manufacturing.Quality;

// LAB1-02 (E-LAB1-4, 5, 10; E-LAB1-02-1…10, 13): the lot's evaluation (baseline §4.2), kept every time its tests change; a NO CUMPLE
// — real or estimated — blocks the lot from CURING, RELEASED or FINAL_RELEASED; only a CUMPLE on real data allows the final release.

public static class Verdicts
{
    public const string Complies = "COMPLIES";
    public const string Fails = "FAILS";
    public const string NoSpec = "NO_SPEC";
    public const string NoData = "NO_DATA";

    /// <summary>E-LAB1-16: the item is not tested (Block 4", not structural).</summary>
    public const string NotTested = "NOT_TESTED";
}

/// <summary>E-LAB1-11/12/15: a release check — enough specimens at the age and the average at least the requirement.</summary>
public static class ReleaseChecks
{
    public const string Pass = "PASS";
    public const string Fail = "FAIL";
    public const string FewSpecimens = "FEW_SPECIMENS";
}

/// <summary>
/// LAB1-03d (E-LAB1-11…16): the two checks of a lot — the initial release on the breaks at 1 day (or a retest up to
/// <c>INITIAL_RELEASE_MAX_AGE</c>, the best that passes) against the item's 1-day minimum average, and the final release on the breaks at
/// <c>FINAL_RELEASE_AGE</c> (3 days) against its 3-day minimum; each needs <c>MIN_SPECIMENS</c> valid specimens. An item that is not tested has none.
/// </summary>
internal static class EarlyChecks
{
    public sealed record Requirements(bool Tested, decimal? MinAvg1d, decimal? MinAvg3d, decimal? MinAvg28d);

    public sealed record Check(string Result, int Age, int Specimens, decimal Average, decimal Required);

    public sealed record Checks(Requirements Spec, Check? Initial, Check? Final);

    private sealed record AgeRow(int Age, int Specimens, decimal Average);

    public static async Task<Checks> ComputeAsync(CommandContext context, Guid lotId, Guid itemId, CancellationToken cancellationToken)
    {
        var spec = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction,
            "SELECT lab_tested, min_avg_1d, min_avg_3d, min_avg_28d FROM qa.item_spec WHERE company_id = @c AND item_id = @i ORDER BY version DESC LIMIT 1",
            r => new Requirements(r.GetBoolean(0), r.NullableDecimal(1), r.NullableDecimal(2), r.NullableDecimal(3)), cancellationToken, ("c", context.CompanyId), ("i", itemId)).ConfigureAwait(false)
            ?? new Requirements(true, null, null, null);
        var maxInitial = (int)await Lab.ParameterAsync(context.Connection, context.Transaction, context.CompanyId, "INITIAL_RELEASE_MAX_AGE", cancellationToken).ConfigureAwait(false);
        var finalAge = (int)await Lab.ParameterAsync(context.Connection, context.Transaction, context.CompanyId, "FINAL_RELEASE_AGE", cancellationToken).ConfigureAwait(false);
        var minSpecimens = (int)await Lab.ParameterAsync(context.Connection, context.Transaction, context.CompanyId, "MIN_SPECIMENS", cancellationToken).ConfigureAwait(false);
        var ages = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT age_days, count(*)::int, round(avg(strength_kgcm2), 6) FROM qa.compression_test
            WHERE company_id = @c AND lot_id = @l AND status = 'RECORDED' AND ((age_days BETWEEN 1 AND @mi) OR age_days = @fa)
            GROUP BY age_days ORDER BY age_days
            """,
            r => new AgeRow(r.GetInt32(0), r.GetInt32(1), r.GetDecimal(2)),
            cancellationToken,
            ("c", context.CompanyId), ("l", lotId), ("mi", maxInitial), ("fa", finalAge)).ConfigureAwait(false);
        Check Judge(AgeRow row, decimal required)
            => new(row.Average < required ? ReleaseChecks.Fail : row.Specimens < minSpecimens ? ReleaseChecks.FewSpecimens : ReleaseChecks.Pass, row.Age, row.Specimens, row.Average, required);
        Check? initial = null, final = null;
        if (spec.Tested && spec.MinAvg1d is { } r1)
        {
            var judged = ages.Where(a => a.Age >= 1 && a.Age <= maxInitial).Select(a => Judge(a, r1)).ToList();
            initial = judged.FirstOrDefault(j => j.Result == ReleaseChecks.Pass) ?? judged.LastOrDefault();
        }

        if (spec.Tested && spec.MinAvg3d is { } r3 && ages.FirstOrDefault(a => a.Age == finalAge) is { } row3)
        {
            final = Judge(row3, r3);
        }

        return new Checks(spec, initial, final);
    }

    public static string Words(Check check) => string.Create(
        CultureInfo.InvariantCulture,
        $"{check.Specimens} probeta(s) a {check.Age} día(s), promedio {check.Average:0.00} kg/cm² (mínimo {check.Required:0.00}, al menos las probetas que pide el laboratorio)");
}

public static class EvaluationBasis
{
    public const string Real = "REAL";
    public const string Estimated = "ESTIMATED";
}

public static class LotAlerts
{
    public const string NoTests = "NO_TESTS";
    public const string FewSpecimens = "FEW_SPECIMENS";
    public const string HighCv = "HIGH_CV";
    public const string HighAbsorption = "HIGH_ABSORPTION";
}

/// <summary>E-LAB1-02-2: Calidad asks to evaluate one lot again, with the requirements and parameters in force now.</summary>
public sealed record ReevaluateLot(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid LotId) : IPlantScopedCommand;

/// <summary>
/// E-LAB1-4, E-LAB1-02-10, E-LAB1-12: Calidad releases for good a RELEASED lot whose breaks at 3 days reach the item's 3-day minimum, or whose
/// evaluation in force is CUMPLE on real 28-day data (step-up). The 28-day breaks still verify it afterwards (E-LAB1-13).
/// </summary>
public sealed record FinalReleaseLot(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid LotId, long ExpectedVersion) : IPlantScopedCommand;

internal static class LotEvaluator
{
    public sealed record Outcome(Guid EvaluationId, string Verdict, string? Basis, decimal? Strength28d, decimal? Min28d, IReadOnlyList<string> Alerts, bool Blocked);

    private sealed record Numbers(
        int Specimens, int? AgeMin, int? AgeMax, decimal? Avg, decimal? Min, decimal? Max, decimal? StdDev, int? EarlyAge, decimal? EarlyAvg, decimal? EarlyMin, decimal? RealAvg, decimal? RealMin,
        decimal MinSpecimens, decimal MaxCv);

    private sealed record Spec(int Version, decimal? MinAvg, decimal? MinIndividual);

    private sealed record Factor(decimal Value, string Source);

    private sealed record Absorption(decimal Average, decimal Limit);

    private sealed record Previous(string Verdict, string? Basis, string? FinalCheck);

    private static decimal Six(decimal value) => decimal.Round(value, Lab.Decimals, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Evaluates the lot with its valid tests and keeps the evaluation. <paramref name="lot"/> must be locked by the caller
    /// (<see cref="Lots.Lots.LockAsync"/>): evaluations of one lot never run side by side.
    /// </summary>
    public static async Task<Outcome> EvaluateAsync(CommandContext context, Lots.Lots.Lot lot, string cause, string commandType, CancellationToken cancellationToken)
    {
        var n = (await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            WITH p AS (SELECT qa.parameter_number(@c, 'AGE_28D_MIN_DAYS') AS a28, qa.parameter_number(@c, 'INITIAL_RELEASE_MAX_AGE') AS imax),
            t AS (SELECT age_days, strength_kgcm2 AS s FROM qa.compression_test WHERE company_id = @c AND lot_id = @l AND status = 'RECORDED'),
            -- E-LAB1-18: the latest early age after the initial-release ages; breaks at 1…imax days never estimate.
            e AS (SELECT max(t.age_days) AS age FROM t, p WHERE t.age_days > p.imax AND t.age_days < p.a28)
            SELECT (SELECT count(*)::int FROM t), (SELECT min(age_days) FROM t), (SELECT max(age_days) FROM t),
                   (SELECT round(avg(s), 6) FROM t), (SELECT min(s) FROM t), (SELECT max(s) FROM t), (SELECT round(stddev_samp(s), 6) FROM t),
                   (SELECT age FROM e),
                   (SELECT round(avg(t.s), 6) FROM t, e WHERE t.age_days = e.age), (SELECT min(t.s) FROM t, e WHERE t.age_days = e.age),
                   (SELECT round(avg(t.s), 6) FROM t, p WHERE t.age_days >= p.a28), (SELECT min(t.s) FROM t, p WHERE t.age_days >= p.a28),
                   qa.parameter_number(@c, 'MIN_SPECIMENS'), qa.parameter_number(@c, 'MAX_CV')
            """,
            r => new Numbers(
                r.GetInt32(0), NullableInt(r, 1), NullableInt(r, 2), r.NullableDecimal(3), r.NullableDecimal(4), r.NullableDecimal(5), r.NullableDecimal(6), NullableInt(r, 7), r.NullableDecimal(8),
                r.NullableDecimal(9), r.NullableDecimal(10), r.NullableDecimal(11), r.GetDecimal(12), r.GetDecimal(13)),
            cancellationToken,
            ("c", context.CompanyId),
            ("l", lot.LotId)).ConfigureAwait(false))!;
        var spec = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction,
            "SELECT version, min_avg_28d, min_individual_28d FROM qa.item_spec WHERE company_id = @c AND item_id = @i ORDER BY version DESC LIMIT 1",
            r => new Spec(r.GetInt32(0), r.NullableDecimal(1), r.NullableDecimal(2)), cancellationToken, ("c", context.CompanyId), ("i", lot.ItemId)).ConfigureAwait(false);
        var absorption = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT a.absorption,
                   CASE WHEN a.density < qa.parameter_number(@c, 'DENSITY_MEDIUM_FROM') THEN qa.parameter_number(@c, 'ABSORPTION_MAX_LIGHT')
                        WHEN a.density < qa.parameter_number(@c, 'DENSITY_NORMAL_FROM') THEN qa.parameter_number(@c, 'ABSORPTION_MAX_MEDIUM')
                        ELSE qa.parameter_number(@c, 'ABSORPTION_MAX_NORMAL') END
            FROM (SELECT round(avg(absorption_kgm3), 6) AS absorption, round(avg(density_kgm3), 6) AS density, count(*) AS blocks
                  FROM qa.absorption_test WHERE company_id = @c AND lot_id = @l AND status = 'RECORDED') a
            WHERE a.blocks > 0
            """,
            r => new Absorption(r.GetDecimal(0), r.GetDecimal(1)),
            cancellationToken,
            ("c", context.CompanyId),
            ("l", lot.LotId)).ConfigureAwait(false);

        // Baseline §4.2: real when there are breaks at the 28-day age; otherwise estimated from the early age ÷ its factor.
        decimal? strength = null, minimum = null;
        string? basis = null;
        Factor? factor = null;
        if (n.RealAvg is not null)
        {
            (strength, minimum, basis) = (n.RealAvg, n.RealMin, EvaluationBasis.Real);
        }
        else if (n.EarlyAge is not null)
        {
            factor = (await Reading.SingleOrDefaultAsync(
                context.Connection, context.Transaction, "SELECT factor, source FROM qa.age_factor(@c, @a)", r => new Factor(r.GetDecimal(0), r.GetString(1)), cancellationToken,
                ("c", context.CompanyId), ("a", n.EarlyAge.Value)).ConfigureAwait(false))!;
            (strength, minimum, basis) = (Six(n.EarlyAvg!.Value / factor.Value), Six(n.EarlyMin!.Value / factor.Value), EvaluationBasis.Estimated);
        }

        decimal? cv = n.StdDev is not null && n.Avg is > 0m ? Six(n.StdDev.Value / n.Avg.Value) : null;
        var alerts = new List<string>();
        if (n.Specimens == 0)
        {
            alerts.Add(LotAlerts.NoTests);
        }
        else if (n.Specimens < n.MinSpecimens)
        {
            alerts.Add(LotAlerts.FewSpecimens);
        }

        if (cv > n.MaxCv)
        {
            alerts.Add(LotAlerts.HighCv);
        }

        if (absorption is not null && absorption.Average > absorption.Limit)
        {
            alerts.Add(LotAlerts.HighAbsorption);
        }

        // LAB1-03d (E-LAB1-11…16): the release checks; an item that is not tested has neither, nor a verdict but NOT_TESTED.
        var checks = await EarlyChecks.ComputeAsync(context, lot.LotId, lot.ItemId, cancellationToken).ConfigureAwait(false);
        if (!checks.Spec.Tested)
        {
            alerts.RemoveAll(a => a is LotAlerts.NoTests or LotAlerts.FewSpecimens);
        }

        var verdict = !checks.Spec.Tested
            ? Verdicts.NotTested
            : spec?.MinAvg is not { } required
            ? Verdicts.NoSpec
            : strength is null
                ? Verdicts.NoData
                : strength >= required && (spec.MinIndividual is null || minimum >= spec.MinIndividual) ? Verdicts.Complies : Verdicts.Fails;

        var previous = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, "SELECT verdict, basis, final_check FROM qa.lot_evaluation WHERE company_id = @c AND lot_id = @l ORDER BY seq DESC LIMIT 1",
            r => new Previous(r.GetString(0), r.NullableString(1), r.NullableString(2)), cancellationToken, ("c", context.CompanyId), ("l", lot.LotId)).ConfigureAwait(false);
        var id = context.Ids.NewId();
        var by = await MfgSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO qa.lot_evaluation (evaluation_id, company_id, lot_id, seq, cause, specimens, age_min, age_max, avg_strength, min_strength, max_strength, std_dev, cv, early_age, early_avg,
                                           early_min, real_avg_28d, real_min_28d, factor_used, factor_source, strength_28d, min_28d, basis, spec_version, min_avg_required,
                                           min_individual_required, absorption_kgm3, absorption_limit_kgm3, verdict, alerts, evaluated_by, evaluated_at,
                                           initial_check, initial_age, initial_specimens, initial_avg, initial_required, final_check, final_specimens, final_avg, final_required)
            SELECT @id, @c, @l, coalesce((SELECT max(seq) FROM qa.lot_evaluation WHERE lot_id = @l), 0) + 1, @cause, @n, @amin, @amax, @avg, @min, @max, @sd, @cv, @eage, @eavg, @emin, @ravg, @rmin,
                   @factor, @fsource, @s28, @m28, @basis, @spec, @reqavg, @reqone, @abs, @abslimit, @verdict, @alerts, @by, @at,
                   @ic, @ia, @in, @iavg, @ireq, @fc, @fn, @favg, @freq
            """,
            cancellationToken,
            ("id", id), ("c", context.CompanyId), ("l", lot.LotId), ("cause", cause), ("n", n.Specimens), ("amin", n.AgeMin), ("amax", n.AgeMax), ("avg", n.Avg), ("min", n.Min), ("max", n.Max),
            ("sd", n.StdDev), ("cv", cv), ("eage", n.EarlyAge), ("eavg", n.EarlyAvg), ("emin", n.EarlyMin), ("ravg", n.RealAvg), ("rmin", n.RealMin), ("factor", factor?.Value),
            ("fsource", factor?.Source), ("s28", strength), ("m28", minimum), ("basis", basis), ("spec", spec?.Version), ("reqavg", spec?.MinAvg), ("reqone", spec?.MinIndividual),
            ("abs", absorption?.Average), ("abslimit", absorption?.Limit), ("verdict", verdict), ("alerts", alerts.ToArray()), ("by", by), ("at", context.Clock.UtcNow),
            ("ic", checks.Initial?.Result), ("ia", checks.Initial?.Age), ("in", checks.Initial?.Specimens), ("iavg", checks.Initial?.Average), ("ireq", checks.Initial?.Required),
            ("fc", checks.Final?.Result), ("fn", checks.Final?.Specimens), ("favg", checks.Final?.Average), ("freq", checks.Final?.Required)).ConfigureAwait(false);
        await context.AppendEventAsync(
            new EventDraft("LotEvaluated", 1, "LotEvaluation", id, 1,
                JsonSerializer.Serialize(new
                {
                    evaluationId = id,
                    lotId = lot.LotId,
                    verdict,
                    basis,
                    strength28d = strength is null ? null : Lab.Text(strength.Value),
                    alerts,
                }),
                Publish: false),
            cancellationToken).ConfigureAwait(false);

        // E-LAB1-4, 5; E-LAB1-02-6, 7, 9: a NO CUMPLE blocks a live lot — once per verdict, and again when an estimate becomes real.
        var live = lot.Status is ("CURING" or "RELEASED" or "FINAL_RELEASED");
        var fails28 = verdict == Verdicts.Fails
            && (previous is null || previous.Verdict != Verdicts.Fails || (previous.Basis == EvaluationBasis.Estimated && basis == EvaluationBasis.Real));
        // E-LAB1-15: breaks at 3 days below the 3-day minimum block the lot, once.
        var fails3 = checks.Final is { Result: ReleaseChecks.Fail } && previous?.FinalCheck != ReleaseChecks.Fail;
        var blocks = live && (fails28 || fails3);
        if (blocks)
        {
            var reason = fails28
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"Laboratorio: NO CUMPLE ({(basis == EvaluationBasis.Real ? "real" : "estimado")}); resistencia a 28 d {strength:0.00} kg/cm², mínimo {minimum:0.00}; requisito {spec!.MinAvg:0.00}{(spec.MinIndividual is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" / {spec.MinIndividual:0.00}"))}.")
                : $"Laboratorio: no llega al mínimo de la liberación final; {EarlyChecks.Words(checks.Final!)}.";
            await LotBlocks.BlockAsync(context, lot, new Lots.Lots.Block("LAB", id), reason, commandType, cancellationToken).ConfigureAwait(false);
        }

        return new Outcome(id, verdict, basis, strength, minimum, alerts, blocks);
    }

    public static object Result(Outcome outcome) => new
    {
        evaluationId = outcome.EvaluationId,
        verdict = outcome.Verdict,
        basis = outcome.Basis,
        strength28d = outcome.Strength28d is null ? null : Lab.Text(outcome.Strength28d.Value),
        min28d = outcome.Min28d is null ? null : Lab.Text(outcome.Min28d.Value),
        alerts = outcome.Alerts,
        blocked = outcome.Blocked,
    };

    private static int? NullableInt(System.Data.Common.DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}

[RequiresPermission("lab_spec:manage")]
public sealed class ReevaluateLotHandler : ICommandHandler<ReevaluateLot>
{
    public string CommandType => "Manufacturing.ReevaluateLot";

    public async Task<string> HandleAsync(ReevaluateLot command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var lot = await Lots.Lots.LockAsync(context, command.PlantId, command.LotId, null, cancellationToken).ConfigureAwait(false);
        if (lot.Status == "VOIDED")
        {
            throw new DomainException(ManufacturingErrors.InvalidState, "The lot is VOIDED: its run was reversed.");
        }

        var outcome = await LotEvaluator.EvaluateAsync(context, lot, "REEVALUATION", CommandType, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { lotId = lot.LotId, evaluation = LotEvaluator.Result(outcome) });
    }
}

[RequiresPermission("fg_lot:final_release", StepUp = true)]
public sealed class FinalReleaseLotHandler : ICommandHandler<FinalReleaseLot>
{
    private sealed record InForce(Guid EvaluationId, string Verdict, string? Basis, string[] Alerts);

    public string CommandType => "Manufacturing.FinalReleaseLot";

    public async Task<string> HandleAsync(FinalReleaseLot command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var lot = await Lots.Lots.LockAsync(context, command.PlantId, command.LotId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (lot.Status != "RELEASED")
        {
            throw new DomainException(ManufacturingErrors.InvalidState, $"The lot is {lot.Status}; the final release is for a lot already released from curing.");
        }

        var evaluation = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, "SELECT evaluation_id, verdict, basis, alerts FROM qa.lot_evaluation WHERE company_id = @c AND lot_id = @l ORDER BY seq DESC LIMIT 1",
            r => new InForce(r.GetGuid(0), r.GetString(1), r.NullableString(2), r.GetFieldValue<string[]>(3)), cancellationToken, ("c", context.CompanyId), ("l", lot.LotId)).ConfigureAwait(false);
        // E-LAB1-12/16: the breaks at 3 days against the 3-day minimum, or (as before) a CUMPLE on real 28-day data; never an untested item.
        var checks = await EarlyChecks.ComputeAsync(context, lot.LotId, lot.ItemId, cancellationToken).ConfigureAwait(false);
        if (!checks.Spec.Tested)
        {
            throw new DomainException(QualityErrors.FinalReleaseRefused, "The item is not tested (not structural): its visual release is its only one (E-LAB1-16).");
        }

        var byThreeDays = checks.Final is { Result: ReleaseChecks.Pass };
        if (!byThreeDays && evaluation is not { Verdict: Verdicts.Complies, Basis: EvaluationBasis.Real })
        {
            throw new DomainException(
                QualityErrors.FinalReleaseRefused,
                evaluation switch
                {
                    _ when checks.Final is { } f => $"The breaks at {f.Age} days do not allow it: {f.Specimens} specimen(s), average {Lab.Text(f.Average)} against {Lab.Text(f.Required)} kg/cm² (E-LAB1-12).",
                    _ when checks.Spec.MinAvg3d is not null => "The lot has no breaks at 3 days yet (E-LAB1-12).",
                    null => "The lot has no tests yet.",
                    { Verdict: Verdicts.Complies } => "The lot complies only by estimate; the final release needs breaks at the 28-day age.",
                    { Verdict: Verdicts.NoSpec } => "The item has no 28-day requirement: there is nothing to release against.",
                    { Verdict: Verdicts.NoData } => "The lot has no 28-day strength yet.",
                    _ => "The lot does not comply.",
                });
        }

        var now = context.Clock.UtcNow;
        var by = await MfgSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        await Lots.Lots.TransitionAsync(
            context, lot, "FINAL_RELEASED", "LotFinalReleased",
            new { lotId = lot.LotId, lotCode = lot.LotCode, evaluationId = evaluation?.EvaluationId, alerts = evaluation?.Alerts ?? [], basis = byThreeDays ? "3_DAYS" : "28_DAYS" }, CommandType,
            cancellationToken, finalRelease: (by, now)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            lotId = lot.LotId,
            status = "FINAL_RELEASED",
            version = lot.Version + 1,
            evaluationId = evaluation?.EvaluationId,
            alerts = evaluation?.Alerts ?? [],
            basis = byThreeDays ? "3_DAYS" : "28_DAYS",
        });
    }
}
