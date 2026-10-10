using System.Text.Json;
using Rochell.Identity.Authorization;
using Rochell.Manufacturing.Lots;
using Rochell.Manufacturing.Machines;
using Rochell.Manufacturing.Quality;
using Rochell.Manufacturing.Runs;
using Rochell.Manufacturing.Shifts;
using Rochell.Platform.Commands;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Manufacturing.Tests;

/// <summary>
/// LAB1-02 (E-LAB1-4, 5, 10; E-LAB1-02-1…10, 13): the lot's evaluation — 28-day strength real or estimated, verdict and alerts —, the
/// automatic block on NO CUMPLE, the final release on a real CUMPLE and the age curve. Expected values come from the validated Excel
/// (lots 8160924P1 and 8121124P1 of docs/architecture/lab1/reference/historico_ensayos.csv).
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class QualityEvaluationTests(PostgresFixture postgres)
{
    private sealed record Stage(TestHarness H, FakeClock Clock, ProductionRunTests.Setup S)
    {
        public Guid Quality { get; set; }

        public Guid Lab { get; set; }
    }

    private static JsonElement Json(string payload) => JsonDocument.Parse(payload).RootElement;

    /// <summary>The production setup with Block 8" requirements (19.5 × 19.5 × 39.5 cm) and the machine as P1.</summary>
    private async Task<Stage> StageAsync(decimal? minAvg, decimal? minIndividual = null)
    {
        var clock = new FakeClock();
        var h = await TestHarness.CreateAsync(postgres, clock);
        var s = await ProductionRunTests.SetupAsync(h);
        var stage = new Stage(h, clock, s) { Quality = await h.SessionWithRolesAsync("CALIDAD"), Lab = await h.SessionWithRolesAsync("LABORATORIO") };
        await h.RunAsync(new SetItemSpec(h.CompanyId, stage.Quality, "spec", s.Block, "8", 19.5m, 19.5m, 39.5m, 0.55m, minAvg, minIndividual), new SetItemSpecHandler());
        await h.RunAsync(new SetMachineShortCode(h.CompanyId, stage.Quality, "p1", s.Plant, s.Machine, 1, "P1"), new SetMachineShortCodeHandler());
        return stage;
    }

    private static async Task<Guid> PostAsync(Stage x, Guid shift, string key)
    {
        var (h, s) = (x.H, x.S);
        var run = (await h.RunAsync(new StartProductionRun(h.CompanyId, s.Supervisor, key, s.Plant, s.Machine, shift, s.Today, s.Block), new StartProductionRunHandler())).ResultRef;
        var summary = Json((await ProductionRunTests.Record(h, s, run, key + "-rec")).ResultPayload);
        return Json((await h.RunAsync(new PostShiftSummary(h.CompanyId, s.Manager, key + "-post", s.Plant, run, summary.GetProperty("version").GetInt64()), new PostShiftSummaryHandler())).ResultPayload)
            .GetProperty("lotId").GetGuid();
    }

    /// <summary>Moves the clock to <paramref name="day"/> days after production; yesterday's sessions have expired.</summary>
    private static void Day(Stage x, ref int today, int day)
    {
        x.Clock.Advance(TimeSpan.FromDays(day - today));
        today = day;
    }

    private static async Task SignInAsync(Stage x)
    {
        x.Quality = await x.H.SessionWithRolesAsync("CALIDAD");
        x.Lab = await x.H.SessionWithRolesAsync("LABORATORIO");
    }

    private static async Task<JsonElement> BreakAsync(Stage x, Guid lot, int day, string key, params CompressionSpecimen[] specimens)
        => Json((await x.H.RunAsync(new RecordCompressionTests(x.H.CompanyId, x.Lab, key, x.S.Plant, lot, x.S.Today.AddDays(day), specimens), new RecordCompressionTestsHandler())).ResultPayload)
            .GetProperty("evaluation");

    private static async Task<JsonElement> DetailAsync(Stage x, Guid lot) => Json(await x.H.QueryAsync(new GetLabLot(x.H.CompanyId, x.Lab, lot), new GetLabLotHandler()));

    private static async Task<long> ReleaseAsync(Stage x, Guid lot)
    {
        var version = (await DetailAsync(x, lot)).GetProperty("lot").GetProperty("version").GetInt64();
        return Json((await x.H.RunAsync(new ReleaseLot(x.H.CompanyId, x.Quality, $"rel-{lot}", x.S.Plant, lot, version, x.S.Patio), new ReleaseLotHandler())).ResultPayload).GetProperty("version").GetInt64();
    }

    private static Task<CommandResult> FinalAsync(Stage x, Guid session, Guid lot, long version, string key)
        => x.H.RunAsync(new FinalReleaseLot(x.H.CompanyId, session, key, x.S.Plant, lot, version), new FinalReleaseLotHandler());

    private static string Verdict(JsonElement evaluation)
        => $"{evaluation.GetProperty("verdict").GetString()}:{evaluation.GetProperty("basis").GetString()}:{evaluation.GetProperty("strength28d").GetString()}:{evaluation.GetProperty("min28d").GetString()}"
            + $":{string.Join('+', evaluation.GetProperty("alerts").EnumerateArray().Select(a => a.GetString()))}:{(evaluation.GetProperty("blocked").GetBoolean() ? "blocked" : "-")}";

    private static CompressionSpecimen Nominal(decimal load) => new(load, 19.5m, 19.5m, 39.5m);

    private static CompressionSpecimen Small(decimal load) => new(load, 19.4m, 19.4m, 39.4m);

    [Trait("AcceptanceLab1", "LAB-01")]
    [Trait("AcceptanceLab1", "LAB-03")]
    [Trait("AcceptanceLab1", "LAB-07")]
    [Trait("AcceptanceLab1", "LAB-17")]
    [Fact]
    public async Task An_early_break_estimates_the_28_day_strength_and_only_a_real_one_allows_the_final_release()
    {
        var x = await StageAsync(75m, 65m);
        await using var h = x.H;
        var lot = await PostAsync(x, x.S.Day, "run");
        var day = 0;
        Day(x, ref day, 3);
        await SignInAsync(x);
        var released = await ReleaseAsync(x, lot);

        // Lot 8160924P1 of the Excel: five specimens at 3 days. Average 77.159594, minimum 57.124310, maximum 98.365953, sample deviation
        // 14.824071, CV 14.824071 ÷ 77.159594 = 0.192122 (above 0.15: «CV alto»). Factor of 3 days 0.84: 77.159594 ÷ 0.84 = 91.856660 and
        // 57.124310 ÷ 0.84 = 68.005131 — above 75 and 65: CUMPLE, estimated.
        var early = await BreakAsync(x, lot, 3, "d3", Small(75187m), Small(61116m), Small(55574m), Small(59348m), new CompressionSpecimen(44000m));
        var afterEarly = await DetailAsync(x, lot);
        var estimated = afterEarly.GetProperty("evaluation");
        var onEstimate = await Assert.ThrowsAsync<DomainException>(() => FinalAsync(x, x.Quality, lot, released, "final-0"));
        var technician = await Assert.ThrowsAsync<DomainException>(() => FinalAsync(x, x.Lab, lot, released, "final-lab"));

        // Lot 8121124P1: six specimens at 28 days. Real average 80.397490, minimum 71.744239, deviation 7.826993, CV 0.097354.
        Day(x, ref day, 28);
        await SignInAsync(x);
        var real = await BreakAsync(x, lot, 28, "d28", Nominal(62863m), Nominal(55261m), Nominal(55927m), Nominal(64990m), Nominal(61085m), Nominal(71431m));
        var afterReal = await DetailAsync(x, lot);
        var final = Json((await FinalAsync(x, x.Quality, lot, afterReal.GetProperty("lot").GetProperty("version").GetInt64(), "final")).ResultPayload);
        var twice = await Assert.ThrowsAsync<DomainException>(() => FinalAsync(x, x.Quality, lot, final.GetProperty("version").GetInt64(), "final-2"));

        Assert.Equal("COMPLIES:ESTIMATED:91.85666:68.005131:HIGH_CV:-", Verdict(early));
        Assert.Equal(
            "5:3:3:77.159594:57.124310:98.365953:14.824071:0.192122:3:77.159594:0.840000:INITIAL:91.856660:68.005131:75.000000:65.000000",
            string.Join(':', new[] { "specimens", "ageMin", "ageMax" }.Select(p => estimated.GetProperty(p).GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Concat(new[] { "avgStrength", "minStrength", "maxStrength", "stdDev", "cv" }.Select(p => estimated.GetProperty(p).GetString()))
                .Append(estimated.GetProperty("earlyAge").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Concat(new[] { "earlyAvg", "factorUsed", "factorSource", "strength28d", "min28d", "minAvgRequired", "minIndividualRequired" }.Select(p => estimated.GetProperty(p).GetString()))));
        Assert.Equal(("RELEASED", false), (afterEarly.GetProperty("lot").GetProperty("status").GetString(), afterEarly.GetProperty("lot").GetProperty("readyForFinalRelease").GetBoolean()));
        Assert.Equal((QualityErrors.FinalReleaseRefused, AuthorizationErrors.NotAuthorized, ManufacturingErrors.InvalidState), (onEstimate.Code, technician.Code, twice.Code));
        // With breaks at 28 days the strength is the real one — no estimate, no factor; the indicators cover the eleven specimens.
        Assert.Equal("COMPLIES:REAL:80.39749:71.744239::-", Verdict(real));
        Assert.Equal(("11:3:28:78.925719:11.017777:0.139597", "80.397490", JsonValueKind.Null, true), (
            string.Join(':', afterReal.GetProperty("evaluation").GetProperty("specimens").GetInt32(), afterReal.GetProperty("evaluation").GetProperty("ageMin").GetInt32(),
                afterReal.GetProperty("evaluation").GetProperty("ageMax").GetInt32(), afterReal.GetProperty("evaluation").GetProperty("avgStrength").GetString(),
                afterReal.GetProperty("evaluation").GetProperty("stdDev").GetString(), afterReal.GetProperty("evaluation").GetProperty("cv").GetString()),
            afterReal.GetProperty("evaluation").GetProperty("realAvg28d").GetString(), afterReal.GetProperty("evaluation").GetProperty("factorUsed").ValueKind,
            afterReal.GetProperty("lot").GetProperty("readyForFinalRelease").GetBoolean()));
        Assert.Equal("FINAL_RELEASED", final.GetProperty("status").GetString());
        Assert.Equal("FINAL_RELEASED:true:RELEASED", await h.ScalarAsync<string>(
            "SELECT status || ':' || (final_released_by IS NOT NULL)::text || ':' || (SELECT string_agg(DISTINCT status, ',') FROM mfg.rack) FROM mfg.fg_lot"));
    }

    [Trait("AcceptanceLab1", "LAB-04")]
    [Trait("AcceptanceLab1", "LAB-10")]
    [Fact]
    public async Task Without_a_requirement_the_verdict_is_sin_requisito_and_without_a_28_day_strength_sin_dato()
    {
        var x = await StageAsync(null);
        await using var h = x.H;
        var lot = await PostAsync(x, x.S.Day, "run");

        // Three blocks of 18.00 / 10.05 / 16.20 kg: absorption 226.415094 kg/m³, density 2,037.735849 (normal weight, limit 208) — above.
        var absorption = Json((await h.RunAsync(
            new RecordAbsorptionTests(h.CompanyId, x.Lab, "abs", x.S.Plant, lot, x.S.Today, [new(18.00m, 10.05m, 16.20m), new(18.00m, 10.05m, 16.20m), new(18.00m, 10.05m, 16.20m)]),
            new RecordAbsorptionTestsHandler())).ResultPayload).GetProperty("evaluation");
        // Broken the day it was produced (age 0): it counts as a specimen and never enters an estimate (E-LAB1-02-4).
        var sameDay = await BreakAsync(x, lot, 0, "d0", Nominal(30000m));
        var notCalidad = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ReevaluateLot(h.CompanyId, x.Lab, "re-lab", x.S.Plant, lot), new ReevaluateLotHandler()));
        // E-LAB1-02-2: the requirement Calidad loads now does not judge the lot until it is evaluated again.
        await h.RunAsync(new SetItemSpec(h.CompanyId, x.Quality, "spec-2", x.S.Block, "8", 19.5m, 19.5m, 39.5m, 0.55m, 70m), new SetItemSpecHandler());
        var unchanged = (await DetailAsync(x, lot)).GetProperty("evaluation").GetProperty("verdict").GetString();
        var again = Json((await h.RunAsync(new ReevaluateLot(h.CompanyId, x.Quality, "re", x.S.Plant, lot), new ReevaluateLotHandler())).ResultPayload).GetProperty("evaluation");
        var detail = await DetailAsync(x, lot);

        Assert.Equal(AuthorizationErrors.NotAuthorized, notCalidad.Code);
        Assert.Equal("NO_SPEC::::NO_TESTS+HIGH_ABSORPTION:-", Verdict(absorption));
        Assert.Equal("NO_SPEC::::FEW_SPECIMENS+HIGH_ABSORPTION:-", Verdict(sameDay));
        Assert.Equal(("NO_SPEC", "NO_DATA::::FEW_SPECIMENS+HIGH_ABSORPTION:-"), (unchanged, Verdict(again)));
        Assert.Equal("CURING:2:1:38.948393", string.Join(':',
            detail.GetProperty("lot").GetProperty("status").GetString(), detail.GetProperty("evaluation").GetProperty("specVersion").GetInt32(),
            detail.GetProperty("evaluation").GetProperty("specimens").GetInt32(), detail.GetProperty("evaluation").GetProperty("avgStrength").GetString()));
        Assert.Equal("TESTS,TESTS,REEVALUATION", await h.ScalarAsync<string>("SELECT string_agg(cause, ',' ORDER BY seq) FROM qa.lot_evaluation"));
        Assert.NotNull(await h.AppExecuteAsync("UPDATE qa.lot_evaluation SET verdict = 'COMPLIES'"));
    }

    [Trait("AcceptanceLab1", "LAB-06")]
    [Trait("AcceptanceLab1", "LAB-08")]
    [Trait("AcceptanceLab1", "LAB-09")]
    [Fact]
    public async Task An_estimated_no_cumple_blocks_a_released_lot_and_calidad_unblocks_it_after_a_real_cumple()
    {
        var x = await StageAsync(100m);
        await using var h = x.H;
        var lot = await PostAsync(x, x.S.Day, "run");
        var day = 0;
        Day(x, ref day, 3);
        await SignInAsync(x);
        var released = await ReleaseAsync(x, lot);

        // Two specimens at 3 days: 98.365953 and 57.564498 — average 77.965226, deviation 28.850986, CV 0.370049. Estimated at 28 days
        // 77.965226 ÷ 0.84 = 92.815745, below the 100 required: NO CUMPLE, estimated — the lot is blocked there and then.
        var early = await BreakAsync(x, lot, 3, "d3", Small(75187m), Small(44000m));
        var blocked = await DetailAsync(x, lot);
        var finalWhileBlocked = await Assert.ThrowsAsync<DomainException>(() => FinalAsync(x, x.Quality, lot, released + 1, "final-0"));
        var blockRow = await h.ScalarAsync<string>("SELECT status || ':' || block_cause || ':' || blocked_from || ':' || (block_evaluation_id IS NOT NULL)::text || ':' || block_reason FROM mfg.fg_lot");
        var racks = await h.ScalarAsync<string>("SELECT string_agg(DISTINCT status, ',') FROM mfg.rack");
        // E-LAB1-02-8: voiding one of the tests that caused the block — 98.365953 ÷ 0.84 = 117.102325 is left, CUMPLE by estimate — does not unblock.
        var voided = Json((await h.RunAsync(
            new VoidCompressionTest(h.CompanyId, x.Lab, "void", x.S.Plant, await h.ScalarAsync<Guid>("SELECT test_id FROM qa.compression_test WHERE line_no = 2"), "Probeta mal refrentada"),
            new VoidCompressionTestHandler())).ResultPayload).GetProperty("evaluation");
        var afterVoid = await h.ScalarAsync<string>("SELECT status FROM mfg.fg_lot");

        // Real breaks at 28 days: three of 80,000 kg ÷ 770.25 cm² = 103.862382. CUMPLE, real — and the lot stays blocked (E-LAB1-02-8).
        Day(x, ref day, 28);
        await SignInAsync(x);
        var real = await BreakAsync(x, lot, 28, "d28", Nominal(80000m), Nominal(80000m), Nominal(80000m));
        var stillBlocked = await DetailAsync(x, lot);
        var noReason = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new UnblockLot(h.CompanyId, x.Quality, "un-0", x.S.Plant, lot, stillBlocked.GetProperty("lot").GetProperty("version").GetInt64(), " "), new UnblockLotHandler()));
        var unblocked = Json((await h.RunAsync(
            new UnblockLot(h.CompanyId, x.Quality, "un", x.S.Plant, lot, stillBlocked.GetProperty("lot").GetProperty("version").GetInt64(), "Rotura a 28 días conforme"), new UnblockLotHandler())).ResultPayload);
        var final = Json((await FinalAsync(x, x.Quality, lot, unblocked.GetProperty("version").GetInt64(), "final")).ResultPayload);

        // A later real break that brings the average down blocks the lot even after its final release (E-LAB1-4):
        // (3 × 103.862382 + 3 × 38.948393) ÷ 6 = 71.405388 (30,000 ÷ 770.25 = 38.948393).
        var late = await BreakAsync(x, lot, 28, "d28-b", Nominal(30000m), Nominal(30000m), Nominal(30000m));

        Assert.Equal("FAILS:ESTIMATED:92.815745:68.529164:FEW_SPECIMENS+HIGH_CV:blocked", Verdict(early));
        Assert.Equal("BLOCKED:LAB:RELEASED:true:Laboratorio: NO CUMPLE (estimado); resistencia a 28 d 92.82 kg/cm², mínimo 68.53; requisito 100.00.", blockRow);
        Assert.Equal("BLOCKED", racks);
        Assert.Equal(("COMPLIES:ESTIMATED:117.102325:117.102325:FEW_SPECIMENS:-", "BLOCKED"), (Verdict(voided), afterVoid));
        Assert.Equal(("BLOCKED", "LAB", "FAILS"), (
            blocked.GetProperty("lot").GetProperty("status").GetString(), blocked.GetProperty("lot").GetProperty("blockCause").GetString(), blocked.GetProperty("lot").GetProperty("verdict").GetString()));
        Assert.Equal((ManufacturingErrors.InvalidState, ManufacturingErrors.ReasonRequired), (finalWhileBlocked.Code, noReason.Code));
        Assert.Equal("COMPLIES:REAL:103.862382:103.862382::-", Verdict(real));
        Assert.Equal(("BLOCKED", false), (stillBlocked.GetProperty("lot").GetProperty("status").GetString(), stillBlocked.GetProperty("lot").GetProperty("readyForFinalRelease").GetBoolean()));
        Assert.Equal(("RELEASED", "FINAL_RELEASED"), (unblocked.GetProperty("status").GetString(), final.GetProperty("status").GetString()));
        Assert.Equal("FAILS:REAL:71.405388:38.948393:HIGH_CV:blocked", Verdict(late));
        Assert.Equal("BLOCKED:LAB:FINAL_RELEASED", await h.ScalarAsync<string>("SELECT status || ':' || block_cause || ':' || blocked_from FROM mfg.fg_lot"));
    }

    [Fact]
    public async Task A_lot_calidad_unblocked_is_blocked_again_only_when_the_estimate_becomes_a_real_no_cumple()
    {
        var x = await StageAsync(100m);
        await using var h = x.H;
        var lot = await PostAsync(x, x.S.Day, "run");
        var day = 0;
        Day(x, ref day, 3);
        await SignInAsync(x);
        await ReleaseAsync(x, lot);
        async Task<long> VersionAsync() => (await DetailAsync(x, lot)).GetProperty("lot").GetProperty("version").GetInt64();
        async Task<string> RowAsync() => (await h.ScalarAsync<string>("SELECT status || ':' || coalesce(block_cause, '-') || ':' || coalesce(blocked_from, '-') FROM mfg.fg_lot"))!;

        var first = await BreakAsync(x, lot, 3, "a", Small(75187m), Small(44000m));
        var afterFirst = await RowAsync();
        await h.RunAsync(new UnblockLot(h.CompanyId, x.Quality, "un-1", x.S.Plant, lot, await VersionAsync(), "Se espera la rotura a 28 días"), new UnblockLotHandler());
        // E-LAB1-02-9: a third specimen and still NO CUMPLE by estimate — (98.365953 + 2 × 57.564498) ÷ 3 = 71.164983 → ÷ 0.84 = 84.720218.
        // Calidad's decision holds: the lot is not blocked again.
        var second = await BreakAsync(x, lot, 3, "b", Small(44000m));
        var afterSecond = await RowAsync();
        // Calidad blocks and unblocks a released lot by hand (E-LAB1-4): it returns to RELEASED.
        await h.RunAsync(new BlockLot(h.CompanyId, x.Quality, "block", x.S.Plant, lot, await VersionAsync(), "Fisuras en la inspección"), new BlockLotHandler());
        var manual = await RowAsync();
        await h.RunAsync(new UnblockLot(h.CompanyId, x.Quality, "un-2", x.S.Plant, lot, await VersionAsync(), "Inspección repetida"), new UnblockLotHandler());
        var afterManual = await RowAsync();

        // The real breaks at 28 days fail too (30,000 ÷ 770.25 = 38.948393): the estimate became real, so the lot is blocked again.
        Day(x, ref day, 28);
        await SignInAsync(x);
        var real = await BreakAsync(x, lot, 28, "c", Nominal(30000m), Nominal(30000m), Nominal(30000m));

        Assert.Equal("FAILS:ESTIMATED:92.815745:68.529164:FEW_SPECIMENS+HIGH_CV:blocked", Verdict(first));
        Assert.Equal("FAILS:ESTIMATED:84.720218:68.529164:HIGH_CV:-", Verdict(second));
        Assert.Equal(("BLOCKED:LAB:RELEASED", "RELEASED:-:-", "BLOCKED:MANUAL:RELEASED", "RELEASED:-:-"), (afterFirst, afterSecond, manual, afterManual));
        Assert.Equal("FAILS:REAL:38.948393:38.948393:HIGH_CV:blocked", Verdict(real));
        Assert.Equal("BLOCKED:LAB:RELEASED", await RowAsync());
        Assert.Equal("CURING→RELEASED,RELEASED→BLOCKED,BLOCKED→RELEASED,RELEASED→BLOCKED,BLOCKED→RELEASED,RELEASED→BLOCKED", await h.ScalarAsync<string>(
            $"SELECT string_agg(from_state || '→' || to_state, ',' ORDER BY state_history_id) FROM core.state_history WHERE aggregate_id = '{lot}' AND from_state IS NOT NULL"));
    }

    [Trait("AcceptanceLab1", "LAB-11")]
    [Fact]
    public async Task The_factor_of_an_age_is_the_lots_own_once_two_lots_have_it_and_the_initial_one_before()
    {
        var x = await StageAsync(null);
        await using var h = x.H;
        var t1 = (await h.RunAsync(new DefineShift(h.CompanyId, x.S.Manager, "t1", x.S.Plant, "T1", new TimeOnly(6, 0), new TimeOnly(14, 0)), new DefineShiftHandler())).ResultRef;
        var t2 = (await h.RunAsync(new DefineShift(h.CompanyId, x.S.Manager, "t2", x.S.Plant, "T2", new TimeOnly(14, 0), new TimeOnly(22, 0)), new DefineShiftHandler())).ResultRef;
        var (a, b, c) = (await PostAsync(x, t1, "a"), await PostAsync(x, t2, "b"), await PostAsync(x, x.S.Day, "c"));
        var day = 0;
        Day(x, ref day, 3);
        await SignInAsync(x);
        // At 3 days: A 60,000 kg → 77.896787; B 64,000 → 83.089906; C 62,000 → 80.493346 (÷ 770.25 cm²).
        await BreakAsync(x, a, 3, "a3", Nominal(60000m));
        await BreakAsync(x, b, 3, "b3", Nominal(64000m));
        var none = await BreakAsync(x, c, 3, "c3", Nominal(62000m));
        async Task<string> FactorOfCAsync(string key)
        {
            await h.RunAsync(new ReevaluateLot(h.CompanyId, x.Quality, key, x.S.Plant, c), new ReevaluateLotHandler());
            var e = (await DetailAsync(x, c)).GetProperty("evaluation");
            return $"{e.GetProperty("factorUsed").GetString()}:{e.GetProperty("factorSource").GetString()}:{e.GetProperty("strength28d").GetString()}";
        }

        Day(x, ref day, 28);
        await SignInAsync(x);
        // At 28 days A and B give 103.862382: A's factor 77.896787 ÷ 103.862382 = 0.75, B's 0.80; the own factor of 3 days is 0.775.
        await BreakAsync(x, a, 28, "a28", Nominal(80000m));
        var oneLot = await FactorOfCAsync("re-1");
        await BreakAsync(x, b, 28, "b28", Nominal(80000m));
        var twoLots = await FactorOfCAsync("re-2");

        // 80.493346 ÷ 0.84 = 95.825412 with the initial factor; ÷ 0.775 = 103.862382 with the lots' own.
        Assert.Equal("NO_SPEC:ESTIMATED:95.825412:95.825412:FEW_SPECIMENS:-", Verdict(none));
        Assert.Equal(("0.840000:INITIAL:95.825412", "0.775000:OWN:103.862382"), (oneLot, twoLots));
        Assert.Equal("3:0.775000:2", await h.ScalarAsync<string>($"SELECT age_days::text || ':' || factor::text || ':' || lots::text FROM qa.own_age_factors('{h.CompanyId}')"));
    }
}
