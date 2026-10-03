using System.Text.Json;
using Rochell.Finance.Ledger;
using Rochell.Identity;
using Rochell.Platform.Commands;
using Rochell.Procurement.Expenses;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>
/// GAS1-03 (E-GAS-03-1…10): expense categories prepared and approved by two people, one by one or in batch; and the configuration
/// load of a company's chart of accounts and of its categories, through the application's commands as the service identity.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ExpenseCategoryTests(PostgresFixture postgres)
{
    private sealed record World(Guid Contador, Guid Controller, Guid Repairs, Guid Payable);

    private static async Task<World> WorldAsync(TestHarness h)
    {
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var repairs = (await h.RunAsync(new CreateAccount(h.CompanyId, controller, "acc-1", "63700", "Reparaciones", "EXPENSE", false), new CreateAccountHandler())).ResultRef;
        var payable = (await h.RunAsync(new CreateAccount(h.CompanyId, controller, "acc-2", "20100", "Cuentas por pagar proveedores", "LIABILITY", true), new CreateAccountHandler())).ResultRef;
        return new World(await h.SessionWithRolesAsync("CONTADOR"), controller, repairs, payable);
    }

    private static Task<string?> CategoriesAsync(TestHarness h)
        => h.ScalarAsync<string>("SELECT string_agg(code || ':' || status || ':' || version, ',' ORDER BY code, status) FROM pur.expense_category");

    [Fact]
    public async Task The_Contador_prepares_and_corrects_and_the_Controller_approves_takes_out_of_use_and_reactivates()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);

        var id = (await h.RunAsync(new PrepareExpenseCategory(h.CompanyId, w.Contador, "p", " reparaciones ", " Reparaciones ", w.Repairs, "02", "SERVICE"), new PrepareExpenseCategoryHandler())).ResultRef;
        await h.RunAsync(new UpdateExpenseCategoryDraft(h.CompanyId, w.Contador, "u", id, 1, "Reparaciones de planta", "02", "SERVICE"), new UpdateExpenseCategoryDraftHandler());
        var approved = JsonDocument.Parse((await h.RunAsync(new ApproveExpenseCategories(h.CompanyId, w.Controller, "a", [id]), new ApproveExpenseCategoriesHandler())).ResultPayload).RootElement;
        var frozen = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new UpdateExpenseCategoryDraft(h.CompanyId, w.Contador, "u2", id, 3, "Otra", "02", "SERVICE"), new UpdateExpenseCategoryDraftHandler()));
        await h.RunAsync(new DeactivateExpenseCategory(h.CompanyId, w.Contador, "d", id, 3), new DeactivateExpenseCategoryHandler());
        var replacement = (await h.RunAsync(new PrepareExpenseCategory(h.CompanyId, w.Contador, "p2", "REPARACIONES", "Reparaciones", w.Repairs, "02", "SERVICE"), new PrepareExpenseCategoryHandler())).ResultRef;
        await h.RunAsync(new ApproveExpenseCategories(h.CompanyId, w.Controller, "a2", [replacement]), new ApproveExpenseCategoriesHandler());
        var taken = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ReactivateExpenseCategory(h.CompanyId, w.Controller, "r", id, 4), new ReactivateExpenseCategoryHandler()));
        var list = JsonDocument.Parse(await h.QueryAsync(new ListExpenseCategories(h.CompanyId, await h.SessionWithRolesAsync("CUENTAS_POR_PAGAR"), "ACTIVE"), new ListExpenseCategoriesHandler()))
            .RootElement.GetProperty("items");

        Assert.Equal("1|0", $"{approved.GetProperty("approved").GetInt32()}|{approved.GetProperty("skipped").GetInt32()}");
        Assert.Equal((ProcurementErrors.InvalidState, ExpenseErrors.CategoryCodeUsed), (frozen.Code, taken.Code));
        Assert.Equal("REPARACIONES:ACTIVE:2,REPARACIONES:INACTIVE:4", await CategoriesAsync(h));
        Assert.Equal("REPARACIONES|Reparaciones|63700|02|SERVICE", string.Join('|', new[] { "code", "name", "accountCode", "goodsType606", "lineClass" }.Select(n => list[0].GetProperty(n).GetString())));
        Assert.Equal(
            "ExpenseCategory:null>DRAFT,DRAFT>ACTIVE,ACTIVE>INACTIVE",
            "ExpenseCategory:" + await h.ScalarAsync<string>($"SELECT string_agg(coalesce(from_state, 'null') || '>' || to_state, ',' ORDER BY state_history_id) FROM core.state_history WHERE aggregate_id = '{id}'"));
    }

    [Fact]
    public async Task A_batch_approves_what_it_can_and_reports_the_rest()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var byContador = (await h.RunAsync(new PrepareExpenseCategory(h.CompanyId, w.Contador, "p1", "REPARACIONES", "Reparaciones", w.Repairs, "02", "SERVICE"), new PrepareExpenseCategoryHandler())).ResultRef;
        var byController = (await h.RunAsync(new PrepareExpenseCategory(h.CompanyId, w.Controller, "p2", "MANT_OFICINA", "Mantenimiento de oficina", w.Repairs, "02", "SERVICE"), new PrepareExpenseCategoryHandler())).ResultRef;
        var controlAccount = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareExpenseCategory(h.CompanyId, w.Contador, "p3", "PROVEEDORES", "Proveedores", w.Payable, "02", "SERVICE"), new PrepareExpenseCategoryHandler()));
        var badType = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareExpenseCategory(h.CompanyId, w.Contador, "p4", "OTRA", "Otra", w.Repairs, "12", "SERVICE"), new PrepareExpenseCategoryHandler()));
        var notAllowed = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveExpenseCategories(h.CompanyId, w.Contador, "a0", [byContador]), new ApproveExpenseCategoriesHandler()));

        var batch = JsonDocument.Parse((await h.RunAsync(new ApproveExpenseCategories(h.CompanyId, w.Controller, "a", [byContador, byController, Guid.NewGuid()]), new ApproveExpenseCategoriesHandler()))
            .ResultPayload).RootElement;

        Assert.Equal((ExpenseErrors.AccountNotExpense, ExpenseErrors.CategoryInvalid, AuthorizationErrors.NotAuthorized), (controlAccount.Code, badType.Code, notAllowed.Code));
        Assert.Equal("3|1|2", $"{batch.GetProperty("requested").GetInt32()}|{batch.GetProperty("approved").GetInt32()}|{batch.GetProperty("skipped").GetInt32()}");
        Assert.Equal(
            $"{ProcurementErrors.ApproverIsCreator},{ExpenseErrors.CategoryNotFound}",
            string.Join(',', batch.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("outcome").GetString() == "SKIPPED").Select(i => i.GetProperty("code").GetString()).Order(StringComparer.Ordinal)));
        Assert.Equal("MANT_OFICINA:DRAFT:1,REPARACIONES:ACTIVE:2", await CategoriesAsync(h));
    }

    [Fact]
    public async Task The_chart_and_the_categories_of_Block_Rochell_load_once_through_the_commands_and_the_categories_wait_for_the_Controller()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.GrantAsync(h.CompanyId, IdentityConstants.ConfigurationLoadUserId, "CARGA_CONFIGURACION");
        var session = await h.Sessions.StartServiceSessionAsync(IdentityConstants.ConfigurationLoadUserId);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root!.FullName, "Rochell.slnx")))
        {
            root = root.Parent;
        }

        // An account the company already had, under the same code with another name: left as it is.
        await h.RunAsync(new CreateAccount(h.CompanyId, controller, "pre", "63500", "Combustibles y lubricantes", "EXPENSE", false), new CreateAccountHandler());
        var chart = ChartPack.Parse(File.ReadAllText(Path.Combine(root.FullName, "deploy", "chart", "block-rochell-2026-10.json")));
        var categories = ExpenseCategoryPack.Parse(File.ReadAllText(Path.Combine(root.FullName, "deploy", "expenses", "categories-block-rochell-2026-10.json")));
        var accounts = await new ChartPackLoader(h.Pipeline, h.App).LoadAsync(h.CompanyId, session, chart);
        var again = await new ChartPackLoader(h.Pipeline, h.App).LoadAsync(h.CompanyId, session, chart);
        var prepared = await new ExpenseCategoryPackLoader(h.Pipeline, h.App).LoadAsync(h.CompanyId, session, categories);
        var preparedAgain = await new ExpenseCategoryPackLoader(h.Pipeline, h.App).LoadAsync(h.CompanyId, session, categories);
        var ids = await h.ScalarAsync<Guid[]>("SELECT array_agg(expense_category_id) FROM pur.expense_category") ?? [];
        var batch = JsonDocument.Parse((await h.RunAsync(new ApproveExpenseCategories(h.CompanyId, controller, "all", ids), new ApproveExpenseCategoriesHandler())).ResultPayload).RootElement;

        // E-GAS-03-8/9/10: 139 accounts (35 left out of the 174 rows: 13 without code, 2 in USD, 20 group headers), 10 of them control.
        string Count(IReadOnlyList<LoadStep> steps) => string.Join(",", steps.GroupBy(s => s.Outcome).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}:{g.Count()}"));
        Assert.Equal("CREATED:138,DIFFERENT:1", Count(accounts));
        Assert.Equal("DIFFERENT:1,EXISTS:138", Count(again));
        Assert.Equal(
            "10100,10300,10302,10400,12100,13300,13400,13500,20100,20300",
            await h.ScalarAsync<string>("SELECT string_agg(code, ',' ORDER BY code) FROM fin.account WHERE is_control"));
        Assert.Equal("ASSET:36,COST:2,EQUITY:5,EXPENSE:64,LIABILITY:25,REVENUE:7", await h.ScalarAsync<string>(
            "SELECT string_agg(account_class || ':' || n, ',' ORDER BY account_class) FROM (SELECT account_class, count(*) n FROM fin.account GROUP BY account_class) x"));
        Assert.Equal("DRAFT:37", Count(prepared));
        Assert.Equal("EXISTS:37", Count(preparedAgain));
        Assert.Equal("37|37", $"{batch.GetProperty("requested").GetInt32()}|{batch.GetProperty("approved").GetInt32()}");
        Assert.Equal("Combustibles y lubricantes", await h.ScalarAsync<string>("SELECT a.name FROM pur.expense_category c JOIN fin.account a USING (account_id) WHERE c.code = 'COMBUSTIBLE'"));
        Assert.Equal(37L, await h.ScalarAsync<long>("SELECT count(*) FROM pur.expense_category WHERE status = 'ACTIVE' AND prepared_by = @u", ("u", IdentityConstants.ConfigurationLoadUserId)));
    }

    [Fact]
    public async Task A_category_whose_account_is_not_in_the_chart_is_skipped_and_reported()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.GrantAsync(h.CompanyId, IdentityConstants.ConfigurationLoadUserId, "CARGA_CONFIGURACION");
        var session = await h.Sessions.StartServiceSessionAsync(IdentityConstants.ConfigurationLoadUserId);
        var pack = ExpenseCategoryPack.Parse("""{"pack":"t","categories":[{"code":"PEAJES","name":"Peajes","account":"63550","goodsType606":"02","lineClass":"SERVICE"}]}""");

        var steps = await new ExpenseCategoryPackLoader(h.Pipeline, h.App).LoadAsync(h.CompanyId, session, pack);

        Assert.Equal("PEAJES:ACCOUNT_MISSING", string.Join(',', steps.Select(s => $"{s.Subject}:{s.Outcome}")));
        Assert.Equal(0L, await h.CountAsync("pur.expense_category"));
        Assert.Throws<FormatException>(() => ChartPack.Parse("""{"pack":"t","accounts":[{"code":"1","name":"A","accountClass":"ASSET","isControl":false},{"code":"1","name":"B","accountClass":"ASSET","isControl":false}]}"""));
    }
}
