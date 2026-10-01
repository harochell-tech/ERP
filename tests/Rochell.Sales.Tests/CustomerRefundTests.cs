using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Reconciliation;
using Rochell.Sales.Deliveries;
using Rochell.Sales.Invoices;
using Rochell.Sales.Orders;
using Rochell.Sales.Proformas;
using Rochell.Sales.Receipts;
using Rochell.Sales.Refunds;
using Rochell.Tax.Authorizations;
using Rochell.TestInfrastructure;
using Rochell.Treasury.Queries;
using Rochell.Treasury.Statements;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>FIS1b-05: the refund of a customer's credit balance — prepared, released by someone else (P-36), matched with the statement (E-FIS1b-8, E-FIS1b-01-9).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class CustomerRefundTests(PostgresFixture postgres)
{
    /// <summary>
    /// A proforma of 100 blocks collected with ITBIS (5,900.00), certified and invoiced as an e-CF 44 of 5,000.00: the receipt keeps
    /// 900.00 unapplied — the ITBIS the customer advanced.
    /// </summary>
    private static async Task<(ReceiptTests.World W, Guid Receipt)> WorldAsync(TestHarness h)
    {
        var w = await ReceiptTests.WorldAsync(h, 100m);
        await h.AdminRequireAsync(
            $"UPDATE fin.posting_rule_version v SET status = 'ACTIVE', approved_by = '{h.UserId}' FROM fin.posting_rule r WHERE r.posting_rule_id = v.posting_rule_id AND r.code = 'P-36'");
        var (order, line) = await ProformaTests.OrderAsync(h, w.S, DeliveryTerms.PickupAtPlant, 100m, collectsItbis: true, "pf");
        var (delivery, _) = await DeliveryTests.DispatchAsync(h, w.S, order, line, 100m, own: false, "pf-d");
        var proforma = await h.ScalarAsync<Guid>("SELECT proforma_id FROM sal.proforma WHERE delivery_id = @d", ("d", delivery));
        var receipt = (await ReceiptTests.Transfer(h, w, "r", 5900.00m)).ResultRef;
        await h.RunAsync(new AllocateReceiptToProformas(h.CompanyId, w.Cobros, "a", receipt, 1, [new(proforma, 5900.00m)]), new AllocateReceiptToProformasHandler());
        var specialist = await h.SessionWithRolesAsync("ESPECIALISTA_FISCAL");
        var authorization = (await h.RunAsync(
            new RegisterFiscalAuthorization(
                h.CompanyId, w.Billing, "auth", w.S.Customer, "CERT-2026-0001", new DateOnly(2026, 9, 1), new DateOnly(2027, 3, 1), "Hotel Playa Bávaro", "CONFOTUR-0456-2025", null, null, null, [proforma]),
            new RegisterFiscalAuthorizationHandler())).ResultRef;
        await h.RunAsync(new AttachAuthorizationDocument(h.CompanyId, w.Billing, "doc", authorization, "CERTIFICADO_DGII", "certificado.pdf", DeliveryTests.Hash), new AttachAuthorizationDocumentHandler());
        await h.RunAsync(new SubmitForVerification(h.CompanyId, w.Billing, "sub", authorization, 1), new SubmitForVerificationHandler());
        await h.RunAsync(new VerifyAuthorization(h.CompanyId, specialist, "ver", authorization, 2), new VerifyAuthorizationHandler());
        var invoice = (await h.RunAsync(new CreateInvoiceFromProformas(h.CompanyId, w.Billing, "pf-i", w.S.Customer, [proforma], authorization), new CreateInvoiceFromProformasHandler())).ResultRef;
        await h.RunAsync(new IssueInvoice(h.CompanyId, w.Billing, "pf-issue", invoice, 1), new IssueInvoiceHandler());
        return (w, receipt);
    }

    private static PrepareCustomerRefund Prepare(TestHarness h, ReceiptTests.World w, string key, Guid receipt, decimal amount, Guid? session = null)
        => new(h.CompanyId, session ?? w.Cobros, key, receipt, w.Bank, "TRANSFER", amount, "Sale la certificación: se devuelve el ITBIS adelantado", "TRF-DEV-001");

    private static Task<string?> State(TestHarness h, Guid refund, Guid receipt)
        => h.ScalarAsync<string>(
            """
            SELECT concat_ws('|', f.refund_no, f.status, f.amount::numeric(19,2), r.application_status, r.unapplied_amount::numeric(19,2))
            FROM fin.customer_refund f, fin.receipt r WHERE f.refund_id = @f AND r.receipt_id = @r
            """,
            ("f", refund),
            ("r", receipt));

    [Trait("AcceptanceFis1b", "PRF-09")]
    [Fact]
    public async Task PRF09_the_ITBIS_advanced_is_refunded_by_two_people_and_matched_with_the_statement()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (w, receipt) = await WorldAsync(h);
        var before = await h.ScalarAsync<string>("SELECT application_status || '|' || unapplied_amount::numeric(19,2) FROM fin.receipt WHERE receipt_id = @r", ("r", receipt));

        var tooMuch = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Prepare(h, w, "x", receipt, 900.01m), new PrepareCustomerRefundHandler()));
        var byController = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Prepare(h, w, "c", receipt, 900.00m, w.Controller), new PrepareCustomerRefundHandler()));
        var refund = (await h.RunAsync(Prepare(h, w, "p", receipt, 900.00m), new PrepareCustomerRefundHandler())).ResultRef;
        var twice = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Prepare(h, w, "p2", receipt, 0.01m), new PrepareCustomerRefundHandler()));
        var prepared = await State(h, refund, receipt);
        var byCobros = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ReleaseCustomerRefund(h.CompanyId, w.Cobros, "rc", refund, 1), new ReleaseCustomerRefundHandler()));
        var released = JsonDocument.Parse((await h.RunAsync(new ReleaseCustomerRefund(h.CompanyId, w.Controller, "rel", refund, 1), new ReleaseCustomerRefundHandler())).ResultPayload).RootElement;

        Assert.Equal("PARTIALLY_APPLIED|900.00", before);
        Assert.Equal((RefundErrors.ExceedsCreditBalance, AuthorizationErrors.NotAuthorized, RefundErrors.ExceedsCreditBalance, AuthorizationErrors.NotAuthorized), (tooMuch.Code, byController.Code, twice.Code, byCobros.Code));
        Assert.Equal("DEV-000001|PREPARED|900.00|PARTIALLY_APPLIED|900.00", prepared);
        Assert.Equal(("RELEASED", "0.00"), (released.GetProperty("status").GetString(), released.GetProperty("receiptUnapplied").GetString()));
        Assert.Equal("DEV-000001|RELEASED|900.00|APPLIED|0.00", await State(h, refund, receipt));
        // P-36: Dr UNAPPLIED_RECEIPTS 900.00 / Cr BANK 900.00. The bank holds the 5,900.00 received less the 900.00 refunded.
        Assert.Equal("UNAPPLIED_RECEIPTS=0.00|BANK=5000.00", await DeliveryTests.Balances(h, w.S, "UNAPPLIED_RECEIPTS", "BANK"));

        // The statement: the transfer in and the refund out; the treasurer matches the refund's DEBIT line, the Controller unmatches it.
        await ReceiptTests.Import(h, w, "st", "TRF-001,Transferencia cliente,,5900.00", "TRF-DEV-001,Devolucion cliente,900.00,");
        var line = await ReceiptTests.LineAsync(h, "Devolucion cliente");
        var toMatch = JsonDocument.Parse(await h.QueryAsync(new ListRefundsToMatch(h.CompanyId, w.Treasurer, w.Bank), new ListRefundsToMatchHandler())).RootElement.GetProperty("items");
        var creditLine = await ReceiptTests.LineAsync(h, "Transferencia cliente");
        var credit = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new MatchBankLineToRefund(h.CompanyId, w.Treasurer, "m0", creditLine, 1, refund, 2), new MatchBankLineToRefundHandler()));
        await h.RunAsync(new MatchBankLineToRefund(h.CompanyId, w.Treasurer, "m", line, 1, refund, 2), new MatchBankLineToRefundHandler());
        var cleared = await State(h, refund, receipt);
        var outstanding = (await ReceiptTests.BankGlAsync(h, w)).GetRawText();
        await h.RunAsync(new UnmatchBankLine(h.CompanyId, w.Controller, "u", line, 2, "Emparejada con la línea equivocada"), new UnmatchBankLineHandler());

        Assert.Equal(("DEV-000001", 1), (toMatch[0].GetProperty("refundNo").GetString(), toMatch.GetArrayLength()));
        Assert.Equal(StatementErrors.LineNotDebit, credit.Code);
        Assert.Equal("DEV-000001|CLEARED|900.00|APPLIED|0.00", cleared);
        Assert.DoesNotContain("OUTSTANDING_REFUND", outstanding, StringComparison.Ordinal);
        Assert.Equal("DEV-000001|RELEASED|900.00|APPLIED|0.00", await State(h, refund, receipt));
        Assert.Contains("OUTSTANDING_REFUND", (await ReceiptTests.BankGlAsync(h, w)).GetRawText(), StringComparison.Ordinal);

        // The reconciliations of receipts, evidence and AR still agree with a refunded receipt.
        var run = JsonDocument.Parse((await h.RunAsync(new RunReconciliation(h.CompanyId, w.Controller, "recon", ["ACC-EVIDENCE", "AR-GL", "RECEIPT-APPL"]), new RunReconciliationHandler())).ResultPayload).RootElement;
        Assert.Equal(
            "ACC-EVIDENCE:MATCHED,AR-GL:MATCHED,RECEIPT-APPL:MATCHED",
            string.Join(',', run.GetProperty("runs").EnumerateArray().Select(r => $"{r.GetProperty("code").GetString()}:{r.GetProperty("status").GetString()}").Order(StringComparer.Ordinal)));
    }

    [Fact]
    public async Task A_prepared_refund_is_voided_and_only_money_in_the_bank_that_is_free_is_refunded()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (w, receipt) = await WorldAsync(h);
        var cheque = (await h.RunAsync(
            new RecordReceipt(h.CompanyId, w.Cobros, "chq", w.S.Customer, "CHEQUE", 1000.00m, ChequeBank: "Banco Popular", ChequeNo: "000123", ChequeDate: ReceiptTests.Today(h)),
            new RecordReceiptHandler())).ResultRef;

        var inTransit = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Prepare(h, w, "t", cheque, 100.00m), new PrepareCustomerRefundHandler()));
        var badMethod = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareCustomerRefund(h.CompanyId, w.Cobros, "m", receipt, w.Bank, "CASH", 100.00m, "Devolución"), new PrepareCustomerRefundHandler()));
        var refund = (await h.RunAsync(Prepare(h, w, "p", receipt, 900.00m), new PrepareCustomerRefundHandler())).ResultRef;
        var noReason = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new VoidCustomerRefund(h.CompanyId, w.Cobros, "n", refund, 1, " "), new VoidCustomerRefundHandler()));
        await h.RunAsync(new VoidCustomerRefund(h.CompanyId, w.Cobros, "v", refund, 1, "El cliente prefiere dejarlo como saldo a favor"), new VoidCustomerRefundHandler());
        var voided = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ReleaseCustomerRefund(h.CompanyId, w.Controller, "rel", refund, 2), new ReleaseCustomerRefundHandler()));
        var list = JsonDocument.Parse(await h.QueryAsync(new ListCustomerRefunds(h.CompanyId, w.S.Seller, w.S.Customer), new ListCustomerRefundsHandler())).RootElement.GetProperty("items");

        Assert.Equal(
            (RefundErrors.ReceiptNotRefundable, RefundErrors.MethodInvalid, ReceiptErrors.ReasonRequired, SalesErrors.InvalidState),
            (inTransit.Code, badMethod.Code, noReason.Code, voided.Code));
        Assert.Equal("DEV-000001|VOIDED|900.00|PARTIALLY_APPLIED|900.00", await State(h, refund, receipt));
        Assert.Equal(("VOIDED", "El cliente prefiere dejarlo como saldo a favor"), (list[0].GetProperty("status").GetString(), list[0].GetProperty("voidReason").GetString()));
        // The credit balance is free again: it can be prepared anew.
        await h.RunAsync(Prepare(h, w, "p2", receipt, 900.00m), new PrepareCustomerRefundHandler());
        Assert.Equal(2L, await h.ScalarAsync<long>("SELECT count(*) FROM fin.customer_refund"));
    }
}
