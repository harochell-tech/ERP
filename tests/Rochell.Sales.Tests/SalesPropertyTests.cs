using System.Globalization;
using System.Text;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Reconciliation;
using Rochell.Sales.CreditNotes;
using Rochell.Sales.Customers;
using Rochell.Sales.Deliveries;
using Rochell.Sales.Invoices;
using Rochell.Sales.Orders;
using Rochell.Sales.Receipts;
using Rochell.Tax;
using Rochell.TestInfrastructure;
using Rochell.Treasury.BankAccounts;
using Xunit;
using Xunit.Abstractions;

namespace Rochell.Sales.Tests;

/// <summary>
/// INV-S (E-VS3-11-1/2/5): random but reproducible (seeded) sequences over two customers and one finished good — orders (credit
/// auto-approved or pending, approved or rejected), deliveries of both terms (gate, full POD, POD with a shortfall, return trip,
/// cancel), invoices (issue, e-CF, void), credit notes, receipts (transfer, cheque, cash), deposits, applications and unapplies,
/// withholdings, bounced cheques and reversals. After EVERY step the invariants are re-summed by SQL; every 25 steps and at the end
/// AR-GL, CONTRACT-ASSET, RECEIPT-APPL, ACC-EVIDENCE, INV-VALUE-GL, INV-QTY-BALANCE and VALUE-GL-LINK must have no ERROR. A failure
/// reports the seed and the steps so far. ROCHELL_INVS_SEEDS (default 1,2,3) and ROCHELL_INVS_STEPS (default 150) tune the run
/// (workflow `inv-s` for long runs).
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SalesPropertyTests(PostgresFixture postgres, ITestOutputHelper output)
{
    private const string SalesItbis = """{"tax_code":"ITBIS","rate":"0.18","effect":"OUTPUT","exempt_item_categories":[]}""";

    /// <summary>E-VS3-11-2: reconciliations checked every this many steps (and at the end).</summary>
    private const int CheckpointEvery = 25;

    private static readonly string[] Reconciliations = ["AR-GL", "CONTRACT-ASSET", "RECEIPT-APPL", "ACC-EVIDENCE", "INV-VALUE-GL", "INV-QTY-BALANCE", "VALUE-GL-LINK"];

    /// <summary>Rejections a random sequence legitimately runs into (it acts on stale or ambitious choices); anything else fails the run.</summary>
    private static readonly HashSet<string> ExpectedRejections = new(StringComparer.Ordinal)
    {
        SalesErrors.InvalidState, DeliveryErrors.QuantityExceedsOpen, DeliveryErrors.InsufficientStock, DeliveryErrors.OpenDeliveries,
        InvoiceErrors.NotBillable, InvoiceErrors.NotVoidable, CreditNoteErrors.InvoiceNotCreditable, CreditNoteErrors.ExceedsCreditable,
        CreditNoteErrors.ExceedsOpenReceivable, ReceiptErrors.ExceedsUnapplied, ReceiptErrors.ExceedsOpen, ReceiptErrors.InvoiceNotOpen,
        ReceiptErrors.NotBounceable, ReceiptErrors.NotReversible, ReceiptErrors.NotDepositable, ReceiptErrors.WithholdingExceeds,
        ReceiptErrors.ApplicationNotFound,
    };

    private static readonly (string Name, string Sql)[] Invariants =
    [
        ("journals balanced", "SELECT count(*) FROM (SELECT journal_id FROM fin.gl_entry GROUP BY journal_id HAVING sum(debit) <> sum(credit)) x"),
        ("AR subledger = AR_CONTROL per customer", """
            SELECT count(*) FROM (
              SELECT coalesce(a.party_id, g.party_id) AS party, coalesce(a.open, 0) AS open, coalesce(g.balance, 0) AS balance
              FROM (SELECT party_id, sum(open_amount) AS open FROM fin.ar_document GROUP BY party_id) a
              FULL JOIN (SELECT party_id, sum(debit - credit) AS balance FROM fin.gl_entry WHERE account_role = 'AR_CONTROL' GROUP BY party_id) g ON g.party_id = a.party_id) x
            WHERE open <> balance
            """),
        ("original − open = applications + withholdings + credit notes (or the void)", """
            SELECT count(*) FROM fin.ar_document d JOIN sal.invoice i ON i.ar_doc_id = d.ar_doc_id
            WHERE d.original_amount - d.open_amount <>
                  (SELECT coalesce(sum(CASE WHEN x.reverses_application_id IS NULL THEN x.amount ELSE -x.amount END), 0) FROM fin.ar_application x WHERE x.ar_doc_id = d.ar_doc_id)
                + (SELECT coalesce(sum(w.amount), 0) FROM fin.customer_withholding w WHERE w.ar_doc_id = d.ar_doc_id AND w.status = 'ACTIVE')
                + (SELECT coalesce(sum(n.total), 0) FROM sal.credit_note n WHERE n.invoice_id = i.invoice_id AND n.commercial_status = 'CONFIRMED')
                + CASE WHEN i.commercial_status = 'VOIDED' THEN d.original_amount ELSE 0 END
            """),
        ("invoice status follows its AR document (E-VS3-07-11)", """
            SELECT count(*) FROM sal.invoice i JOIN fin.ar_document a ON a.ar_doc_id = i.ar_doc_id
            WHERE i.commercial_status NOT IN ('DRAFT', 'VOIDED') AND i.commercial_status <> CASE
              WHEN a.open_amount = 0 THEN CASE WHEN (SELECT coalesce(sum(n.total), 0) FROM sal.credit_note n WHERE n.invoice_id = i.invoice_id AND n.commercial_status = 'CONFIRMED') = i.total
                                               THEN 'CREDITED' ELSE 'PAID' END
              WHEN EXISTS (SELECT 1 FROM fin.ar_application x WHERE x.ar_doc_id = a.ar_doc_id AND x.reverses_application_id IS NULL
                             AND NOT EXISTS (SELECT 1 FROM fin.ar_application u WHERE u.reverses_application_id = x.application_id))
                OR EXISTS (SELECT 1 FROM fin.customer_withholding w WHERE w.ar_doc_id = a.ar_doc_id AND w.status = 'ACTIVE') THEN 'PARTIALLY_PAID'
              ELSE 'CONFIRMED' END
            """),
        ("receipt: live applications + unapplied = amount (none when bounced or reversed)", """
            SELECT count(*) FROM fin.receipt r
            WHERE (SELECT coalesce(sum(CASE WHEN x.reverses_application_id IS NULL THEN x.amount ELSE -x.amount END), 0) FROM fin.ar_application x WHERE x.receipt_id = r.receipt_id)
                  + CASE WHEN r.status = 'RECORDED' THEN r.unapplied_amount ELSE 0 END
                  <> CASE WHEN r.status = 'RECORDED' THEN r.amount ELSE 0 END
               OR r.unapplied_amount < 0
            """),
        ("contract asset per delivery line = (delivered − invoiced) × price", """
            SELECT count(*) FROM log.delivery_line dl
            JOIN sal.sales_order_line ol ON ol.line_id = dl.sales_order_line_id
            WHERE dl.qty_delivered > 0
              AND (SELECT coalesce(sum(e.debit - e.credit), 0) FROM fin.gl_entry e WHERE e.account_role IN ('CONTRACT_ASSET', 'UNBILLED_RECEIVABLE') AND e.subledger_ref = dl.delivery_line_id)
                  <> CASE WHEN dl.qty_invoiced >= dl.qty_delivered THEN 0 ELSE round((dl.qty_delivered - dl.qty_invoiced) * ol.unit_price, 2) END
            """),
        ("invoiced ≤ delivered ≤ issued; delivered ≤ ordered", """
            SELECT (SELECT count(*) FROM log.delivery_line WHERE qty_invoiced > qty_delivered OR qty_delivered > qty_issued OR qty_issued > qty_planned)
                 + (SELECT count(*) FROM sal.sales_order_line WHERE qty_delivered > qty_ordered OR qty_invoiced > qty_delivered)
            """),
        ("stock ≥ 0; valuation = value entries = FG GL (P-3)", """
            SELECT (SELECT count(*) FROM inv.inv_stock_balance WHERE quantity < 0)
                 + (SELECT count(*) FROM inv.inv_valuation_balance v
                    WHERE v.value <> (SELECT coalesce(sum(e.amount), 0) FROM inv.inv_value_entry e WHERE e.valuation_area_id = v.valuation_area_id AND e.item_id = v.item_id))
                 + (SELECT count(*) WHERE (SELECT coalesce(sum(value), 0) FROM inv.inv_valuation_balance)
                                         <> (SELECT coalesce(sum(debit - credit), 0) FROM fin.gl_entry WHERE account_role IN ('RAW_MATERIAL', 'FINISHED_GOODS', 'FINISHED_GOODS_IN_TRANSIT')))
            """),
        ("no automatic credit approval above the limit, on hold or overdue", """
            SELECT count(*) FROM sal.credit_check
            WHERE decision = 'AUTO_APPROVED'
              AND (exposure_ar + exposure_orders + exposure_uninvoiced + order_amount > credit_limit OR credit_hold OR overdue_days > overdue_days_block)
            """),
    ];

    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        foreach (var seed in (Environment.GetEnvironmentVariable("ROCHELL_INVS_SEEDS") ?? "1,2,3").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            data.Add(int.Parse(seed, CultureInfo.InvariantCulture));
        }

        return data;
    }

    [Trait("AcceptanceVs3", "INV-S")]
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Random_sales_sequences_keep_every_invariant(int seed)
    {
        var steps = int.Parse(Environment.GetEnvironmentVariable("ROCHELL_INVS_STEPS") ?? "150", CultureInfo.InvariantCulture);
        await using var h = await TestHarness.CreateAsync(postgres);
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
            await h.CreateActiveMapAsync(role, await h.CreateAccountAsync(code, role, control));
        }

        await h.CreateAccountAsync("1101", "Banco de prueba", isControl: true);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var bank = (await h.RunAsync(new RegisterBankAccount(h.CompanyId, controller, "bank", "TEST_BANK", "0123456789", "1101"), new RegisterBankAccountHandler())).ResultRef;
        var billing = await h.SessionWithRolesAsync("FACTURACION");
        var billing2 = await h.SessionWithRolesAsync("FACTURACION");
        var cobros = await h.SessionWithRolesAsync("COBROS");
        var treasurer = await h.SessionWithRolesAsync("TESORERO");

        // A second customer with a small limit, so some orders wait for Crédito.
        var small = (await h.RunAsync(new CreateCustomer(h.CompanyId, s.Seller, "c2", "101000001", "Ferretería Dos"), new CreateCustomerHandler())).ResultRef;
        var terms = JsonDocument.Parse((await h.RunAsync(new PrepareCustomerTerms(h.CompanyId, s.Credit, "t2", small, 30, 20000.00m, false), new PrepareCustomerTermsHandler())).ResultPayload)
            .RootElement.GetProperty("termsVersionId").GetGuid();
        await h.RunAsync(new ApproveCustomerTerms(h.CompanyId, controller, "t2-a", terms), new ApproveCustomerTermsHandler());
        await h.RunAsync(new ActivateCustomer(h.CompanyId, s.Credit, "c2-a", small, 1), new ActivateCustomerHandler());
        Guid[] customers = [s.Customer, small];

        var random = new Random(seed);
        var log = new StringBuilder();
        var rejections = new Dictionary<string, int>(StringComparer.Ordinal);
        var counter = 0;
        string Key() => $"k-{++counter}";
        decimal Cents(int maxCents) => random.Next(1, maxCents + 1) / 100m;
        T Pick<T>(List<T> items) => items[random.Next(items.Count)];

        async Task Step(string description, Func<Task> run)
        {
            log.AppendLine(CultureInfo.InvariantCulture, $"{counter}: {description}");
            try
            {
                await run();
            }
            catch (DomainException ex) when (ExpectedRejections.Contains(ex.Code))
            {
                log.AppendLine(CultureInfo.InvariantCulture, $"   rejected {ex.Code}");
                rejections[ex.Code] = rejections.GetValueOrDefault(ex.Code) + 1;
            }
        }

        async Task<List<(Guid Id, long Version, string A, string B)>> Rows(string sql)
        {
            var list = new List<(Guid, long, string, string)>();
            await using var command = h.Admin.CreateCommand(sql);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add((reader.GetGuid(0), reader.GetInt64(1), reader.IsDBNull(2) ? string.Empty : reader.GetValue(2).ToString()!, reader.IsDBNull(3) ? string.Empty : reader.GetValue(3).ToString()!));
            }

            return list;
        }

        async Task CheckpointAsync(string when)
        {
            var run = await h.RunAsync(new RunReconciliation(h.CompanyId, controller, Key(), Reconciliations), new RunReconciliationHandler());
            var errors = JsonDocument.Parse(run.ResultPayload).RootElement.GetProperty("runs").EnumerateArray()
                .Where(x => x.GetProperty("errors").GetInt32() > 0).Select(x => x.GetProperty("code").GetString()).ToList();
            var detail = errors.Count == 0 ? string.Empty : await h.ScalarAsync<string>(
                "SELECT string_agg(x.classification || ' ' || x.match_key || ' ' || coalesce(x.value_a::text, '') || ' / ' || coalesce(x.value_b::text, ''), '; ') FROM rec.recon_exception x JOIN rec.recon_run r USING (run_id) WHERE x.severity = 'ERROR' AND r.command_id = (SELECT command_id FROM core.command_log WHERE result_ref = @r)",
                ("r", run.ResultRef));
            Assert.True(errors.Count == 0, $"Seed {seed}, {when}: reconciliations with ERROR findings: {string.Join(", ", errors)} ({detail})\n{log}");
        }

        for (var step = 0; step < steps; step++)
        {
            switch (random.Next(20))
            {
                case 0 or 1 or 2:
                    {
                        // The Vendedor orders 5…120 blocks for one of the customers and submits it for credit.
                        var customer = customers[random.Next(customers.Length)];
                        var term = random.Next(2) == 0 ? DeliveryTerms.PickupAtPlant : DeliveryTerms.DeliveredOwnTransport;
                        var quantity = random.Next(5, 121);
                        await Step($"order {quantity} {term} for {(customer == small ? "small" : "big")}", async () =>
                        {
                            var order = (await h.RunAsync(
                                new CreateSalesOrder(h.CompanyId, s.Seller, Key(), customer, s.Plant, term, term == DeliveryTerms.DeliveredOwnTransport ? "Obra" : null, null, null, [new(s.Block, "un", quantity)]),
                                new CreateSalesOrderHandler())).ResultRef;
                            await h.RunAsync(new SubmitForCredit(h.CompanyId, s.Seller, Key(), order, 1), new SubmitForCreditHandler());
                        });
                        break;
                    }

                case 3:
                    {
                        var pending = await Rows("SELECT sales_order_id, version, order_no, '' FROM sal.sales_order WHERE status = 'PENDING_CREDIT' ORDER BY order_no");
                        if (pending.Count > 0)
                        {
                            var (id, version, no, _) = Pick(pending);
                            await Step($"credit decides {no}", () => random.Next(3) == 0
                                ? h.RunAsync(new RejectCredit(h.CompanyId, s.Credit, Key(), id, version, "Sin crédito"), new RejectCreditHandler())
                                : h.RunAsync(new ApproveCredit(h.CompanyId, s.Credit, Key(), id, version), new ApproveCreditHandler()));
                        }

                        break;
                    }

                case 4 or 5:
                    {
                        // Despacho plans part or all of what is still open of a confirmed order.
                        var open = await Rows(
                            """
                            SELECT o.sales_order_id, 0, l.line_id,
                                   (l.qty_ordered - l.qty_delivered - coalesce((SELECT sum(dl.qty_planned) FROM log.delivery_line dl JOIN log.delivery d ON d.delivery_id = dl.delivery_id
                                      WHERE dl.sales_order_line_id = l.line_id AND d.status IN ('PLANNED', 'LOADING', 'LOADED', 'IN_TRANSIT')), 0))::int
                            FROM sal.sales_order o JOIN sal.sales_order_line l ON l.sales_order_id = o.sales_order_id AND l.lines_version = o.lines_version
                            WHERE o.status IN ('CONFIRMED', 'PARTIALLY_DELIVERED')
                            ORDER BY o.order_no
                            """);
                        var candidates = open.Where(o => int.Parse(o.B, CultureInfo.InvariantCulture) > 0).ToList();
                        if (candidates.Count > 0)
                        {
                            var (order, _, line, left) = Pick(candidates);
                            var quantity = random.Next(1, int.Parse(left, CultureInfo.InvariantCulture) + 1);
                            await Step($"plan {quantity} of {order}", () => h.RunAsync(new PlanDelivery(h.CompanyId, s.Dispatch, Key(), order, [new(Guid.Parse(line), quantity)]), new PlanDeliveryHandler()));
                        }

                        break;
                    }

                case 6 or 7 or 8:
                    {
                        // A delivery takes its next step; at the site the POD is full, short (a transit loss), partly returned, or a return trip.
                        var live = await Rows("SELECT delivery_id, version, status, delivery_term_code FROM log.delivery WHERE status IN ('PLANNED', 'LOADING', 'LOADED', 'IN_TRANSIT') ORDER BY delivery_no");
                        if (live.Count > 0)
                        {
                            var (id, version, status, term) = Pick(live);
                            var lines = await Rows($"SELECT delivery_line_id, 0, qty_issued::int, '' FROM log.delivery_line WHERE delivery_id = '{id}' ORDER BY line_no");
                            switch (status)
                            {
                                case "PLANNED" when random.Next(8) == 0:
                                    await Step($"cancel {id}", () => h.RunAsync(new CancelDelivery(h.CompanyId, s.Dispatch, Key(), id, version, "Cliente pospuso"), new CancelDeliveryHandler()));
                                    break;
                                case "PLANNED":
                                    await Step($"load {id}", () => h.RunAsync(
                                        term == DeliveryTerms.DeliveredOwnTransport
                                            ? new StartLoading(h.CompanyId, s.Dispatch, Key(), id, version, s.Truck, s.Driver, null, null)
                                            : new StartLoading(h.CompanyId, s.Dispatch, Key(), id, version, null, null, "A 123-456", "Pedro Cliente"),
                                        new StartLoadingHandler()));
                                    break;
                                case "LOADING":
                                    await Step($"loaded {id}", () => h.RunAsync(new ConfirmLoaded(h.CompanyId, s.Dispatch, Key(), id, version, [.. lines.Select(l => new LoadedLine(l.Id, s.Patio))]), new ConfirmLoadedHandler()));
                                    break;
                                case "LOADED":
                                    await Step($"gate {id}", () => h.RunAsync(new RecordGateOut(h.CompanyId, s.Dispatch, Key(), id, version, 9000m, 8000m, $"TK-{counter}", DeliveryTests.Hash), new RecordGateOutHandler()));
                                    break;
                                default:
                                    {
                                        var mode = random.Next(10);
                                        if (mode == 0)
                                        {
                                            await Step($"return trip {id}", () => h.RunAsync(new RecordReturnTrip(h.CompanyId, s.Dispatch, Key(), id, version, "Obra cerrada"), new RecordReturnTripHandler()));
                                            break;
                                        }

                                        var pod = lines.Select(l =>
                                        {
                                            var issued = int.Parse(l.A, CultureInfo.InvariantCulture);
                                            var missing = mode >= 7 ? random.Next(0, Math.Max(1, issued / 5) + 1) : 0;
                                            var back = mode == 9 ? missing : 0;
                                            return new PodLine(l.Id, issued - missing, back);
                                        }).ToList();
                                        var exception = pod.Any(p => p.QtyReceived < int.Parse(lines.First(l => l.Id == p.DeliveryLineId).A, CultureInfo.InvariantCulture)) ? "Faltante en la entrega" : null;
                                        await Step($"POD {id} ({mode})", () => h.RunAsync(
                                            new RecordPod(h.CompanyId, s.Dispatch, Key(), id, version, "Ing. Gómez", h.Clock.UtcNow.AddMinutes(-1), "pod.jpg", DeliveryTests.Hash, pod, exception),
                                            new RecordPodHandler()));
                                        break;
                                    }
                            }
                        }

                        break;
                    }

                case 9 or 10:
                    {
                        // Facturación invoices everything billable of one customer and issues it.
                        var billable = await Rows(
                            """
                            SELECT o.party_id, 0, string_agg(dl.delivery_line_id::text, ','), ''
                            FROM log.delivery_line dl JOIN log.delivery d ON d.delivery_id = dl.delivery_id JOIN sal.sales_order o ON o.sales_order_id = d.sales_order_id
                            WHERE d.status IN ('DELIVERED', 'DELIVERED_WITH_EXCEPTIONS') AND dl.qty_delivered > dl.qty_invoiced
                            GROUP BY o.party_id
                            """);
                        if (billable.Count > 0)
                        {
                            var (party, _, lineList, _) = Pick(billable);
                            await Step($"invoice {party}", async () =>
                            {
                                var invoice = (await h.RunAsync(
                                    new CreateInvoiceFromDeliveries(h.CompanyId, billing, Key(), party, [.. lineList.Split(',').Select(Guid.Parse)]), new CreateInvoiceFromDeliveriesHandler())).ResultRef;
                                await h.RunAsync(new IssueInvoice(h.CompanyId, billing, Key(), invoice, 1), new IssueInvoiceHandler());
                            });
                        }

                        break;
                    }

                case 11:
                    {
                        // The e-CF of a pending invoice is recorded (mostly), or the Controller voids it while never fiscalized.
                        var pending = await Rows(
                            """
                            SELECT i.invoice_id, i.version, i.net_total::numeric(19,2)::text || '|' || i.tax_total::numeric(19,2)::text || '|' || i.total::numeric(19,2)::text, p.rnc
                            FROM sal.invoice i JOIN md.party p ON p.party_id = i.party_id
                            WHERE i.fiscal_status = 'PENDING_EXTERNAL' AND i.commercial_status <> 'VOIDED' ORDER BY i.invoice_no
                            """);
                        if (pending.Count > 0)
                        {
                            var (id, version, totals, rnc) = Pick(pending);
                            if (random.Next(5) == 0)
                            {
                                await Step($"void {id}", () => h.RunAsync(new VoidUnfiscalizedInvoice(h.CompanyId, controller, Key(), id, version, "Precio equivocado"), new VoidUnfiscalizedInvoiceHandler()));
                            }
                            else
                            {
                                var t = totals.Split('|').Select(x => decimal.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                                var encf = "E31" + (counter + 1).ToString("D10", CultureInfo.InvariantCulture);
                                await Step($"e-CF {encf}", () => h.RunAsync(
                                    new RecordExternalFiscalDocument(h.CompanyId, billing, Key(), id, version, encf, h.Clock.UtcNow.AddMinutes(-1), "SEC", "e-cf.xml", DeliveryTests.Hash, rnc, t[0], t[1], t[2]),
                                    new RecordExternalFiscalDocumentHandler()));
                            }
                        }

                        break;
                    }

                case 12:
                    {
                        // A credit note on a fiscalized invoice line, issued by the other Facturación user; sometimes its e-CF 34.
                        var lines = await Rows(
                            """
                            SELECT il.invoice_id, 0, il.invoice_line_id,
                                   (il.net_amount - coalesce((SELECT sum(cl.net_amount) FROM sal.credit_note_line cl JOIN sal.credit_note n ON n.credit_note_id = cl.credit_note_id
                                      WHERE cl.invoice_line_id = il.invoice_line_id AND n.commercial_status = 'CONFIRMED'), 0))::numeric(19,2)
                            FROM sal.invoice_line il JOIN sal.invoice i ON i.invoice_id = il.invoice_id
                            WHERE i.fiscal_status = 'ACCEPTED_EXTERNAL' AND i.commercial_status IN ('CONFIRMED', 'PARTIALLY_PAID', 'PAID')
                            """);
                        var candidates = lines.Where(l => decimal.Parse(l.B, CultureInfo.InvariantCulture) > 0m).ToList();
                        if (candidates.Count > 0)
                        {
                            var (invoice, _, line, left) = Pick(candidates);
                            var net = Math.Min(decimal.Parse(left, CultureInfo.InvariantCulture), Cents(300_000));
                            await Step($"credit note {net} on {invoice}", async () =>
                            {
                                var note = (await h.RunAsync(new CreateCreditNote(h.CompanyId, billing, Key(), invoice, "DESCUENTO", "Descuento", [new(Guid.Parse(line), net)]), new CreateCreditNoteHandler())).ResultRef;
                                await h.RunAsync(new IssueCreditNote(h.CompanyId, billing2, Key(), note, 1), new IssueCreditNoteHandler());
                            });
                        }

                        break;
                    }

                case 13 or 14:
                    {
                        // Cobros records a receipt: transfer, cheque or cash.
                        var customer = customers[random.Next(customers.Length)];
                        var amount = Cents(2_000_000);
                        var method = random.Next(3);
                        await Step($"receipt {amount} method {method}", () => h.RunAsync(
                            method switch
                            {
                                0 => new RecordReceipt(h.CompanyId, cobros, Key(), customer, "TRANSFER", amount, ReceiptTests.Today(h), bank),
                                1 => new RecordReceipt(h.CompanyId, cobros, Key(), customer, "CHEQUE", amount, ChequeBank: "Banco", ChequeNo: $"CH-{counter}", ChequeDate: ReceiptTests.Today(h)),
                                _ => new RecordReceipt(h.CompanyId, cobros, Key(), customer, "CASH", amount),
                            },
                            new RecordReceiptHandler()));
                        break;
                    }

                case 15:
                    {
                        var inTransit = await Rows("SELECT receipt_id, version, receipt_no, '' FROM fin.receipt WHERE status = 'RECORDED' AND bank_status = 'IN_TRANSIT' ORDER BY receipt_no");
                        if (inTransit.Count > 0)
                        {
                            var chosen = inTransit.Where(_ => random.Next(2) == 0).Select(r => r.Id).DefaultIfEmpty(inTransit[0].Id).Distinct().ToList();
                            await Step($"deposit {chosen.Count} receipts", () => h.RunAsync(new DepositReceipts(h.CompanyId, cobros, Key(), bank, chosen), new DepositReceiptsHandler()));
                        }

                        break;
                    }

                case 16 or 17:
                    {
                        // Cobros applies a receipt to an open invoice of its customer (all it can, or part of it).
                        var pairs = await Rows(
                            """
                            SELECT r.receipt_id, r.version, i.invoice_id, least(r.unapplied_amount, a.open_amount)::numeric(19,2)
                            FROM fin.receipt r
                            JOIN sal.invoice i ON i.party_id = r.party_id AND i.commercial_status IN ('CONFIRMED', 'PARTIALLY_PAID')
                            JOIN fin.ar_document a ON a.ar_doc_id = i.ar_doc_id AND a.open_amount > 0
                            WHERE r.status = 'RECORDED' AND r.unapplied_amount > 0
                            ORDER BY r.receipt_no, i.invoice_no
                            """);
                        if (pairs.Count > 0)
                        {
                            var (receipt, version, invoice, most) = Pick(pairs);
                            var cap = decimal.Parse(most, CultureInfo.InvariantCulture);
                            var amount = random.Next(2) == 0 ? cap : Math.Min(cap, Cents(1_000_000));
                            await Step($"apply {amount} of {receipt}", () => h.RunAsync(new ApplyReceipt(h.CompanyId, cobros, Key(), receipt, version, [new(Guid.Parse(invoice), amount)]), new ApplyReceiptHandler()));
                        }

                        break;
                    }

                case 18:
                    {
                        // An application is undone, or a withholding is recorded on an open invoice (or the Controller reverses one).
                        var live = await Rows(
                            """
                            SELECT x.receipt_id, 0, x.event_id, '' FROM fin.ar_application x
                            WHERE x.reverses_application_id IS NULL AND NOT EXISTS (SELECT 1 FROM fin.ar_application u WHERE u.reverses_application_id = x.application_id)
                            """);
                        var open = await Rows(
                            """
                            SELECT i.invoice_id, 0, a.open_amount::numeric(19,2), '' FROM sal.invoice i JOIN fin.ar_document a ON a.ar_doc_id = i.ar_doc_id
                            WHERE i.commercial_status IN ('CONFIRMED', 'PARTIALLY_PAID') AND a.open_amount > 0
                            """);
                        var active = await Rows("SELECT withholding_id, version, '', '' FROM fin.customer_withholding WHERE status = 'ACTIVE'");
                        var mode = random.Next(3);
                        if (mode == 0 && live.Count > 0)
                        {
                            var (receipt, _, eventId, _) = Pick(live);
                            await Step($"unapply {eventId}", () => h.RunAsync(new UnapplyReceipt(h.CompanyId, cobros, Key(), receipt, Guid.Parse(eventId), "Factura equivocada"), new UnapplyReceiptHandler()));
                        }
                        else if (mode == 1 && open.Count > 0)
                        {
                            var (invoice, _, openAmount, _) = Pick(open);
                            var amount = Math.Min(decimal.Parse(openAmount, CultureInfo.InvariantCulture), Cents(200_000));
                            await Step($"withholding {amount} on {invoice}", () => h.RunAsync(
                                new RecordCustomerWithholding(h.CompanyId, cobros, Key(), invoice, "ISR", amount, ReceiptTests.Today(h), $"C-{counter}", "cert.pdf", DeliveryTests.Hash),
                                new RecordCustomerWithholdingHandler()));
                        }
                        else if (active.Count > 0)
                        {
                            var (id, version, _, _) = Pick(active);
                            await Step($"reverse withholding {id}", () => h.RunAsync(new ReverseCustomerWithholding(h.CompanyId, controller, Key(), id, version, "Certificado equivocado"), new ReverseCustomerWithholdingHandler()));
                        }

                        break;
                    }

                default:
                    {
                        // The bank returns a deposited cheque (Tesorería), or the Controller reverses a receipt recorded by mistake.
                        var cheques = await Rows("SELECT receipt_id, version, receipt_no, '' FROM fin.receipt WHERE status = 'RECORDED' AND method = 'CHEQUE' AND deposit_id IS NOT NULL");
                        var loose = await Rows(
                            """
                            SELECT receipt_id, version, receipt_no, '' FROM fin.receipt
                            WHERE status = 'RECORDED' AND application_status = 'UNAPPLIED' AND ((method = 'TRANSFER' AND bank_status = 'DEPOSITED') OR bank_status = 'IN_TRANSIT')
                            """);
                        if (cheques.Count > 0 && random.Next(2) == 0)
                        {
                            var (id, version, no, _) = Pick(cheques);
                            await Step($"bounce {no}", () => h.RunAsync(new MarkReceiptBounced(h.CompanyId, treasurer, Key(), id, version, "Fondos insuficientes"), new MarkReceiptBouncedHandler()));
                        }
                        else if (loose.Count > 0)
                        {
                            var (id, version, no, _) = Pick(loose);
                            await Step($"reverse {no}", () => h.RunAsync(new ReverseReceipt(h.CompanyId, controller, Key(), id, version, "Registrado dos veces"), new ReverseReceiptHandler()));
                        }

                        break;
                    }
            }

            foreach (var (name, sql) in Invariants)
            {
                var violations = await h.ScalarAsync<long>(sql);
                Assert.True(violations == 0, $"Seed {seed}, after step {step}: invariant '{name}' has {violations} violation(s).\n{log}");
            }

            if ((step + 1) % CheckpointEvery == 0)
            {
                await CheckpointAsync($"checkpoint after step {step}");
            }
        }

        await CheckpointAsync("end");
        output.WriteLine(
            $"seed {seed}: {counter} commands; {await h.CountAsync("sal.sales_order")} orders, {await h.CountAsync("log.delivery")} deliveries, {await h.CountAsync("sal.invoice")} invoices, " +
            $"{await h.CountAsync("sal.credit_note")} credit notes, {await h.CountAsync("fin.receipt")} receipts, {await h.CountAsync("fin.ar_application")} application rows; " +
            $"rejected: {string.Join(", ", rejections.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => $"{r.Key}×{r.Value}"))}");
    }
}
