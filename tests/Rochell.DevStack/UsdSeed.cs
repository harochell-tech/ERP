using Rochell.Finance.Ledger;
using Rochell.Procurement.Expenses;
using Rochell.TestInfrastructure;

namespace Rochell.DevStack;

/// <summary>
/// USD1-07b (E-USD1-07-7): what the USD journey needs — foreign payables, imports to settle and the exchange difference accounts mapped, the
/// rules P-38…P-43R approved, and the fixed-asset category «Montacargas y equipos» (606 type 04) approved. The rate of the day, the foreign
/// supplier and its documents come from the journey itself. A development world, never a migration.
/// </summary>
internal static class UsdSeed
{
    public static async Task RunAsync(TestHarness h, Guid controller)
    {
        // Classed accounts, so the balance sheet and income statement keep covering the whole chart (STRUCT-COVERAGE).
        foreach (var (role, code, name, accountClass, control) in new[]
        {
            ("AP_FOREIGN", "21020", "Proveedores del exterior", "LIABILITY", true), ("IMPORT_CLEARING", "13900", "Importaciones por liquidar", "ASSET", true),
            ("FX_GAIN", "48100", "Ganancia cambiaria", "REVENUE", false), ("FX_LOSS", "68100", "Pérdida cambiaria", "EXPENSE", false),
            ("FX_UNREALIZED", "68200", "Diferencia cambiaria no realizada", "EXPENSE", false),
        })
        {
            var account = (await h.RunAsync(new CreateAccount(h.CompanyId, controller, "dev-acc-" + code, code, name, accountClass, control), new CreateAccountHandler())).ResultRef;
            await h.CreateActiveMapAsync(role, account);
        }

        await h.AdminRequireAsync(
            $"UPDATE fin.posting_rule_version v SET status = 'ACTIVE', approved_by = '{h.UserId}' FROM fin.posting_rule r "
            + "WHERE r.posting_rule_id = v.posting_rule_id AND v.version = 1 AND r.code IN ('P-38', 'P-39', 'P-40', 'P-41', 'P-42', 'P-43', 'P-43R')");

        var contador = await h.ScalarAsync<Guid>("SELECT ra.user_id FROM iam.role_assignment ra JOIN iam.role r ON r.role_id = ra.role_id WHERE r.code = 'CONTADOR' LIMIT 1");
        var asset = (await h.RunAsync(new CreateAccount(h.CompanyId, controller, "dev-acc-15300", "15300", "Montacargas y equipos", "ASSET", false), new CreateAccountHandler())).ResultRef;
        var category = (await h.RunAsync(
            new PrepareExpenseCategory(h.CompanyId, await h.CreateSessionAsync(contador), "dev-cat-montacargas", "MONTACARGAS", "Montacargas y equipos", asset, "04", "GOODS"),
            new PrepareExpenseCategoryHandler())).ResultRef;
        await h.RunAsync(new ApproveExpenseCategories(h.CompanyId, controller, "dev-cat-montacargas-a", [category]), new ApproveExpenseCategoriesHandler());
    }
}
