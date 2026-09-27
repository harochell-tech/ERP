using System.Text.Json;
using Rochell.Finance.Configuration;
using Rochell.Finance.Explain;
using Rochell.Finance.Ledger;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Finance.Tests;

/// <summary>FIN1-02: the editable chart of accounts and the adjustment journal (Frozen Baseline FIN-1 §6: GL-01…04, GL-07).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ManualJournalTests(PostgresFixture postgres)
{
    private const string Support = "5f70bf18a086007016e948b04aed3b82103a36bea41755b6cddfaf10ace3c6ef";

    private sealed record Setup(Guid Contador, Guid Controller, Guid Expense, Guid Accrued, Guid Control);

    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static async Task<Setup> SetupAsync(TestHarness h)
    {
        for (var y = Today(h).Year - 1; y <= Today(h).Year + 1; y++)
        {
            await h.OpenPeriodsAsync(y);
        }

        return new Setup(
            await h.SessionWithRolesAsync("CONTADOR"),
            await h.SessionWithRolesAsync("CONTROLLER"),
            await h.CreateAccountAsync("6200", "Energía", isControl: false),
            await h.CreateAccountAsync("2200", "Gastos acumulados por pagar", isControl: false),
            await h.CreateAccountAsync("2100", "Cuentas por pagar", isControl: true));
    }

    private static ManualJournalLine[] Lines(Setup s, decimal amount = 1250.00m)
        => [new(s.Expense, amount, 0m, Memo: "Energía de septiembre"), new(s.Accrued, 0m, amount)];

    private static async Task<Guid> PrepareAsync(TestHarness h, Setup s, string key, bool autoReverse = false, DateOnly? date = null, ManualJournalLine[]? lines = null)
        => (await h.RunAsync(
            new PrepareManualJournal(h.CompanyId, s.Contador, key, date ?? Today(h), "Provisión de energía", "Factura EDE-2026-09", Support, "ACR-NTX", autoReverse, lines ?? Lines(s)),
            new PrepareManualJournalHandler())).ResultRef;

    private static async Task<Guid> SubmittedAsync(TestHarness h, Setup s, string key, bool autoReverse = false, DateOnly? date = null)
    {
        var id = await PrepareAsync(h, s, key + "-p", autoReverse, date);
        await h.RunAsync(new SubmitManualJournal(h.CompanyId, s.Contador, key + "-s", id, 1), new SubmitManualJournalHandler());
        return id;
    }

    private static Task<CommandResult> ApproveAsync(TestHarness h, Guid session, Guid id, string key, long version = 2)
        => h.RunAsync(new ApproveManualJournal(h.CompanyId, session, key, id, version), new ApproveManualJournalHandler());

    private static Task<string?> BalanceAsync(TestHarness h, Guid account)
        => h.ScalarAsync<string>("SELECT coalesce(sum(debit - credit), 0)::numeric(19,2)::text FROM fin.gl_entry WHERE account_id = @a", ("a", account));

    [Trait("AcceptanceFin1", "GL-01")]
    [Fact]
    public async Task GL01_a_balanced_adjustment_is_prepared_submitted_and_approved_by_another_person()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        var id = await SubmittedAsync(h, s, "aj");

        var approved = await ApproveAsync(h, s.Controller, id, "approve");

        var result = JsonDocument.Parse(approved.ResultPayload).RootElement;
        Assert.Equal("POSTED", result.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("reversalJournalId").ValueKind);
        Assert.Equal("MANUAL_ADJUSTMENT|-|P-34,P-34", await h.ScalarAsync<string>(
            $"""
            SELECT j.journal_type || '|' || coalesce(j.posting_rule_id::text, '-') || '|' || string_agg(e.rule_line_code, ',' ORDER BY e.line_no)
            FROM fin.manual_journal m JOIN fin.gl_journal j ON j.source_event_id = m.posting_event_id JOIN fin.gl_entry e ON e.journal_id = j.journal_id
            WHERE m.manual_journal_id = '{id}' GROUP BY j.journal_type, j.posting_rule_id
            """));
        Assert.Equal(("1250.00", "-1250.00"), (await BalanceAsync(h, s.Expense), await BalanceAsync(h, s.Accrued)));
        Assert.Equal("AJ-000001:POSTED:3", await h.ScalarAsync<string>($"SELECT journal_no || ':' || status || ':' || version FROM fin.manual_journal WHERE manual_journal_id = '{id}'"));
        Assert.Equal("DRAFT,PENDING_APPROVAL,POSTED", await h.ScalarAsync<string>(
            $"SELECT string_agg(to_state, ',' ORDER BY state_history_id) FROM core.state_history WHERE aggregate_id = '{id}'"));

        var detail = JsonDocument.Parse(await h.QueryAsync(new GetManualJournal(h.CompanyId, s.Controller, id), new GetManualJournalHandler())).RootElement;
        Assert.Equal(2, detail.GetProperty("lines").GetArrayLength());
        Assert.Equal(Support, detail.GetProperty("supportSha256").GetString());
        Assert.Equal(("1250.00", "1250.00", "0.00"), (detail.GetProperty("totalDebit").GetString(), detail.GetProperty("totalCredit").GetString(), detail.GetProperty("difference").GetString()));
        var entry = await h.ScalarAsync<Guid>(
            $"SELECT e.gl_entry_id FROM fin.gl_entry e JOIN fin.manual_journal m ON m.posting_event_id = e.source_event_id WHERE m.manual_journal_id = '{id}' AND e.line_no = 1");
        var explained = await h.QueryAsync(new ExplainEntry(h.CompanyId, s.Controller, entry), new ExplainEntryHandler());
        Assert.Contains("AJ-000001", explained, StringComparison.Ordinal);
    }

    [Trait("AcceptanceFin1", "GL-02")]
    [Fact]
    public async Task GL02_an_adjustment_to_a_control_or_inactive_account_is_refused()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        var inactive = await h.CreateAccountAsync("6300", "Gasto viejo", isControl: false);
        await h.RunAsync(new DeactivateAccount(h.CompanyId, s.Controller, "off", inactive), new DeactivateAccountHandler());

        var control = await Assert.ThrowsAsync<DomainException>(() => PrepareAsync(h, s, "control", lines: [new(s.Expense, 10m, 0m), new(s.Control, 0m, 10m)]));
        var off = await Assert.ThrowsAsync<DomainException>(() => PrepareAsync(h, s, "inactive", lines: [new(inactive, 10m, 0m), new(s.Accrued, 0m, 10m)]));
        var unbalanced = await PrepareAsync(h, s, "unbalanced", lines: [new(s.Expense, 10m, 0m), new(s.Accrued, 0m, 9m)]);
        var submit = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SubmitManualJournal(h.CompanyId, s.Contador, "submit", unbalanced, 1), new SubmitManualJournalHandler()));
        var badSupport = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareManualJournal(h.CompanyId, s.Contador, "support", Today(h), "Provisión", "Factura", "abc", "ACR-NTX", false, Lines(s)), new PrepareManualJournalHandler()));

        Assert.Equal(
            (LedgerErrors.AccountNotAllowed, LedgerErrors.AccountNotAllowed, LedgerErrors.Unbalanced, LedgerErrors.SupportInvalid),
            (control.Code, off.Code, submit.Code, badSupport.Code));
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM fin.gl_journal WHERE journal_type = 'MANUAL_ADJUSTMENT'"));
    }

    [Trait("AcceptanceFin1", "GL-03")]
    [Fact]
    public async Task GL03_the_preparer_cannot_approve_or_reject_and_a_rejection_needs_a_reason()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        var id = await SubmittedAsync(h, s, "aj");

        var self = await Assert.ThrowsAsync<DomainException>(() => ApproveAsync(h, s.Contador, id, "self"));
        var selfReject = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RejectManualJournal(h.CompanyId, s.Contador, "self-reject", id, 2, "No"), new RejectManualJournalHandler()));
        var noReason = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RejectManualJournal(h.CompanyId, s.Controller, "no-reason", id, 2, " "), new RejectManualJournalHandler()));
        await h.RunAsync(new RejectManualJournal(h.CompanyId, s.Controller, "reject", id, 2, "Falta la factura"), new RejectManualJournalHandler());

        Assert.Equal((AuthorizationErrors.NotAuthorized, AuthorizationErrors.NotAuthorized, LedgerErrors.ReasonRequired), (self.Code, selfReject.Code, noReason.Code));
        Assert.Equal("REJECTED:Falta la factura", await h.ScalarAsync<string>($"SELECT status || ':' || rejection_reason FROM fin.manual_journal WHERE manual_journal_id = '{id}'"));
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM fin.gl_entry"));
    }

    [Trait("AcceptanceFin1", "GL-04")]
    [Fact]
    public async Task GL04_an_auto_reversing_adjustment_reverses_on_the_first_day_of_the_next_month()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        var id = await SubmittedAsync(h, s, "aj", autoReverse: true);

        var approved = JsonDocument.Parse((await ApproveAsync(h, s.Controller, id, "approve")).ResultPayload).RootElement;

        var firstOfNext = new DateOnly(Today(h).Year, Today(h).Month, 1).AddMonths(1);
        Assert.Equal(firstOfNext.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), approved.GetProperty("reversalDate").GetString());
        Assert.Equal(
            $"{Today(h):yyyy-MM-dd}:1250.00:0.00|{firstOfNext:yyyy-MM-dd}:0.00:1250.00",
            await h.ScalarAsync<string>(
                $"SELECT string_agg(posting_date::text || ':' || debit::numeric(19,2)::text || ':' || credit::numeric(19,2)::text, '|' ORDER BY posting_date) FROM fin.gl_entry WHERE account_id = '{s.Expense}'"));
        Assert.Equal("0.00", await BalanceAsync(h, s.Accrued));
        var reverse = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ReverseManualJournal(h.CompanyId, s.Controller, "reverse", id, 3, "Duplicado"), new ReverseManualJournalHandler()));
        Assert.Equal(LedgerErrors.InvalidState, reverse.Code);
    }

    [Fact]
    public async Task A_posted_adjustment_is_reversed_with_a_reason_and_a_withdrawn_draft_is_corrected()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        var id = await SubmittedAsync(h, s, "aj");
        await h.RunAsync(new WithdrawManualJournal(h.CompanyId, s.Contador, "withdraw", id, 2), new WithdrawManualJournalHandler());
        await h.RunAsync(
            new UpdateManualJournal(h.CompanyId, s.Contador, "update", id, 3, Today(h), "Provisión de energía corregida", "Factura EDE-2026-09", Support, "ACR-NTX", false, Lines(s, 1300.00m)),
            new UpdateManualJournalHandler());
        await h.RunAsync(new SubmitManualJournal(h.CompanyId, s.Contador, "resubmit", id, 4), new SubmitManualJournalHandler());
        await ApproveAsync(h, s.Controller, id, "approve", 5);

        await h.RunAsync(new ReverseManualJournal(h.CompanyId, s.Controller, "reverse", id, 6, "Registrado en el mes equivocado"), new ReverseManualJournalHandler());

        Assert.Equal("0.00", await BalanceAsync(h, s.Expense));
        Assert.Equal("REVERSED:7", await h.ScalarAsync<string>($"SELECT status || ':' || version FROM fin.manual_journal WHERE manual_journal_id = '{id}'"));
        Assert.Equal("1300.00", await h.ScalarAsync<string>($"SELECT max(debit)::numeric(19,2)::text FROM fin.gl_entry WHERE account_id = '{s.Expense}'"));
        var list = JsonDocument.Parse(await h.QueryAsync(new ListManualJournals(h.CompanyId, s.Contador, "REVERSED"), new ListManualJournalsHandler())).RootElement;
        Assert.Equal("1300.00", list.GetProperty("items")[0].GetProperty("total").GetString());
    }

    [Fact]
    public async Task An_adjustment_into_a_closed_component_is_refused()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        var lastMonth = new DateOnly(Today(h).Year, Today(h).Month, 1).AddMonths(-1);
        var id = await SubmittedAsync(h, s, "aj", date: lastMonth);
        await h.SetComponentAsync(lastMonth, "ACR-NTX", "CLOSED");

        var ex = await Assert.ThrowsAsync<DomainException>(() => ApproveAsync(h, s.Controller, id, "approve"));

        Assert.Equal(FinanceErrors.PeriodClosed, ex.Code);
    }

    [Trait("AcceptanceFin1", "GL-07")]
    [Fact]
    public async Task GL07_an_account_with_a_balance_is_not_deactivated_and_accounts_are_created_with_their_class()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        var created = await h.RunAsync(new CreateAccount(h.CompanyId, s.Controller, "create", "6400", "Mantenimiento", "expense", false), new CreateAccountHandler());
        var duplicate = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new CreateAccount(h.CompanyId, s.Controller, "dup", "6400", "Otro", "EXPENSE", false), new CreateAccountHandler()));
        var badClass = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new CreateAccount(h.CompanyId, s.Controller, "bad", "6500", "Otro", "OTHER", false), new CreateAccountHandler()));
        var byContador = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new UpdateAccount(h.CompanyId, s.Contador, "contador", s.Expense, "Energía", "EXPENSE"), new UpdateAccountHandler()));
        await h.RunAsync(new UpdateAccount(h.CompanyId, s.Controller, "class", s.Expense, "Energía eléctrica", "EXPENSE"), new UpdateAccountHandler());
        await ApproveAsync(h, s.Controller, await SubmittedAsync(h, s, "aj"), "approve");

        var withBalance = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new DeactivateAccount(h.CompanyId, s.Controller, "off", s.Expense), new DeactivateAccountHandler()));
        await h.RunAsync(new DeactivateAccount(h.CompanyId, s.Controller, "off-new", created.ResultRef), new DeactivateAccountHandler());
        await h.RunAsync(new ActivateAccount(h.CompanyId, s.Controller, "on-new", created.ResultRef), new ActivateAccountHandler());

        Assert.Equal(
            (LedgerErrors.AccountDuplicate, LedgerErrors.AccountInvalid, AuthorizationErrors.NotAuthorized, LedgerErrors.AccountHasBalance),
            (duplicate.Code, badClass.Code, byContador.Code, withBalance.Code));
        var accounts = JsonDocument.Parse(await h.QueryAsync(new ListAccounts(h.CompanyId, s.Controller), new ListAccountsHandler())).RootElement.GetProperty("items");
        Assert.Equal("6200:Energía eléctrica:EXPENSE:ACTIVE|6400:Mantenimiento:EXPENSE:ACTIVE", string.Join('|', accounts.EnumerateArray()
            .Where(a => a.GetProperty("code").GetString() is "6200" or "6400")
            .Select(a => $"{a.GetProperty("code").GetString()}:{a.GetProperty("name").GetString()}:{a.GetProperty("accountClass").GetString()}:{a.GetProperty("status").GetString()}")));
        Assert.Equal("AccountActivated,AccountCreated,AccountDeactivated", await h.ScalarAsync<string>(
            $"SELECT string_agg(event_type, ',' ORDER BY event_type) FROM core.domain_event WHERE aggregate_id = '{created.ResultRef}'"));
    }
}
