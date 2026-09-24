using Npgsql;
using Rochell.Finance.Posting;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Finance.Tests;

/// <summary>Posting Engine: AT-04, P-1 (all or nothing), PD-01 (late entry), REV-01 (exact reversal), rounding.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PostingEngineTests(PostgresFixture postgres)
{
    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static Task<CommandResult> Post(TestHarness h, TestLedger l, string key, decimal amount, DateOnly? date = null, decimal? credit = null, decimal control = 0)
        => h.RunAsync(new TestPostingCommand(h.CompanyId, h.SessionId, key, l.PlantId, amount, date ?? Today(h), credit, control), new TestPostingHandler());

    private static Guid JournalOf(CommandResult result)
        => System.Text.Json.JsonDocument.Parse(result.ResultPayload).RootElement.GetProperty("journalId").GetGuid();

    [Fact]
    public async Task AT04_posting_writes_a_balanced_journal_lines_and_balances()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var ledger = await h.CreateLedgerAsync();

        var journal = JournalOf(await Post(h, ledger, "at04", 1250.50m, control: 100m));

        Assert.Equal("T-DR:6100:1250.5000:0.0000,T-CR:4100:0.0000:1350.5000,T-CTL:2100:100.0000:0.0000", await h.ScalarAsync<string>(
            """
            SELECT string_agg(e.rule_line_code || ':' || a.code || ':' || e.debit || ':' || e.credit, ',' ORDER BY e.line_no)
            FROM fin.gl_entry e JOIN fin.account a USING (account_id) WHERE e.journal_id = @j
            """,
            ("j", journal)));
        Assert.Equal(0m, await h.ScalarAsync<decimal>("SELECT sum(debit) - sum(credit) FROM fin.gl_entry"));
        Assert.Equal(0m, await h.ScalarAsync<decimal>(
            "SELECT (SELECT sum(debit - credit) FROM fin.gl_entry) - (SELECT sum(debit - credit) FROM fin.gl_period_balance)"));
        Assert.True(await h.ScalarAsync<bool>(
            "SELECT late_entry = false AND posting_date = @d AND journal_type = 'AUTO' FROM fin.gl_journal WHERE journal_id = @j",
            ("d", Today(h)),
            ("j", journal)));
    }

    [Fact]
    public async Task Row_hashes_recomputed_from_stored_rows_match()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var ledger = await h.CreateLedgerAsync();
        await Post(h, ledger, "hash", 10.25m, control: 3m);

        await using var command = h.Admin.CreateCommand(
            """
            SELECT gl_entry_id, journal_id, line_no, company_id, posting_date, account_id, account_role, debit, credit, currency,
                   plant_id, item_id, party_id, subledger_type, subledger_ref, inv_value_entry_id, source_event_id, rule_line_code,
                   determination_inputs::text, row_hash
            FROM fin.gl_entry
            """);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = 0;
        while (await reader.ReadAsync())
        {
            Assert.Equal(reader.GetFieldValue<byte[]>(19), PostingEngine.ReadEntry(reader).ComputeRowHash());
            rows++;
        }

        Assert.Equal(3, rows);
    }

    [Fact]
    public async Task Amounts_are_rounded_half_up_to_two_decimals()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var ledger = await h.CreateLedgerAsync();

        var journal = JournalOf(await Post(h, ledger, "round", 10.005m));

        Assert.Equal(10.01m, await h.ScalarAsync<decimal>("SELECT debit FROM fin.gl_entry WHERE journal_id = @j AND rule_line_code = 'T-DR'", ("j", journal)));
    }

    [Fact]
    public async Task Unbalanced_posting_is_rejected_and_nothing_is_written()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var ledger = await h.CreateLedgerAsync();
        await h.CreateActivePolicyAsync("POSTING", PolicySetup.Posting);

        var ex = await Assert.ThrowsAsync<DomainException>(() => Post(h, ledger, "unbalanced", 100m, credit: 90m));

        Assert.Equal(FinanceErrors.PostingUnbalanced, ex.Code);
        Assert.Equal((0L, 0L, 0L), await h.CountsAsync());
        Assert.Equal(0L, await h.CountAsync("fin.gl_journal"));
    }

    public static TheoryData<string> MissingPrerequisites() => new() { "mapping", "rule", "period" };

    [Theory]
    [MemberData(nameof(MissingPrerequisites))]
    public async Task P1_missing_prerequisite_rolls_back_the_whole_command(string missing)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var ledger = await h.CreateLedgerAsync(mapIncome: missing != "mapping", activateRule: missing != "rule");
        var date = missing == "period" ? Today(h).AddYears(5) : Today(h);

        var ex = await Assert.ThrowsAsync<DomainException>(() => Post(h, ledger, "p1-" + missing, 100m, date));

        Assert.Equal(FinanceErrors.PostingPrerequisiteMissing, ex.Code);
        Assert.Equal((0L, 0L, 0L), await h.CountsAsync());
        Assert.Equal(0L, await h.CountAsync("fin.gl_journal"));
        Assert.Equal(0L, await h.CountAsync("fin.gl_entry"));
    }

    [Fact]
    public async Task PD01_closed_component_moves_the_posting_to_the_next_open_period_as_late_entry()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var ledger = await h.CreateLedgerAsync();
        var lastMonth = Today(h).AddMonths(-1);
        await h.SetComponentAsync(lastMonth, "INV-MOV", "CLOSED");

        var journal = JournalOf(await Post(h, ledger, "late", 50m, lastMonth));
        var firstOfThisMonth = new DateOnly(Today(h).Year, Today(h).Month, 1);

        Assert.True(await h.ScalarAsync<bool>("SELECT late_entry AND posting_date = @d FROM fin.gl_journal WHERE journal_id = @j", ("d", firstOfThisMonth), ("j", journal)));
        Assert.Equal(lastMonth.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), await h.ScalarAsync<string>(
            "SELECT e.business_date::text FROM fin.gl_journal j JOIN core.domain_event e ON e.event_id = j.source_event_id WHERE j.journal_id = @j", ("j", journal)));
    }

    [Fact]
    public async Task Posting_is_rejected_when_the_component_is_closed_in_every_later_period()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var ledger = await h.CreateLedgerAsync();
        await h.AdminRequireAsync($"UPDATE fin.close_component_state SET status = 'CLOSED', version = version + 1, closed_by = '{h.UserId}', closed_at = now(), snapshot_hash = sha256('fixture') WHERE company_id = '{h.CompanyId}' AND component = 'INV-MOV'");

        var ex = await Assert.ThrowsAsync<DomainException>(() => Post(h, ledger, "all-closed", 50m));

        Assert.Equal(FinanceErrors.PostingPrerequisiteMissing, ex.Code);
    }

    [Fact]
    public async Task Other_component_being_closed_does_not_affect_the_rule()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var ledger = await h.CreateLedgerAsync();
        await h.SetComponentAsync(Today(h), "AP-REC", "CLOSED");

        var journal = JournalOf(await Post(h, ledger, "ap-closed", 50m));

        Assert.False(await h.ScalarAsync<bool>("SELECT late_entry FROM fin.gl_journal WHERE journal_id = @j", ("j", journal)));
    }

    [Fact]
    public async Task REV01_reversal_is_the_exact_inverse_and_happens_once()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var ledger = await h.CreateLedgerAsync();
        var original = JournalOf(await Post(h, ledger, "orig", 777.77m, control: 22.23m));

        var reversal = JournalOf(await h.RunAsync(new TestReversalCommand(h.CompanyId, h.SessionId, "rev-1", original, Today(h)), new TestReversalHandler()));
        var twice = await Assert.ThrowsAsync<DomainException>(() =>
            h.RunAsync(new TestReversalCommand(h.CompanyId, h.SessionId, "rev-2", original, Today(h)), new TestReversalHandler()));

        Assert.Equal(FinanceErrors.AlreadyReversed, twice.Code);
        Assert.Equal(0L, await h.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM (SELECT * FROM fin.gl_entry WHERE journal_id = @o) o
            FULL JOIN (SELECT * FROM fin.gl_entry WHERE journal_id = @r) r ON r.line_no = o.line_no
            WHERE o.gl_entry_id IS NULL OR r.gl_entry_id IS NULL
               OR NOT (o.account_id = r.account_id AND o.account_role = r.account_role AND o.debit = r.credit AND o.credit = r.debit
                       AND o.plant_id IS NOT DISTINCT FROM r.plant_id AND o.subledger_ref IS NOT DISTINCT FROM r.subledger_ref
                       AND o.rule_line_code = r.rule_line_code)
            """,
            ("o", original),
            ("r", reversal)));
        Assert.Equal(0m, await h.ScalarAsync<decimal>("SELECT sum(debit - credit) FROM fin.gl_period_balance WHERE account_id = @a", ("a", ledger.ExpenseAccount)));
        Assert.Equal("REVERSAL", await h.ScalarAsync<string>("SELECT journal_type FROM fin.gl_journal WHERE reverses_journal_id = @o", ("o", original)));
    }

    [Fact]
    public async Task Category_specific_mapping_wins_over_the_generic_one()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var ledger = await h.CreateLedgerAsync();
        var special = await h.CreateAccountAsync("6199", "Gasto especial", isControl: false);
        await h.CreateActiveMapAsync("TEST_EXPENSE", special, category: "CEMENTO");

        var journal = JournalOf(await Post(h, ledger, "generic", 10m));

        // TEST_EXPENSE line carries no item, so the generic mapping (6100) applies.
        Assert.Equal("6100", await h.ScalarAsync<string>(
            "SELECT a.code FROM fin.gl_entry e JOIN fin.account a USING (account_id) WHERE e.journal_id = @j AND e.rule_line_code = 'T-DR'", ("j", journal)));
    }
}
