using System.Globalization;
using System.Text;
using System.Text.Json;
using Rochell.Inventory;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Procurement.GoodsReceipts;
using Rochell.Procurement.PurchaseOrders;
using Rochell.Procurement.ReceiptCorrections;
using Rochell.Procurement.SupplierInvoices;
using Rochell.Reconciliation;
using Rochell.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Rochell.Procurement.Tests;

/// <summary>
/// E-VS1-3 property tests: random but reproducible (seeded) sequences of receipts, corrections, reversals, supplier invoices at
/// random prices (with price exceptions), invoice reversals and fixture issues. After EVERY command the invariants are checked
/// against full re-sums of the ledgers — independent of the control totals the deferred position check uses (0021) — and at the
/// end all reconciliations must be free of ERROR findings. A failure reports the seed and the steps so far.
/// ROCHELL_PROPERTY_SEEDS (default 1-6) and ROCHELL_PROPERTY_STEPS (default 80) tune the run.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class LedgerPropertyTests(PostgresFixture postgres, ITestOutputHelper output)
{
    /// <summary>Business rejections a random sequence legitimately runs into; anything else is a failure.</summary>
    private static readonly HashSet<string> ExpectedRejections = new(StringComparer.Ordinal)
    {
        ProcurementErrors.ReceiptToleranceExceeded, ProcurementErrors.ReceiptNotReversible, ProcurementErrors.UseReceiptCorrection,
        ProcurementErrors.AlreadyInvoiced, ProcurementErrors.InvalidState, ProcurementErrors.CorrectionNotPending,
        ProcurementErrors.QuantityInvalid, ProcurementErrors.QtyExceedsAvailable, ProcurementErrors.QtyExceptionNotApprovable,
        ProcurementErrors.ValueTooSmall, InventoryErrors.InsufficientStock, "AP_NOT_OPEN",
    };

    /// <summary>
    /// One SQL per invariant; each returns the number of violations. Everything is re-summed from the ledgers, never read from
    /// balances or control totals alone.
    /// </summary>
    private static readonly (string Name, string Sql)[] Invariants =
    [
        ("L1 journal balanced", "SELECT count(*) FROM (SELECT journal_id FROM fin.gl_entry GROUP BY journal_id HAVING sum(debit) <> sum(credit)) x"),
        ("L2 value entry ↔ one GL line", "SELECT count(*) FROM inv.inv_value_entry v WHERE (SELECT count(*) FROM fin.gl_entry e WHERE e.inv_value_entry_id = v.value_entry_id AND e.debit - e.credit = v.amount) <> 1"),
        ("L2 RAW line ↔ value entry", "SELECT count(*) FROM fin.gl_entry e WHERE e.account_role = 'RAW_MATERIAL' AND e.inv_value_entry_id IS NULL"),
        ("L3 valuation qty = Σ quantity entries", """
            SELECT count(*) FROM inv.inv_valuation_balance b WHERE b.quantity <> (
              SELECT coalesce(sum(q.quantity), 0) FROM inv.inv_quantity_entry q JOIN md.plant p ON p.plant_id = q.plant_id
              WHERE p.valuation_area_id = b.valuation_area_id AND q.item_id = b.item_id)
            """),
        ("L3 valuation value = Σ value entries = Σ RAW GL", """
            SELECT count(*) FROM inv.inv_valuation_balance b
            WHERE b.value <> (SELECT coalesce(sum(v.amount), 0) FROM inv.inv_value_entry v WHERE v.valuation_area_id = b.valuation_area_id AND v.item_id = b.item_id)
               OR b.value <> (SELECT coalesce(sum(g.debit - g.credit), 0) FROM fin.gl_entry g JOIN md.plant p ON p.plant_id = g.plant_id
                              WHERE g.account_role = 'RAW_MATERIAL' AND p.valuation_area_id = b.valuation_area_id AND g.item_id = b.item_id)
            """),
        ("L3 control totals = full sums", "SELECT count(*) FROM inv.inv_valuation_balance WHERE ledger_quantity <> quantity OR ledger_value <> value OR gl_value <> value"),
        ("L4 stock per lot = Σ its entries, never negative", """
            SELECT count(*) FROM inv.inv_stock_balance s
            WHERE s.quantity < 0 OR s.quantity <> (SELECT coalesce(sum(q.quantity), 0) FROM inv.inv_quantity_entry q
                                                    WHERE q.location_id = s.location_id AND q.item_id = s.item_id AND q.lot_id = s.lot_id)
            """),
        ("Area stock = valuation quantity", """
            SELECT count(*) FROM inv.inv_valuation_balance b WHERE b.quantity <> (
              SELECT coalesce(sum(s.quantity), 0) FROM inv.inv_stock_balance s JOIN md.plant p ON p.plant_id = s.plant_id
              WHERE p.valuation_area_id = b.valuation_area_id AND s.item_id = b.item_id)
            """),
        ("PO line invoiced ≤ received ≤ tolerance", "SELECT count(*) FROM pur.purchase_order_line WHERE qty_invoiced > qty_received OR qty_received < 0"),
        ("AP subledger = AP GL", """
            SELECT count(*) FROM (SELECT (SELECT coalesce(sum(open_amount), 0) FROM fin.ap_document) AS subledger,
                                         (SELECT coalesce(sum(credit - debit), 0) FROM fin.gl_entry WHERE account_role = 'AP_CONTROL') AS gl) x
            WHERE subledger <> gl
            """),
    ];

    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        foreach (var seed in (Environment.GetEnvironmentVariable("ROCHELL_PROPERTY_SEEDS") ?? "1,2,3,4,5,6").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            data.Add(int.Parse(seed, CultureInfo.InvariantCulture));
        }

        return data;
    }

    private sealed record Receipt(Guid GoodsReceiptId, Guid LineId, Guid LotId, Guid PoLineId);

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Random_command_sequences_keep_every_ledger_invariant(int seed)
    {
        var steps = int.Parse(Environment.GetEnvironmentVariable("ROCHELL_PROPERTY_STEPS") ?? "80", CultureInfo.InvariantCulture);
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        await h.EnableInvoicePostingAsync();
        await h.EnableTestIssueAsync();
        await h.CreateActiveMapAsync("MATERIAL_USAGE_VARIANCE", await h.CreateAccountAsync("5110", "Variación de uso de material", isControl: false));
        await h.AdminRequireAsync(
            $"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id IN ('{CorrectionSetup.R03A}', '{CorrectionSetup.R03B}', '{ReversalSetup.R02B}') AND version = 1");
        var p = r.Purchasing;
        var clerk = await h.SessionWithRolesAsync("CUENTAS_POR_PAGAR");
        var today = BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

        var random = new Random(seed);
        var lines = new List<(Guid Po, Guid PoLine, Guid Item, decimal Price)>();
        for (var i = 0; i < 2; i++)
        {
            var sandPrice = 900m + random.Next(0, 300);
            var cementPrice = 1100m + random.Next(0, 300);
            var po = (await h.RunAsync(new CreatePurchaseOrder(h.CompanyId, p.Buyer, $"po-{i}", p.PlantId, p.SupplierId, today, [new(p.Sand, "t", 200m, sandPrice), new(p.Cement, "t", 200m, cementPrice)]), new CreatePurchaseOrderHandler())).ResultRef;
            await h.RunAsync(new SubmitPurchaseOrder(h.CompanyId, p.Buyer, $"po-{i}-s", p.PlantId, po, 1), new SubmitPurchaseOrderHandler());
            await h.RunAsync(new ApprovePurchaseOrder(h.CompanyId, p.Controller, $"po-{i}-a", p.PlantId, po, 2), new ApprovePurchaseOrderHandler());
            var ids = await h.ScalarAsync<Guid[]>("SELECT array_agg(po_line_id ORDER BY line_no) FROM pur.purchase_order_line WHERE po_id = @p", ("p", po));
            lines.Add((po, ids![0], p.Sand, sandPrice));
            lines.Add((po, ids[1], p.Cement, cementPrice));
        }

        var receipts = new List<Receipt>();
        var invoices = new List<(Guid Si, long Version)>();
        var log = new StringBuilder();
        var counter = 0;
        string Key() => $"k-{++counter}";
        decimal Quantity(int maxThousandths) => random.Next(1, maxThousandths) / 1000m; // 0.001 … max, 3 decimals

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
            }
        }

        for (var step = 0; step < steps; step++)
        {
            var (po, poLine, item, price) = lines[random.Next(lines.Count)];
            var pick = receipts.Count == 0 ? null : receipts[random.Next(receipts.Count)];
            switch (pick is null ? 0 : random.Next(10))
            {
                case < 3:
                    var qty = Quantity(8000);
                    await Step($"receive {qty} on {poLine}", async () =>
                    {
                        var result = await h.RunAsync(new PostGoodsReceipt(h.CompanyId, r.Storekeeper, Key(), p.PlantId, po, random.Next(2) == 0 ? r.LocationA : r.LocationB, h.Clock.UtcNow.AddSeconds(-1), [new(poLine, qty)]), new PostGoodsReceiptHandler());
                        var line = JsonDocument.Parse(result.ResultPayload).RootElement.GetProperty("lines")[0];
                        receipts.Add(new Receipt(result.ResultRef, line.GetProperty("grLineId").GetGuid(), line.GetProperty("lotId").GetGuid(), poLine));
                    });
                    break;
                case 3:
                    var delta = (random.Next(2) == 0 ? 1 : -1) * Quantity(2000);
                    await Step($"correct {pick!.GoodsReceiptId} by {delta}", async () =>
                    {
                        var rc = await h.RunAsync(new CreateReceiptCorrection(h.CompanyId, r.Storekeeper, Key(), p.PlantId, pick.GoodsReceiptId, pick.LineId, delta, "Ajuste", "ticket"), new CreateReceiptCorrectionHandler());
                        await h.RunAsync(new ApproveReceiptCorrection(h.CompanyId, p.Controller, Key(), rc.ResultRef), new ApproveReceiptCorrectionHandler());
                    });
                    break;
                case 4:
                    await Step($"reverse receipt {pick!.GoodsReceiptId}", () => h.RunAsync(new ReverseGoodsReceipt(h.CompanyId, p.Controller, Key(), pick.GoodsReceiptId, "Error"), new ReverseGoodsReceiptHandler()));
                    break;
                case 5 or 6:
                    var billed = lines.Single(l => l.PoLine == pick!.PoLineId);
                    var invoicePrice = billed.Price + random.Next(-40, 41); // up to ±4 %: sometimes a price exception
                    var invoiceQty = Quantity(3000);
                    await Step($"invoice {invoiceQty} at {invoicePrice} on {billed.PoLine}", async () =>
                    {
                        var number = "B01" + (counter + 1).ToString("D8", CultureInfo.InvariantCulture);
                        var si = (await h.RunAsync(new RegisterSupplierInvoice(h.CompanyId, clerk, Key(), p.SupplierId, number, today, today.AddDays(30), [new(billed.PoLine, invoiceQty, invoicePrice)]), new RegisterSupplierInvoiceHandler())).ResultRef;
                        var status = JsonDocument.Parse((await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, clerk, Key(), si, 1), new MatchSupplierInvoiceHandler())).ResultPayload).RootElement.GetProperty("status").GetString();
                        var version = 2L;
                        if (status == SupplierInvoiceStatus.MatchException)
                        {
                            await h.RunAsync(new ApproveMatchException(h.CompanyId, p.Controller, Key(), si, version, "Precio pactado"), new ApproveMatchExceptionHandler());
                            version++;
                        }

                        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, clerk, Key(), si, version), new PostSupplierInvoiceHandler());
                        invoices.Add((si, version + 1));
                    });
                    break;
                case 7 when invoices.Count > 0:
                    var reversed = invoices[random.Next(invoices.Count)].Si;
                    invoices.RemoveAll(x => x.Si == reversed);
                    var reversedVersion = await h.ScalarAsync<long>("SELECT version FROM pur.supplier_invoice WHERE si_id = @s", ("s", reversed));
                    await Step($"reverse invoice {reversed}", () => h.RunAsync(new ReverseSupplierInvoice(h.CompanyId, p.Controller, Key(), reversed, reversedVersion, "Anulada"), new ReverseSupplierInvoiceHandler()));
                    break;
                default:
                    var issueQty = Quantity(4000);
                    var issueItem = lines.Single(l => l.PoLine == pick!.PoLineId).Item;
                    await Step($"issue {issueQty} from lot {pick!.LotId}", () => h.RunAsync(new TestIssueStock(h.CompanyId, h.SessionId, Key(), r.LocationA, issueItem, pick.LotId, issueQty, today), new TestIssueStockHandler()));
                    break;
            }

            foreach (var (name, sql) in Invariants)
            {
                var violations = await h.ScalarAsync<long>(sql);
                Assert.True(violations == 0, $"Seed {seed}, after step {step}: invariant '{name}' has {violations} violation(s).\n{log}");
            }
        }

        var run = await h.RunAsync(new RunReconciliation(h.CompanyId, p.Controller, "final"), new RunReconciliationHandler());
        var errors = JsonDocument.Parse(run.ResultPayload).RootElement.GetProperty("runs").EnumerateArray()
            .Where(x => x.GetProperty("errors").GetInt32() > 0).Select(x => x.GetProperty("code").GetString()).ToList();
        output.WriteLine($"seed {seed}: {counter} commands, {receipts.Count} receipts, {invoices.Count} invoices still posted");
        Assert.True(errors.Count == 0, $"Seed {seed}: reconciliations with ERROR findings: {string.Join(", ", errors)}\n{log}");
    }
}
