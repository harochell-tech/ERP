using Rochell.Audit;
using Rochell.Finance.Ledger;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Reconciliation.Tests;

/// <summary>FIN1-02 (E-FIN1-6, E-FIN1-01-3): the ACR components close after MANUAL-EVIDENCE and TB-BALANCED, and freeze adjustments.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class AdjustmentCloseTests(PostgresFixture postgres)
{
    private const string Support = "5f70bf18a086007016e948b04aed3b82103a36bea41755b6cddfaf10ace3c6ef";

    [Fact]
    public async Task ACR_NTX_closes_with_its_adjustments_reconciled_and_then_refuses_new_ones()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var today = BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);
        var lastMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-1).AddDays(9);
        for (var y = today.Year - 1; y <= today.Year + 1; y++)
        {
            await h.OpenPeriodsAsync(y);
        }

        var contador = await h.SessionWithRolesAsync("CONTADOR");
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var expense = await h.CreateAccountAsync("6200", "Energía", isControl: false);
        var accrued = await h.CreateAccountAsync("2200", "Gastos acumulados por pagar", isControl: false);
        ManualJournalLine[] lines = [new(expense, 800.00m, 0m), new(accrued, 0m, 800.00m)];

        async Task<Guid> Submitted(string key)
        {
            var id = (await h.RunAsync(
                new PrepareManualJournal(h.CompanyId, contador, key, lastMonth, "Provisión", "Factura EDE", Support, "ACR-NTX", true, lines),
                new PrepareManualJournalHandler())).ResultRef;
            await h.RunAsync(new SubmitManualJournal(h.CompanyId, contador, key + "-s", id, 1), new SubmitManualJournalHandler());
            return id;
        }

        await h.RunAsync(new ApproveManualJournal(h.CompanyId, controller, "approve", await Submitted("aj1"), 2), new ApproveManualJournalHandler());
        var late = await Submitted("aj2");
        await new LedgerSealer(h.Sealer, h.Clock).SealAllAsync(CancellationToken.None);
        var period = await h.ScalarAsync<Guid>("SELECT period_id FROM fin.period WHERE company_id = @c AND @d BETWEEN starts_on AND ends_on", ("c", h.CompanyId), ("d", lastMonth));

        var closed = await h.RunAsync(new CloseComponent(h.CompanyId, controller, "close", period, "ACR-NTX"), new CloseComponentHandler());
        var refused = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveManualJournal(h.CompanyId, controller, "approve-late", late, 2), new ApproveManualJournalHandler()));

        Assert.Contains("snapshotHash", closed.ResultPayload, StringComparison.Ordinal);
        Assert.Equal("MANUAL-EVIDENCE:MATCHED,TB-BALANCED:MATCHED", await h.ScalarAsync<string>(
            "SELECT string_agg(recon_code || ':' || status, ',' ORDER BY recon_code) FROM rec.recon_run"));
        Assert.Equal("PERIOD_CLOSED", refused.Code);
        Assert.Equal("CLOSED", await h.ScalarAsync<string>($"SELECT status FROM fin.close_component_state WHERE period_id = '{period}' AND component = 'ACR-NTX'"));
    }
}
