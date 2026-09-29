using Rochell.TestInfrastructure;

namespace Rochell.DevStack;

/// <summary>A user the developer can sign in as, labelled with its roles.</summary>
internal sealed record DevAccount(Guid UserId, string Label);

/// <summary>
/// E-PR18b-9: development data built from the test fixtures (never in db/migrations): one company and plant with two
/// locations, an ACTIVE supplier and items, accounts and maps, open periods, the slice policies, approved posting rules and
/// active fiscal rules (TEST sources), and one user per slice role, plus a storekeeper scoped to the plant.
/// VS2-08: a posted invoice of the supplier (6 t of sand, AP 10,620.00, due in 30 days), the company bank account TEST_BANK
/// 0123456789 on GL 1101, the supplier's account verified 73 h ago, R-09 and R-10 approved, BANK_CHARGES mapped, the TREASURY
/// aging buckets 30 / 60 / 90 and a treasurer. UI-01: the two security roles and a new employee without roles. FIN1-04: every
/// account classed by its first digit (1 asset … 6 expense), capital, accrued expenses and energy accounts, approved balance
/// sheet and income statement structures, and a Contador. VS3-10b: the VS#3 data of <see cref="SalesSeed"/>. MFG1-07: the MFG-1 data of <see cref="MfgSeed"/>.
/// </summary>
internal static class DevSeed
{
    private const string R10 = "0192f001-0000-7000-8000-000000000010";

    public static async Task<IReadOnlyList<DevAccount>> RunAsync(TestHarness h)
    {
        var payments = await h.CreatePaymentSetupAsync(bankCode: "TEST_BANK");
        var receiving = payments.Invoicing.Receiving;
        await h.CreateActiveMapAsync("BANK_CHARGES", await h.CreateAccountAsync("6105", "Cargos bancarios", isControl: false));
        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '{R10}' AND version = 1");
        await h.CreateActivePolicyAsync("TREASURY", new Dictionary<string, string>
        {
            ["ap_aging_bucket_1_days"] = "30",
            ["ap_aging_bucket_2_days"] = "60",
            ["ap_aging_bucket_3_days"] = "90",
        });
        await h.SessionWithRolesAsync("AUDITOR");
        await h.SessionWithRolesAsync("SEGUNDO_APROBADOR_CIERRE");
        var approver = await h.SessionWithRolesAsync("APROBADOR_POLITICAS"); // E-B03-15-4: approves accounting policy versions
        await h.SessionWithRolesAsync("ADMIN_SEGURIDAD"); // UI-01: requests role changes
        await h.SessionWithRolesAsync("SEGUNDO_APROBADOR_SEGURIDAD"); // UI-01: decides them
        await h.SessionWithRolesAsync("CONTADOR"); // FIN1-04: prepares adjustments
        await SalesSeed.RunAsync(h, receiving.Purchasing.PlantId, payments.Controller, approver); // VS3-10b, before the accounts are classed
        await MfgSeed.RunAsync(h, receiving.Purchasing.PlantId); // MFG1-07
        await SeedLedgerAsync(h);
        var newcomer = await h.CreateUserAsync(); // UI-01: a user created with the CLI, no role yet
        var plantStorekeeper = await h.CreateUserAsync();
        await h.GrantAsync(h.CompanyId, plantStorekeeper, "ALMACENISTA", receiving.Purchasing.PlantId);

        var accounts = new List<DevAccount>();
        await using var command = h.Admin.CreateCommand(
            """
            SELECT u.user_id,
                   string_agg(r.name || CASE WHEN ra.plant_id IS NULL THEN '' ELSE ' (planta ' || p.code || ')' END, ' + ' ORDER BY r.name)
            FROM iam.user u
            JOIN iam.role_assignment ra ON ra.user_id = u.user_id
            JOIN iam.role r ON r.role_id = ra.role_id
            LEFT JOIN md.plant p ON p.plant_id = ra.plant_id
            WHERE u.kind = 'HUMAN' AND r.code <> 'TEST_PINGER'
            GROUP BY u.user_id
            ORDER BY 2
            """);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            accounts.Add(new DevAccount(reader.GetGuid(0), reader.GetString(1)));
        }

        accounts.Add(new DevAccount(newcomer, "Empleado nuevo (sin roles)"));
        return accounts;
    }

    /// <summary>FIN1-04: classes and report structures, written directly as the fixture does (prepared by the Controller, approved
    /// by the Aprobador de políticas).</summary>
    private static async Task SeedLedgerAsync(TestHarness h)
    {
        await h.CreateAccountAsync("2200", "Gastos acumulados por pagar", isControl: false);
        await h.CreateAccountAsync("3100", "Capital social", isControl: false);
        await h.CreateAccountAsync("6200", "Energía eléctrica", isControl: false);
        await h.AdminRequireAsync(
            $"""
            UPDATE fin.account SET account_class = CASE left(code, 1) WHEN '1' THEN 'ASSET' WHEN '2' THEN 'LIABILITY' WHEN '3' THEN 'EQUITY'
              WHEN '4' THEN 'REVENUE' WHEN '5' THEN 'COST' ELSE 'EXPENSE' END WHERE company_id = '{h.CompanyId}';
            """);
        const string Controller = "(SELECT ra.user_id FROM iam.role_assignment ra JOIN iam.role r ON r.role_id = ra.role_id WHERE r.code = 'CONTROLLER' LIMIT 1)";
        const string Approver = "(SELECT ra.user_id FROM iam.role_assignment ra JOIN iam.role r ON r.role_id = ra.role_id WHERE r.code = 'APROBADOR_POLITICAS' LIMIT 1)";
        foreach (var (report, lines) in new (string, (string Code, string Caption, int Sign, string Class)[])[]
        {
            ("BALANCE_SHEET", [("A", "Activo", 1, "ASSET"), ("P", "Pasivo", -1, "LIABILITY"), ("K", "Patrimonio", -1, "EQUITY")]),
            ("INCOME_STATEMENT", [("I", "Ingresos", -1, "REVENUE"), ("C", "Costos", 1, "COST"), ("G", "Gastos", 1, "EXPENSE")]),
        })
        {
            var structure = Guid.CreateVersion7();
            var sql = new System.Text.StringBuilder(
                $"INSERT INTO fin.report_structure_version VALUES ('{structure}', '{h.CompanyId}', '{report}', 1, '2026-01-01', 'DRAFT', {Controller}, NULL);\n");
            var order = 0;
            foreach (var (code, caption, sign, accountClass) in lines)
            {
                var line = Guid.CreateVersion7();
                sql.Append($"INSERT INTO fin.report_line VALUES ('{line}', '{h.CompanyId}', '{structure}', '{code}', '{caption}', NULL, {sign}, {++order});\n");
                sql.Append($"INSERT INTO fin.report_line_account SELECT '{h.CompanyId}', '{structure}', '{line}', account_id FROM fin.account WHERE company_id = '{h.CompanyId}' AND account_class = '{accountClass}';\n");
            }

            sql.Append($"UPDATE fin.report_structure_version SET status = 'ACTIVE', approved_by = {Approver} WHERE structure_version_id = '{structure}';");
            await h.AdminRequireAsync(sql.ToString());
        }
    }
}
