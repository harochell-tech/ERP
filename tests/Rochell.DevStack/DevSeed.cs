using Rochell.TestInfrastructure;

namespace Rochell.DevStack;

/// <summary>A user the developer can sign in as, labelled with its roles.</summary>
internal sealed record DevAccount(Guid UserId, string Label);

/// <summary>
/// E-PR18b-9: development data built from the test fixtures (never in db/migrations): one company and plant with two
/// locations, an ACTIVE supplier and items, accounts and maps, open periods, the slice policies, approved posting rules and
/// active fiscal rules (TEST sources), and one user per slice role, plus a storekeeper scoped to the plant.
/// </summary>
internal static class DevSeed
{
    private const string Withholding = """{"tax_code":"RET_ITBIS","rate":"0.30","base":"ITBIS","party_types":["COMPANY"]}""";

    public static async Task<IReadOnlyList<DevAccount>> RunAsync(TestHarness h)
    {
        var receiving = await h.CreateReceivingSetupAsync();
        await h.EnableInvoicePostingAsync(withholdingDefinition: Withholding);
        await h.SessionWithRolesAsync("CUENTAS_POR_PAGAR");
        await h.SessionWithRolesAsync("AUDITOR");
        await h.SessionWithRolesAsync("SEGUNDO_APROBADOR_CIERRE");
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
