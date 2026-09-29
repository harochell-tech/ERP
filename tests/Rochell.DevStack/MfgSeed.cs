using Rochell.Platform.Time;
using Rochell.TestInfrastructure;

namespace Rochell.DevStack;

/// <summary>
/// MFG1-07 (E-MFG1-07-7): MFG-1 development data on the seeded plant — the production accounts and maps (WIP 1340, conversion 5150,
/// usage and price variances 5102 / 5103, scrap 5160, revaluation 5190), P-08, P-10, P-12, P-13 and REVAL approved, the PRODUCTION
/// policy (5 % tolerance), the product ADOQUIN-H (no stock, no standard yet: the journey prepares its recipe and standard), the
/// admixture ADITIVO-P, stock of sand, cement and admixture in the plant's first stock location, and one user per production role.
/// </summary>
internal static class MfgSeed
{
    private const string TestReceipt = "0192f000-0000-7000-8000-0000000000f1";

    public static async Task RunAsync(TestHarness h, Guid plantId)
    {
        foreach (var (role, code, name, control) in new[]
        {
            ("WIP", "1340", "Producción en proceso", true), ("CONVERSION_ABSORPTION", "5150", "Absorción de conversión", false),
            ("MATERIAL_USAGE_VARIANCE", "5102", "Variación de uso de materiales", false), ("MATERIAL_PRICE_VARIANCE", "5103", "Variación de precio de materiales", false),
            ("PRODUCTION_SCRAP", "5160", "Scrap de producto terminado", false), ("STANDARD_REVALUATION", "5190", "Revaluación de costo estándar", false),
        })
        {
            if (await h.ScalarAsync<long>("SELECT count(*) FROM fin.account_role_map WHERE company_id = @c AND account_role = @r AND status = 'ACTIVE'", ("c", h.CompanyId), ("r", role)) == 0)
            {
                await h.CreateActiveMapAsync(role, await h.CreateAccountAsync(code, name, control));
            }
        }

        await h.AdminRequireAsync(
            $"""
            UPDATE fin.posting_rule_version v SET status = 'ACTIVE', approved_by = '{h.UserId}'
            FROM fin.posting_rule r WHERE r.posting_rule_id = v.posting_rule_id AND v.status = 'DRAFT'
              AND (r.code IN ('P-08', 'P-10', 'P-12', 'P-13', 'REVAL') OR r.posting_rule_id = '{TestReceipt}');
            """);
        await h.CreateActivePolicyAsync("PRODUCTION", new Dictionary<string, string> { ["usage_tolerance_pct"] = "0.05" });

        await h.AdminRequireAsync($"INSERT INTO md.item VALUES (gen_random_uuid(), '{h.CompanyId}', 'ADOQUIN-H', 'Adoquín holandés', 'FINISHED_GOOD', 'un', 'ADOQUIN', 'ACTIVE', 1)");
        var additive = await h.CreateActiveItemAsync("ADITIVO-P", "l", "ADITIVO");
        var sand = await h.ScalarAsync<Guid>("SELECT item_id FROM md.item WHERE company_id = @c AND code = 'ARENA-LAVADA'", ("c", h.CompanyId));
        var cement = await h.ScalarAsync<Guid>("SELECT item_id FROM md.item WHERE company_id = @c AND code = 'CEMENTO-GRIS'", ("c", h.CompanyId));
        var location = await h.ScalarAsync<Guid>("SELECT location_id FROM md.location WHERE plant_id = @p AND NOT is_transit AND NOT is_curing ORDER BY code LIMIT 1", ("p", plantId));
        // The receipts' counterpart: TEST.RECEIPT credits TEST_INCOME, which the purchasing setup does not map.
        if (await h.ScalarAsync<long>("SELECT count(*) FROM fin.account_role_map WHERE company_id = @c AND account_role = 'TEST_INCOME' AND status = 'ACTIVE'", ("c", h.CompanyId)) == 0)
        {
            await h.CreateActiveMapAsync("TEST_INCOME", await h.CreateAccountAsync("4195", "Ingreso de prueba (existencias iniciales)", isControl: false));
        }

        var yesterday = BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow).AddDays(-1);
        await h.RunAsync(new TestReceiveStock(h.CompanyId, h.SessionId, "dev-mfg-sand", location, sand, 50m, 50000.00m, yesterday), new TestReceiveStockHandler());
        await h.RunAsync(new TestReceiveStock(h.CompanyId, h.SessionId, "dev-mfg-cement", location, cement, 20m, 164000.00m, yesterday), new TestReceiveStockHandler());
        await h.RunAsync(new TestReceiveStock(h.CompanyId, h.SessionId, "dev-mfg-additive", location, additive, 200m, 10000.00m, yesterday), new TestReceiveStockHandler());

        await h.SessionWithRolesAsync("GERENTE_PLANTA");
        await h.SessionWithRolesAsync("SUPERVISOR_PRODUCCION");
        await h.SessionWithRolesAsync("CALIDAD");
    }
}
