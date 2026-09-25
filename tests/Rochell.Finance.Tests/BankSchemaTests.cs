using Npgsql;
using Rochell.Platform.Data;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Finance.Tests;

/// <summary>
/// VS2-01 schema guarantees (VS#2 §2, §4, §7; E-VS2-01-1…14): bank accounts, supplier bank accounts, payments, applications and
/// statements, written as the application role. The commands arrive in VS2-02…05; here a fixture command writes the rows.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class BankSchemaTests(PostgresFixture postgres)
{
    private const string InsufficientPrivilege = "42501";
    private const string Evidence = "Llamada al 809-555-0100 el 2026-09-25, confirmó Ana Pérez";

    private sealed record Bank(Guid GlAccount, Guid BankAccount, Guid Supplier, Guid Verifier);

    private static async Task Run(TestHarness h, string key, string sql, params TestState[] states)
        => await h.RunAsync(new TestTreasurySql(h.CompanyId, h.SessionId, key, sql, states), new TestTreasurySqlHandler());

    private static async Task<string?> Fails(TestHarness h, string key, string sql, params TestState[] states)
    {
        var ex = await Record.ExceptionAsync(() => Run(h, key, sql, states));
        return ex is PostgresException pg ? pg.SqlState : ex?.GetType().Name;
    }

    private static async Task<Bank> BankAsync(TestHarness h)
    {
        var gl = await h.CreateAccountAsync("1101", "Banco de prueba", isControl: true);
        var supplier = await h.CreateActiveSupplierAsync("101000002", "Proveedor de prueba");
        var bank = Guid.CreateVersion7();
        await Run(h, "bank",
            $"INSERT INTO fin.bank_account VALUES ('{bank}', '{h.CompanyId}', 'BPD', '0123456789', 'DOP', '{gl}', 'ACTIVE', 1)",
            new TestState("BankAccount", bank, null, "ACTIVE"));
        return new Bank(gl, bank, supplier, await h.CreateUserAsync());
    }

    private static string RequestSql(TestHarness h, Guid id, Guid supplier, int version, string number = "9876543210")
        => $"""
           INSERT INTO md.party_bank_account (party_bank_account_id, company_id, party_id, version, bank_code, account_number, account_holder,
             status, requested_by, requested_at)
           VALUES ('{id}', '{h.CompanyId}', '{supplier}', {version}, 'BHD', '{number}', 'Proveedor de prueba', 'REVIEW', @user, now() - interval '1 hour')
           """;

    private static string VerifySql(Guid id, Guid verifier, string evidence = Evidence, string hold = "72 hours")
        => $"""
           UPDATE md.party_bank_account SET status = 'VERIFIED', verified_by = '{verifier}', verified_at = now(),
             verification_evidence = '{evidence}', payable_from = now() + interval '{hold}'
           WHERE party_bank_account_id = '{id}'
           """;

    private static async Task<Guid> VerifiedAccountAsync(TestHarness h, Bank b, Guid? supplier = null, int version = 1)
    {
        var id = Guid.CreateVersion7();
        await Run(h, "request-" + id, RequestSql(h, id, supplier ?? b.Supplier, version), new TestState("PartyBankAccount", id, null, "REVIEW"));
        await Run(h, "verify-" + id, VerifySql(id, b.Verifier), new TestState("PartyBankAccount", id, "REVIEW", "VERIFIED"));
        return id;
    }

    private static string PrepareSql(TestHarness h, Bank b, Guid payment, Guid partyAccount, Guid? supplier = null, string method = "TRANSFER")
        => $"""
           INSERT INTO fin.payment (payment_id, company_id, direction, party_id, bank_account_id, party_bank_account_id, method, amount, currency,
             value_date, status, prepared_by, version)
           VALUES ('{payment}', '{h.CompanyId}', 'DISBURSEMENT', '{supplier ?? b.Supplier}', '{b.BankAccount}', '{partyAccount}', '{method}', 45040.00, 'DOP',
             current_date, 'PREPARED', @user, 1)
           """;

    private static async Task<Guid> PreparedPaymentAsync(TestHarness h, Bank b, Guid partyAccount)
    {
        var payment = Guid.CreateVersion7();
        await Run(h, "prepare-" + payment, PrepareSql(h, b, payment, partyAccount), new TestState("Payment", payment, null, "PREPARED"));
        return payment;
    }

    private static string ReleaseSql(Guid payment, Guid releaser)
        => $"UPDATE fin.payment SET status = 'RELEASED', released_by = '{releaser}', posting_event_id = @event, version = 2 WHERE payment_id = '{payment}'";

    [Fact]
    public async Task Bank_account_formats_and_one_control_account_per_bank_account()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var b = await BankAsync(h);
        var other = await h.CreateAccountAsync("1102", "Otro banco", isControl: true);
        var plain = await h.CreateAccountAsync("1103", "No control", isControl: false);
        var mapped = await h.CreateAccountAsync("2101", "Cuentas por pagar", isControl: true);
        await h.CreateActiveMapAsync("AP_CONTROL", mapped);
        string Insert(string code, string number, Guid gl)
            => $"INSERT INTO fin.bank_account VALUES ('{Guid.CreateVersion7()}', '{h.CompanyId}', '{code}', '{number}', 'DOP', '{gl}', 'ACTIVE', 1)";

        // E-VS2-01-3: normalized bank code and digits-only account number.
        Assert.Equal(SqlStates.CheckViolation, (await h.AppExecuteAsync(Insert("bpd", "0123456780", other)))?.SqlState);
        Assert.Equal(SqlStates.CheckViolation, (await h.AppExecuteAsync(Insert("B", "0123456780", other)))?.SqlState);
        Assert.Equal(SqlStates.CheckViolation, (await h.AppExecuteAsync(Insert("BPD", "012-345-678", other)))?.SqlState);
        Assert.Equal(SqlStates.CheckViolation, (await h.AppExecuteAsync(Insert("BPD", "1234", other)))?.SqlState);
        // E-VS2-01-1: a control account, not shared, not in the role map (either order).
        Assert.Equal(SqlStates.RaiseException, (await h.AppExecuteAsync(Insert("BPD", "0123456780", plain)))?.SqlState);
        Assert.Equal(SqlStates.UniqueViolation, (await h.AppExecuteAsync(Insert("BPD", "0123456780", b.GlAccount)))?.SqlState);
        Assert.Equal(SqlStates.RaiseException, (await h.AppExecuteAsync(Insert("BPD", "0123456780", mapped)))?.SqlState);
        Assert.Equal(SqlStates.RaiseException, (await Assert.ThrowsAsync<PostgresException>(() => h.CreateActiveMapAsync("AP_CONTROL", b.GlAccount))).SqlState);
        Assert.Equal(SqlStates.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => h.CreateActiveMapAsync("BANK", other))).SqlState);
        // Duplicate bank and number.
        Assert.Equal(SqlStates.UniqueViolation, (await h.AppExecuteAsync(Insert("BPD", "0123456789", other)))?.SqlState);
    }

    [Fact]
    public async Task Status_changes_need_their_state_history_row()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var b = await BankAsync(h);
        var gl = await h.CreateAccountAsync("1102", "Otro banco", isControl: true);

        // ADR-027: at COMMIT, without the fixture's state_history row.
        var insert = await h.AppExecuteAsync($"INSERT INTO fin.bank_account VALUES ('{Guid.CreateVersion7()}', '{h.CompanyId}', 'BHD', '555666777', 'DOP', '{gl}', 'ACTIVE', 1)");
        var close = await h.AppExecuteAsync($"UPDATE fin.bank_account SET status = 'CLOSED', version = 2 WHERE bank_account_id = '{b.BankAccount}'");
        await Run(h, "close", $"UPDATE fin.bank_account SET status = 'CLOSED', version = 2 WHERE bank_account_id = '{b.BankAccount}'",
            new TestState("BankAccount", b.BankAccount, "ACTIVE", "CLOSED"));
        var reopen = await Fails(h, "reopen", $"UPDATE fin.bank_account SET status = 'ACTIVE', version = 3 WHERE bank_account_id = '{b.BankAccount}'",
            new TestState("BankAccount", b.BankAccount, "CLOSED", "ACTIVE"));
        var identity = await h.AppExecuteAsync($"UPDATE fin.bank_account SET gl_account_id = '{gl}' WHERE bank_account_id = '{b.BankAccount}'");

        Assert.Equal(SqlStates.RaiseException, insert?.SqlState);
        Assert.Equal(SqlStates.RaiseException, close?.SqlState);
        Assert.Equal(SqlStates.RaiseException, reopen);
        Assert.Equal(InsufficientPrivilege, identity?.SqlState);
        Assert.Equal("CLOSED:2", await h.ScalarAsync<string>("SELECT status || ':' || version FROM fin.bank_account"));
    }

    /// <summary>PAY-10 (CHECK half; the command half comes with VerifyPartyBankAccount in VS2-02): the requester cannot verify.</summary>
    [Trait("AcceptanceVs2", "PAY-10")]
    [Fact]
    public async Task Requester_cannot_verify_their_own_supplier_bank_account()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var b = await BankAsync(h);
        var account = Guid.CreateVersion7();
        await Run(h, "request", RequestSql(h, account, b.Supplier, 1), new TestState("PartyBankAccount", account, null, "REVIEW"));

        var self = await Fails(h, "self-verify",
            $"UPDATE md.party_bank_account SET status = 'VERIFIED', verified_by = @user, verified_at = now(), verification_evidence = '{Evidence}', "
            + $"payable_from = now() + interval '72 hours' WHERE party_bank_account_id = '{account}'",
            new TestState("PartyBankAccount", account, "REVIEW", "VERIFIED"));

        Assert.Equal(SqlStates.CheckViolation, self);
        Assert.Equal("REVIEW", await h.ScalarAsync<string>("SELECT status FROM md.party_bank_account"));
    }

    [Theory]
    [InlineData("Llamé al proveedor.", "72 hours")]          // E-VS2-01-4: evidence under 20 characters
    [InlineData(Evidence, "71 hours")]                       // E-VS2-8: 72 calendar hours from the verification
    [InlineData(Evidence, "0 hours")]
    public async Task Verification_needs_evidence_and_the_72_hour_hold(string evidence, string hold)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var b = await BankAsync(h);
        var account = Guid.CreateVersion7();
        await Run(h, "request", RequestSql(h, account, b.Supplier, 1), new TestState("PartyBankAccount", account, null, "REVIEW"));

        Assert.Equal(SqlStates.CheckViolation, await Fails(h, "verify", VerifySql(account, b.Verifier, evidence, hold),
            new TestState("PartyBankAccount", account, "REVIEW", "VERIFIED")));
    }

    [Fact]
    public async Task A_new_verified_version_supersedes_the_old_one_and_keeps_its_verification()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var b = await BankAsync(h);
        var v1 = await VerifiedAccountAsync(h, b);
        var v2 = Guid.CreateVersion7();
        var v3 = Guid.CreateVersion7();
        await Run(h, "request-v2", RequestSql(h, v2, b.Supplier, 2, "1111122222"), new TestState("PartyBankAccount", v2, null, "REVIEW"));

        // E-VS2-01-9: one REVIEW per supplier; one VERIFIED per supplier; SUPERSEDED only by a newer version verified in the same transaction.
        var secondReview = await Fails(h, "request-v3", RequestSql(h, v3, b.Supplier, 3, "3333344444"), new TestState("PartyBankAccount", v3, null, "REVIEW"));
        var twoVerified = await Fails(h, "verify-v2-alone", VerifySql(v2, b.Verifier), new TestState("PartyBankAccount", v2, "REVIEW", "VERIFIED"));
        var supersededAlone = await Fails(h, "supersede-alone", $"UPDATE md.party_bank_account SET status = 'SUPERSEDED' WHERE party_bank_account_id = '{v1}'",
            new TestState("PartyBankAccount", v1, "VERIFIED", "SUPERSEDED"));
        await Run(h, "verify-v2",
            $"UPDATE md.party_bank_account SET status = 'SUPERSEDED' WHERE party_bank_account_id = '{v1}'; " + VerifySql(v2, b.Verifier),
            new TestState("PartyBankAccount", v1, "VERIFIED", "SUPERSEDED"), new TestState("PartyBankAccount", v2, "REVIEW", "VERIFIED"));
        var rewrite = await Fails(h, "rewrite", $"UPDATE md.party_bank_account SET verification_evidence = '{Evidence} (editado)' WHERE party_bank_account_id = '{v1}'");

        Assert.Equal(SqlStates.UniqueViolation, secondReview);
        Assert.Equal(SqlStates.UniqueViolation, twoVerified);
        Assert.Equal(SqlStates.RaiseException, supersededAlone);
        Assert.Equal(SqlStates.RaiseException, rewrite);
        // E-VS2-01-8: the superseded version keeps who verified it, when, with what evidence.
        Assert.Equal($"1:SUPERSEDED:{b.Verifier}:true,2:VERIFIED:{b.Verifier}:true", await h.ScalarAsync<string>(
            "SELECT string_agg(version || ':' || status || ':' || verified_by || ':' || (payable_from = verified_at + interval '72 hours'), ',' ORDER BY version) FROM md.party_bank_account"));
    }

    [Fact]
    public async Task A_rejection_records_who_when_and_why()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var b = await BankAsync(h);
        var account = Guid.CreateVersion7();
        await Run(h, "request", RequestSql(h, account, b.Supplier, 1), new TestState("PartyBankAccount", account, null, "REVIEW"));
        string Reject(string rejecter, string reason)
            => $"UPDATE md.party_bank_account SET status = 'REJECTED', rejected_by = {rejecter}, rejected_at = now(), rejection_reason = {reason} WHERE party_bank_account_id = '{account}'";
        var state = new TestState("PartyBankAccount", account, "REVIEW", "REJECTED");

        // E-VS2-01-10.
        Assert.Equal(SqlStates.CheckViolation, await Fails(h, "no-reason", Reject($"'{b.Verifier}'", "' '"), state));
        Assert.Equal(SqlStates.CheckViolation, await Fails(h, "self", Reject("@user", "'Cuenta no coincide'"), state));
        await Run(h, "reject", Reject($"'{b.Verifier}'", "'El titular no coincide con el proveedor'"), state);
        Assert.Equal(SqlStates.RaiseException, await Fails(h, "verify-rejected", VerifySql(account, b.Verifier), new TestState("PartyBankAccount", account, "REJECTED", "VERIFIED")));
    }

    /// <summary>PAY-04 (CHECK half): the preparer cannot release. E-VS2-01-14: transfer to an account of the same supplier.</summary>
    [Trait("AcceptanceVs2", "PAY-04")]
    [Fact]
    public async Task Payment_four_eyes_method_and_supplier_account()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var b = await BankAsync(h);
        var account = await VerifiedAccountAsync(h, b);
        var otherSupplier = await h.CreateActiveSupplierAsync("101000003", "Otro proveedor");
        var otherAccount = await VerifiedAccountAsync(h, b, otherSupplier);
        var payment = await PreparedPaymentAsync(h, b, account);
        Guid Id() => Guid.CreateVersion7();

        var cheque = await Fails(h, "cheque", PrepareSql(h, b, Id(), account, method: "CHEQUE"));
        var foreignAccount = await Fails(h, "foreign", PrepareSql(h, b, Id(), otherAccount));
        var selfRelease = await Fails(h, "self-release",
            $"UPDATE fin.payment SET status = 'RELEASED', released_by = @user, posting_event_id = @event, version = 2 WHERE payment_id = '{payment}'",
            new TestState("Payment", payment, "PREPARED", "RELEASED"));
        var releasedWithoutEvent = await Fails(h, "no-event",
            $"UPDATE fin.payment SET status = 'RELEASED', released_by = '{b.Verifier}', version = 2 WHERE payment_id = '{payment}'",
            new TestState("Payment", payment, "PREPARED", "RELEASED"));

        Assert.Equal(SqlStates.CheckViolation, cheque);
        Assert.Equal(SqlStates.ForeignKeyViolation, foreignAccount);
        Assert.Equal(SqlStates.CheckViolation, selfRelease);
        Assert.Equal(SqlStates.CheckViolation, releasedWithoutEvent);
        Assert.Equal("PREPARED", await h.ScalarAsync<string>($"SELECT status::text FROM fin.payment WHERE payment_id = '{payment}'"));
    }

    [Fact]
    public async Task Payment_follows_its_state_machine_and_freezes_after_preparation()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var b = await BankAsync(h);
        var account = await VerifiedAccountAsync(h, b);
        var payment = await PreparedPaymentAsync(h, b, account);
        TestState To(string from, string to) => new("Payment", payment, from, to);

        var cleared = await Fails(h, "clear-prepared", $"UPDATE fin.payment SET status = 'CLEARED', version = 2 WHERE payment_id = '{payment}'", To("PREPARED", "CLEARED"));
        await Run(h, "edit", $"UPDATE fin.payment SET amount = 45000.00, version = 2 WHERE payment_id = '{payment}'");
        await Run(h, "release", ReleaseSql(payment, b.Verifier).Replace("version = 2", "version = 3", StringComparison.Ordinal), To("PREPARED", "RELEASED"));
        var editReleased = await Fails(h, "edit-released", $"UPDATE fin.payment SET amount = 1.00, version = 4 WHERE payment_id = '{payment}'");
        var voidReleased = await Fails(h, "void-released", $"UPDATE fin.payment SET status = 'VOIDED', version = 4 WHERE payment_id = '{payment}'", To("RELEASED", "VOIDED"));
        await Run(h, "clear", $"UPDATE fin.payment SET status = 'CLEARED', version = 4 WHERE payment_id = '{payment}'", To("RELEASED", "CLEARED"));
        // E-VS2-01-11: unmatching returns a CLEARED payment to RELEASED.
        await Run(h, "unclear", $"UPDATE fin.payment SET status = 'RELEASED', version = 5 WHERE payment_id = '{payment}'", To("CLEARED", "RELEASED"));
        await Run(h, "reverse", $"UPDATE fin.payment SET status = 'REVERSED', version = 6 WHERE payment_id = '{payment}'", To("RELEASED", "REVERSED"));
        var delete = await h.AppExecuteAsync($"DELETE FROM fin.payment WHERE payment_id = '{payment}'");

        Assert.Equal(SqlStates.RaiseException, cleared);
        Assert.Equal(SqlStates.RaiseException, editReleased);
        Assert.Equal(SqlStates.RaiseException, voidReleased);
        Assert.Equal(InsufficientPrivilege, delete?.SqlState);
        Assert.Equal("REVERSED:45000.0000:6", await h.ScalarAsync<string>($"SELECT status || ':' || amount || ':' || version FROM fin.payment WHERE payment_id = '{payment}'"));
        Assert.Equal("PREPARED,RELEASED,CLEARED,RELEASED,REVERSED", await h.ScalarAsync<string>(
            $"SELECT string_agg(h.to_state, ',' ORDER BY h.xmin::text::bigint) FROM core.state_history h WHERE h.aggregate_id = '{payment}'"));
    }

    [Fact]
    public async Task Applications_belong_to_the_payment_supplier_and_are_append_only()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var b = await BankAsync(h);
        var account = await VerifiedAccountAsync(h, b);
        var payment = await PreparedPaymentAsync(h, b, account);
        var otherSupplier = await h.CreateActiveSupplierAsync("101000003", "Otro proveedor");
        var ownDoc = Guid.CreateVersion7();
        var otherDoc = Guid.CreateVersion7();
        // AP documents without their invoices (the FK is off for the fixture only); VS2-03 pays real posted invoices.
        await h.AdminRequireAsync(
            $"""
            BEGIN;
            SET LOCAL session_replication_role = replica;
            INSERT INTO fin.ap_document VALUES
              ('{ownDoc}', '{h.CompanyId}', '{b.Supplier}', 'SUPPLIER_INVOICE', gen_random_uuid(), current_date, current_date, 45040.00, 45040.00, 1),
              ('{otherDoc}', '{h.CompanyId}', '{otherSupplier}', 'SUPPLIER_INVOICE', gen_random_uuid(), current_date, current_date, 100.00, 100.00, 1);
            COMMIT;
            """);
        var original = Guid.CreateVersion7();
        string Apply(Guid id, Guid doc, string amount, Guid? reverses = null)
            => $"INSERT INTO fin.ap_application VALUES ('{id}', '{h.CompanyId}', '{payment}', '{doc}', {amount}, @event, {(reverses is null ? "NULL" : $"'{reverses}'")})";

        var foreign = await Fails(h, "foreign", Apply(Guid.CreateVersion7(), otherDoc, "100.00"));
        await Run(h, "apply", Apply(original, ownDoc, "45040.00"));
        var wrongReversal = await Fails(h, "wrong-reversal", Apply(Guid.CreateVersion7(), ownDoc, "40.00", original));
        await Run(h, "unapply", Apply(Guid.CreateVersion7(), ownDoc, "45040.00", original));
        var twice = await Fails(h, "unapply-twice", Apply(Guid.CreateVersion7(), ownDoc, "45040.00", original));
        var update = await h.AppExecuteAsync("UPDATE fin.ap_application SET amount = 1");

        Assert.Equal(SqlStates.RaiseException, foreign);
        Assert.Equal(SqlStates.RaiseException, wrongReversal);
        Assert.Equal(SqlStates.UniqueViolation, twice);
        Assert.Equal(InsufficientPrivilege, update?.SqlState);
        Assert.Equal(2L, await h.CountAsync("fin.ap_application"));
    }

    [Fact]
    public async Task Statement_lines_are_unique_even_without_a_bank_reference()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var b = await BankAsync(h);
        var account = await VerifiedAccountAsync(h, b);
        var payment = await PreparedPaymentAsync(h, b, account);
        var statement = Guid.CreateVersion7();
        await Run(h, "import",
            $"INSERT INTO fin.bank_statement VALUES ('{statement}', '{h.CompanyId}', '{b.BankAccount}', current_date - 30, current_date, 100000.00, 54960.00, sha256('extracto'::bytea), @user, now())");
        string Line(Guid id, string direction, string? reference, int occurrence, string date = "current_date")
            => $"""
               INSERT INTO fin.bank_statement_line (line_id, company_id, statement_id, bank_account_id, value_date, direction, amount, bank_reference,
                 description, occurrence, status, version)
               VALUES ('{id}', '{h.CompanyId}', '{statement}', '{b.BankAccount}', {date}, '{direction}', 150.00, {(reference is null ? "NULL" : $"'{reference}'")},
                 'Comisión por transferencia', {occurrence}, 'UNMATCHED', 1)
               """;
        var debit = Guid.CreateVersion7();
        var credit = Guid.CreateVersion7();

        // E-VS2-01-12 (IDM-04): identical lines without a reference differ only by their occurrence in the file.
        Assert.Null(await h.AppExecuteAsync(Line(debit, "DEBIT", null, 1)));
        Assert.Equal(SqlStates.UniqueViolation, (await h.AppExecuteAsync(Line(Guid.CreateVersion7(), "DEBIT", null, 1)))?.SqlState);
        Assert.Null(await h.AppExecuteAsync(Line(Guid.CreateVersion7(), "DEBIT", null, 2)));
        Assert.Null(await h.AppExecuteAsync(Line(credit, "CREDIT", "TRF-1", 1)));
        Assert.Equal(SqlStates.RaiseException, (await h.AppExecuteAsync(Line(Guid.CreateVersion7(), "DEBIT", "TRF-2", 1, "current_date + 1")))?.SqlState);
        Assert.Equal(InsufficientPrivilege, (await h.AppExecuteAsync("UPDATE fin.bank_statement SET closing_balance = 0"))?.SqlState);

        // Only a DEBIT line of the payment's bank account is matched; each status change has its history.
        var matchCredit = await Fails(h, "match-credit",
            $"UPDATE fin.bank_statement_line SET status = 'MATCHED', matched_payment_id = '{payment}', version = 2 WHERE line_id = '{credit}'",
            new TestState("BankStatementLine", credit, "UNMATCHED", "MATCHED"));
        var noHistory = await h.AppExecuteAsync($"UPDATE fin.bank_statement_line SET status = 'MATCHED', matched_payment_id = '{payment}', version = 2 WHERE line_id = '{debit}'");
        await Run(h, "match",
            $"UPDATE fin.bank_statement_line SET status = 'MATCHED', matched_payment_id = '{payment}', version = 2 WHERE line_id = '{debit}'",
            new TestState("BankStatementLine", debit, "UNMATCHED", "MATCHED"));
        var toCharge = await Fails(h, "matched-to-charge",
            $"UPDATE fin.bank_statement_line SET status = 'CHARGE_RECOGNIZED', matched_payment_id = NULL, charge_event_id = @event, version = 3 WHERE line_id = '{debit}'",
            new TestState("BankStatementLine", debit, "MATCHED", "CHARGE_RECOGNIZED"));

        Assert.Equal(SqlStates.RaiseException, matchCredit);
        Assert.Equal(SqlStates.RaiseException, noHistory?.SqlState);
        Assert.Equal(SqlStates.RaiseException, toCharge);
        Assert.Equal("MATCHED:2", await h.ScalarAsync<string>($"SELECT status || ':' || version FROM fin.bank_statement_line WHERE line_id = '{debit}'"));
    }

    [Fact]
    public async Task Bank_lines_in_the_ledger_carry_the_bank_subledger_and_the_bank_gl_account()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var ledger = await h.CreateLedgerAsync();
        var b = await BankAsync(h);
        await h.RunAsync(
            new TestPostingCommand(h.CompanyId, h.SessionId, "seed", ledger.PlantId, 100m, BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow)),
            new TestPostingHandler());
        var journal = await h.ScalarAsync<Guid>("SELECT journal_id FROM fin.gl_journal");
        string Inject(string role, Guid account, string subledger, Guid reference)
        {
            var id = Guid.CreateVersion7();
            return $"""
                INSERT INTO fin.gl_journal (journal_id, company_id, posting_date, period_id, source_event_id, posting_rule_id, posting_rule_version,
                  posting_generation, journal_type, reverses_journal_id, late_entry, occurred_at, row_hash)
                SELECT '{id}', company_id, posting_date, period_id, source_event_id, posting_rule_id, posting_rule_version, 99, 'AUTO', NULL, false, occurred_at, sha256('x'::bytea)
                FROM fin.gl_journal WHERE journal_id = '{journal}';
                INSERT INTO fin.gl_entry (gl_entry_id, journal_id, line_no, company_id, posting_date, account_id, account_role, debit, credit, currency,
                  subledger_type, subledger_ref, source_event_id, rule_line_code, determination_inputs, row_hash)
                SELECT gen_random_uuid(), '{id}', 1, company_id, posting_date, '{account}', '{role}', 50, 0, 'DOP', '{subledger}', '{reference}',
                       source_event_id, rule_line_code, jsonb_build_object(), sha256('y'::bytea)
                FROM fin.gl_entry WHERE journal_id = '{journal}' AND line_no = 1;
                """;
        }

        // E-VS2-01-2: role BANK ⇔ subledger BANK. E-VS2-01-13: the reference is a bank account and the line posts to its GL account.
        Assert.Equal(SqlStates.CheckViolation, (await h.AppExecuteAsync(Inject("BANK", b.GlAccount, "AP", b.BankAccount)))?.SqlState);
        Assert.Equal(SqlStates.CheckViolation, (await h.AppExecuteAsync(Inject("AP_CONTROL", b.GlAccount, "BANK", b.BankAccount)))?.SqlState);
        Assert.Equal(SqlStates.RaiseException, (await h.AppExecuteAsync(Inject("BANK", ledger.ControlAccount, "BANK", b.BankAccount)))?.SqlState);
        Assert.Equal(SqlStates.RaiseException, (await h.AppExecuteAsync(Inject("BANK", b.GlAccount, "BANK", b.Supplier)))?.SqlState);
    }

    [Fact]
    public async Task Bank_tables_are_isolated_by_company()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var b = await BankAsync(h);
        await VerifiedAccountAsync(h, b);
        var other = await h.CreateCompanyAsync();

        var (connection, transaction) = await h.OpenAppTransactionAsync(other);
        await using (connection)
        await using (transaction)
        {
            await using var count = new NpgsqlCommand("SELECT (SELECT count(*) FROM fin.bank_account) + (SELECT count(*) FROM md.party_bank_account)", connection, transaction);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }

        var crossTenant = await h.AppExecuteAsync(
            $"INSERT INTO fin.bank_statement VALUES (gen_random_uuid(), '{h.CompanyId}', '{b.BankAccount}', current_date, current_date, 0, 0, sha256('x'::bytea), '{h.UserId}', now())",
            other);
        Assert.Equal(InsufficientPrivilege, crossTenant?.SqlState);
        Assert.Equal(3L, await h.ScalarAsync<long>(
            "SELECT count(*) FROM pg_class WHERE relrowsecurity AND oid IN ('fin.bank_account'::regclass, 'md.party_bank_account'::regclass, 'fin.payment'::regclass)"));
        Assert.Equal(3L, await h.ScalarAsync<long>(
            "SELECT count(*) FROM pg_class WHERE relrowsecurity AND oid IN ('fin.ap_application'::regclass, 'fin.bank_statement'::regclass, 'fin.bank_statement_line'::regclass)"));
    }

    [Fact]
    public async Task Every_period_has_the_bank_reconciliation_component_open()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.OpenPeriodsAsync(h.Clock.UtcNow.Year);

        // E-VS2-01-6 (same SQL as `rochell-migrate open-periods`).
        Assert.Equal(12L, await h.ScalarAsync<long>("SELECT count(*) FROM fin.close_component_state WHERE component = 'BANK-REC' AND status = 'OPEN'"));
    }
}
