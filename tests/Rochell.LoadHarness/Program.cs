using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Rochell.Audit;
using Rochell.Platform.Time;
using Rochell.Procurement.GoodsReceipts;
using Rochell.Procurement.PurchaseOrders;
using Rochell.Reconciliation;
using Rochell.TestInfrastructure;

[assembly: AssemblyMetadata("Acceptance", "PF-01")]

namespace Rochell.LoadHarness;

/// <summary>
/// PF-01 (E-PR19-3): 10,000 synthetic goods receipts through the real command pipeline (application role, row-level security,
/// posting engine, ledgers, deferred checks) with 8 workers and the sealer running; then all 8 reconciliations. Pass:
/// p95 of PostGoodsReceipt below 500 ms and the reconciliations below 30 s together. Writes pf01.json and pf01.md to --report
/// and exits 1 when a threshold fails.
///
///   dotnet run --project tests/Rochell.LoadHarness -c Release -- [--receipts 10000] [--workers 8] [--report load-report]
/// </summary>
internal static class LoadProgram
{
    private static readonly TimeSpan ReceiptP95Limit = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReconciliationLimit = TimeSpan.FromSeconds(30);
    private const int ReceiptsPerLine = 100;

    public static async Task<int> Main(string[] args)
    {
        var receipts = int.Parse(Arg(args, "--receipts") ?? "10000", CultureInfo.InvariantCulture);
        var workers = int.Parse(Arg(args, "--workers") ?? "8", CultureInfo.InvariantCulture);
        var reportDir = Path.GetFullPath(Arg(args, "--report") ?? "load-report");

        var postgres = new PostgresFixture();
        await postgres.InitializeAsync();
        try
        {
            await using var h = await TestHarness.CreateAsync(postgres);
            var r = await h.CreateReceivingSetupAsync();
            await h.EnableInvoicePostingAsync();
            var lines = await OrdersAsync(h, r, (receipts + ReceiptsPerLine - 1) / ReceiptsPerLine);
            Console.WriteLine($"PF-01: {receipts} receipts, {workers} workers, {lines.Count} PO lines; sealer running.");

            using var stopSealer = new CancellationTokenSource();
            var sealer = new LedgerSealer(h.Sealer, h.Clock);
            var sealing = Task.Run(async () =>
            {
                try
                {
                    await sealer.RunAsync(TimeSpan.FromSeconds(5), stopSealer.Token);
                }
                catch (OperationCanceledException)
                {
                    // Stopped after the load.
                }
            });

            var latencies = new ConcurrentBag<long>();
            var failures = new ConcurrentQueue<string>();
            var next = -1;
            var wall = Stopwatch.StartNew();
            await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
            {
                int i;
                while ((i = Interlocked.Increment(ref next)) < receipts)
                {
                    var (po, poLine) = lines[i % lines.Count];
                    var watch = Stopwatch.StartNew();
                    try
                    {
                        await h.RunAsync(
                            new PostGoodsReceipt(h.CompanyId, r.Storekeeper, $"pf01-{i}", r.Purchasing.PlantId, po, i % 2 == 0 ? r.LocationA : r.LocationB, h.Clock.UtcNow.AddSeconds(-1), [new(poLine, 1m)]),
                            new PostGoodsReceiptHandler());
                        latencies.Add(watch.ElapsedTicks);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        failures.Enqueue($"{i}: {ex.GetType().Name} {ex.Message}");
                    }
                }
            })));
            wall.Stop();

            await stopSealer.CancelAsync();
            await sealing;
            await sealer.SealAllAsync(CancellationToken.None);

            var reconciliation = Stopwatch.StartNew();
            var run = await h.RunAsync(new RunReconciliation(h.CompanyId, r.Purchasing.Controller, "pf01-recon"), new RunReconciliationHandler());
            reconciliation.Stop();
            var reconStatuses = JsonDocument.Parse(run.ResultPayload).RootElement.GetProperty("runs").EnumerateArray()
                .Select(x => $"{x.GetProperty("code").GetString()}:{x.GetProperty("status").GetString()}")
                .ToList();

            var sorted = latencies.Order().Select(t => TimeSpan.FromTicks(t * TimeSpan.TicksPerSecond / Stopwatch.Frequency)).ToList();
            var p50 = Percentile(sorted, 50);
            var p95 = Percentile(sorted, 95);
            var p99 = Percentile(sorted, 99);
            var max = sorted.Count == 0 ? TimeSpan.Zero : sorted[^1];
            var passed = failures.IsEmpty && sorted.Count == receipts && p95 < ReceiptP95Limit && reconciliation.Elapsed < ReconciliationLimit;

            var report = new
            {
                test = "PF-01",
                passed,
                receipts,
                workers,
                succeeded = sorted.Count,
                failed = failures.Count,
                firstFailures = failures.Take(10).ToList(),
                wallSeconds = Seconds(wall.Elapsed),
                throughputPerSecond = (sorted.Count * 1000L / Math.Max(1L, (long)wall.Elapsed.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture),
                postGoodsReceiptMs = new { p50 = Ms(p50), p95 = Ms(p95), p99 = Ms(p99), max = Ms(max), limitP95 = Ms(ReceiptP95Limit) },
                reconciliationSeconds = Seconds(reconciliation.Elapsed),
                reconciliationLimitSeconds = Seconds(ReconciliationLimit),
                reconciliations = reconStatuses,
                machine = new { Environment.ProcessorCount, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription },
                sealedGroups = await h.ScalarAsync<long>("SELECT count(*) FROM audit.integrity_state WHERE integrity_status = 'SEALED'"),
                pendingGroups = await h.ScalarAsync<long>("SELECT count(*) FROM audit.integrity_state WHERE integrity_status <> 'SEALED'"),
            };

            Directory.CreateDirectory(reportDir);
            await File.WriteAllTextAsync(Path.Combine(reportDir, "pf01.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            var md = new StringBuilder()
                .AppendLine(CultureInfo.InvariantCulture, $"## PF-01 — {(passed ? "PASA" : "NO PASA")}")
                .AppendLine()
                .AppendLine("| Medida | Resultado | Límite |")
                .AppendLine("| --- | --- | --- |")
                .AppendLine(CultureInfo.InvariantCulture, $"| Recepciones exitosas | {sorted.Count} / {receipts} ({failures.Count} fallas) | {receipts} |")
                .AppendLine(CultureInfo.InvariantCulture, $"| PostGoodsReceipt p50 / p95 / p99 / máx | {Ms(p50)} / **{Ms(p95)}** / {Ms(p99)} / {Ms(max)} ms | p95 < {Ms(ReceiptP95Limit)} ms |")
                .AppendLine(CultureInfo.InvariantCulture, $"| Duración de la carga ({workers} hilos) | {Seconds(wall.Elapsed)} s | — |")
                .AppendLine(CultureInfo.InvariantCulture, $"| 8 conciliaciones | **{Seconds(reconciliation.Elapsed)} s** | < {Seconds(ReconciliationLimit)} s |")
                .AppendLine(CultureInfo.InvariantCulture, $"| Resultados | {string.Join(", ", reconStatuses)} | — |")
                .AppendLine(CultureInfo.InvariantCulture, $"| Máquina | {Environment.ProcessorCount} CPU, {System.Runtime.InteropServices.RuntimeInformation.OSDescription} | — |");
            await File.WriteAllTextAsync(Path.Combine(reportDir, "pf01.md"), md.ToString());
            Console.WriteLine(md.ToString());
            return passed ? 0 : 1;
        }
        finally
        {
            await postgres.DisposeAsync();
        }
    }

    /// <summary>Approved orders with two lines each (sand and cement), enough for <paramref name="lineCount"/> lines of 100 t.</summary>
    private static async Task<List<(Guid Po, Guid PoLine)>> OrdersAsync(TestHarness h, TestReceiving r, int lineCount)
    {
        var p = r.Purchasing;
        var today = BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);
        var lines = new List<(Guid, Guid)>();
        for (var i = 0; lines.Count < lineCount; i++)
        {
            var po = (await h.RunAsync(
                new CreatePurchaseOrder(h.CompanyId, p.Buyer, $"po-{i}", p.PlantId, p.SupplierId, today, [new(p.Sand, "t", ReceiptsPerLine, 1000m), new(p.Cement, "t", ReceiptsPerLine, 1200m)]),
                new CreatePurchaseOrderHandler())).ResultRef;
            await h.RunAsync(new SubmitPurchaseOrder(h.CompanyId, p.Buyer, $"po-{i}-s", p.PlantId, po, 1), new SubmitPurchaseOrderHandler());
            await h.RunAsync(new ApprovePurchaseOrder(h.CompanyId, p.Controller, $"po-{i}-a", p.PlantId, po, 2), new ApprovePurchaseOrderHandler());
            await using var command = h.Admin.CreateCommand("SELECT po_line_id FROM pur.purchase_order_line WHERE po_id = @p ORDER BY line_no");
            command.Parameters.AddWithValue("p", po);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                lines.Add((po, reader.GetGuid(0)));
            }
        }

        return lines;
    }

    /// <summary>Nearest-rank percentile.</summary>
    private static TimeSpan Percentile(List<TimeSpan> sorted, int percent)
        => sorted.Count == 0 ? TimeSpan.Zero : sorted[Math.Max(0, ((percent * sorted.Count) + 99) / 100 - 1)];

    private static string Ms(TimeSpan value) => (value.Ticks / TimeSpan.TicksPerMillisecond).ToString(CultureInfo.InvariantCulture);

    private static string Seconds(TimeSpan value) => (value.Ticks / TimeSpan.TicksPerMillisecond / 100 / 10m).ToString("0.0", CultureInfo.InvariantCulture);

    private static string? Arg(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
