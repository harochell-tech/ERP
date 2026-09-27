using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Procurement.SupplierInvoices;
using Rochell.TestInfrastructure;
using Rochell.Treasury.Payments;
using Xunit;

namespace Rochell.Treasury.Tests;

/// <summary>VS2-04: payment reversal (PAY-08; E-VS2-04-1…7).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PaymentReversalTests(PostgresFixture postgres)
{
    private const string Reason = "Transferencia devuelta por el banco receptor";

    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static async Task<(TestPayments P, Guid Payment, decimal Amount)> ReleasedAsync(TestHarness h)
    {
        var p = await h.CreatePaymentSetupAsync();
        var amount = await h.ScalarAsync<decimal>("SELECT open_amount FROM fin.ap_document WHERE ap_doc_id = @d", ("d", p.ApDocs[0]));
        var payment = (await h.RunAsync(
            new PrepareSupplierPayment(h.CompanyId, p.Treasurer, "prepare", p.Supplier, p.BankAccount, p.PartyBankAccount, Today(h), null, [new(p.ApDocs[0], amount)]),
            new PrepareSupplierPaymentHandler())).ResultRef;
        await h.RunAsync(new ReleaseSupplierPayment(h.CompanyId, p.Controller, "release", payment, 1), new ReleaseSupplierPaymentHandler());
        return (p, payment, amount);
    }

    private static Task<CommandResult> Reverse(TestHarness h, TestPayments p, Guid payment, string key, long version, string reason = Reason, Guid? session = null)
        => h.RunAsync(new ReversePayment(h.CompanyId, session ?? p.Controller, key, payment, version, reason), new ReversePaymentHandler());

    [Trait("AcceptanceVs2", "PAY-08")]
    [Fact]
    public async Task Reversing_a_released_payment_restores_the_invoice_and_reverses_R09_exactly()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (p, payment, amount) = await ReleasedAsync(h);

        await Reverse(h, p, payment, "reverse", 2);

        Assert.Equal("REVERSED:3", await h.ScalarAsync<string>($"SELECT status || ':' || version FROM fin.payment WHERE payment_id = '{payment}'"));
        Assert.Equal(amount, await h.ScalarAsync<decimal>("SELECT open_amount FROM fin.ap_document WHERE ap_doc_id = @d", ("d", p.ApDocs[0])));
        // The reversal swaps debit and credit of every R-09 line: AP_CONTROL and BANK net to zero; no live application is left.
        Assert.Equal("AP_CONTROL:0.0000,BANK:0.0000", await h.ScalarAsync<string>(
            "SELECT string_agg(account_role || ':' || net, ',' ORDER BY account_role) FROM (SELECT account_role, sum(debit - credit) AS net FROM fin.gl_entry WHERE rule_line_code LIKE 'R09-%' GROUP BY account_role) x"));
        Assert.Equal("AUTO,REVERSAL", await h.ScalarAsync<string>(
            "SELECT string_agg(j.journal_type, ',' ORDER BY j.journal_type) FROM fin.gl_journal j JOIN fin.posting_rule r ON r.posting_rule_id = j.posting_rule_id WHERE r.code = 'R-09'"));
        Assert.Equal(0m, await h.ScalarAsync<decimal>(
            $"SELECT sum(CASE WHEN reverses_application_id IS NULL THEN amount ELSE -amount END) FROM fin.ap_application WHERE payment_id = '{payment}'"));

        // The invoice can be reversed again: VS#1 requires its AP document fully open.
        var si = await h.ScalarAsync<Guid>("SELECT si_id FROM pur.supplier_invoice");
        await h.RunAsync(new ReverseSupplierInvoice(h.CompanyId, p.Controller, "reverse-si", si, 3, "Factura anulada por el proveedor"), new ReverseSupplierInvoiceHandler());
        Assert.Equal("REVERSED", await h.ScalarAsync<string>($"SELECT document_status::text FROM pur.supplier_invoice WHERE si_id = '{si}'"));
    }

    [Fact]
    public async Task A_cleared_payment_is_reversed_and_its_statement_line_stays_matched()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (p, payment, amount) = await ReleasedAsync(h);
        var statement = Guid.CreateVersion7();
        var line = Guid.CreateVersion7();
        // E-VS2-04-6: matching arrives in VS2-05; the fixture clears the payment against a DEBIT line.
        await h.RunAsync(
            new TestTreasurySql(
                h.CompanyId, h.SessionId, "clear",
                $"""
                INSERT INTO fin.bank_statement VALUES ('{statement}', '{h.CompanyId}', '{p.BankAccount}', current_date - 5, current_date, 100000.00, {100000m - amount:0.00}, sha256('x'::bytea), @user, now());
                INSERT INTO fin.bank_statement_line (line_id, company_id, statement_id, bank_account_id, value_date, direction, amount, description, occurrence, status, version)
                VALUES ('{line}', '{h.CompanyId}', '{statement}', '{p.BankAccount}', current_date, 'DEBIT', {amount:0.00}, 'PAG-000001', 1, 'UNMATCHED', 1);
                UPDATE fin.bank_statement_line SET status = 'MATCHED', matched_payment_id = '{payment}', version = 2 WHERE line_id = '{line}';
                UPDATE fin.payment SET status = 'CLEARED', version = 3 WHERE payment_id = '{payment}';
                """,
                [new TestState("BankStatementLine", line, "UNMATCHED", "MATCHED"), new TestState("Payment", payment, "RELEASED", "CLEARED")]),
            new TestTreasurySqlHandler());

        await Reverse(h, p, payment, "reverse", 3);

        Assert.Equal("REVERSED", await h.ScalarAsync<string>($"SELECT status::text FROM fin.payment WHERE payment_id = '{payment}'"));
        Assert.Equal($"MATCHED:{payment}", await h.ScalarAsync<string>($"SELECT status || ':' || matched_payment_id FROM fin.bank_statement_line WHERE line_id = '{line}'"));
        Assert.Equal(Reason, await h.ScalarAsync<string>($"SELECT reason FROM core.state_history WHERE aggregate_id = '{payment}' AND to_state = 'REVERSED'"));
    }

    [Fact]
    public async Task Only_released_or_cleared_payments_are_reversed_with_a_real_reason_by_the_controller()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (p, payment, _) = await ReleasedAsync(h);
        var fullyPaid = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareSupplierPayment(h.CompanyId, p.Treasurer, "prepared", p.Supplier, p.BankAccount, p.PartyBankAccount, Today(h), null, [new(p.ApDocs[0], 1.00m)]),
            new PrepareSupplierPaymentHandler()));

        var shortReason = await Assert.ThrowsAsync<DomainException>(() => Reverse(h, p, payment, "short", 2, "Error"));
        var stale = await Assert.ThrowsAsync<DomainException>(() => Reverse(h, p, payment, "stale", 1));
        var treasurer = await Assert.ThrowsAsync<DomainException>(() => Reverse(h, p, payment, "treasurer", 2, session: p.Treasurer));
        await Reverse(h, p, payment, "reverse", 2);
        var twice = await Assert.ThrowsAsync<DomainException>(() => Reverse(h, p, payment, "again", 3));

        Assert.Equal(PaymentErrors.ApplicationExceedsOpenAmount, fullyPaid.Code); // fully paid until the reversal
        Assert.Equal(
            (PaymentErrors.ReasonRequired, PaymentErrors.VersionConflict, AuthorizationErrors.NotAuthorized, PaymentErrors.NotReversible),
            (shortReason.Code, stale.Code, treasurer.Code, twice.Code));
    }

    [Fact]
    public async Task A_prepared_payment_is_voided_not_reversed()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePaymentSetupAsync();
        var payment = (await h.RunAsync(
            new PrepareSupplierPayment(h.CompanyId, p.Treasurer, "prepare", p.Supplier, p.BankAccount, p.PartyBankAccount, Today(h), null, [new(p.ApDocs[0], 100.00m)]),
            new PrepareSupplierPaymentHandler())).ResultRef;

        var ex = await Assert.ThrowsAsync<DomainException>(() => Reverse(h, p, payment, "reverse", 1));

        Assert.Equal(PaymentErrors.NotReversible, ex.Code);
    }

    [Fact]
    public async Task The_reversal_posts_late_when_BANK_REC_is_closed_for_today()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (p, payment, _) = await ReleasedAsync(h);
        await h.SetComponentAsync(Today(h), "BANK-REC", "CLOSED");

        var result = await Reverse(h, p, payment, "reverse", 2);

        var nextMonth = new DateOnly(Today(h).Year, Today(h).Month, 1).AddMonths(1);
        Assert.Contains($"\"postingDate\":\"{nextMonth:yyyy-MM-dd}\"", result.ResultPayload, StringComparison.Ordinal);
        Assert.Contains("\"lateEntry\":true", result.ResultPayload, StringComparison.Ordinal);
    }
}
