using System.Globalization;
using System.Text;
using System.Text.Json;
using Rochell.Audit;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Rochell.Treasury.Payments;
using Rochell.Treasury.Statements;
using Xunit;

namespace Rochell.Reconciliation.Tests;

/// <summary>VS2-06: BANK-GL, PAY-APPL and the BANK-REC close (BNK-04, BNK-05; E-VS2-06-1…10).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class BankReconciliationTests(PostgresFixture postgres)
{
    private const string R10 = "0192f001-0000-7000-8000-000000000010";

    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static DateOnly EndOfMonth(DateOnly d) => new DateOnly(d.Year, d.Month, 1).AddMonths(1).AddDays(-1);

    private static string D(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string M(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    private sealed record Setup(TestPayments P, Guid Payment, decimal Amount);

    /// <summary>A TEST_BANK account, R-10 approved, BANK_CHARGES mapped, one payment of the whole invoice released today.</summary>
    private static async Task<Setup> ReleasedAsync(TestHarness h)
    {
        var p = await h.CreatePaymentSetupAsync(bankCode: "TEST_BANK");
        var charges = await h.CreateAccountAsync("6105", "Cargos bancarios", isControl: false);
        await h.CreateActiveMapAsync("BANK_CHARGES", charges);
        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '{R10}' AND version = 1");
        var amount = await h.ScalarAsync<decimal>("SELECT open_amount FROM fin.ap_document WHERE ap_doc_id = @d", ("d", p.ApDocs[0]));
        var payment = (await h.RunAsync(
            new PrepareSupplierPayment(h.CompanyId, p.Treasurer, "prepare", p.Supplier, p.BankAccount, p.PartyBankAccount, Today(h), null, [new(p.ApDocs[0], amount)]),
            new PrepareSupplierPaymentHandler())).ResultRef;
        await h.RunAsync(new ReleaseSupplierPayment(h.CompanyId, p.Controller, "release", payment, 1), new ReleaseSupplierPaymentHandler());
        return new Setup(p, payment, amount);
    }

    /// <summary>A TEST_BANK statement from today to the end of the month (the cutoff of the month's close).</summary>
    private static Task<CommandResult> Import(TestHarness h, TestPayments p, string key, decimal opening, decimal closing, params string[] rows)
        => ImportTo(h, p, key, EndOfMonth(Today(h)), opening, closing, rows);

    private static Task<CommandResult> ImportTo(TestHarness h, TestPayments p, string key, DateOnly to, decimal opening, decimal closing, params string[] rows)
        => h.RunAsync(
            new ImportBankStatement(
                h.CompanyId, p.Treasurer, key, p.BankAccount, $"{key}.csv",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("\n", ["Fecha,Referencia,Descripcion,Debito,Credito", .. rows]) + "\n")),
                Today(h), to, opening, closing),
            new ImportBankStatementHandler());

    private static Task<Guid> LineAsync(TestHarness h, string description)
        => h.ScalarAsync<Guid>("SELECT line_id FROM fin.bank_statement_line WHERE description = @d", ("d", description));

    private static async Task<JsonElement> ReconcileAsync(TestHarness h, Guid controller, string key, DateOnly cutoff, params string[] codes)
        => JsonDocument.Parse((await h.RunAsync(new RunReconciliation(h.CompanyId, controller, key, codes.Length == 0 ? null : codes, cutoff), new RunReconciliationHandler())).ResultPayload).RootElement;

    private static string Status(JsonElement result, string code)
        => result.GetProperty("runs").EnumerateArray().Single(r => r.GetProperty("code").GetString() == code).GetProperty("status").GetString()!;

    private static Task<string?> FindingsAsync(TestHarness h, string code)
        => h.ScalarAsync<string>(
            """
            SELECT string_agg(x.classification || ':' || x.severity, ',' ORDER BY x.classification)
            FROM rec.recon_exception x JOIN rec.recon_run r USING (run_id)
            WHERE r.recon_code = @code AND r.run_id = (SELECT run_id FROM rec.recon_run WHERE recon_code = @code ORDER BY as_of DESC, run_id DESC LIMIT 1)
            """,
            ("code", code));

    [Trait("AcceptanceVs2", "BNK-04")]
    [Fact]
    public async Task A_month_with_matched_payments_and_charges_reconciles_and_BANK_REC_closes()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var (p, payment, amount) = await ReleasedAsync(h);
        var month = Today(h);
        await Import(h, p, "statement", 0m, -amount - 150m, $"{D(month)},,PAG-000001,{M(amount)},", $"{D(month)},,Comision,150.00,");
        await h.RunAsync(new MatchBankLine(h.CompanyId, p.Treasurer, "match", await LineAsync(h, "PAG-000001"), 1, payment, 2), new MatchBankLineHandler());
        await h.RunAsync(new RecognizeBankCharge(h.CompanyId, p.Controller, "charge", await LineAsync(h, "Comision"), 1), new RecognizeBankChargeHandler());

        clock.Advance(TimeSpan.FromDays(40));
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        await new LedgerSealer(h.Sealer, h.Clock).SealAllAsync(CancellationToken.None);
        var period = await h.ScalarAsync<Guid>("SELECT period_id FROM fin.period WHERE company_id = @c AND @d BETWEEN starts_on AND ends_on", ("c", h.CompanyId), ("d", month));
        var closed = await h.RunAsync(new CloseComponent(h.CompanyId, controller, "close", period, "BANK-REC"), new CloseComponentHandler());

        Assert.Contains("snapshotHash", closed.ResultPayload, StringComparison.Ordinal);
        Assert.Equal("CLOSED", await h.ScalarAsync<string>($"SELECT status FROM fin.close_component_state WHERE period_id = '{period}' AND component = 'BANK-REC'"));
        Assert.Equal("ACC-EVIDENCE:MATCHED,BANK-GL:MATCHED,PAY-APPL:MATCHED,RECEIPT-APPL:MATCHED", await h.ScalarAsync<string>(
            "SELECT string_agg(recon_code || ':' || status, ',' ORDER BY recon_code) FROM rec.recon_run"));
        Assert.Equal(EndOfMonth(month).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), await h.ScalarAsync<string>("SELECT DISTINCT cutoff_date::text FROM rec.recon_run"));
        // The snapshot holds the bank account's GL and statement balances (−amount − 150.00 both) and a zero difference.
        var gl = M(-amount - 150m);
        Assert.Equal($"bank_difference:0.00,bank_gl:{gl},bank_statement:{gl},pay_appl_applications:{M(amount)},pay_appl_payments:{M(amount)}", await h.ScalarAsync<string>(
            $"""
            SELECT string_agg(b ->> 'kind' || ':' || round((b ->> 'amount')::numeric, 2), ',' ORDER BY b ->> 'kind')
            FROM fin.close_snapshot s, jsonb_array_elements(s.content -> 'balances') b WHERE s.period_id = '{period}' AND s.component = 'BANK-REC'
            """));
    }

    [Trait("AcceptanceVs2", "BNK-05")]
    [Fact]
    public async Task A_released_payment_without_its_statement_line_is_an_in_transit_item()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var (p, _, amount) = await ReleasedAsync(h);
        var month = Today(h);
        // The bank has not debited the payment yet; a deposit is on the statement but not in the books.
        await ImportTo(h, p, "statement", month.AddDays(45), 0m, 500m, $"{D(month)},,Deposito,,500.00");

        clock.Advance(TimeSpan.FromDays(40));
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        await new LedgerSealer(h.Sealer, h.Clock).SealAllAsync(CancellationToken.None);
        var period = await h.ScalarAsync<Guid>("SELECT period_id FROM fin.period WHERE company_id = @c AND @d BETWEEN starts_on AND ends_on", ("c", h.CompanyId), ("d", month));
        await h.RunAsync(new CloseComponent(h.CompanyId, controller, "close", period, "BANK-REC"), new CloseComponentHandler());

        // GL −amount = statement 500.00 + outstanding payment (−amount) − unrecorded credit (500.00).
        Assert.Equal($"in_transit:OUTSTANDING_PAYMENT:PAG-000001:{M(-amount)},in_transit:UNRECORDED_CREDIT:Deposito:500.00", await h.ScalarAsync<string>(
            $"""
            SELECT string_agg(b ->> 'kind' || ':' || split_part(b ->> 'key', '/', 2) || ':' || split_part(b ->> 'key', '/', 3) || ':' || round((b ->> 'amount')::numeric, 2), ',' ORDER BY b ->> 'key')
            FROM fin.close_snapshot s, jsonb_array_elements(s.content -> 'balances') b WHERE s.period_id = '{period}' AND b ->> 'kind' = 'in_transit'
            """));
        // Past 30 days the items are reported as warnings (they do not block).
        await ReconcileAsync(h, controller, "aged", month.AddDays(35), "BANK-GL");
        Assert.Equal("IN_TRANSIT_AGED:WARNING,IN_TRANSIT_AGED:WARNING", await FindingsAsync(h, "BANK-GL"));
    }

    [Fact]
    public async Task A_missing_statement_or_a_wrong_opening_balance_blocks_BANK_REC()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (p, _, amount) = await ReleasedAsync(h);

        var missing = await ReconcileAsync(h, p.Controller, "missing", Today(h), "BANK-GL");
        await Import(h, p, "statement", 1000m, 1000m - amount, $"{D(Today(h))},,PAG-000001,{M(amount)},");
        var opening = await ReconcileAsync(h, p.Controller, "opening", Today(h), "BANK-GL");

        Assert.Equal(("EXCEPTIONS", "EXCEPTIONS"), (Status(missing, "BANK-GL"), Status(opening, "BANK-GL")));
        // The statement starts at 1,000.00 while the books start at 0: the opening difference explains the BANK-GL difference.
        Assert.Equal("BANK_GL_DIFFERENCE:ERROR,OPENING_DIFFERENCE:ERROR", await FindingsAsync(h, "BANK-GL"));
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM rec.recon_exception WHERE classification = 'STATEMENT_MISSING'"));
    }

    [Fact]
    public async Task A_reversed_payment_nets_out_and_a_pending_return_is_an_in_transit_item()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (p, payment, _) = await ReleasedAsync(h);
        var today = Today(h);

        // Released and reversed before the bank debited it: R-09 and its reversal cancel, nothing is listed.
        await h.RunAsync(new ReversePayment(h.CompanyId, p.Controller, "reverse", payment, 2, "Pago anulado antes del debito"), new ReversePaymentHandler());
        await Import(h, p, "statement", 0m, 0m, $"{D(today)},,Comision,1.00,", $"{D(today)},,Reintegro,,1.00");
        await h.RunAsync(new RecognizeBankCharge(h.CompanyId, p.Controller, "charge", await LineAsync(h, "Comision"), 1), new RecognizeBankChargeHandler());
        var netted = await ReconcileAsync(h, p.Controller, "netted", today, "BANK-GL", "PAY-APPL");

        // GL −1.00 (R-09 and its reversal cancel, R-10 −1.00) = statement 0.00 − unrecorded credit 1.00.
        Assert.Equal(("MATCHED", "MATCHED"), (Status(netted, "BANK-GL"), Status(netted, "PAY-APPL")));
    }

    [Fact]
    public async Task A_cleared_then_reversed_payment_waits_for_its_return()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (p, payment, amount) = await ReleasedAsync(h);
        var today = Today(h);
        await Import(h, p, "statement", 0m, -amount, $"{D(today)},,PAG-000001,{M(amount)},");
        await h.RunAsync(new MatchBankLine(h.CompanyId, p.Treasurer, "match", await LineAsync(h, "PAG-000001"), 1, payment, 2), new MatchBankLineHandler());
        await h.RunAsync(new ReversePayment(h.CompanyId, p.Controller, "reverse", payment, 3, "Transferencia devuelta por el banco receptor"), new ReversePaymentHandler());

        var pending = await ReconcileAsync(h, p.Controller, "pending", today, "BANK-GL");

        // GL 0.00 = statement −amount + outstanding return (+amount): balanced with one item.
        Assert.Equal("MATCHED", Status(pending, "BANK-GL"));
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM rec.recon_exception"));
    }

    [Fact]
    public async Task PAY_APPL_reports_an_application_that_does_not_match_its_payment_and_blocks_AP_REC_too()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (p, payment, _) = await ReleasedAsync(h);
        await h.AdminRequireAsync($"BEGIN; SET LOCAL session_replication_role = replica; UPDATE fin.ap_application SET amount = amount - 1 WHERE payment_id = '{payment}'; COMMIT;");

        var result = await ReconcileAsync(h, p.Controller, "tampered", Today(h), "PAY-APPL");

        Assert.Equal("EXCEPTIONS", Status(result, "PAY-APPL"));
        Assert.Equal(
            "APPLICATION_WITHOUT_R09_LINE:ERROR,AP_DOCUMENT_APPLICATION_DIFFERENCE:ERROR,PAYMENT_APPLICATION_DIFFERENCE:ERROR",
            await FindingsAsync(h, "PAY-APPL"));
        Assert.Equal("AP-REC,BANK-REC", await h.ScalarAsync<string>("SELECT string_agg(component, ',' ORDER BY component) FROM rec.recon_blocking WHERE recon_code = 'PAY-APPL'"));
    }
}
