using System.Text;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Rochell.Treasury.BankAccounts;
using Rochell.Treasury.Statements;
using Xunit;

namespace Rochell.Treasury.Tests;

/// <summary>VS2-02: company bank accounts (E-VS2-1, E-VS2-01-1/3, E-VS2-02-2/3).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class BankAccountTests(PostgresFixture postgres)
{
    private static Task<CommandResult> Register(TestHarness h, Guid controller, string key, string bank, string number, string gl)
        => h.RunAsync(new RegisterBankAccount(h.CompanyId, controller, key, bank, number, gl), new RegisterBankAccountHandler());

    [Fact]
    public async Task The_controller_registers_a_bank_account_on_its_own_control_account()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.CreateAccountAsync("1101", "Banco", isControl: true);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");

        var result = await Register(h, controller, "reg", " bpd ", "012-345 6789", "1101");

        Assert.Equal("BPD|0123456789|ACTIVE|1", await h.ScalarAsync<string>(
            $"SELECT bank_code || '|' || account_number || '|' || status || '|' || version FROM fin.bank_account WHERE bank_account_id = '{result.ResultRef}'"));
        Assert.Equal("BankAccountRegistered", await h.ScalarAsync<string>($"SELECT event_type FROM core.domain_event WHERE aggregate_id = '{result.ResultRef}'"));
    }

    [Theory]
    [InlineData("9999", TreasuryErrors.GlAccountNotFound)]
    [InlineData("6100", TreasuryErrors.GlAccountNotEligible)] // not a control account
    [InlineData("2100", TreasuryErrors.GlAccountNotEligible)] // a control account already mapped to a role
    public async Task The_GL_account_must_be_an_unmapped_control_account(string code, string error)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.CreateLedgerAsync(); // 6100 expense, 4100 income, 2100 control mapped to TEST_CONTROL
        var controller = await h.SessionWithRolesAsync("CONTROLLER");

        var ex = await Assert.ThrowsAsync<DomainException>(() => Register(h, controller, "reg", "BPD", "0123456789", code));

        Assert.Equal(error, ex.Code);
    }

    [Fact]
    public async Task Invalid_identifiers_and_duplicates_are_refused()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.CreateAccountAsync("1101", "Banco", isControl: true);
        await h.CreateAccountAsync("1102", "Otro banco", isControl: true);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        await Register(h, controller, "first", "BPD", "0123456789", "1101");

        var shortNumber = await Assert.ThrowsAsync<DomainException>(() => Register(h, controller, "short", "BPD", "1234", "1102"));
        var letters = await Assert.ThrowsAsync<DomainException>(() => Register(h, controller, "letters", "BPD", "12AB5678", "1102"));
        var sameNumber = await Assert.ThrowsAsync<DomainException>(() => Register(h, controller, "dup", "bpd", "0123-456789", "1102"));
        var sameGl = await Assert.ThrowsAsync<DomainException>(() => Register(h, controller, "gl", "BHD", "9876543210", "1101"));

        Assert.Equal("BANK_ACCOUNT_INVALID", shortNumber.Code);
        Assert.Equal("BANK_ACCOUNT_INVALID", letters.Code);
        Assert.Equal(TreasuryErrors.BankAccountDuplicate, sameNumber.Code);
        Assert.Equal(TreasuryErrors.GlAccountNotEligible, sameGl.Code);
    }

    [Fact]
    public async Task Only_the_controller_manages_bank_accounts()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.CreateAccountAsync("1101", "Banco", isControl: true);
        var treasurer = await h.SessionWithRolesAsync("TESORERO");

        var ex = await Assert.ThrowsAsync<DomainException>(() => Register(h, treasurer, "reg", "BPD", "0123456789", "1101"));

        Assert.Equal(AuthorizationErrors.NotAuthorized, ex.Code);
    }

    [Fact]
    public async Task Closing_needs_a_reason_the_current_version_and_no_open_items()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.InitTestEnvironmentAsync();
        var ledger = await h.CreateLedgerAsync(activateRule: false);
        await h.CreateActiveMapAsync("BANK_CHARGES", ledger.ExpenseAccount);
        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '0192f001-0000-7000-8000-000000000010'");
        await h.CreateAccountAsync("1101", "Banco", isControl: true);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var treasurer = await h.SessionWithRolesAsync("TESORERO");
        var bank = (await Register(h, controller, "reg", "TEST_BANK", "0123456789", "1101")).ResultRef;
        var today = BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);
        var csv = $"Fecha,Referencia,Descripcion,Debito,Credito\n{today:dd/MM/yyyy},,Comisión,150.00,\n";
        await h.RunAsync(
            new ImportBankStatement(h.CompanyId, treasurer, "statement", bank, "extracto.csv", Convert.ToBase64String(Encoding.UTF8.GetBytes(csv)), today, today, 150m, 0m),
            new ImportBankStatementHandler());
        var line = await h.ScalarAsync<Guid>("SELECT line_id FROM fin.bank_statement_line");
        Task<CommandResult> Close(string key, long version, string reason)
            => h.RunAsync(new CloseBankAccount(h.CompanyId, controller, key, bank, version, reason), new CloseBankAccountHandler());

        var noReason = await Assert.ThrowsAsync<DomainException>(() => Close("no-reason", 1, " "));
        var stale = await Assert.ThrowsAsync<DomainException>(() => Close("stale", 2, "Cuenta cancelada por el banco"));
        var openLine = await Assert.ThrowsAsync<DomainException>(() => Close("open", 1, "Cuenta cancelada por el banco"));
        await h.RunAsync(new RecognizeBankCharge(h.CompanyId, controller, "charge", line, 1), new RecognizeBankChargeHandler());
        await Close("close", 1, "Cuenta cancelada por el banco");

        Assert.Equal(TreasuryErrors.ReasonRequired, noReason.Code);
        Assert.Equal(TreasuryErrors.VersionConflict, stale.Code);
        Assert.Equal(TreasuryErrors.BankAccountHasOpenItems, openLine.Code);
        Assert.Equal("CLOSED:2", await h.ScalarAsync<string>($"SELECT status || ':' || version FROM fin.bank_account WHERE bank_account_id = '{bank}'"));
        Assert.Equal("Cuenta cancelada por el banco", await h.ScalarAsync<string>($"SELECT reason FROM core.state_history WHERE aggregate_id = '{bank}' AND to_state = 'CLOSED'"));
    }
}
