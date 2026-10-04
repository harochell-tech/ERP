using Rochell.Finance.Ledger;
using Rochell.Procurement.Expenses;
using Rochell.Tax;
using Rochell.TestInfrastructure;

namespace Rochell.DevStack;

/// <summary>
/// GAS1-07 (E-GAS-07-8): what the expense journeys need — the tax types ITBIS 18 %, Exento and Telecomunicaciones in force, the
/// three tax expense accounts mapped, P-37 approved, and the expense categories REPARACIONES and TELEFONO approved plus COMBUSTIBLE
/// waiting for the Controller. A development world, never a migration.
/// </summary>
internal static class ExpenseSeed
{
    private const string P37 = "0192f001-0000-7000-8000-000000000029";

    public static async Task RunAsync(TestHarness h, Guid controller)
    {
        // The fiscal users the purchase setup already created (no duplicate sign-in names).
        var analyst = await h.ScalarAsync<Guid>("SELECT ra.user_id FROM iam.role_assignment ra JOIN iam.role r ON r.role_id = ra.role_id WHERE r.code = 'ANALISTA_FISCAL' LIMIT 1");
        var specialist = await h.ScalarAsync<Guid>("SELECT ra.user_id FROM iam.role_assignment ra JOIN iam.role r ON r.role_id = ra.role_id WHERE r.code = 'ESPECIALISTA_FISCAL' LIMIT 1");
        var actors = new FiscalActors(await h.CreateSessionAsync(analyst), await h.CreateSessionAsync(specialist));
        var from = new DateOnly(2026, 1, 1);
        await h.ActivateRuleAsync(actors, "dev-itbis18", "ITBIS_18", FiscalRuleKinds.PurchaseTaxType,
            """{"label":"ITBIS 18 %","components":[{"tax_code":"ITBIS","rate":"0.18","effect":"RECOVERABLE_INPUT"}]}""", from);
        await h.ActivateRuleAsync(actors, "dev-exento", "EXENTO", FiscalRuleKinds.PurchaseTaxType, """{"label":"Exento","components":[]}""", from);
        await h.ActivateRuleAsync(actors, "dev-telecom", "TELECOM", FiscalRuleKinds.PurchaseTaxType,
            """{"label":"Telecomunicaciones (ITBIS 18 % + ISC 10 % + CDT 2 %)","components":[{"tax_code":"ITBIS","rate":"0.18","effect":"RECOVERABLE_INPUT"},{"tax_code":"ISC","rate":"0.10","effect":"SELECTIVE_TAX"},{"tax_code":"CDT","rate":"0.02","effect":"OTHER_TAX"}]}""",
            from);

        foreach (var (role, code, name) in new[]
        {
            ("SELECTIVE_TAX_EXPENSE", "63950", "Impuesto selectivo al consumo"), ("OTHER_TAX_EXPENSE", "63960", "Otros impuestos y tasas"), ("LEGAL_TIP_EXPENSE", "63900", "Propinas"),
        })
        {
            await h.CreateActiveMapAsync(role, (await h.RunAsync(new CreateAccount(h.CompanyId, controller, "dev-acc-" + code, code, name, "EXPENSE", false), new CreateAccountHandler())).ResultRef);
        }

        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '{P37}' AND version = 1");

        var contador = await h.ScalarAsync<Guid>("SELECT ra.user_id FROM iam.role_assignment ra JOIN iam.role r ON r.role_id = ra.role_id WHERE r.code = 'CONTADOR' LIMIT 1");
        var preparer = await h.CreateSessionAsync(contador);
        var approve = new List<Guid>();
        foreach (var (code, account, name, type, lineClass, approved) in new[]
        {
            ("REPARACIONES", "63700", "Reparaciones", "02", "SERVICE", true), ("TELEFONO", "63300", "Teléfono e internet", "02", "SERVICE", true),
            ("COMBUSTIBLE", "63500", "Combustible", "02", "GOODS", false),
        })
        {
            var accountId = (await h.RunAsync(new CreateAccount(h.CompanyId, controller, "dev-acc-" + account, account, name, "EXPENSE", false), new CreateAccountHandler())).ResultRef;
            var id = (await h.RunAsync(new PrepareExpenseCategory(h.CompanyId, preparer, "dev-cat-" + code, code, name, accountId, type, lineClass), new PrepareExpenseCategoryHandler())).ResultRef;
            if (approved)
            {
                approve.Add(id);
            }
        }

        await h.RunAsync(new ApproveExpenseCategories(h.CompanyId, controller, "dev-cats", approve), new ApproveExpenseCategoriesHandler());
    }
}
