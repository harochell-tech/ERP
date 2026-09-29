using Rochell.Audit;
using Rochell.Platform.Commands;
using Rochell.Reconciliation;
using Rochell.Sales.CreditNotes;
using Rochell.Sales.Deliveries;
using Rochell.Sales.Invoices;
using Rochell.Sales.Orders;
using Rochell.Sales.Receipts;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>
/// E-VS3-11-3: sales races, throttled like CC-04 — gate-outs that exhaust the stock, credit notes on one invoice line, an application
/// racing a bounced cheque, and a receipt dated in a month racing its AR-REC close. SAL-09, concurrent credit submissions and
/// concurrent applications of one receipt are in InvoiceTests, SalesOrderTests and ReceiptTests.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SalesConcurrencyTests(PostgresFixture postgres)
{
    private const int Racers = 8;

    /// <summary>Runs the actions together; returns each one's error code (null when it succeeded).</summary>
    private static async Task<List<string?>> RaceAsync(IEnumerable<Func<Task>> actions)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var throttle = new SemaphoreSlim(Racers);
        var tasks = actions.Select(action => Task.Run(async () =>
        {
            await gate.Task;
            await throttle.WaitAsync();
            try
            {
                await action();
                return (string?)null;
            }
            catch (DomainException ex)
            {
                return ex.Code;
            }
            finally
            {
                throttle.Release();
            }
        })).ToList();
        gate.SetResult();
        return [.. await Task.WhenAll(tasks)];
    }

    [Fact]
    public async Task Gate_outs_that_exhaust_the_stock_at_once_never_leave_it_negative()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await DeliveryTests.SetupAsync(h); // 2,000 blocks in stock
        var (order, orderLine) = await DeliveryTests.ConfirmedOrderAsync(h, s, DeliveryTerms.PickupAtPlant, 2500m);
        var deliveries = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var delivery = (await h.RunAsync(new PlanDelivery(h.CompanyId, s.Dispatch, $"p{i}", order, [new(orderLine, 500m)]), new PlanDeliveryHandler())).ResultRef;
            var line = await h.ScalarAsync<Guid>("SELECT delivery_line_id FROM log.delivery_line WHERE delivery_id = @d", ("d", delivery));
            await h.RunAsync(new StartLoading(h.CompanyId, s.Dispatch, $"l{i}", delivery, 1, null, null, "A 123-456", "Pedro Cliente"), new StartLoadingHandler());
            await h.RunAsync(new ConfirmLoaded(h.CompanyId, s.Dispatch, $"c{i}", delivery, 2, [new(line, s.Patio)]), new ConfirmLoadedHandler());
            deliveries.Add(delivery);
        }

        var outcomes = await RaceAsync(deliveries.Select((d, i) => (Func<Task>)(() =>
            h.RunAsync(new RecordGateOut(h.CompanyId, s.Dispatch, $"g{i}", d, 3, 9000m, 8000m, $"TK-{i}", DeliveryTests.Hash), new RecordGateOutHandler()))));

        // 4 × 500 = 2,000 leave; the fifth finds no stock.
        Assert.Equal(4, outcomes.Count(o => o is null));
        Assert.Equal(DeliveryErrors.InsufficientStock, Assert.Single(outcomes, o => o is not null));
        Assert.Equal("0.000000|0", await h.ScalarAsync<string>(
            "SELECT sum(quantity)::numeric(18,6)::text || '|' || count(*) FILTER (WHERE quantity < 0) FROM inv.inv_stock_balance WHERE item_id = @i", ("i", s.Block)));
        Assert.Equal("2000.000000", await h.ScalarAsync<string>("SELECT qty_delivered::text FROM sal.sales_order_line WHERE line_id = @l", ("l", orderLine)));
    }

    [Fact]
    public async Task Credit_notes_on_one_invoice_line_issued_at_once_never_credit_more_than_its_net()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await ReceiptTests.WorldAsync(h); // FA-000001: 50,000.00 net + 9,000.00 ITBIS
        await h.RunAsync(
            new RecordExternalFiscalDocument(h.CompanyId, w.Billing, "fisc", w.Invoice, 2, "E310000000001", h.Clock.UtcNow.AddMinutes(-2), "A1", "e-cf.xml", DeliveryTests.Hash,
                "131925332", 50000.00m, 9000.00m, 59000.00m),
            new RecordExternalFiscalDocumentHandler());
        var line = await h.ScalarAsync<Guid>("SELECT invoice_line_id FROM sal.invoice_line WHERE invoice_id = @i", ("i", w.Invoice));
        var notes = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            notes.Add((await h.RunAsync(new CreateCreditNote(h.CompanyId, w.Billing, $"n{i}", w.Invoice, "DESCUENTO", "Descuento", [new(line, 20000.00m)]), new CreateCreditNoteHandler())).ResultRef);
        }

        var issuer = await h.SessionWithRolesAsync("FACTURACION");
        var outcomes = await RaceAsync(notes.Select((n, i) => (Func<Task>)(() => h.RunAsync(new IssueCreditNote(h.CompanyId, issuer, $"i{i}", n, 1), new IssueCreditNoteHandler()))));

        // Two notes of 20,000.00 fit the 50,000.00 line; a third would credit 60,000.00.
        Assert.Equal(2, outcomes.Count(o => o is null));
        Assert.All(outcomes.Where(o => o is not null), code => Assert.Equal(CreditNoteErrors.ExceedsCreditable, code));
        Assert.Equal("40000.00|11800.00", await h.ScalarAsync<string>(
            """
            SELECT (SELECT sum(net_total) FROM sal.credit_note WHERE commercial_status = 'CONFIRMED')::numeric(19,2)::text || '|' ||
                   (SELECT open_amount FROM fin.ar_document WHERE ar_doc_id = @a)::numeric(19,2)::text
            """,
            ("a", w.ArDoc)));
    }

    [Fact]
    public async Task An_application_racing_the_bounce_of_its_cheque_leaves_a_coherent_receipt_and_invoice()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await ReceiptTests.WorldAsync(h); // 59,000.00 open
        var cheque = (await h.RunAsync(
            new RecordReceipt(h.CompanyId, w.Cobros, "chq", w.S.Customer, "CHEQUE", 59000.00m, ChequeBank: "Banco Popular", ChequeNo: "000123", ChequeDate: ReceiptTests.Today(h)),
            new RecordReceiptHandler())).ResultRef;
        await h.RunAsync(new DepositReceipts(h.CompanyId, w.Cobros, "dep", w.Bank, [cheque]), new DepositReceiptsHandler());

        var outcomes = await RaceAsync([
            () => h.RunAsync(new ApplyReceipt(h.CompanyId, w.Cobros, "apply", cheque, 2, [new(w.Invoice, 59000.00m)]), new ApplyReceiptHandler()),
            () => h.RunAsync(new MarkReceiptBounced(h.CompanyId, w.Treasurer, "bounce", cheque, 2, "Fondos insuficientes"), new MarkReceiptBouncedHandler()),
        ]);

        // Whichever locks the receipt first wins; the other finds it changed. Never a bounced cheque still paying the invoice.
        Assert.Equal(1, outcomes.Count(o => o is null));
        Assert.Contains(outcomes.Single(o => o is not null), new[] { SalesErrors.VersionConflict, SalesErrors.InvalidState });
        var state = await h.ScalarAsync<string>(
            """
            SELECT r.status || ':' || r.application_status || '|' || i.commercial_status || ':' || a.open_amount::numeric(19,2)::text
            FROM fin.receipt r, sal.invoice i JOIN fin.ar_document a ON a.ar_doc_id = i.ar_doc_id WHERE r.receipt_id = @r AND i.invoice_id = @i
            """,
            ("r", cheque),
            ("i", w.Invoice));
        Assert.Equal(outcomes[0] is null ? "RECORDED:APPLIED|PAID:0.00" : "BOUNCED:UNAPPLIED|CONFIRMED:59000.00", state);
    }

    [Fact]
    public async Task A_receipt_dated_in_the_month_racing_its_AR_REC_close_never_posts_into_the_closed_month()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var w = await ReceiptTests.WorldAsync(h);
        await h.RunAsync(
            new RecordExternalFiscalDocument(h.CompanyId, w.Billing, "fisc", w.Invoice, 2, "E310000000001", h.Clock.UtcNow.AddMinutes(-2), "A1", "e-cf.xml", DeliveryTests.Hash,
                "131925332", 50000.00m, 9000.00m, 59000.00m),
            new RecordExternalFiscalDocumentHandler());
        var month = ReceiptTests.Today(h);
        var period = await h.ScalarAsync<Guid>("SELECT period_id FROM fin.period WHERE company_id = @c AND @d BETWEEN starts_on AND ends_on", ("c", h.CompanyId), ("d", month));

        clock.Advance(TimeSpan.FromDays(40));
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var cobros = await h.SessionWithRolesAsync("COBROS");
        await new LedgerSealer(h.Sealer, h.Clock).SealAllAsync(CancellationToken.None);
        var codes = await RaceAsync([
            () => h.RunAsync(new CloseComponent(h.CompanyId, controller, "close", period, "AR-REC"), new CloseComponentHandler()),
            () => h.RunAsync(new RecordReceipt(h.CompanyId, cobros, "late", w.S.Customer, "TRANSFER", 100.00m, month, w.Bank), new RecordReceiptHandler()),
        ]);

        // Either the close wins and the receipt posts late into an open month, or the receipt wins, its entries in the month are not
        // sealed yet and the close is refused (Patch 1.1 gate). Never a closed AR-REC month with the receipt inside it.
        Assert.Null(codes[1]);
        var postedInMonth = await h.ScalarAsync<bool>(
            $"SELECT j.period_id = '{period}' FROM fin.gl_journal j JOIN fin.posting_rule r USING (posting_rule_id) WHERE r.code = 'P-23'");
        var closed = await h.ScalarAsync<string>($"SELECT status FROM fin.close_component_state WHERE period_id = '{period}' AND component = 'AR-REC'") == "CLOSED";
        if (closed)
        {
            Assert.Null(codes[0]);
            Assert.False(postedInMonth);
            Assert.True(await h.ScalarAsync<bool>("SELECT late_entry FROM fin.gl_journal j JOIN fin.posting_rule r USING (posting_rule_id) WHERE r.code = 'P-23'"));
        }
        else
        {
            Assert.Equal(ReconciliationErrors.IntegrityNotSealed, codes[0]);
            Assert.True(postedInMonth);
        }
    }
}
