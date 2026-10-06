using System.Text.Json;
using Rochell.Finance.ExchangeRates;
using Rochell.Platform.Commands;
using Rochell.Procurement.Expenses;
using Rochell.Procurement.Imports;
using Rochell.Procurement.SupplierInvoices;
using Rochell.Reconciliation;
using Rochell.TestInfrastructure;
using Rochell.Treasury.BankAccounts;
using Rochell.Treasury.Transfers;
using Xunit;
using static Rochell.Procurement.Tests.ExpenseInvoiceTests;
using static Rochell.Procurement.Tests.ForeignInvoiceTests;

namespace Rochell.Procurement.Tests;

/// <summary>
/// USD1-06 (USD-08, USD-10, E-USD1-06-1…5): the month-end revaluation of USD payables and USD banks (P-43, reversed the next day by P-43R),
/// undone to redo it; the FX-REVAL, IMPORT-CLEARING and AP-GL (USD) reconciliations; the DUA's ITBIS in the IT-1 summary.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class FxRevaluationTests(PostgresFixture postgres)
{
    private const string P42 = "0192f001-0000-7000-8000-000000000034";

    private sealed record Month(Foreign F, DateOnly InvoiceDate, DateOnly End, DateOnly RateDay);

    /// <summary>
    /// Last month: a rate of 60.00 a week before its last weekday and 60.50 on that weekday; the foreign invoice of USD 10,000.00 (600,000.00)
    /// posted at 60.00 and USD 1,000.00 bought into a USD account at 60.00 (60,000.00).
    /// </summary>
    private static async Task<Month> LastMonthAsync(TestHarness h)
    {
        var f = await ForeignAsync(h);
        var first = new DateOnly(Today(h).Year, Today(h).Month, 1);
        var end = first.AddDays(-1);
        var rateDay = end.DayOfWeek switch { DayOfWeek.Saturday => end.AddDays(-1), DayOfWeek.Sunday => end.AddDays(-2), _ => end };
        var invoiceDate = rateDay.AddDays(-7);
        var treasurer = await h.SessionWithRolesAsync("TESORERO");
        foreach (var (key, day, rate) in new[] { ("r1", invoiceDate, 60m), ("r2", rateDay, 60.5m) })
        {
            var r = await h.RunAsync(new PrepareExchangeRate(h.CompanyId, treasurer, key, "USD", day, rate, "Banco Central"), new PrepareExchangeRateHandler());
            await h.RunAsync(new ApproveExchangeRate(h.CompanyId, f.W.Controller, key + "-a", r.ResultRef, 1), new ApproveExchangeRateHandler());
        }

        var si = (await h.RunAsync(
            new RegisterExpenseInvoice(h.CompanyId, f.W.Clerk, "si", f.Supplier, "INV-2026-0147", invoiceDate, invoiceDate.AddDays(60), f.W.Plant,
                [Usd(f.Forklift, "Montacargas usado", 1m, 8000m), Usd(f.Parts, "Repuestos", 4m, 500m)]),
            new RegisterExpenseInvoiceHandler())).ResultRef;
        await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, f.W.Clerk, "si-m", si, 1), new MatchSupplierInvoiceHandler());
        await h.RunAsync(new ApproveMatchException(h.CompanyId, f.W.Controller, "si-a", si, 2, "Importación"), new ApproveMatchExceptionHandler());
        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, f.W.Clerk, "si-p", si, 3), new PostSupplierInvoiceHandler());

        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id IN ('{P42}', "
            + "'0192f001-0000-7000-8000-000000000035', '0192f001-0000-7000-8000-000000000036') AND version = 1");
        await h.CreateActiveMapAsync("FX_UNREALIZED", await h.CreateAccountAsync("68200", "Diferencia cambiaria no realizada", isControl: false));
        await h.CreateAccountAsync("1101", "Banco en pesos", isControl: true);
        await h.CreateAccountAsync("1102", "Banco en dólares", isControl: true);
        var peso = (await h.RunAsync(new RegisterBankAccount(h.CompanyId, f.W.Controller, "bank-dop", "BPD", "0123456789", "1101"), new RegisterBankAccountHandler())).ResultRef;
        var usd = (await h.RunAsync(new RegisterBankAccount(h.CompanyId, f.W.Controller, "bank-usd", "BPD", "0987654321", "1102", "USD"), new RegisterBankAccountHandler())).ResultRef;
        var transfer = await h.RunAsync(new PrepareBankTransfer(h.CompanyId, treasurer, "buy", peso, usd, invoiceDate, 1000m, 60m), new PrepareBankTransferHandler());
        await h.RunAsync(new ReleaseBankTransfer(h.CompanyId, f.W.Controller, "buy-r", transfer.ResultRef, 1), new ReleaseBankTransferHandler());
        return new Month(f, invoiceDate, end, rateDay);
    }

    /// <summary>Foreign payables, the USD bank and the unrealized difference (code:debit − credit) on <paramref name="day"/>.</summary>
    private static Task<string?> OnAsync(TestHarness h, DateOnly day)
        => h.ScalarAsync<string>(
            """
            SELECT string_agg(a.code || ':' || coalesce((SELECT sum(e.debit - e.credit) FROM fin.gl_entry e WHERE e.account_id = a.account_id AND e.posting_date <= @d), 0)::numeric(19,2), ','
                              ORDER BY a.code)
            FROM fin.account a WHERE a.code IN ('1102', '21020', '68200')
            """,
            ("d", day));

    private static async Task<string> FindingsAsync(TestHarness h, Guid controller, string key, string code, DateOnly cutoff)
    {
        await h.RunAsync(new RunReconciliation(h.CompanyId, controller, key, [code], cutoff), new RunReconciliationHandler());
        return await h.ScalarAsync<string>(
            """
            SELECT coalesce(string_agg(x.classification || ':' || x.match_key, ',' ORDER BY x.match_key), '-')
            FROM rec.recon_exception x JOIN rec.recon_run r USING (run_id)
            WHERE r.recon_code = @code AND r.run_id = (SELECT run_id FROM rec.recon_run WHERE recon_code = @code ORDER BY as_of DESC, run_id DESC LIMIT 1)
            """,
            ("code", code)) ?? "-";
    }

    [Trait("AcceptanceUsd1", "USD-08")]
    [Fact]
    public async Task USD08_an_open_invoice_of_USD_10000_at_60_50_carries_a_loss_of_5000_on_the_last_day_reversed_the_next()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var m = await LastMonthAsync(h);
        var controller = m.F.W.Controller;
        var missing = await FindingsAsync(h, controller, "fx1", "FX-REVAL", Today(h));

        var posted = JsonDocument.Parse((await h.RunAsync(new PostFxRevaluation(h.CompanyId, controller, "rev", m.End), new PostFxRevaluationHandler())).ResultPayload).RootElement;
        var again = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new PostFxRevaluation(h.CompanyId, controller, "rev2", m.End), new PostFxRevaluationHandler()));
        var notEnded = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PostFxRevaluation(h.CompanyId, controller, "rev3", Today(h).AddMonths(1)), new PostFxRevaluationHandler()));
        var revalued = await FindingsAsync(h, controller, "fx2", "FX-REVAL", Today(h));
        var apUsd = await FindingsAsync(h, controller, "ap", "AP-GL", Today(h));

        // Payable 10,000 × 60.50 = 605,000.00 (+5,000.00, a loss); the bank 1,000 × 60.50 = 60,500.00 (+500.00, a gain): net loss 4,500.00.
        Assert.Equal($"FX_REVALUATION_MISSING:month:{m.End:yyyy-MM}", missing);
        Assert.Equal("60.5000|4500.00", $"{posted.GetProperty("rate").GetString()}|{posted.GetProperty("loss").GetString()}");
        Assert.Equal((FxRevaluationErrors.AlreadyRevalued, FxRevaluationErrors.MonthNotEnded), (again.Code, notEnded.Code));
        Assert.Equal("1102:60500.00,21020:-605000.00,68200:4500.00", await OnAsync(h, m.End));
        Assert.Equal("1102:60000.00,21020:-600000.00,68200:0.00", await OnAsync(h, m.End.AddDays(1)));
        Assert.Equal(("-", "-"), (revalued, apUsd));

        // Undone, both journals are reversed and the month can be revalued again.
        var id = posted.GetProperty("revaluationId").GetGuid();
        await h.RunAsync(new UndoFxRevaluation(h.CompanyId, controller, "undo", id, 1, "Tasa de cierre equivocada"), new UndoFxRevaluationHandler());
        Assert.Equal("1102:60000.00,21020:-600000.00,68200:0.00", await OnAsync(h, m.End));
        await h.RunAsync(new PostFxRevaluation(h.CompanyId, controller, "rev4", m.End), new PostFxRevaluationHandler());
        Assert.Equal("UNDONE,POSTED", await h.ScalarAsync<string>("SELECT string_agg(status, ',' ORDER BY version DESC, status DESC) FROM fin.fx_revaluation"));
    }

    [Trait("AcceptanceUsd1", "USD-10")]
    [Fact]
    public async Task USD10_import_clearing_reconciles_per_DUA_warns_when_unsettled_and_the_IT1_shows_the_customs_ITBIS()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var f = await ForeignAsync(h);
        await h.CreateActiveMapAsync("IMPORT_CLEARING", await h.CreateAccountAsync("13900", "Importaciones por liquidar", isControl: true));
        await h.AdminRequireAsync("UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '" + h.UserId + "' WHERE posting_rule_id = '0192f001-0000-7000-8000-000000000031' AND version = 1");
        var dua = await h.RunAsync(
            new RegisterCustomsDeclaration(h.CompanyId, f.W.Clerk, "dua", f.W.S.Purchasing.SupplierId, f.W.Plant, "10020-IM-2610-000123", Today(h), Today(h).AddDays(5), 660000m, 30000m, 124200m, 0m),
            new RegisterCustomsDeclarationHandler());

        var fresh = await FindingsAsync(h, f.W.Controller, "ic1", "IMPORT-CLEARING", Today(h));
        var overdue = await FindingsAsync(h, f.W.Controller, "ic2", "IMPORT-CLEARING", Today(h).AddDays(31));
        var it1 = JsonDocument.Parse(await h.QueryAsync(
            new Rochell.Tax.Reports.GetIt1Summary(h.CompanyId, await h.SessionWithRolesAsync("CONTADOR"), $"{Today(h):yyyyMM}"), new Rochell.Tax.Reports.GetIt1SummaryHandler())).RootElement;

        Assert.Equal("-", fresh); // the DUA's 30,000.00 is what «Importaciones por liquidar» holds for it
        Assert.Equal("IMPORT_SETTLEMENT_OVERDUE:DUA 10020-IM-2610-000123", overdue);
        Assert.Equal("1|124200.00", $"{it1.GetProperty("importDeclarations").GetInt32()}|{it1.GetProperty("importItbis").GetString()}");
        Assert.NotEqual(Guid.Empty, dua.ResultRef);
    }
}
