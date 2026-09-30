using System.Globalization;
using System.Text;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Queries;
using Rochell.Platform.Time;
using Rochell.Reconciliation.Queries;
using Rochell.TestInfrastructure;
using Rochell.Treasury.BankAccounts;
using Rochell.Treasury.Payments;
using Rochell.Treasury.Queries;
using Rochell.Treasury.Statements;
using Xunit;

namespace Rochell.Treasury.Tests;

/// <summary>UX4-01: bank account alias (E-UX4-6), payment totals, AP aging bucket totals and the amount in transit (E-UX4-2).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class Ux4TreasuryTests(PostgresFixture postgres)
{
    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static async Task<JsonElement> Query<TQuery>(TestHarness h, TQuery query, IQueryHandler<TQuery> handler)
        where TQuery : IQuery
        => JsonDocument.Parse(await h.QueryAsync(query, handler)).RootElement;

    private static string Money(JsonElement e, string property) => decimal.Parse(e.GetProperty(property).GetString()!, CultureInfo.InvariantCulture).ToString("0.00", CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_bank_account_alias_is_set_trimmed_cleared_and_validated()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePaymentSetupAsync();
        SetBankAccountAlias Alias(string key, long version, string? alias) => new(h.CompanyId, p.Controller, key, p.BankAccount, version, alias);

        var set = JsonDocument.Parse((await h.RunAsync(Alias("a1", 1, "  Nómina Popular  "), new SetBankAccountAliasHandler())).ResultPayload).RootElement;
        var tooLong = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Alias("a2", 2, new string('x', 61)), new SetBankAccountAliasHandler()));
        var stale = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Alias("a3", 1, "Otra"), new SetBankAccountAliasHandler()));
        var treasurer = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SetBankAccountAlias(h.CompanyId, p.Treasurer, "a4", p.BankAccount, 2, "Mía"), new SetBankAccountAliasHandler()));
        var listed = (await Query(h, new ListBankAccounts(h.CompanyId, p.Treasurer), new ListBankAccountsHandler())).GetProperty("items")[0];
        await h.RunAsync(Alias("a5", 2, "   "), new SetBankAccountAliasHandler());
        var cleared = (await Query(h, new ListBankAccounts(h.CompanyId, p.Treasurer), new ListBankAccountsHandler())).GetProperty("items")[0];

        Assert.Equal(("Nómina Popular", 2L), (set.GetProperty("alias").GetString(), set.GetProperty("version").GetInt64()));
        Assert.Equal((TreasuryErrors.BankAccountAliasInvalid, TreasuryErrors.VersionConflict, "NOT_AUTHORIZED"), (tooLong.Code, stale.Code, treasurer.Code));
        Assert.Equal(("Nómina Popular", "ACTIVE", 2L), (listed.GetProperty("alias").GetString(), listed.GetProperty("status").GetString(), listed.GetProperty("version").GetInt64()));
        Assert.Equal((JsonValueKind.Null, 3L), (cleared.GetProperty("alias").ValueKind, cleared.GetProperty("version").GetInt64()));
        Assert.Equal("Nómina Popular|", await h.ScalarAsync<string>(
            "SELECT string_agg(coalesce(payload ->> 'alias', ''), '|' ORDER BY aggregate_version) FROM core.domain_event WHERE event_type = 'BankAccountAliasSet'"));
    }

    [Fact]
    public async Task Payments_carry_the_alias_and_the_list_totals_the_whole_filter()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePaymentSetupAsync(invoices: 2, bankCode: "TEST_BANK"); // two invoices of 5 310.00
        await h.RunAsync(new SetBankAccountAlias(h.CompanyId, p.Controller, "alias", p.BankAccount, 1, "Operativa"), new SetBankAccountAliasHandler());
        foreach (var (doc, i) in p.ApDocs.Select((d, i) => (d, i)))
        {
            await h.RunAsync(
                new PrepareSupplierPayment(h.CompanyId, p.Treasurer, $"prepare-{i}", p.Supplier, p.BankAccount, p.PartyBankAccount, Today(h), $"TRF-{i}", [new(doc, 5310.00m)]),
                new PrepareSupplierPaymentHandler());
        }

        var page = await Query(h, new ListPayments(h.CompanyId, p.Treasurer, Limit: 1), new ListPaymentsHandler());
        var voided = await Query(h, new ListPayments(h.CompanyId, p.Treasurer, Status: "VOIDED"), new ListPaymentsHandler());
        var payment = page.GetProperty("items")[0];
        var detail = await Query(h, new GetPayment(h.CompanyId, p.Treasurer, payment.GetProperty("paymentId").GetGuid()), new GetPaymentHandler());

        // One item on the page, but the filter selects both: 2 payments, 10 620.00.
        Assert.Equal((1, 2, "10620.00"), (page.GetProperty("items").GetArrayLength(), page.GetProperty("count").GetInt32(), Money(page, "total")));
        Assert.Equal((0, "0.00"), (voided.GetProperty("count").GetInt32(), Money(voided, "total")));
        Assert.Equal(("Operativa", "Operativa"), (payment.GetProperty("bankAccountAlias").GetString(), detail.GetProperty("bankAccountAlias").GetString()));
    }

    [Fact]
    public async Task AP_aging_totals_each_bucket_and_the_bank_reconciliation_sums_what_is_in_transit()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePaymentSetupAsync(invoices: 2, bankCode: "TEST_BANK");
        await h.CreateActivePolicyAsync("TREASURY", new Dictionary<string, string>
        {
            ["ap_aging_bucket_1_days"] = "30",
            ["ap_aging_bucket_2_days"] = "60",
            ["ap_aging_bucket_3_days"] = "90",
        });
        await h.RunAsync(new SetBankAccountAlias(h.CompanyId, p.Controller, "alias", p.BankAccount, 1, "Operativa"), new SetBankAccountAliasHandler());
        var aging = await Query(h, new GetApAging(h.CompanyId, p.Treasurer, Today(h).AddDays(45)), new GetApAgingHandler());

        // A released payment of 5 310.00 the bank does not show yet, and a 100.00 bank charge not yet recognized.
        var payment = (await h.RunAsync(
            new PrepareSupplierPayment(h.CompanyId, p.Treasurer, "prepare", p.Supplier, p.BankAccount, p.PartyBankAccount, Today(h), "TRF-1", [new(p.ApDocs[0], 5310.00m)]),
            new PrepareSupplierPaymentHandler())).ResultRef;
        await h.RunAsync(new ReleaseSupplierPayment(h.CompanyId, p.Controller, "release", payment, 1), new ReleaseSupplierPaymentHandler());
        var csv = $"Fecha,Referencia,Descripcion,Debito,Credito\n{Today(h):dd/MM/yyyy},,COMISION,100.00,\n";
        await h.RunAsync(
            new ImportBankStatement(h.CompanyId, p.Treasurer, "import", p.BankAccount, "e.csv", Convert.ToBase64String(Encoding.UTF8.GetBytes(csv)), Today(h), Today(h), 0m, -100.00m),
            new ImportBankStatementHandler());
        var bankGl = await Query(h, new GetBankReconciliation(h.CompanyId, p.Treasurer, p.BankAccount), new GetBankReconciliationHandler());
        var statements = (await Query(h, new ListBankStatements(h.CompanyId, p.Treasurer), new ListBankStatementsHandler())).GetProperty("items")[0];

        // 15 days past the due date both invoices (10 620.00) are in bucket 1.
        var t = aging.GetProperty("bucketTotals");
        Assert.Equal("0.00|10620.00|0.00|0.00|0.00|10620.00", string.Join('|', new[] { "current", "bucket1", "bucket2", "bucket3", "over", "total" }.Select(x => Money(t, x))));

        // GL −5 310.00 = statement −100.00 + GL items −5 310.00 − line items −100.00; nothing unexplained.
        Assert.Equal(("-5310.00", "-100.00", "-5310.00", "-100.00", "0.00"), (Money(bankGl, "glBalance"), Money(bankGl, "statementBalance"), Money(bankGl, "glItemsTotal"),
            Money(bankGl, "lineItemsTotal"), Money(bankGl, "difference")));
        Assert.Equal("Operativa", statements.GetProperty("bankAccountAlias").GetString());
    }
}
