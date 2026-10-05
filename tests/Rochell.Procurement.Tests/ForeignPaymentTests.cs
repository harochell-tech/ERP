using System.Text.Json;
using Rochell.Finance.ExchangeRates;
using Rochell.Platform.Commands;
using Rochell.Procurement.Expenses;
using Rochell.Procurement.SupplierInvoices;
using Rochell.TestInfrastructure;
using Rochell.Treasury.BankAccounts;
using Rochell.Treasury.Payments;
using Xunit;
using static Rochell.Procurement.Tests.ExpenseInvoiceTests;
using static Rochell.Procurement.Tests.ForeignInvoiceTests;

namespace Rochell.Procurement.Tests;

/// <summary>
/// USD1-05a (USD-07, E-USD1-05-1…6): payments of USD payables — from a peso account at the bank's rate, from a USD account at the approved
/// rate of the value date — relieve the payable at its invoice's pesos and post the realized exchange difference (P-41).
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ForeignPaymentTests(PostgresFixture postgres)
{
    private const string P41 = "0192f001-0000-7000-8000-000000000033";

    private sealed record Setup(Foreign F, Guid Invoice, Guid ApDoc, Guid Treasurer, Guid PesoBank, Guid UsdBank, Guid SupplierAccount);

    /// <summary>The foreign invoice of USD 10,000.00 at 60 (600,000.00) posted; a peso and a USD bank account; the supplier's IBAN verified.</summary>
    private static async Task<Setup> SetupAsync(TestHarness h)
    {
        var f = await ForeignAsync(h);
        var si = (await h.RunAsync(
            new RegisterExpenseInvoice(
                h.CompanyId, f.W.Clerk, "si", f.Supplier, "INV-2026-0147", Today(h), Today(h).AddDays(30), f.W.Plant,
                [Usd(f.Forklift, "Montacargas usado", 1m, 8000m), Usd(f.Parts, "Repuestos", 4m, 500m)]),
            new RegisterExpenseInvoiceHandler())).ResultRef;
        await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, f.W.Clerk, "si-m", si, 1), new MatchSupplierInvoiceHandler());
        await h.RunAsync(new ApproveMatchException(h.CompanyId, f.W.Controller, "si-a", si, 2, "Importación"), new ApproveMatchExceptionHandler());
        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, f.W.Clerk, "si-p", si, 3), new PostSupplierInvoiceHandler());

        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '{P41}' AND version = 1");
        await h.CreateActiveMapAsync("FX_LOSS", await h.CreateAccountAsync("68100", "Pérdida cambiaria", isControl: false));
        await h.CreateActiveMapAsync("FX_GAIN", await h.CreateAccountAsync("48100", "Ganancia cambiaria", isControl: false));
        await h.CreateAccountAsync("1101", "Banco en pesos", isControl: true);
        await h.CreateAccountAsync("1102", "Banco en dólares", isControl: true);
        var peso = (await h.RunAsync(new RegisterBankAccount(h.CompanyId, f.W.Controller, "bank-dop", "BPD", "0123456789", "1101"), new RegisterBankAccountHandler())).ResultRef;
        var usd = (await h.RunAsync(new RegisterBankAccount(h.CompanyId, f.W.Controller, "bank-usd", "BPD", "0987654321", "1102", "usd"), new RegisterBankAccountHandler())).ResultRef;
        var treasurer = await h.SessionWithRolesAsync("TESORERO");
        var account = await h.VerifiedPartyBankAccountAsync(f.Supplier, treasurer, f.W.Controller, 1, "US64SVBKUS6S3300958879", 73);
        var apDoc = await h.ScalarAsync<Guid>("SELECT ap_doc_id FROM fin.ap_document WHERE source_doc_id = @s", ("s", si));
        return new Setup(f, si, apDoc, treasurer, peso, usd, account);
    }

    private static async Task<JsonElement> PayAsync(TestHarness h, Setup s, string key, Guid bank, decimal usd, decimal? rate = null)
    {
        var payment = (await h.RunAsync(
            new PrepareSupplierPayment(h.CompanyId, s.Treasurer, key, s.F.Supplier, bank, s.SupplierAccount, Today(h), "SWIFT", [new(s.ApDoc, usd)], rate),
            new PrepareSupplierPaymentHandler())).ResultRef;
        return JsonDocument.Parse((await h.RunAsync(new ReleaseSupplierPayment(h.CompanyId, s.F.W.Controller, key + "-r", payment, 1), new ReleaseSupplierPaymentHandler()))
            .ResultPayload).RootElement.Clone();
    }

    /// <summary>Bank accounts, foreign payables and exchange difference (code:debit − credit) | the AP_FOREIGN / BANK USD amounts | the AP document.</summary>
    private static Task<string?> BooksAsync(TestHarness h)
        => h.ScalarAsync<string>(
            """
            SELECT (SELECT string_agg(a.code || ':' || coalesce((SELECT sum(e.debit - e.credit) FROM fin.gl_entry e WHERE e.account_id = a.account_id), 0)::numeric(19,2), ',' ORDER BY a.code)
                    FROM fin.account a WHERE a.code IN ('1101', '1102', '21020', '48100', '68100'))
                   || '|' || (SELECT original_amount::numeric(19,2) || '/' || open_amount::numeric(19,2) || ' USD ' || original_amount_fc::numeric(19,2) || '/' || open_amount_fc::numeric(19,2)
                              FROM fin.ap_document WHERE currency = 'USD')
            """);

    [Trait("AcceptanceUsd1", "USD-07")]
    [Fact]
    public async Task USD07_USD_10000_paid_from_a_peso_account_at_61_costs_610000_and_a_realized_loss_of_10000()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);

        var noRate = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareSupplierPayment(h.CompanyId, s.Treasurer, "x", s.F.Supplier, s.PesoBank, s.SupplierAccount, Today(h), null, [new(s.ApDoc, 10000m)]),
            new PrepareSupplierPaymentHandler()));
        var tooMuch = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareSupplierPayment(h.CompanyId, s.Treasurer, "y", s.F.Supplier, s.PesoBank, s.SupplierAccount, Today(h), null, [new(s.ApDoc, 10000.01m)], 61m),
            new PrepareSupplierPaymentHandler()));
        var released = await PayAsync(h, s, "pay", s.PesoBank, 10000m, 61m);
        var payment = released.GetProperty("paymentId").GetGuid();

        Assert.Equal((PaymentErrors.ExchangeRateInvalid, PaymentErrors.ApplicationExceedsOpenAmount), (noRate.Code, tooMuch.Code));
        Assert.Equal("1101:-610000.00,1102:0.00,21020:0.00,48100:0.00,68100:10000.00|600000.00/0.00 USD 10000.00/0.00", await BooksAsync(h));
        Assert.Equal(
            "P41-DR-AP:600000.00:USD 10000.00,P41-CR-BANK:610000.00:DOP -,P41-DR-FXL:10000.00:DOP -",
            await h.ScalarAsync<string>(
                "SELECT string_agg(rule_line_code || ':' || (debit + credit)::numeric(19,2) || ':' || currency || ' ' || coalesce(amount_fc::numeric(19,2)::text, '-'), ',' ORDER BY line_no) FROM fin.gl_entry WHERE rule_line_code LIKE 'P41-%'"));

        // Reversed, the payable is open again in both currencies and the loss is gone.
        await h.RunAsync(new ReversePayment(h.CompanyId, s.F.W.Controller, "rev", payment, 2, "Transferencia devuelta por el banco"), new ReversePaymentHandler());
        Assert.Equal("1101:0.00,1102:0.00,21020:-600000.00,48100:0.00,68100:0.00|600000.00/600000.00 USD 10000.00/10000.00", await BooksAsync(h));
    }

    [Fact]
    public async Task From_a_USD_account_at_the_days_rate_partial_payments_relieve_the_payable_pro_rata_and_the_last_takes_the_rest()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);

        // USD 4,000.00 at 60: 240,000.00 out of the USD account, 240,000.00 of the payable, no difference.
        var typed = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareSupplierPayment(h.CompanyId, s.Treasurer, "t", s.F.Supplier, s.UsdBank, s.SupplierAccount, Today(h), null, [new(s.ApDoc, 4000m)], 61m),
            new PrepareSupplierPaymentHandler()));
        await PayAsync(h, s, "p1", s.UsdBank, 4000m);
        var first = await BooksAsync(h);

        // The day's rate is corrected to 59: the last USD 6,000.00 leave as 354,000.00 against the 360,000.00 left — a gain of 6,000.00.
        var fix = await h.RunAsync(new PrepareExchangeRate(h.CompanyId, s.Treasurer, "fix", "USD", Today(h), 59m, "Banco Central (corrección)"), new PrepareExchangeRateHandler());
        await h.RunAsync(new ApproveExchangeRate(h.CompanyId, s.F.W.Controller, "fix-a", fix.ResultRef, 1), new ApproveExchangeRateHandler());
        await PayAsync(h, s, "p2", s.UsdBank, 6000m);

        Assert.Equal(PaymentErrors.ExchangeRateInvalid, typed.Code);
        Assert.Equal("1101:0.00,1102:-240000.00,21020:-360000.00,48100:0.00,68100:0.00|600000.00/360000.00 USD 10000.00/6000.00", first);
        Assert.Equal("1101:0.00,1102:-594000.00,21020:0.00,48100:-6000.00,68100:0.00|600000.00/0.00 USD 10000.00/0.00", await BooksAsync(h));
        Assert.Equal(
            "USD 4000.00,USD 6000.00",
            await h.ScalarAsync<string>("SELECT string_agg(currency || ' ' || amount_fc::numeric(19,2), ',' ORDER BY amount_fc) FROM fin.gl_entry WHERE rule_line_code = 'P41-CR-BANK'"));
    }

    [Fact]
    public async Task A_payment_pays_one_currency_and_only_a_foreign_supplier_has_an_IBAN()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);

        var fromUsdToPesos = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new Rochell.MasterData.BankAccounts.RequestPartyBankAccount(h.CompanyId, s.Treasurer, "iban-local", s.F.W.S.Purchasing.SupplierId, "BHD", "DO28BAGR00000001212453611324", "Proveedor"),
            new Rochell.MasterData.BankAccounts.RequestPartyBankAccountHandler()));
        var badCurrency = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new RegisterBankAccount(h.CompanyId, s.F.W.Controller, "eur", "BPD", "11111111", "1101", "EUR"), new RegisterBankAccountHandler()));

        Assert.Equal(
            (Rochell.MasterData.BankAccounts.BankIdentifiers.Invalid, Rochell.MasterData.BankAccounts.BankIdentifiers.Invalid),
            (fromUsdToPesos.Code, badCurrency.Code));
        Assert.Equal(
            "US64SVBKUS6S3300958879",
            await h.ScalarAsync<string>("SELECT account_number FROM md.party_bank_account WHERE party_bank_account_id = @a", ("a", s.SupplierAccount)));
    }
}
