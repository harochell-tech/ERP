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
/// E-LAB1-4, E-LAB1-02-10: Calidad releases for good a RELEASED lot whose evaluation in force is CUMPLE on real data (step-up).
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

    private sealed record Previous(string Verdict, string? Basis);

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
            WITH p AS (SELECT qa.parameter_number(@c, 'AGE_28D_MIN_DAYS') AS a28),
            t AS (SELECT age_days, strength_kgcm2 AS s FROM qa.compression_test WHERE company_id = @c AND lot_id = @l AND status = 'RECORDED'),
            e AS (SELECT min(t.age_days) AS age FROM t, p WHERE t.age_days >= 1 AND t.age_days < p.a28)
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

        var verdict = spec?.MinAvg is not { } required
            ? Verdicts.NoSpec
            : strength is null
                ? Verdicts.NoData
                : strength >= required && (spec.MinIndividual is null || minimum >= spec.MinIndividual) ? Verdicts.Complies : Verdicts.Fails;

        var previous = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, "SELECT verdict, basis FROM qa.lot_evaluation WHERE company_id = @c AND lot_id = @l ORDER BY seq DESC LIMIT 1",
            r => new Previous(r.GetString(0), r.NullableString(1)), cancellationToken, ("c", context.CompanyId), ("l", lot.LotId)).ConfigureAwait(false);
        var id = context.Ids.NewId();
        var by = await MfgSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO qa.lot_evaluation (evaluation_id, company_id, lot_id, seq, cause, specimens, age_min, age_max, avg_strength, min_strength, max_strength, std_dev, cv, early_age, early_avg,
                                           early_min, real_avg_28d, real_min_28d, factor_used, factor_source, strength_28d, min_28d, basis, spec_version, min_avg_required,
                                           min_individual_required, absorption_kgm3, absorption_limit_kgm3, verdict, alerts, evaluated_by, evaluated_at)
            SELECT @id, @c, @l, coalesce((SELECT max(seq) FROM qa.lot_evaluation WHERE lot_id = @l), 0) + 1, @cause, @n, @amin, @amax, @avg, @min, @max, @sd, @cv, @eage, @eavg, @emin, @ravg, @rmin,
                   @factor, @fsource, @s28, @m28, @basis, @spec, @reqavg, @reqone, @abs, @abslimit, @verdict, @alerts, @by, @at
            """,
            cancellationToken,
            ("id", id), ("c", context.CompanyId), ("l", lot.LotId), ("cause", cause), ("n", n.Specimens), ("amin", n.AgeMin), ("amax", n.AgeMax), ("avg", n.Avg), ("min", n.Min), ("max", n.Max),
            ("sd", n.StdDev), ("cv", cv), ("eage", n.EarlyAge), ("eavg", n.EarlyAvg), ("emin", n.EarlyMin), ("ravg", n.RealAvg), ("rmin", n.RealMin), ("factor", factor?.Value),
            ("fsource", factor?.Source), ("s28", strength), ("m28", minimum), ("basis", basis), ("spec", spec?.Version), ("reqavg", spec?.MinAvg), ("reqone", spec?.MinIndividual),
            ("abs", absorption?.Average), ("abslimit", absorption?.Limit), ("verdict", verdict), ("alerts", alerts.ToArray()), ("by", by), ("at", context.Clock.UtcNow)).ConfigureAwait(false);
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
        var blocks = verdict == Verdicts.Fails
            && lot.Status is ("CURING" or "RELEASED" or "FINAL_RELEASED")
            && (previous is null || previous.Verdict != Verdicts.Fails || (previous.Basis == EvaluationBasis.Estimated && basis == EvaluationBasis.Real));
        if (blocks)
        {
            var reason = string.Create(
                CultureInfo.InvariantCulture,
                $"Laboratorio: NO CUMPLE ({(basis == EvaluationBasis.Real ? "real" : "estimado")}); resistencia a 28 d {strength:0.00} kg/cm², mínimo {minimum:0.00}; requisito {spec!.MinAvg:0.00}{(spec.MinIndividual is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" / {spec.MinIndividual:0.00}"))}.");
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
        if (evaluation is not { Verdict: Verdicts.Complies, Basis: EvaluationBasis.Real })
        {
            throw new DomainException(
                QualityErrors.FinalReleaseRefused,
                evaluation switch
                {
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
            context, lot, "FINAL_RELEASED", "LotFinalReleased", new { lotId = lot.LotId, lotCode = lot.LotCode, evaluationId = evaluation.EvaluationId, alerts = evaluation.Alerts }, CommandType,
            cancellationToken, finalRelease: (by, now)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { lotId = lot.LotId, status = "FINAL_RELEASED", version = lot.Version + 1, evaluationId = evaluation.EvaluationId, alerts = evaluation.Alerts });
    }
}
