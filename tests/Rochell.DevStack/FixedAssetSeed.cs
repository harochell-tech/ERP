using Rochell.Finance.ExchangeRates;
using Rochell.Finance.Ledger;
using Rochell.MasterData.Suppliers;
using Rochell.Platform.Time;
using Rochell.Procurement.Expenses;
using Rochell.Procurement.SupplierInvoices;
using Rochell.TestInfrastructure;

namespace Rochell.DevStack;

/// <summary>
/// AF1-05 (E-AF1-05-9): what the fixed-asset journey needs — the depreciation accounts, the disposal accounts mapped, P-44 / P-45 / P-46
/// approved, and an electric forklift bought two months ago (USD 9,400.00 at 60.00 = 564,000.00) whose card waits for its class and its
/// service. The class itself is prepared and approved in the journey. After <see cref="UsdSeed"/>. A development world, never a migration.
/// </summary>
internal static class FixedAssetSeed
{
    public static async Task RunAsync(TestHarness h, Guid controller, Guid plantId, Guid clerk)
    {
        foreach (var (code, name, accountClass) in new[]
        {
            ("15390", "Depreciación acumulada de montacargas", "ASSET"), ("64100", "Gasto de depreciación", "EXPENSE"),
        })
        {
            await h.RunAsync(new CreateAccount(h.CompanyId, controller, "dev-acc-" + code, code, name, accountClass, false), new CreateAccountHandler());
        }

        foreach (var (role, code, name, accountClass) in new[]
        {
            ("ASSET_SALE_RECEIVABLE", "13800", "Venta de activos por cobrar", "ASSET"), ("ASSET_DISPOSAL_GAIN", "71500", "Ganancia en venta de activos", "REVENUE"),
            ("ASSET_DISPOSAL_LOSS", "81500", "Pérdida en baja de activos", "EXPENSE"),
        })
        {
            var account = (await h.RunAsync(new CreateAccount(h.CompanyId, controller, "dev-acc-" + code, code, name, accountClass, false), new CreateAccountHandler())).ResultRef;
            await h.CreateActiveMapAsync(role, account);
        }

        await h.AdminRequireAsync(
            $"UPDATE fin.posting_rule_version v SET status = 'ACTIVE', approved_by = '{h.UserId}' FROM fin.posting_rule r "
            + "WHERE r.posting_rule_id = v.posting_rule_id AND v.version = 1 AND r.code IN ('P-44', 'P-45', 'P-46')");

        // The forklift two months ago: its day's rate, its seller, its invoice approved over the amount and posted.
        var today = BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);
        var day = new DateOnly(today.Year, today.Month, 1).AddMonths(-2).AddDays(9);
        // The dev sign-in lists one identity per role: the seed acts as the existing Tesorero and Comprador.
        async Task<Guid> ExistingAsync(string role)
            => await h.CreateSessionAsync(await h.ScalarAsync<Guid>(
                "SELECT ra.user_id FROM iam.role_assignment ra JOIN iam.role r ON r.role_id = ra.role_id WHERE r.code = @r LIMIT 1", ("r", role)));
        var treasurer = await ExistingAsync("TESORERO");
        var rate = (await h.RunAsync(new PrepareExchangeRate(h.CompanyId, treasurer, "dev-fa-rate", "USD", day, 60m, "Banco Central"), new PrepareExchangeRateHandler())).ResultRef;
        await h.RunAsync(new ApproveExchangeRate(h.CompanyId, controller, "dev-fa-rate-a", rate, 1), new ApproveExchangeRateHandler());
        var buyer = await ExistingAsync("COMPRADOR");
        var seller = (await h.RunAsync(new CreateForeignSupplier(h.CompanyId, buyer, "dev-fa-sup", "Yale Material Handling", "US"), new CreateForeignSupplierHandler())).ResultRef;
        await h.RunAsync(new ActivateSupplier(h.CompanyId, controller, "dev-fa-sup-a", seller, 1), new ActivateSupplierHandler());
        var category = await h.ScalarAsync<Guid>("SELECT expense_category_id FROM pur.expense_category WHERE code = 'MONTACARGAS'");
        var invoice = (await h.RunAsync(
            new RegisterExpenseInvoice(
                h.CompanyId, clerk, "dev-fa-inv", seller, "YALE-2026-0088", day, day.AddDays(60), plantId,
                [new ExpenseLineInput("Montacargas eléctrico Yale ERP050", category, null, 1m, 9400m)]),
            new RegisterExpenseInvoiceHandler())).ResultRef;
        await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, clerk, "dev-fa-inv-m", invoice, 1), new MatchSupplierInvoiceHandler());
        var version = await h.ScalarAsync<long>("SELECT version FROM pur.supplier_invoice WHERE si_id = @s", ("s", invoice));
        if (await h.ScalarAsync<string>("SELECT document_status FROM pur.supplier_invoice WHERE si_id = @s", ("s", invoice)) == SupplierInvoiceStatus.MatchException)
        {
            await h.RunAsync(new ApproveMatchException(h.CompanyId, controller, "dev-fa-inv-a", invoice, version, "Montacargas importado"), new ApproveMatchExceptionHandler());
            version++;
        }

        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, clerk, "dev-fa-inv-p", invoice, version), new PostSupplierInvoiceHandler());
    }
}
