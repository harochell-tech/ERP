using Npgsql;

namespace Rochell.TestInfrastructure;

/// <summary>Ledger fixtures for the harness company: accounts, ACTIVE mappings, periods and the test posting rule.</summary>
public sealed record TestLedger(Guid PlantId, Guid ExpenseAccount, Guid IncomeAccount, Guid ControlAccount);

public static class FinanceSetup
{
    public const string TestRule = "TEST.POSTING";

    /// <summary>
    /// Creates a plant, accounts 6100 (expense), 4100 (income), 2100 (control), ACTIVE mappings from 2020-01-01,
    /// periods for last, current and next year (all components OPEN) and activates TEST.POSTING v1.
    /// </summary>
    public static async Task<TestLedger> CreateLedgerAsync(this TestHarness h, bool mapIncome = true, bool activateRule = true)
    {
        ArgumentNullException.ThrowIfNull(h);
        var plant = await h.CreatePlantAsync();
        var expense = await h.CreateAccountAsync("6100", "Gasto de prueba", isControl: false);
        var income = await h.CreateAccountAsync("4100", "Ingreso de prueba", isControl: false);
        var control = await h.CreateAccountAsync("2100", "Control de prueba", isControl: true);
        await h.CreateActiveMapAsync("TEST_EXPENSE", expense);
        await h.CreateActiveMapAsync("TEST_CONTROL", control);
        if (mapIncome)
        {
            await h.CreateActiveMapAsync("TEST_INCOME", income);
        }

        var year = h.Clock.UtcNow.Year;
        for (var y = year - 1; y <= year + 1; y++)
        {
            await h.OpenPeriodsAsync(y);
        }

        if (activateRule)
        {
            await h.AdminRequireAsync(
                $"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '0192f000-0000-7000-8000-0000000000e1' AND version = 1");
        }

        return new TestLedger(plant, expense, income, control);
    }

    public static async Task<Guid> CreateAccountAsync(this TestHarness h, string code, string name, bool isControl)
    {
        ArgumentNullException.ThrowIfNull(h);
        var id = Guid.CreateVersion7();
        await using var command = h.Admin.CreateCommand("INSERT INTO fin.account (account_id, company_id, code, name, is_control) VALUES (@id, @c, @code, @name, @control)");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("c", h.CompanyId);
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("control", isControl);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    /// <summary>ACTIVE mapping prepared by the deployment identity and approved by the harness user.</summary>
    public static Task<Guid> CreateActiveMapAsync(this TestHarness h, string role, Guid account, DateOnly? from = null, string? category = null)
        => h.CreateMapAsync(role, account, from ?? new DateOnly(2020, 1, 1), category, active: true);

    public static async Task<Guid> CreateMapAsync(this TestHarness h, string role, Guid account, DateOnly from, string? category, bool active, Guid? preparedBy = null)
    {
        ArgumentNullException.ThrowIfNull(h);
        var id = Guid.CreateVersion7();
        await using var command = h.Admin.CreateCommand(
            """
            INSERT INTO fin.account_role_map (map_id, company_id, account_role, item_category, account_id, effective_from, prepared_by, approved_by, status)
            VALUES (@id, @c, @role, @category, @account, @from, @prepared, @approved, @status)
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("c", h.CompanyId);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("category", (object?)category ?? DBNull.Value);
        command.Parameters.AddWithValue("account", account);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("prepared", preparedBy ?? Guid.Parse("00000000-0000-7000-8000-00000000d001"));
        command.Parameters.AddWithValue("approved", active ? h.UserId : DBNull.Value);
        command.Parameters.AddWithValue("status", active ? "ACTIVE" : "DRAFT");
        await command.ExecuteNonQueryAsync();
        return id;
    }

    /// <summary>Same SQL as `rochell-migrate open-periods`.</summary>
    public static async Task OpenPeriodsAsync(this TestHarness h, int year)
    {
        ArgumentNullException.ThrowIfNull(h);
        await using var command = h.Admin.CreateCommand(
            """
            WITH months AS (SELECT make_date(@year, m, 1) AS starts_on FROM generate_series(1, 12) AS m),
                 inserted AS (
                   INSERT INTO fin.period (period_id, company_id, starts_on, ends_on)
                   SELECT gen_random_uuid(), @c, m.starts_on, (m.starts_on + interval '1 month' - interval '1 day')::date FROM months m
                   ON CONFLICT (company_id, starts_on) DO NOTHING
                   RETURNING company_id, period_id)
            INSERT INTO fin.close_component_state (company_id, period_id, component, status, version)
            SELECT i.company_id, i.period_id, comp, 'OPEN', 1 FROM inserted i CROSS JOIN (VALUES ('INV-MOV'), ('AP-REC')) AS v (comp)
            """);
        command.Parameters.AddWithValue("year", year);
        command.Parameters.AddWithValue("c", h.CompanyId);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Sets the status of a close component for the month containing <paramref name="date"/> without running CloseComponent.
    /// A CLOSED state carries a fixture closer and snapshot hash, as the state guard requires (PR-16). A fixture state has no
    /// snapshot, reopen request or history, so it is written with triggers off (the E-VS1-8 evidence check would refuse it).
    /// </summary>
    public static Task SetComponentAsync(this TestHarness h, DateOnly date, string component, string status)
    {
        ArgumentNullException.ThrowIfNull(h);
        var closing = status == "CLOSED" ? $", closed_by = '{h.UserId}', closed_at = now(), snapshot_hash = sha256('fixture')" : string.Empty;
        return h.AdminRequireAsync(
            $"""
            BEGIN;
            SET LOCAL session_replication_role = replica;
            UPDATE fin.close_component_state s SET status = '{status}', version = version + 1{closing}
            FROM fin.period p WHERE p.period_id = s.period_id AND p.company_id = '{h.CompanyId}'
              AND DATE '{date:yyyy-MM-dd}' BETWEEN p.starts_on AND p.ends_on AND s.component = '{component}';
            COMMIT;
            """);
    }

    public static async Task AdminRequireAsync(this TestHarness h, string sql)
    {
        ArgumentNullException.ThrowIfNull(h);
        var error = await h.AdminExecuteAsync(sql);
        if (error is not null)
        {
            throw new InvalidOperationException("Fixture SQL failed: " + error.MessageText, error);
        }
    }
}
