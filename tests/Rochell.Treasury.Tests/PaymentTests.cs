using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Rochell.Treasury.Payments;
using Xunit;

namespace Rochell.Treasury.Tests;

/// <summary>VS2-03: supplier payments — prepare, update, void, release with R-09 (PAY-01…07, PAY-09; E-VS2-03-1…9).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PaymentTests(PostgresFixture postgres)
{
    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static Task<decimal> OpenAsync(TestHarness h, Guid apDoc)
        => h.ScalarAsync<decimal>("SELECT open_amount FROM fin.ap_document WHERE ap_doc_id = @d", ("d", apDoc));

    private static async Task<Guid> Prepare(TestHarness h, TestPayments p, string key, params PaymentApplication[] applications)
        => (await h.RunAsync(
            new PrepareSupplierPayment(h.CompanyId, p.Treasurer, key, p.Supplier, p.BankAccount, p.PartyBankAccount, Today(h), null, applications),
            new PrepareSupplierPaymentHandler())).ResultRef;

    private static Task<CommandResult> Release(TestHarness h, TestPayments p, Guid payment, string key, long version = 1, Guid? session = null)
        => h.RunAsync(new ReleaseSupplierPayment(h.CompanyId, session ?? p.Controller, key, payment, version), new ReleaseSupplierPaymentHandler());

    private static Task<string?> JournalLinesAsync(TestHarness h, Guid payment)
        => h.ScalarAsync<string>(
            """
            SELECT string_agg(e.rule_line_code || ':' || e.account_role || ':' || e.debit || ':' || e.credit, ',' ORDER BY e.line_no)
            FROM fin.payment p JOIN fin.gl_entry e ON e.source_event_id = p.posting_event_id WHERE p.payment_id = @p
            """,
            ("p", payment));

    [Trait("AcceptanceVs2", "PAY-01")]
    [Fact]
    public async Task Paying_an_invoice_in_full_clears_its_balance_and_posts_R09()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePaymentSetupAsync();
        var open = await OpenAsync(h, p.ApDocs[0]);

        var payment = await Prepare(h, p, "prepare", new PaymentApplication(p.ApDocs[0], open));
        await Release(h, p, payment, "release");

        Assert.Equal(0m, await OpenAsync(h, p.ApDocs[0]));
        Assert.Equal("RELEASED:PAG-000001:2", await h.ScalarAsync<string>($"SELECT status || ':' || payment_no || ':' || version FROM fin.payment WHERE payment_id = '{payment}'"));
        Assert.Equal($"R09-DR-AP:AP_CONTROL:{open:0.0000}:0.0000,R09-CR-BANK:BANK:0.0000:{open:0.0000}", await JournalLinesAsync(h, payment));
        // AP-GL: the supplier's AP_CONTROL balance equals its open AP documents (0); BANK-GL: the bank's GL account is credited.
        Assert.Equal("0.0000|0.0000", await h.ScalarAsync<string>(
            """
            SELECT (SELECT sum(credit - debit) FROM fin.gl_entry WHERE account_role = 'AP_CONTROL') || '|' || (SELECT sum(open_amount) FROM fin.ap_document)
            """));
        Assert.Equal(open, await h.ScalarAsync<decimal>(
            $"SELECT sum(e.credit) FROM fin.gl_entry e JOIN fin.bank_account b ON b.gl_account_id = e.account_id WHERE b.bank_account_id = '{p.BankAccount}'"));
    }

    [Trait("AcceptanceVs2", "PAY-02")]
    [Fact]
    public async Task One_partial_payment_covers_the_first_invoice_and_part_of_the_second()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePaymentSetupAsync(invoices: 2);
        var first = await OpenAsync(h, p.ApDocs[0]);
        var second = await OpenAsync(h, p.ApDocs[1]);

        var payment = await Prepare(h, p, "prepare", new PaymentApplication(p.ApDocs[0], first), new PaymentApplication(p.ApDocs[1], 1000.00m));
        await Release(h, p, payment, "release");

        Assert.Equal((0m, second - 1000m), (await OpenAsync(h, p.ApDocs[0]), await OpenAsync(h, p.ApDocs[1])));
        var lines = await JournalLinesAsync(h, payment);
        Assert.Equal(2, lines!.Split(',').Count(l => l.StartsWith("R09-DR-AP", StringComparison.Ordinal)));
        Assert.EndsWith($"R09-CR-BANK:BANK:0.0000:{first + 1000m:0.0000}", lines, StringComparison.Ordinal);
    }

    [Trait("AcceptanceVs2", "PAY-03")]
    [Fact]
    public async Task An_application_above_the_open_amount_is_refused_and_nothing_is_written()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePaymentSetupAsync();
        var open = await OpenAsync(h, p.ApDocs[0]);

        var ex = await Assert.ThrowsAsync<DomainException>(() => Prepare(h, p, "too-much", new PaymentApplication(p.ApDocs[0], open + 0.01m)));

        Assert.Equal(PaymentErrors.ApplicationExceedsOpenAmount, ex.Code);
        Assert.Equal(0L, await h.CountAsync("fin.payment"));
    }

    [Trait("AcceptanceVs2", "PAY-04")]
    [Fact]
    public async Task The_preparer_cannot_release()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePaymentSetupAsync();
        var payment = await Prepare(h, p, "prepare", new PaymentApplication(p.ApDocs[0], 100.00m));

        // The treasurer holds payment:prepare, never payment:release (SoD, VS#2 §7); the CHECK half is in BankSchemaTests.
        var ex = await Assert.ThrowsAsync<DomainException>(() => Release(h, p, payment, "self", session: p.Treasurer));

        Assert.Equal(AuthorizationErrors.NotAuthorized, ex.Code);
        Assert.Equal("PREPARED", await h.ScalarAsync<string>($"SELECT status::text FROM fin.payment WHERE payment_id = '{payment}'"));
    }

    [Trait("AcceptanceVs2", "PAY-05")]
    [Fact]
    public async Task Two_payments_of_the_same_invoice_released_at_once_never_leave_a_negative_balance()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePaymentSetupAsync();
        var open = await OpenAsync(h, p.ApDocs[0]);
        var first = await Prepare(h, p, "first", new PaymentApplication(p.ApDocs[0], open));
        var second = await Prepare(h, p, "second", new PaymentApplication(p.ApDocs[0], open)); // preparing reserves nothing (E-VS2-6)

        var results = await Task.WhenAll(
            Record.ExceptionAsync(() => Release(h, p, first, "release-first")),
            Record.ExceptionAsync(() => Release(h, p, second, "release-second")));

        var failure = Assert.Single(results.OfType<DomainException>());
        Assert.Single(results, r => r is null);
        Assert.Equal(PaymentErrors.ApplicationExceedsOpenAmount, failure.Code);
        Assert.Equal(0m, await OpenAsync(h, p.ApDocs[0]));
        Assert.Equal("PREPARED,RELEASED", await h.ScalarAsync<string>("SELECT string_agg(status::text, ',' ORDER BY status::text) FROM fin.payment"));
    }

    [Trait("AcceptanceVs2", "PAY-06")]
    [Theory]
    [InlineData(71, false)]
    [InlineData(73, true)]
    public async Task A_transfer_is_released_only_72_hours_after_the_supplier_account_was_verified(int verifiedHoursAgo, bool released)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePaymentSetupAsync(verifiedHoursAgo: verifiedHoursAgo);
        var payment = await Prepare(h, p, "prepare", new PaymentApplication(p.ApDocs[0], 100.00m)); // VERIFIED is enough to prepare

        var error = await Record.ExceptionAsync(() => Release(h, p, payment, "release"));

        Assert.Equal(released, error is null);
        Assert.Equal(released ? null : PaymentErrors.PartyBankAccountNotPayable, (error as DomainException)?.Code);
    }

    [Trait("AcceptanceVs2", "PAY-07")]
    [Fact]
    public async Task A_new_unverified_account_is_refused_and_a_superseded_one_stops_paying()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePaymentSetupAsync();
        var requested = Guid.CreateVersion7();
        await h.RunAsync(
            new TestTreasurySql(
                h.CompanyId, h.SessionId, "request-new",
                $"""
                INSERT INTO md.party_bank_account (party_bank_account_id, company_id, party_id, version, bank_code, account_number, account_holder, status, requested_by, requested_at)
                VALUES ('{requested}', '{h.CompanyId}', '{p.Supplier}', 2, 'BHD', '1111122222', 'Proveedor', 'REVIEW', @user, now())
                """,
                [new TestState("PartyBankAccount", requested, null, "REVIEW")]),
            new TestTreasurySqlHandler());
        var toNew = await Record.ExceptionAsync(() => h.RunAsync(
            new PrepareSupplierPayment(h.CompanyId, p.Treasurer, "to-new", p.Supplier, p.BankAccount, requested, Today(h), null, [new(p.ApDocs[0], 100.00m)]),
            new PrepareSupplierPaymentHandler()));
        var toOld = await Prepare(h, p, "to-old", new PaymentApplication(p.ApDocs[0], 100.00m)); // the old account still pays

        // A newer version is verified: the old one is SUPERSEDED before the payment to it is released.
        var rejecter = await h.ScalarAsync<Guid>("SELECT user_id FROM iam.session WHERE session_id = @s", ("s", p.Controller));
        await h.RunAsync(
            new TestTreasurySql(
                h.CompanyId, h.SessionId, "reject-new",
                $"UPDATE md.party_bank_account SET status = 'REJECTED', rejected_by = '{rejecter}', rejected_at = now(), rejection_reason = 'Cuenta equivocada' WHERE party_bank_account_id = '{requested}'",
                [new TestState("PartyBankAccount", requested, "REVIEW", "REJECTED")]),
            new TestTreasurySqlHandler());
        await h.VerifiedPartyBankAccountAsync(p.Supplier, p.Treasurer, p.Controller, 3, "3333344444", hoursAgo: 1);
        var release = await Record.ExceptionAsync(() => Release(h, p, toOld, "release-old"));

        Assert.Equal(PaymentErrors.PartyBankAccountNotVerified, (toNew as DomainException)?.Code);
        Assert.Equal(PaymentErrors.PartyBankAccountNotPayable, (release as DomainException)?.Code);
    }

    [Trait("AcceptanceVs2", "PAY-09")]
    [Fact]
    public async Task Release_needs_a_recent_reauthentication()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var p = await h.CreatePaymentSetupAsync();
        var payment = await Prepare(h, p, "prepare", new PaymentApplication(p.ApDocs[0], 100.00m));
        clock.Advance(TimeSpan.FromMinutes(10)); // the sign-in step-up is older than 5 minutes; the session is still open

        var ex = await Assert.ThrowsAsync<DomainException>(() => Release(h, p, payment, "release"));

        Assert.Equal(AuthorizationErrors.StepUpRequired, ex.Code);
    }

    [Fact]
    public async Task A_prepared_payment_is_updated_as_a_whole_voided_with_a_reason_and_then_frozen()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePaymentSetupAsync(invoices: 2);
        var payment = await Prepare(h, p, "prepare", new PaymentApplication(p.ApDocs[0], 100.00m));

        await h.RunAsync(
            new UpdatePreparedPayment(h.CompanyId, p.Treasurer, "update", payment, 1, p.BankAccount, p.PartyBankAccount, Today(h), "TRF-77",
                [new(p.ApDocs[0], 200.00m), new(p.ApDocs[1], 50.00m)]),
            new UpdatePreparedPaymentHandler());
        var stale = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new VoidPayment(h.CompanyId, p.Treasurer, "void-stale", payment, 1, "Error"), new VoidPaymentHandler()));
        var noReason = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new VoidPayment(h.CompanyId, p.Treasurer, "void-empty", payment, 2, " "), new VoidPaymentHandler()));
        await h.RunAsync(new VoidPayment(h.CompanyId, p.Treasurer, "void", payment, 2, "El proveedor pidió otra forma de pago"), new VoidPaymentHandler());
        var release = await Assert.ThrowsAsync<DomainException>(() => Release(h, p, payment, "release", version: 3));

        Assert.Equal("VOIDED:250.0000:TRF-77:3", await h.ScalarAsync<string>($"SELECT status || ':' || amount || ':' || bank_reference || ':' || version FROM fin.payment WHERE payment_id = '{payment}'"));
        Assert.Equal("1:100.0000,2:200.0000,2:50.0000", await h.ScalarAsync<string>(
            $"SELECT string_agg(payment_version || ':' || amount, ',' ORDER BY payment_version, amount DESC) FROM fin.payment_allocation WHERE payment_id = '{payment}'"));
        Assert.Equal((PaymentErrors.VersionConflict, PaymentErrors.ReasonRequired, PaymentErrors.NotPrepared), (stale.Code, noReason.Code, release.Code));
    }

    [Fact]
    public async Task Amounts_and_value_dates_are_checked_when_preparing_and_releasing()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePaymentSetupAsync();
        Task<CommandResult> PrepareOn(string key, DateOnly date, decimal amount)
            => h.RunAsync(new PrepareSupplierPayment(h.CompanyId, p.Treasurer, key, p.Supplier, p.BankAccount, p.PartyBankAccount, date, null, [new(p.ApDocs[0], amount)]), new PrepareSupplierPaymentHandler());

        var decimals = await Assert.ThrowsAsync<DomainException>(() => PrepareOn("decimals", Today(h), 100.005m));
        var beforeInvoice = await Assert.ThrowsAsync<DomainException>(() => PrepareOn("before", Today(h).AddDays(-1), 100.00m));
        var future = (await PrepareOn("future", Today(h).AddDays(3), 100.00m)).ResultRef;
        var releaseFuture = await Assert.ThrowsAsync<DomainException>(() => Release(h, p, future, "release-future"));
        var empty = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareSupplierPayment(h.CompanyId, p.Treasurer, "empty", p.Supplier, p.BankAccount, p.PartyBankAccount, Today(h), null, []), new PrepareSupplierPaymentHandler()));

        Assert.Equal(
            (PaymentErrors.AmountInvalid, PaymentErrors.ValueDateBeforeInvoice, PaymentErrors.ValueDateInFuture, PaymentErrors.ApplicationsRequired),
            (decimals.Code, beforeInvoice.Code, releaseFuture.Code, empty.Code));
    }

    [Fact]
    public async Task R09_waits_for_both_BANK_REC_and_AP_REC_open_and_posts_late_otherwise()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePaymentSetupAsync();
        var payment = await Prepare(h, p, "prepare", new PaymentApplication(p.ApDocs[0], 100.00m));
        await h.SetComponentAsync(Today(h), "AP-REC", "CLOSED"); // BANK-REC stays open: AP-REC alone forces late entry

        var result = await Release(h, p, payment, "release");

        var nextMonth = new DateOnly(Today(h).Year, Today(h).Month, 1).AddMonths(1);
        Assert.Equal($"{nextMonth:yyyy-MM-dd}|true", await h.ScalarAsync<string>(
            $"SELECT j.posting_date || '|' || j.late_entry FROM fin.gl_journal j JOIN fin.payment p ON p.posting_event_id = j.source_event_id WHERE p.payment_id = '{payment}'"));
        Assert.Contains("\"lateEntry\":true", result.ResultPayload, StringComparison.Ordinal);
    }

    /// <summary>
    /// After release (E-VS2-01-11): RELEASED → CLEARED → RELEASED (unmatch); frozen, never voided, never deleted; REVERSED only with
    /// its reversal rows and journal (ReversePayment, VS2-04), never by a bare status change.
    /// </summary>
    [Fact]
    public async Task A_released_payment_follows_its_state_machine_and_is_frozen()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePaymentSetupAsync();
        var payment = await Prepare(h, p, "prepare", new PaymentApplication(p.ApDocs[0], 100.00m));
        await Release(h, p, payment, "release");
        async Task<string?> Step(string key, string sql, params TestState[] states)
        {
            var ex = await Record.ExceptionAsync(() => h.RunAsync(new TestTreasurySql(h.CompanyId, h.SessionId, key, sql, states), new TestTreasurySqlHandler()));
            return ex is Npgsql.PostgresException pg ? pg.SqlState : ex?.GetType().Name;
        }

        TestState To(string from, string to) => new("Payment", payment, from, to);
        var edit = await Step("edit", $"UPDATE fin.payment SET amount = 1.00, version = 3 WHERE payment_id = '{payment}'");
        var @void = await Step("void", $"UPDATE fin.payment SET status = 'VOIDED', version = 3 WHERE payment_id = '{payment}'", To("RELEASED", "VOIDED"));
        // VS2-05: CLEARED needs its matched statement line (MatchBankLine, BankStatementTests); REVERSED needs the R-09 reversal (VS2-04).
        var bareClear = await Step("clear", $"UPDATE fin.payment SET status = 'CLEARED', version = 3 WHERE payment_id = '{payment}'", To("RELEASED", "CLEARED"));
        var bareReverse = await Step("reverse", $"UPDATE fin.payment SET status = 'REVERSED', version = 3 WHERE payment_id = '{payment}'", To("RELEASED", "REVERSED"));
        var delete = await h.AppExecuteAsync($"DELETE FROM fin.payment WHERE payment_id = '{payment}'");

        Assert.Equal(("P0001", "P0001"), (edit, @void));
        Assert.Equal(("P0001", "P0001"), (bareClear, bareReverse));
        Assert.Equal("42501", delete?.SqlState);
        Assert.Equal("PREPARED,RELEASED", await h.ScalarAsync<string>(
            $"SELECT string_agg(h.to_state, ',' ORDER BY h.xmin::text::bigint) FROM core.state_history h WHERE h.aggregate_id = '{payment}'"));
    }
}
