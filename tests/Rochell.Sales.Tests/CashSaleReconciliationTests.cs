using System.Text.Json;
using Rochell.Reconciliation;
using Rochell.Sales.CashSales;
using Rochell.Sales.Invoices;
using Rochell.Sales.Orders;
using Rochell.Sales.Receipts;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Rochell.Treasury.Statements;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>
/// CF1-04 (E-CF1-10, E-CF1-11, E-CF1-02-2): CASH-SALE — cash orders never have delivered more than what is collected with money
/// that counts, their assigned amounts agree with their assignments, and cash or cheques not deposited are warned about.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class CashSaleReconciliationTests(PostgresFixture postgres)
{
    private static readonly string[] Codes = ["AR-GL", "CASH-SALE", "CONTRACT-ASSET", "PROFORMA-ASIG", "RECEIPT-APPL"];

    private sealed record World(ReceiptTests.World W, Guid Cashier);

    private static async Task<World> WorldAsync(TestHarness h, bool alertDays = true)
    {
        var w = await ReceiptTests.WorldAsync(h, 100m);
        var actors = await h.FiscalActorsAsync();
        await h.ActivateRuleAsync(actors, "umbral", "CONSUMIDOR_ID", FiscalRuleKinds.ConsumerIdThreshold, """{"amount":"250000.00"}""", new DateOnly(2026, 1, 1));
        if (alertDays)
        {
            // The delivery setup's REVENUE_ACCOUNTING version, given the alert threshold (fixture: parameters of an ACTIVE version are frozen).
            await h.AdminRequireAsync(
                $"""
                SET LOCAL session_replication_role = replica;
                INSERT INTO acc.accounting_policy_parameter (company_id, policy_version_id, param_code, value)
                SELECT company_id, policy_version_id, 'cash_deposit_alert_days', to_jsonb('2'::text)
                FROM acc.accounting_policy_version WHERE company_id = '{h.CompanyId}' AND policy_code = 'REVENUE_ACCOUNTING' AND status = 'ACTIVE';
                """);
        }

        return new World(w, await h.SessionWithRolesAsync("CAJA"));
    }

    private static async Task<(Guid Order, Guid Line)> PendingAsync(TestHarness h, World w, string key, decimal blocks)
    {
        var order = (await h.RunAsync(
            new CreateCashSale(h.CompanyId, w.Cashier, key, w.W.S.Plant, DeliveryTerms.PickupAtPlant, null, null, [new SalesOrderLineInput(w.W.S.Block, "un", blocks)], "María Pérez"),
            new CreateCashSaleHandler())).ResultRef;
        await h.RunAsync(new SubmitCashSaleForPayment(h.CompanyId, w.Cashier, key + "-pay", order, 1), new SubmitCashSaleForPaymentHandler());
        return (order, await h.ScalarAsync<Guid>("SELECT line_id FROM sal.sales_order_line WHERE sales_order_id = @o", ("o", order)));
    }

    private static async Task<string> RunAsync(TestHarness h, World w, string key)
    {
        var result = JsonDocument.Parse((await h.RunAsync(new RunReconciliation(h.CompanyId, w.W.Controller, key, Codes), new RunReconciliationHandler())).ResultPayload).RootElement;
        return string.Join(',', result.GetProperty("runs").EnumerateArray().Select(r => $"{r.GetProperty("code").GetString()}:{r.GetProperty("status").GetString()}").Order(StringComparer.Ordinal));
    }

    private static Task<string?> FindingsAsync(TestHarness h)
        => h.ScalarAsync<string>(
            """
            SELECT string_agg(x.match_key || ':' || x.classification || ':' || coalesce(x.value_a::numeric(19,2)::text, '-') || ':' || coalesce(x.value_b::numeric(19,2)::text, '-'), ','
                              ORDER BY x.match_key, x.classification)
            FROM rec.recon_exception x JOIN rec.recon_run r USING (run_id)
            WHERE r.recon_code = 'CASH-SALE' AND r.run_id = (SELECT run_id FROM rec.recon_run WHERE recon_code = 'CASH-SALE' ORDER BY run_id DESC LIMIT 1)
            """);

    [Trait("AcceptanceCf1", "CF-12")]
    [Fact]
    public async Task CF12_a_month_with_cash_sales_paid_delivered_and_invoiced_reconciles()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);

        // One sale paid by transfer, delivered in two pickups, the first invoiced; another paid and not delivered; a third pending.
        var (order, line) = await PendingAsync(h, w, "s", 100m);
        var consumer = await h.ScalarAsync<Guid>("SELECT party_id FROM md.party WHERE party_kind = 'CONSUMER'");
        var receipt = (await h.RunAsync(new RecordReceipt(h.CompanyId, w.Cashier, "r", consumer, "TRANSFER", 6490.00m, ReceiptTests.Today(h), w.W.Bank, "TRF-1"), new RecordReceiptHandler())).ResultRef;
        await h.RunAsync(new AllocateReceiptToOrder(h.CompanyId, w.Cashier, "a", receipt, 1, order, 5900.00m), new AllocateReceiptToOrderHandler());
        var (_, first) = await DeliveryTests.DispatchAsync(h, w.W.S, order, line, 60m, own: false, "cs-d1");
        await DeliveryTests.DispatchAsync(h, w.W.S, order, line, 40m, own: false, "cs-d2");
        var invoice = (await h.RunAsync(new CreateInvoiceFromDeliveries(h.CompanyId, w.W.Billing, "cs-i", consumer, [first]), new CreateInvoiceFromDeliveriesHandler())).ResultRef;
        await h.RunAsync(new IssueInvoice(h.CompanyId, w.W.Billing, "cs-issue", invoice, 1), new IssueInvoiceHandler());
        var (paid, _) = await PendingAsync(h, w, "p", 10m);
        var version = await h.ScalarAsync<long>("SELECT version FROM fin.receipt WHERE receipt_id = @r", ("r", receipt));
        await h.RunAsync(new AllocateReceiptToOrder(h.CompanyId, w.Cashier, "a2", receipt, version, paid, 590.00m), new AllocateReceiptToOrderHandler());
        await PendingAsync(h, w, "u", 5m);

        Assert.Equal("AR-GL:MATCHED,CASH-SALE:MATCHED,CONTRACT-ASSET:MATCHED,PROFORMA-ASIG:MATCHED,RECEIPT-APPL:MATCHED", await RunAsync(h, w, "recon"));
        Assert.Equal("AR-REC", await h.ScalarAsync<string>("SELECT component FROM rec.recon_blocking WHERE recon_code = 'CASH-SALE'"));
    }

    [Fact]
    public async Task A_delivery_left_unpaid_by_a_bounced_cheque_blocks_and_cash_kept_without_depositing_is_warned_about()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var w = await WorldAsync(h);
        var (order, line) = await PendingAsync(h, w, "s", 100m);
        var consumer = await h.ScalarAsync<Guid>("SELECT party_id FROM md.party WHERE party_kind = 'CONSUMER'");
        var cheque = (await h.RunAsync(
            new RecordReceipt(h.CompanyId, w.Cashier, "chq", consumer, "CHEQUE", 5900.00m, ChequeBank: "Banco Popular", ChequeNo: "000777", ChequeDate: ReceiptTests.Today(h)), new RecordReceiptHandler())).ResultRef;
        await h.RunAsync(new AllocateReceiptToOrder(h.CompanyId, w.Cashier, "a", cheque, 1, order, 5900.00m), new AllocateReceiptToOrderHandler());
        var deposit = (await h.RunAsync(new DepositReceipts(h.CompanyId, w.W.Cobros, "dep", w.W.Bank, [cheque]), new DepositReceiptsHandler())).ResultRef;
        await ReceiptTests.Import(h, w.W, "st", "DEP-9,Deposito cheque 000777,,5900.00", "DEV-9,Cheque devuelto 000777,5900.00,");
        await h.RunAsync(
            new MatchBankLineToReceipt(h.CompanyId, w.W.Treasurer, "m", await ReceiptTests.LineAsync(h, "Deposito cheque 000777"), 1, 1, DepositId: deposit), new MatchBankLineToReceiptHandler());
        await h.RunAsync(new ConfirmCashSale(h.CompanyId, w.Cashier, "v", order), new ConfirmCashSaleHandler());

        // 60 blocks leave (3,540.00 with ITBIS); then the cheque bounces: delivered and not collected.
        await DeliveryTests.DispatchAsync(h, w.W.S, order, line, 60m, own: false, "cs-d1");
        var version = await h.ScalarAsync<long>("SELECT version FROM fin.receipt WHERE receipt_id = @r", ("r", cheque));
        await h.RunAsync(new MarkReceiptBounced(h.CompanyId, w.W.Treasurer, "bounce", cheque, version, "Fondos insuficientes"), new MarkReceiptBouncedHandler());

        // The customer brings 1,000.00 in cash, assigned to the order and kept in the drawer for three days (the alert is at 2).
        var cash = (await h.RunAsync(new RecordReceipt(h.CompanyId, w.Cashier, "cash", consumer, "CASH", 1000.00m), new RecordReceiptHandler())).ResultRef;
        await h.RunAsync(new AllocateReceiptToOrder(h.CompanyId, w.Cashier, "a-cash", cash, 1, order, 1000.00m), new AllocateReceiptToOrderHandler());
        var sameDay = (await RunAsync(h, w, "recon-1"), await FindingsAsync(h));
        clock.Advance(TimeSpan.FromDays(3));
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var later = JsonDocument.Parse((await h.RunAsync(new RunReconciliation(h.CompanyId, controller, "recon-2", ["CASH-SALE"]), new RunReconciliationHandler())).ResultPayload).RootElement;
        var receiptNo = await h.ScalarAsync<string>("SELECT receipt_no FROM fin.receipt WHERE receipt_id = @r", ("r", cash));

        Assert.Contains("CASH-SALE:EXCEPTIONS", sameDay.Item1, StringComparison.Ordinal);
        Assert.Contains("PROFORMA-ASIG:MATCHED", sameDay.Item1, StringComparison.Ordinal);
        Assert.Equal("PV:PV-000002:CASH_SALE_UNPAID:3540.00:1000.00", sameDay.Item2);
        Assert.Equal("EXCEPTIONS", later.GetProperty("runs")[0].GetProperty("status").GetString());
        Assert.Equal($"PV:PV-000002:CASH_SALE_UNPAID:3540.00:1000.00,REC:{receiptNo}:CASH_UNDEPOSITED:1000.00:3.00", await FindingsAsync(h));
    }

    [Fact]
    public async Task Without_the_alert_parameter_the_run_fails_only_when_cash_or_cheques_wait_for_their_deposit()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h, alertDays: false);
        var (order, _) = await PendingAsync(h, w, "s", 10m);
        var consumer = await h.ScalarAsync<Guid>("SELECT party_id FROM md.party WHERE party_kind = 'CONSUMER'");

        var nothingInTransit = await RunAsync(h, w, "recon-1");
        var cash = (await h.RunAsync(new RecordReceipt(h.CompanyId, w.Cashier, "cash", consumer, "CASH", 590.00m), new RecordReceiptHandler())).ResultRef;
        await h.RunAsync(new AllocateReceiptToOrder(h.CompanyId, w.Cashier, "a", cash, 1, order, 590.00m), new AllocateReceiptToOrderHandler());
        var inTransit = await RunAsync(h, w, "recon-2");

        // Tampered by the owner role, as a defect would leave it: the order's assigned amount without its assignment.
        await h.AdminRequireAsync($"SET LOCAL session_replication_role = replica; UPDATE sal.sales_order SET allocated_amount = 100 WHERE sales_order_id = '{order}'");
        await h.AdminRequireAsync(
            $"""
            SET LOCAL session_replication_role = replica;
            INSERT INTO acc.accounting_policy_parameter (company_id, policy_version_id, param_code, value)
            SELECT company_id, policy_version_id, 'cash_deposit_alert_days', to_jsonb('2'::text)
            FROM acc.accounting_policy_version WHERE company_id = '{h.CompanyId}' AND policy_code = 'REVENUE_ACCOUNTING' AND status = 'ACTIVE';
            """);
        var tampered = await RunAsync(h, w, "recon-3");

        Assert.Contains("CASH-SALE:MATCHED", nothingInTransit, StringComparison.Ordinal);
        Assert.Contains("CASH-SALE:FAILED", inTransit, StringComparison.Ordinal);
        Assert.Contains("CASH-SALE:EXCEPTIONS", tampered, StringComparison.Ordinal);
        Assert.Equal("PV:PV-000002:ORDER_ALLOCATION_DIFFERENCE:100.00:590.00", await FindingsAsync(h));
    }
}
