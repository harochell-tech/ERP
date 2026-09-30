using System.Text.Json;
using Rochell.Finance.Ledger;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Reconciliation.Tests;

/// <summary>ADM-2 (E-ADM-2-4/5/7): a superadministrator prepares and approves alone; everyone else keeps four eyes; CONTROLS-WAIVED warns.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SuperadminWaiverTests(PostgresFixture postgres)
{
    private const string Support = "5f70bf18a086007016e948b04aed3b82103a36bea41755b6cddfaf10ace3c6ef";

    [Fact]
    public async Task A_superadmin_prepares_and_approves_an_adjustment_alone_and_CONTROLS_WAIVED_lists_it()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var today = BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);
        await h.OpenPeriodsAsync(today.Year);
        var super = await h.SessionWithRolesAsync("SUPERADMIN");
        var contador = await h.SessionWithRolesAsync("CONTADOR");
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var expense = await h.CreateAccountAsync("6200", "Energía", isControl: false);
        var accrued = await h.CreateAccountAsync("2200", "Gastos acumulados por pagar", isControl: false);
        ManualJournalLine[] lines = [new(expense, 800.00m, 0m), new(accrued, 0m, 800.00m)];

        async Task<Guid> Submitted(Guid session, string key)
        {
            var id = (await h.RunAsync(
                new PrepareManualJournal(h.CompanyId, session, key, today, "Provisión", "Factura EDE", Support, "ACR-NTX", false, lines),
                new PrepareManualJournalHandler())).ResultRef;
            await h.RunAsync(new SubmitManualJournal(h.CompanyId, session, key + "-s", id, 1), new SubmitManualJournalHandler());
            return id;
        }

        var bySuper = await Submitted(super, "aj-super");
        var approve = await h.RunAsync(new ApproveManualJournal(h.CompanyId, super, "approve-super", bySuper, 2), new ApproveManualJournalHandler());
        var byContador = await Submitted(contador, "aj-contador");
        var refused = await h.AdminExecuteAsync($"UPDATE fin.manual_journal SET approved_by = prepared_by WHERE manual_journal_id = '{byContador}'"); // the database's last guard
        var run = JsonDocument.Parse((await h.RunAsync(new RunReconciliation(h.CompanyId, controller, "recon", ["CONTROLS-WAIVED"]), new RunReconciliationHandler())).ResultPayload);

        Assert.Equal("POSTED", await h.ScalarAsync<string>("SELECT status FROM fin.manual_journal WHERE manual_journal_id = @j", ("j", bySuper)));
        Assert.Equal(("23514", "manual_journal_four_eyes"), (refused?.SqlState, refused?.ConstraintName));
        Assert.Equal("CONTROLS-WAIVED:EXCEPTIONS", string.Join(',', run.RootElement.GetProperty("runs").EnumerateArray().Select(r => $"{r.GetProperty("code").GetString()}:{r.GetProperty("status").GetString()}")));
        var commandId = await h.ScalarAsync<Guid>("SELECT command_id FROM core.command_log WHERE result_ref = @r", ("r", approve.ResultRef));
        Assert.Equal($"command:Finance.ApproveManualJournal:{commandId}:CONTROL_WAIVED:WARNING", await h.ScalarAsync<string>(
            "SELECT string_agg(match_key || ':' || classification || ':' || severity, ',') FROM rec.recon_exception"));
    }
}
