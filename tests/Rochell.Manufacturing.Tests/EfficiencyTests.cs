using System.Globalization;
using System.Text.Json;
using Rochell.Identity;
using Rochell.Manufacturing.Portal;
using Rochell.Platform.Commands;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Manufacturing.Tests;

/// <summary>
/// MFG3-02 (E-MFG3-2…7, E-MFG3-01-1/4): yesterday's shift 07:00–19:00 of planta2 — 150 cycles of the 6" mould (600 blocks), a
/// maintenance 14:00–15:00, a 40-minute mechanical stoppage, a 10-minute one without reason and one inside the maintenance — and the
/// daily report's 30 broken blocks: the draft takes them as fresh scrap, and the efficiency is computed with an ideal cycle of 120 s.
/// Planned 720 − 60 = 660 min; running 660 − 50 = 610; availability 0.9242; performance 150 × 120 s = 300 min ÷ 610 = 0.4918; quality
/// 570 ÷ 600 = 0.95; OEE 0.4318; lost blocks 50 min × 60 ÷ 120 s × 4 = 100.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class EfficiencyTests(PostgresFixture postgres)
{
    private sealed class FakePortal(PortalExport export) : IPortalSource
    {
        public PortalExport Export { get; set; } = export;

        public Task<PortalExport> FetchAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken) => Task.FromResult(Export);
    }

    [Fact]
    public async Task Broken_blocks_reach_the_draft_and_the_shift_efficiency_follows_the_portal()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ProductionRunTests.SetupAsync(h);
        await h.GrantAsync(h.CompanyId, IdentityConstants.DailyProcessUserId, "PROCESO_DIARIO");
        await h.AdminRequireAsync(
            $"""
            INSERT INTO mfg.portal_machine VALUES ('{h.CompanyId}', 'planta2', '{s.Machine}', NULL, 1);
            INSERT INTO mfg.portal_mould VALUES ('{h.CompanyId}', '6', '{s.Block}', 1);
            INSERT INTO mfg.portal_shift VALUES ('{h.CompanyId}', 1, '{s.Day}', 1);
            INSERT INTO mfg.portal_material VALUES ('{h.CompanyId}', 'dosificadora1', 'CEMENTO', '{s.Cement}', 'kg', '{s.Patio}', 1),
                                                   ('{h.CompanyId}', 'dosificadora1', 'ARENA', '{s.Sand}', 'm3', '{s.Patio}', 1),
                                                   ('{h.CompanyId}', 'dosificadora1', 'ADITIVO', '{s.Additive}', 'l', '{s.Patio}', 1);
            """);
        var day = s.Today.AddDays(-1);
        var d = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var export = new PortalExport(
            "now",
            [new PortalShift("planta2", d, 1, $"{d} 07:00:00", $"{d} 19:00:00", true, 150, 0, [new PortalMould("6", 4, 150, 600)], 0, $"{d} 07:02:00", $"{d} 18:50:00", 0)],
            [],
            [new PortalStoppage(11, "planta2", $"{d} 11:00:00", $"{d} 11:40:00", 2400, "fallo_mecanico", "Cadena"), new PortalStoppage(12, "planta2", $"{d} 16:00:00", $"{d} 16:10:00", null, null, null),
             new PortalStoppage(13, "planta2", $"{d} 14:30:00", $"{d} 14:40:00", 600, "mantenimiento", null)],
            [new PortalMaintenance(5, "planta2", $"{d} 14:00:00", $"{d} 15:00:00", "Cambio de zapatas")],
            [new PortalDailyReport("planta2", d, 30, new Dictionary<string, int> { ["6"] = 560 }, $"{d}T17:10:00")]);
        var portal = new FakePortal(export);
        var service = await h.Sessions.StartServiceSessionAsync(IdentityConstants.DailyProcessUserId);
        await PortalRunner.RunOnceAsync(h.Pipeline, portal, h.CompanyId, service, s.Today, 1, CancellationToken.None);
        await h.RunAsync(new SetIdealCycle(h.CompanyId, s.Manager, "ideal", s.Machine, s.Block, day.AddDays(-7), 120m), new SetIdealCycleHandler());
        var twice = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SetIdealCycle(h.CompanyId, s.Manager, "ideal2", s.Machine, s.Block, day.AddDays(-7), 110m), new SetIdealCycleHandler()));

        // The same report again changes nothing; a new version of the stoppage (its reason written later) is kept apart.
        portal.Export = export with { Stoppages = [.. export.Stoppages!.Take(1), export.Stoppages![1] with { Reason = "falta_material" }, export.Stoppages![2]] };
        await PortalRunner.RunOnceAsync(h.Pipeline, portal, h.CompanyId, service, s.Today, 1, CancellationToken.None);
        var efficiency = JsonDocument.Parse(await h.QueryAsync(new GetMachineEfficiency(h.CompanyId, s.Supervisor, day, day), new GetMachineEfficiencyHandler())).RootElement;
        var shift = efficiency.GetProperty("shifts")[0];

        Assert.Equal(ManufacturingErrors.IdealCycleInvalid, twice.Code);
        Assert.Equal("PORTAL:570:0:30:DRAFT", await h.ScalarAsync<string>(
            """
            SELECT s.source || ':' || s.good_units::numeric(18,0) || ':' || s.mix_scrap_units::numeric(18,0) || ':' || s.fresh_scrap_units::numeric(18,0) || ':' || s.status
            FROM mfg.shift_summary s JOIN mfg.production_run r ON r.run_id = s.run_id WHERE r.machine_id = @m AND s.status = 'DRAFT'
            """,
            ("m", s.Machine)));
        Assert.Equal((4L, 1L, 1L), (await h.ScalarAsync<long>("SELECT count(*) FROM mfg.portal_stoppage"), await h.ScalarAsync<long>("SELECT count(*) FROM mfg.portal_maintenance"),
            await h.ScalarAsync<long>("SELECT count(*) FROM mfg.portal_daily_report")));
        Assert.Equal(("660", "60", "50", "610", 150), (shift.GetProperty("plannedMinutes").GetString(), shift.GetProperty("maintenanceMinutes").GetString(),
            shift.GetProperty("stoppageMinutes").GetString(), shift.GetProperty("runningMinutes").GetString(), shift.GetProperty("cycles").GetInt32()));
        Assert.Equal(("0.9242", "0.4918", "0.95", "0.4318"), (shift.GetProperty("availability").GetString(), shift.GetProperty("performance").GetString(),
            shift.GetProperty("quality").GetString(), shift.GetProperty("oee").GetString()));
        Assert.Equal("100", shift.GetProperty("lostUnits").GetString());
        Assert.Equal(0, shift.GetProperty("stoppagesWithoutReason").GetInt32()); // the later version gave it a reason
        Assert.Equal("fallo_mecanico:1:40,falta_material:1:10", string.Join(',', shift.GetProperty("reasons").EnumerateArray()
            .Select(r => $"{r.GetProperty("reason").GetString()}:{r.GetProperty("count").GetInt32()}:{r.GetProperty("minutes").GetString()}")));
        Assert.Equal(0, efficiency.GetProperty("missingIdealCycles").GetArrayLength());
        Assert.Equal("0.4318", efficiency.GetProperty("machines")[0].GetProperty("oee").GetString());
    }
}
