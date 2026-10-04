using System.Text.Json;
using Rochell.Finance.Ledger;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Procurement.Expenses;
using Rochell.Procurement.Queries;
using Rochell.Procurement.SupplierInvoices;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>
/// GAS1-04 (E-GAS-04-1…7, GAS-03…09 and GAS-13): the expense invoice without a purchase order — registered with its lines'
/// categories and tax types, matched against the PURCHASING approval amount (25,000.00 in the test policy), approved by the
/// Controller from it, posted with P-37 (each line to its category's account) and reversed while nothing was paid.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ExpenseInvoiceTests(PostgresFixture postgres)
{
    private const string P37 = "0192f001-0000-7000-8000-000000000029";

    private sealed record World(TestInvoicing S, Guid Clerk, Guid Controller, Guid Plant, IReadOnlyDictionary<string, Guid> Types, IReadOnlyDictionary<string, Guid> Categories);

    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static async Task<World> WorldAsync(TestHarness h, string? withholding = null)
    {
        var s = await h.CreateInvoicingSetupAsync();
        await h.EnableInvoicePostingAsync(withholdingDefinition: withholding);
        var actors = await h.FiscalActorsAsync(initEnvironment: false);
        var types = new Dictionary<string, Guid>();
        foreach (var (code, definition) in new[]
        {
            ("ITBIS_18", """{"label":"ITBIS 18 %","components":[{"tax_code":"ITBIS","rate":"0.18","effect":"RECOVERABLE_INPUT"}]}"""),
            ("EXENTO", """{"label":"Exento","components":[]}"""),
            ("TELECOM", """{"label":"Telecomunicaciones","components":[{"tax_code":"ITBIS","rate":"0.18","effect":"RECOVERABLE_INPUT"},{"tax_code":"ISC","rate":"0.10","effect":"SELECTIVE_TAX"},{"tax_code":"CDT","rate":"0.02","effect":"OTHER_TAX"}]}"""),
            ("SEGUROS", """{"label":"Seguros","components":[{"tax_code":"ISC","rate":"0.16","effect":"SELECTIVE_TAX"}]}"""),
            ("CONSUMO_PROPINA", """{"label":"Consumo con propina","components":[{"tax_code":"ITBIS","rate":"0.18","effect":"RECOVERABLE_INPUT"},{"tax_code":"PROPINA","rate":"0.10","effect":"LEGAL_TIP"}]}"""),
        })
        {
            await h.ActivateRuleAsync(actors, code.ToLowerInvariant(), code, FiscalRuleKinds.PurchaseTaxType, definition, new DateOnly(2026, 1, 1));
            types[code] = await h.ScalarAsync<Guid>("SELECT rule_id FROM tax.fiscal_rule WHERE code = @code", ("code", code));
        }

        // The tax accounts of E-GAS-10 and P-37 approved by the Controller (A-01).
        await h.CreateActiveMapAsync("SELECTIVE_TAX_EXPENSE", await h.CreateAccountAsync("63950", "Impuesto selectivo al consumo", isControl: false));
        await h.CreateActiveMapAsync("OTHER_TAX_EXPENSE", await h.CreateAccountAsync("63960", "Otros impuestos y tasas", isControl: false));
        await h.CreateActiveMapAsync("LEGAL_TIP_EXPENSE", await h.CreateAccountAsync("63900", "Propinas", isControl: false));
        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '{P37}' AND version = 1");

        // Expense accounts and their categories, prepared by the Contador and approved by the Controller.
        var controller = s.Purchasing.Controller;
        var contador = await h.SessionWithRolesAsync("CONTADOR");
        var categories = new Dictionary<string, Guid>();
        foreach (var (code, account, name, type, lineClass) in new[]
        {
            ("REPARACIONES", "63700", "Reparaciones", "02", "SERVICE"), ("TELEFONO", "63300", "Teléfono", "02", "SERVICE"), ("SEGUROS", "64000", "Seguros", "11", "SERVICE"),
            ("REPRESENTACION", "62400", "Gastos de representación", "05", "SERVICE"), ("COMBUSTIBLE", "63500", "Combustible", "02", "GOODS"), ("REPUESTOS", "66150", "Repuestos", "02", "GOODS"),
        })
        {
            var accountId = (await h.RunAsync(new CreateAccount(h.CompanyId, controller, "acc-" + code, account, name, "EXPENSE", false), new CreateAccountHandler())).ResultRef;
            categories[code] = (await h.RunAsync(new PrepareExpenseCategory(h.CompanyId, contador, "cat-" + code, code, name, accountId, type, lineClass), new PrepareExpenseCategoryHandler())).ResultRef;
        }

        await h.RunAsync(new ApproveExpenseCategories(h.CompanyId, controller, "cats", [.. categories.Values]), new ApproveExpenseCategoriesHandler());
        return new World(s, s.Clerk, controller, s.Purchasing.PlantId, types, categories);
    }

    private static ExpenseLineInput Line(World w, string description, string category, string type, decimal qty, decimal price) => new(description, w.Categories[category], w.Types[type], qty, price);

    private static async Task<Guid> RegisterAsync(TestHarness h, World w, string key, Guid supplier, string ncf, params ExpenseLineInput[] lines)
        => (await h.RunAsync(new RegisterExpenseInvoice(h.CompanyId, w.Clerk, key, supplier, ncf, Today(h), Today(h).AddDays(30), w.Plant, lines), new RegisterExpenseInvoiceHandler())).ResultRef;

    private static async Task<JsonElement> RunAsync<T>(TestHarness h, T command, ICommandHandler<T> handler)
        where T : ICommand
        => JsonDocument.Parse((await h.RunAsync(command, handler)).ResultPayload).RootElement;

    private static string Role(string role) => $"(SELECT coalesce(sum(debit - credit), 0)::numeric(19,2) FROM fin.gl_entry WHERE account_role = '{role}')";

    /// <summary>ITBIS | selective | other | tip | withholding | AP control | each expense account (code:amount) | AP document original/open.</summary>
    private static Task<string?> BooksAsync(TestHarness h)
        => h.ScalarAsync<string>(
            $"""
            SELECT {Role("ITBIS_RECOVERABLE")} || '|' || {Role("SELECTIVE_TAX_EXPENSE")} || '|' || {Role("OTHER_TAX_EXPENSE")} || '|' || {Role("LEGAL_TIP_EXPENSE")} || '|' ||
                   {Role("WITHHOLDING_PAYABLE")} || '|' || {Role("AP_CONTROL")} || '|' ||
                   coalesce((SELECT string_agg(a.code || ':' || x.amount, ',' ORDER BY a.code)
                             FROM (SELECT account_id, sum(debit - credit)::numeric(19,2) AS amount FROM fin.gl_entry WHERE account_role = 'PURCHASE_EXPENSE' GROUP BY account_id) x
                             JOIN fin.account a ON a.account_id = x.account_id), '-') || '|' ||
                   coalesce((SELECT string_agg(original_amount::numeric(19,2) || '/' || open_amount::numeric(19,2), ',') FROM fin.ap_document), '-')
            """);

    [Trait("AcceptanceGas1", "GAS-03")]
    [Fact]
    public async Task GAS03_a_repair_below_the_approval_amount_is_matched_and_posted_to_its_categorys_account()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var si = await RegisterAsync(h, w, "r", w.S.Purchasing.SupplierId, "B0100000701", Line(w, "Reparación de la mezcladora", "REPARACIONES", "ITBIS_18", 1m, 10000m));

        var match = await RunAsync(h, new MatchSupplierInvoice(h.CompanyId, w.Clerk, "m", si, 1), new MatchSupplierInvoiceHandler());
        var posted = await RunAsync(h, new PostSupplierInvoice(h.CompanyId, w.Clerk, "p", si, 2), new PostSupplierInvoiceHandler());
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetSupplierInvoice(h.CompanyId, w.Clerk, si), new GetSupplierInvoiceHandler())).RootElement;

        // 10,000.00 + 18 % = 11,800.00 < 25,000.00.
        Assert.Equal("MATCHED|11800.00|25000.00", $"{match.GetProperty("status").GetString()}|{match.GetProperty("total").GetString()}|{match.GetProperty("approvalThreshold").GetString()}");
        Assert.Equal("POSTED|11800.00", $"{posted.GetProperty("accountingStatus").GetString()}|{posted.GetProperty("payable").GetString()}");
        Assert.Equal("1800.00|0.00|0.00|0.00|0.00|-11800.00|63700:10000.00|11800.00/11800.00", await BooksAsync(h));
        Assert.Equal(
            "EXPENSE|1800.0000|11800.0000|EXPENSE|Reparación de la mezcladora|Reparaciones|ITBIS_18",
            $"{detail.GetProperty("docClass").GetString()}|{detail.GetProperty("itbisTotal").GetString()}|{detail.GetProperty("grossTotal").GetString()}|" +
            string.Join('|', new[] { "lineKind", "description", "expenseCategoryName", "taxTypeCode" }.Select(n => detail.GetProperty("lines")[0].GetProperty(n).GetString())));
        Assert.Equal(
            "P-37:P37-DR-EXP,P37-DR-ITBIS,P37-CR-AP",
            await h.ScalarAsync<string>(
                """
                SELECT r.code || ':' || string_agg(e.rule_line_code, ',' ORDER BY e.line_no)
                FROM fin.gl_journal j JOIN fin.posting_rule r ON r.posting_rule_id = j.posting_rule_id JOIN fin.gl_entry e ON e.journal_id = j.journal_id GROUP BY r.code
                """));
    }

    [Trait("AcceptanceGas1", "GAS-04")]
    [Trait("AcceptanceGas1", "GAS-05")]
    [Trait("AcceptanceGas1", "GAS-06")]
    [Trait("AcceptanceGas1", "GAS-07")]
    [Trait("AcceptanceGas1", "GAS-08")]
    [Fact]
    public async Task GAS04_08_from_the_approval_amount_the_Controller_approves_and_each_tax_type_goes_to_its_accounts()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var si = await RegisterAsync(
            h, w, "r", w.S.Purchasing.SupplierId, "B0100000702",
            Line(w, "Teléfono de la planta, septiembre", "TELEFONO", "TELECOM", 1m, 5000m),
            Line(w, "Póliza de la flota", "SEGUROS", "SEGUROS", 1m, 20000m),
            Line(w, "Almuerzo con cliente", "REPRESENTACION", "CONSUMO_PROPINA", 1m, 2000m),
            Line(w, "Gasoil de la pala", "COMBUSTIBLE", "EXENTO", 30m, 100m));

        var match = await RunAsync(h, new MatchSupplierInvoice(h.CompanyId, w.Clerk, "m", si, 1), new MatchSupplierInvoiceHandler());
        var byClerk = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveMatchException(h.CompanyId, w.Clerk, "a0", si, 2, "Autorizado"), new ApproveMatchExceptionHandler()));
        var early = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new PostSupplierInvoice(h.CompanyId, w.Clerk, "p0", si, 2), new PostSupplierInvoiceHandler()));
        await h.RunAsync(new ApproveMatchException(h.CompanyId, w.Controller, "a", si, 2, "Gastos del mes revisados"), new ApproveMatchExceptionHandler());
        var posted = await RunAsync(h, new PostSupplierInvoice(h.CompanyId, w.Clerk, "p", si, 3), new PostSupplierInvoiceHandler());

        // Net 30,000.00. Telephone 5,000: ITBIS 900, ISC 500, CDT 100. Insurance 20,000: ISC 3,200. Lunch 2,000: ITBIS 360, tip 200.
        // Fuel 3,000: nothing. Taxes 5,260.00; total 35,260.00 ≥ 25,000.00.
        Assert.Equal("MATCH_EXCEPTION|35260.00", $"{match.GetProperty("status").GetString()}|{match.GetProperty("total").GetString()}");
        Assert.Equal((AuthorizationErrors.NotAuthorized, ProcurementErrors.InvalidState), (byClerk.Code, early.Code));
        Assert.Equal("35260.00", posted.GetProperty("payable").GetString());
        Assert.Equal("1260.00|3700.00|100.00|200.00|0.00|-35260.00|62400:2000.00,63300:5000.00,63500:3000.00,64000:20000.00|35260.00/35260.00", await BooksAsync(h));
    }

    [Trait("AcceptanceGas1", "GAS-09")]
    [Fact]
    public async Task GAS09_an_individual_is_withheld_ISR_on_services_only_and_ITBIS_on_every_lines_ITBIS()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h, """{"tax_code":"RET_ITBIS","rate":"0.30","base":"ITBIS","party_types":["INDIVIDUAL"]}""");
        await h.ActivateRuleAsync(
            await h.FiscalActorsAsync(initEnvironment: false), "isr", "RET_ISR_SERVICIOS", FiscalRuleKinds.PurchaseWithholding,
            """{"tax_code":"RET_ISR","rate":"0.10","base":"NET","party_types":["INDIVIDUAL"],"isr_withholding_type":"2","applies_to":["EXPENSE_SERVICE"]}""", new DateOnly(2026, 1, 1));
        var mechanic = await h.CreateActiveSupplierAsync("00112345678", "Pedro Mecánico");
        var si = await RegisterAsync(
            h, w, "r", mechanic, "B1100000001",
            Line(w, "Reparación del motor", "REPARACIONES", "ITBIS_18", 1m, 10000m),
            Line(w, "Filtro de aceite", "REPUESTOS", "ITBIS_18", 2m, 500m));

        await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, w.Clerk, "m", si, 1), new MatchSupplierInvoiceHandler());
        var posted = await RunAsync(h, new PostSupplierInvoice(h.CompanyId, w.Clerk, "p", si, 2), new PostSupplierInvoiceHandler());

        // Net 11,000.00; ITBIS 1,800 + 180 = 1,980.00. Withheld: ISR 10 % of the service (1,000.00) and 30 % of each line's ITBIS
        // (540.00 + 54.00) = 1,594.00, none of ISR on the filter (a good). Payable 11,000 + 1,980 − 1,594 = 11,386.00.
        Assert.Equal("11386.00", posted.GetProperty("payable").GetString());
        Assert.Equal("1980.00|0.00|0.00|0.00|-1594.00|-11386.00|63700:10000.00,66150:1000.00|11386.00/11386.00", await BooksAsync(h));
    }

    [Trait("AcceptanceGas1", "GAS-13")]
    [Fact]
    public async Task GAS13_a_posted_expense_invoice_without_payments_is_reversed_and_its_fiscal_number_is_free_again()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var si = await RegisterAsync(h, w, "r", w.S.Purchasing.SupplierId, "B0100000703", Line(w, "Teléfono", "TELEFONO", "TELECOM", 1m, 5000m));
        await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, w.Clerk, "m", si, 1), new MatchSupplierInvoiceHandler());
        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, w.Clerk, "p", si, 2), new PostSupplierInvoiceHandler());

        var byClerk = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ReverseSupplierInvoice(h.CompanyId, w.Clerk, "x0", si, 3, "Duplicada"), new ReverseSupplierInvoiceHandler()));
        var reversed = await RunAsync(h, new ReverseSupplierInvoice(h.CompanyId, w.Controller, "x", si, 3, "Registrada dos veces"), new ReverseSupplierInvoiceHandler());
        var again = await RegisterAsync(h, w, "r2", w.S.Purchasing.SupplierId, "B0100000703", Line(w, "Teléfono", "TELEFONO", "TELECOM", 1m, 5000m));

        Assert.Equal(AuthorizationErrors.NotAuthorized, byClerk.Code);
        Assert.Equal("REVERSED", reversed.GetProperty("status").GetString());
        Assert.Equal("0.00|0.00|0.00|0.00|0.00|0.00|63300:0.00|6500.00/0.00", await BooksAsync(h));
        Assert.Equal("REVERSED:REVERSED,DRAFT:NOT_POSTED", await h.ScalarAsync<string>(
            "SELECT string_agg(document_status || ':' || accounting_status, ',' ORDER BY document_status DESC) FROM pur.supplier_invoice"));
        Assert.NotEqual(Guid.Empty, again);
    }

    [Fact]
    public async Task An_expense_invoice_needs_an_active_category_a_tax_type_in_force_a_plant_and_a_free_fiscal_number()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        await RegisterAsync(h, w, "r", w.S.Purchasing.SupplierId, "B0100000704", Line(w, "Reparación", "REPARACIONES", "ITBIS_18", 1m, 100m));
        var version = await h.ScalarAsync<long>("SELECT version FROM pur.expense_category WHERE code = 'COMBUSTIBLE'");
        await h.RunAsync(new DeactivateExpenseCategory(h.CompanyId, w.Controller, "off", w.Categories["COMBUSTIBLE"], version), new DeactivateExpenseCategoryHandler());
        Task<CommandResult> Register(string key, string ncf, Guid plant, params ExpenseLineInput[] lines)
            => h.RunAsync(new RegisterExpenseInvoice(h.CompanyId, w.Clerk, key, w.S.Purchasing.SupplierId, ncf, Today(h), Today(h), plant, lines), new RegisterExpenseInvoiceHandler());

        var used = await Assert.ThrowsAsync<DomainException>(() => Register("x1", "B0100000704", w.Plant, Line(w, "Reparación", "REPARACIONES", "ITBIS_18", 1m, 100m)));
        var inactive = await Assert.ThrowsAsync<DomainException>(() => Register("x2", "B0100000705", w.Plant, Line(w, "Gasoil", "COMBUSTIBLE", "EXENTO", 1m, 100m)));
        var unknownType = await Assert.ThrowsAsync<DomainException>(() => Register(
            "x3", "B0100000706", w.Plant, new ExpenseLineInput("Reparación", w.Categories["REPARACIONES"], Guid.NewGuid(), 1m, 100m)));
        var noPlant = await Assert.ThrowsAsync<DomainException>(() => Register("x4", "B0100000707", Guid.NewGuid(), Line(w, "Reparación", "REPARACIONES", "ITBIS_18", 1m, 100m)));
        var noText = await Assert.ThrowsAsync<DomainException>(() => Register("x5", "B0100000708", w.Plant, Line(w, " ", "REPARACIONES", "ITBIS_18", 1m, 100m)));

        Assert.Equal(
            (ProcurementErrors.FiscalNumberUsed, ExpenseErrors.CategoryNotFound, TaxErrors.FiscalGateClosed, ProcurementErrors.PlantMismatch, ProcurementErrors.LinesRequired),
            (used.Code, inactive.Code, unknownType.Code, noPlant.Code, noText.Code));
    }
}
