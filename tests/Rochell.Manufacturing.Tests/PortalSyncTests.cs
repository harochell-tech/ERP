using System.Text.Json;
using Rochell.Identity;
using Rochell.Manufacturing.Machines;
using Rochell.Manufacturing.Portal;
using Rochell.Manufacturing.Recipes;
using Rochell.Manufacturing.Runs;
using Rochell.Platform.Commands;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Manufacturing.Tests;

/// <summary>
/// MFG2-02 (E-MFG2-1…14, E-MFG2-01-1…8): the portal's machines and batch plant become DRAFT shift summaries — opened runs, units from
/// the blocks, the theoretical as a pending placeholder until the batch plant's post, the post split by theoretical consumption, a
/// person's change kept, never posted pending, warnings for what is not paired. Recipe of MFG1-03: 150 units per batch, cement 180 kg,
/// sand 1.8 t, admixture 1.5 l per batch.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PortalSyncTests(PostgresFixture postgres)
{
    private sealed class FakePortal : IPortalSource
    {
        public List<PortalShift> Shifts { get; } = [];

        public List<PortalPost> Posts { get; } = [];

        public bool Down { get; set; }

        public Task<PortalExport> FetchAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
            => Down ? throw new HttpRequestException("Simulated: the portal does not answer.") : Task.FromResult(new PortalExport("now", [.. Shifts], [.. Posts]));
    }

    private sealed record World(TestHarness H, ProductionRunTests.Setup S, FakePortal Portal, string Day);

    private static async Task<World> WorldAsync(TestHarness h)
    {
        var s = await ProductionRunTests.SetupAsync(h);
        await h.GrantAsync(h.CompanyId, IdentityConstants.DailyProcessUserId, "PROCESO_DIARIO");
        await h.AdminRequireAsync(
            $"""
            INSERT INTO mfg.portal_machine VALUES ('{h.CompanyId}', 'planta2', '{s.Machine}', 'dosificadora2', 1);
            INSERT INTO mfg.portal_mould VALUES ('{h.CompanyId}', '6', '{s.Block}', 1);
            INSERT INTO mfg.portal_shift VALUES ('{h.CompanyId}', 1, '{s.Day}', 1);
            INSERT INTO mfg.portal_material VALUES ('{h.CompanyId}', 'dosificadora2', 'CEMENTO', '{s.Cement}', 'kg', '{s.Patio}', 1),
                                                   ('{h.CompanyId}', 'dosificadora2', 'ARENA', '{s.Sand}', 'm3', '{s.Patio}', 1),
                                                   ('{h.CompanyId}', 'dosificadora2', 'ADITIVO', '{s.Additive}', 'l', '{s.Patio}', 1);
            """);
        return new World(h, s, new FakePortal(), s.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static PortalShift Shift(World w, string machine, int cycles, bool closed, int blocksPerCycle = 4)
        => new(machine, w.Day, 1, $"{w.Day} 07:00:00", $"{w.Day} 19:00:00", closed, cycles, 0, [new PortalMould("6", blocksPerCycle, cycles, cycles * blocksPerCycle)], 12,
            $"{w.Day} 07:05:00", $"{w.Day} 18:40:00", 0);

    private static PortalPost Post(World w, long id, int batches, string cement, string sandM3, string additive)
        => new(id, "dosificadora2", w.Day, 1, batches, [new PortalMaterial("CEMENTO", cement, "kg"), new PortalMaterial("ARENA", sandM3, "m3"), new PortalMaterial("ADITIVO", additive, "l")],
            $"{w.Day} 19:02:00");

    private static async Task<PortalRunner.PassResult> PassAsync(World w)
    {
        var session = await w.H.Sessions.StartServiceSessionAsync(IdentityConstants.DailyProcessUserId);
        return await PortalRunner.RunOnceAsync(w.H.Pipeline, w.Portal, w.H.CompanyId, session, w.S.Today, 1, CancellationToken.None);
    }

    private static Task<string?> SummaryAsync(TestHarness h, Guid machine)
        => h.ScalarAsync<string>(
            """
            SELECT s.source || ':' || s.consumption_source || ':' || s.batches || ':' || s.good_units::numeric(18,0) || ':' || s.status || ':' ||
                   (SELECT string_agg(i.code || '=' || c.qty::numeric(18,3) || '/' || c.theoretical_qty::numeric(18,3), ',' ORDER BY i.code)
                    FROM mfg.material_consumption c JOIN md.item i ON i.item_id = c.material_item_id WHERE c.summary_id = s.summary_id)
            FROM mfg.shift_summary s JOIN mfg.production_run r ON r.run_id = s.run_id WHERE r.machine_id = @m AND s.status IN ('DRAFT', 'POSTED')
            """,
            ("m", machine));

    [Trait("AcceptanceMfg2", "MFG2-SYNC")]
    [Fact]
    public async Task The_portal_shift_becomes_a_pending_draft_then_takes_the_batch_plant_consumption_and_is_posted_by_the_manager()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);

        // 375 cycles × 4 = 1,500 blocks while the shift runs: run opened, draft with the theoretical as placeholder.
        w.Portal.Shifts.Add(Shift(w, "planta2", 375, closed: false));
        var first = await PassAsync(w);
        var pending = await SummaryAsync(h, w.S.Machine);
        var run = await h.ScalarAsync<Guid>("SELECT run_id FROM mfg.production_run WHERE machine_id = @m", ("m", w.S.Machine));
        var version = await h.ScalarAsync<long>("SELECT version FROM mfg.shift_summary WHERE run_id = @r", ("r", run));
        var early = await Assert.ThrowsAsync<DomainException>(
            () => h.RunAsync(new PostShiftSummary(h.CompanyId, w.S.Manager, "post-0", w.S.Plant, run, version), new PostShiftSummaryHandler()));

        // The shift ends and the batch plant posts (twice: the second replaces the first).
        w.Portal.Shifts[0] = Shift(w, "planta2", 375, closed: true);
        w.Portal.Posts.Add(Post(w, 7, 10, "1700", "12", "15"));
        w.Portal.Posts.Add(Post(w, 8, 11, "1850.5", "12.5", "16"));
        var second = await PassAsync(w);
        var real = await SummaryAsync(h, w.S.Machine);
        var third = await PassAsync(w);

        Assert.True(first.Ok);
        Assert.Equal((1, 1), (first.RunsOpened, first.Written));
        // 1,500 / 150 = 10 batches; theoretical cement 1,800 kg, sand 18 t, admixture 15 l.
        Assert.Equal("PORTAL:PENDING:10:1500:DRAFT:ADITIVO-P=15.000/15.000,ARENA-LAVADA=18.000/18.000,CEMENTO-GU=1800.000/1800.000", pending);
        Assert.Equal(ManufacturingErrors.ConsumptionPending, early.Code);
        var detail = JsonDocument.Parse(await h.QueryAsync(new Rochell.Manufacturing.Queries.GetProductionRun(h.CompanyId, w.S.Supervisor, run), new Rochell.Manufacturing.Queries.GetProductionRunHandler()))
            .RootElement.GetProperty("portal");
        Assert.Equal(("PORTAL", "BATCH_PLANT", "dosificadora2", "1500", true), (detail.GetProperty("source").GetString(), detail.GetProperty("consumptionSource").GetString(),
            detail.GetProperty("batchPlant").GetString(), detail.GetProperty("portalUnits").GetString(), detail.GetProperty("shiftClosed").GetBoolean()));
        Assert.Equal((2, 1), (second.Posts, second.Written));
        // The latest post: 11 batches, cement 1,850.5 kg, sand 12.5 m³ × 1.47 = 18.375 t, admixture 16 l; theoretical by units unchanged.
        Assert.Equal("PORTAL:BATCH_PLANT:11:1500:DRAFT:ADITIVO-P=16.000/15.000,ARENA-LAVADA=18.375/18.000,CEMENTO-GU=1850.500/1800.000", real);
        Assert.Equal(0, third.Written);

        // The Supervisor records the broken blocks (same consumption): the portal no longer replaces the draft; the manager posts.
        var consumption = new List<ConsumptionInput>
        {
            new(w.S.Cement, w.S.Patio, 1850.5m, "kg"), new(w.S.Sand, w.S.Patio, 18.375m, "t"), new(w.S.Additive, w.S.Patio, 16m, "l"),
        };
        var edited = JsonDocument.Parse((await h.RunAsync(
            new RecordShiftSummary(h.CompanyId, w.S.Supervisor, "edit", w.S.Plant, run, 11, 1480m, 0m, 20m, consumption), new RecordShiftSummaryHandler())).ResultPayload).RootElement;
        w.Portal.Shifts[0] = Shift(w, "planta2", 400, closed: true);
        var fourth = await PassAsync(w);
        var posted = JsonDocument.Parse((await h.RunAsync(
            new PostShiftSummary(h.CompanyId, w.S.Manager, "post", w.S.Plant, run, edited.GetProperty("version").GetInt64()), new PostShiftSummaryHandler())).ResultPayload).RootElement;

        Assert.Equal("BATCH_PLANT", edited.GetProperty("consumptionSource").GetString()); // the same consumption keeps its source
        Assert.Equal(0, fourth.Written);
        Assert.Equal("POSTED", posted.GetProperty("status").GetString());
        Assert.StartsWith("PORTAL:BATCH_PLANT:11:1480:POSTED:", await SummaryAsync(h, w.S.Machine));
        Assert.NotNull(await h.ScalarAsync<Guid?>("SELECT edited_by FROM mfg.shift_summary WHERE run_id = @r", ("r", run)));
    }

    [Fact]
    public async Task A_batch_plant_feeding_two_machines_splits_its_consumption_by_their_theoretical_and_typing_it_needs_a_reason()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var second = (await h.RunAsync(new CreateMachine(h.CompanyId, w.S.Manager, "m2", w.S.Plant, "BESSER-2", "Besser V3-12 (2)"), new CreateMachineHandler())).ResultRef;
        var recipe = (await h.RunAsync(
            new PrepareRecipe(h.CompanyId, w.S.Supervisor, "r2", w.S.Plant, w.S.Block, second, 150m, 6m, 600m, 24, 168,
                [new RecipeLineInput(w.S.Cement, 180m), new RecipeLineInput(w.S.Sand, 1.8m), new RecipeLineInput(w.S.Additive, 1.5m)]),
            new PrepareRecipeHandler())).ResultRef;
        await h.RunAsync(new ApproveRecipe(h.CompanyId, w.S.Manager, "r2a", w.S.Plant, recipe), new ApproveRecipeHandler());
        await h.AdminRequireAsync($"INSERT INTO mfg.portal_machine VALUES ('{h.CompanyId}', 'planta3', '{second}', 'dosificadora2', 1)");

        // planta2 1,500 blocks, planta3 750: the post's 3,000 kg of cement goes 2,000 / 1,000.
        w.Portal.Shifts.Add(Shift(w, "planta2", 375, closed: true));
        w.Portal.Shifts.Add(Shift(w, "planta3", 125, closed: true, blocksPerCycle: 6));
        w.Portal.Posts.Add(Post(w, 1, 15, "3000", "15", "24"));
        var pass = await PassAsync(w);
        var one = await SummaryAsync(h, w.S.Machine);
        var two = await SummaryAsync(h, second);
        var run = await h.ScalarAsync<Guid>("SELECT run_id FROM mfg.production_run WHERE machine_id = @m", ("m", w.S.Machine));
        var typed = new List<ConsumptionInput> { new(w.S.Cement, w.S.Patio, 1900m, "kg"), new(w.S.Sand, w.S.Patio, 10m, "m3"), new(w.S.Additive, w.S.Patio, 16m, "l") };
        var noReason = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new RecordShiftSummary(h.CompanyId, w.S.Supervisor, "t0", w.S.Plant, run, 10, 1500m, 0m, 0m, typed), new RecordShiftSummaryHandler()));
        var withReason = JsonDocument.Parse((await h.RunAsync(
            new RecordShiftSummary(h.CompanyId, w.S.Supervisor, "t1", w.S.Plant, run, 10, 1500m, 0m, 0m, typed, "La báscula del cemento marcó de más"), new RecordShiftSummaryHandler()))
            .ResultPayload).RootElement;

        Assert.Equal((2, 2), (pass.RunsOpened, pass.Written));
        // Batches 15 split by units: 1,500 / 2,250 → 10, the rest 5. Sand 15 m³ → 10 / 5 m³ = 14.7 / 7.35 t; admixture 24 → 16 / 8.
        Assert.Equal("PORTAL:BATCH_PLANT:10:1500:DRAFT:ADITIVO-P=16.000/15.000,ARENA-LAVADA=14.700/18.000,CEMENTO-GU=2000.000/1800.000", one);
        Assert.Equal("PORTAL:BATCH_PLANT:5:750:DRAFT:ADITIVO-P=8.000/7.500,ARENA-LAVADA=7.350/9.000,CEMENTO-GU=1000.000/900.000", two);
        Assert.Equal(ManufacturingErrors.ReasonRequired, noReason.Code);
        Assert.Equal("MANUAL", withReason.GetProperty("consumptionSource").GetString());
    }

    [Fact]
    public async Task What_is_not_paired_is_reported_and_a_portal_that_does_not_answer_is_recorded()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        w.Portal.Shifts.Add(Shift(w, "planta9", 10, closed: false));
        w.Portal.Shifts.Add(new PortalShift("planta2", w.Day, 1, $"{w.Day} 07:00:00", $"{w.Day} 19:00:00", false, 10, 0, [new PortalMould("8", 3, 10, 30)], 0, null, null, 0));

        var pass = await PassAsync(w);
        w.Portal.Down = true;
        var down = await PassAsync(w);

        Assert.True(pass.Ok);
        Assert.Contains(pass.Warnings, x => x.Contains("planta9", StringComparison.Ordinal));
        Assert.Contains(pass.Warnings, x => x.Contains("molde 8", StringComparison.Ordinal));
        Assert.False(down.Ok);
        Assert.Contains("does not answer", down.Error, StringComparison.Ordinal);
        Assert.Equal("true:true", await h.ScalarAsync<string>(
            "SELECT (last_ok_at IS NOT NULL) || ':' || (last_error IS NOT NULL) FROM mfg.portal_sync_state WHERE company_id = @c", ("c", h.CompanyId)));
    }
}
