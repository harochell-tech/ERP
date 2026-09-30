using System.Text.Json;
using Rochell.Finance.Configuration;
using Rochell.Finance.Ledger;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Finance.Tests;

/// <summary>UX4-01 · E-UX4-2: the trial balance's debit / credit balances, the results inside equity and each account's balance.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class Ux4FinanceTests(PostgresFixture postgres)
{
    private const string Support = "5f70bf18a086007016e948b04aed3b82103a36bea41755b6cddfaf10ace3c6ef";

    [Fact]
    public async Task Balances_are_split_by_side_results_shown_inside_equity_and_each_account_carries_its_balance()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var today = BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);
        for (var y = today.Year - 1; y <= today.Year + 1; y++)
        {
            await h.OpenPeriodsAsync(y);
        }

        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var contador = await h.SessionWithRolesAsync("CONTADOR");
        var approver = await h.SessionWithRolesAsync("APROBADOR_POLITICAS");
        async Task<Guid> Account(string code, string name, string accountClass)
            => (await h.RunAsync(new CreateAccount(h.CompanyId, controller, "acc-" + code, code, name, accountClass, false), new CreateAccountHandler())).ResultRef;
        var (cash, accrued, capital, sales, cost, energy) = (await Account("1100", "Caja", "ASSET"), await Account("2200", "Gastos acumulados", "LIABILITY"),
            await Account("3100", "Capital", "EQUITY"), await Account("4100", "Ventas", "REVENUE"), await Account("5100", "Costo de ventas", "COST"), await Account("6200", "Energía", "EXPENSE"));
        var unused = await Account("1190", "Caja chica", "ASSET");
        async Task Adjust(string key, DateOnly date, params ManualJournalLine[] lines)
        {
            var id = (await h.RunAsync(
                new PrepareManualJournal(h.CompanyId, contador, key, date, "Asiento " + key, "Soporte " + key, Support, "ACR-NTX", false, lines), new PrepareManualJournalHandler())).ResultRef;
            await h.RunAsync(new SubmitManualJournal(h.CompanyId, contador, key + "-s", id, 1), new SubmitManualJournalHandler());
            await h.RunAsync(new ApproveManualJournal(h.CompanyId, controller, key + "-a", id, 2), new ApproveManualJournalHandler());
        }

        // Last year: capital 10 000 in cash, sales 3 000, energy 1 000. This year: sales 5 000, cost 2 000, energy 1 250 accrued.
        var priorYear = new DateOnly(today.Year - 1, 12, 15);
        await Adjust("py-capital", priorYear, new(cash, 10000.00m, 0m), new(capital, 0m, 10000.00m));
        await Adjust("py-sales", priorYear, new(cash, 3000.00m, 0m), new(sales, 0m, 3000.00m));
        await Adjust("py-energy", priorYear, new(energy, 1000.00m, 0m), new(cash, 0m, 1000.00m));
        await Adjust("sales", today, new(cash, 5000.00m, 0m), new(sales, 0m, 5000.00m));
        await Adjust("cost", today, new(cost, 2000.00m, 0m), new(cash, 0m, 2000.00m));
        await Adjust("energy", today, new(energy, 1250.00m, 0m), new(accrued, 0m, 1250.00m));
        async Task Structure(string key, string report, params ReportLineInput[] lines)
        {
            var id = (await h.RunAsync(new PrepareReportStructure(h.CompanyId, controller, key, report, new DateOnly(today.Year, 1, 1), lines), new PrepareReportStructureHandler())).ResultRef;
            await h.RunAsync(new ApproveReportStructure(h.CompanyId, approver, key + "-a", id), new ApproveReportStructureHandler());
        }

        await Structure("bs", "BALANCE_SHEET", new("A", "Activo", null, 1, 1, [cash, unused]), new("P", "Pasivo", null, -1, 2, [accrued]), new("K", "Patrimonio", null, -1, 3, [capital]));

        var tb = JsonDocument.Parse(await h.QueryAsync(new GetTrialBalance(h.CompanyId, contador, new DateOnly(today.Year, 1, 1), today), new GetTrialBalanceHandler())).RootElement;
        var bs = JsonDocument.Parse(await h.QueryAsync(new GetBalanceSheet(h.CompanyId, contador, today), new GetBalanceSheetHandler())).RootElement;
        var accounts = JsonDocument.Parse(await h.QueryAsync(new ListAccounts(h.CompanyId, contador), new ListAccountsHandler())).RootElement;

        // Closing: cash 12 000 + 5 000 − 2 000 = 15 000 debit; accrued 1 250, capital 10 000 and sales 5 000 credit; cost 2 000 and energy
        // 1 250 debit; prior years' result 3 000 − 1 000 = 2 000 credit. Each side adds up to 18 250.00.
        Assert.Equal(
            "1100:15000.00/0.00|2200:0.00/1250.00|3100:0.00/10000.00|4100:0.00/5000.00|5100:2000.00/0.00|6200:1250.00/0.00|:0.00/2000.00",
            string.Join('|', tb.GetProperty("rows").EnumerateArray().Select(r => $"{r.GetProperty("code").GetString()}:{r.GetProperty("debitBalance").GetString()}/{r.GetProperty("creditBalance").GetString()}")));
        Assert.Equal(("18250.00", "18250.00", true), (tb.GetProperty("totalDebitBalance").GetString(), tb.GetProperty("totalCreditBalance").GetString(), tb.GetProperty("balanced").GetBoolean()));

        // Equity 10 000 + result of the year 1 750 + prior years 2 000 = 13 750; + liabilities 1 250 = 15 000 = assets.
        Assert.Equal(("13750.00", "15000.00", "15000.00"), (bs.GetProperty("totalEquityWithResults").GetString(), bs.GetProperty("totalLiabilitiesAndEquity").GetString(),
            bs.GetProperty("totalAssets").GetString()));

        var byCode = accounts.GetProperty("items").EnumerateArray().ToDictionary(a => a.GetProperty("code").GetString()!);
        Assert.Equal(("15000.00", true), (byCode["1100"].GetProperty("balance").GetString(), byCode["1100"].GetProperty("hasEntries").GetBoolean()));
        Assert.Equal(("-10000.00", true), (byCode["3100"].GetProperty("balance").GetString(), byCode["3100"].GetProperty("hasEntries").GetBoolean()));
        Assert.Equal(("0.00", false), (byCode["1190"].GetProperty("balance").GetString(), byCode["1190"].GetProperty("hasEntries").GetBoolean()));
    }
}
