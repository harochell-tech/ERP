using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Sales.CashSales;
using Rochell.Sales.CreditNotes;
using Rochell.Sales.Deliveries;
using Rochell.Sales.Invoices;
using Rochell.Sales.Orders;
using Rochell.Sales.Receipts;
using Rochell.Sales.Refunds;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>
/// CF1-03 (E-CF1-5, 6, 8, 13, E-CF1-01-7, 8, E-CF1-03-1…3): the invoices of a cash sale are born paid with what was assigned to the
/// order, name its buyer and are recorded as e-CF 32 without a receiver RNC or with a passport; a credit note and a refund return
/// money through the bank; a paid sale without deliveries is cancelled and its receipts freed. BLOQUE-6 at 50.00, ITBIS 18 %.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class CashSaleInvoiceTests(PostgresFixture postgres)
{
    internal sealed record World(ReceiptTests.World W, Guid Cashier, Guid Consumer);

    internal static async Task<World> WorldAsync(TestHarness h)
    {
        var w = await ReceiptTests.WorldAsync(h, 100m);
        var actors = await h.FiscalActorsAsync();
        await h.ActivateRuleAsync(actors, "umbral", "CONSUMIDOR_ID", FiscalRuleKinds.ConsumerIdThreshold, """{"amount":"250000.00"}""", new DateOnly(2026, 1, 1));
        await h.AdminRequireAsync(
            $"UPDATE fin.posting_rule_version v SET status = 'ACTIVE', approved_by = '{h.UserId}' FROM fin.posting_rule r WHERE r.posting_rule_id = v.posting_rule_id AND r.code IN ('P-22', 'P-36') AND v.status = 'DRAFT'");
        var cashier = await h.SessionWithRolesAsync("CAJA");

        // The first (unpaid, cancelled) sale creates the final consumer.
        var first = (await h.RunAsync(Sale(h, w.S, cashier, "first", 1m), new CreateCashSaleHandler())).ResultRef;
        await h.RunAsync(new CancelCashSale(h.CompanyId, cashier, "first-x", first, 1, "Venta de arranque"), new CancelCashSaleHandler());
        return new World(w, cashier, await h.ScalarAsync<Guid>("SELECT party_id FROM md.party WHERE company_id = @c AND party_kind = 'CONSUMER'", ("c", h.CompanyId)));
    }

    internal static CreateCashSale Sale(TestHarness h, DeliveryTests.Setup s, Guid cashier, string key, decimal blocks, string? idKind = null, string? id = null)
        => new(h.CompanyId, cashier, key, s.Plant, DeliveryTerms.PickupAtPlant, null, null, [new SalesOrderLineInput(s.Block, "un", blocks)], "María Pérez", null, idKind, id);

    /// <summary>A sale of <paramref name="blocks"/> blocks paid in full by a transfer; returns the order, its line and the receipt.</summary>
    internal static async Task<(Guid Order, Guid Line, Guid Receipt)> PaidAsync(TestHarness h, World w, string key, decimal blocks, decimal toPay, string? idKind = null, string? id = null)
    {
        var order = (await h.RunAsync(Sale(h, w.W.S, w.Cashier, key, blocks, idKind, id), new CreateCashSaleHandler())).ResultRef;
        await h.RunAsync(new SubmitCashSaleForPayment(h.CompanyId, w.Cashier, key + "-pay", order, 1), new SubmitCashSaleForPaymentHandler());
        var receipt = (await h.RunAsync(
            new RecordReceipt(h.CompanyId, w.Cashier, key + "-r", w.Consumer, "TRANSFER", toPay, ReceiptTests.Today(h), w.W.Bank, "TRF-" + key), new RecordReceiptHandler())).ResultRef;
        await h.RunAsync(new AllocateReceiptToOrder(h.CompanyId, w.Cashier, key + "-a", receipt, 1, order, toPay), new AllocateReceiptToOrderHandler());
        return (order, await h.ScalarAsync<Guid>("SELECT line_id FROM sal.sales_order_line WHERE sales_order_id = @o", ("o", order)), receipt);
    }

    // Keys carry "cs-": the world's own order, delivery and invoice used "o", "d1", "i" on the same sessions, and a repeated key
    // would replay them (E-03).
    private static async Task<(Guid Invoice, JsonElement Issued)> InvoiceAsync(TestHarness h, World w, string key, Guid deliveryLine)
    {
        var invoice = (await h.RunAsync(new CreateInvoiceFromDeliveries(h.CompanyId, w.W.Billing, key, w.Consumer, [deliveryLine]), new CreateInvoiceFromDeliveriesHandler())).ResultRef;
        var issued = JsonDocument.Parse((await h.RunAsync(new IssueInvoice(h.CompanyId, w.W.Billing, key + "-issue", invoice, 1), new IssueInvoiceHandler())).ResultPayload).RootElement;
        return (invoice, issued);
    }

    private static Task<string?> ReceiptAsync(TestHarness h, Guid receipt)
        => h.ScalarAsync<string>(
            "SELECT application_status || ':' || unapplied_amount::numeric(19,2) || ':' || allocated_amount::numeric(19,2) FROM fin.receipt WHERE receipt_id = @r", ("r", receipt));

    [Trait("AcceptanceCf1", "CF-08")]
    [Fact]
    public async Task CF08_each_delivery_of_a_paid_sale_is_invoiced_as_an_eCF_32_with_its_buyer_and_is_born_paid()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var (order, line, receipt) = await PaidAsync(h, w, "s", 100m, 5900.00m, "CEDULA", "40212345678");
        var (_, first) = await DeliveryTests.DispatchAsync(h, w.W.S, order, line, 60m, own: false, "cs-d1");
        var (_, second) = await DeliveryTests.DispatchAsync(h, w.W.S, order, line, 40m, own: false, "cs-d2");
        var (other, otherLine, _) = await PaidAsync(h, w, "o", 10m, 590.00m);
        var (_, foreign) = await DeliveryTests.DispatchAsync(h, w.W.S, other, otherLine, 10m, own: false, "cs-d3");

        // E-CF1-03-1: one order per invoice; E-CF1-9: never exempt.
        var twoOrders = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new CreateInvoiceFromDeliveries(h.CompanyId, w.W.Billing, "two", w.Consumer, [first, foreign]), new CreateInvoiceFromDeliveriesHandler()));
        var exempt = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new CreateInvoiceFromDeliveries(h.CompanyId, w.W.Billing, "exempt", w.Consumer, [first], Guid.CreateVersion7()), new CreateInvoiceFromDeliveriesHandler()));

        // 60 blocks: 3,000.00 + 540.00 = 3,540.00, taken from the 5,900.00 assigned; 2,360.00 stays assigned for the next delivery.
        var (invoice, issued) = await InvoiceAsync(h, w, "cs-i1", first);
        var afterFirst = (await ReceiptAsync(h, receipt), await h.ScalarAsync<string>("SELECT allocated_amount::numeric(19,2)::text FROM sal.sales_order WHERE sales_order_id = @o", ("o", order)));
        var (_, last) = await InvoiceAsync(h, w, "cs-i2", second);

        Assert.Equal((InvoiceErrors.OneCashOrder, InvoiceErrors.EcfTypeInvalid), (twoOrders.Code, exempt.Code));
        Assert.Equal(("PAID", "3540.00", "3540.00"), (issued.GetProperty("commercialStatus").GetString(), issued.GetProperty("total").GetString(), issued.GetProperty("collectedOnOrder").GetString()));
        Assert.Equal(("PARTIALLY_APPLIED:2360.00:2360.00", "2360.00"), afterFirst);
        Assert.Equal(("PAID", "2360.00"), (last.GetProperty("commercialStatus").GetString(), last.GetProperty("collectedOnOrder").GetString()));
        Assert.Equal("APPLIED:0.00:0.00", await ReceiptAsync(h, receipt));
        Assert.Equal("32:María Pérez:CEDULA:40212345678:true:0.00", await h.ScalarAsync<string>(
            """
            SELECT i.ecf_type || ':' || i.buyer_name || ':' || i.buyer_id_kind || ':' || i.buyer_id || ':' || (i.due_date = i.invoice_date)::text || ':' || a.open_amount::numeric(19,2)
            FROM sal.invoice i JOIN fin.ar_document a ON a.ar_doc_id = i.ar_doc_id WHERE i.invoice_id = @i
            """,
            ("i", invoice)));
        Assert.Equal("DELIVERED:5900.00:0.00", await h.ScalarAsync<string>(
            "SELECT status || ':' || payment_total::numeric(19,2) || ':' || allocated_amount::numeric(19,2) FROM sal.sales_order WHERE sales_order_id = @o", ("o", order)));

        // The consumer owes nothing and has nothing unapplied: receivable and unapplied receipts of the party are zero in the ledger.
        Assert.Equal("0.00|-590.00", await h.ScalarAsync<string>(
            """
            SELECT string_agg(coalesce((SELECT sum(e.debit - e.credit) FROM fin.gl_entry e WHERE e.account_role = r.role AND e.party_id = @p), 0)::numeric(19,2)::text, '|' ORDER BY r.n)
            FROM (VALUES (1, 'AR_CONTROL'), (2, 'UNAPPLIED_RECEIPTS')) AS r (n, role)
            """,
            ("p", w.Consumer))); // 590.00: the other, still uninvoiced, sale
    }

    [Trait("AcceptanceCf1", "CF-09")]
    [Fact]
    public async Task CF09_the_eCF_32_is_recorded_without_a_receiver_or_with_the_buyers_passport()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var (anonymous, anonymousLine, _) = await PaidAsync(h, w, "a", 10m, 590.00m);
        var (foreigner, foreignerLine, _) = await PaidAsync(h, w, "p", 10m, 590.00m, "pasaporte", "ab1234567");
        var (_, d1) = await DeliveryTests.DispatchAsync(h, w.W.S, anonymous, anonymousLine, 10m, own: false, "cs-d1");
        var (_, d2) = await DeliveryTests.DispatchAsync(h, w.W.S, foreigner, foreignerLine, 10m, own: false, "cs-d2");
        var (first, _) = await InvoiceAsync(h, w, "cs-i1", d1);
        var (second, _) = await InvoiceAsync(h, w, "cs-i2", d2);
        RecordExternalFiscalDocument Record(string key, Guid invoice, string encf, string? rnc, string? passport = null)
            => new(h.CompanyId, w.W.Billing, key, invoice, 3, encf, h.Clock.UtcNow.AddMinutes(-2), "A1B2C3", "e-cf.xml", DeliveryTests.Hash, rnc, 500.00m, 90.00m, 590.00m, passport);

        var named = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Record("f0", first, "E320000000001", "40212345678"), new RecordExternalFiscalDocumentHandler()));
        var wrongType = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Record("f1", first, "E310000000001", null), new RecordExternalFiscalDocumentHandler()));
        await h.RunAsync(Record("f2", first, "E320000000001", null), new RecordExternalFiscalDocumentHandler());
        var noPassport = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Record("f3", second, "E320000000002", ""), new RecordExternalFiscalDocumentHandler()));
        await h.RunAsync(Record("f4", second, "E320000000002", "", "AB1234567"), new RecordExternalFiscalDocumentHandler());

        Assert.Equal((InvoiceErrors.FiscalDocumentMismatch, InvoiceErrors.EncfInvalid, InvoiceErrors.FiscalDocumentMismatch), (named.Code, wrongType.Code, noPassport.Code));
        Assert.Equal("E320000000001::,E320000000002::AB1234567", await h.ScalarAsync<string>(
            "SELECT string_agg(encf || ':' || coalesce(receiver_rnc, '') || ':' || coalesce(receiver_passport, ''), ',' ORDER BY encf) FROM tax.external_fiscal_record"));
        Assert.Equal("ACCEPTED_EXTERNAL,ACCEPTED_EXTERNAL", await h.ScalarAsync<string>(
            "SELECT string_agg(fiscal_status, ',' ORDER BY invoice_no) FROM sal.invoice WHERE invoice_id IN (@a, @b)", ("a", first), ("b", second)));
    }

    [Trait("AcceptanceCf1", "CF-10")]
    [Fact]
    public async Task CF10_a_credit_note_on_the_eCF_32_and_a_refund_through_the_bank_return_money_to_the_consumer()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var (order, line, receipt) = await PaidAsync(h, w, "s", 100m, 5900.00m);
        var (_, deliveryLine) = await DeliveryTests.DispatchAsync(h, w.W.S, order, line, 100m, own: false, "cs-d");
        var (invoice, _) = await InvoiceAsync(h, w, "cs-i", deliveryLine);
        await h.RunAsync(
            new RecordExternalFiscalDocument(h.CompanyId, w.W.Billing, "f", invoice, 3, "E320000000001", h.Clock.UtcNow.AddMinutes(-2), "A1B2C3", "e-cf.xml", DeliveryTests.Hash, null, 5000.00m, 900.00m, 5900.00m),
            new RecordExternalFiscalDocumentHandler());

        // A price credit of 500.00 net (590.00 with ITBIS). The invoice is paid, so Cobros first undoes the application; the note is
        // created by Facturación and issued by another person; Cobros applies the receipt again to what the invoice still owes.
        var application = await h.ScalarAsync<Guid>("SELECT event_id FROM fin.ar_application WHERE receipt_id = @r AND reverses_application_id IS NULL", ("r", receipt));
        await h.RunAsync(new UnapplyReceipt(h.CompanyId, w.W.Cobros, "un", receipt, application, "Nota de crédito por descuento"), new UnapplyReceiptHandler());
        var invoiceLine = await h.ScalarAsync<Guid>("SELECT invoice_line_id FROM sal.invoice_line WHERE invoice_id = @i", ("i", invoice));
        var note = (await h.RunAsync(new CreateCreditNote(h.CompanyId, w.W.Billing, "nc", invoice, "DESCUENTO", "Descuento comercial", [new(invoiceLine, 500.00m)]), new CreateCreditNoteHandler())).ResultRef;
        await h.RunAsync(new IssueCreditNote(h.CompanyId, await h.SessionWithRolesAsync("FACTURACION"), "nc-issue", note, 1), new IssueCreditNoteHandler());
        await h.RunAsync(
            new RecordExternalCreditNoteDocument(h.CompanyId, w.W.Billing, "nc-f", note, 2, "E340000000001", h.Clock.UtcNow.AddMinutes(-1), "Z9Y8X7", "e-cf-nc.xml", DeliveryTests.Hash, null, 500.00m, 90.00m, 590.00m),
            new RecordExternalCreditNoteDocumentHandler());
        var version = await h.ScalarAsync<long>("SELECT version FROM fin.receipt WHERE receipt_id = @r", ("r", receipt));
        await h.RunAsync(new ApplyReceipt(h.CompanyId, w.W.Cobros, "re", receipt, version, [new(invoice, 5310.00m)]), new ApplyReceiptHandler());

        // The 590.00 left on the receipt go back through the bank: never in cash (E-CF1-03-3); Cobros prepares, the Controller releases.
        var inCash = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareCustomerRefund(h.CompanyId, w.W.Cobros, "cash", receipt, w.W.Bank, "CASH", 590.00m, "Devolución por nota de crédito"), new PrepareCustomerRefundHandler()));
        var refund = (await h.RunAsync(
            new PrepareCustomerRefund(h.CompanyId, w.W.Cobros, "dev", receipt, w.W.Bank, "TRANSFER", 590.00m, "Devolución por nota de crédito NC-000001"), new PrepareCustomerRefundHandler())).ResultRef;
        await h.RunAsync(new ReleaseCustomerRefund(h.CompanyId, w.W.Controller, "rel", refund, 1), new ReleaseCustomerRefundHandler());

        Assert.Equal(RefundErrors.MethodInvalid, inCash.Code);
        Assert.Equal("PAID:0.00", await h.ScalarAsync<string>(
            "SELECT i.commercial_status || ':' || a.open_amount::numeric(19,2) FROM sal.invoice i JOIN fin.ar_document a ON a.ar_doc_id = i.ar_doc_id WHERE i.invoice_id = @i", ("i", invoice)));
        Assert.Equal("APPLIED:0.00:0.00", await ReceiptAsync(h, receipt));
        Assert.Equal("E340000000001::", await h.ScalarAsync<string>(
            "SELECT encf || ':' || coalesce(receiver_rnc, '') || ':' || coalesce(receiver_passport, '') FROM tax.external_fiscal_record WHERE credit_note_id = @n", ("n", note)));
        Assert.Equal("DEV-000001:RELEASED:590.00", await h.ScalarAsync<string>("SELECT refund_no || ':' || status || ':' || amount::numeric(19,2) FROM fin.customer_refund WHERE refund_id = @r", ("r", refund)));
        Assert.Equal("0.00|0.00|5310.00", await h.ScalarAsync<string>(
            """
            SELECT string_agg(coalesce((SELECT sum(e.debit - e.credit) FROM fin.gl_entry e
                                         WHERE e.account_role = r.role AND (r.role = 'BANK' OR e.party_id = @p) AND (r.role <> 'BANK' OR e.subledger_ref = @bank)), 0)::numeric(19,2)::text, '|' ORDER BY r.n)
            FROM (VALUES (1, 'AR_CONTROL'), (2, 'UNAPPLIED_RECEIPTS'), (3, 'BANK')) AS r (n, role)
            """,
            ("p", w.Consumer), ("bank", w.W.Bank)));
    }

    [Trait("AcceptanceCf1", "CF-11")]
    [Fact]
    public async Task CF11_a_paid_sale_without_deliveries_is_cancelled_and_its_receipt_is_left_free_to_refund()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var (order, line, receipt) = await PaidAsync(h, w, "s", 100m, 5900.00m);
        var planned = (await h.RunAsync(new PlanDelivery(h.CompanyId, w.W.S.Dispatch, "plan", order, [new(line, 60m)]), new PlanDeliveryHandler())).ResultRef;
        var version = await h.ScalarAsync<long>("SELECT version FROM sal.sales_order WHERE sales_order_id = @o", ("o", order));

        var withDelivery = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new CancelCashSale(h.CompanyId, w.Cashier, "x0", order, version, "El cliente desistió"), new CancelCashSaleHandler()));
        await h.RunAsync(new CancelDelivery(h.CompanyId, w.W.S.Dispatch, "plan-x", planned, 1, "La venta se cancela"), new CancelDeliveryHandler());
        version = await h.ScalarAsync<long>("SELECT version FROM sal.sales_order WHERE sales_order_id = @o", ("o", order));
        var noReason = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new CancelCashSale(h.CompanyId, w.Cashier, "x1", order, version, " "), new CancelCashSaleHandler()));
        var byCobros = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new CancelCashSale(h.CompanyId, w.W.Cobros, "x2", order, version, "El cliente desistió"), new CancelCashSaleHandler()));
        var cancelled = JsonDocument.Parse((await h.RunAsync(new CancelCashSale(h.CompanyId, w.Cashier, "x", order, version, "El cliente desistió"), new CancelCashSaleHandler())).ResultPayload).RootElement;
        var refund = (await h.RunAsync(
            new PrepareCustomerRefund(h.CompanyId, w.W.Cobros, "dev", receipt, w.W.Bank, "TRANSFER", 5900.00m, "Venta PV cancelada"), new PrepareCustomerRefundHandler())).ResultRef;

        Assert.Equal((SalesErrors.InvalidState, ReceiptErrors.ReasonRequired, AuthorizationErrors.NotAuthorized), (withDelivery.Code, noReason.Code, byCobros.Code));
        Assert.Equal(("CANCELLED", "5900.00"), (cancelled.GetProperty("status").GetString(), cancelled.GetProperty("released").GetString()));
        Assert.Equal("CANCELLED:0.00:El cliente desistió", await h.ScalarAsync<string>(
            "SELECT status || ':' || allocated_amount::numeric(19,2) || ':' || cancel_reason FROM sal.sales_order WHERE sales_order_id = @o", ("o", order)));
        Assert.Equal("UNAPPLIED:5900.00:0.00", await ReceiptAsync(h, receipt));
        Assert.Equal("PREPARED", await h.ScalarAsync<string>("SELECT status FROM fin.customer_refund WHERE refund_id = @r", ("r", refund)));
    }
}
