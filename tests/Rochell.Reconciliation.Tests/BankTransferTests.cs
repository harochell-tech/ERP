using System.Globalization;
using System.Text;
using System.Text.Json;
using Rochell.Finance.ExchangeRates;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Reconciliation.Queries;
using Rochell.TestInfrastructure;
using Rochell.Treasury.BankAccounts;
using Rochell.Treasury.Statements;
using Rochell.Treasury.Transfers;
using Xunit;

namespace Rochell.Reconciliation.Tests;

/// <summary>
/// USD1-05b (USD-09, E-USD1-05b-1…5): buying USD from the peso account (P-42, no exchange difference), the USD account's statement in USD
/// matched to the transfer, and BANK-GL of the USD account in USD.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class BankTransferTests(PostgresFixture postgres)
{
    private const string P42 = "0192f001-0000-7000-8000-000000000034";

    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static string D(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private sealed record Setup(TestPayments P, Guid UsdBank);

    /// <summary>The payment setup (its peso account on TEST_BANK), a USD account on TEST_BANK, today's rate 60.00 and P-42 approved.</summary>
    private static async Task<Setup> SetupAsync(TestHarness h)
    {
        var p = await h.CreatePaymentSetupAsync(bankCode: "TEST_BANK");
        await h.CreateAccountAsync("1102", "Banco en dólares", isControl: true);
        var usd = (await h.RunAsync(new RegisterBankAccount(h.CompanyId, p.Controller, "bank-usd", "TEST_BANK", "5550001111", "1102", "USD"), new RegisterBankAccountHandler())).ResultRef;
        var rate = await h.RunAsync(new PrepareExchangeRate(h.CompanyId, p.Treasurer, "rate", "USD", Today(h), 60m, "Banco Central"), new PrepareExchangeRateHandler());
        await h.RunAsync(new ApproveExchangeRate(h.CompanyId, p.Controller, "rate-a", rate.ResultRef, 1), new ApproveExchangeRateHandler());
        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '{P42}' AND version = 1");
        return new Setup(p, usd);
    }

    private static async Task<(Guid Id, JsonElement Prepared)> BuyAsync(TestHarness h, Setup s, string key, decimal usd, decimal? rate)
    {
        var prepared = await h.RunAsync(new PrepareBankTransfer(h.CompanyId, s.P.Treasurer, key, s.P.BankAccount, s.UsdBank, Today(h), usd, rate, "Compra de dólares"), new PrepareBankTransferHandler());
        await h.RunAsync(new ReleaseBankTransfer(h.CompanyId, s.P.Controller, key + "-r", prepared.ResultRef, 1), new ReleaseBankTransferHandler());
        return (prepared.ResultRef, JsonDocument.Parse(prepared.ResultPayload).RootElement.Clone());
    }

    private static Task<string?> BanksAsync(TestHarness h)
        => h.ScalarAsync<string>(
            """
            SELECT string_agg(a.code || ':' || coalesce((SELECT sum(e.debit - e.credit) FROM fin.gl_entry e WHERE e.account_id = a.account_id), 0)::numeric(19,2)
                              || '/' || coalesce((SELECT sum(sign(e.debit - e.credit) * e.amount_fc) FROM fin.gl_entry e WHERE e.account_id = a.account_id), 0)::numeric(19,2), ',' ORDER BY a.code)
            FROM fin.account a WHERE a.code IN ('1101', '1102')
            """);

    [Trait("AcceptanceUsd1", "USD-09")]
    [Fact]
    public async Task USD09_USD_bought_from_the_peso_account_are_matched_in_USD_and_the_USD_account_reconciles_in_USD()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);

        var noRate = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareBankTransfer(h.CompanyId, s.P.Treasurer, "x", s.P.BankAccount, s.UsdBank, Today(h), 1000m), new PrepareBankTransferHandler()));
        var ownRelease = await h.RunAsync(new PrepareBankTransfer(h.CompanyId, s.P.Treasurer, "own", s.P.BankAccount, s.UsdBank, Today(h), 10m, 60m), new PrepareBankTransferHandler());
        var same = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new ReleaseBankTransfer(h.CompanyId, s.P.Treasurer, "own-r", ownRelease.ResultRef, 1), new ReleaseBankTransferHandler()));
        await h.RunAsync(new VoidBankTransfer(h.CompanyId, s.P.Treasurer, "own-v", ownRelease.ResultRef, 1, "Monto equivocado"), new VoidBankTransferHandler());

        // USD 1,000.00 at the bank's 60.50: 60,500.00 leave the peso account and enter the USD account with its USD.
        var (transfer, prepared) = await BuyAsync(h, s, "buy", 1000m, 60.5m);
        var books = await BanksAsync(h);

        // The USD account's statement: USD 1,000.00 credited, matched by its USD amount.
        var csv = string.Join("\n", "Fecha,Referencia,Descripcion,Debito,Credito", $"{D(Today(h))},TRF,Compra de dolares,,1000.00") + "\n";
        await h.RunAsync(
            new ImportBankStatement(h.CompanyId, s.P.Treasurer, "st", s.UsdBank, "usd.csv", Convert.ToBase64String(Encoding.UTF8.GetBytes(csv)), Today(h), Today(h), 0m, 1000m),
            new ImportBankStatementHandler());
        var line = await h.ScalarAsync<Guid>("SELECT line_id FROM fin.bank_statement_line WHERE description = 'Compra de dolares'");
        var charge = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RecognizeBankCharge(h.CompanyId, s.P.Controller, "chg", line, 1), new RecognizeBankChargeHandler()));
        await h.RunAsync(new MatchBankLineToTransfer(h.CompanyId, s.P.Treasurer, "match", line, 1, transfer), new MatchBankLineToTransferHandler());
        var reconciliation = JsonDocument.Parse(await h.QueryAsync(
            new GetBankReconciliation(h.CompanyId, s.P.Controller, s.UsdBank, Today(h)), new GetBankReconciliationHandler())).RootElement;

        Assert.Equal((TransferErrors.Invalid, AuthorizationErrors.NotAuthorized), (noRate.Code, same.Code)); // Tesorería prepares, never releases (SoD)
        Assert.Equal(StatementErrors.LineNotDebit, charge.Code); // a CREDIT line is never a charge, and a USD account takes none (E-USD1-05b-5)
        Assert.Equal("60500.00|1000.00|60.5000|60500.00", $"{prepared.GetProperty("fromAmount").GetString()}|{prepared.GetProperty("toAmount").GetString()}|" +
            $"{prepared.GetProperty("exchangeRate").GetString()}|{prepared.GetProperty("amountDop").GetString()}");
        Assert.Equal("1101:-60500.00/0.00,1102:60500.00/1000.00", books);
        Assert.Equal($"MATCHED:{transfer}", await h.ScalarAsync<string>($"SELECT status || ':' || matched_transfer_id FROM fin.bank_statement_line WHERE line_id = '{line}'"));
        Assert.Equal(
            "1000.0000|1000.0000|0.0000",
            $"{reconciliation.GetProperty("glBalance").GetString()}|{reconciliation.GetProperty("statementBalance").GetString()}|{reconciliation.GetProperty("difference").GetString()}");
    }

    [Fact]
    public async Task A_debit_line_of_a_USD_account_is_no_charge_and_a_reversed_transfer_restores_both_accounts()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        var (transfer, _) = await BuyAsync(h, s, "buy", 500m, 61m);

        var csv = string.Join("\n", "Fecha,Referencia,Descripcion,Debito,Credito", $"{D(Today(h))},,Comision swift,25.00,") + "\n";
        await h.RunAsync(
            new ImportBankStatement(h.CompanyId, s.P.Treasurer, "st", s.UsdBank, "usd.csv", Convert.ToBase64String(Encoding.UTF8.GetBytes(csv)), Today(h), Today(h), 500m, 475m),
            new ImportBankStatementHandler());
        var line = await h.ScalarAsync<Guid>("SELECT line_id FROM fin.bank_statement_line WHERE description = 'Comision swift'");
        var charge = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RecognizeBankCharge(h.CompanyId, s.P.Controller, "chg", line, 1), new RecognizeBankChargeHandler()));
        await h.RunAsync(new ReverseBankTransfer(h.CompanyId, s.P.Controller, "rev", transfer, 2, "Compra cancelada por el banco"), new ReverseBankTransferHandler());

        Assert.Equal(StatementErrors.UsdAccountNotSupported, charge.Code);
        Assert.Equal("1101:0.00/0.00,1102:0.00/0.00", await BanksAsync(h));
        Assert.Equal("REVERSED", await h.ScalarAsync<string>("SELECT status FROM fin.bank_transfer WHERE transfer_id = @t", ("t", transfer)));
    }
}
