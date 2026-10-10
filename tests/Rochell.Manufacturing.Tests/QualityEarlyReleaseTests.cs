using System.Text.Json;
using Rochell.Manufacturing.Lots;
using Rochell.Manufacturing.Machines;
using Rochell.Manufacturing.Quality;
using Rochell.Manufacturing.Runs;
using Rochell.Platform.Commands;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Manufacturing.Tests;

/// <summary>
/// LAB1-03d (E-LAB1-11…17): the initial release needs 3 specimens at 1 day averaging the item's 1-day minimum (a retest at 2 days may give
/// it); the final release comes from 3 specimens at 3 days averaging the 3-day minimum, and the 28-day breaks still verify it; below the
/// 3-day minimum the lot is blocked. Block 4" is not tested: its visual release is its only one.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class QualityEarlyReleaseTests(PostgresFixture postgres)
{
    private sealed record Stage(TestHarness H, FakeClock Clock, ProductionRunTests.Setup S)
    {
        public Guid Quality { get; set; }

        public Guid Lab { get; set; }
    }

    private static JsonElement Json(string payload) => JsonDocument.Parse(payload).RootElement;

    /// <summary>
    /// 19.5 × 39.5 = 770.25 cm²: 30,000 kg = 38.95, 35,000 kg = 45.44, 52,000 kg = 67.51, 56,000 kg = 72.70, 60,000 kg = 77.90 kg/cm².
    /// </summary>
    private static CompressionSpecimen Load(decimal kg) => new(kg, 19.5m, 19.5m, 39.5m);

    private async Task<Stage> StageAsync(decimal? min1d, decimal? min3d, decimal? min28d, bool tested = true)
    {
        var clock = new FakeClock();
        var h = await TestHarness.CreateAsync(postgres, clock);
        var s = await ProductionRunTests.SetupAsync(h);
        var stage = new Stage(h, clock, s) { Quality = await h.SessionWithRolesAsync("CALIDAD"), Lab = await h.SessionWithRolesAsync("LABORATORIO") };
        await h.RunAsync(
            new SetItemSpec(h.CompanyId, stage.Quality, "spec", s.Block, "8", 19.5m, 19.5m, 39.5m, null, min28d, null, min1d, min3d, tested), new SetItemSpecHandler());
        await h.RunAsync(new SetMachineShortCode(h.CompanyId, stage.Quality, "p1", s.Plant, s.Machine, 1, "P1"), new SetMachineShortCodeHandler());
        return stage;
    }

    private static async Task<Guid> PostAsync(Stage x, string key)
    {
        var (h, s) = (x.H, x.S);
        var run = (await h.RunAsync(new StartProductionRun(h.CompanyId, s.Supervisor, key, s.Plant, s.Machine, s.Day, s.Today, s.Block), new StartProductionRunHandler())).ResultRef;
        var summary = Json((await ProductionRunTests.Record(h, s, run, key + "-rec")).ResultPayload);
        return Json((await h.RunAsync(new PostShiftSummary(h.CompanyId, s.Manager, key + "-post", s.Plant, run, summary.GetProperty("version").GetInt64()), new PostShiftSummaryHandler())).ResultPayload)
            .GetProperty("lotId").GetGuid();
    }

    private static async Task DayAsync(Stage x, int day)
    {
        x.Clock.Advance(x.S.Today.AddDays(day).ToDateTime(new TimeOnly(23, 30)) - x.Clock.UtcNow);
        x.Quality = await x.H.SessionWithRolesAsync("CALIDAD");
        x.Lab = await x.H.SessionWithRolesAsync("LABORATORIO");
    }

    private static async Task<JsonElement> BreakAsync(Stage x, Guid lot, int day, string key, params CompressionSpecimen[] specimens)
        => Json((await x.H.RunAsync(new RecordCompressionTests(x.H.CompanyId, x.Lab, key, x.S.Plant, lot, x.S.Today.AddDays(day), specimens), new RecordCompressionTestsHandler())).ResultPayload)
            .GetProperty("evaluation");

    private static async Task<JsonElement> DetailAsync(Stage x, Guid lot) => Json(await x.H.QueryAsync(new GetLabLot(x.H.CompanyId, x.Lab, lot), new GetLabLotHandler()));

    private static async Task<long> VersionAsync(Stage x, Guid lot) => (await DetailAsync(x, lot)).GetProperty("lot").GetProperty("version").GetInt64();

    private static async Task<CommandResult> ReleaseAsync(Stage x, Guid lot, string key)
        => await x.H.RunAsync(new ReleaseLot(x.H.CompanyId, x.Quality, key, x.S.Plant, lot, await VersionAsync(x, lot), x.S.Patio), new ReleaseLotHandler());

    private static async Task<CommandResult> FinalAsync(Stage x, Guid lot, string key)
        => await x.H.RunAsync(new FinalReleaseLot(x.H.CompanyId, x.Quality, key, x.S.Plant, lot, await VersionAsync(x, lot)), new FinalReleaseLotHandler());

    private static string Check(JsonElement evaluation, string prefix)
        => $"{evaluation.GetProperty(prefix + "Check").GetString()}:{evaluation.GetProperty(prefix + "Specimens").GetInt32()}:{evaluation.GetProperty(prefix + "Avg").GetString()}";

    [Trait("AcceptanceLab1", "LAB-18")]
    [Trait("AcceptanceLab1", "LAB-19")]
    [Fact]
    public async Task The_initial_release_needs_the_1_day_minimum_the_final_one_the_3_day_minimum_and_28_days_still_verify()
    {
        var x = await StageAsync(40m, 70m, 50m);
        await using var h = x.H;
        var lot = await PostAsync(x, "run");

        // Day 1: no breaks yet, then three below 40 — the lot stays curing, not blocked.
        await DayAsync(x, 1);
        var none = await Assert.ThrowsAsync<DomainException>(() => ReleaseAsync(x, lot, "rel-0"));
        await BreakAsync(x, lot, 1, "d1", Load(30000m), Load(30000m), Load(30000m));
        var low = await Assert.ThrowsAsync<DomainException>(() => ReleaseAsync(x, lot, "rel-1"));
        var afterDay1 = (await DetailAsync(x, lot)).GetProperty("lot").GetProperty("status").GetString();

        // Day 2: the retest reaches 40 with two specimens only — not enough; a third one releases it.
        await DayAsync(x, 2);
        await BreakAsync(x, lot, 2, "d2", Load(35000m), Load(35000m));
        var few = await Assert.ThrowsAsync<DomainException>(() => ReleaseAsync(x, lot, "rel-2"));
        await BreakAsync(x, lot, 2, "d2b", Load(35000m));
        await ReleaseAsync(x, lot, "rel-3");
        var initial = (await DetailAsync(x, lot)).GetProperty("evaluation");

        // Day 3: three at 72.70 — the final release. (The 28-day estimate still comes from the earliest breaks: 38.95 ÷ 0.70 = 55.64 ≥ 50.)
        await DayAsync(x, 3);
        var threeDays = await BreakAsync(x, lot, 3, "d3", Load(56000m), Load(56000m), Load(56000m));
        var final = Json((await FinalAsync(x, lot, "final")).ResultPayload);
        var finalCheck = (await DetailAsync(x, lot)).GetProperty("evaluation");

        // Day 28: the real breaks (38.95) do not reach 50 — the verification blocks the lot although it had its final release (E-LAB1-13).
        await DayAsync(x, 28);
        var real = await BreakAsync(x, lot, 28, "d28", Load(30000m), Load(30000m), Load(30000m));
        var lotAfter = (await DetailAsync(x, lot)).GetProperty("lot");

        Assert.Equal(QualityErrors.InitialReleaseRefused, none.Code);
        Assert.Equal(QualityErrors.InitialReleaseRefused, low.Code);
        Assert.Equal(QualityErrors.InitialReleaseRefused, few.Code);
        Assert.Equal("CURING", afterDay1);
        Assert.Equal("PASS:3:45.439792", Check(initial, "initial"));
        Assert.Equal(2, initial.GetProperty("initialAge").GetInt32());
        Assert.Equal("COMPLIES:ESTIMATED:-", $"{threeDays.GetProperty("verdict").GetString()}:{threeDays.GetProperty("basis").GetString()}:{(threeDays.GetProperty("blocked").GetBoolean() ? "blocked" : "-")}");
        Assert.Equal("FINAL_RELEASED:3_DAYS", $"{final.GetProperty("status").GetString()}:{final.GetProperty("basis").GetString()}");
        Assert.Equal("PASS:3:72.703668", Check(finalCheck, "final"));
        Assert.Equal("FAILS:REAL:True", $"{real.GetProperty("verdict").GetString()}:{real.GetProperty("basis").GetString()}:{real.GetProperty("blocked").GetBoolean()}");
        Assert.Equal("BLOCKED:LAB", $"{lotAfter.GetProperty("status").GetString()}:{lotAfter.GetProperty("blockCause").GetString()}");
    }

    [Trait("AcceptanceLab1", "LAB-19")]
    [Fact]
    public async Task Breaks_at_3_days_below_the_minimum_block_the_released_lot_and_refuse_the_final_release()
    {
        var x = await StageAsync(40m, 70m, null);
        await using var h = x.H;
        var lot = await PostAsync(x, "run");
        await DayAsync(x, 1);
        await BreakAsync(x, lot, 1, "d1", Load(35000m), Load(35000m), Load(35000m));
        await ReleaseAsync(x, lot, "rel");

        await DayAsync(x, 3);
        var low = await BreakAsync(x, lot, 3, "d3", Load(52000m), Load(52000m), Load(52000m));
        var detail = await DetailAsync(x, lot);
        await x.H.RunAsync(new UnblockLot(x.H.CompanyId, x.Quality, "unblock", x.S.Plant, lot, await VersionAsync(x, lot), "Revisión: se ensaya otra vez a 28 días"), new UnblockLotHandler());
        var refused = await Assert.ThrowsAsync<DomainException>(() => FinalAsync(x, lot, "final"));
        // The same 3-day result does not block it twice (E-LAB1-02-9).
        var again = Json((await x.H.RunAsync(new ReevaluateLot(x.H.CompanyId, x.Quality, "again", x.S.Plant, lot), new ReevaluateLotHandler())).ResultPayload).GetProperty("evaluation");

        Assert.Equal("NO_SPEC:True", $"{low.GetProperty("verdict").GetString()}:{low.GetProperty("blocked").GetBoolean()}");
        Assert.Equal("BLOCKED:LAB", $"{detail.GetProperty("lot").GetProperty("status").GetString()}:{detail.GetProperty("lot").GetProperty("blockCause").GetString()}");
        Assert.Contains("liberación final", detail.GetProperty("lot").GetProperty("blockReason").GetString(), StringComparison.Ordinal);
        Assert.Equal("FAIL:3:67.510549", Check(detail.GetProperty("evaluation"), "final"));
        Assert.Equal(QualityErrors.FinalReleaseRefused, refused.Code);
        Assert.False(again.GetProperty("blocked").GetBoolean());
    }

    [Trait("AcceptanceLab1", "LAB-20")]
    [Fact]
    public async Task An_item_that_is_not_tested_is_released_by_sight_and_never_gets_a_final_release_nor_a_certificate()
    {
        var x = await StageAsync(null, null, null, tested: false);
        await using var h = x.H;
        var minimums = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new SetItemSpec(h.CompanyId, x.Quality, "spec-bad", x.S.Block, "8", 19.5m, 19.5m, 39.5m, null, null, null, 40m, null, false), new SetItemSpecHandler()));
        var lot = await PostAsync(x, "run");
        await DayAsync(x, 1);
        await ReleaseAsync(x, lot, "rel");
        var evaluation = Json((await h.RunAsync(new ReevaluateLot(h.CompanyId, x.Quality, "eval", x.S.Plant, lot), new ReevaluateLotHandler())).ResultPayload).GetProperty("evaluation");
        var final = await Assert.ThrowsAsync<DomainException>(() => FinalAsync(x, lot, "final"));
        await BreakAsync(x, lot, 1, "d1", Load(60000m));
        var certificate = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new IssueLabCertificate(h.CompanyId, x.Quality, "cert", x.S.Plant, lot, x.S.Today.AddDays(1)), new IssueLabCertificateHandler()));
        var specs = Json(await h.QueryAsync(new ListItemSpecs(h.CompanyId, x.Lab), new ListItemSpecsHandler()));

        Assert.Equal(QualityErrors.SpecInvalid, minimums.Code);
        Assert.Equal("NOT_TESTED:", $"{evaluation.GetProperty("verdict").GetString()}:{string.Join('+', evaluation.GetProperty("alerts").EnumerateArray().Select(a => a.GetString()))}");
        Assert.Equal(QualityErrors.FinalReleaseRefused, final.Code);
        Assert.Equal(QualityErrors.CertificateRefused, certificate.Code);
        Assert.False(specs.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("itemId").GetGuid() == x.S.Block).GetProperty("labTested").GetBoolean());
        Assert.Equal("40:70", $"{specs.GetProperty("defaultMinAvg1d").GetString()}:{specs.GetProperty("defaultMinAvg3d").GetString()}");
    }
}
