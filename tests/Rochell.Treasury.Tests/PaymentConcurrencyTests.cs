using System.Globalization;
using System.Text;
using Rochell.Audit;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Reconciliation;
using Rochell.TestInfrastructure;
using Rochell.Treasury.Payments;
using Rochell.Treasury.Statements;
using Xunit;

namespace Rochell.Treasury.Tests;

/// <summary>
/// E-VS2-09-3: payments under concurrency. Every scenario starts its commands together (one gate) and throttles itself like CC-04;
/// the database must end consistent whichever command wins.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PaymentConcurrencyTests(PostgresFixture postgres)
{
    private const string R10 = "0192f001-0000-7000-8000-000000000010";
    private const int Racers = 8;

    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    /// <summary>Runs the actions together; returns each one's error code (null when it succeeded).</summary>
    private static async Task<List<string?>> RaceAsync(IEnumerable<Func<Task>> actions)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var throttle = new SemaphoreSlim(Racers);
        var tasks = actions.Select(action => Task.Run(async () =>
        {
            await gate.Task;
            await throttle.WaitAsync();
            try
            {
                await action();
                return (string?)null;
            }
            catch (DomainException ex)
            {
                return ex.Code;
            }
            finally
            {
                throttle.Release();
            }
        })).ToList();
        gate.SetResult();
        return [.. await Task.WhenAll(tasks)];
    }

    private static async Task<Guid> PrepareAsync(TestHarness h, TestPayments p, string key, Guid apDoc, decimal amount)
        => (await h.RunAsync(
            new PrepareSupplierPayment(h.CompanyId, p.Treasurer, key, p.Supplier, p.BankAccount, p.PartyBankAccount, Today(h), null, [new(apDoc, amount)]),
            new PrepareSupplierPaymentHandler())).ResultRef;

    private static Task<CommandResult> Import(TestHarness h, TestPayments p, string key, string csv, decimal closing, DateOnly? to = null)
        => h.RunAsync(
            new ImportBankStatement(h.CompanyId, p.Treasurer, key, p.BankAccount, "extracto.csv", Convert.ToBase64String(Encoding.UTF8.GetBytes(csv)), Today(h), to ?? Today(h), 0m, closing),
            new ImportBankStatementHandler());

    [Fact]
    public async Task Concurrent_releases_against_one_invoice_never_overdraw_it()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePaymentSetupAsync(bankCode: "TEST_BANK");
        var open = await h.ScalarAsync<decimal>("SELECT open_amount FROM fin.ap_document WHERE ap_doc_id = @d", ("d", p.ApDocs[0]));
        var payments = new List<Guid>();
        for (var i = 0; i < Racers; i++)
        {
            payments.Add(await PrepareAsync(h, p, $"prepare-{i}", p.ApDocs[0], open)); // preparing reserves nothing (E-VS2-6)
        }

        var codes = await RaceAsync(payments.Select((id, i) => (Func<Task>)(() => h.RunAsync(new ReleaseSupplierPayment(h.CompanyId, p.Controller, $"release-{i}", id, 1), new ReleaseSupplierPaymentHandler()))));

        Assert.Single(codes, c => c is null);
        Assert.All(codes.Where(c => c is not null), c => Assert.Equal(PaymentErrors.ApplicationExceedsOpenAmount, c));
        Assert.Equal(0m, await h.ScalarAsync<decimal>("SELECT open_amount FROM fin.ap_document WHERE ap_doc_id = @d", ("d", p.ApDocs[0])));
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM fin.payment WHERE status::text = 'RELEASED'"));
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM fin.gl_journal j JOIN fin.posting_rule r USING (posting_rule_id) WHERE r.code = 'R-09'"));
    }

    [Fact]
    public async Task One_line_and_two_payments_or_two_lines_and_one_payment_match_once()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePaymentSetupAsync(invoices: 2, bankCode: "TEST_BANK");
        var a = await PrepareAsync(h, p, "prepare-a", p.ApDocs[0], 100m);
        var b = await PrepareAsync(h, p, "prepare-b", p.ApDocs[1], 100m);
        await h.RunAsync(new ReleaseSupplierPayment(h.CompanyId, p.Controller, "release-a", a, 1), new ReleaseSupplierPaymentHandler());
        await h.RunAsync(new ReleaseSupplierPayment(h.CompanyId, p.Controller, "release-b", b, 1), new ReleaseSupplierPaymentHandler());
        var day = Today(h).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        await Import(h, p, "import", $"Fecha,Referencia,Descripcion,Debito,Credito\n{day},,Uno,100.00,\n{day},,Dos,100.00,\n{day},,Tres,100.00,\n", -300m);
        var lines = new List<Guid>();
        foreach (var d in new[] { "Uno", "Dos", "Tres" })
        {
            lines.Add(await h.ScalarAsync<Guid>("SELECT line_id FROM fin.bank_statement_line WHERE description = @d", ("d", d)));
        }

        // One line, two payments: one match.
        var oneLine = await RaceAsync([
            () => h.RunAsync(new MatchBankLine(h.CompanyId, p.Treasurer, "l1-a", lines[0], 1, a, 2), new MatchBankLineHandler()),
            () => h.RunAsync(new MatchBankLine(h.CompanyId, p.Treasurer, "l1-b", lines[0], 1, b, 2), new MatchBankLineHandler()),
        ]);
        var winner = await h.ScalarAsync<Guid>("SELECT matched_payment_id FROM fin.bank_statement_line WHERE line_id = @l", ("l", lines[0]));
        var other = winner == a ? b : a;

        // Two lines, one payment (the one still RELEASED): one match.
        var onePayment = await RaceAsync([
            () => h.RunAsync(new MatchBankLine(h.CompanyId, p.Treasurer, "l2", lines[1], 1, other, 2), new MatchBankLineHandler()),
            () => h.RunAsync(new MatchBankLine(h.CompanyId, p.Treasurer, "l3", lines[2], 1, other, 2), new MatchBankLineHandler()),
        ]);

        Assert.Single(oneLine, c => c is null);
        Assert.Single(onePayment, c => c is null);
        Assert.All(oneLine.Concat(onePayment).Where(c => c is not null), c => Assert.Contains(c, new[] { StatementErrors.VersionConflict, StatementErrors.LineNotUnmatched, StatementErrors.PaymentNotReleased }));
        Assert.Equal("CLEARED:CLEARED", await h.ScalarAsync<string>($"SELECT string_agg(status::text, ':') FROM fin.payment WHERE payment_id IN ('{a}', '{b}')"));
        Assert.Equal(2L, await h.ScalarAsync<long>("SELECT count(*) FROM fin.bank_statement_line WHERE status = 'MATCHED'"));
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM (SELECT matched_payment_id FROM fin.bank_statement_line WHERE matched_payment_id IS NOT NULL GROUP BY 1 HAVING count(*) > 1) x"));
    }

    [Fact]
    public async Task The_same_file_imported_concurrently_makes_one_statement()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePaymentSetupAsync(bankCode: "TEST_BANK");
        var day = Today(h).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var csv = $"Fecha,Referencia,Descripcion,Debito,Credito\n{day},,Comision,150.00,\n{day},,Deposito,,50.00\n";

        var codes = await RaceAsync(Enumerable.Range(0, Racers).Select(i => (Func<Task>)(() => Import(h, p, $"import-{i}", csv, -100m))));

        Assert.Single(codes, c => c is null);
        Assert.All(codes.Where(c => c is not null), c => Assert.Equal(StatementErrors.AlreadyImported, c));
        Assert.Equal("1|2|1", await h.ScalarAsync<string>(
            "SELECT (SELECT count(*) FROM fin.bank_statement) || '|' || (SELECT count(*) FROM fin.bank_statement_line) || '|' || (SELECT count(*) FROM fin.bank_statement_file)"));
    }

    [Fact]
    public async Task A_charge_racing_the_BANK_REC_close_never_posts_into_the_closed_month()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var p = await h.CreatePaymentSetupAsync(bankCode: "TEST_BANK");
        await h.CreateActiveMapAsync("BANK_CHARGES", await h.CreateAccountAsync("6105", "Cargos bancarios", isControl: false));
        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '{R10}' AND version = 1");
        var month = Today(h);
        var endOfMonth = new DateOnly(month.Year, month.Month, 1).AddMonths(1).AddDays(-1);
        var day = month.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        await Import(h, p, "import", $"Fecha,Referencia,Descripcion,Debito,Credito\n{day},,Comision,150.00,\n", -150m, endOfMonth);
        var line = await h.ScalarAsync<Guid>("SELECT line_id FROM fin.bank_statement_line");
        var period = await h.ScalarAsync<Guid>("SELECT period_id FROM fin.period WHERE company_id = @c AND @d BETWEEN starts_on AND ends_on", ("c", h.CompanyId), ("d", month));

        clock.Advance(TimeSpan.FromDays(40));
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        await new LedgerSealer(h.Sealer, h.Clock).SealAllAsync(CancellationToken.None);
        var codes = await RaceAsync([
            () => h.RunAsync(new CloseComponent(h.CompanyId, controller, "close", period, "BANK-REC"), new CloseComponentHandler()),
            () => h.RunAsync(new RecognizeBankCharge(h.CompanyId, controller, "charge", line, 1), new RecognizeBankChargeHandler()),
        ]);

        // Either the close wins: the charge then posts late into the next month and the snapshot lists the unrecorded debit; or
        // the charge wins: it posts into the month, its ledger group is not sealed yet and the close is refused (Patch 1.1 gate).
        // Never a closed month with the charge inside it.
        Assert.Null(codes[1]);
        var postedInMonth = await h.ScalarAsync<bool>(
            $"SELECT j.period_id = '{period}' FROM fin.gl_journal j JOIN fin.posting_rule r USING (posting_rule_id) WHERE r.code = 'R-10'");
        var closed = await h.ScalarAsync<string>($"SELECT status FROM fin.close_component_state WHERE period_id = '{period}' AND component = 'BANK-REC'") == "CLOSED";
        if (closed)
        {
            Assert.Null(codes[0]);
            Assert.False(postedInMonth);
            Assert.True(await h.ScalarAsync<bool>("SELECT late_entry FROM fin.gl_journal j JOIN fin.posting_rule r USING (posting_rule_id) WHERE r.code = 'R-10'"));
            Assert.True(await h.ScalarAsync<bool>(
                $"""
                SELECT EXISTS (SELECT 1 FROM fin.close_snapshot s, jsonb_array_elements(s.content -> 'balances') b
                               WHERE s.period_id = '{period}' AND s.component = 'BANK-REC' AND b ->> 'kind' = 'in_transit' AND b ->> 'key' LIKE '%/UNRECORDED_DEBIT/%')
                """));
        }
        else
        {
            Assert.Equal(ReconciliationErrors.IntegrityNotSealed, codes[0]);
            Assert.True(postedInMonth);
        }
    }
}
