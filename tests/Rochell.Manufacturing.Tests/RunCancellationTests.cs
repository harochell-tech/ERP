using System.Text.Json;
using Rochell.Manufacturing.Runs;
using Rochell.Platform.Commands;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Manufacturing.Tests;

/// <summary>
/// MFG2-05 (E-MFG2-19): a run whose shift summary is still a draft is cancelled and its draft discarded with it, posting nothing; a
/// run whose summary was posted cannot be cancelled (it is reversed instead).
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class RunCancellationTests(PostgresFixture postgres)
{
    private static async Task<long> RunVersionAsync(TestHarness h, Guid run)
        => await h.ScalarAsync<long>("SELECT version FROM mfg.production_run WHERE run_id = @r", ("r", run));

    [Fact]
    public async Task A_run_with_a_draft_summary_is_cancelled_and_the_draft_discarded_without_posting()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ProductionRunTests.SetupAsync(h);
        var run = (await ProductionRunTests.Start(h, s, "start")).ResultRef;
        await ProductionRunTests.Record(h, s, run, "draft");
        var journals = await h.ScalarAsync<long>("SELECT count(*) FROM fin.gl_journal");

        var version = await RunVersionAsync(h, run);
        var noReason = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new CancelProductionRun(h.CompanyId, s.Supervisor, "c0", s.Plant, run, version, " "), new CancelProductionRunHandler()));
        var cancelled = JsonDocument.Parse((await h.RunAsync(
            new CancelProductionRun(h.CompanyId, s.Supervisor, "c1", s.Plant, run, version, "Ya está en el inventario de apertura"), new CancelProductionRunHandler())).ResultPayload).RootElement;

        Assert.Equal(ManufacturingErrors.ReasonRequired, noReason.Code);
        Assert.Equal("CANCELLED", cancelled.GetProperty("status").GetString());
        Assert.Equal("DISCARDED:DRAFT→DISCARDED:Ya está en el inventario de apertura", await h.ScalarAsync<string>(
            """
            SELECT s.status || ':' || h.from_state || '→' || h.to_state || ':' || h.reason
            FROM mfg.shift_summary s JOIN core.state_history h ON h.aggregate_id = s.summary_id AND h.to_state = 'DISCARDED'
            WHERE s.run_id = @r
            """,
            ("r", run)));
        Assert.Equal(journals, await h.ScalarAsync<long>("SELECT count(*) FROM fin.gl_journal"));

        // The machine, shift, day and item are free again.
        var again = await ProductionRunTests.Start(h, s, "start-again");
        Assert.NotEqual(run, again.ResultRef);
    }

    [Fact]
    public async Task A_run_whose_summary_was_posted_is_not_cancelled()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ProductionRunTests.SetupAsync(h);
        var run = (await ProductionRunTests.Start(h, s, "start")).ResultRef;
        var summary = JsonDocument.Parse((await ProductionRunTests.Record(h, s, run, "draft")).ResultPayload).RootElement;
        await h.RunAsync(new PostShiftSummary(h.CompanyId, s.Manager, "post", s.Plant, run, summary.GetProperty("version").GetInt64()), new PostShiftSummaryHandler());

        var version = await RunVersionAsync(h, run);
        var refused = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new CancelProductionRun(h.CompanyId, s.Supervisor, "c1", s.Plant, run, version, "Error"), new CancelProductionRunHandler()));

        // Posting completes the run; a posted summary is reversed, never cancelled.
        Assert.Equal(ManufacturingErrors.InvalidState, refused.Code);
        Assert.Equal("POSTED", await h.ScalarAsync<string>("SELECT status FROM mfg.shift_summary WHERE run_id = @r", ("r", run)));
    }
}
