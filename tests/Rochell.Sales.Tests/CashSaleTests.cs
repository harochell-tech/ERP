using Rochell.Platform.Commands;
using Rochell.Sales.CashSales;
using Rochell.Sales.Deliveries;
using Rochell.Sales.Orders;
using Rochell.Sales.Proformas;
using Rochell.Sales.Receipts;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Rochell.Treasury.Statements;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>
/// CF1-02 (E-CF1-1…7, 13, E-CF1-01-1…6, E-CF1-02-1…3): the cash sale to the final consumer — the consumer created on first use, the
/// buyer, what must be paid, receipts assigned without a journal, confirmation only with money that counts, and nothing dispatched
/// while it is not covered. BLOQUE-6 at 50.00 with ITBIS 18 %; the identification is mandatory from 250,000.00.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class CashSaleTests(PostgresFixture postgres)
{
    private sealed record World(ReceiptTests.World W, Guid Cashier);

    private static async Task<World> WorldAsync(TestHarness h, string? threshold = "250000.00")
    {
        var w = await ReceiptTests.WorldAsync(h, 100m);
        if (threshold is not null)
        {
            var actors = await h.FiscalActorsAsync();
            await h.ActivateRuleAsync(actors, "umbral", "CONSUMIDOR_ID", FiscalRuleKinds.ConsumerIdThreshold, $$"""{"amount":"{{threshold}}"}""", new DateOnly(2026, 1, 1));
        }

        return new World(w, await h.SessionWithRolesAsync("CAJA"));
    }

    private static CreateCashSale Sale(TestHarness h, World w, string key, decimal blocks, string? idKind = null, string? id = null, Guid? session = null)
        => new(h.CompanyId, session ?? w.Cashier, key, w.W.S.Plant, DeliveryTerms.PickupAtPlant, null, null, [new SalesOrderLineInput(w.W.S.Block, "un", blocks)], "María Pérez", "809-555-0101", idKind, id);

    /// <summary>A sale of <paramref name="blocks"/> blocks sent to payment; returns the order.</summary>
    private static async Task<Guid> PendingAsync(TestHarness h, World w, string key, decimal blocks)
    {
        var order = (await h.RunAsync(Sale(h, w, key, blocks), new CreateCashSaleHandler())).ResultRef;
        await h.RunAsync(new SubmitCashSaleForPayment(h.CompanyId, w.Cashier, key + "-pay", order, 1), new SubmitCashSaleForPaymentHandler());
        return order;
    }

    private static Task<Guid> ConsumerAsync(TestHarness h) => h.ScalarAsync<Guid>("SELECT party_id FROM md.party WHERE company_id = @c AND party_kind = 'CONSUMER'", ("c", h.CompanyId));

    private static Task<string?> OrderAsync(TestHarness h, Guid order)
        => h.ScalarAsync<string>(
            "SELECT status || ':' || coalesce(payment_total::numeric(19,2)::text, '-') || ':' || allocated_amount::numeric(19,2) FROM sal.sales_order WHERE sales_order_id = @o", ("o", order));

    private static async Task<Guid> CashAsync(TestHarness h, World w, string key, decimal amount)
        => (await h.RunAsync(new RecordReceipt(h.CompanyId, w.Cashier, key, await ConsumerAsync(h), "CASH", amount), new RecordReceiptHandler())).ResultRef;

    [Trait("AcceptanceCf1", "CF-01")]
    [Fact]
    public async Task CF01_the_first_cash_sale_creates_the_final_consumer_once_and_a_draft_order_with_its_buyer()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);

        var first = (await h.RunAsync(Sale(h, w, "s1", 100m, "cedula", "402-1234567-8"), new CreateCashSaleHandler())).ResultRef;
        var second = (await h.RunAsync(Sale(h, w, "s2", 10m, session: w.W.S.Seller), new CreateCashSaleHandler())).ResultRef;
        var consumer = await ConsumerAsync(h);
        var badId = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Sale(h, w, "bad", 10m, "CEDULA", "4021234567"), new CreateCashSaleHandler()));
        var kindOnly = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Sale(h, w, "kind", 10m, "PASAPORTE"), new CreateCashSaleHandler()));
        var asCredit = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new CreateSalesOrder(h.CompanyId, w.W.S.Seller, "credit", consumer, w.W.S.Plant, DeliveryTerms.PickupAtPlant, null, null, null, [new SalesOrderLineInput(w.W.S.Block, "un", 10m)]),
            new CreateSalesOrderHandler()));
        var cobros = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(Sale(h, w, "cobros", 10m, session: w.W.Cobros), new CreateCashSaleHandler()));

        Assert.Equal("CONSUMER::Consumidor final:ACTIVE:ACTIVE:true:false", await h.ScalarAsync<string>(
            "SELECT party_kind || ':' || coalesce(rnc, '') || ':' || legal_name || ':' || status || ':' || customer_status || ':' || is_customer::text || ':' || is_supplier::text FROM md.party WHERE party_id = @p",
            ("p", consumer)));
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM md.party WHERE party_kind = 'CONSUMER'"));
        Assert.Equal("DRAFT:true:5000.00:María Pérez:809-555-0101:CEDULA:40212345678", await h.ScalarAsync<string>(
            """
            SELECT status || ':' || cash_sale::text || ':' || total_net::numeric(19,2) || ':' || buyer_name || ':' || buyer_phone || ':' || buyer_id_kind || ':' || buyer_id
            FROM sal.sales_order WHERE sales_order_id = @o
            """,
            ("o", first)));
        Assert.Equal(consumer, await h.ScalarAsync<Guid>("SELECT party_id FROM sal.sales_order WHERE sales_order_id = @o", ("o", second)));
        Assert.Equal(
            (CashSaleErrors.BuyerInvalid, CashSaleErrors.BuyerInvalid, OrderErrors.UseCashSale, AuthorizationErrors.NotAuthorized), (badId.Code, kindOnly.Code, asCredit.Code, cobros.Code));
    }

    [Trait("AcceptanceCf1", "CF-02")]
    [Trait("AcceptanceCf1", "CF-03")]
    [Fact]
    public async Task CF02_03_a_sale_sent_to_payment_owes_its_net_plus_ITBIS_and_is_confirmed_when_cash_assigned_covers_it_without_a_journal()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var order = (await h.RunAsync(Sale(h, w, "s", 100m), new CreateCashSaleHandler())).ResultRef;

        // 100 blocks × 50.00 = 5,000.00 + 18 % = 5,900.00 to pay. Never through credit.
        var toCredit = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SubmitForCredit(h.CompanyId, w.W.S.Seller, "credit", order, 1), new SubmitForCreditHandler()));
        await h.RunAsync(new SubmitCashSaleForPayment(h.CompanyId, w.Cashier, "pay", order, 1), new SubmitCashSaleForPaymentHandler());
        var pending = await OrderAsync(h, order);
        var planEarly = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new PlanDelivery(h.CompanyId, w.W.S.Dispatch, "early", order, []), new PlanDeliveryHandler()));

        // Two cash receipts: 4,000.00 leaves it pending; the rest confirms it. Nothing posts beyond the receipts (P-23).
        var part = await CashAsync(h, w, "r1", 4000.00m);
        var rest = await CashAsync(h, w, "r2", 2000.00m);
        var other = (await ReceiptTests.Transfer(h, w.W, "other", 1900.00m)).ResultRef;
        var journals = await h.ScalarAsync<long>("SELECT count(*) FROM fin.gl_journal");
        await h.RunAsync(new AllocateReceiptToOrder(h.CompanyId, w.Cashier, "a1", part, 1, order, 4000.00m), new AllocateReceiptToOrderHandler());
        var partly = await OrderAsync(h, order);
        var notYet = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ConfirmCashSale(h.CompanyId, w.Cashier, "verify", order), new ConfirmCashSaleHandler()));
        var tooMuch = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new AllocateReceiptToOrder(h.CompanyId, w.Cashier, "a-over", rest, 1, order, 2000.00m), new AllocateReceiptToOrderHandler()));
        var foreign = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new AllocateReceiptToOrder(h.CompanyId, w.W.Cobros, "a-foreign", other, 1, order, 1900.00m), new AllocateReceiptToOrderHandler()));
        await h.RunAsync(new AllocateReceiptToOrder(h.CompanyId, w.Cashier, "a2", rest, 1, order, 1900.00m), new AllocateReceiptToOrderHandler());

        Assert.Equal((OrderErrors.UseCashSale, SalesErrors.LinesRequired), (toCredit.Code, planEarly.Code));
        Assert.Equal(("PENDING_PAYMENT:5900.00:0.00", "PENDING_PAYMENT:5900.00:4000.00", "CONFIRMED:5900.00:5900.00"), (pending, partly, await OrderAsync(h, order)));
        Assert.Equal((CashSaleErrors.NotPaid, CashSaleErrors.ExceedsDue, SalesErrors.InvalidState), (notYet.Code, tooMuch.Code, foreign.Code));
        Assert.Equal(journals, await h.ScalarAsync<long>("SELECT count(*) FROM fin.gl_journal"));
        Assert.Equal("4000.00:4000.00,2000.00:1900.00", await h.ScalarAsync<string>(
            "SELECT string_agg(unapplied_amount::numeric(19,2) || ':' || allocated_amount::numeric(19,2), ',' ORDER BY receipt_no) FROM fin.receipt WHERE receipt_id IN (@a, @b)", ("a", part), ("b", rest)));
        Assert.Equal("DRAFT>PENDING_PAYMENT,PENDING_PAYMENT>CONFIRMED", await h.ScalarAsync<string>(
            "SELECT string_agg(from_state || '>' || to_state, ',' ORDER BY state_history_id) FROM core.state_history WHERE aggregate_id = @o AND from_state IS NOT NULL", ("o", order)));

        // Confirmed and covered: it is planned like any order.
        var line = await h.ScalarAsync<Guid>("SELECT line_id FROM sal.sales_order_line WHERE sales_order_id = @o", ("o", order));
        await h.RunAsync(new PlanDelivery(h.CompanyId, w.W.S.Dispatch, "plan", order, [new(line, 60m)]), new PlanDeliveryHandler());
    }

    [Trait("AcceptanceCf1", "CF-04")]
    [Trait("AcceptanceCf1", "CF-05")]
    [Fact]
    public async Task CF04_05_a_cheque_counts_once_its_deposit_is_matched_with_the_statement_and_a_bounce_stops_the_dispatch()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var order = await PendingAsync(h, w, "s", 100m);
        var consumer = await ConsumerAsync(h);
        var cheque = (await h.RunAsync(
            new RecordReceipt(h.CompanyId, w.Cashier, "chq", consumer, "CHEQUE", 5900.00m, ChequeBank: "Banco Popular", ChequeNo: "000777", ChequeDate: ReceiptTests.Today(h)), new RecordReceiptHandler())).ResultRef;

        // Assigned but not in the bank: the sale waits, whoever presses «Verificar pago».
        await h.RunAsync(new AllocateReceiptToOrder(h.CompanyId, w.Cashier, "a", cheque, 1, order, 5900.00m), new AllocateReceiptToOrderHandler());
        var assigned = await OrderAsync(h, order);
        var inHand = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ConfirmCashSale(h.CompanyId, w.Cashier, "v1", order), new ConfirmCashSaleHandler()));
        var deposit = (await h.RunAsync(new DepositReceipts(h.CompanyId, w.W.Cobros, "dep", w.W.Bank, [cheque]), new DepositReceiptsHandler())).ResultRef;
        var deposited = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ConfirmCashSale(h.CompanyId, w.Cashier, "v2", order), new ConfirmCashSaleHandler()));

        // The bank credits the deposit; Treasury matches it; «Verificar pago» confirms the sale and a delivery is planned.
        await ReceiptTests.Import(h, w.W, "st", "DEP-9,Deposito cheque 000777,,5900.00", "DEV-9,Cheque devuelto 000777,5900.00,");
        await h.RunAsync(
            new MatchBankLineToReceipt(h.CompanyId, w.W.Treasurer, "m", await ReceiptTests.LineAsync(h, "Deposito cheque 000777"), 1, 1, DepositId: deposit), new MatchBankLineToReceiptHandler());
        await h.RunAsync(new ConfirmCashSale(h.CompanyId, w.Cashier, "v3", order), new ConfirmCashSaleHandler());
        var confirmed = await OrderAsync(h, order);
        var line = await h.ScalarAsync<Guid>("SELECT line_id FROM sal.sales_order_line WHERE sales_order_id = @o", ("o", order));
        var planned = (await h.RunAsync(new PlanDelivery(h.CompanyId, w.W.S.Dispatch, "plan", order, [new(line, 60m)]), new PlanDeliveryHandler())).ResultRef;
        var deliveryLine = await h.ScalarAsync<Guid>("SELECT delivery_line_id FROM log.delivery_line WHERE delivery_id = @d", ("d", planned));
        await h.RunAsync(new StartLoading(h.CompanyId, w.W.S.Dispatch, "load", planned, 1, null, null, "A 123-456", "Pedro Cliente"), new StartLoadingHandler());
        await h.RunAsync(new ConfirmLoaded(h.CompanyId, w.W.S.Dispatch, "loaded", planned, 2, [new(deliveryLine, w.W.S.Patio)]), new ConfirmLoadedHandler());

        // The cheque bounces: its assignment is released, the order keeps its state, and nothing leaves or is planned (E-CF1-02-2).
        var version = await h.ScalarAsync<long>("SELECT version FROM fin.receipt WHERE receipt_id = @r", ("r", cheque));
        await h.RunAsync(new MarkReceiptBounced(h.CompanyId, w.W.Treasurer, "bounce", cheque, version, "Fondos insuficientes"), new MarkReceiptBouncedHandler());
        var gate = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new RecordGateOut(h.CompanyId, w.W.S.Dispatch, "gate", planned, 3, 14000m, 8000m, "TK-9", DeliveryTests.Hash), new RecordGateOutHandler()));
        var plan = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new PlanDelivery(h.CompanyId, w.W.S.Dispatch, "plan-2", order, [new(line, 40m)]), new PlanDeliveryHandler()));

        // Paid again in cash: the same delivery leaves.
        var cash = await CashAsync(h, w, "cash", 5900.00m);
        await h.RunAsync(new AllocateReceiptToOrder(h.CompanyId, w.Cashier, "a-cash", cash, 1, order, 5900.00m), new AllocateReceiptToOrderHandler());
        await h.RunAsync(new RecordGateOut(h.CompanyId, w.W.S.Dispatch, "gate-2", planned, 3, 14000m, 8000m, "TK-9", DeliveryTests.Hash), new RecordGateOutHandler());

        Assert.Equal(("PENDING_PAYMENT:5900.00:5900.00", "CONFIRMED:5900.00:5900.00"), (assigned, confirmed));
        Assert.Equal((CashSaleErrors.NotPaid, CashSaleErrors.NotPaid, CashSaleErrors.NotPaid, CashSaleErrors.NotPaid), (inHand.Code, deposited.Code, gate.Code, plan.Code));
        Assert.Equal("BOUNCED:0.00", await h.ScalarAsync<string>("SELECT status || ':' || allocated_amount::numeric(19,2) FROM fin.receipt WHERE receipt_id = @r", ("r", cheque)));
        Assert.Equal("PARTIALLY_DELIVERED:5900.00:5900.00", await OrderAsync(h, order));
    }

    [Trait("AcceptanceCf1", "CF-06")]
    [Trait("AcceptanceCf1", "CF-07")]
    [Fact]
    public async Task CF06_07_the_buyers_identification_is_mandatory_from_the_rules_amount_and_without_the_rule_nothing_goes_to_payment()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h, threshold: null);
        var small = (await h.RunAsync(Sale(h, w, "small", 100m), new CreateCashSaleHandler())).ResultRef;

        var noRule = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SubmitCashSaleForPayment(h.CompanyId, w.Cashier, "p0", small, 1), new SubmitCashSaleForPaymentHandler()));

        // The rule: identification from 5,900.00. The sale of exactly 5,900.00 needs it; one of 5,841.00 (99 blocks) does not.
        var actors = await h.FiscalActorsAsync();
        await h.ActivateRuleAsync(actors, "umbral", "CONSUMIDOR_ID", FiscalRuleKinds.ConsumerIdThreshold, """{"amount":"5900.00"}""", new DateOnly(2026, 1, 1));
        var anonymous = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new SubmitCashSaleForPayment(h.CompanyId, w.Cashier, "p1", small, 1), new SubmitCashSaleForPaymentHandler()));
        await h.RunAsync(
            new UpdateCashSaleDraft(h.CompanyId, w.Cashier, "id", small, 1, w.W.S.Plant, DeliveryTerms.PickupAtPlant, null, null, [new SalesOrderLineInput(w.W.S.Block, "un", 100m)], "María Pérez", null, "PASAPORTE", "ab1234567"),
            new UpdateCashSaleDraftHandler());
        await h.RunAsync(new SubmitCashSaleForPayment(h.CompanyId, w.Cashier, "p2", small, 2), new SubmitCashSaleForPaymentHandler());
        var below = (await h.RunAsync(Sale(h, w, "below", 99m), new CreateCashSaleHandler())).ResultRef;
        await h.RunAsync(new SubmitCashSaleForPayment(h.CompanyId, w.Cashier, "p3", below, 1), new SubmitCashSaleForPaymentHandler());
        var definition = await Assert.ThrowsAsync<DomainException>(() => h.ConfigureAsync(actors, "bad", "OTRO_UMBRAL", FiscalRuleKinds.ConsumerIdThreshold, """{"amount":"0"}""", new DateOnly(2026, 1, 1)));

        Assert.Equal((CashSaleErrors.ThresholdRuleMissing, CashSaleErrors.BuyerIdRequired, TaxErrors.FiscalRuleInvalid), (noRule.Code, anonymous.Code, definition.Code));
        Assert.Equal("PENDING_PAYMENT:5900.00:PASAPORTE:AB1234567", await h.ScalarAsync<string>(
            "SELECT status || ':' || payment_total::numeric(19,2) || ':' || buyer_id_kind || ':' || buyer_id FROM sal.sales_order WHERE sales_order_id = @o", ("o", small)));
        Assert.Equal("PENDING_PAYMENT:5841.00:0.00", await OrderAsync(h, below));
    }

    [Fact]
    public async Task An_assignment_is_released_with_a_reason_while_the_sale_waits_and_a_sale_with_money_assigned_is_not_cancelled_or_redrafted()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var order = await PendingAsync(h, w, "s", 100m);
        var cash = await CashAsync(h, w, "r", 3000.00m);
        var allocated = System.Text.Json.JsonDocument.Parse(
            (await h.RunAsync(new AllocateReceiptToOrder(h.CompanyId, w.Cashier, "a", cash, 1, order, 3000.00m), new AllocateReceiptToOrderHandler())).ResultPayload).RootElement;
        var eventId = allocated.GetProperty("allocationEventId").GetGuid();
        var version = await h.ScalarAsync<long>("SELECT version FROM sal.sales_order WHERE sales_order_id = @o", ("o", order));

        var redraft = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ReturnCashSaleToDraft(h.CompanyId, w.Cashier, "back", order, version), new ReturnCashSaleToDraftHandler()));
        var cancel = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new CancelSalesOrder(h.CompanyId, w.W.S.Seller, "cancel", order, version, "El cliente desistió"), new CancelSalesOrderHandler()));
        var noReason = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ReleaseOrderAllocation(h.CompanyId, w.Cashier, "rel0", cash, eventId, " "), new ReleaseOrderAllocationHandler()));
        await h.RunAsync(new ReleaseOrderAllocation(h.CompanyId, w.Cashier, "rel", cash, eventId, "Se asignó al pedido equivocado"), new ReleaseOrderAllocationHandler());
        var twice = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ReleaseOrderAllocation(h.CompanyId, w.Cashier, "rel2", cash, eventId, "otra vez"), new ReleaseOrderAllocationHandler()));
        var released = await OrderAsync(h, order);
        version = await h.ScalarAsync<long>("SELECT version FROM sal.sales_order WHERE sales_order_id = @o", ("o", order));
        await h.RunAsync(new ReturnCashSaleToDraft(h.CompanyId, w.Cashier, "back-2", order, version), new ReturnCashSaleToDraftHandler());

        Assert.Equal(("2900.00", "PENDING_PAYMENT"), (allocated.GetProperty("stillToPay").GetString(), allocated.GetProperty("orderStatus").GetString()));
        Assert.Equal((SalesErrors.InvalidState, SalesErrors.InvalidState, ReceiptErrors.ReasonRequired, AllocationErrors.NotFound), (redraft.Code, cancel.Code, noReason.Code, twice.Code));
        Assert.Equal(("PENDING_PAYMENT:5900.00:0.00", "DRAFT:-:0.00"), (released, await OrderAsync(h, order)));
        Assert.Equal("3000.00:0.00", await h.ScalarAsync<string>("SELECT unapplied_amount::numeric(19,2) || ':' || allocated_amount::numeric(19,2) FROM fin.receipt WHERE receipt_id = @r", ("r", cash)));
    }
}
