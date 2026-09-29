using System.Text.Json;
using Rochell.Manufacturing.Costing;
using Rochell.Manufacturing.Queries;
using Rochell.Manufacturing.Runs;
using Rochell.Platform.Commands;
using Rochell.Reconciliation;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Manufacturing.Tests;

/// <summary>MFG1-05: collector settlement (P-13), production reconciliations and the close order (E-MFG1-05-1…8; MFG-07, MFG-11).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SettlementTests(PostgresFixture postgres)
{
    private const string P13 = "0192f001-0000-7000-8000-000000000027";

    private static async Task<(ProductionRunTests.Setup S, Guid Run, Guid Collector, Guid Usage, Guid Price)> PostedAsync(TestHarness h)
    {
        var s = await ProductionRunTests.SetupAsync(h);
        var usage = await h.CreateAccountAsync("5102", "Variación de uso de materiales", isControl: false);
        var price = await h.CreateAccountAsync("5103", "Variación de precio de materiales", isControl: false);
        await h.CreateActiveMapAsync("MATERIAL_USAGE_VARIANCE", usage);
        await h.CreateActiveMapAsync("MATERIAL_PRICE_VARIANCE", price);
        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '{P13}' AND version = 1");
        var started = JsonDocument.Parse((await ProductionRunTests.Start(h, s, "start")).ResultPayload).RootElement;
        var run = started.GetProperty("runId").GetGuid();
        var summary = JsonDocument.Parse((await ProductionRunTests.Record(h, s, run, "rec")).ResultPayload).RootElement;
        await h.RunAsync(new PostShiftSummary(h.CompanyId, s.Manager, "post", s.Plant, run, summary.GetProperty("version").GetInt64()), new PostShiftSummaryHandler());
        return (s, run, started.GetProperty("collectorId").GetGuid(), usage, price);
    }

    [Trait("AcceptanceMfg1", "MFG-07")]
    [Fact]
    public async Task MFG07_the_settlement_splits_the_WIP_into_usage_and_price_variances_and_leaves_it_at_zero()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var (s, run, collector, usageAccount, priceAccount) = await PostedAsync(h);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");

        var early = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SettleCostCollector(h.CompanyId, controller, "early", s.Plant, collector, 1), new SettleCostCollectorHandler()));
        clock.Advance(TimeSpan.FromDays(40));
        controller = await h.SessionWithRolesAsync("CONTROLLER");
        var manager = await h.SessionWithRolesAsync("GERENTE_PLANTA");
        var supervisorSettles = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SettleCostCollector(h.CompanyId, manager, "mgr", s.Plant, collector, 1), new SettleCostCollectorHandler()));
        var settled = JsonDocument.Parse((await h.RunAsync(new SettleCostCollector(h.CompanyId, controller, "settle", s.Plant, collector, 1), new SettleCostCollectorHandler())).ResultPayload).RootElement;
        var twice = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SettleCostCollector(h.CompanyId, controller, "again", s.Plant, collector, 2), new SettleCostCollectorHandler()));
        var reverse = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ReverseShiftSummary(h.CompanyId, manager, "rev", s.Plant, run, 2, "Tarde"), new ReverseShiftSummaryHandler()));

        Assert.Equal(
            (ManufacturingErrors.MonthNotEnded, AuthorizationErrors.NotAuthorized, ManufacturingErrors.CollectorSettled, ManufacturingErrors.CollectorSettled),
            (early.Code, supervisorSettles.Code, twice.Code, reverse.Code));
        // Usage at standard price: cement (1,850 − 1.2 × 1,480) × 8.00 = 592.00; sand (18.375 − 0.012 × 1,480) × 1,000.00 = 615.00;
        // admixture (15 − 0.01 × 1,480) × 50.00 = 10.00 → 1,217.00. Price = WIP 1,570.30 − 1,217.00 = 353.30.
        Assert.Equal("1570.30:1217.00:353.30", $"{settled.GetProperty("wipBalance").GetString()}:{settled.GetProperty("usageVariance").GetString()}:{settled.GetProperty("priceVariance").GetString()}");
        Assert.Equal("0.00:1217.00:353.30", await h.ScalarAsync<string>(
            $"SELECT {ProductionRunTests.Balance(s.Wip)} || ':' || {ProductionRunTests.Balance(usageAccount)} || ':' || {ProductionRunTests.Balance(priceAccount)}"));
        var monthEnd = new DateOnly(s.Today.Year, s.Today.Month, 1).AddMonths(1).AddDays(-1);
        Assert.Equal(monthEnd, DateOnly.FromDateTime(await h.ScalarAsync<DateTime>("SELECT posting_date::timestamp FROM fin.gl_journal WHERE source_event_id = (SELECT settlement_event_id FROM mfg.cost_collector)")));
        var collectors = JsonDocument.Parse(await h.QueryAsync(new ListCostCollectors(h.CompanyId, controller, s.Plant), new ListCostCollectorsHandler())).RootElement.GetProperty("items");
        Assert.Equal("SETTLED:1:0.0000:1217.0000:353.3000", string.Join('|', collectors.EnumerateArray().Select(c =>
            $"{c.GetProperty("status").GetString()}:{c.GetProperty("runs").GetInt32()}:{c.GetProperty("wipBalance").GetString()}:{c.GetProperty("usageVariance").GetString()}:{c.GetProperty("priceVariance").GetString()}")));
        var run2 = JsonDocument.Parse((await h.RunAsync(new RunReconciliation(h.CompanyId, controller, "recon", ["WIP-GL", "WIP-OPEN", "INV-VALUE-GL", "VALUE-GL-LINK"]), new RunReconciliationHandler())).ResultPayload).RootElement;
        Assert.Equal("INV-VALUE-GL:MATCHED,VALUE-GL-LINK:MATCHED,WIP-GL:MATCHED,WIP-OPEN:MATCHED", string.Join(',', run2.GetProperty("runs").EnumerateArray()
            .Select(r => $"{r.GetProperty("code").GetString()}:{r.GetProperty("status").GetString()}").Order(StringComparer.Ordinal)));
    }

    [Trait("AcceptanceMfg1", "MFG-11")]
    [Fact]
    public async Task MFG11_production_reconciliations_report_open_runs_usage_out_of_tolerance_and_the_close_order()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (s, _, _, _, _) = await PostedAsync(h);
        await h.CreateActivePolicyAsync("PRODUCTION", new Dictionary<string, string> { ["usage_tolerance_pct"] = "0.025" });
        var night = (await h.RunAsync(new Shifts.DefineShift(h.CompanyId, s.Manager, "night", s.Plant, "NOCHE", new TimeOnly(19, 0), new TimeOnly(7, 0)), new Shifts.DefineShiftHandler())).ResultRef;
        await h.RunAsync(new StartProductionRun(h.CompanyId, s.Supervisor, "open-run", s.Plant, s.Machine, night, s.Today, s.Block), new StartProductionRunHandler());
        var controller = await h.SessionWithRolesAsync("CONTROLLER");

        var result = JsonDocument.Parse((await h.RunAsync(
            new RunReconciliation(h.CompanyId, controller, "recon", ["SHIFT-OPEN", "USAGE-TOLERANCE", "CURING-OVERDUE", "WIP-GL", "PRODUCTION-CLOSE-ORDER"]), new RunReconciliationHandler())).ResultPayload).RootElement;

        Assert.Equal("CURING-OVERDUE:MATCHED,PRODUCTION-CLOSE-ORDER:EXCEPTIONS,SHIFT-OPEN:EXCEPTIONS,USAGE-TOLERANCE:EXCEPTIONS,WIP-GL:MATCHED", string.Join(',', result.GetProperty("runs").EnumerateArray()
            .Select(r => $"{r.GetProperty("code").GetString()}:{r.GetProperty("status").GetString()}").Order(StringComparer.Ordinal)));
        // Cement 1,850 vs 1,800 (2.8 %) is beyond 2.5 %; sand 18.375 vs 18 (2.08 %) and admixture 15 vs 15 are within.
        Assert.Equal("USAGE_OUT_OF_TOLERANCE:WARNING:1", await h.ScalarAsync<string>(
            "SELECT classification || ':' || severity || ':' || count(*) FROM rec.recon_exception x JOIN rec.recon_run r ON r.run_id = x.run_id WHERE r.recon_code = 'USAGE-TOLERANCE' GROUP BY classification, severity"));
        // OP-DAY still open blocks COST-SET; COST-SET still open blocks INV-MOV.
        Assert.Equal("COST-SET:OP-DAY|INV-MOV:COST-SET", await h.ScalarAsync<string>(
            "SELECT string_agg(x.component || ':' || split_part(x.match_key, ':', 1), '|' ORDER BY x.component) FROM rec.recon_exception x JOIN rec.recon_run r ON r.run_id = x.run_id WHERE r.recon_code = 'PRODUCTION-CLOSE-ORDER'"));
    }
}
