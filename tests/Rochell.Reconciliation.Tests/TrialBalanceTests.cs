using System.Globalization;
using System.Text;
using System.Text.Json;
using Rochell.Finance.Ledger;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Rochell.Treasury.Payments;
using Rochell.Treasury.Statements;
using Xunit;

namespace Rochell.Reconciliation.Tests;

/// <summary>FIN1-03 GL-05: the trial balance of a month with purchases, payments, bank charges and adjustments.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class TrialBalanceTests(PostgresFixture postgres)
{
    private const string R10 = "0192f001-0000-7000-8000-000000000010";
    private const string Support = "5f70bf18a086007016e948b04aed3b82103a36bea41755b6cddfaf10ace3c6ef";

    private static string D(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string M(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    [Trait("AcceptanceFin1", "GL-05")]
    [Fact]
    public async Task GL05_a_month_with_purchases_payments_charges_and_adjustments_balances_and_each_account_is_the_sum_of_its_entries()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var today = BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);
        var p = await h.CreatePaymentSetupAsync(bankCode: "TEST_BANK");   // posted supplier invoices: the purchases
        var charges = await h.CreateAccountAsync("6105", "Cargos bancarios", isControl: false);
        await h.CreateActiveMapAsync("BANK_CHARGES", charges);
        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '{R10}' AND version = 1");
        var amount = await h.ScalarAsync<decimal>("SELECT open_amount FROM fin.ap_document WHERE ap_doc_id = @d", ("d", p.ApDocs[0]));
        var payment = (await h.RunAsync(
            new PrepareSupplierPayment(h.CompanyId, p.Treasurer, "prepare", p.Supplier, p.BankAccount, p.PartyBankAccount, today, null, [new(p.ApDocs[0], amount)]),
            new PrepareSupplierPaymentHandler())).ResultRef;
        await h.RunAsync(new ReleaseSupplierPayment(h.CompanyId, p.Controller, "release", payment, 1), new ReleaseSupplierPaymentHandler());
        await h.RunAsync(
            new ImportBankStatement(
                h.CompanyId, p.Treasurer, "statement", p.BankAccount, "statement.csv",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"Fecha,Referencia,Descripcion,Debito,Credito\n{D(today)},,Comision,150.00,\n")),
                today, today, 0m, -150m),
            new ImportBankStatementHandler());
        var line = await h.ScalarAsync<Guid>("SELECT line_id FROM fin.bank_statement_line WHERE description = 'Comision'");
        await h.RunAsync(new RecognizeBankCharge(h.CompanyId, p.Controller, "charge", line, 1), new RecognizeBankChargeHandler());

        var contador = await h.SessionWithRolesAsync("CONTADOR");
        var energy = await h.CreateAccountAsync("6200", "Energía", isControl: false);
        var accrued = await h.CreateAccountAsync("2200", "Gastos acumulados por pagar", isControl: false);
        var adjustment = (await h.RunAsync(
            new PrepareManualJournal(h.CompanyId, contador, "aj", today, "Provisión de energía", "Factura EDE", Support, "ACR-NTX", false, [new(energy, 1250.00m, 0m), new(accrued, 0m, 1250.00m)]),
            new PrepareManualJournalHandler())).ResultRef;
        await h.RunAsync(new SubmitManualJournal(h.CompanyId, contador, "aj-s", adjustment, 1), new SubmitManualJournalHandler());
        await h.RunAsync(new ApproveManualJournal(h.CompanyId, p.Controller, "aj-a", adjustment, 2), new ApproveManualJournalHandler());

        var from = new DateOnly(today.Year, today.Month, 1);
        var tb = JsonDocument.Parse(await h.QueryAsync(new GetTrialBalance(h.CompanyId, p.Controller, from, today), new GetTrialBalanceHandler())).RootElement;
        var bank = JsonDocument.Parse(await h.QueryAsync(new GetTrialBalance(h.CompanyId, p.Controller, from, today, BankAccountId: p.BankAccount), new GetTrialBalanceHandler())).RootElement;

        Assert.True(tb.GetProperty("balanced").GetBoolean());
        Assert.False(tb.GetProperty("filtered").GetBoolean());
        Assert.Equal(tb.GetProperty("totalDebit").GetString(), tb.GetProperty("totalCredit").GetString());
        Assert.Equal(
            await h.ScalarAsync<string>(
                """
                SELECT string_agg(code || ':' || s, '|' ORDER BY code) FROM (
                  SELECT a.code, sum(e.debit - e.credit)::numeric(19,2)::text AS s FROM fin.gl_entry e JOIN fin.account a ON a.account_id = e.account_id GROUP BY a.code) x
                """),
            string.Join('|', tb.GetProperty("rows").EnumerateArray().Select(r => $"{r.GetProperty("code").GetString()}:{r.GetProperty("closing").GetString()}")));
        Assert.Equal("AUTO,MANUAL_ADJUSTMENT", await h.ScalarAsync<string>("SELECT string_agg(DISTINCT journal_type, ',' ORDER BY journal_type) FROM fin.gl_journal"));
        Assert.Equal(
            (true, 1, M(-amount - 150m)),
            (bank.GetProperty("filtered").GetBoolean(), bank.GetProperty("rows").GetArrayLength(), bank.GetProperty("rows")[0].GetProperty("closing").GetString()));
    }
}
