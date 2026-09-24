using Rochell.Platform.Data;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Finance.Tests;

/// <summary>Database guarantees of the ledger: append-only, same-transaction lines, control accounts, TMP-01, RLS, privileges.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class FinanceSchemaTests(PostgresFixture postgres)
{
    private const string InsufficientPrivilege = "42501";

    private static async Task<(TestHarness H, TestLedger L, Guid Journal)> PostedAsync(PostgresFixture postgres)
    {
        var h = await TestHarness.CreateAsync(postgres);
        var ledger = await h.CreateLedgerAsync();
        var result = await h.RunAsync(
            new TestPostingCommand(h.CompanyId, h.SessionId, "seed", ledger.PlantId, 100m, BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow)),
            new TestPostingHandler());
        var journal = System.Text.Json.JsonDocument.Parse(result.ResultPayload).RootElement.GetProperty("journalId").GetGuid();
        return (h, ledger, journal);
    }

    [Fact]
    public async Task Account_roles_of_the_slice_are_seeded()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        Assert.Equal(
            "AP_CONTROL:t,GRNI:f,INVENTORY_ADJUSTMENT:f,ITBIS_RECOVERABLE:f,MATERIAL_USAGE_VARIANCE:f,PURCHASE_PRICE_VARIANCE:f,RAW_MATERIAL:t,ROUNDING_DIFFERENCE:f,WITHHOLDING_PAYABLE:f",
            await h.ScalarAsync<string>("SELECT string_agg(role_code || ':' || CASE WHEN is_control THEN 't' ELSE 'f' END, ',' ORDER BY role_code) FROM fin.account_role WHERE role_code NOT LIKE 'TEST%'"));
    }

    [Theory]
    [InlineData("UPDATE fin.gl_entry SET debit = debit")]
    [InlineData("DELETE FROM fin.gl_entry")]
    [InlineData("UPDATE fin.gl_journal SET late_entry = true")]
    [InlineData("DELETE FROM fin.gl_journal")]
    [InlineData("TRUNCATE fin.gl_entry")]
    public async Task Journals_and_entries_are_append_only_even_for_the_owner(string sql)
    {
        var (h, _, _) = await PostedAsync(postgres);
        await using (h)
        {
            Assert.Equal(SqlStates.RaiseException, (await h.AdminExecuteAsync(sql))?.SqlState);
        }
    }

    [Theory]
    [InlineData("UPDATE fin.gl_entry SET debit = debit")]
    [InlineData("DELETE FROM fin.gl_journal")]
    [InlineData("INSERT INTO fin.account (account_id, company_id, code, name, is_control) VALUES (gen_random_uuid(), '{0}', '9999', 'x', false)")]
    [InlineData("UPDATE fin.account_role_map SET account_id = account_id")]
    [InlineData("INSERT INTO fin.period (period_id, company_id, starts_on, ends_on) VALUES (gen_random_uuid(), '{0}', '2040-01-01', '2040-01-31')")]
    public async Task Application_role_cannot_bypass_the_posting_engine(string sqlTemplate)
    {
        var (h, _, _) = await PostedAsync(postgres);
        await using (h)
        {
            var ex = await h.AppExecuteAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, sqlTemplate, h.CompanyId));

            Assert.Equal(InsufficientPrivilege, ex?.SqlState);
        }
    }

    [Fact]
    public async Task A_component_cannot_be_closed_without_CloseComponent()
    {
        // PR-16: the application may write the state columns (CloseComponent does), but the guard only accepts a close that
        // carries its closer and snapshot hash, with the version advanced.
        var (h, _, _) = await PostedAsync(postgres);
        await using (h)
        {
            var ex = await h.AppExecuteAsync($"UPDATE fin.close_component_state SET status = 'CLOSED' WHERE company_id = '{h.CompanyId}'", h.CompanyId);

            Assert.Contains("is not allowed", ex?.MessageText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Lines_cannot_be_added_to_a_committed_journal()
    {
        var (h, l, journal) = await PostedAsync(postgres);
        await using (h)
        {
            var ex = await h.AdminExecuteAsync(
                $$"""
                INSERT INTO fin.gl_entry (gl_entry_id, journal_id, line_no, company_id, posting_date, account_id, account_role, debit, credit, source_event_id, rule_line_code, determination_inputs, row_hash)
                SELECT gen_random_uuid(), journal_id, 99, company_id, posting_date, '{{l.IncomeAccount}}', 'TEST_INCOME', 1, 0, source_event_id, 'x', '{}', sha256('x')
                FROM fin.gl_journal WHERE journal_id = '{{journal}}'
                """);

            Assert.Contains("only be added in the transaction", ex!.MessageText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Unbalanced_journal_written_directly_by_the_application_fails_at_commit()
    {
        var (h, l, journal) = await PostedAsync(postgres);
        await using (h)
        {
            var (connection, tx) = await h.OpenAppTransactionAsync();
            await using (connection)
            await using (tx)
            {
                var newJournal = Guid.CreateVersion7();
                await using var insert = new Npgsql.NpgsqlCommand(
                    $$"""
                    INSERT INTO fin.gl_journal (journal_id, company_id, posting_date, period_id, source_event_id, posting_rule_id, posting_rule_version, posting_generation, journal_type, occurred_at, row_hash)
                    SELECT '{{newJournal}}', company_id, posting_date, period_id, source_event_id, posting_rule_id, posting_rule_version, 2, 'AUTO', occurred_at, row_hash
                    FROM fin.gl_journal WHERE journal_id = '{{journal}}';
                    INSERT INTO fin.gl_entry (gl_entry_id, journal_id, line_no, company_id, posting_date, account_id, account_role, debit, credit, source_event_id, rule_line_code, determination_inputs, row_hash)
                    SELECT gen_random_uuid(), '{{newJournal}}', 1, company_id, posting_date, '{{l.ExpenseAccount}}', 'TEST_EXPENSE', 50, 0, source_event_id, 'x', '{}', sha256('x')
                    FROM fin.gl_journal WHERE journal_id = '{{journal}}';
                    """,
                    connection,
                    tx);
                await insert.ExecuteNonQueryAsync();

                var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => tx.CommitAsync());

                Assert.Contains("unbalanced or incomplete", ex.MessageText, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Control_accounts_require_their_subledger_and_matching_role()
    {
        var (h, l, _) = await PostedAsync(postgres);
        await using (h)
        {
            var wrongRole = await h.AdminExecuteAsync(
                $"INSERT INTO fin.account_role_map (map_id, company_id, account_role, account_id, effective_from, prepared_by, status) VALUES (gen_random_uuid(), '{h.CompanyId}', 'GRNI', '{l.ControlAccount}', '2040-01-01', '00000000-0000-7000-8000-00000000d001', 'DRAFT')");

            Assert.Contains("control roles map only to control accounts", wrongRole!.MessageText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TMP01_active_mappings_and_rule_versions_cannot_overlap()
    {
        var (h, l, _) = await PostedAsync(postgres);
        await using (h)
        {
            var map = await h.AdminExecuteAsync(
                $"INSERT INTO fin.account_role_map (map_id, company_id, account_role, account_id, effective_from, prepared_by, approved_by, status) VALUES (gen_random_uuid(), '{h.CompanyId}', 'TEST_EXPENSE', '{l.ExpenseAccount}', '2030-01-01', '00000000-0000-7000-8000-00000000d001', '{h.UserId}', 'ACTIVE')");
            var rule = await h.AdminExecuteAsync(
                """
                INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, effective_from, status, approved_by)
                SELECT posting_rule_id, 3, definition, explanation_templates, close_component, DATE '2030-01-01', 'ACTIVE', approved_by
                FROM fin.posting_rule_version WHERE posting_rule_id = '0192f000-0000-7000-8000-0000000000e1' AND version = 1
                """);

            Assert.Equal("23P01", map?.SqlState);
            Assert.Equal("23P01", rule?.SqlState);
        }
    }

    [Fact]
    public async Task Approved_configuration_is_immutable_except_closing_its_range_once()
    {
        var (h, _, _) = await PostedAsync(postgres);
        await using (h)
        {
            Assert.Equal(SqlStates.RaiseException, (await h.AdminExecuteAsync("UPDATE fin.posting_rule_version SET definition = '{\"lines\": []}' WHERE posting_rule_id = '0192f000-0000-7000-8000-0000000000e1' AND version = 1"))?.SqlState);
            Assert.Equal(SqlStates.RaiseException, (await h.AdminExecuteAsync("UPDATE fin.posting_rule_version SET status = 'DRAFT', approved_by = NULL WHERE posting_rule_id = '0192f000-0000-7000-8000-0000000000e1' AND version = 1"))?.SqlState);
            Assert.Null(await h.AdminExecuteAsync("UPDATE fin.posting_rule_version SET effective_to = DATE '2099-01-01' WHERE posting_rule_id = '0192f000-0000-7000-8000-0000000000e1' AND version = 1"));
            Assert.Equal(SqlStates.RaiseException, (await h.AdminExecuteAsync("UPDATE fin.posting_rule_version SET effective_to = DATE '2098-01-01' WHERE posting_rule_id = '0192f000-0000-7000-8000-0000000000e1' AND version = 1"))?.SqlState);
            Assert.Equal(SqlStates.RaiseException, (await h.AdminExecuteAsync("DELETE FROM fin.account_role_map"))?.SqlState);
        }
    }

    [Fact]
    public async Task Row_level_security_isolates_ledgers()
    {
        var (h, _, _) = await PostedAsync(postgres);
        await using (h)
        {
            var other = await h.CreateCompanyAsync();
            var (connection, tx) = await h.OpenAppTransactionAsync(other);
            await using (connection)
            await using (tx)
            {
                await using var count = new Npgsql.NpgsqlCommand(
                    "SELECT (SELECT count(*) FROM fin.gl_journal) + (SELECT count(*) FROM fin.gl_entry) + (SELECT count(*) FROM fin.account) + (SELECT count(*) FROM fin.period)",
                    connection,
                    tx);
                Assert.Equal(0L, await count.ExecuteScalarAsync());
            }
        }
    }
}
