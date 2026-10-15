using System.Text.Json;
using Rochell.Manufacturing.Machines;
using Rochell.Manufacturing.Quality;
using Rochell.Manufacturing.Runs;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Manufacturing.Tests;

/// <summary>
/// LAB1-04a (E-LAB1-19): a lot of a tested product owes breaks at 1, 3 and 28 days after it entered curing. The 1-day test is due the
/// next day and leaves the list once specimens of 1 day are recorded; on day 4 the 3-day test is late. A product that is not tested owes
/// nothing.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PendingLabTestsTests(PostgresFixture postgres)
{
    private static JsonElement Json(string payload) => JsonDocument.Parse(payload).RootElement;

    private static CompressionSpecimen Load(decimal kg) => new(kg, 19.5m, 19.5m, 39.5m);

    private static string Pending(JsonElement list)
        => $"{list.GetProperty("overdue").GetInt32()}/{list.GetProperty("dueToday").GetInt32()}:"
           + string.Join(',', list.GetProperty("items").EnumerateArray().Select(i => $"{i.GetProperty("age").GetInt32()}{(i.GetProperty("overdue").GetBoolean() ? "!" : string.Empty)}"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_tested_lot_owes_its_breaks_at_1_3_and_28_days_and_an_untested_one_nothing(bool tested)
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var s = await ProductionRunTests.SetupAsync(h);
        var quality = await h.SessionWithRolesAsync("CALIDAD");
        await h.RunAsync(new SetItemSpec(h.CompanyId, quality, "spec", s.Block, "8", 19.5m, 19.5m, 39.5m, null, tested ? 50m : null, null, null, null, tested), new SetItemSpecHandler());
        await h.RunAsync(new SetMachineShortCode(h.CompanyId, quality, "p1", s.Plant, s.Machine, 1, "P1"), new SetMachineShortCodeHandler());
        var run = (await h.RunAsync(new StartProductionRun(h.CompanyId, s.Supervisor, "run", s.Plant, s.Machine, s.Day, s.Today, s.Block), new StartProductionRunHandler())).ResultRef;
        var summary = Json((await ProductionRunTests.Record(h, s, run, "rec")).ResultPayload);
        var lot = Json((await h.RunAsync(new PostShiftSummary(h.CompanyId, s.Manager, "post", s.Plant, run, summary.GetProperty("version").GetInt64()), new PostShiftSummaryHandler())).ResultPayload)
            .GetProperty("lotId").GetGuid();

        async Task<string> At(int day, int ahead)
        {
            clock.Advance(s.Today.AddDays(day).ToDateTime(new TimeOnly(23, 30)) - clock.UtcNow);
            var lab = await h.SessionWithRolesAsync("LABORATORIO");
            return Pending(Json(await h.QueryAsync(new ListPendingLabTests(h.CompanyId, lab, ahead), new ListPendingLabTestsHandler())));
        }

        var nextWeek = await At(0, 7);
        var dayOne = await At(1, 0);
        var lab = await h.SessionWithRolesAsync("LABORATORIO");
        if (tested)
        {
            await h.RunAsync(new RecordCompressionTests(h.CompanyId, lab, "b1", s.Plant, lot, s.Today.AddDays(1), [Load(52000m), Load(52000m), Load(52000m)]), new RecordCompressionTestsHandler());
        }

        var afterBreak = await At(1, 0);
        var dayFour = await At(4, 0);

        Assert.Equal(tested ? ("0/0:1,3", "0/1:1", "0/0:", "1/0:3!") : ("0/0:", "0/0:", "0/0:", "0/0:"), (nextWeek, dayOne, afterBreak, dayFour));
    }
}
