using System.Text.Json;
using Rochell.Manufacturing.Costing;
using Rochell.Manufacturing.Lots;
using Rochell.Manufacturing.Runs;
using Rochell.Manufacturing.Shifts;
using Rochell.Platform.Commands;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Manufacturing.Tests;

/// <summary>
/// E-MFG1-08-3: production races, throttled like CC-04 — two postings of one summary (MFG-10), two releases of one lot, postings that
/// exhaust a material, and a settlement racing the reversal of one of its summaries.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ProductionConcurrencyTests(PostgresFixture postgres)
{
    private const int Racers = 8;

    /// <summary>Runs the actions together; returns each one's error code (null when it succeeded).</summary>
    private static async Task<List<string?>> RaceAsync(IEnumerable<Func<Task>> actions)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var throttle = new SemaphoreSlim(Racers);
        var tasks = actions.Select(action => Task.Run(async () =>
        {
            await gate.Task;
            await throttle.WaitAsync();
            try
            {
                await action();
                return (string?)null;
            }
            catch (DomainException ex)
            {
                return ex.Code;
            }
            finally
            {
                throttle.Release();
            }
        })).ToList();
        gate.SetResult();
        return [.. await Task.WhenAll(tasks)];
    }

    private static async Task<Guid> RecordedRunAsync(TestHarness h, ProductionRunTests.Setup s, Guid shift, string key, decimal cement = 1850m)
    {
        var run = (await h.RunAsync(new StartProductionRun(h.CompanyId, s.Supervisor, $"start-{key}", s.Plant, s.Machine, shift, s.Today, s.Block), new StartProductionRunHandler())).ResultRef;
        await ProductionRunTests.Record(h, s, run, $"rec-{key}", cement: cement);
        return run;
    }

    private static async Task<Guid> ShiftAsync(TestHarness h, ProductionRunTests.Setup s, string code, int starts)
        => (await h.RunAsync(new DefineShift(h.CompanyId, s.Manager, $"shift-{code}", s.Plant, code, new TimeOnly(starts, 0), new TimeOnly(starts + 1, 0)), new DefineShiftHandler())).ResultRef;

    [Trait("AcceptanceMfg1", "MFG-10")]
    [Fact]
    public async Task MFG10_two_postings_of_one_summary_at_once_receive_the_lot_once()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ProductionRunTests.SetupAsync(h);
        var run = await RecordedRunAsync(h, s, s.Day, "a");
        var managers = new[] { s.Manager, await h.SessionWithRolesAsync("GERENTE_PLANTA") };

        var outcomes = await RaceAsync(managers.Select((m, i) => (Func<Task>)(() =>
            h.RunAsync(new PostShiftSummary(h.CompanyId, m, $"post-{i}", s.Plant, run, 1), new PostShiftSummaryHandler()))));

        Assert.Equal(1, outcomes.Count(o => o is null));
        Assert.Contains(Assert.Single(outcomes, o => o is not null), new[] { ManufacturingErrors.InvalidState, ManufacturingErrors.VersionConflict });
        Assert.Equal("1|1|1480.000000|2", await h.ScalarAsync<string>(
            """
            SELECT (SELECT count(*) FROM mfg.fg_lot) || '|' || (SELECT count(*) FROM inv.inv_quantity_entry WHERE movement_type::text = 'PRODUCTION_RECEIPT') || '|'
                   || (SELECT sum(quantity)::text FROM inv.inv_quantity_entry WHERE movement_type::text = 'PRODUCTION_RECEIPT') || '|'
                   || (SELECT count(*) FROM fin.gl_journal WHERE source_event_id IN (SELECT material_event_id FROM mfg.shift_summary UNION SELECT receipt_event_id FROM mfg.shift_summary))
            """));
    }

    [Fact]
    public async Task Two_releases_of_one_lot_at_once_move_it_once()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var s = await ProductionRunTests.SetupAsync(h);
        var run = await RecordedRunAsync(h, s, s.Day, "a");
        var lot = JsonDocument.Parse((await h.RunAsync(new PostShiftSummary(h.CompanyId, s.Manager, "post", s.Plant, run, 1), new PostShiftSummaryHandler())).ResultPayload)
            .RootElement.GetProperty("lotId").GetGuid();
        clock.Advance(TimeSpan.FromHours(48));
        var quality = new[] { await h.SessionWithRolesAsync("CALIDAD"), await h.SessionWithRolesAsync("CALIDAD") };

        var outcomes = await RaceAsync(quality.Select((q, i) => (Func<Task>)(() =>
            h.RunAsync(new ReleaseLot(h.CompanyId, q, $"rel-{i}", s.Plant, lot, 1, s.Patio), new ReleaseLotHandler()))));

        Assert.Equal(1, outcomes.Count(o => o is null));
        Assert.Contains(Assert.Single(outcomes, o => o is not null), new[] { ManufacturingErrors.InvalidState, ManufacturingErrors.VersionConflict });
        Assert.Equal("PATIO-A:1480.000000|CURADO:0.000000", await h.ScalarAsync<string>(
            $"SELECT string_agg(l.code || ':' || b.quantity::text, '|' ORDER BY l.is_curing, l.code) FROM inv.inv_stock_balance b JOIN md.location l ON l.location_id = b.location_id WHERE b.lot_id = '{lot}'"));
    }

    [Fact]
    public async Task Postings_that_exhaust_a_material_at_once_never_leave_its_stock_negative()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ProductionRunTests.SetupAsync(h); // 20,000 kg of cement
        var runs = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            runs.Add(await RecordedRunAsync(h, s, await ShiftAsync(h, s, $"T{i}", 1 + (2 * i)), $"r{i}", cement: 6000m)); // three fit, the fourth does not
        }

        var outcomes = await RaceAsync(runs.Select((run, i) => (Func<Task>)(async () =>
            await h.RunAsync(new PostShiftSummary(h.CompanyId, await h.SessionWithRolesAsync("GERENTE_PLANTA"), $"post-{i}", s.Plant, run, 1), new PostShiftSummaryHandler()))));

        Assert.Equal(3, outcomes.Count(o => o is null));
        Assert.Equal(Inventory.InventoryErrors.InsufficientStock, Assert.Single(outcomes, o => o is not null));
        Assert.Equal("2000.000000|0", await h.ScalarAsync<string>(
            "SELECT sum(quantity)::numeric(18,6)::text || '|' || count(*) FILTER (WHERE quantity < 0) FROM inv.inv_stock_balance WHERE item_id = @i", ("i", s.Cement)));
    }

    [Fact]
    public async Task A_settlement_racing_the_reversal_of_one_of_its_summaries_lets_only_one_of_them_through()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var s = await ProductionRunTests.SetupAsync(h);
        var started = JsonDocument.Parse((await h.RunAsync(new StartProductionRun(h.CompanyId, s.Supervisor, "start", s.Plant, s.Machine, s.Day, s.Today, s.Block), new StartProductionRunHandler())).ResultPayload).RootElement;
        var run = started.GetProperty("runId").GetGuid();
        await ProductionRunTests.Record(h, s, run, "rec");
        await h.RunAsync(new PostShiftSummary(h.CompanyId, s.Manager, "post", s.Plant, run, 1), new PostShiftSummaryHandler());
        await h.CreateActiveMapAsync("MATERIAL_USAGE_VARIANCE", await h.CreateAccountAsync("5102", "Variación de uso", isControl: false));
        await h.CreateActiveMapAsync("MATERIAL_PRICE_VARIANCE", await h.CreateAccountAsync("5103", "Variación de precio", isControl: false));
        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '0192f001-0000-7000-8000-000000000027' AND version = 1");
        clock.Advance(TimeSpan.FromDays(40));
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var manager = await h.SessionWithRolesAsync("GERENTE_PLANTA");

        var outcomes = await RaceAsync(
        [
            () => h.RunAsync(new SettleCostCollector(h.CompanyId, controller, "settle", s.Plant, started.GetProperty("collectorId").GetGuid(), 1), new SettleCostCollectorHandler()),
            () => h.RunAsync(new ReverseShiftSummary(h.CompanyId, manager, "reverse", s.Plant, run, 2, "Carrera con la liquidación"), new ReverseShiftSummaryHandler()),
        ]);

        Assert.Equal(1, outcomes.Count(o => o is null));
        Assert.Contains(Assert.Single(outcomes, o => o is not null), new[] { ManufacturingErrors.CollectorSettled, ManufacturingErrors.RunsOpen });
        // Either the collector settled over the posted summary, or the summary was reversed and the collector stays OPEN with WIP 0.
        Assert.Equal("0.00", await h.ScalarAsync<string>("SELECT coalesce(sum(debit - credit), 0)::numeric(19,2)::text FROM fin.gl_entry WHERE account_role = 'WIP'"));
        Assert.Equal(outcomes[0] is null ? "SETTLED:POSTED" : "OPEN:REVERSED", await h.ScalarAsync<string>(
            "SELECT (SELECT status FROM mfg.cost_collector) || ':' || (SELECT string_agg(status, ',') FROM mfg.shift_summary WHERE status <> 'DRAFT')"));
    }
}
