using System.Globalization;
using System.Text;
using System.Text.Json;
using Rochell.MasterData.Queries;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Reconciliation.Queries;
using Rochell.TestInfrastructure;
using Rochell.Treasury.Payments;
using Rochell.Treasury.Queries;
using Rochell.Treasury.Statements;
using Xunit;

namespace Rochell.Treasury.Tests;

/// <summary>VS2-07: treasury queries (E-VS2-07-1…7, E-UI-4).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class TreasuryQueryTests(PostgresFixture postgres)
{
    private static DateOnly Today(TestHarness h) => BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    private static async Task<JsonElement> Query<TQuery>(TestHarness h, TQuery query, Platform.Queries.IQueryHandler<TQuery> handler)
        where TQuery : Platform.Queries.IQuery
        => JsonDocument.Parse(await h.Queries.ExecuteAsync(query, handler)).RootElement;

    private static Task EnableAgingAsync(TestHarness h)
        => h.CreateActivePolicyAsync("TREASURY", new Dictionary<string, string>
        {
            ["ap_aging_bucket_1_days"] = "30",
            ["ap_aging_bucket_2_days"] = "60",
            ["ap_aging_bucket_3_days"] = "90",
        });

    [Fact]
    public async Task AP_aging_puts_each_open_document_in_the_TREASURY_policy_buckets()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePaymentSetupAsync(invoices: 2);
        var due = Today(h).AddDays(30);

        var missing = await Assert.ThrowsAsync<DomainException>(() => h.Queries.ExecuteAsync(new GetApAging(h.CompanyId, p.Treasurer), new GetApAgingHandler()));
        await EnableAgingAsync(h);
        var current = await Query(h, new GetApAging(h.CompanyId, p.Treasurer, Today(h)), new GetApAgingHandler());
        var buckets = new List<string>();
        foreach (var days in new[] { 15, 45, 75, 120 })
        {
            var aging = await Query(h, new GetApAging(h.CompanyId, p.Treasurer, due.AddDays(days)), new GetApAgingHandler());
            buckets.Add(aging.GetProperty("suppliers")[0].GetProperty("documents")[0].GetProperty("bucket").GetString()!);
        }

        Assert.Equal(TreasuryQueryErrors.PolicyMissing, missing.Code);
        // Two invoices of 3 t × 1,500 + 18 % ITBIS = 5,310.00 each, both due in 30 days.
        var supplier = current.GetProperty("suppliers")[0];
        Assert.Equal("10620.00|10620.00|0.00", $"{Money(supplier, "current")}|{Money(supplier, "total")}|{Money(supplier, "bucket1")}");
        Assert.Equal(["BUCKET_1", "BUCKET_2", "BUCKET_3", "OVER"], buckets);
        Assert.Equal(30, current.GetProperty("buckets").GetProperty("bucket1Days").GetInt32());
    }

    [Fact]
    public async Task The_payment_proposal_lists_invoices_due_and_the_suppliers_payability_with_masked_numbers()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePaymentSetupAsync(verifiedHoursAgo: 10);
        var today = Today(h);

        var notYet = await Query(h, new GetPaymentProposal(h.CompanyId, p.Treasurer, today), new GetPaymentProposalHandler());
        var treasurer = await Query(h, new GetPaymentProposal(h.CompanyId, p.Treasurer, today.AddDays(30)), new GetPaymentProposalHandler());
        var controller = await Query(h, new GetPaymentProposal(h.CompanyId, p.Controller, today.AddDays(30), p.Supplier), new GetPaymentProposalHandler());

        Assert.Equal(0, notYet.GetProperty("suppliers").GetArrayLength());
        var s = treasurer.GetProperty("suppliers")[0];
        Assert.Equal("HOLD_PENDING|••••3210|10620.00|1", $"{s.GetProperty("payability").GetString()}|{s.GetProperty("accountNumber").GetString()}|{Money(s, "openAmount")}|{s.GetProperty("invoices").GetArrayLength()}");
        Assert.Equal("9876543210", controller.GetProperty("suppliers")[0].GetProperty("accountNumber").GetString());
    }

    [Fact]
    public async Task Payments_list_and_detail_show_plan_applications_lines_and_history()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePaymentSetupAsync(bankCode: "TEST_BANK");
        var amount = await h.ScalarAsync<decimal>("SELECT open_amount FROM fin.ap_document WHERE ap_doc_id = @d", ("d", p.ApDocs[0]));
        var payment = (await h.RunAsync(
            new PrepareSupplierPayment(h.CompanyId, p.Treasurer, "prepare", p.Supplier, p.BankAccount, p.PartyBankAccount, Today(h), "TRF-1", [new(p.ApDocs[0], amount)]),
            new PrepareSupplierPaymentHandler())).ResultRef;

        var prepared = await Query(h, new GetPayment(h.CompanyId, p.Treasurer, payment), new GetPaymentHandler());
        await h.RunAsync(new ReleaseSupplierPayment(h.CompanyId, p.Controller, "release", payment, 1), new ReleaseSupplierPaymentHandler());
        var csv = $"Fecha,Referencia,Descripcion,Debito,Credito\n{Today(h):dd/MM/yyyy},,PAG-000001,{amount.ToString("0.00", CultureInfo.InvariantCulture)},\n";
        await h.RunAsync(
            new ImportBankStatement(h.CompanyId, p.Treasurer, "import", p.BankAccount, "e.csv", Convert.ToBase64String(Encoding.UTF8.GetBytes(csv)), Today(h), Today(h), 0m, -amount),
            new ImportBankStatementHandler());
        var line = await h.ScalarAsync<Guid>("SELECT line_id FROM fin.bank_statement_line");
        await h.RunAsync(new MatchBankLine(h.CompanyId, p.Treasurer, "match", line, 1, payment, 2), new MatchBankLineHandler());
        var cleared = await Query(h, new GetPayment(h.CompanyId, p.Controller, payment), new GetPaymentHandler());
        var list = await Query(h, new ListPayments(h.CompanyId, p.Treasurer, Status: "CLEARED"), new ListPaymentsHandler());
        var none = await Query(h, new ListPayments(h.CompanyId, p.Treasurer, Status: "VOIDED"), new ListPaymentsHandler());
        var missing = await Assert.ThrowsAsync<DomainException>(() => h.Queries.ExecuteAsync(new GetPayment(h.CompanyId, p.Treasurer, Guid.CreateVersion7()), new GetPaymentHandler()));

        Assert.Equal("PREPARED|1|0|TRF-1|••••6789", $"{prepared.GetProperty("status").GetString()}|{prepared.GetProperty("plan").GetArrayLength()}|{prepared.GetProperty("applications").GetArrayLength()}|{prepared.GetProperty("bankReference").GetString()}|{prepared.GetProperty("accountNumber").GetString()}");
        Assert.Equal("CLEARED|0|1|1|0123456789", $"{cleared.GetProperty("status").GetString()}|{cleared.GetProperty("plan").GetArrayLength()}|{cleared.GetProperty("applications").GetArrayLength()}|{cleared.GetProperty("statementLines").GetArrayLength()}|{cleared.GetProperty("accountNumber").GetString()}");
        Assert.Equal("PREPARED>RELEASED>CLEARED", string.Join('>', cleared.GetProperty("history").EnumerateArray().Select(c => c.GetProperty("to").GetString())));
        Assert.Equal("PAG-000001", Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("paymentNo").GetString());
        Assert.Equal(0, none.GetProperty("items").GetArrayLength());
        Assert.Equal(Platform.Queries.QueryErrors.NotFound, missing.Code);

        // Statements and lines, and the BANK-GL of the account (GL −amount = statement −amount, nothing in transit).
        var statements = await Query(h, new ListBankStatements(h.CompanyId, p.Treasurer), new ListBankStatementsHandler());
        var matched = await Query(h, new ListBankStatementLines(h.CompanyId, p.Treasurer, Status: "MATCHED"), new ListBankStatementLinesHandler());
        var bankGl = await Query(h, new GetBankReconciliation(h.CompanyId, p.Treasurer, p.BankAccount), new GetBankReconciliationHandler());
        Assert.Equal("1|0|e.csv", $"{statements.GetProperty("items")[0].GetProperty("lines").GetInt32()}|{statements.GetProperty("items")[0].GetProperty("unmatched").GetInt32()}|{statements.GetProperty("items")[0].GetProperty("fileName").GetString()}");
        Assert.Equal("PAG-000001", matched.GetProperty("items")[0].GetProperty("matchedPaymentNo").GetString());
        Assert.Equal($"false|{Money(-amount)}|{Money(-amount)}|0.00|0|0", $"{bankGl.GetProperty("skipped").GetBoolean().ToString().ToLowerInvariant()}|{Money(bankGl, "glBalance")}|{Money(bankGl, "statementBalance")}|{Money(bankGl, "difference")}|{bankGl.GetProperty("glItems").GetArrayLength()}|{bankGl.GetProperty("findings").GetArrayLength()}");
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM rec.recon_run"));
    }

    [Fact]
    public async Task Bank_accounts_and_supplier_account_versions_are_masked_unless_the_reader_may_see_numbers()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePaymentSetupAsync();
        var auditor = await h.SessionWithRolesAsync("AUDITOR");

        var treasurer = await Query(h, new ListBankAccounts(h.CompanyId, p.Treasurer), new ListBankAccountsHandler());
        var audit = await Query(h, new ListBankAccounts(h.CompanyId, auditor), new ListBankAccountsHandler());
        var versions = await Query(h, new ListPartyBankAccounts(h.CompanyId, p.Treasurer, p.Supplier), new ListPartyBankAccountsHandler());
        var suppliers = await Query(h, new ListSuppliers(h.CompanyId, p.Controller), new ListSuppliersHandler());

        Assert.Equal("••••6789|1101|ACTIVE", $"{treasurer.GetProperty("items")[0].GetProperty("accountNumber").GetString()}|{treasurer.GetProperty("items")[0].GetProperty("glAccountCode").GetString()}|{treasurer.GetProperty("items")[0].GetProperty("status").GetString()}");
        Assert.Equal("0123456789", audit.GetProperty("items")[0].GetProperty("accountNumber").GetString());
        var v = Assert.Single(versions.GetProperty("items").EnumerateArray());
        Assert.Equal("VERIFIED|••••3210|1", $"{v.GetProperty("status").GetString()}|{v.GetProperty("accountNumber").GetString()}|{v.GetProperty("version").GetInt32()}");
        var s = suppliers.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("supplierId").GetGuid() == p.Supplier);
        Assert.Equal("PAYABLE|10620.00", $"{s.GetProperty("bankAccountState").GetString()}|{Money(s, "openApAmount")}");
    }

    private static string Money(JsonElement e, string property) => Money(decimal.Parse(e.GetProperty(property).GetString()!, CultureInfo.InvariantCulture));

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
