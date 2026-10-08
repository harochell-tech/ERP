using System.Globalization;
using System.Text.Json;
using Rochell.Identity;
using Rochell.Identity.Authorization;
using Rochell.Manufacturing.Maintenance;
using Rochell.Manufacturing.Portal;
using Rochell.Platform.Commands;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Manufacturing.Tests;

/// <summary>
/// MFG3-03 (E-MFG3-8…10, E-MFG3-01-3/5): a task every 200 cycles is «Por vencer» after 190 cycles; the portal's maintenance window that
/// names it is the task done and restarts it; one every 10 running hours counts planned time minus stoppages; the plant manager records
/// a task done in Core; the Supervisor cannot define tasks.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class MaintenancePlanTests(PostgresFixture postgres)
{
    private sealed class FakePortal(PortalExport export) : IPortalSource
    {
        public PortalExport Export { get; set; } = export;

        public Task<PortalExport> FetchAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken) => Task.FromResult(Export);
    }

    private static PortalShift Shift(string d, int cycles) =>
        new("planta2", d, 1, $"{d} 07:00:00", $"{d} 19:00:00", true, cycles, 0, [new PortalMould("6", 4, cycles, cycles * 4)], 0, $"{d} 07:02:00", $"{d} 18:50:00", 0);

    private static async Task<JsonElement> TaskAsync(TestHarness h, Guid session, string code)
        => JsonDocument.Parse(await h.QueryAsync(new ListMaintenanceTasks(h.CompanyId, session), new ListMaintenanceTasksHandler())).RootElement.GetProperty("items").EnumerateArray()
            .Single(t => t.GetProperty("code").GetString() == code);

    [Fact]
    public async Task Tasks_come_due_with_the_portals_cycles_and_hours_and_restart_when_done()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ProductionRunTests.SetupAsync(h);
        await h.GrantAsync(h.CompanyId, IdentityConstants.DailyProcessUserId, "PROCESO_DIARIO");
        await h.AdminRequireAsync(
            $"""
            INSERT INTO mfg.portal_machine VALUES ('{h.CompanyId}', 'planta2', '{s.Machine}', NULL, 1);
            INSERT INTO mfg.portal_mould VALUES ('{h.CompanyId}', '6', '{s.Block}', 1);
            INSERT INTO mfg.portal_shift VALUES ('{h.CompanyId}', 1, '{s.Day}', 1);
            """);
        await h.RunAsync(new DefineMaintenanceTask(h.CompanyId, s.Manager, "t1", s.Machine, " zapatas ", "Cambiar zapatas", MaintenanceKinds.Cycles, 200), new DefineMaintenanceTaskHandler());
        await h.RunAsync(new DefineMaintenanceTask(h.CompanyId, s.Manager, "t2", s.Machine, "ENGRASE", "Engrasar la mesa", MaintenanceKinds.RunningHours, 10), new DefineMaintenanceTaskHandler());
        var supervisor = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new DefineMaintenanceTask(h.CompanyId, s.Supervisor, "x", s.Machine, "MOLDE", "Revisar molde", MaintenanceKinds.Days, 7), new DefineMaintenanceTaskHandler()));
        var duplicate = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new DefineMaintenanceTask(h.CompanyId, s.Manager, "t3", s.Machine, "ZAPATAS", "Otra", MaintenanceKinds.Days, 7), new DefineMaintenanceTaskHandler()));

        // Two shifts dated after the tasks were created (tomorrow and the day after), so their cycles count.
        var d1 = s.Today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var d2 = s.Today.AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var portal = new FakePortal(new PortalExport("now", [Shift(d1, 150), Shift(d2, 40)], [], [new PortalStoppage(1, "planta2", $"{d1} 09:00:00", $"{d1} 10:00:00", 3600, "otro", null)],
            [], []));
        var service = await h.Sessions.StartServiceSessionAsync(IdentityConstants.DailyProcessUserId);
        await h.RunAsync(new ImportPortalData(h.CompanyId, service, "i1", s.Today, s.Today.AddDays(2)), new ImportPortalDataHandler(portal));
        var dueSoon = await TaskAsync(h, s.Supervisor, "ZAPATAS");

        // The mechanic closes a maintenance window naming the task in the portal.
        portal.Export = portal.Export with { Maintenance = [new PortalMaintenance(9, "planta2", $"{d2} 12:00:00", $"{d2} 12:30:00", "Cambio de zapatas", "zapatas")] };
        await h.RunAsync(new ImportPortalData(h.CompanyId, service, "i2", s.Today, s.Today.AddDays(2)), new ImportPortalDataHandler(portal));
        await h.RunAsync(new ImportPortalData(h.CompanyId, service, "i3", s.Today, s.Today.AddDays(2)), new ImportPortalDataHandler(portal));
        var restarted = await TaskAsync(h, s.Supervisor, "ZAPATAS");
        var engrase = await TaskAsync(h, s.Supervisor, "ENGRASE");
        await h.RunAsync(new RecordMaintenanceDone(h.CompanyId, s.Manager, "done", engrase.GetProperty("taskId").GetGuid(), h.Clock.UtcNow, "Engrasada"), new RecordMaintenanceDoneHandler());

        Assert.Equal(AuthorizationErrors.NotAuthorized, supervisor.Code);
        Assert.Equal(MaintenanceErrors.TaskInvalid, duplicate.Code);
        Assert.Equal(("190", "0.95", "POR_VENCER"), (dueSoon.GetProperty("since").GetString(), dueSoon.GetProperty("used").GetString(), dueSoon.GetProperty("state").GetString()));
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM mfg.maintenance_done WHERE source = 'PORTAL'")); // once, though read twice
        Assert.Equal("PORTAL", restarted.GetProperty("done")[0].GetProperty("source").GetString());
        Assert.Equal("OK", restarted.GetProperty("state").GetString());
        // Running hours count only what lies before now; these shifts are dated ahead of the clock (the efficiency tests cover the hours).
        Assert.Equal(MaintenanceKinds.RunningHours, engrase.GetProperty("frequencyKind").GetString());
        Assert.Equal(2L, await h.ScalarAsync<long>("SELECT count(*) FROM mfg.maintenance_done"));
    }
}
