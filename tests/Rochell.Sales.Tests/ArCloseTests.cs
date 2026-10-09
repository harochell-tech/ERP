using System.Text.Json;
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

/// <summary>VS3-08: AR-GL, CONTRACT-ASSET, RECEIPT-APPL, FISC-DOC and the AR-REC close (AR-04; E-VS3-08-1…12).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ArCloseTests(PostgresFixture postgres)
{
    private static readonly string[] ArRecons = ["ACC-EVIDENCE", "AR-GL", "CONTRACT-ASSET", "FISC-DOC", "RECEIPT-APPL"];

    private static async Task<JsonElement> ReconcileAsync(TestHarness h, Guid controller, string key, params string[] codes)
        => JsonDocument.Parse((await h.RunAsync(new RunReconciliation(h.CompanyId, controller, key, codes), new RunReconciliationHandler())).ResultPayload).RootElement;

    private static string Statuses(JsonElement result)
        => string.Join(',', result.GetProperty("runs").EnumerateArray().Select(r => $"{r.GetProperty("code").GetString()}:{r.GetProperty("status").GetString()}").Order(StringComparer.Ordinal));

    private static Task<string?> FindingsAsync(TestHarness h)
        => h.ScalarAsync<string>("SELECT string_agg(r.recon_code || ':' || x.classification, ',' ORDER BY r.recon_code, x.classification) FROM rec.recon_exception x JOIN rec.recon_run r USING (run_id)");

    /// <summary>
    /// The month: FA-000001 (59,000.00) fiscalized, a credit note of 5,900.00, an ISR withholding of 1,000.00, a transfer of 52,100.00
    /// applied (PAID), an advance of 10,000.00 left unapplied, and a second delivery of 200 blocks (10,000.00) not invoiced.
    /// </summary>
    private static async Task<ReceiptTests.World> MonthAsync(TestHarness h, bool fiscalize = true)
    {
        var w = await ReceiptTests.WorldAsync(h);
        var (order, orderLine) = await DeliveryTests.ConfirmedOrderAsync(h, w.S, DeliveryTerms.PickupAtPlant, 200m, "o2");
        await DeliveryTests.DispatchAsync(h, w.S, order, orderLine, 200m, own: false, "d2");
        if (!fiscalize)
        {
            return w;
        }

        await h.RunAsync(
            new RecordExternalFiscalDocument(h.CompanyId, w.Billing, "fisc", w.Invoice, 2, "E310000000001", h.Clock.UtcNow.AddMinutes(-2), "A1B2C3", "e-cf.xml", DeliveryTests.Hash,
                "131925332", 50000.00m, 9000.00m, 59000.00m),
            new RecordExternalFiscalDocumentHandler());
        var invoiceLine = await h.ScalarAsync<Guid>("SELECT invoice_line_id FROM sal.invoice_line WHERE invoice_id = @i", ("i", w.Invoice));
        var note = (await h.RunAsync(new CreateCreditNote(h.CompanyId, w.Billing, "nc", w.Invoice, "DESCUENTO", "Descuento por volumen", [new(invoiceLine, 5000.00m)]), new CreateCreditNoteHandler())).ResultRef;
        await h.RunAsync(new IssueCreditNote(h.CompanyId, await h.SessionWithRolesAsync("FACTURACION"), "nc-i", note, 1), new IssueCreditNoteHandler());
        await h.RunAsync(
            new RecordExternalCreditNoteDocument(h.CompanyId, w.Billing, "nc-f", note, 2, "E340000000001", h.Clock.UtcNow.AddMinutes(-1), "Z9", "nc.xml", DeliveryTests.Hash,
                "131925332", 5000.00m, 900.00m, 5900.00m),
            new RecordExternalCreditNoteDocumentHandler());
        await h.RunAsync(
            new RecordCustomerWithholding(h.CompanyId, w.Cobros, "isr", w.Invoice, "ISR", 1000.00m, ReceiptTests.Today(h), "ISR-1", "isr-1.pdf", DeliveryTests.Hash),
            new RecordCustomerWithholdingHandler());
        var receipt = (await ReceiptTests.Transfer(h, w, "r", 52100.00m)).ResultRef;
        await h.RunAsync(new ApplyReceipt(h.CompanyId, w.Cobros, "a", receipt, 1, [new(w.Invoice, 52100.00m)]), new ApplyReceiptHandler());
        await ReceiptTests.Transfer(h, w, "advance", 10000.00m);
        return w;
    }

    [Trait("AcceptanceVs3", "AR-04")]
    [Fact]
    public async Task AR04_a_month_with_sales_and_receipts_reconciles_and_AR_REC_closes()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var w = await MonthAsync(h);
        var month = ReceiptTests.Today(h);
        var run = await ReconcileAsync(h, w.Controller, "recon", ArRecons);

        clock.Advance(TimeSpan.FromDays(40));
        await new LedgerSealer(h.Sealer, h.Clock).SealAllAsync(CancellationToken.None);
        var (controller, cobros) = (await h.SessionWithRolesAsync("CONTROLLER"), await h.SessionWithRolesAsync("COBROS")); // the month's sessions expired
        var period = await h.ScalarAsync<Guid>("SELECT period_id FROM fin.period WHERE company_id = @c AND @d BETWEEN starts_on AND ends_on", ("c", h.CompanyId), ("d", month));
        // X1-01b (E-X1-01-6): the 200 blocks delivered and not invoiced hold the close until the Controller accepts them.
        var held = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new CloseComponent(h.CompanyId, controller, "close-0", period, "AR-REC"), new CloseComponentHandler()));
        await h.RunAsync(new AcceptUnbilledDeliveries(h.CompanyId, controller, "accept", period, "Entrega del día 30 facturada en el mes siguiente"), new AcceptUnbilledDeliveriesHandler());
        var closed = await h.RunAsync(new CloseComponent(h.CompanyId, controller, "close", period, "AR-REC"), new CloseComponentHandler());
        var late = await h.RunAsync(
            new RecordReceipt(h.CompanyId, cobros, "late", w.S.Customer, "TRANSFER", 100.00m, month, w.Bank), new RecordReceiptHandler());

        Assert.Equal("ACC-EVIDENCE:MATCHED,AR-GL:MATCHED,CONTRACT-ASSET:MATCHED,FISC-DOC:MATCHED,RECEIPT-APPL:MATCHED", Statuses(run));
        Assert.Contains("CONTRACT-ASSET", held.Message, StringComparison.Ordinal);
        Assert.Equal("PAID:0.00", await h.ScalarAsync<string>(
            "SELECT i.commercial_status || ':' || a.open_amount::numeric(19,2)::text FROM sal.invoice i JOIN fin.ar_document a ON a.ar_doc_id = i.ar_doc_id WHERE i.invoice_id = @i", ("i", w.Invoice)));
        Assert.Contains("snapshotHash", closed.ResultPayload, StringComparison.Ordinal);
        Assert.Equal("CLOSED", await h.ScalarAsync<string>($"SELECT status FROM fin.close_component_state WHERE period_id = '{period}' AND component = 'AR-REC'"));

        // The snapshot: AR by customer, the 10,000.00 delivered not invoiced and the 10,000.00 advance.
        Assert.Equal("contract_asset:10000.00|unapplied_receipts:10000.00", await h.ScalarAsync<string>(
            """
            SELECT string_agg(b ->> 'kind' || ':' || (b ->> 'amount')::numeric(19,2)::text, '|' ORDER BY b ->> 'kind')
            FROM fin.close_snapshot s CROSS JOIN jsonb_array_elements(s.content -> 'balances') b
            WHERE s.component = 'AR-REC' AND b ->> 'kind' IN ('contract_asset', 'unapplied_receipts')
            """));
        Assert.Equal("RECEIPT-APPL", await h.ScalarAsync<string>(
            "SELECT string_agg(recon_code, ',' ORDER BY recon_code) FROM rec.recon_blocking WHERE component = 'BANK-REC' AND recon_code = 'RECEIPT-APPL'"));

        // A receipt dated in the closed month goes to the next open date (AR-REC also required by P-23).
        Assert.Equal("true", await h.ScalarAsync<string>(
            "SELECT j.late_entry::text FROM fin.gl_journal j JOIN fin.receipt r ON r.posting_event_id = j.source_event_id WHERE r.receipt_id = @r", ("r", late.ResultRef)));
    }

    /// <summary>X1-01b (E-X1-3, E-X1-01-6, E-X1-01b-1): at the month's end a delivery not invoiced is an error until the Controller accepts it; a later one holds again.</summary>
    [Fact]
    public async Task The_months_deliveries_not_invoiced_hold_its_end_until_the_Controller_accepts_them()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await ReceiptTests.WorldAsync(h);
        var (order, orderLine) = await DeliveryTests.ConfirmedOrderAsync(h, w.S, DeliveryTerms.PickupAtPlant, 200m, "o2");
        await DeliveryTests.DispatchAsync(h, w.S, order, orderLine, 100m, own: false, "d2");
        var today = ReceiptTests.Today(h);
        var monthEnd = new DateOnly(today.Year, today.Month, 1).AddMonths(1).AddDays(-1);
        var period = await h.ScalarAsync<Guid>("SELECT period_id FROM fin.period WHERE company_id = @c AND @d BETWEEN starts_on AND ends_on", ("c", h.CompanyId), ("d", today));
        async Task<string?> Unbilled(string key, DateOnly cutoff)
        {
            var run = await h.RunAsync(new RunReconciliation(h.CompanyId, w.Controller, key, ["CONTRACT-ASSET"], cutoff), new RunReconciliationHandler());
            return await h.ScalarAsync<string>(
                """
                SELECT count(x.exception_id)::text || ':' || coalesce(sum(x.value_a), 0)::numeric(19,2)::text
                FROM rec.recon_run r LEFT JOIN rec.recon_exception x ON x.run_id = r.run_id AND x.classification = 'UNBILLED_AT_CLOSE'
                WHERE r.command_id = @cmd
                """,
                ("cmd", run.CommandId));
        }

        var atEnd = await Unbilled("end-1", monthEnd);
        var notMonthEnd = await Unbilled("mid", monthEnd.AddDays(-1));
        var noReason = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new AcceptUnbilledDeliveries(h.CompanyId, w.Controller, "a0", period, " "), new AcceptUnbilledDeliveriesHandler()));
        var accepted = JsonDocument.Parse((await h.RunAsync(new AcceptUnbilledDeliveries(h.CompanyId, w.Controller, "a1", period, "Se factura el día 1"), new AcceptUnbilledDeliveriesHandler())).ResultPayload).RootElement;
        var afterAcceptance = await Unbilled("end-2", monthEnd);
        await DeliveryTests.DispatchAsync(h, w.S, order, orderLine, 100m, own: false, "d3");
        var afterNewDelivery = await Unbilled("end-3", monthEnd);

        Assert.Equal("1:5000.00", atEnd);
        Assert.Equal("0:0.00", notMonthEnd); // not a month's end: nothing to hold
        Assert.Equal(SalesErrors.FieldRequired, noReason.Code);
        Assert.Equal("1|5000.00", $"{accepted.GetProperty("lines").GetInt32()}|{accepted.GetProperty("unbilled").GetString()}");
        Assert.Equal("0:0.00", afterAcceptance);
        Assert.Equal("1:5000.00", afterNewDelivery);
    }

    [Fact]
    public async Task A_pending_eCF_and_a_tampered_AR_document_are_found_and_block_the_AR_REC_close()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var w = await MonthAsync(h, fiscalize: false);
        var month = ReceiptTests.Today(h);
        await h.AdminRequireAsync(
            $"""
            ALTER TABLE fin.ar_document DISABLE TRIGGER ar_document_guard;
            UPDATE fin.ar_document SET open_amount = open_amount - 1 WHERE ar_doc_id = '{w.ArDoc}';
            ALTER TABLE fin.ar_document ENABLE TRIGGER ar_document_guard;
            """);

        var run = await ReconcileAsync(h, w.Controller, "recon", ArRecons);
        clock.Advance(TimeSpan.FromDays(40));
        await new LedgerSealer(h.Sealer, h.Clock).SealAllAsync(CancellationToken.None);
        var period = await h.ScalarAsync<Guid>("SELECT period_id FROM fin.period WHERE company_id = @c AND @d BETWEEN starts_on AND ends_on", ("c", h.CompanyId), ("d", month));
        var controller = await h.SessionWithRolesAsync("CONTROLLER"); // the month's sessions expired
        var blocked = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new CloseComponent(h.CompanyId, controller, "close", period, "AR-REC"), new CloseComponentHandler()));

        Assert.Equal("ACC-EVIDENCE:MATCHED,AR-GL:EXCEPTIONS,CONTRACT-ASSET:MATCHED,FISC-DOC:EXCEPTIONS,RECEIPT-APPL:EXCEPTIONS", Statuses(run));
        Assert.Equal("AR-GL:AR_GL_DIFFERENCE,FISC-DOC:FISCAL_DOCUMENT_PENDING,RECEIPT-APPL:AR_DOCUMENT_SETTLEMENT_DIFFERENCE", await h.ScalarAsync<string>(
            "SELECT string_agg(DISTINCT r.recon_code || ':' || x.classification, ',') FROM rec.recon_exception x JOIN rec.recon_run r USING (run_id) WHERE r.as_of = (SELECT min(as_of) FROM rec.recon_run)"));
        Assert.Equal(ReconciliationErrors.ReconciliationErrorsFound, blocked.Code);
        Assert.Contains("AR-GL", blocked.Message, StringComparison.Ordinal);
    }
}
