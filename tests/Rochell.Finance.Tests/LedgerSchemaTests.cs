using Npgsql;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Finance.Tests;

/// <summary>
/// FIN1-01 schema guarantees (Frozen Baseline FIN-1 §2, §5; E-FIN1-01-1…7): accounts, the adjustment journal and report structures,
/// written as the application role. The commands arrive in FIN1-02; here a fixture command writes the rows.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class LedgerSchemaTests(PostgresFixture postgres)
{
    private static async Task Run(TestHarness h, string key, string sql, params TestState[] states)
        => await h.RunAsync(new TestTreasurySql(h.CompanyId, h.SessionId, key, sql, states), new TestTreasurySqlHandler());

    private static async Task<string?> Fails(TestHarness h, string key, string sql, params TestState[] states)
    {
        var ex = await Record.ExceptionAsync(() => Run(h, key, sql, states));
        return ex is PostgresException pg ? pg.SqlState : ex?.GetType().Name;
    }

    private static string Draft(TestHarness h, Guid id, string no = "AJ-000001")
        => $"""
           INSERT INTO fin.manual_journal (manual_journal_id, company_id, journal_no, posting_date, description, support_ref, support_sha256,
             close_component, auto_reverse, status, prepared_by, version)
           VALUES ('{id}', '{h.CompanyId}', '{no}', current_date, 'Provisión de energía', 'Factura EDE-2026-09', sha256('soporte'), 'ACR-NTX', false, 'DRAFT', @user, 1)
           """;

    private static string Line(TestHarness h, Guid journal, int no, Guid account, decimal debit, decimal credit, long version = 1)
        => $"""
           INSERT INTO fin.manual_journal_line (company_id, manual_journal_id, line_no, account_id, debit, credit, journal_version)
           VALUES ('{h.CompanyId}', '{journal}', {no}, '{account}', {debit.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {credit.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {version})
           """;

    [Fact]
    public async Task An_adjustment_takes_only_active_non_control_accounts_and_is_sent_balanced()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var expense = await h.CreateAccountAsync("6200", "Energía", isControl: false);
        var accrued = await h.CreateAccountAsync("2200", "Gastos acumulados por pagar", isControl: false);
        var control = await h.CreateAccountAsync("2100", "Cuentas por pagar", isControl: true);
        var journal = Guid.CreateVersion7();
        await Run(h, "draft", Draft(h, journal), new TestState("ManualJournal", journal, null, "DRAFT"));

        var controlLine = await Fails(h, "control", Line(h, journal, 1, control, 0m, 100m));
        var unbalanced = await Fails(h, "unbalanced",
            Line(h, journal, 1, expense, 100m, 0m) + ";" + Line(h, journal, 2, accrued, 0m, 90m) +
            $"; UPDATE fin.manual_journal SET status = 'PENDING_APPROVAL', version = 2 WHERE manual_journal_id = '{journal}'",
            new TestState("ManualJournal", journal, "DRAFT", "PENDING_APPROVAL"));
        await Run(h, "balanced",
            Line(h, journal, 1, expense, 100m, 0m) + ";" + Line(h, journal, 2, accrued, 0m, 100m) +
            $"; UPDATE fin.manual_journal SET status = 'PENDING_APPROVAL', version = 2 WHERE manual_journal_id = '{journal}'",
            new TestState("ManualJournal", journal, "DRAFT", "PENDING_APPROVAL"));
        var afterSubmit = await Fails(h, "late-line", Line(h, journal, 3, expense, 1m, 0m, 2));
        var edit = await Fails(h, "edit", $"UPDATE fin.manual_journal SET description = 'Otra', version = 3 WHERE manual_journal_id = '{journal}'");
        var selfApproval = await Fails(h, "self",
            $"UPDATE fin.manual_journal SET status = 'REJECTED', rejected_by = @user, rejection_reason = 'x', version = 3 WHERE manual_journal_id = '{journal}'",
            new TestState("ManualJournal", journal, "PENDING_APPROVAL", "REJECTED"));

        Assert.Equal(("P0001", "P0001", "P0001", "P0001", "23514"), (controlLine, unbalanced, afterSubmit, edit, selfApproval));
        Assert.Equal("PENDING_APPROVAL:2", await h.ScalarAsync<string>($"SELECT status || ':' || version FROM fin.manual_journal WHERE manual_journal_id = '{journal}'"));
    }

    [Fact]
    public async Task A_posted_adjustment_needs_its_manual_journal_and_manual_lines_stay_in_manual_journals()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var expense = await h.CreateAccountAsync("6200", "Energía", isControl: false);
        var approver = await h.CreateUserAsync();
        var journal = Guid.CreateVersion7();
        await Run(h, "draft", Draft(h, journal) + ";" + Line(h, journal, 1, expense, 100m, 0m) + ";" + Line(h, journal, 2, expense, 0m, 100m) +
            $"; UPDATE fin.manual_journal SET status = 'PENDING_APPROVAL', version = 2 WHERE manual_journal_id = '{journal}'",
            new TestState("ManualJournal", journal, null, "DRAFT"), new TestState("ManualJournal", journal, "DRAFT", "PENDING_APPROVAL"));

        var noJournal = await Fails(h, "post",
            $"UPDATE fin.manual_journal SET status = 'POSTED', approved_by = '{approver}', posting_event_id = @event, version = 3 WHERE manual_journal_id = '{journal}'",
            new TestState("ManualJournal", journal, "PENDING_APPROVAL", "POSTED"));
        var mapped = await h.AdminExecuteAsync( // maps are loaded by the deployment role (E-B03-15-2)
            $"INSERT INTO fin.account_role_map (map_id, company_id, account_role, account_id, effective_from, prepared_by, status) VALUES (gen_random_uuid(), '{h.CompanyId}', 'MANUAL_ADJUSTMENT', '{expense}', current_date, '{approver}', 'DRAFT')");

        Assert.Equal("P0001", noJournal); // K-25 for adjustments
        Assert.Equal("P0001", mapped?.SqlState);
    }

    [Fact]
    public async Task Accounts_keep_code_and_control_flag_and_are_never_deleted_and_the_ACR_components_open()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.OpenPeriodsAsync(2026);
        var account = await h.CreateAccountAsync("6200", "Energía", isControl: false);

        var rename = await h.AppExecuteAsync($"UPDATE fin.account SET name = 'Energía eléctrica', account_class = 'EXPENSE' WHERE account_id = '{account}'");
        var recode = await h.AppExecuteAsync($"UPDATE fin.account SET code = '6201' WHERE account_id = '{account}'");
        var badClass = await h.AppExecuteAsync($"UPDATE fin.account SET account_class = 'OTHER' WHERE account_id = '{account}'");
        var delete = await h.AppExecuteAsync($"DELETE FROM fin.account WHERE account_id = '{account}'");

        Assert.Null(rename);
        Assert.Equal(("42501", "23514", "42501"), (recode?.SqlState, badClass?.SqlState, delete?.SqlState)); // code: no column grant, and the guard
        Assert.Equal("ACR-NTX:OPEN,ACR-TAX:OPEN", await h.ScalarAsync<string>(
            "SELECT string_agg(DISTINCT component || ':' || status, ',' ORDER BY component || ':' || status) FROM fin.close_component_state WHERE component LIKE 'ACR-%'"));
    }

    [Fact]
    public async Task A_report_structure_is_approved_by_another_person_and_holds_each_account_once()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var cash = await h.CreateAccountAsync("1000", "Caja", isControl: false);
        var approver = await h.CreateUserAsync();
        var structure = Guid.CreateVersion7();
        var line = Guid.CreateVersion7();
        await Run(h, "structure",
            $"""
            INSERT INTO fin.report_structure_version VALUES ('{structure}', '{h.CompanyId}', 'BALANCE_SHEET', 1, current_date, 'DRAFT', @user, NULL);
            INSERT INTO fin.report_line VALUES ('{line}', '{h.CompanyId}', '{structure}', 'A1', 'Efectivo', NULL, 1, 1);
            INSERT INTO fin.report_line_account VALUES ('{h.CompanyId}', '{structure}', '{line}', '{cash}');
            """);

        var twice = await Fails(h, "twice", $"INSERT INTO fin.report_line_account VALUES ('{h.CompanyId}', '{structure}', '{line}', '{cash}')");
        var self = await Fails(h, "self", $"UPDATE fin.report_structure_version SET status = 'ACTIVE', approved_by = @user WHERE structure_version_id = '{structure}'");
        await Run(h, "approve", $"UPDATE fin.report_structure_version SET status = 'ACTIVE', approved_by = '{approver}' WHERE structure_version_id = '{structure}'");
        var lateLine = await Fails(h, "late", $"INSERT INTO fin.report_line VALUES (gen_random_uuid(), '{h.CompanyId}', '{structure}', 'A2', 'Bancos', NULL, 1, 2)");

        Assert.Equal(("23505", "23514", "P0001"), (twice, self, lateLine));
    }
}
