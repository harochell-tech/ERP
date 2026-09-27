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
/// aging buckets 30 / 60 / 90 and a treasurer.
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
        await h.SessionWithRolesAsync("APROBADOR_POLITICAS"); // E-B03-15-4: approves accounting policy versions
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

        return accounts;
    }
}
