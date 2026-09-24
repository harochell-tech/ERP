using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
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
/// CC-04 (E-PR19-2): 50 workers run mixed commands for 60 s over 3 purchase orders and 2 items — receipts, quantity corrections
/// (created and approved), reversals, supplier invoices (registered, matched, posted) and fixture issues. Afterwards: no technical
/// failure, no deadlock left after the pipeline's retries, only expected business rejections, every journal balanced and every
/// value entry with exactly one GL line (AT-04, VAL-01), and INV-QTY-BALANCE, INV-VALUE-GL, VALUE-GL-LINK and AP-GL MATCHED.
/// ROCHELL_CC04_SECONDS shortens or lengthens the run (default 60).
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ConcurrencySoakTests(PostgresFixture postgres, ITestOutputHelper output)
{
    private const int Workers = 50;

    /// <summary>What concurrent work may legitimately refuse: the guards that keep the ledgers consistent.</summary>
    private static readonly HashSet<string> ExpectedRejections = new(StringComparer.Ordinal)
    {
        ProcurementErrors.ReceiptToleranceExceeded,
        ProcurementErrors.ReceiptNotReversible,
        ProcurementErrors.UseReceiptCorrection,
        ProcurementErrors.AlreadyInvoiced,
        ProcurementErrors.InvalidState,
        ProcurementErrors.VersionConflict,
        ProcurementErrors.CorrectionNotPending,
        ProcurementErrors.QuantityInvalid,
        ProcurementErrors.QtyExceedsAvailable,
        InventoryErrors.InsufficientStock,
    };

    private sealed record Receipt(Guid GoodsReceiptId, Guid LineId, Guid LotId, Guid PoLineId);

    [Trait("Acceptance", "AT-04")]
    [Trait("Acceptance", "CC-04")]
    [Trait("Acceptance", "VAL-01")]
    [Fact]
    public async Task CC04_fifty_workers_of_mixed_commands_leave_the_ledgers_reconciled()
    {
        var seconds = int.Parse(Environment.GetEnvironmentVariable("ROCHELL_CC04_SECONDS") ?? "60", CultureInfo.InvariantCulture);
        await using var h = await TestHarness.CreateAsync(postgres);
        var r = await h.CreateReceivingSetupAsync();
        await h.EnableInvoicePostingAsync();
        await h.EnableTestIssueAsync();

        // Corrections (R-03A/B) on top of invoice posting, which already mapped PPV (EnableCorrectionsAsync would map it twice).
        await h.CreateActiveMapAsync("MATERIAL_USAGE_VARIANCE", await h.CreateAccountAsync("5110", "Variación de uso de material", isControl: false));
        await h.AdminRequireAsync(
            $"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id IN ('{CorrectionSetup.R03A}', '{CorrectionSetup.R03B}') AND version = 1");
        var p = r.Purchasing;
        var clerk = await h.SessionWithRolesAsync("CUENTAS_POR_PAGAR");
        var today = BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);

        // Three approved orders, each with a sand line and a cement line (the Controller has no approval limit, E-PR08-2).
        var lines = new List<(Guid Po, Guid PoLine)>();
        for (var i = 0; i < 3; i++)
        {
            var po = (await h.RunAsync(new CreatePurchaseOrder(h.CompanyId, p.Buyer, $"cc04-po-{i}", p.PlantId, p.SupplierId, today, [new(p.Sand, "t", 5000m, 1000m), new(p.Cement, "t", 5000m, 1200m)]), new CreatePurchaseOrderHandler())).ResultRef;
            await h.RunAsync(new SubmitPurchaseOrder(h.CompanyId, p.Buyer, $"cc04-sub-{i}", p.PlantId, po, 1), new SubmitPurchaseOrderHandler());
            await h.RunAsync(new ApprovePurchaseOrder(h.CompanyId, p.Controller, $"cc04-apr-{i}", p.PlantId, po, 2), new ApprovePurchaseOrderHandler());
            await using var command = h.Admin.CreateCommand("SELECT po_line_id FROM pur.purchase_order_line WHERE po_id = @p ORDER BY line_no");
            command.Parameters.AddWithValue("p", po);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                lines.Add((po, reader.GetGuid(0)));
            }
        }

        var receipts = new ConcurrentBag<Receipt>();
        var outcomes = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var failures = new ConcurrentQueue<string>();
        var counter = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

        async Task Attempt(string action, Func<Task> run)
        {
            try
            {
                await run();
                outcomes.AddOrUpdate(action + ":ok", 1, (_, n) => n + 1);
            }
            catch (DomainException ex) when (ExpectedRejections.Contains(ex.Code))
            {
                outcomes.AddOrUpdate(action + ":" + ex.Code, 1, (_, n) => n + 1);
            }
            catch (Exception ex) when (ex is DomainException or DbException or InvalidOperationException)
            {
                failures.Enqueue($"{action}: {ex.GetType().Name} {(ex as DomainException)?.Code ?? (ex as DbException)?.SqlState} {ex.Message}");
            }
        }

        string Key(string prefix) => $"{prefix}-{Interlocked.Increment(ref counter)}";

        async Task Worker(int seed)
        {
            var random = new Random(seed);
            while (!stop.IsCancellationRequested)
            {
                var (po, poLine) = lines[random.Next(lines.Count)];
                var known = receipts.ToArray();
                var pick = known.Length == 0 ? null : known[random.Next(known.Length)];
                switch (pick is null ? 0 : random.Next(10))
                {
                    case < 4 or 9:
                        await Attempt("receive", async () =>
                        {
                            var result = await h.RunAsync(
                                new PostGoodsReceipt(h.CompanyId, r.Storekeeper, Key("gr"), p.PlantId, po, r.LocationA, h.Clock.UtcNow.AddSeconds(-1), [new(poLine, 2m)]),
                                new PostGoodsReceiptHandler());
                            var line = JsonDocument.Parse(result.ResultPayload).RootElement.GetProperty("lines")[0];
                            receipts.Add(new Receipt(result.ResultRef, line.GetProperty("grLineId").GetGuid(), line.GetProperty("lotId").GetGuid(), poLine));
                        });
                        break;
                    case 4 or 5:
                        await Attempt("correct", async () =>
                        {
                            var delta = random.Next(2) == 0 ? 0.5m : -0.5m;
                            var correction = await h.RunAsync(
                                new CreateReceiptCorrection(h.CompanyId, r.Storekeeper, Key("rc"), p.PlantId, pick!.GoodsReceiptId, pick.LineId, delta, "Ajuste de báscula", "ticket-corregido"),
                                new CreateReceiptCorrectionHandler());
                            await h.RunAsync(new ApproveReceiptCorrection(h.CompanyId, p.Controller, Key("rca"), correction.ResultRef), new ApproveReceiptCorrectionHandler());
                        });
                        break;
                    case 6:
                        await Attempt("reverse", () => h.RunAsync(new ReverseGoodsReceipt(h.CompanyId, p.Controller, Key("grr"), pick!.GoodsReceiptId, "Recepción duplicada"), new ReverseGoodsReceiptHandler()));
                        break;
                    case 7:
                        await Attempt("invoice", async () =>
                        {
                            var number = "B01" + Interlocked.Increment(ref counter).ToString("D8", CultureInfo.InvariantCulture);
                            var price = pick!.PoLineId == lines[0].PoLine || pick.PoLineId == lines[2].PoLine || pick.PoLineId == lines[4].PoLine ? 1000m : 1200m;
                            var si = (await h.RunAsync(
                                new RegisterSupplierInvoice(h.CompanyId, clerk, Key("si"), p.SupplierId, number, today, today.AddDays(30), [new(pick.PoLineId, 1m, price)]),
                                new RegisterSupplierInvoiceHandler())).ResultRef;
                            var match = await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, clerk, Key("sim"), si, 1), new MatchSupplierInvoiceHandler());
                            if (JsonDocument.Parse(match.ResultPayload).RootElement.GetProperty("status").GetString() == SupplierInvoiceStatus.Matched)
                            {
                                await h.RunAsync(new PostSupplierInvoice(h.CompanyId, clerk, Key("sip"), si, 2), new PostSupplierInvoiceHandler());
                            }
                        });
                        break;
                    default:
                        await Attempt("issue", () => h.RunAsync(
                            new TestIssueStock(h.CompanyId, h.SessionId, Key("iss"), r.LocationA, ItemOf(pick!.PoLineId), pick.LotId, 0.5m, today),
                            new TestIssueStockHandler()));
                        break;
                }
            }
        }

        Guid ItemOf(Guid poLine) => lines.FindIndex(l => l.PoLine == poLine) % 2 == 0 ? p.Sand : p.Cement;

        await Task.WhenAll(Enumerable.Range(1, Workers).Select(seed => Task.Run(() => Worker(seed))));

        foreach (var (outcome, count) in outcomes.OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            output.WriteLine($"{outcome} {count}");
        }

        Assert.True(failures.IsEmpty, "Unexpected failures:\n" + string.Join('\n', failures.GroupBy(f => f).Select(g => $"{g.Count()} × {g.Key}").Take(20)));
        foreach (var action in new[] { "receive", "correct", "reverse", "invoice", "issue" })
        {
            Assert.True(outcomes.GetValueOrDefault(action + ":ok") > 0, $"No successful {action} in {seconds} s; the mix did not exercise it.");
        }

        Assert.DoesNotContain(await h.OutcomesAsync(), o => o is "FAILED_TECHNICAL" or "CONFLICT_RETRYABLE");

        // AT-04: every journal balances; every value entry has exactly one GL line of the same amount (VAL-01).
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM (SELECT journal_id FROM fin.gl_entry GROUP BY journal_id HAVING sum(debit) <> sum(credit)) unbalanced"));
        Assert.Equal(0L, await h.ScalarAsync<long>(
            """
            SELECT count(*) FROM inv.inv_value_entry v
            WHERE (SELECT count(*) FROM fin.gl_entry e WHERE e.inv_value_entry_id = v.value_entry_id AND e.debit - e.credit = v.amount) <> 1
            """));
        var run = await h.RunAsync(new RunReconciliation(h.CompanyId, p.Controller, "cc04-recon", ["INV-QTY-BALANCE", "INV-VALUE-GL", "VALUE-GL-LINK", "AP-GL"]), new RunReconciliationHandler());
        var statuses = JsonDocument.Parse(run.ResultPayload).RootElement.GetProperty("runs").EnumerateArray().Select(x => x.GetProperty("code").GetString() + ":" + x.GetProperty("status").GetString()).ToList();
        Assert.Equal(["INV-QTY-BALANCE:MATCHED", "INV-VALUE-GL:MATCHED", "VALUE-GL-LINK:MATCHED", "AP-GL:MATCHED"], statuses);
    }
}
