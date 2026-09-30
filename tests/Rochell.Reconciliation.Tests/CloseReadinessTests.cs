using System.Text.Json;
using Rochell.Audit;
using Rochell.Platform.Commands;
using Rochell.Platform.Queries;
using Rochell.Platform.Time;
using Rochell.Procurement.SupplierInvoices;
using Rochell.Reconciliation.Queries;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Reconciliation.Tests;

/// <summary>UX3-01 (E-UX3-1): close readiness per component, read with the close guards' own rules.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class CloseReadinessTests(PostgresFixture postgres)
{
    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static DateOnly LastMonth(TestHarness h) => new DateOnly(Today(h).Year, Today(h).Month, 1).AddMonths(-1).AddDays(9);

    private static Task<Guid> PeriodAsync(TestHarness h, DateOnly date)
        => h.ScalarAsync<Guid>("SELECT period_id FROM fin.period WHERE company_id = @c AND @d BETWEEN starts_on AND ends_on", ("c", h.CompanyId), ("d", date));

    private static async Task<DateOnly> EndsOnAsync(TestHarness h, Guid period)
        => DateOnly.ParseExact((await h.ScalarAsync<string>("SELECT ends_on::text FROM fin.period WHERE period_id = @p", ("p", period)))!, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<JsonElement> ReadinessAsync(TestHarness h, Guid session, Guid period)
        => JsonDocument.Parse(await h.QueryAsync(new GetCloseReadiness(h.CompanyId, session, period), new GetCloseReadinessHandler())).RootElement;

    private static JsonElement Component(JsonElement readiness, string component)
        => readiness.GetProperty("components").EnumerateArray().Single(c => c.GetProperty("component").GetString() == component);

    /// <summary>Each blocking reconciliation as code:blocking errors (- without a run for the period).</summary>
    private static string Blocking(JsonElement component)
        => string.Join(',', component.GetProperty("reconciliations").EnumerateArray().Select(r =>
            $"{r.GetProperty("reconCode").GetString()}:{(r.GetProperty("blockingErrors").ValueKind == JsonValueKind.Null ? "-" : r.GetProperty("blockingErrors").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture))}"));

    private static Task Tamper(TestHarness h, string sql) => h.AdminRequireAsync($"BEGIN; SET LOCAL session_replication_role = replica; {sql}; COMMIT;");

    private static Task Verify(TestHarness h, Guid controller, string key, DateOnly cutoff, params string[] codes)
        => h.RunAsync(new RunReconciliation(h.CompanyId, controller, key, codes, cutoff), new RunReconciliationHandler());

    [Fact]
    public async Task A_blocking_error_keeps_a_component_not_ready_until_a_clean_run_for_the_period()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateInvoicingSetupAsync();
        await h.EnableInvoicePostingAsync();
        var si = (await h.RunAsync(
            new RegisterSupplierInvoice(h.CompanyId, s.Clerk, "r", s.Purchasing.SupplierId, "B0100000001", Today(h), Today(h).AddDays(30), [new(s.PoLineId, 6m, 1500m)]),
            new RegisterSupplierInvoiceHandler())).ResultRef;
        await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, s.Clerk, "m", si, 1), new MatchSupplierInvoiceHandler());
        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, s.Clerk, "p", si, 2), new PostSupplierInvoiceHandler());
        var controller = s.Purchasing.Controller;
        var period = await PeriodAsync(h, LastMonth(h));
        var endsOn = await EndsOnAsync(h, period);

        // Nothing verified for the period yet: every blocking reconciliation lacks a run; a run as of today does not count.
        await h.RunAsync(new RunReconciliation(h.CompanyId, controller, "today", ["AP-GL", "PAY-APPL", "ACC-EVIDENCE"]), new RunReconciliationHandler());
        var before = await ReadinessAsync(h, controller, period);

        // The AP document loses 100.00 of its 10,620.00 (6 t × 1,500.00 + 18 % ITBIS): AP-GL (AP-REC) and PAY-APPL (no component) report errors.
        await Tamper(h, "UPDATE fin.ap_document SET open_amount = open_amount - 100");
        await Verify(h, controller, "v1", endsOn, "AP-GL", "PAY-APPL", "ACC-EVIDENCE");
        var broken = await ReadinessAsync(h, controller, period);
        await Tamper(h, "UPDATE fin.ap_document SET open_amount = open_amount + 100");
        await Verify(h, controller, "v2", endsOn, "AP-GL", "PAY-APPL", "ACC-EVIDENCE");
        var fixedUp = await ReadinessAsync(h, controller, period);

        Assert.True(before.GetProperty("ended").GetBoolean());
        Assert.True(before.GetProperty("sealed").GetBoolean()); // everything posted is dated this month
        Assert.Equal("ACC-EVIDENCE:-,AP-GL:-,PAY-APPL:-", Blocking(Component(before, "AP-REC")));
        Assert.False(Component(before, "AP-REC").GetProperty("ready").GetBoolean());
        Assert.Equal("ACC-EVIDENCE:0,AP-GL:1,PAY-APPL:1", Blocking(Component(broken, "AP-REC")));
        Assert.False(Component(broken, "AP-REC").GetProperty("ready").GetBoolean());
        Assert.Equal("EXCEPTIONS", Component(broken, "AP-REC").GetProperty("reconciliations").EnumerateArray()
            .Single(r => r.GetProperty("reconCode").GetString() == "AP-GL").GetProperty("runStatus").GetString());
        Assert.Equal("Cuentas por pagar contra contabilidad", Component(broken, "AP-REC").GetProperty("reconciliations").EnumerateArray()
            .Single(r => r.GetProperty("reconCode").GetString() == "AP-GL").GetProperty("name").GetString());
        Assert.Equal("ACC-EVIDENCE:0,AP-GL:0,PAY-APPL:0", Blocking(Component(fixedUp, "AP-REC")));
        Assert.True(Component(fixedUp, "AP-REC").GetProperty("ready").GetBoolean());
        Assert.Equal("OPEN", Component(fixedUp, "AP-REC").GetProperty("status").GetString());

        // BANK-REC also has PAY-APPL (clean now) but BANK-GL and RECEIPT-APPL were never verified for the period.
        Assert.Equal("ACC-EVIDENCE:0,BANK-GL:-,PAY-APPL:0,RECEIPT-APPL:-", Blocking(Component(fixedUp, "BANK-REC")));
        Assert.False(Component(fixedUp, "BANK-REC").GetProperty("ready").GetBoolean());

        // The close agrees: AP-REC of last month closes.
        await h.RunAsync(new CloseComponent(h.CompanyId, controller, "close", period, "AP-REC"), new CloseComponentHandler());
        var closed = await ReadinessAsync(h, controller, period);
        Assert.Equal("CLOSED", Component(closed, "AP-REC").GetProperty("status").GetString());
        Assert.False(Component(closed, "AP-REC").GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task A_period_that_has_not_ended_or_is_not_sealed_is_not_ready()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.CreateStockSetupAsync();
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var current = await PeriodAsync(h, Today(h));
        var last = await PeriodAsync(h, LastMonth(h));

        // A command with only a domain event whose business date falls in last month leaves it unsealed until the sealer runs.
        await h.RunAsync(h.Ping("event-only", occurredAt: LastMonth(h).ToDateTime(new TimeOnly(15, 0), DateTimeKind.Utc)), new PingHandler());
        var unsealed = await ReadinessAsync(h, controller, last);
        await new LedgerSealer(h.Sealer, h.Clock).SealAllAsync(CancellationToken.None);
        await Verify(h, controller, "v", await EndsOnAsync(h, last), "INV-QTY-BALANCE", "INV-VALUE-BALANCE", "INV-VALUE-GL", "VAL-RESIDUAL", "VALUE-GL-LINK", "ACC-EVIDENCE", "PRODUCTION-CLOSE-ORDER");
        var sealedUp = await ReadinessAsync(h, controller, last);
        var running = await ReadinessAsync(h, controller, current);
        var missing = await Assert.ThrowsAsync<DomainException>(() => ReadinessAsync(h, controller, Guid.CreateVersion7()));

        Assert.False(unsealed.GetProperty("sealed").GetBoolean());
        Assert.False(Component(unsealed, "INV-MOV").GetProperty("sealed").GetBoolean());
        Assert.True(sealedUp.GetProperty("sealed").GetBoolean());
        Assert.True(Component(sealedUp, "INV-MOV").GetProperty("ready").GetBoolean());
        Assert.False(running.GetProperty("ended").GetBoolean());
        Assert.All(running.GetProperty("components").EnumerateArray(), c => Assert.False(c.GetProperty("ready").GetBoolean()));
        Assert.Equal(QueryErrors.NotFound, missing.Code);
    }
}
