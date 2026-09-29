using System.Text;
using System.Text.Json;
using Rochell.Platform.Time;
using Rochell.Sales.Customers;
using Rochell.Sales.Fleet;
using Rochell.Sales.Opening;
using Rochell.Sales.Pricing;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Rochell.Treasury.BankAccounts;

namespace Rochell.DevStack;

/// <summary>
/// VS3-10b (E-VS3-10-10): VS#3 development data on the seeded plant — the sales accounts and maps (classed later with the rest), the
/// sales posting rules approved, CREDIT (with the AR aging buckets) and REVENUE_ACCOUNTING policies, SALES_ITBIS 18 % active,
/// BLOQUE-6 with a 32.75 standard cost, 2,000 units opened in the plant's first stock location, a price of 50.00, the ACTIVE customer
/// Constructora Uno (30 days, 1,000,000.00), our truck L123456 and its driver, a second company bank account TEST_BANK 5555554321
/// on GL 1102 for customer receipts (so the treasury journey's account stays apart), and one user per VS#3 role.
/// </summary>
internal static class SalesSeed
{
    private const string SalesItbis = """{"tax_code":"ITBIS","rate":"0.18","effect":"OUTPUT","exempt_item_categories":[]}""";

    public static async Task RunAsync(TestHarness h, Guid plantId, Guid controller, Guid approver)
    {
        foreach (var (role, code, name, control) in new[]
        {
            ("FINISHED_GOODS", "1350", "Producto terminado", true), ("FINISHED_GOODS_IN_TRANSIT", "1351", "Producto terminado en tránsito", true),
            ("MIGRATION_CLEARING", "3990", "Contrapartida de migración", false), ("COGS", "5100", "Costo de ventas", false),
            ("CONTRACT_ASSET", "1240", "Activo de contrato", true), ("UNBILLED_RECEIVABLE", "1245", "Cuentas por cobrar no facturadas", true),
            ("REVENUE_PRODUCT", "4105", "Ventas de producto terminado", false), ("TRANSIT_LOSS", "6900", "Pérdidas en tránsito", false),
            ("AR_CONTROL", "1210", "Cuentas por cobrar clientes", true), ("ITBIS_PAYABLE", "2150", "ITBIS por pagar", false),
            ("SALES_DISCOUNTS", "4190", "Descuentos en ventas", false), ("UNAPPLIED_RECEIPTS", "2120", "Cobros no aplicados", true),
            ("CASH_IN_TRANSIT", "1105", "Efectivo y cheques en tránsito", true), ("WITHHOLDING_RECEIVABLE", "1260", "Retenciones de clientes por cobrar", false),
        })
        {
            await h.CreateActiveMapAsync(role, await h.CreateAccountAsync(code, name, control));
        }

        await h.CreateAccountAsync("1102", "Banco de cobros", isControl: true);
        await h.AdminRequireAsync(
            $"""
            UPDATE fin.posting_rule_version v SET status = 'ACTIVE', approved_by = '{h.UserId}'
            FROM fin.posting_rule r WHERE r.posting_rule_id = v.posting_rule_id AND v.status = 'DRAFT'
              AND r.code IN ('OPEN-INV', 'P-15', 'P-15R', 'P-16', 'P-30', 'P-18', 'P-22', 'P-23', 'P-24', 'P-25', 'P-27', 'P-29');
            """);
        await h.CreateActivePolicyAsync("CREDIT", new Dictionary<string, string>
        {
            ["overdue_days_block"] = "30",
            ["ar_aging_bucket_1_days"] = "30",
            ["ar_aging_bucket_2_days"] = "60",
            ["ar_aging_bucket_3_days"] = "90",
        });
        await h.CreateActivePolicyAsync("REVENUE_ACCOUNTING", new Dictionary<string, string>
        {
            ["unbilled_delivery_presentation"] = "CONTRACT_ASSET",
            ["unbilled_aging_alert_days"] = "30",
            ["delivery_open_alert_hours"] = "24",
        });

        // SALES_ITBIS through the gate by the fiscal users the purchase setup already created (no duplicate sign-in names).
        var analyst = await h.ScalarAsync<Guid>("SELECT ra.user_id FROM iam.role_assignment ra JOIN iam.role r ON r.role_id = ra.role_id WHERE r.code = 'ANALISTA_FISCAL' LIMIT 1");
        var specialist = await h.ScalarAsync<Guid>("SELECT ra.user_id FROM iam.role_assignment ra JOIN iam.role r ON r.role_id = ra.role_id WHERE r.code = 'ESPECIALISTA_FISCAL' LIMIT 1");
        var actors = new FiscalActors(await h.CreateSessionAsync(analyst), await h.CreateSessionAsync(specialist));
        await h.ActivateRuleAsync(actors, "itbis-ventas", "ITBIS_VENTAS", FiscalRuleKinds.SalesItbis, SalesItbis, new DateOnly(2026, 1, 1));

        // The product, its cost and price, and the opening stock in the plant's first stock location.
        var block = Guid.CreateVersion7();
        await h.AdminRequireAsync($"INSERT INTO md.item VALUES ('{block}', '{h.CompanyId}', 'BLOQUE-6', 'Bloque de 6 pulgadas', 'FINISHED_GOOD', 'un', 'BLOQUE', 'ACTIVE', 1)");
        var area = await h.ScalarAsync<Guid>("SELECT valuation_area_id FROM md.plant WHERE plant_id = @p", ("p", plantId));
        var plantCode = await h.ScalarAsync<string>("SELECT code FROM md.plant WHERE plant_id = @p", ("p", plantId));
        var location = await h.ScalarAsync<string>("SELECT code FROM md.location WHERE plant_id = @p AND NOT is_transit ORDER BY code LIMIT 1", ("p", plantId));
        var cost = JsonDocument.Parse((await h.RunAsync(new PrepareStandardCost(h.CompanyId, controller, "dev-cost", block, area, 32.75m), new PrepareStandardCostHandler())).ResultPayload)
            .RootElement.GetProperty("costVersionId").GetGuid();
        await h.RunAsync(new ApproveStandardCost(h.CompanyId, approver, "dev-cost-a", cost), new ApproveStandardCostHandler());
        var today = BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);
        var csv = Convert.ToBase64String(Encoding.UTF8.GetBytes($"planta,ubicacion,producto,cantidad,documento\n{plantCode},{location},BLOQUE-6,2000,ADM-1\n"));
        var batch = (await h.RunAsync(new PrepareOpeningInventory(h.CompanyId, controller, "dev-open", "apertura.csv", csv, new DateOnly(today.Year, today.Month, 1)), new PrepareOpeningInventoryHandler())).ResultRef;
        await h.RunAsync(new PostOpeningInventory(h.CompanyId, approver, "dev-open-post", batch, 1), new PostOpeningInventoryHandler());
        var prices = (await h.RunAsync(new PreparePriceList(h.CompanyId, controller, "dev-prices", [new(block, "un", 50.00m)]), new PreparePriceListHandler())).ResultRef;
        await h.RunAsync(new ApprovePriceList(h.CompanyId, approver, "dev-prices-a", prices), new ApprovePriceListHandler());

        // The people of VS#3, the customer, the fleet and the receipts' bank account.
        var seller = await h.SessionWithRolesAsync("VENDEDOR");
        var credit = await h.SessionWithRolesAsync("CREDITO");
        var dispatch = await h.SessionWithRolesAsync("DESPACHO");
        await h.SessionWithRolesAsync("FACTURACION");
        await h.SessionWithRolesAsync("COBROS");
        var customer = (await h.RunAsync(new CreateCustomer(h.CompanyId, seller, "dev-customer", "131925332", "Constructora Uno"), new CreateCustomerHandler())).ResultRef;
        var terms = JsonDocument.Parse((await h.RunAsync(new PrepareCustomerTerms(h.CompanyId, credit, "dev-terms", customer, 30, 1000000.00m, false), new PrepareCustomerTermsHandler())).ResultPayload)
            .RootElement.GetProperty("termsVersionId").GetGuid();
        await h.RunAsync(new ApproveCustomerTerms(h.CompanyId, controller, "dev-terms-a", terms), new ApproveCustomerTermsHandler());
        await h.RunAsync(new ActivateCustomer(h.CompanyId, credit, "dev-customer-a", customer, 1), new ActivateCustomerHandler());
        await h.RunAsync(new RegisterVehicle(h.CompanyId, dispatch, "dev-truck", "L123456", 12000m), new RegisterVehicleHandler());
        await h.RunAsync(new RegisterDriver(h.CompanyId, dispatch, "dev-driver", "Juan Pérez", "00112345678"), new RegisterDriverHandler());
        await h.RunAsync(new RegisterBankAccount(h.CompanyId, controller, "dev-sales-bank", "TEST_BANK", "5555554321", "1102"), new RegisterBankAccountHandler());
    }
}
