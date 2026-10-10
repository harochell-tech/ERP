using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Manufacturing.Quality;

/// <summary>
/// One specimen: measures in cm (a missing one is the item's nominal measure), weight and load in kg, the block's condition
/// (SECO_AL_AIRE, HUMEDO, SATURADO) and the failure type's code — both optional (E-LAB1-01-11).
/// </summary>
public sealed record CompressionSpecimen(
    decimal LoadKg, decimal? WidthCm = null, decimal? HeightCm = null, decimal? LengthCm = null, decimal? WeightKg = null, string? BlockCondition = null, string? FailureType = null,
    string? Notes = null);

/// <summary>
/// E-LAB1-01-6…10: the lab records the specimens of a lot broken on one date (1 to 30). The technician is the session's user; the gross
/// area and the gross strength are computed and kept; the lot may be in any status but VOIDED.
/// </summary>
public sealed record RecordCompressionTests(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid LotId, DateOnly BreakDate, IReadOnlyList<CompressionSpecimen> Specimens) : IPlantScopedCommand;

/// <summary>E-LAB1-01-9: a test is never edited — it is voided with a reason (step-up) and recorded again; the voided one stays visible.</summary>
public sealed record VoidCompressionTest(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid TestId, string Reason) : IPlantScopedCommand;

/// <summary>One block (ASTM C140), in kg: Ws saturated, Wi immersed, Wd oven-dry.</summary>
public sealed record AbsorptionBlock(decimal WsKg, decimal WiKg, decimal WdKg, string? Notes = null);

/// <summary>E-LAB1-01-12: the absorption and density blocks of a lot tested on one date (1 to 30); refused unless Ws &gt; Wi, Ws ≥ Wd and Wd &gt; 0.</summary>
public sealed record RecordAbsorptionTests(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid LotId, DateOnly TestDate, IReadOnlyList<AbsorptionBlock> Blocks) : IPlantScopedCommand;

public sealed record VoidAbsorptionTest(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid TestId, string Reason) : IPlantScopedCommand;

internal static class LabTests
{
    public const string Compression = "CompressionTest";
    public const string Absorption = "AbsorptionTest";

    public static async Task<Lab.LotRow> LotOfPlantAsync(CommandContext context, Guid plantId, Guid lotId, CancellationToken cancellationToken)
    {
        var lot = await Lab.LotAsync(context.Connection, context.Transaction, context.CompanyId, lotId, cancellationToken).ConfigureAwait(false);
        return lot.PlantId == plantId ? lot : throw new DomainException(ManufacturingErrors.PlantMismatch, "The lot belongs to another plant.");
    }

    public static void EnsureRows(int count, string what)
    {
        if (count is 0 or > Lab.MaxRows)
        {
            throw new DomainException(QualityErrors.TestInvalid, $"Send 1 to {Lab.MaxRows} {what}.");
        }
    }

    /// <summary>RECORDED → VOIDED with who, when and why; <paramref name="table"/> is one of the two test tables.</summary>
    public static async Task<string> VoidAsync(
        CommandContext context, string table, string aggregate, Guid plantId, Guid testId, string? reason, string commandType, CancellationToken cancellationToken)
    {
        var why = Lots.Lots.Reason(reason);
        var lotOfTest = await MfgSql.ScalarAsync<Guid?>(context, $"SELECT lot_id FROM {table} WHERE company_id = @c AND test_id = @t", cancellationToken, ("c", context.CompanyId), ("t", testId))
            .ConfigureAwait(false) ?? throw new DomainException(ManufacturingErrors.NotFound, "The test does not exist.");
        var locked = await Lots.Lots.LockAsync(context, plantId, lotOfTest, null, cancellationToken).ConfigureAwait(false);
        var test = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            $"""
            SELECT t.status, r.plant_id, t.lot_id FROM {table} t JOIN mfg.fg_lot f ON f.lot_id = t.lot_id JOIN mfg.production_run r ON r.run_id = f.run_id
            WHERE t.company_id = @c AND t.test_id = @t FOR UPDATE OF t
            """,
            r => Tuple.Create(r.GetString(0), r.GetGuid(1), r.GetGuid(2)),
            cancellationToken,
            ("c", context.CompanyId),
            ("t", testId)).ConfigureAwait(false)
            ?? throw new DomainException(ManufacturingErrors.NotFound, "The test does not exist.");
        var (status, plant, lotId) = test;
        if (plant != plantId)
        {
            throw new DomainException(ManufacturingErrors.PlantMismatch, "The test's lot belongs to another plant.");
        }

        if (status != "RECORDED")
        {
            throw new DomainException(ManufacturingErrors.InvalidState, "The test is already voided.");
        }

        var eventId = await context.AppendEventAsync(
            new EventDraft(aggregate + "Voided", 1, aggregate, testId, await MfgSql.NextEventVersionAsync(context, aggregate, testId, cancellationToken).ConfigureAwait(false),
                JsonSerializer.Serialize(new { testId, lotId, reason = why }), Publish: false),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            $"UPDATE {table} SET status = 'VOIDED', void_reason = @r, voided_by = @by, voided_at = @at WHERE test_id = @t",
            cancellationToken,
            ("r", why), ("by", await MfgSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false)), ("at", context.Clock.UtcNow), ("t", testId)).ConfigureAwait(false);
        await context.AppendStateAsync(aggregate, testId, "DOCUMENT", "RECORDED", "VOIDED", commandType, eventId, cancellationToken, why).ConfigureAwait(false);
        // E-LAB1-03-5: the certificates that showed this specimen are void from now on.
        IReadOnlyList<string> certificates = aggregate == Compression
            ? await LabCertificates.VoidForTestAsync(context, testId, why, commandType, cancellationToken).ConfigureAwait(false)
            : [];
        // E-LAB1-02-8: the lot is evaluated again without the voided test; a lot it had blocked stays blocked until Calidad unblocks it.
        var outcome = await LotEvaluator.EvaluateAsync(context, locked, "TESTS", commandType, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { testId, lotId, status = "VOIDED", certificatesVoided = certificates, evaluation = LotEvaluator.Result(outcome) });
    }
}

[RequiresPermission("lab_test:record")]
public sealed class RecordCompressionTestsHandler : ICommandHandler<RecordCompressionTests>
{
    public string CommandType => "Manufacturing.RecordCompressionTests";

    public async Task<string> HandleAsync(RecordCompressionTests command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var specimens = command.Specimens ?? [];
        LabTests.EnsureRows(specimens.Count, "specimens");
        var lot = await LabTests.LotOfPlantAsync(context, command.PlantId, command.LotId, cancellationToken).ConfigureAwait(false);
        var locked = await Lots.Lots.LockAsync(context, command.PlantId, command.LotId, null, cancellationToken).ConfigureAwait(false);
        var age = Lab.Age(lot, command.BreakDate, MfgSql.Today(context), "The break date");
        var spec = await Lab.SpecAsync(context.Connection, context.Transaction, context.CompanyId, lot.ItemId, cancellationToken).ConfigureAwait(false);
        var toMpa = await Lab.ParameterAsync(context.Connection, context.Transaction, context.CompanyId, "KGCM2_TO_MPA", cancellationToken).ConfigureAwait(false);
        var technician = await MfgSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var batch = context.ResultRef;
        var eventId = await context.AppendEventAsync(
            new EventDraft("CompressionTestsRecorded", 1, "CompressionTestBatch", batch, 1,
                JsonSerializer.Serialize(new { batchId = batch, lotId = lot.LotId, breakDate = command.BreakDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), specimens = specimens.Count }),
                Publish: false),
            cancellationToken).ConfigureAwait(false);

        var tests = new List<object>();
        var line = 0;
        foreach (var s in specimens)
        {
            line++;
            if (s is null || !BlockConditions.IsValid(s.BlockCondition))
            {
                throw new DomainException(QualityErrors.TestInvalid, $"Specimen {line}: the block's condition is SECO_AL_AIRE, HUMEDO or SATURADO.");
            }

            var width = Lab.Measure(s.WidthCm, $"Specimen {line}: the width");
            var height = Lab.Measure(s.HeightCm, $"Specimen {line}: the height");
            var length = Lab.Measure(s.LengthCm, $"Specimen {line}: the length");
            var weight = Lab.Measure(s.WeightKg, $"Specimen {line}: the weight");
            var load = Lab.Measure(s.LoadKg, $"Specimen {line}: the load")!.Value;
            var failure = string.IsNullOrWhiteSpace(s.FailureType) ? null : s.FailureType.Trim().ToUpperInvariant();
            if (failure is not null)
            {
                await Lab.EnsureFailureTypeAsync(context.Connection, context.Transaction, context.CompanyId, failure, cancellationToken).ConfigureAwait(false);
            }

            var result = Lab.Compute(width, height, length, load, spec, toMpa);
            var id = context.Ids.NewId();
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO qa.compression_test (test_id, company_id, lot_id, batch_id, line_no, break_date, age_days, width_cm, height_cm, length_cm, nominal_used, spec_version, weight_kg, load_kg,
                                                 gross_area_cm2, strength_kgcm2, block_condition, failure_type, notes, tested_by, recorded_at, status)
                VALUES (@id, @c, @lot, @batch, @line, @date, @age, @w, @h, @l, @nominal, @spec, @weight, @load, @area, @strength, @condition, @failure, @notes, @by, @at, 'RECORDED')
                """,
                cancellationToken,
                ("id", id), ("c", context.CompanyId), ("lot", lot.LotId), ("batch", batch), ("line", line), ("date", command.BreakDate), ("age", age), ("w", width), ("h", height), ("l", length),
                ("nominal", result.NominalUsed), ("spec", spec?.Version), ("weight", weight), ("load", load), ("area", result.GrossAreaCm2), ("strength", result.StrengthKgcm2),
                ("condition", s.BlockCondition), ("failure", failure), ("notes", Lab.Note(s.Notes, $"Specimen {line}: the note")), ("by", technician), ("at", context.Clock.UtcNow)).ConfigureAwait(false);
            await context.AppendStateAsync(LabTests.Compression, id, "DOCUMENT", null, "RECORDED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
            tests.Add(new
            {
                testId = id,
                nominalUsed = result.NominalUsed,
                grossAreaCm2 = Lab.Text(result.GrossAreaCm2),
                strengthKgcm2 = Lab.Text(result.StrengthKgcm2),
                strengthMpa = Lab.Text(result.StrengthMpa),
            });
        }

        // E-LAB1-02-1: the lot is evaluated again with what was just recorded; a NO CUMPLE blocks it here (E-LAB1-02-6).
        var outcome = await LotEvaluator.EvaluateAsync(context, locked, "TESTS", CommandType, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { batchId = batch, lotId = lot.LotId, fieldCode = lot.FieldCode, ageDays = age, tests, evaluation = LotEvaluator.Result(outcome) });
    }
}

[RequiresPermission("lab_test:record", StepUp = true)]
public sealed class VoidCompressionTestHandler : ICommandHandler<VoidCompressionTest>
{
    public string CommandType => "Manufacturing.VoidCompressionTest";

    public Task<string> HandleAsync(VoidCompressionTest command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        return LabTests.VoidAsync(context, "qa.compression_test", LabTests.Compression, command.PlantId, command.TestId, command.Reason, CommandType, cancellationToken);
    }
}

[RequiresPermission("lab_test:record")]
public sealed class RecordAbsorptionTestsHandler : ICommandHandler<RecordAbsorptionTests>
{
    public string CommandType => "Manufacturing.RecordAbsorptionTests";

    public async Task<string> HandleAsync(RecordAbsorptionTests command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var blocks = command.Blocks ?? [];
        LabTests.EnsureRows(blocks.Count, "blocks");
        var lot = await LabTests.LotOfPlantAsync(context, command.PlantId, command.LotId, cancellationToken).ConfigureAwait(false);
        var locked = await Lots.Lots.LockAsync(context, command.PlantId, command.LotId, null, cancellationToken).ConfigureAwait(false);
        Lab.Age(lot, command.TestDate, MfgSql.Today(context), "The test date");
        var technician = await MfgSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var batch = context.ResultRef;
        var eventId = await context.AppendEventAsync(
            new EventDraft("AbsorptionTestsRecorded", 1, "AbsorptionTestBatch", batch, 1,
                JsonSerializer.Serialize(new { batchId = batch, lotId = lot.LotId, testDate = command.TestDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), blocks = blocks.Count }),
                Publish: false),
            cancellationToken).ConfigureAwait(false);

        var tests = new List<Guid>();
        var line = 0;
        foreach (var b in blocks)
        {
            line++;
            if (b is null || b.WiKg < 0m || !(b.WsKg > b.WiKg && b.WsKg >= b.WdKg && b.WdKg > 0m)
                || decimal.Round(b.WsKg, Lab.Decimals) != b.WsKg || decimal.Round(b.WiKg, Lab.Decimals) != b.WiKg || decimal.Round(b.WdKg, Lab.Decimals) != b.WdKg)
            {
                throw new DomainException(QualityErrors.TestInvalid, $"Block {line}: the saturated weight is above the immersed one and not below the dry one, the dry one above zero (kg, 6 decimals at most).");
            }

            var id = context.Ids.NewId();
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO qa.absorption_test (test_id, company_id, lot_id, batch_id, line_no, test_date, ws_kg, wi_kg, wd_kg, notes, tested_by, recorded_at, status)
                VALUES (@id, @c, @lot, @batch, @line, @date, @ws, @wi, @wd, @notes, @by, @at, 'RECORDED')
                """,
                cancellationToken,
                ("id", id), ("c", context.CompanyId), ("lot", lot.LotId), ("batch", batch), ("line", line), ("date", command.TestDate), ("ws", b.WsKg), ("wi", b.WiKg), ("wd", b.WdKg),
                ("notes", Lab.Note(b.Notes, $"Block {line}: the note")), ("by", technician), ("at", context.Clock.UtcNow)).ConfigureAwait(false);
            await context.AppendStateAsync(LabTests.Absorption, id, "DOCUMENT", null, "RECORDED", CommandType, eventId, cancellationToken).ConfigureAwait(false);
            tests.Add(id);
        }

        var outcome = await LotEvaluator.EvaluateAsync(context, locked, "TESTS", CommandType, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { batchId = batch, lotId = lot.LotId, fieldCode = lot.FieldCode, tests, evaluation = LotEvaluator.Result(outcome) });
    }
}

[RequiresPermission("lab_test:record", StepUp = true)]
public sealed class VoidAbsorptionTestHandler : ICommandHandler<VoidAbsorptionTest>
{
    public string CommandType => "Manufacturing.VoidAbsorptionTest";

    public Task<string> HandleAsync(VoidAbsorptionTest command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        return LabTests.VoidAsync(context, "qa.absorption_test", LabTests.Absorption, command.PlantId, command.TestId, command.Reason, CommandType, cancellationToken);
    }
}
