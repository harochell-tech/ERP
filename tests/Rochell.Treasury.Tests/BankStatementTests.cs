using System.Globalization;
using System.Text;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Rochell.Treasury.Payments;
using Rochell.Treasury.Statements;
using Xunit;

namespace Rochell.Treasury.Tests;

/// <summary>VS2-05: statement import, matching and bank charges (BNK-01…03; E-VS2-05-1…9).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class BankStatementTests(PostgresFixture postgres)
{
    private const string R10 = "0192f001-0000-7000-8000-000000000010";
    private const string Header = "Fecha,Referencia,Descripcion,Debito,Credito";

    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static string D(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string M(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>The payment setup on a TEST_BANK account, R-10 approved, BANK_CHARGES mapped, one payment of the whole invoice released.</summary>
    private static async Task<(TestPayments P, Guid Payment, decimal Amount)> ReleasedAsync(TestHarness h)
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
        return (p, payment, amount);
    }

    private static string Csv(params string[] rows) => string.Join("\n", [Header, .. rows]) + "\n";

    private static Task<CommandResult> Import(
        TestHarness h, TestPayments p, string key, string csv, DateOnly from, DateOnly to, decimal opening, decimal closing, Guid? bankAccount = null)
        => h.RunAsync(
            new ImportBankStatement(h.CompanyId, p.Treasurer, key, bankAccount ?? p.BankAccount, "extracto.csv", Convert.ToBase64String(Encoding.UTF8.GetBytes(csv)), from, to, opening, closing),
            new ImportBankStatementHandler());

    private static Task<Guid> LineAsync(TestHarness h, string description)
        => h.ScalarAsync<Guid>("SELECT line_id FROM fin.bank_statement_line WHERE description = @d", ("d", description));

    [Trait("AcceptanceVs2", "BNK-02")]
    [Fact]
    public async Task A_suggested_match_confirmed_by_the_treasurer_clears_the_payment()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (p, payment, amount) = await ReleasedAsync(h);
        var today = Today(h);
        var imported = await Import(h, p, "import", Csv($"{D(today.AddDays(1))},TRF-889,Transferencia PAG-000001,{M(amount)},", $"{D(today.AddDays(1))},,Deposito,,500.00"), today, today.AddDays(1), 100000m, 100000m - amount + 500m);
        var statement = imported.ResultRef;

        var suggestions = await h.Queries.ExecuteAsync(new SuggestBankMatches(h.CompanyId, p.Treasurer, statement), new SuggestBankMatchesHandler());
        var line = await LineAsync(h, "Transferencia PAG-000001");
        await h.RunAsync(new MatchBankLine(h.CompanyId, p.Treasurer, "match", line, 1, payment, 2), new MatchBankLineHandler());

        Assert.Contains($"\"paymentId\":\"{payment}\"", suggestions, StringComparison.Ordinal);
        Assert.Contains("\"basis\":\"PAYMENT_NO\"", suggestions, StringComparison.Ordinal);
        Assert.Equal($"MATCHED:2:{payment}", await h.ScalarAsync<string>($"SELECT status || ':' || version || ':' || matched_payment_id FROM fin.bank_statement_line WHERE line_id = '{line}'"));
        Assert.Equal("CLEARED:3", await h.ScalarAsync<string>($"SELECT status || ':' || version FROM fin.payment WHERE payment_id = '{payment}'"));
        Assert.Equal("BankLineMatched,PaymentCleared", await h.ScalarAsync<string>(
            "SELECT string_agg(event_type, ',' ORDER BY event_type) FROM core.domain_event WHERE event_type IN ('BankLineMatched', 'PaymentCleared')"));
        Assert.Equal(2L, await h.ScalarAsync<long>(
            $"SELECT count(*) FROM core.state_history WHERE (aggregate_id = '{line}' AND to_state = 'MATCHED') OR (aggregate_id = '{payment}' AND to_state = 'CLEARED')"));
        // The CREDIT line stays UNMATCHED: an in-transit item for BANK-GL (E-VS2-05-7); only DEBIT lines are suggested.
        Assert.Equal("UNMATCHED", await h.ScalarAsync<string>("SELECT status FROM fin.bank_statement_line WHERE direction = 'CREDIT'"));
        Assert.DoesNotContain("Deposito", suggestions, StringComparison.Ordinal);
    }

    [Trait("AcceptanceVs2", "BNK-01")]
    [Fact]
    public async Task The_same_file_is_refused_and_an_overlapping_one_adds_only_its_new_lines()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (p, _, _) = await ReleasedAsync(h);
        var today = Today(h);
        var first = Csv($"{D(today)},REF-1,Pago de luz,1500.00,", $"{D(today)},,Comision,150.00,");
        var statement = (await Import(h, p, "first", first, today, today, 10000m, 8350m)).ResultRef;

        var again = await Assert.ThrowsAsync<DomainException>(() => Import(h, p, "again", first, today, today, 10000m, 8350m));
        var overlapping = await Import(
            h, p, "overlap", Csv($"{D(today)},REF-1,Pago de luz,1500.00,", $"{D(today)},,Comision,150.00,", $"{D(today.AddDays(1))},,Deposito,,700.00"), today, today.AddDays(1), 10000m, 9050m);

        Assert.Equal(StatementErrors.AlreadyImported, again.Code);
        Assert.Contains(statement.ToString(), again.Message, StringComparison.Ordinal);
        Assert.Contains("\"inserted\":1", overlapping.ResultPayload, StringComparison.Ordinal);
        Assert.Contains($"\"existingStatementId\":\"{statement}\"", overlapping.ResultPayload, StringComparison.Ordinal);
        Assert.Contains("\"row\":2", overlapping.ResultPayload, StringComparison.Ordinal);
        Assert.Contains("\"row\":3", overlapping.ResultPayload, StringComparison.Ordinal);
        Assert.Equal(3L, await h.ScalarAsync<long>("SELECT count(*) FROM fin.bank_statement_line"));
        Assert.Equal(2L, await h.ScalarAsync<long>("SELECT count(*) FROM fin.bank_statement_file"));
        Assert.Equal(1L, await h.ScalarAsync<long>($"SELECT count(*) FROM fin.bank_statement_line WHERE statement_id = '{overlapping.ResultRef}'"));
    }

    [Fact]
    public async Task Identical_lines_of_one_file_are_numbered_and_the_file_is_kept()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (p, _, _) = await ReleasedAsync(h);
        var today = Today(h);
        var csv = Csv($"{D(today)},,Comision,\"1,000.00\",", $"{D(today)},,Comision,\"1,000.00\",");

        var statement = (await Import(h, p, "import", csv, today, today, 5000m, 3000m)).ResultRef;

        Assert.Equal("1,2", await h.ScalarAsync<string>("SELECT string_agg(occurrence::text, ',' ORDER BY occurrence) FROM fin.bank_statement_line WHERE bank_reference IS NULL"));
        Assert.Equal(csv, Encoding.UTF8.GetString((await h.ScalarAsync<byte[]>($"SELECT content FROM fin.bank_statement_file WHERE statement_id = '{statement}'"))!));
        Assert.Equal("TEST_BANK:1", await h.ScalarAsync<string>(
            $"SELECT f.bank_code || ':' || f.version FROM fin.bank_statement_file b JOIN fin.bank_statement_format f USING (format_id) WHERE b.statement_id = '{statement}'"));
    }

    [Fact]
    public async Task A_bad_row_a_wrong_balance_or_a_line_outside_the_period_rejects_the_whole_file()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (p, _, _) = await ReleasedAsync(h);
        var today = Today(h);

        var badRows = await Assert.ThrowsAsync<DomainException>(() => Import(
            h, p, "bad", Csv($"{D(today)},,Comision,150.00,", "31/02/2026,,Fecha mala,10.00,", $"{D(today)},,Cero,0.00,", $"{D(today)},,Ambos,1.00,1.00", $"{D(today)},,Tres decimales,1.005,"), today, today, 1000m, 850m));
        var balance = await Assert.ThrowsAsync<DomainException>(() => Import(h, p, "balance", Csv($"{D(today)},,Comision,150.00,"), today, today, 1000m, 900m));
        var outside = await Assert.ThrowsAsync<DomainException>(() => Import(h, p, "outside", Csv($"{D(today.AddDays(2))},,Comision,150.00,"), today, today.AddDays(1), 1000m, 850m));
        var noFields = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new ImportBankStatement(h.CompanyId, p.Treasurer, "fields", p.BankAccount, "e.csv", Convert.ToBase64String(Encoding.UTF8.GetBytes(Csv($"{D(today)},,Comision,150.00,")))),
            new ImportBankStatementHandler()));

        Assert.Equal(
            (StatementErrors.FileInvalid, StatementErrors.BalanceMismatch, StatementErrors.LineOutsidePeriod, StatementErrors.FieldsRequired),
            (badRows.Code, balance.Code, outside.Code, noFields.Code));
        Assert.Contains("4 row(s)", badRows.Message, StringComparison.Ordinal);
        foreach (var row in new[] { "row 3:", "row 4:", "row 5:", "row 6:" })
        {
            Assert.Contains(row, badRows.Message, StringComparison.Ordinal);
        }

        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM fin.bank_statement"));
    }

    [Fact]
    public async Task A_bank_without_a_format_or_a_closed_BANK_REC_period_refuses_the_import()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (p, _, _) = await ReleasedAsync(h);
        var today = Today(h);
        await h.CreateAccountAsync("1102", "Banco sin formato", isControl: true);
        var other = (await h.RunAsync(
            new Treasury.BankAccounts.RegisterBankAccount(h.CompanyId, p.Controller, "bank-2", "BPD", "5555555555", "1102"),
            new Treasury.BankAccounts.RegisterBankAccountHandler())).ResultRef;
        var csv = Csv($"{D(today)},,Comision,150.00,");

        var noFormat = await Assert.ThrowsAsync<DomainException>(() => Import(h, p, "no-format", csv, today, today, 1000m, 850m, other));
        await h.SetComponentAsync(today, "BANK-REC", "CLOSED");
        var closed = await Assert.ThrowsAsync<DomainException>(() => Import(h, p, "closed", csv, today, today, 1000m, 850m));

        Assert.Equal((StatementErrors.FormatMissing, StatementErrors.PeriodClosed), (noFormat.Code, closed.Code));
        Assert.Contains("row(s) 2", closed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_signed_windows_1252_format_reads_period_and_balances_from_the_file()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePaymentSetupAsync(bankCode: "TEST_SIGNED");
        var today = Today(h);
        var iso = (DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var text = string.Join(
            "\r\n",
            $"Periodo;{iso(today)};{iso(today)}",
            "Saldo inicial;1.000,00",
            "Saldo final;2.834,07",
            "Fecha;Descripción;Referencia;Monto",
            $"{iso(today)};Comisión por transferencia;;-165,93",
            $"{iso(today)};Depósito \"en tránsito\";DEP-1;2.000,00",
            "Fin del extracto") + "\r\n";
        var bytes = CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetBytes(text);

        var result = await h.RunAsync(
            new ImportBankStatement(h.CompanyId, p.Treasurer, "signed", p.BankAccount, "estado.csv", Convert.ToBase64String(bytes)),
            new ImportBankStatementHandler());
        var disagree = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new ImportBankStatement(h.CompanyId, p.Treasurer, "disagree", p.BankAccount, "estado2.csv", Convert.ToBase64String([.. bytes, .. "\r\n"u8.ToArray()]), OpeningBalance: 999m),
            new ImportBankStatementHandler()));

        Assert.Equal("1000.0000:2834.0700", await h.ScalarAsync<string>($"SELECT opening_balance || ':' || closing_balance FROM fin.bank_statement WHERE statement_id = '{result.ResultRef}'"));
        Assert.Equal("CREDIT:2000.0000:DEP-1:Depósito \"en tránsito\"|DEBIT:165.9300::Comisión por transferencia", await h.ScalarAsync<string>(
            "SELECT string_agg(direction || ':' || amount || ':' || coalesce(bank_reference, '') || ':' || description, '|' ORDER BY direction) FROM fin.bank_statement_line"));
        Assert.Equal(StatementErrors.FieldsDisagree, disagree.Code);
    }

    [Fact]
    public async Task A_match_needs_a_released_payment_of_the_account_with_the_exact_amount_within_ten_days()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (p, payment, amount) = await ReleasedAsync(h);
        var today = Today(h);
        await Import(
            h, p, "import",
            Csv($"{D(today)},,Diferente,{M(amount - 1m)},", $"{D(today.AddDays(11))},,Tarde,{M(amount)},", $"{D(today)},,Credito,,{M(amount)}", $"{D(today.AddDays(10))},,Justo,{M(amount)},"),
            today, today.AddDays(11), 100000m, 100000m - (2m * amount) - amount + 1m + amount);

        async Task<DomainException> Match(string description, string key, long paymentVersion = 2)
        {
            var line = await LineAsync(h, description);
            return await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new MatchBankLine(h.CompanyId, p.Treasurer, key, line, 1, payment, paymentVersion), new MatchBankLineHandler()));
        }

        var differs = await Match("Diferente", "differs");
        var late = await Match("Tarde", "late");
        var credit = await Match("Credito", "credit");
        var stale = await Match("Justo", "stale", paymentVersion: 1);
        var controller = await Assert.ThrowsAsync<DomainException>(async () => await h.RunAsync(
            new MatchBankLine(h.CompanyId, p.Controller, "controller", await LineAsync(h, "Justo"), 1, payment, 2), new MatchBankLineHandler()));
        await h.RunAsync(new MatchBankLine(h.CompanyId, p.Treasurer, "match", await LineAsync(h, "Justo"), 1, payment, 2), new MatchBankLineHandler());
        var twice = await Match("Tarde", "twice", paymentVersion: 3);

        Assert.Equal(
            (StatementErrors.AmountDiffers, StatementErrors.DateOutsideWindow, StatementErrors.PaymentNotReversed, StatementErrors.VersionConflict, AuthorizationErrors.NotAuthorized, StatementErrors.PaymentNotReleased),
            (differs.Code, late.Code, credit.Code, stale.Code, controller.Code, twice.Code));
    }

    [Fact]
    public async Task Unmatching_returns_the_payment_to_released_and_a_reversed_payment_keeps_its_line()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (p, payment, amount) = await ReleasedAsync(h);
        var today = Today(h);
        await Import(h, p, "import", Csv($"{D(today)},,PAG-000001,{M(amount)},"), today, today, 100000m, 100000m - amount);
        var line = await LineAsync(h, "PAG-000001");
        await h.RunAsync(new MatchBankLine(h.CompanyId, p.Treasurer, "match", line, 1, payment, 2), new MatchBankLineHandler());

        var noReason = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new UnmatchBankLine(h.CompanyId, p.Controller, "no-reason", line, 2, "  "), new UnmatchBankLineHandler()));
        await h.RunAsync(new UnmatchBankLine(h.CompanyId, p.Controller, "unmatch", line, 2, "Conciliado con el pago equivocado"), new UnmatchBankLineHandler());

        Assert.Equal(StatementErrors.ReasonRequired, noReason.Code);
        Assert.Equal("UNMATCHED:3:", await h.ScalarAsync<string>($"SELECT status || ':' || version || ':' || coalesce(matched_payment_id::text, '') FROM fin.bank_statement_line WHERE line_id = '{line}'"));
        Assert.Equal("RELEASED:4", await h.ScalarAsync<string>($"SELECT status || ':' || version FROM fin.payment WHERE payment_id = '{payment}'"));
        Assert.Equal("Conciliado con el pago equivocado", await h.ScalarAsync<string>($"SELECT reason FROM core.state_history WHERE aggregate_id = '{payment}' AND from_state = 'CLEARED' AND to_state = 'RELEASED'"));

        // E-VS2-04-6: the CLEARED path end to end — match again, reverse, and the line stays matched to the reversed payment.
        await h.RunAsync(new MatchBankLine(h.CompanyId, p.Treasurer, "match-again", line, 3, payment, 4), new MatchBankLineHandler());
        await h.RunAsync(new ReversePayment(h.CompanyId, p.Controller, "reverse", payment, 5, "Transferencia devuelta por el banco receptor"), new ReversePaymentHandler());
        var reversed = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new UnmatchBankLine(h.CompanyId, p.Controller, "unmatch-reversed", line, 4, "Intento tras la reversa"), new UnmatchBankLineHandler()));

        Assert.Equal(StatementErrors.PaymentReversed, reversed.Code);
        Assert.Equal($"MATCHED:{payment}", await h.ScalarAsync<string>($"SELECT status || ':' || matched_payment_id FROM fin.bank_statement_line WHERE line_id = '{line}'"));
    }

    [Fact]
    public async Task The_banks_return_of_a_reversed_transfer_is_matched_to_the_payment()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (p, payment, amount) = await ReleasedAsync(h);
        var today = Today(h);
        await Import(h, p, "import", Csv($"{D(today)},,PAG-000001,{M(amount)},"), today, today, 100000m, 100000m - amount);
        var debit = await LineAsync(h, "PAG-000001");
        await Import(
            h, p, "returns", Csv($"{D(today.AddDays(2))},,Devolucion PAG-000001,,{M(amount)}", $"{D(today.AddDays(2))},,Devolucion repetida,,{M(amount)}"),
            today.AddDays(1), today.AddDays(2), 100000m - amount, 100000m + amount);
        var back = await LineAsync(h, "Devolucion PAG-000001");
        var repeated = await LineAsync(h, "Devolucion repetida");
        Task<CommandResult> Match(string key, Guid line, long paymentVersion) => h.RunAsync(new MatchBankLine(h.CompanyId, p.Treasurer, key, line, 1, payment, paymentVersion), new MatchBankLineHandler());

        var beforeReversal = await Assert.ThrowsAsync<DomainException>(() => Match("early", back, 2));
        await h.RunAsync(new MatchBankLine(h.CompanyId, p.Treasurer, "match", debit, 1, payment, 2), new MatchBankLineHandler());
        await h.RunAsync(new ReversePayment(h.CompanyId, p.Controller, "reverse", payment, 3, "Transferencia devuelta por el banco receptor"), new ReversePaymentHandler());
        await Match("return", back, 4);
        var second = await Assert.ThrowsAsync<DomainException>(() => Match("second", repeated, 4));

        Assert.Equal((StatementErrors.PaymentNotReversed, StatementErrors.ReturnAlreadyMatched), (beforeReversal.Code, second.Code));
        Assert.Equal($"MATCHED:2:{payment}", await h.ScalarAsync<string>($"SELECT status || ':' || version || ':' || matched_payment_id FROM fin.bank_statement_line WHERE line_id = '{back}'"));
        Assert.Equal("REVERSED:4", await h.ScalarAsync<string>($"SELECT status || ':' || version FROM fin.payment WHERE payment_id = '{payment}'"));

        // A return matched by mistake is unmatched; the reversed payment and its DEBIT line do not change.
        await h.RunAsync(new UnmatchBankLine(h.CompanyId, p.Controller, "unmatch", back, 2, "Devolucion conciliada por error"), new UnmatchBankLineHandler());
        await h.RunAsync(new MatchBankLine(h.CompanyId, p.Treasurer, "return-2", repeated, 1, payment, 4), new MatchBankLineHandler());

        Assert.Equal("UNMATCHED:3", await h.ScalarAsync<string>($"SELECT status || ':' || version FROM fin.bank_statement_line WHERE line_id = '{back}'"));
        Assert.Equal($"MATCHED:{payment}", await h.ScalarAsync<string>($"SELECT status || ':' || matched_payment_id FROM fin.bank_statement_line WHERE line_id = '{debit}'"));
        Assert.Equal("REVERSED:4", await h.ScalarAsync<string>($"SELECT status || ':' || version FROM fin.payment WHERE payment_id = '{payment}'"));
    }

    [Fact]
    public async Task A_payment_reversed_before_the_bank_debited_it_has_no_return()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (p, payment, amount) = await ReleasedAsync(h);
        var today = Today(h);
        await h.RunAsync(new ReversePayment(h.CompanyId, p.Controller, "reverse", payment, 2, "Pago anulado antes del debito"), new ReversePaymentHandler());
        await Import(h, p, "import", Csv($"{D(today)},,Credito PAG-000001,,{M(amount)}"), today, today, 0m, amount);

        var ex = await Assert.ThrowsAsync<DomainException>(async () => await h.RunAsync(
            new MatchBankLine(h.CompanyId, p.Treasurer, "return", await LineAsync(h, "Credito PAG-000001"), 1, payment, 3), new MatchBankLineHandler()));

        Assert.Equal(StatementErrors.ReturnWithoutTransfer, ex.Code);
    }

    [Trait("AcceptanceVs2", "BNK-03")]
    [Fact]
    public async Task A_bank_charge_is_posted_with_R10_at_the_line_date()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (p, _, _) = await ReleasedAsync(h);
        var today = Today(h);
        await Import(h, p, "import", Csv($"{D(today)},,Comision transferencia,150.00,", $"{D(today)},,Impuesto DGII 0.15%,15.93,", $"{D(today)},,Deposito,,50.00"), today, today, 1000m, 884.07m);
        var charge = await LineAsync(h, "Comision transferencia");
        var tax = await LineAsync(h, "Impuesto DGII 0.15%");

        var result = await h.RunAsync(new RecognizeBankCharge(h.CompanyId, p.Controller, "charge", charge, 1), new RecognizeBankChargeHandler());
        await h.RunAsync(new RecognizeBankCharge(h.CompanyId, p.Controller, "tax", tax, 1), new RecognizeBankChargeHandler());
        var credit = await Assert.ThrowsAsync<DomainException>(async () => await h.RunAsync(
            new RecognizeBankCharge(h.CompanyId, p.Controller, "credit", await LineAsync(h, "Deposito"), 1), new RecognizeBankChargeHandler()));
        var twice = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RecognizeBankCharge(h.CompanyId, p.Controller, "twice", charge, 2), new RecognizeBankChargeHandler()));
        var treasurer = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new RecognizeBankCharge(h.CompanyId, p.Treasurer, "treasurer", tax, 2), new RecognizeBankChargeHandler()));

        Assert.Contains($"\"postingDate\":\"{today:yyyy-MM-dd}\"", result.ResultPayload, StringComparison.Ordinal);
        Assert.Equal("CHARGE_RECOGNIZED:2", await h.ScalarAsync<string>($"SELECT status || ':' || version FROM fin.bank_statement_line WHERE line_id = '{charge}'"));
        // Dr BANK_CHARGES / Cr BANK (the bank account's own GL account) for the full line amount: 150.00 + 15.93.
        Assert.Equal("BANK:0.00:165.93,BANK_CHARGES:165.93:0.00", await h.ScalarAsync<string>(
            "SELECT string_agg(account_role || ':' || d || ':' || c, ',' ORDER BY account_role) FROM (SELECT account_role, sum(debit)::numeric(19,2) AS d, sum(credit)::numeric(19,2) AS c FROM fin.gl_entry WHERE rule_line_code LIKE 'R10-%' GROUP BY account_role) x"));
        Assert.Equal(p.BankAccount, await h.ScalarAsync<Guid>("SELECT DISTINCT subledger_ref FROM fin.gl_entry WHERE rule_line_code = 'R10-CR-BANK'"));
        Assert.Equal(
            (StatementErrors.LineNotDebit, StatementErrors.LineNotUnmatched, AuthorizationErrors.NotAuthorized),
            (credit.Code, twice.Code, treasurer.Code));
    }
}
