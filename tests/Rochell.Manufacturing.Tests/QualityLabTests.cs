using System.Text.Json;
using Rochell.Identity.Authorization;
using Rochell.Manufacturing.Machines;
using Rochell.Manufacturing.Quality;
using Rochell.Manufacturing.Queries;
using Rochell.Manufacturing.Recipes;
using Rochell.Manufacturing.Runs;
using Rochell.Manufacturing.Shifts;
using Rochell.Platform.Commands;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Manufacturing.Tests;

/// <summary>
/// LAB1-01 (E-LAB1-1…10, E-LAB1-01-1…15): the lot's field code and the machine's short code; per-item requirements, parameters and
/// failure types; compression tests per specimen and absorption tests per block, recorded by the lab and voided with a reason.
/// Expected values come from the validated Excel (docs/architecture/lab1/reference/historico_ensayos.csv, lot 8160924P1).
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class QualityLabTests(PostgresFixture postgres)
{
    private static JsonElement Json(string payload) => JsonDocument.Parse(payload).RootElement;

    private static string Code(DateOnly day, string machine, string? shift = null) => $"8{day:ddMMyy}{machine}{(shift is null ? string.Empty : "-" + shift)}";

    /// <summary>Block 8": prefix 8, nominal 19.5 × 19.5 × 39.5 cm, 55 % net area (the Excel's CONFIG).</summary>
    private static Task<CommandResult> SpecAsync(TestHarness h, Guid quality, ProductionRunTests.Setup s, string key, decimal? minAvg = null)
        => h.RunAsync(new SetItemSpec(h.CompanyId, quality, key, s.Block, "8", 19.5m, 19.5m, 39.5m, 0.55m, minAvg), new SetItemSpecHandler());

    private static async Task<JsonElement> PostAsync(TestHarness h, ProductionRunTests.Setup s, Guid machine, Guid shift, string key)
    {
        var run = (await h.RunAsync(new StartProductionRun(h.CompanyId, s.Supervisor, key, s.Plant, machine, shift, s.Today, s.Block), new StartProductionRunHandler())).ResultRef;
        var summary = Json((await ProductionRunTests.Record(h, s, run, key + "-rec")).ResultPayload);
        return Json((await h.RunAsync(new PostShiftSummary(h.CompanyId, s.Manager, key + "-post", s.Plant, run, summary.GetProperty("version").GetInt64()), new PostShiftSummaryHandler())).ResultPayload);
    }

    private static async Task<JsonElement> LotAsync(TestHarness h, Guid session, Guid lot)
        => Json(await h.QueryAsync(new GetLabLot(h.CompanyId, session, lot), new GetLabLotHandler()));

    [Trait("AcceptanceLab1", "LAB-01")]
    [Trait("AcceptanceLab1", "LAB-02")]
    [Fact]
    public async Task Five_specimens_at_three_days_give_the_excels_areas_and_strengths_and_the_one_without_measures_uses_the_nominal_ones()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var s = await ProductionRunTests.SetupAsync(h);
        var quality = await h.SessionWithRolesAsync("CALIDAD");
        await SpecAsync(h, quality, s, "spec");
        await h.RunAsync(new SetMachineShortCode(h.CompanyId, quality, "p1", s.Plant, s.Machine, 1, " p1 "), new SetMachineShortCodeHandler());
        var posted = await PostAsync(h, s, s.Machine, s.Day, "run");
        var lot = posted.GetProperty("lotId").GetGuid();
        clock.Advance(TimeSpan.FromDays(3));
        var lab = await h.SessionWithRolesAsync("LABORATORIO");
        var breakDate = s.Today.AddDays(3);
        CompressionSpecimen[] specimens =
        [
            new(75187m, 19.4m, 19.4m, 39.4m, BlockCondition: BlockConditions.AirDry, FailureType: "conica"), new(61116m, 19.4m, 19.4m, 39.4m), new(55574m, 19.4m, 19.4m, 39.4m),
            new(59348m, 19.4m, 19.4m, 39.4m, WeightKg: 16.8m, Notes: " Cara irregular "), new(44000m),
        ];

        var preview = Json(await h.QueryAsync(new PreviewCompressionTests(h.CompanyId, lab, lot, breakDate, [.. specimens, new(0m)]), new PreviewCompressionTestsHandler()));
        var early = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RecordCompressionTests(h.CompanyId, lab, "early", s.Plant, lot, s.Today.AddDays(-1), specimens), new RecordCompressionTestsHandler()));
        var future = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RecordCompressionTests(h.CompanyId, lab, "future", s.Plant, lot, breakDate.AddDays(1), specimens), new RecordCompressionTestsHandler()));
        var unknownFailure = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new RecordCompressionTests(h.CompanyId, lab, "failure", s.Plant, lot, breakDate, [new(44000m, FailureType: "EXPLOSIVA")]), new RecordCompressionTestsHandler()));
        var supervisor = await h.SessionWithRolesAsync("SUPERVISOR_PRODUCCION");
        var notTheLab = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RecordCompressionTests(h.CompanyId, supervisor, "sup", s.Plant, lot, breakDate, specimens), new RecordCompressionTestsHandler()));
        var recorded = Json((await h.RunAsync(new RecordCompressionTests(h.CompanyId, lab, "rec", s.Plant, lot, breakDate, specimens), new RecordCompressionTestsHandler())).ResultPayload);
        var detail = await LotAsync(h, supervisor, lot);
        var tests = detail.GetProperty("compression").EnumerateArray().ToList();

        Assert.Equal((QualityErrors.TestInvalid, QualityErrors.TestInvalid, QualityErrors.TestInvalid, AuthorizationErrors.NotAuthorized), (early.Code, future.Code, unknownFailure.Code, notTheLab.Code));
        Assert.Equal(Code(s.Today, "P1", "DIA"), posted.GetProperty("fieldCode").GetString());
        Assert.Equal((Code(s.Today, "P1", "DIA"), 3), (recorded.GetProperty("fieldCode").GetString(), recorded.GetProperty("ageDays").GetInt32()));
        // Area = width × length: 19.4 × 39.4 = 764.36; the specimen without measures, 19.5 × 39.5 = 770.25 (nominal). Strength = load ÷ area:
        // 75,187 ÷ 764.36 = 98.365953; 61,116 → 79.957088; 55,574 → 72.706578; 59,348 → 77.644042; 44,000 ÷ 770.25 = 57.124310.
        // MPa = kg/cm² × 0.0980665: 9.646405, 7.841112, 7.130080, 7.614279, 5.601981.
        Assert.Equal(
            "false:764.36:98.365953:9.646405|false:764.36:79.957088:7.841112|false:764.36:72.706578:7.13008|false:764.36:77.644042:7.614279|true:770.25:57.12431:5.601981",
            string.Join('|', recorded.GetProperty("tests").EnumerateArray().Select(t =>
                $"{(t.GetProperty("nominalUsed").GetBoolean() ? "true" : "false")}:{t.GetProperty("grossAreaCm2").GetString()}:{t.GetProperty("strengthKgcm2").GetString()}:{t.GetProperty("strengthMpa").GetString()}")));
        // The preview is the same computation; its sixth line (no load) says why it cannot be recorded.
        Assert.Equal("98.365953,79.957088,72.706578,77.644042,57.124310,:LAB_TEST_INVALID", string.Join(',', preview.GetProperty("lines").EnumerateArray().Select(l =>
            l.GetProperty("error").ValueKind == JsonValueKind.Null ? l.GetProperty("strengthKgcm2").GetString() : ":" + l.GetProperty("error").GetString())));
        Assert.Equal((3, false), (preview.GetProperty("ageDays").GetInt32(), preview.GetProperty("ageZero").GetBoolean()));
        Assert.Equal(5, detail.GetProperty("lot").GetProperty("compressionTests").GetInt32());
        // Net-area strength = gross ÷ 0.55: 98.365953 ÷ 0.55 = 178.847187; 57.124310 ÷ 0.55 = 103.862382.
        Assert.Equal("3:19.400000x19.400000x39.400000:false:9.646405:178.847187:SECO_AL_AIRE:CONICA:Cónica", string.Join(':',
            tests[0].GetProperty("ageDays").GetInt32(), $"{tests[0].GetProperty("widthCm").GetString()}x{tests[0].GetProperty("heightCm").GetString()}x{tests[0].GetProperty("lengthCm").GetString()}",
            tests[0].GetProperty("nominalUsed").GetBoolean() ? "true" : "false", tests[0].GetProperty("strengthMpa").GetString(), tests[0].GetProperty("netStrengthKgcm2").GetString(),
            tests[0].GetProperty("blockCondition").GetString(), tests[0].GetProperty("failureType").GetString(), tests[0].GetProperty("failureTypeName").GetString()));
        Assert.Equal("19.500000x19.500000x39.500000:true:770.250000:57.124310:5.601981:103.862382", string.Join(':',
            $"{tests[4].GetProperty("widthCm").GetString()}x{tests[4].GetProperty("heightCm").GetString()}x{tests[4].GetProperty("lengthCm").GetString()}",
            tests[4].GetProperty("nominalUsed").GetBoolean() ? "true" : "false", tests[4].GetProperty("grossAreaCm2").GetString(), tests[4].GetProperty("strengthKgcm2").GetString(),
            tests[4].GetProperty("strengthMpa").GetString(), tests[4].GetProperty("netStrengthKgcm2").GetString()));
        Assert.Equal(("16.800000", "Cara irregular"), (tests[3].GetProperty("weightKg").GetString(), tests[3].GetProperty("notes").GetString()));
        Assert.Equal("1:1:true|1:5:true", await h.ScalarAsync<string>(
            "SELECT string_agg(DISTINCT spec_version::text || ':' || line_no::text || ':' || (tested_by = (SELECT user_id FROM iam.session WHERE session_id = @s))::text, '|') FROM qa.compression_test WHERE line_no IN (1, 5)",
            ("s", lab)));
    }

    [Fact]
    public async Task A_test_is_voided_with_a_reason_and_recorded_again_and_never_changed()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var s = await ProductionRunTests.SetupAsync(h);
        var posted = await PostAsync(h, s, s.Machine, s.Day, "run");
        var lot = posted.GetProperty("lotId").GetGuid();
        var lab = await h.SessionWithRolesAsync("LABORATORIO");

        // No requirements yet: a specimen with its measures is recorded, one without them is not (E-LAB1-01-8); age 0 is flagged (E-LAB1-01-6).
        var noSpec = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RecordCompressionTests(h.CompanyId, lab, "nospec", s.Plant, lot, s.Today, [new(44000m)]), new RecordCompressionTestsHandler()));
        var recorded = Json((await h.RunAsync(
            new RecordCompressionTests(h.CompanyId, lab, "rec", s.Plant, lot, s.Today, [new(44000m, 19.5m, 19.5m, 39.5m), new(45000m, 19.5m, 19.5m, 39.5m)]), new RecordCompressionTestsHandler())).ResultPayload);
        var first = recorded.GetProperty("tests")[0].GetProperty("testId").GetGuid();
        var noReason = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new VoidCompressionTest(h.CompanyId, lab, "v0", s.Plant, first, " "), new VoidCompressionTestHandler()));
        await h.RunAsync(new VoidCompressionTest(h.CompanyId, lab, "v1", s.Plant, first, "Carga mal leída"), new VoidCompressionTestHandler());
        var twice = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new VoidCompressionTest(h.CompanyId, lab, "v2", s.Plant, first, "Otra vez"), new VoidCompressionTestHandler()));
        var edit = await h.AppExecuteAsync($"UPDATE qa.compression_test SET void_reason = 'x', voided_by = tested_by, voided_at = now(), status = 'VOIDED', load_kg = 1 WHERE test_id <> '{first}'");
        var delete = await h.AppExecuteAsync("DELETE FROM qa.compression_test");
        var detail = await LotAsync(h, lab, lot);

        Assert.Equal((QualityErrors.SpecMissing, ManufacturingErrors.ReasonRequired, ManufacturingErrors.InvalidState), (noSpec.Code, noReason.Code, twice.Code));
        Assert.NotNull(edit);
        Assert.NotNull(delete);
        Assert.Equal("VOIDED:Carga mal leída:true|RECORDED::true", string.Join('|', detail.GetProperty("compression").EnumerateArray().Select(t =>
            $"{t.GetProperty("status").GetString()}:{t.GetProperty("voidReason").GetString()}:{(t.GetProperty("ageZero").GetBoolean() ? "true" : "false")}")));
        Assert.Equal(1, detail.GetProperty("lot").GetProperty("compressionTests").GetInt32()); // the voided one stays visible and does not count
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("spec").ValueKind);
        Assert.Equal("RECORDED→VOIDED", await h.ScalarAsync<string>(
            $"SELECT string_agg(coalesce(from_state, '') || '→' || to_state, ',') FROM core.state_history WHERE aggregate_id = '{first}' AND from_state IS NOT NULL"));
    }

    [Trait("AcceptanceLab1", "LAB-10")]
    [Fact]
    public async Task Three_absorption_blocks_give_absorption_density_class_and_limit()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var s = await ProductionRunTests.SetupAsync(h);
        var lot = (await PostAsync(h, s, s.Machine, s.Day, "run")).GetProperty("lotId").GetGuid();
        clock.Advance(TimeSpan.FromDays(2));
        var lab = await h.SessionWithRolesAsync("LABORATORIO");
        var date = s.Today.AddDays(2);

        var badWeights = new List<string>();
        foreach (var block in new AbsorptionBlock[] { new(10m, 10m, 9m), new(16m, 9m, 16.5m), new(16m, 9m, 0m) })
        {
            badWeights.Add((await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RecordAbsorptionTests(h.CompanyId, lab, $"bad-{badWeights.Count}", s.Plant, lot, date, [block]), new RecordAbsorptionTestsHandler()))).Code);
        }

        var recorded = Json((await h.RunAsync(
            new RecordAbsorptionTests(h.CompanyId, lab, "abs", s.Plant, lot, date, [new(17.80m, 9.90m, 16.50m), new(17.00m, 8.60m, 15.20m), new(18.00m, 10.05m, 16.20m), new(18.00m, 10.05m, 12.00m, "Mal secado")]),
            new RecordAbsorptionTestsHandler())).ResultPayload);
        await h.RunAsync(new VoidAbsorptionTest(h.CompanyId, lab, "void", s.Plant, recorded.GetProperty("tests")[3].GetGuid(), "Bloque mal secado"), new VoidAbsorptionTestHandler());
        var detail = await LotAsync(h, lab, lot);
        var summary = detail.GetProperty("absorptionSummary");

        Assert.Equal([QualityErrors.TestInvalid, QualityErrors.TestInvalid, QualityErrors.TestInvalid], badWeights);
        // Absorption = (Ws − Wd) ÷ (Ws − Wi) × 1000; fraction = (Ws − Wd) ÷ Wd; density = Wd ÷ (Ws − Wi) × 1000.
        // 1: 1.30 ÷ 7.90 → 164.556962; 1.30 ÷ 16.50 → 0.078788; 16.50 ÷ 7.90 → 2,088.607595 (normal, limit 208, OK).
        // 2: 1.80 ÷ 8.40 → 214.285714; 1.80 ÷ 15.20 → 0.118421; 15.20 ÷ 8.40 → 1,809.523810 (medium, limit 240, OK).
        // 3: 1.80 ÷ 7.95 → 226.415094; 1.80 ÷ 16.20 → 0.111111; 16.20 ÷ 7.95 → 2,037.735849 (normal, limit 208, high).
        // 4 (voided): 6.00 ÷ 7.95 → 754.716981; 6.00 ÷ 12.00 → 0.500000; 12.00 ÷ 7.95 → 1,509.433962 (light, limit 288, high).
        Assert.Equal(
            "164.556962:0.078788:2088.607595:NORMAL:208:OK:RECORDED|214.285714:0.118421:1809.523810:MEDIANO:240:OK:RECORDED|226.415094:0.111111:2037.735849:NORMAL:208:ALTA:RECORDED"
            + "|754.716981:0.500000:1509.433962:LIVIANO:288:ALTA:VOIDED",
            string.Join('|', detail.GetProperty("absorption").EnumerateArray().Select(t => string.Join(':',
                t.GetProperty("absorptionKgm3").GetString(), t.GetProperty("absorptionFraction").GetString(), t.GetProperty("densityKgm3").GetString(), t.GetProperty("densityClass").GetString(),
                decimal.Parse(t.GetProperty("absorptionLimitKgm3").GetString()!, System.Globalization.CultureInfo.InvariantCulture).ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                t.GetProperty("guide").GetString(), t.GetProperty("status").GetString()))));
        // The lot's averages over its three valid blocks: 605.257770 ÷ 3 = 201.752590; 0.308320 ÷ 3 = 0.102773; 5,935.867254 ÷ 3 = 1,978.622418 → medium, limit 240.
        Assert.Equal("3:201.752590:0.102773:1978.622418:MEDIANO:false", string.Join(':',
            summary.GetProperty("blocks").GetInt32(), summary.GetProperty("absorptionKgm3").GetString(), summary.GetProperty("absorptionFraction").GetString(), summary.GetProperty("densityKgm3").GetString(),
            summary.GetProperty("densityClass").GetString(), summary.GetProperty("aboveLimit").GetBoolean() ? "true" : "false"));
        Assert.Equal(3, detail.GetProperty("lot").GetProperty("absorptionTests").GetInt32());
    }

    [Trait("AcceptanceLab1", "LAB-12")]
    [Fact]
    public async Task Runs_of_two_machines_get_different_field_codes_a_second_shift_carries_its_suffix_and_a_lot_waits_for_what_is_missing()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ProductionRunTests.SetupAsync(h);
        var quality = await h.SessionWithRolesAsync("CALIDAD");
        var second = (await h.RunAsync(new CreateMachine(h.CompanyId, s.Manager, "m2", s.Plant, "BESSER-2", "Besser Vibrapac"), new CreateMachineHandler())).ResultRef;
        var recipe = (await h.RunAsync(
            new PrepareRecipe(h.CompanyId, s.Supervisor, "r2", s.Plant, s.Block, second, 150m, 6m, 600m, 24, 168,
                [new RecipeLineInput(s.Cement, 180m), new RecipeLineInput(s.Sand, 1.8m), new RecipeLineInput(s.Additive, 1.5m)]),
            new PrepareRecipeHandler())).ResultRef;
        await h.RunAsync(new ApproveRecipe(h.CompanyId, s.Manager, "r2a", s.Plant, recipe), new ApproveRecipeHandler());
        var t1 = (await h.RunAsync(new DefineShift(h.CompanyId, s.Manager, "t1", s.Plant, "T1", new TimeOnly(6, 0), new TimeOnly(14, 0)), new DefineShiftHandler())).ResultRef;
        var t2 = (await h.RunAsync(new DefineShift(h.CompanyId, s.Manager, "t2", s.Plant, "T2", new TimeOnly(14, 0), new TimeOnly(22, 0)), new DefineShiftHandler())).ResultRef;

        // E-LAB1-01-4: posting never fails for a missing prefix or short code — the lot waits, and the lab's list says for what.
        var waiting = await PostAsync(h, s, s.Machine, t1, "a");
        async Task<string> WaitsForAsync()
        {
            var list = Json(await h.QueryAsync(new ListLabLots(h.CompanyId, quality), new ListLabLotsHandler()));
            return $"{list.GetProperty("withoutFieldCode").GetInt32()}:{string.Join(',', list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("fieldCodeWaitsFor").GetString() ?? i.GetProperty("fieldCode").GetString()))}";
        }

        var noPrefix = await WaitsForAsync();
        var spec = Json((await SpecAsync(h, quality, s, "spec")).ResultPayload);
        var noShortCode = await WaitsForAsync();
        var badShortCode = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SetMachineShortCode(h.CompanyId, quality, "bad", s.Plant, s.Machine, 1, "P-1"), new SetMachineShortCodeHandler()));
        var p1 = Json((await h.RunAsync(new SetMachineShortCode(h.CompanyId, quality, "p1", s.Plant, s.Machine, 1, "P1"), new SetMachineShortCodeHandler())).ResultPayload);
        var duplicate = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SetMachineShortCode(h.CompanyId, quality, "dup", s.Plant, second, 1, "P1"), new SetMachineShortCodeHandler()));
        await h.RunAsync(new SetMachineShortCode(h.CompanyId, quality, "p2", s.Plant, second, 1, "P2"), new SetMachineShortCodeHandler());
        var notCalidad = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SetMachineShortCode(h.CompanyId, s.Manager, "mgr", s.Plant, second, 2, "P3"), new SetMachineShortCodeHandler()));

        var sameShiftOtherMachine = await PostAsync(h, s, second, t1, "b");
        var secondShift = await PostAsync(h, s, s.Machine, t2, "c");

        Assert.Equal((QualityErrors.SpecInvalid, ManufacturingErrors.CodeDuplicate, AuthorizationErrors.NotAuthorized), (badShortCode.Code, duplicate.Code, notCalidad.Code));
        Assert.Equal(JsonValueKind.Null, waiting.GetProperty("fieldCode").ValueKind);
        Assert.Equal(("1:ITEM_PREFIX", "1:MACHINE_SHORT_CODE"), (noPrefix, noShortCode));
        Assert.Equal((0, 1), (spec.GetProperty("lotsCoded").GetInt32(), p1.GetProperty("lotsCoded").GetInt32()));
        Assert.Equal((Code(s.Today, "P2"), Code(s.Today, "P1", "T2")), (sameShiftOtherMachine.GetProperty("fieldCode").GetString(), secondShift.GetProperty("fieldCode").GetString()));
        Assert.Equal($"{Code(s.Today, "P1")},{Code(s.Today, "P1", "T2")},{Code(s.Today, "P2")}", await h.ScalarAsync<string>("SELECT string_agg(field_code, ',' ORDER BY field_code) FROM mfg.fg_lot"));
        Assert.Equal("P1,P2", string.Join(',', Json(await h.QueryAsync(new ListMachines(h.CompanyId, quality), new ListMachinesHandler())).GetProperty("items").EnumerateArray()
            .Select(m => m.GetProperty("shortCode").GetString())));
        // Set once: the field code of a lot never changes.
        Assert.NotNull(await h.AppExecuteAsync("UPDATE mfg.fg_lot SET field_code = '8010101P9', version = version + 1"));
    }

    [Fact]
    public async Task The_lot_of_a_run_redone_after_a_reversal_reuses_the_voided_lots_field_code()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ProductionRunTests.SetupAsync(h);
        var quality = await h.SessionWithRolesAsync("CALIDAD");
        await SpecAsync(h, quality, s, "spec");
        await h.RunAsync(new SetMachineShortCode(h.CompanyId, quality, "p1", s.Plant, s.Machine, 1, "P1"), new SetMachineShortCodeHandler());
        var run = (await ProductionRunTests.Start(h, s, "start")).ResultRef;
        var summary = Json((await ProductionRunTests.Record(h, s, run, "rec")).ResultPayload);
        var first = Json((await h.RunAsync(new PostShiftSummary(h.CompanyId, s.Manager, "post", s.Plant, run, summary.GetProperty("version").GetInt64()), new PostShiftSummaryHandler())).ResultPayload);
        await h.RunAsync(new ReverseShiftSummary(h.CompanyId, s.Manager, "rev", s.Plant, run, first.GetProperty("version").GetInt64(), "Unidades mal contadas"), new ReverseShiftSummaryHandler());
        var lab = await h.SessionWithRolesAsync("LABORATORIO");
        var voidedLot = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new RecordCompressionTests(h.CompanyId, lab, "voided", s.Plant, first.GetProperty("lotId").GetGuid(), s.Today, [new(44000m)]), new RecordCompressionTestsHandler()));
        var draft = Json(await h.QueryAsync(new GetProductionRun(h.CompanyId, s.Manager, run), new GetProductionRunHandler())).GetProperty("summary");
        var again = Json((await h.RunAsync(new PostShiftSummary(h.CompanyId, s.Manager, "repost", s.Plant, run, draft.GetProperty("version").GetInt64()), new PostShiftSummaryHandler())).ResultPayload);

        Assert.Equal(ManufacturingErrors.InvalidState, voidedLot.Code); // E-LAB1-01-7
        Assert.Equal((Code(s.Today, "P1", "DIA"), Code(s.Today, "P1", "DIA")), (first.GetProperty("fieldCode").GetString(), again.GetProperty("fieldCode").GetString()));
        Assert.Equal("CURING,VOIDED", await h.ScalarAsync<string>("SELECT string_agg(status, ',' ORDER BY status) FROM mfg.fg_lot WHERE field_code = @f", ("f", Code(s.Today, "P1", "DIA"))));
    }

    [Trait("AcceptanceLab1", "LAB-17")]
    [Fact]
    public async Task The_lab_technician_records_tests_and_reads_but_holds_neither_the_final_release_nor_the_requirements()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ProductionRunTests.SetupAsync(h);
        var lab = await h.SessionWithRolesAsync("LABORATORIO");
        var quality = await h.SessionWithRolesAsync("CALIDAD");

        var spec = await Assert.ThrowsAsync<DomainException>(() => SpecAsync(h, lab, s, "spec"));
        var parameter = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SetLabParameter(h.CompanyId, lab, "p", "MAX_CV", 0.2m), new SetLabParameterHandler()));
        var failure = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new DefineFailureType(h.CompanyId, lab, "f", "MIXTA", "Mixta"), new DefineFailureTypeHandler()));
        var noRawMaterial = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SetItemSpec(h.CompanyId, quality, "raw", s.Cement, "8", 19.5m, 19.5m, 39.5m), new SetItemSpecHandler()));
        var individualAlone = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new SetItemSpec(h.CompanyId, quality, "one", s.Block, "8", 19.5m, 19.5m, 39.5m, MinIndividual28d: 60m), new SetItemSpecHandler()));
        await SpecAsync(h, quality, s, "v1");
        await SpecAsync(h, quality, s, "v2", minAvg: 70m);
        var specs = Json(await h.QueryAsync(new ListItemSpecs(h.CompanyId, lab), new ListItemSpecsHandler())).GetProperty("items").EnumerateArray().Single();

        Assert.Equal(
            (AuthorizationErrors.NotAuthorized, AuthorizationErrors.NotAuthorized, AuthorizationErrors.NotAuthorized, ManufacturingErrors.NotFinishedGood, QualityErrors.SpecInvalid),
            (spec.Code, parameter.Code, failure.Code, noRawMaterial.Code, individualAlone.Code));
        Assert.Equal("lab:read,lab_test:record", await h.ScalarAsync<string>(
            "SELECT string_agg(rp.permission_code, ',' ORDER BY rp.permission_code) FROM iam.role r JOIN iam.role_permission rp USING (role_id) WHERE r.code = 'LABORATORIO'"));
        Assert.Equal("CALIDAD", await h.ScalarAsync<string>(
            "SELECT string_agg(r.code, ',' ORDER BY r.code) FROM iam.role r JOIN iam.role_permission rp USING (role_id) WHERE rp.permission_code = 'fg_lot:final_release' AND r.code <> 'SUPERADMIN'"));
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM iam.sod_rule WHERE permission_a = 'fg_lot:final_release' AND permission_b = 'shift_summary:record'"));
        Assert.Equal("BLOQUE-6:2:8:70.000000", $"{specs.GetProperty("itemCode").GetString()}:{specs.GetProperty("version").GetInt32()}:{specs.GetProperty("lotPrefix").GetString()}:{specs.GetProperty("minAvg28d").GetString()}");
    }

    [Fact]
    public async Task Calidad_changes_parameters_and_failure_types_and_the_history_says_who_changed_what()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var quality = await h.SessionWithRolesAsync("CALIDAD");
        async Task<JsonElement> SettingsAsync() => Json(await h.QueryAsync(new GetLabSettings(h.CompanyId, quality), new GetLabSettingsHandler()));

        var before = await SettingsAsync();
        var outOfRange = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SetLabParameter(h.CompanyId, quality, "a", "MAX_CV", 1.5m), new SetLabParameterHandler()));
        var notWhole = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SetLabParameter(h.CompanyId, quality, "b", "MIN_SPECIMENS", 2.5m), new SetLabParameterHandler()));
        var unknown = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SetLabParameter(h.CompanyId, quality, "c", "NADA", 1m), new SetLabParameterHandler()));
        var textForNumber = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SetLabParameter(h.CompanyId, quality, "d", "MAX_CV", Text: "quince"), new SetLabParameterHandler()));
        await h.RunAsync(new SetLabParameter(h.CompanyId, quality, "e", "max_cv", 0.2m), new SetLabParameterHandler());
        await h.RunAsync(new SetLabParameter(h.CompanyId, quality, "f", "MAX_CV", 0.18m), new SetLabParameterHandler());
        await h.RunAsync(new SetLabParameter(h.CompanyId, quality, "g", "EQUIPMENT_SERIAL", Text: " 230101 "), new SetLabParameterHandler());
        await h.RunAsync(new DefineFailureType(h.CompanyId, quality, "h", "mixta", "Mixta"), new DefineFailureTypeHandler());
        await h.RunAsync(new DefineFailureType(h.CompanyId, quality, "i", "OTRA", "Otra (describir)"), new DefineFailureTypeHandler());
        await h.RunAsync(new SetFailureTypeStatus(h.CompanyId, quality, "j", "CONICA", "INACTIVE"), new SetFailureTypeStatusHandler());
        var noSuchType = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SetFailureTypeStatus(h.CompanyId, quality, "k", "NADA", "INACTIVE"), new SetFailureTypeStatusHandler()));
        var after = await SettingsAsync();
        string Parameter(JsonElement settings, string code)
        {
            var p = settings.GetProperty("parameters").EnumerateArray().Single(x => x.GetProperty("code").GetString() == code);
            return $"{p.GetProperty("number").GetString() ?? p.GetProperty("text").GetString()}:{(p.GetProperty("own").GetBoolean() ? "own" : "shared")}";
        }

        Assert.Equal(
            (QualityErrors.ParameterInvalid, QualityErrors.ParameterInvalid, QualityErrors.ParameterInvalid, QualityErrors.ParameterInvalid, ManufacturingErrors.NotFound),
            (outOfRange.Code, notWhole.Code, unknown.Code, textForNumber.Code, noSuchType.Code));
        // The starting values are the validated Excel's: 13 parameters and the 28 initial age factors; LAB1-03 adds the certificate's signer and title.
        Assert.Equal(43, before.GetProperty("parameters").GetArrayLength());
        Assert.Equal(
            "0.15000000:shared|3.00000000:shared|26.00000000:shared|2.00000000:shared|0.09806650:shared|0.84000000:shared|0.98300000:shared|1.00000000:shared|220808:shared",
            string.Join('|', new[] { "MAX_CV", "MIN_SPECIMENS", "AGE_28D_MIN_DAYS", "OWN_FACTOR_MIN_LOTS", "KGCM2_TO_MPA", "AGE_FACTOR_03", "AGE_FACTOR_22", "AGE_FACTOR_28", "EQUIPMENT_SERIAL" }
                .Select(c => Parameter(before, c))));
        Assert.Equal("0.18000000:own|230101:own|3.00000000:shared", $"{Parameter(after, "MAX_CV")}|{Parameter(after, "EQUIPMENT_SERIAL")}|{Parameter(after, "MIN_SPECIMENS")}");
        Assert.Equal("EQUIPMENT_SERIAL=230101,MAX_CV=0.18,MAX_CV=0.2", string.Join(',', after.GetProperty("history").EnumerateArray().Select(c => $"{c.GetProperty("code").GetString()}={c.GetProperty("value").GetString()}")));
        Assert.Equal("CONICA,CONO_CORTE,CORTE_DIAGONAL,COLUMNAR,DESPRENDIMIENTO_CARA,APLASTAMIENTO_LOCAL,OTRA", string.Join(',', before.GetProperty("failureTypes").EnumerateArray().Select(t => t.GetProperty("code").GetString())));
        Assert.Equal("CONICA:Cónica:INACTIVE:own|OTRA:Otra (describir):ACTIVE:own|MIXTA:Mixta:ACTIVE:own", string.Join('|', after.GetProperty("failureTypes").EnumerateArray()
            .Where(t => !t.GetProperty("shared").GetBoolean())
            .Select(t => $"{t.GetProperty("code").GetString()}:{t.GetProperty("name").GetString()}:{t.GetProperty("status").GetString()}:own")));
        Assert.Equal(8, after.GetProperty("failureTypes").GetArrayLength());
    }
}
