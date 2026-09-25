using Rochell.Audit;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Reconciliation.Tests;

/// <summary>Regression tests of the interim ledger review findings #25-#28 (E-VS1-7…10).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class InterimReviewCloseTests(PostgresFixture postgres)
{
    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static DateOnly LastMonth(TestHarness h) => new DateOnly(Today(h).Year, Today(h).Month, 1).AddMonths(-1).AddDays(9);

    private static Task<Guid> PeriodAsync(TestHarness h, DateOnly date)
        => h.ScalarAsync<Guid>("SELECT period_id FROM fin.period WHERE company_id = @c AND @d BETWEEN starts_on AND ends_on", ("c", h.CompanyId), ("d", date));

    private static Task SealAsync(TestHarness h) => new LedgerSealer(h.Sealer, h.Clock).SealAllAsync(CancellationToken.None);

    private static Task<CommandResult> Receive(TestHarness h, TestStock s, string key, DateOnly businessDate)
        => h.RunAsync(new TestReceiveStock(h.CompanyId, h.SessionId, key, s.LocationA, s.ItemId, 1m, 10.00m, businessDate), new TestReceiveStockHandler());

    private static async Task<CommandResult> Close(TestHarness h, Guid controller, DateOnly month, string component, string key)
        => await h.RunAsync(new CloseComponent(h.CompanyId, controller, key, await PeriodAsync(h, month), component), new CloseComponentHandler());

    private static Task<string?> StatusAsync(TestHarness h, Guid period, string component)
        => h.ScalarAsync<string>("SELECT status FROM fin.close_component_state WHERE period_id = @p AND component = @k", ("p", period), ("k", component));

    /// <summary>The real TEST.RECEIPT handler, paused after its writes and before COMMIT (holds the shared period lock).</summary>
    [RequiresPermission("test:ping")]
    private sealed class PausedReceiveHandler(TaskCompletionSource written, Task gate) : ICommandHandler<TestReceiveStock>
    {
        public string CommandType => "Test.PausedReceiveStock";

        public async Task<string> HandleAsync(TestReceiveStock command, CommandContext context, CancellationToken cancellationToken)
        {
            var result = await new TestReceiveStockHandler().HandleAsync(command, context, cancellationToken);
            written.TrySetResult();
            await gate.WaitAsync(cancellationToken);
            return result;
        }
    }

    /// <summary>
    /// B02-1 (L10). CloseComponent runs SERIALIZABLE: its snapshot is taken at the first statement (tenant settings), before it
    /// waits for the exclusive period lock. A posting that holds the shared lock and commits while the close waits is invisible
    /// to the close's gate and reconciliations, so the component closes with an unsealed group in the period and a snapshot
    /// that omits the posting.
    /// </summary>
    [Fact]
    public async Task B02_1_close_must_not_succeed_over_a_posting_that_committed_while_it_waited_for_the_period_lock()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var period = await PeriodAsync(h, LastMonth(h));
        await Receive(h, s, "before", LastMonth(h));
        await SealAsync(h);

        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var posting = h.RunAsync(
            new TestReceiveStock(h.CompanyId, h.SessionId, "during", s.LocationA, s.ItemId, 1m, 10.00m, LastMonth(h)),
            new PausedReceiveHandler(written, gate.Task));
        await written.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var close = Close(h, controller, LastMonth(h), "INV-MOV", "close");
        var waiting = 0L;
        for (var i = 0; i < 200 && waiting == 0; i++)
        {
            await Task.Delay(50);
            waiting = await h.ScalarAsync<long>("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted");
        }

        gate.SetResult();
        await posting;
        var closeError = await Record.ExceptionAsync(() => close);

        var unsealed = await h.ScalarAsync<long>(
            "SELECT count(*) FROM audit.integrity_state WHERE integrity_status <> 'SEALED' AND ledger <> 'DOMAIN_EVENT' AND posting_date BETWEEN @s AND @e",
            ("s", new DateOnly(LastMonth(h).Year, LastMonth(h).Month, 1)),
            ("e", new DateOnly(LastMonth(h).Year, LastMonth(h).Month, 1).AddMonths(1).AddDays(-1)));
        var snapshotValuation = await h.ScalarAsync<string>("SELECT b ->> 'quantity' FROM fin.close_snapshot, jsonb_array_elements(content -> 'balances') b WHERE b ->> 'kind' = 'valuation'");
        var ledgerQuantity = await h.ScalarAsync<decimal>("SELECT sum(quantity) FROM inv.inv_quantity_entry WHERE posting_date BETWEEN @s AND @e",
            ("s", new DateOnly(LastMonth(h).Year, LastMonth(h).Month, 1)),
            ("e", new DateOnly(LastMonth(h).Year, LastMonth(h).Month, 1).AddMonths(1).AddDays(-1)));

        Assert.Equal(1L, waiting);                   // the close did wait for the posting's shared lock
        Assert.True(unsealed > 0);                   // the posting left unsealed groups in the period
        Assert.Equal(2m, ledgerQuantity);            // two receipts are in the period
        // Guarantee (L10): a component never closes while a group of its period is unsealed.
        Assert.True(
            closeError is not null && await StatusAsync(h, period, "INV-MOV") == "OPEN",
            $"Close succeeded: status {await StatusAsync(h, period, "INV-MOV")}, {unsealed} unsealed groups in the period, snapshot valuation quantity {snapshotValuation} vs ledger {ledgerQuantity}.");
    }

    /// <summary>
    /// B02-2 (L10/L15, P-8). The state guard only checks transitions; the application role holds UPDATE on status/version, so it
    /// reopens a CLOSED component with no reopen request and no second approver (and no state_history row).
    /// </summary>
    [Fact]
    public async Task B02_2_application_role_cannot_reopen_a_closed_component_without_an_approved_reopen_request()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var period = await PeriodAsync(h, LastMonth(h));
        await Receive(h, s, "r", LastMonth(h));
        await SealAsync(h);
        await Close(h, controller, LastMonth(h), "INV-MOV", "close");

        var error = await h.AppExecuteAsync($"UPDATE fin.close_component_state SET status = 'REOPENED', version = version + 1 WHERE period_id = '{period}' AND component = 'INV-MOV'");

        Assert.True(error is not null, $"rochell_app reopened the component directly; status now {await StatusAsync(h, period, "INV-MOV")}, reopen requests: {await h.CountAsync("fin.reopen_request")}.");
    }

    /// <summary>
    /// B02-3 (L10). "A closed component accepts nothing" is enforced only in the Posting Engine: fin.gl_journal checks that the
    /// posting date is inside the period, not that the rule's component is open, so the application role can book a balanced
    /// journal into a CLOSED period.
    /// </summary>
    [Fact]
    public async Task B02_3_database_rejects_a_journal_into_a_closed_component()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var period = await PeriodAsync(h, LastMonth(h));
        await Receive(h, s, "r", LastMonth(h));
        await SealAsync(h);
        await Close(h, controller, LastMonth(h), "INV-MOV", "close");
        var journal = await h.ScalarAsync<Guid>("SELECT journal_id FROM fin.gl_journal");
        var injected = Guid.CreateVersion7();

        var error = await h.AppExecuteAsync(
            $"""
            INSERT INTO fin.gl_journal (journal_id, company_id, posting_date, period_id, source_event_id, posting_rule_id, posting_rule_version,
              posting_generation, journal_type, reverses_journal_id, late_entry, occurred_at, row_hash)
            SELECT '{injected}', company_id, posting_date, period_id, source_event_id, posting_rule_id, posting_rule_version, 99, 'AUTO', NULL, false, occurred_at, sha256('x'::bytea)
            FROM fin.gl_journal WHERE journal_id = '{journal}';
            INSERT INTO fin.gl_entry (gl_entry_id, journal_id, line_no, company_id, posting_date, account_id, account_role, debit, credit, currency,
              source_event_id, rule_line_code, determination_inputs, row_hash)
            SELECT gen_random_uuid(), '{injected}', n, company_id, posting_date, account_id, account_role,
                   CASE WHEN n = 1 THEN 5000 ELSE 0 END, CASE WHEN n = 2 THEN 5000 ELSE 0 END, 'DOP', source_event_id, rule_line_code, jsonb_build_object(), sha256('y'::bytea)
            FROM fin.gl_entry, generate_series(1, 2) n WHERE journal_id = '{journal}' AND rule_line_code = 'TR-CR';
            """);

        Assert.Equal("CLOSED", await StatusAsync(h, period, "INV-MOV"));
        Assert.True(error is not null, $"rochell_app booked journal {injected} into the CLOSED period (journals now: {await h.CountAsync("fin.gl_journal")}).");
    }

    /// <summary>
    /// B02-4 (L3/L4). The position checks fire only on ledger inserts. A balance projection changed without a ledger row commits;
    /// 0021 also dropped the area-wide Σ stock = valuation quantity comparison, so a stray stock change in another lot is no longer
    /// caught by the next movement of the area either. Only the reconciliations see it.
    /// </summary>
    [Fact]
    public async Task B02_4_balance_projections_cannot_drift_from_the_ledgers_at_commit()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        await Receive(h, s, "r1", Today(h));
        var lot = await h.ScalarAsync<Guid>("SELECT lot_id FROM inv.lot");

        var valuation = await h.AppExecuteAsync("UPDATE inv.inv_valuation_balance SET value = value + 1000");
        var stock = await h.AppExecuteAsync($"UPDATE inv.inv_stock_balance SET quantity = quantity + 5 WHERE lot_id = '{lot}'");
        await Receive(h, s, "r2", Today(h)); // a normal movement still works

        // E-VS1-10: both direct edits are refused at COMMIT; the projections still equal the ledgers.
        Assert.Equal("P0001", valuation?.SqlState);
        Assert.Equal("P0001", stock?.SqlState);
        Assert.Equal("2.000000|2.000000|20.0000", await h.ScalarAsync<string>(
            "SELECT (SELECT sum(quantity) FROM inv.inv_stock_balance) || '|' || quantity || '|' || value FROM inv.inv_valuation_balance"));
    }
}
