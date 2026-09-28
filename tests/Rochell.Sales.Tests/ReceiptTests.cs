using System.Globalization;
using System.Text;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Reconciliation.Queries;
using Rochell.Sales.Invoices;
using Rochell.Sales.Orders;
using Rochell.Sales.Receipts;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Rochell.Treasury.BankAccounts;
using Rochell.Treasury.Statements;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>VS3-07: receipts, deposits, application, customer withholding, bounced cheques and matching (AR-01…03; E-VS3-07-1…15).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ReceiptTests(PostgresFixture postgres)
{
    private const string SalesItbis = """{"tax_code":"ITBIS","rate":"0.18","effect":"OUTPUT","exempt_item_categories":[]}""";

    internal sealed record World(DeliveryTests.Setup S, Guid Cobros, Guid Treasurer, Guid Controller, Guid Billing, Guid Bank, Guid Invoice, Guid ArDoc);

    /// <summary>An issued invoice of <paramref name="blocks"/> blocks at 50.00 + 18 % ITBIS, the receipt rules approved, a TEST_BANK account.</summary>
    internal static async Task<World> WorldAsync(TestHarness h, decimal blocks = 1000m)
    {
        var s = await DeliveryTests.SetupAsync(h);
        var actors = await h.FiscalActorsAsync();
        await h.ActivateRuleAsync(actors, "itbis-ventas", "ITBIS_VENTAS", FiscalRuleKinds.SalesItbis, SalesItbis, new DateOnly(2026, 1, 1));
        await h.AdminRequireAsync(
            $"""
            UPDATE fin.posting_rule_version v SET status = 'ACTIVE', approved_by = '{h.UserId}'
            FROM fin.posting_rule r WHERE r.posting_rule_id = v.posting_rule_id AND r.code IN ('P-23', 'P-24', 'P-25', 'P-27', 'P-29');
            """);
        foreach (var (role, code, control) in new[] { ("UNAPPLIED_RECEIPTS", "2120", true), ("CASH_IN_TRANSIT", "1105", true), ("WITHHOLDING_RECEIVABLE", "1260", false) })
        {
            s.Accounts[role] = await h.CreateAccountAsync(code, role, control);
            await h.CreateActiveMapAsync(role, s.Accounts[role]);
        }

        s.Accounts["BANK"] = await h.CreateAccountAsync("1101", "Banco de prueba", isControl: true);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var bank = (await h.RunAsync(new RegisterBankAccount(h.CompanyId, controller, "bank", "TEST_BANK", "0123456789", "1101"), new RegisterBankAccountHandler())).ResultRef;

        var (order, orderLine) = await DeliveryTests.ConfirmedOrderAsync(h, s, DeliveryTerms.PickupAtPlant, blocks);
        var (_, line) = await DeliveryTests.DispatchAsync(h, s, order, orderLine, blocks, own: false, "d1");
        var billing = await h.SessionWithRolesAsync("FACTURACION");
        var invoice = (await h.RunAsync(new CreateInvoiceFromDeliveries(h.CompanyId, billing, "i", s.Customer, [line]), new CreateInvoiceFromDeliveriesHandler())).ResultRef;
        await h.RunAsync(new IssueInvoice(h.CompanyId, billing, "issue", invoice, 1), new IssueInvoiceHandler());
        var arDoc = await h.ScalarAsync<Guid>("SELECT ar_doc_id FROM sal.invoice WHERE invoice_id = @i", ("i", invoice));
        return new World(s, await h.SessionWithRolesAsync("COBROS"), await h.SessionWithRolesAsync("TESORERO"), controller, billing, bank, invoice, arDoc);
    }

    internal static DateOnly Today(TestHarness h) => Platform.Time.BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

    internal static Task<CommandResult> Transfer(TestHarness h, World w, string key, decimal amount, Guid? session = null)
        => h.RunAsync(new RecordReceipt(h.CompanyId, session ?? w.Cobros, key, w.S.Customer, "TRANSFER", amount, Today(h), w.Bank, "TRF-001"), new RecordReceiptHandler());

    private static Task<CommandResult> Apply(TestHarness h, World w, string key, Guid receipt, long version, decimal amount)
        => h.RunAsync(new ApplyReceipt(h.CompanyId, w.Cobros, key, receipt, version, [new(w.Invoice, amount)]), new ApplyReceiptHandler());

    private static Task<string?> InvoiceState(TestHarness h, World w)
        => h.ScalarAsync<string>(
            "SELECT i.commercial_status || ':' || a.open_amount::numeric(19,2)::text FROM sal.invoice i JOIN fin.ar_document a ON a.ar_doc_id = i.ar_doc_id WHERE i.invoice_id = @i", ("i", w.Invoice));

    private static Task<CommandResult> Import(TestHarness h, World w, string key, params string[] rows)
    {
        var day = Today(h).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var csv = string.Join("\n", ["Fecha,Referencia,Descripcion,Debito,Credito", .. rows.Select(r => day + "," + r)]) + "\n";
        var net = rows.Sum(r => decimal.Parse(r.Split(',')[3] is { Length: > 0 } c ? c : "0", CultureInfo.InvariantCulture) - decimal.Parse(r.Split(',')[2] is { Length: > 0 } d ? d : "0", CultureInfo.InvariantCulture));
        return h.RunAsync(
            new ImportBankStatement(h.CompanyId, w.Treasurer, key, w.Bank, "extracto.csv", Convert.ToBase64String(Encoding.UTF8.GetBytes(csv)), Today(h), Today(h), 0m, net),
            new ImportBankStatementHandler());
    }

    private static Task<Guid> LineAsync(TestHarness h, string description)
        => h.ScalarAsync<Guid>("SELECT line_id FROM fin.bank_statement_line WHERE description = @d", ("d", description));

    private static async Task<JsonElement> BankGlAsync(TestHarness h, World w)
        => JsonDocument.Parse(await h.Queries.ExecuteAsync(new GetBankReconciliation(h.CompanyId, w.Treasurer, w.Bank), new GetBankReconciliationHandler())).RootElement;

    private static string M(JsonElement e, string property)
        => decimal.Parse(e.GetProperty(property).GetString()!, CultureInfo.InvariantCulture).ToString("0.00", CultureInfo.InvariantCulture);

    [Trait("AcceptanceVs3", "AR-01")]
    [Fact]
    public async Task AR01_a_transfer_and_the_customers_ITBIS_withholding_pay_the_invoice_with_P23_P25_P27()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h, blocks: 2000m); // 100,000.00 + 18,000.00 = 118,000.00
        var receipt = JsonDocument.Parse((await Transfer(h, w, "r", 100000.00m)).ResultPayload).RootElement;
        var receiptId = receipt.GetProperty("receiptId").GetGuid();

        var applied = JsonDocument.Parse((await Apply(h, w, "a", receiptId, 1, 100000.00m)).ResultPayload).RootElement;
        var partly = await InvoiceState(h, w);
        var withheld = JsonDocument.Parse((await h.RunAsync(
            new RecordCustomerWithholding(h.CompanyId, w.Cobros, "wh", w.Invoice, "ITBIS", 18000.00m, Today(h), "CR-2026-0001", "certificado-0001.pdf", DeliveryTests.Hash),
            new RecordCustomerWithholdingHandler())).ResultPayload).RootElement;

        Assert.Equal(("REC-000001", "DEPOSITED"), (receipt.GetProperty("receiptNo").GetString(), receipt.GetProperty("bankStatus").GetString()));
        Assert.Equal(("APPLIED", "0.00", "PARTIALLY_PAID:18000.00"), (applied.GetProperty("applicationStatus").GetString(), applied.GetProperty("unapplied").GetString(), partly));
        Assert.Equal("PAID", withheld.GetProperty("invoiceStatus").GetString());
        Assert.Equal("PAID:0.00", await InvoiceState(h, w));
        Assert.Equal("AR_CONTROL=0.00|UNAPPLIED_RECEIPTS=0.00|WITHHOLDING_RECEIVABLE=18000.00|BANK=100000.00",
            await DeliveryTests.Balances(h, w.S, "AR_CONTROL", "UNAPPLIED_RECEIPTS", "WITHHOLDING_RECEIVABLE", "BANK"));
        Assert.Equal("P-23|P-25|P-27", await h.ScalarAsync<string>(
            "SELECT string_agg(r.code, '|' ORDER BY r.code) FROM fin.gl_journal j JOIN fin.posting_rule r ON r.posting_rule_id = j.posting_rule_id WHERE r.code IN ('P-23', 'P-25', 'P-27')"));

        // AR by customer (AR-GL arrives with VS3-08): open documents = AR_CONTROL of the customer.
        Assert.Equal("0.00=0.00", await h.ScalarAsync<string>(
            """
            SELECT (SELECT sum(open_amount) FROM fin.ar_document WHERE party_id = @p)::numeric(19,2)::text || '=' ||
                   (SELECT coalesce(sum(debit - credit), 0) FROM fin.gl_entry WHERE party_id = @p AND account_id = @a)::numeric(19,2)::text
            """,
            ("p", w.S.Customer),
            ("a", w.S.Accounts["AR_CONTROL"])));
        var detail = JsonDocument.Parse(await h.QueryAsync(new Queries.GetReceipt(h.CompanyId, w.S.Seller, receiptId), new Queries.GetReceiptHandler())).RootElement;
        Assert.Equal("FA-000001:100000.00:true", string.Join('|', detail.GetProperty("applications").EnumerateArray()
            .Select(a => $"{a.GetProperty("invoiceNo").GetString()}:{a.GetProperty("amount").GetString()}:{a.GetProperty("live").GetBoolean().ToString().ToLowerInvariant()}")));
    }

    [Trait("AcceptanceVs3", "AR-02")]
    [Fact]
    public async Task AR02_a_bounced_cheque_undoes_its_applications_reopens_the_invoice_and_leaves_the_bank()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h); // 59,000.00
        var receipt = (await h.RunAsync(
            new RecordReceipt(h.CompanyId, w.Cobros, "chq", w.S.Customer, "CHEQUE", 59000.00m, ChequeBank: "Banco Popular", ChequeNo: "000123", ChequeDate: Today(h)),
            new RecordReceiptHandler())).ResultRef;
        var inTransit = await DeliveryTests.Balances(h, w.S, "CASH_IN_TRANSIT", "BANK");
        var deposit = (await h.RunAsync(new DepositReceipts(h.CompanyId, w.Cobros, "dep", w.Bank, [receipt]), new DepositReceiptsHandler())).ResultRef;
        await Apply(h, w, "a", receipt, 2, 59000.00m);
        var paid = await InvoiceState(h, w);
        await Import(h, w, "st", "DEP-1,Deposito cheques,,59000.00", "DEV-1,Cheque devuelto 000123,59000.00,");
        await h.RunAsync(new MatchBankLineToReceipt(h.CompanyId, w.Treasurer, "m-dep", await LineAsync(h, "Deposito cheques"), 1, null, deposit, 1), new MatchBankLineToReceiptHandler());

        var cobrosBounces = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new MarkReceiptBounced(h.CompanyId, w.Cobros, "b0", receipt, 4, "Fondos insuficientes"), new MarkReceiptBouncedHandler()));
        var bounced = JsonDocument.Parse((await h.RunAsync(new MarkReceiptBounced(h.CompanyId, w.Treasurer, "b", receipt, 4, "Fondos insuficientes"), new MarkReceiptBouncedHandler())).ResultPayload).RootElement;
        await h.RunAsync(new MatchBankLineToReceipt(h.CompanyId, w.Treasurer, "m-dev", await LineAsync(h, "Cheque devuelto 000123"), 1, receipt, null, 6), new MatchBankLineToReceiptHandler());

        Assert.Equal("CASH_IN_TRANSIT=59000.00|BANK=0.00", inTransit);
        Assert.Equal("PAID:0.00", paid);
        Assert.Equal(AuthorizationErrors.NotAuthorized, cobrosBounces.Code);
        Assert.Equal(("BOUNCED", "CONFIRMED"), (bounced.GetProperty("status").GetString(), bounced.GetProperty("invoices").GetProperty("FA-000001").GetString()));
        Assert.Equal("CONFIRMED:59000.00", await InvoiceState(h, w));
        Assert.Equal("BOUNCED:UNAPPLIED:MATCHED:59000.00", await h.ScalarAsync<string>(
            "SELECT status || ':' || application_status || ':' || bank_status || ':' || unapplied_amount::numeric(19,2)::text FROM fin.receipt WHERE receipt_id = @r", ("r", receipt)));
        Assert.Equal("AR_CONTROL=59000.00|UNAPPLIED_RECEIPTS=0.00|CASH_IN_TRANSIT=0.00|BANK=0.00",
            await DeliveryTests.Balances(h, w.S, "AR_CONTROL", "UNAPPLIED_RECEIPTS", "CASH_IN_TRANSIT", "BANK"));
        Assert.Equal("1|1", await h.ScalarAsync<string>(
            "SELECT (SELECT count(*) FROM fin.ar_application WHERE reverses_application_id IS NULL) || '|' || (SELECT count(*) FROM fin.ar_application WHERE reverses_application_id IS NOT NULL)"));
        var bankGl = await BankGlAsync(h, w);
        Assert.Equal("0.00|0.00|0.00|0", $"{M(bankGl, "glBalance")}|{M(bankGl, "statementBalance")}|{M(bankGl, "difference")}|{bankGl.GetProperty("glItems").GetArrayLength()}");
    }

    [Trait("AcceptanceVs3", "AR-03")]
    [Fact]
    public async Task AR03_a_transfer_is_matched_to_the_CREDIT_line_and_BANK_GL_balances()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var receipt = (await Transfer(h, w, "r", 59000.00m)).ResultRef;
        await Import(h, w, "st", "TRF-9,Transferencia Constructora Uno,,59000.00", "X-1,Otro deposito,,500.00");
        var line = await LineAsync(h, "Transferencia Constructora Uno");
        var before = await BankGlAsync(h, w);

        var other = await LineAsync(h, "Otro deposito");
        var wrongAmount = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new MatchBankLineToReceipt(h.CompanyId, w.Treasurer, "m0", other, 1, receipt, null, 1), new MatchBankLineToReceiptHandler()));
        var cobrosMatches = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new MatchBankLineToReceipt(h.CompanyId, w.Cobros, "m1", line, 1, receipt, null, 1), new MatchBankLineToReceiptHandler()));
        await h.RunAsync(new MatchBankLineToReceipt(h.CompanyId, w.Treasurer, "m", line, 1, receipt, null, 1), new MatchBankLineToReceiptHandler());
        var after = await BankGlAsync(h, w);

        // Before: GL 59,000.00 vs statement 59,500.00, the receipt in transit and both lines unrecorded; after: only the 500.00 line.
        Assert.Equal("59000.00|59500.00|0.00|OUTSTANDING_RECEIPT", $"{M(before, "glBalance")}|{M(before, "statementBalance")}|{M(before, "difference")}|{string.Join(',', before.GetProperty("glItems").EnumerateArray().Select(i => i.GetProperty("kind").GetString()))}");
        Assert.Equal("59000.00|59500.00|0.00|0|1", $"{M(after, "glBalance")}|{M(after, "statementBalance")}|{M(after, "difference")}|{after.GetProperty("glItems").GetArrayLength()}|{after.GetProperty("lineItems").GetArrayLength()}");
        Assert.Equal((StatementErrors.AmountDiffers, AuthorizationErrors.NotAuthorized), (wrongAmount.Code, cobrosMatches.Code));
        Assert.Equal("RECORDED:MATCHED", await h.ScalarAsync<string>("SELECT status || ':' || bank_status FROM fin.receipt WHERE receipt_id = @r", ("r", receipt)));

        await h.RunAsync(new UnmatchBankLine(h.CompanyId, w.Controller, "u", line, 2, "Línea equivocada"), new UnmatchBankLineHandler());
        Assert.Equal("DEPOSITED:UNMATCHED", await h.ScalarAsync<string>(
            "SELECT r.bank_status || ':' || l.status FROM fin.receipt r, fin.bank_statement_line l WHERE r.receipt_id = @r AND l.line_id = @l", ("r", receipt), ("l", line)));
    }

    [Fact]
    public async Task Two_applications_of_the_same_receipt_at_once_never_apply_more_than_it_has()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var receipt = (await Transfer(h, w, "r", 59000.00m)).ResultRef;

        var outcomes = await Task.WhenAll(new[] { "a", "b" }.Select(k => Task.Run(() => Record.ExceptionAsync(() => Apply(h, w, k, receipt, 1, 40000.00m)))));

        Assert.Equal(1, outcomes.Count(o => o is null));
        Assert.Contains(Assert.IsType<DomainException>(outcomes.Single(o => o is not null)).Code, new[] { ReceiptErrors.ExceedsOpen, ReceiptErrors.ExceedsUnapplied, SalesErrors.VersionConflict });
        Assert.Equal("19000.00|PARTIALLY_PAID:19000.00", await h.ScalarAsync<string>("SELECT unapplied_amount::numeric(19,2)::text FROM fin.receipt WHERE receipt_id = @r", ("r", receipt)) + "|" + await InvoiceState(h, w));
    }

    [Fact]
    public async Task Unapply_reverse_and_withholding_corrections_put_the_invoice_and_the_ledger_back()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var billingRecords = await Assert.ThrowsAsync<DomainException>(() => Transfer(h, w, "x", 100m, w.Billing));
        var receipt = (await Transfer(h, w, "r", 59000.00m)).ResultRef;
        var applied = JsonDocument.Parse((await Apply(h, w, "a", receipt, 1, 59000.00m)).ResultPayload).RootElement;

        var appliedReverse = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ReverseReceipt(h.CompanyId, w.Controller, "rv0", receipt, 2, "Error"), new ReverseReceiptHandler()));
        var unapplied = JsonDocument.Parse((await h.RunAsync(
            new UnapplyReceipt(h.CompanyId, w.Cobros, "u", receipt, applied.GetProperty("applicationEventId").GetGuid(), "Factura equivocada"), new UnapplyReceiptHandler())).ResultPayload).RootElement;
        await h.RunAsync(new ReverseReceipt(h.CompanyId, w.Controller, "rv", receipt, 3, "Transferencia registrada dos veces"), new ReverseReceiptHandler());

        var isr = (await h.RunAsync(
            new RecordCustomerWithholding(h.CompanyId, w.Cobros, "isr", w.Invoice, "ISR", 1000.00m, Today(h), "ISR-77", "isr-77.pdf", DeliveryTests.Hash), new RecordCustomerWithholdingHandler())).ResultRef;
        var partly = await InvoiceState(h, w);
        var tooMuchItbis = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new RecordCustomerWithholding(h.CompanyId, w.Cobros, "itbis", w.Invoice, "ITBIS", 9000.01m, Today(h), "IT-1", "it-1.pdf", DeliveryTests.Hash), new RecordCustomerWithholdingHandler()));
        await h.RunAsync(new ReverseCustomerWithholding(h.CompanyId, w.Controller, "isr-rv", isr, 1, "Certificado de otra factura"), new ReverseCustomerWithholdingHandler());

        Assert.Equal(AuthorizationErrors.NotAuthorized, billingRecords.Code);
        Assert.Equal(ReceiptErrors.NotReversible, appliedReverse.Code);
        Assert.Equal(("UNAPPLIED", "CONFIRMED"), (unapplied.GetProperty("applicationStatus").GetString(), unapplied.GetProperty("invoices").GetProperty("FA-000001").GetString()));
        Assert.Equal("PARTIALLY_PAID:58000.00", partly);
        Assert.Equal(ReceiptErrors.WithholdingExceeds, tooMuchItbis.Code);
        Assert.Equal("CONFIRMED:59000.00", await InvoiceState(h, w));
        Assert.Equal("REVERSED:UNAPPLIED", await h.ScalarAsync<string>("SELECT status || ':' || application_status FROM fin.receipt WHERE receipt_id = @r", ("r", receipt)));
        Assert.Equal("AR_CONTROL=59000.00|UNAPPLIED_RECEIPTS=0.00|WITHHOLDING_RECEIVABLE=0.00|BANK=0.00",
            await DeliveryTests.Balances(h, w.S, "AR_CONTROL", "UNAPPLIED_RECEIPTS", "WITHHOLDING_RECEIVABLE", "BANK"));
    }
}
