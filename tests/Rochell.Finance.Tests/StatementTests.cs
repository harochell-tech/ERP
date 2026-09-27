using System.Globalization;
using System.Text.Json;
using Rochell.Finance.Ledger;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Finance.Tests;

/// <summary>FIN1-03: report structures, trial balance, account ledger, balance sheet, income statement and CSV (GL-06; E-FIN1-03-1…11).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class StatementTests(PostgresFixture postgres)
{
    private const string Support = "5f70bf18a086007016e948b04aed3b82103a36bea41755b6cddfaf10ace3c6ef";

    private sealed record Books(
        Guid Contador, Guid Controller, Guid Approver, Guid Plant, Guid Cash, Guid Accrued, Guid Capital, Guid Sales, Guid Cost, Guid Energy, DateOnly Today, DateOnly PriorYear);

    private static async Task<Guid> Account(TestHarness h, Guid controller, string code, string name, string accountClass)
        => (await h.RunAsync(new CreateAccount(h.CompanyId, controller, "acc-" + code, code, name, accountClass, false), new CreateAccountHandler())).ResultRef;

    private static async Task Adjust(TestHarness h, Books b, string key, DateOnly date, params ManualJournalLine[] lines)
    {
        var id = (await h.RunAsync(
            new PrepareManualJournal(h.CompanyId, b.Contador, key, date, "Asiento " + key, "Soporte " + key, Support, "ACR-NTX", false, lines), new PrepareManualJournalHandler())).ResultRef;
        await h.RunAsync(new SubmitManualJournal(h.CompanyId, b.Contador, key + "-s", id, 1), new SubmitManualJournalHandler());
        await h.RunAsync(new ApproveManualJournal(h.CompanyId, b.Controller, key + "-a", id, 2), new ApproveManualJournalHandler());
    }

    /// <summary>
    /// Last year (Dec 15): capital 10 000 in cash, sales 3 000, energy 1 000. This year (today): sales 5 000, cost 2 000, energy
    /// 1 250 accrued at a plant. Cash 15 000 = accrued 1 250 + capital 10 000 + result of the year 1 750 + prior years 2 000.
    /// </summary>
    private static async Task<Books> BooksAsync(TestHarness h)
    {
        var today = BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);
        for (var y = today.Year - 1; y <= today.Year + 1; y++)
        {
            await h.OpenPeriodsAsync(y);
        }

        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var b = new Books(
            await h.SessionWithRolesAsync("CONTADOR"), controller, await h.SessionWithRolesAsync("APROBADOR_POLITICAS"), await h.CreatePlantAsync(),
            await Account(h, controller, "1100", "Caja", "ASSET"), await Account(h, controller, "2200", "Gastos acumulados por pagar", "LIABILITY"),
            await Account(h, controller, "3100", "Capital", "EQUITY"), await Account(h, controller, "4100", "Ventas", "REVENUE"),
            await Account(h, controller, "5100", "Costo de ventas", "COST"), await Account(h, controller, "6200", "Energía", "EXPENSE"),
            today, new DateOnly(today.Year - 1, 12, 15));
        await Adjust(h, b, "py-capital", b.PriorYear, new(b.Cash, 10000.00m, 0m), new(b.Capital, 0m, 10000.00m));
        await Adjust(h, b, "py-sales", b.PriorYear, new(b.Cash, 3000.00m, 0m), new(b.Sales, 0m, 3000.00m));
        await Adjust(h, b, "py-energy", b.PriorYear, new(b.Energy, 1000.00m, 0m), new(b.Cash, 0m, 1000.00m));
        await Adjust(h, b, "sales", b.Today, new(b.Cash, 5000.00m, 0m), new(b.Sales, 0m, 5000.00m));
        await Adjust(h, b, "cost", b.Today, new(b.Cost, 2000.00m, 0m), new(b.Cash, 0m, 2000.00m));
        await Adjust(h, b, "energy", b.Today, new(b.Energy, 1250.00m, 0m, PlantId: b.Plant), new(b.Accrued, 0m, 1250.00m));
        return b;
    }

    private static ReportLineInput L(string code, string caption, string? parent, int sign, int order, params Guid[] accounts) => new(code, caption, parent, sign, order, accounts);

    private static async Task<Guid> StructureAsync(TestHarness h, Books b, string key, string report, params ReportLineInput[] lines)
    {
        var id = (await h.RunAsync(new PrepareReportStructure(h.CompanyId, b.Controller, key, report, new DateOnly(b.Today.Year, 1, 1), lines), new PrepareReportStructureHandler())).ResultRef;
        await h.RunAsync(new ApproveReportStructure(h.CompanyId, b.Approver, key + "-a", id), new ApproveReportStructureHandler());
        return id;
    }

    private static ReportLineInput[] BalanceLines(Books b) =>
    [
        L("A", "Activo", null, 1, 1), L("A1", "Efectivo", "A", 1, 1, b.Cash),
        L("P", "Pasivo", null, -1, 2), L("P1", "Gastos acumulados", "P", -1, 1, b.Accrued),
        L("K", "Patrimonio", null, -1, 3, b.Capital),
    ];

    private static ReportLineInput[] IncomeLines(Books b) =>
        [L("I", "Ingresos", null, -1, 1, b.Sales), L("C", "Costo de ventas", null, 1, 2, b.Cost), L("G", "Gastos", null, 1, 3, b.Energy)];

    private static string Lines(JsonElement statement)
        => string.Join('|', statement.GetProperty("lines").EnumerateArray().Select(l => $"{l.GetProperty("lineCode").GetString()}={l.GetProperty("amount").GetString()}"));

    [Trait("AcceptanceFin1", "GL-06")]
    [Fact]
    public async Task GL06_an_approved_structure_presents_a_balance_sheet_that_balances_and_an_income_statement()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var b = await BooksAsync(h);
        await StructureAsync(h, b, "bs", "BALANCE_SHEET", BalanceLines(b));
        await StructureAsync(h, b, "is", "INCOME_STATEMENT", IncomeLines(b));

        var bs = JsonDocument.Parse(await h.QueryAsync(new GetBalanceSheet(h.CompanyId, b.Controller, b.Today), new GetBalanceSheetHandler())).RootElement;
        var income = JsonDocument.Parse(await h.QueryAsync(new GetIncomeStatement(h.CompanyId, b.Controller, new DateOnly(b.Today.Year, 1, 1), b.Today), new GetIncomeStatementHandler())).RootElement;
        var lastYear = JsonDocument.Parse(await h.QueryAsync(new GetIncomeStatement(h.CompanyId, b.Controller, new DateOnly(b.Today.Year - 1, 1, 1), new DateOnly(b.Today.Year - 1, 12, 31)), new GetIncomeStatementHandler())).RootElement;

        Assert.Equal("A=15000.00|A1=15000.00|P=1250.00|P1=1250.00|K=10000.00", Lines(bs));
        Assert.Equal(
            ("15000.00", "1250.00", "10000.00", "1750.00", "2000.00", "0.00", true),
            (bs.GetProperty("totalAssets").GetString(), bs.GetProperty("totalLiabilities").GetString(), bs.GetProperty("totalEquity").GetString(),
             bs.GetProperty("currentYearResult").GetString(), bs.GetProperty("priorYearsResult").GetString(), bs.GetProperty("difference").GetString(), bs.GetProperty("balanced").GetBoolean()));
        Assert.Equal("I=5000.00|C=2000.00|G=1250.00", Lines(income));
        Assert.Equal(("5000.00", "2000.00", "1250.00", "1750.00"), (income.GetProperty("revenue").GetString(), income.GetProperty("cost").GetString(), income.GetProperty("expenses").GetString(), income.GetProperty("netIncome").GetString()));
        Assert.Equal("2000.00", lastYear.GetProperty("netIncome").GetString());
        Assert.Equal(0, bs.GetProperty("unassignedAccounts").GetArrayLength());
    }

    [Trait("AcceptanceFin1", "GL-06")]
    [Fact]
    public async Task A_structure_holds_each_account_once_covers_every_active_account_of_its_classes_and_supersedes_the_previous_one()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var b = await BooksAsync(h);

        Task<CommandResult> Prepare(string key, params ReportLineInput[] lines)
            => h.RunAsync(new PrepareReportStructure(h.CompanyId, b.Controller, key, "BALANCE_SHEET", b.Today, lines), new PrepareReportStructureHandler());

        var twice = await Assert.ThrowsAsync<DomainException>(() => Prepare("twice", L("A", "Activo", null, 1, 1, b.Cash), L("B", "Otro", null, 1, 2, b.Cash)));
        var wrongClass = await Assert.ThrowsAsync<DomainException>(() => Prepare("class", L("A", "Activo", null, 1, 1, b.Cash, b.Sales)));
        var orphan = await Assert.ThrowsAsync<DomainException>(() => Prepare("orphan", L("A1", "Efectivo", "A", 1, 1, b.Cash)));
        var partial = (await Prepare("partial", L("A", "Activo", null, 1, 1, b.Cash))).ResultRef;
        var incomplete = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveReportStructure(h.CompanyId, b.Approver, "partial-a", partial), new ApproveReportStructureHandler()));
        var self = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveReportStructure(h.CompanyId, b.Controller, "self", partial), new ApproveReportStructureHandler()));
        var first = await StructureAsync(h, b, "v2", "BALANCE_SHEET", BalanceLines(b));
        var second = await StructureAsync(h, b, "v3", "BALANCE_SHEET", BalanceLines(b));

        Assert.Equal(
            (LedgerErrors.StructureInvalid, LedgerErrors.StructureInvalid, LedgerErrors.StructureInvalid, LedgerErrors.StructureIncomplete, AuthorizationErrors.NotAuthorized),
            (twice.Code, wrongClass.Code, orphan.Code, incomplete.Code, self.Code));
        Assert.Contains("3100", incomplete.Message, StringComparison.Ordinal);
        Assert.Equal("1:DRAFT,2:SUPERSEDED,3:ACTIVE", await h.ScalarAsync<string>(
            "SELECT string_agg(version || ':' || status, ',' ORDER BY version) FROM fin.report_structure_version WHERE report = 'BALANCE_SHEET'"));
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetReportStructure(h.CompanyId, b.Approver, partial), new GetReportStructureHandler())).RootElement;
        Assert.Equal("2200,3100", string.Join(',', detail.GetProperty("missingAccounts").EnumerateArray().Select(a => a.GetProperty("code").GetString())));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task The_trial_balance_opens_income_accounts_at_the_start_of_the_year_and_carries_prior_results_on_their_own_row()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var b = await BooksAsync(h);
        var from = new DateOnly(b.Today.Year, 1, 1);

        var tb = JsonDocument.Parse(await h.QueryAsync(new GetTrialBalance(h.CompanyId, b.Contador, from, b.Today), new GetTrialBalanceHandler())).RootElement;
        var plant = JsonDocument.Parse(await h.QueryAsync(new GetTrialBalance(h.CompanyId, b.Contador, from, b.Today, PlantId: b.Plant), new GetTrialBalanceHandler())).RootElement;
        var inverted = await Assert.ThrowsAsync<DomainException>(() => h.QueryAsync(new GetTrialBalance(h.CompanyId, b.Contador, b.Today, from), new GetTrialBalanceHandler()));

        Assert.Equal(
            "1100:12000.00:5000.00:2000.00:15000.00|2200:0.00:0.00:1250.00:-1250.00|3100:-10000.00:0.00:0.00:-10000.00|4100:0.00:0.00:5000.00:-5000.00|"
            + "5100:0.00:2000.00:0.00:2000.00|6200:0.00:1250.00:0.00:1250.00|:-2000.00:0.00:0.00:-2000.00",
            string.Join('|', tb.GetProperty("rows").EnumerateArray().Select(r => string.Join(':',
                r.GetProperty("code").GetString(), r.GetProperty("opening").GetString(), r.GetProperty("debit").GetString(), r.GetProperty("credit").GetString(), r.GetProperty("closing").GetString()))));
        Assert.Equal(
            ("0.00", "8250.00", "8250.00", "0.00", true),
            (tb.GetProperty("totalOpening").GetString(), tb.GetProperty("totalDebit").GetString(), tb.GetProperty("totalCredit").GetString(), tb.GetProperty("totalClosing").GetString(), tb.GetProperty("balanced").GetBoolean()));
        Assert.Equal(GetTrialBalanceHandler.PriorYearsCaption, tb.GetProperty("rows")[6].GetProperty("name").GetString());
        Assert.Equal((true, false, "6200"), (plant.GetProperty("filtered").GetBoolean(), plant.GetProperty("balanced").GetBoolean(), plant.GetProperty("rows")[0].GetProperty("code").GetString()));
        Assert.Equal(QueryErrorsInvalid, inverted.Code);
    }

    private const string QueryErrorsInvalid = "INVALID_PARAMETER";

    [Fact]
    public async Task The_account_ledger_shows_each_movement_with_its_document_and_running_balance()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var b = await BooksAsync(h);
        var from = new DateOnly(b.Today.Year, 1, 1);

        var ledger = JsonDocument.Parse(await h.QueryAsync(new GetAccountLedger(h.CompanyId, b.Contador, b.Cash, from, b.Today), new GetAccountLedgerHandler())).RootElement;
        var page = JsonDocument.Parse(await h.QueryAsync(new GetAccountLedger(h.CompanyId, b.Contador, b.Cash, from, b.Today, Limit: 1, Offset: 1), new GetAccountLedgerHandler())).RootElement;
        var energy = JsonDocument.Parse(await h.QueryAsync(new GetAccountLedger(h.CompanyId, b.Contador, b.Energy, from, b.Today), new GetAccountLedgerHandler())).RootElement;

        Assert.Equal(("12000.00", "15000.00", 2), (ledger.GetProperty("opening").GetString(), ledger.GetProperty("closing").GetString(), ledger.GetProperty("count").GetInt32()));
        Assert.Equal(
            "MANUAL_JOURNAL:AJ-000004:5000.00:0.00:17000.00|MANUAL_JOURNAL:AJ-000005:0.00:2000.00:15000.00",
            string.Join('|', ledger.GetProperty("movements").EnumerateArray().Select(m => string.Join(':',
                m.GetProperty("documentKind").GetString(), m.GetProperty("documentNumber").GetString(), m.GetProperty("debit").GetString(), m.GetProperty("credit").GetString(), m.GetProperty("balance").GetString()))));
        Assert.Equal(("15000.00", 1), (page.GetProperty("movements")[0].GetProperty("balance").GetString(), page.GetProperty("movements").GetArrayLength()));
        Assert.Equal(("0.00", "1250.00"), (energy.GetProperty("opening").GetString(), energy.GetProperty("closing").GetString())); // income accounts open on Jan 1 (E-FIN1-03-4)
    }

    [Fact]
    public async Task Statements_wait_for_every_active_account_to_have_a_class_and_for_an_approved_structure()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var b = await BooksAsync(h);

        var noStructure = await Assert.ThrowsAsync<DomainException>(() => h.QueryAsync(new GetBalanceSheet(h.CompanyId, b.Controller, b.Today), new GetBalanceSheetHandler()));
        await h.CreateAccountAsync("9999", "Cuenta sin clase", isControl: false);
        var noClass = await Assert.ThrowsAsync<DomainException>(() => h.QueryAsync(new GetIncomeStatement(h.CompanyId, b.Controller, b.Today, b.Today), new GetIncomeStatementHandler()));
        var tb = JsonDocument.Parse(await h.QueryAsync(new GetTrialBalance(h.CompanyId, b.Controller, b.Today, b.Today), new GetTrialBalanceHandler())).RootElement;

        Assert.Equal((LedgerErrors.StructureMissing, LedgerErrors.AccountClassMissing), (noStructure.Code, noClass.Code));
        Assert.Contains("9999", noClass.Message, StringComparison.Ordinal);
        Assert.True(tb.GetProperty("balanced").GetBoolean()); // the trial balance still works (E-FIN1-03-8)
    }

    [Fact]
    public async Task The_CSV_shows_the_same_report_with_Spanish_headers_and_two_decimals()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var b = await BooksAsync(h);
        await StructureAsync(h, b, "bs", "BALANCE_SHEET", BalanceLines(b));
        await StructureAsync(h, b, "is", "INCOME_STATEMENT", IncomeLines(b));
        var from = new DateOnly(b.Today.Year, 1, 1);

        var tb = LedgerCsv.TrialBalance(await h.QueryAsync(new GetTrialBalance(h.CompanyId, b.Controller, from, b.Today), new GetTrialBalanceHandler())).Split("\r\n");
        var ledger = LedgerCsv.AccountLedger(await h.QueryAsync(new GetAccountLedger(h.CompanyId, b.Controller, b.Cash, from, b.Today, All: true), new GetAccountLedgerHandler())).Split("\r\n");
        var bs = LedgerCsv.BalanceSheet(await h.QueryAsync(new GetBalanceSheet(h.CompanyId, b.Controller, b.Today), new GetBalanceSheetHandler()));
        var income = LedgerCsv.IncomeStatement(await h.QueryAsync(new GetIncomeStatement(h.CompanyId, b.Controller, from, b.Today), new GetIncomeStatementHandler()));

        Assert.Equal("Código,Cuenta,Clase,Saldo inicial,Débitos,Créditos,Saldo final", tb[0]);
        Assert.Equal("1100,Caja,ASSET,12000.00,5000.00,2000.00,15000.00", tb[1]);
        Assert.Equal(",Totales,,0.00,8250.00,8250.00,0.00", tb[^2]);
        Assert.Equal($"{b.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)},MANUAL_ADJUSTMENT,ManualJournalApproved,MANUAL_JOURNAL,AJ-000004,P-34,5000.00,0.00,17000.00", ledger[2]);
        Assert.Contains("\"  Efectivo\"", bs, StringComparison.Ordinal);
        Assert.Contains("DIFF,Diferencia,,0.00", bs, StringComparison.Ordinal);
        Assert.Contains("NET,Resultado neto,,1750.00", income, StringComparison.Ordinal);
    }
}
