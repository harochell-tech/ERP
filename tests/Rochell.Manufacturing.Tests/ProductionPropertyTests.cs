using System.Globalization;
using System.Text;
using System.Text.Json;
using Rochell.Inventory;
using Rochell.Manufacturing.Costing;
using Rochell.Manufacturing.Lots;
using Rochell.Manufacturing.Recipes;
using Rochell.Manufacturing.Runs;
using Rochell.Manufacturing.Shifts;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Reconciliation;
using Rochell.Sales;
using Rochell.Sales.Customers;
using Rochell.Sales.Deliveries;
using Rochell.Sales.Orders;
using Rochell.Sales.Pricing;
using Rochell.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Rochell.Manufacturing.Tests;

/// <summary>
/// INV-M (E-MFG1-08-1/2/5): random but reproducible (seeded) sequences over one plant, two products on one machine and three
/// materials — runs, shift summaries (some consuming more than the stock), postings, reversals, releases before and after curing,
/// blocks and unblocks, scrap in curing and in the yard, new standards (revaluation), pickups at the plant (sometimes from CURADO),
/// restocks, clock advances and settlements of ended months. After EVERY step the invariants are re-summed by SQL; every 25 steps
/// and at the end INV-VALUE-GL, INV-QTY-BALANCE, VALUE-GL-LINK and WIP-GL must have no ERROR. A failure reports the seed and the
/// steps so far. ROCHELL_INVM_SEEDS (default 1,2,3) and ROCHELL_INVM_STEPS (default 150) tune the run (workflow `inv-m`).
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ProductionPropertyTests(PostgresFixture postgres, ITestOutputHelper output)
{
    private const int CheckpointEvery = 25;
    private const string Hash = "5f70bf18a086007016e948b04aed3b82103a36bea41755b6cddfaf10ace3c6ef";

    private static readonly string[] Reconciliations = ["INV-VALUE-GL", "INV-QTY-BALANCE", "VALUE-GL-LINK", "WIP-GL"];

    /// <summary>Rejections a random sequence legitimately runs into; anything else fails the run.</summary>
    private static readonly HashSet<string> ExpectedRejections = new(StringComparer.Ordinal)
    {
        ManufacturingErrors.InvalidState, ManufacturingErrors.RunExists, ManufacturingErrors.CuringNotDone, ManufacturingErrors.LotMoved,
        ManufacturingErrors.CollectorSettled, ManufacturingErrors.MonthNotEnded, ManufacturingErrors.RunsOpen, ManufacturingErrors.QuantityInvalid,
        InventoryErrors.InsufficientStock, DeliveryErrors.LocationInvalid, SalesErrors.InTransitExists,
    };

    private static readonly (string Name, string Sql)[] Invariants =
    [
        ("journals balanced", "SELECT count(*) FROM (SELECT journal_id FROM fin.gl_entry GROUP BY journal_id HAVING sum(debit) <> sum(credit)) x"),
        ("stock ≥ 0; valuation = value entries = inventory GL (P-3)", """
            SELECT (SELECT count(*) FROM inv.inv_stock_balance WHERE quantity < 0)
                 + (SELECT count(*) FROM inv.inv_valuation_balance v
                    WHERE v.value <> (SELECT coalesce(sum(e.amount), 0) FROM inv.inv_value_entry e WHERE e.valuation_area_id = v.valuation_area_id AND e.item_id = v.item_id))
                 + (SELECT count(*) WHERE (SELECT coalesce(sum(value), 0) FROM inv.inv_valuation_balance)
                                         <> (SELECT coalesce(sum(debit - credit), 0) FROM fin.gl_entry WHERE account_role IN ('RAW_MATERIAL', 'FINISHED_GOODS', 'FINISHED_GOODS_IN_TRANSIT')))
            """),
        ("a settled collector has no WIP", """
            SELECT count(*) FROM mfg.cost_collector k
            WHERE k.status = 'SETTLED' AND (SELECT coalesce(sum(debit - credit), 0) FROM fin.gl_entry WHERE account_role = 'WIP' AND subledger_ref = k.collector_id) <> 0
            """),
        ("released lots are out of CURADO; curing and blocked lots are only in CURADO", """
            SELECT count(*) FROM mfg.fg_lot f
            WHERE (f.status = 'RELEASED' AND EXISTS (SELECT 1 FROM inv.inv_stock_balance b JOIN md.location l ON l.location_id = b.location_id WHERE b.lot_id = f.lot_id AND l.is_curing AND b.quantity > 0))
               OR (f.status IN ('CURING', 'BLOCKED') AND EXISTS (SELECT 1 FROM inv.inv_stock_balance b JOIN md.location l ON l.location_id = b.location_id WHERE b.lot_id = f.lot_id AND NOT l.is_curing AND b.quantity > 0))
            """),
        ("nothing is dispatched from CURADO", "SELECT count(*) FROM log.delivery_line_lot x JOIN md.location l ON l.location_id = x.source_location_id WHERE l.is_curing"),
        ("a lot's racks add up to its good units", """
            SELECT count(*) FROM mfg.fg_lot f JOIN mfg.shift_summary s ON s.summary_id = f.summary_id
            WHERE f.status <> 'VOIDED' AND (SELECT sum(units) FROM mfg.rack k WHERE k.lot_id = f.lot_id) <> s.good_units
            """),
        ("a posted summary has its lot; a reversed one's lot is voided", """
            SELECT count(*) FROM mfg.shift_summary s LEFT JOIN mfg.fg_lot f ON f.summary_id = s.summary_id
            WHERE (s.status = 'POSTED' AND (f.lot_id IS NULL OR f.status = 'VOIDED')) OR (s.status = 'REVERSED' AND f.status IS DISTINCT FROM 'VOIDED') OR (s.status = 'DRAFT' AND f.lot_id IS NOT NULL)
            """),
    ];

    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        foreach (var seed in (Environment.GetEnvironmentVariable("ROCHELL_INVM_SEEDS") ?? "1,2,3").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            data.Add(int.Parse(seed, CultureInfo.InvariantCulture));
        }

        return data;
    }

    private sealed class People
    {
        public Guid Supervisor { get; set; }

        public Guid Manager { get; set; }

        public Guid Quality { get; set; }

        public Guid Controller { get; set; }

        public Guid Approver { get; set; }

        public Guid Seller { get; set; }

        public Guid Dispatch { get; set; }

        public Guid Fixture { get; set; }

        public async Task RefreshAsync(TestHarness h)
        {
            Supervisor = await h.SessionWithRolesAsync("SUPERVISOR_PRODUCCION");
            Manager = await h.SessionWithRolesAsync("GERENTE_PLANTA");
            Quality = await h.SessionWithRolesAsync("CALIDAD");
            Controller = await h.SessionWithRolesAsync("CONTROLLER");
            Approver = await h.SessionWithRolesAsync("APROBADOR_POLITICAS");
            Seller = await h.SessionWithRolesAsync("VENDEDOR");
            Dispatch = await h.SessionWithRolesAsync("DESPACHO");
            Fixture = await h.CreateSessionAsync(h.UserId);
        }
    }

    [Trait("AcceptanceMfg1", "INV-M")]
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Random_production_sequences_keep_every_invariant(int seed)
    {
        var steps = int.Parse(Environment.GetEnvironmentVariable("ROCHELL_INVM_STEPS") ?? "150", CultureInfo.InvariantCulture);
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var s = await ProductionRunTests.SetupAsync(h); // BLOQUE-6 on BESSER-1 with recipe and standard 28.00, shift DIA, stock of three materials
        var p = new People();
        await p.RefreshAsync(h);

        // A second product on the same machine, a night shift, and what scrap, settlement, revaluation and pickups post to.
        foreach (var (role, code, control) in new[]
        {
            ("MATERIAL_USAGE_VARIANCE", "5102", false), ("MATERIAL_PRICE_VARIANCE", "5103", false), ("PRODUCTION_SCRAP", "5160", false), ("STANDARD_REVALUATION", "5190", false),
            ("FINISHED_GOODS_IN_TRANSIT", "1351", true), ("COGS", "5100", false), ("CONTRACT_ASSET", "1240", true), ("REVENUE_PRODUCT", "4110", false), ("TRANSIT_LOSS", "6900", false),
        })
        {
            await h.CreateActiveMapAsync(role, await h.CreateAccountAsync(code, role, control));
        }

        await h.AdminRequireAsync(
            $"""
            UPDATE fin.posting_rule_version v SET status = 'ACTIVE', approved_by = '{h.UserId}'
            FROM fin.posting_rule r WHERE r.posting_rule_id = v.posting_rule_id AND v.version = 1 AND v.status = 'DRAFT' AND r.code IN ('P-12', 'P-13', 'REVAL', 'P-15', 'P-15R', 'P-16', 'P-30');
            """);
        await h.CreateActivePolicyAsync("CREDIT", new Dictionary<string, string>
        {
            ["overdue_days_block"] = "30",
            ["ar_aging_bucket_1_days"] = "30",
            ["ar_aging_bucket_2_days"] = "60",
            ["ar_aging_bucket_3_days"] = "90",
        });
        await h.CreateActivePolicyAsync("REVENUE_ACCOUNTING", new Dictionary<string, string>
        {
            ["unbilled_delivery_presentation"] = "CONTRACT_ASSET",
            ["unbilled_aging_alert_days"] = "30",
            ["delivery_open_alert_hours"] = "24",
        });
        var paver = Guid.CreateVersion7();
        await h.AdminRequireAsync($"INSERT INTO md.item VALUES ('{paver}', '{h.CompanyId}', 'ADOQUIN-H', 'Adoquín holandés', 'FINISHED_GOOD', 'un', 'ADOQUIN', 'ACTIVE', 1)");
        var paverRecipe = (await h.RunAsync(
            new PrepareRecipe(h.CompanyId, p.Supervisor, "paver-recipe", s.Plant, paver, s.Machine, 120m, 4m, 480m, 12, 72,
                [new RecipeLineInput(s.Cement, 150m), new RecipeLineInput(s.Sand, 2.0m), new RecipeLineInput(s.Additive, 1.0m)]),
            new PrepareRecipeHandler())).ResultRef;
        await h.RunAsync(new ApproveRecipe(h.CompanyId, p.Manager, "paver-recipe-a", s.Plant, paverRecipe), new ApproveRecipeHandler());
        var blockRecipe = await h.ScalarAsync<Guid>("SELECT recipe_version_id FROM mfg.recipe_version WHERE item_id = @i AND status = 'ACTIVE'", ("i", s.Block));
        async Task NewStandardAsync(Guid recipe, decimal conversion, string key)
        {
            var cost = (await h.RunAsync(
                new PrepareStandardCostFromRecipe(h.CompanyId, p.Controller, key, recipe, [new(s.Cement, 8.00m), new(s.Sand, 1000.00m), new(s.Additive, 50.00m)], conversion),
                new PrepareStandardCostFromRecipeHandler())).ResultRef;
            await h.RunAsync(new ApproveStandardCost(h.CompanyId, p.Approver, key + "-a", cost), new ApproveStandardCostHandler());
        }

        await NewStandardAsync(paverRecipe, 4.50m, "paver-cost");
        var night = (await h.RunAsync(new DefineShift(h.CompanyId, p.Manager, "night", s.Plant, "NOCHE", new TimeOnly(19, 0), new TimeOnly(7, 0)), new DefineShiftHandler())).ResultRef;
        var prices = (await h.RunAsync(new PreparePriceList(h.CompanyId, p.Controller, "prices", [new(s.Block, "un", 50.00m), new(paver, "un", 45.00m)]), new PreparePriceListHandler())).ResultRef;
        await h.RunAsync(new ApprovePriceList(h.CompanyId, p.Approver, "prices-a", prices), new ApprovePriceListHandler());
        var credit = await h.SessionWithRolesAsync("CREDITO");
        var customer = (await h.RunAsync(new CreateCustomer(h.CompanyId, p.Seller, "customer", "131925332", "Constructora Uno"), new CreateCustomerHandler())).ResultRef;
        var terms = JsonDocument.Parse((await h.RunAsync(new PrepareCustomerTerms(h.CompanyId, credit, "terms", customer, 30, 100000000.00m, false), new PrepareCustomerTermsHandler())).ResultPayload)
            .RootElement.GetProperty("termsVersionId").GetGuid();
        await h.RunAsync(new ApproveCustomerTerms(h.CompanyId, p.Controller, "terms-a", terms), new ApproveCustomerTermsHandler());
        await h.RunAsync(new ActivateCustomer(h.CompanyId, credit, "customer-a", customer, 1), new ActivateCustomerHandler());

        (Guid Item, Guid Recipe, decimal UnitsPerBatch, (Guid Material, decimal PerBatch)[] Lines)[] products =
        [
            (s.Block, blockRecipe, 150m, [(s.Cement, 180m), (s.Sand, 1.8m), (s.Additive, 1.5m)]),
            (paver, paverRecipe, 120m, [(s.Cement, 150m), (s.Sand, 2.0m), (s.Additive, 1.0m)]),
        ];
        Guid[] shifts = [s.Day, night];
        var curing = Guid.Empty;

        var random = new Random(seed);
        var log = new StringBuilder();
        var rejections = new Dictionary<string, int>(StringComparer.Ordinal);
        var counter = 0;
        string Key() => $"k-{++counter}";
        T Pick<T>(List<T> items) => items[random.Next(items.Count)];
        DateOnly Today() => BusinessCalendar.DefaultBusinessDate(clock.UtcNow);

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
            var run = await h.RunAsync(new RunReconciliation(h.CompanyId, p.Controller, Key(), Reconciliations), new RunReconciliationHandler());
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
                        // The supervisor starts a run of one product on one shift, today or one or two days back.
                        var product = products[random.Next(products.Length)];
                        var shift = shifts[random.Next(shifts.Length)];
                        var date = Today().AddDays(-random.Next(3));
                        await Step($"start {(product.Item == paver ? "paver" : "block")} {date:yyyy-MM-dd}", () =>
                            h.RunAsync(new StartProductionRun(h.CompanyId, p.Supervisor, Key(), s.Plant, s.Machine, shift, date, product.Item), new StartProductionRunHandler()));
                        break;
                    }

                case 3 or 4:
                    {
                        // The summary of an open run: 1…12 batches, some units lost, consumption around the theoretical (now and then far above the stock).
                        var open = await Rows("SELECT run_id, 0, item_id::text, '' FROM mfg.production_run WHERE status = 'IN_PROGRESS' ORDER BY run_no");
                        if (open.Count > 0)
                        {
                            var (run, _, item, _) = Pick(open);
                            var product = products.Single(x => x.Item == Guid.Parse(item));
                            var batches = random.Next(1, 13);
                            var good = Math.Max(1m, (batches * product.UnitsPerBatch) - random.Next(0, 31));
                            var excess = random.Next(12) == 0 ? 20m : 1m;
                            var consumption = product.Lines.Select(l =>
                                new ConsumptionInput(l.Material, s.Patio, decimal.Round(l.PerBatch * batches * excess * (random.Next(90, 113) / 100m), 3), l.Material == s.Cement ? "kg" : l.Material == s.Sand ? "t" : "l")).ToList();
                            await Step($"record {batches} batches, {good} good (×{excess})", () =>
                                h.RunAsync(new RecordShiftSummary(h.CompanyId, p.Supervisor, Key(), s.Plant, run, batches, good, random.Next(0, 20), 0m, consumption), new RecordShiftSummaryHandler()));
                        }

                        break;
                    }

                case 5 or 6:
                    {
                        var drafts = await Rows("SELECT r.run_id, ss.version, r.run_no, '' FROM mfg.shift_summary ss JOIN mfg.production_run r ON r.run_id = ss.run_id WHERE ss.status = 'DRAFT' ORDER BY r.run_no");
                        if (drafts.Count > 0)
                        {
                            var (run, version, no, _) = Pick(drafts);
                            await Step($"post {no}", () => h.RunAsync(new PostShiftSummary(h.CompanyId, p.Manager, Key(), s.Plant, run, version), new PostShiftSummaryHandler()));
                            curing = await h.ScalarAsync<Guid?>("SELECT location_id FROM md.location WHERE plant_id = @p AND is_curing", ("p", s.Plant)) ?? Guid.Empty;
                        }

                        break;
                    }

                case 7:
                    {
                        var posted = await Rows("SELECT r.run_id, ss.version, r.run_no, '' FROM mfg.shift_summary ss JOIN mfg.production_run r ON r.run_id = ss.run_id WHERE ss.status = 'POSTED' ORDER BY r.run_no");
                        if (posted.Count > 0)
                        {
                            var (run, version, no, _) = Pick(posted);
                            await Step($"reverse {no}", () => h.RunAsync(new ReverseShiftSummary(h.CompanyId, p.Manager, Key(), s.Plant, run, version, "Corrección"), new ReverseShiftSummaryHandler()));
                        }

                        break;
                    }

                case 8 or 9:
                    {
                        var lots = await Rows("SELECT lot_id, version, '', '' FROM mfg.fg_lot WHERE status = 'CURING' ORDER BY lot_id");
                        if (lots.Count > 0)
                        {
                            var (lot, version, _, _) = Pick(lots);
                            await Step($"release {lot}", () => h.RunAsync(new ReleaseLot(h.CompanyId, p.Quality, Key(), s.Plant, lot, version, s.Patio), new ReleaseLotHandler()));
                        }

                        break;
                    }

                case 10:
                    {
                        var lots = await Rows("SELECT lot_id, version, status, '' FROM mfg.fg_lot WHERE status IN ('CURING', 'BLOCKED') ORDER BY lot_id");
                        if (lots.Count > 0)
                        {
                            var (lot, version, status, _) = Pick(lots);
                            await Step($"{(status == "CURING" ? "block" : "unblock")} {lot}", () => status == "CURING"
                                ? h.RunAsync(new BlockLot(h.CompanyId, p.Quality, Key(), s.Plant, lot, version, "Fisuras"), new BlockLotHandler())
                                : h.RunAsync(new UnblockLot(h.CompanyId, p.Quality, Key(), s.Plant, lot, version, "Revisado"), new UnblockLotHandler()));
                        }

                        break;
                    }

                case 11:
                    {
                        // Scrap part (or more than all) of a lot where it is.
                        var stock = await Rows(
                            "SELECT b.lot_id, 0, b.location_id::text, b.quantity::int FROM inv.inv_stock_balance b JOIN mfg.fg_lot f ON f.lot_id = b.lot_id WHERE b.quantity > 0 ORDER BY b.lot_id, b.location_id");
                        if (stock.Count > 0)
                        {
                            var (lot, _, location, quantity) = Pick(stock);
                            var take = random.Next(8) == 0 ? int.Parse(quantity, CultureInfo.InvariantCulture) + 1 : random.Next(1, Math.Max(2, int.Parse(quantity, CultureInfo.InvariantCulture) + 1));
                            await Step($"scrap {take} of {lot}", () =>
                                h.RunAsync(new ScrapLot(h.CompanyId, p.Manager, Key(), s.Plant, lot, Guid.Parse(location), take, "Rotura"), new ScrapLotHandler()));
                        }

                        break;
                    }

                case 12 or 13:
                    {
                        // A customer picks up 10…300 units of a product, loaded from the yard (or, now and then, tried from CURADO).
                        var product = products[random.Next(products.Length)];
                        var quantity = random.Next(10, 301);
                        var fromCuring = curing != Guid.Empty && random.Next(5) == 0;
                        await Step($"pickup {quantity} {(product.Item == paver ? "paver" : "block")}{(fromCuring ? " from CURADO" : string.Empty)}", async () =>
                        {
                            var order = (await h.RunAsync(
                                new CreateSalesOrder(h.CompanyId, p.Seller, Key(), customer, s.Plant, DeliveryTerms.PickupAtPlant, null, null, null, [new(product.Item, "un", quantity)]),
                                new CreateSalesOrderHandler())).ResultRef;
                            await h.RunAsync(new SubmitForCredit(h.CompanyId, p.Seller, Key(), order, 1), new SubmitForCreditHandler());
                            var line = await h.ScalarAsync<Guid>("SELECT line_id FROM sal.sales_order_line WHERE sales_order_id = @o", ("o", order));
                            var delivery = (await h.RunAsync(new PlanDelivery(h.CompanyId, p.Dispatch, Key(), order, [new(line, quantity)]), new PlanDeliveryHandler())).ResultRef;
                            var deliveryLine = await h.ScalarAsync<Guid>("SELECT delivery_line_id FROM log.delivery_line WHERE delivery_id = @d", ("d", delivery));
                            await h.RunAsync(new StartLoading(h.CompanyId, p.Dispatch, Key(), delivery, 1, null, null, "A 123-456", "Pedro Cliente"), new StartLoadingHandler());
                            await h.RunAsync(new ConfirmLoaded(h.CompanyId, p.Dispatch, Key(), delivery, 2, [new(deliveryLine, fromCuring ? curing : s.Patio)]), new ConfirmLoadedHandler());
                            await h.RunAsync(new RecordGateOut(h.CompanyId, p.Dispatch, Key(), delivery, 3, 30000m, 8000m, "TK", Hash), new RecordGateOutHandler());
                        });
                        break;
                    }

                case 14:
                    {
                        var product = products[random.Next(products.Length)];
                        var conversion = random.Next(400, 701) / 100m;
                        await Step($"new standard {(product.Item == paver ? "paver" : "block")} conversion {conversion}", () => NewStandardAsync(product.Recipe, conversion, Key()));
                        break;
                    }

                case 15:
                    {
                        await Step("restock", async () =>
                        {
                            await h.RunAsync(new TestReceiveStock(h.CompanyId, p.Fixture, Key(), s.Patio, s.Cement, 5000m, 41000.00m, Today()), new TestReceiveStockHandler());
                            await h.RunAsync(new TestReceiveStock(h.CompanyId, p.Fixture, Key(), s.Patio, s.Sand, 30m, 30150.00m, Today()), new TestReceiveStockHandler());
                            await h.RunAsync(new TestReceiveStock(h.CompanyId, p.Fixture, Key(), s.Patio, s.Additive, 50m, 2500.00m, Today()), new TestReceiveStockHandler());
                        });
                        break;
                    }

                case 16 or 17:
                    {
                        // Time passes: most often hours, now and then weeks (so months end and collectors can settle).
                        var hours = random.Next(15) == 0 ? random.Next(15, 26) * 24 : random.Next(6, 37);
                        log.AppendLine(CultureInfo.InvariantCulture, $"{counter}: advance {hours} h");
                        clock.Advance(TimeSpan.FromHours(hours));
                        await p.RefreshAsync(h);
                        break;
                    }

                default:
                    {
                        var collectors = await Rows("SELECT collector_id, version, period_month::text, '' FROM mfg.cost_collector WHERE status = 'OPEN' ORDER BY period_month, collector_id");
                        if (collectors.Count > 0)
                        {
                            var (collector, version, month, _) = Pick(collectors);
                            await Step($"settle {collector} ({month})", () =>
                                h.RunAsync(new SettleCostCollector(h.CompanyId, p.Controller, Key(), s.Plant, collector, version), new SettleCostCollectorHandler()));
                        }

                        break;
                    }
            }

            foreach (var (name, sql) in Invariants)
            {
                var violations = await h.ScalarAsync<long>(sql);
                Assert.True(violations == 0, $"Seed {seed}, after step {step} ({counter}): invariant '{name}' broken ({violations}).\n{log}");
            }

            if ((step + 1) % CheckpointEvery == 0)
            {
                await CheckpointAsync($"step {step}");
            }
        }

        await CheckpointAsync("end");
        output.WriteLine($"Seed {seed}: {steps} steps; rejections {string.Join(", ", rejections.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => $"{r.Key} {r.Value}"))}");
        output.WriteLine(await h.ScalarAsync<string>(
            """
            SELECT 'runs ' || (SELECT count(*) FROM mfg.production_run) || ', posted ' || (SELECT count(*) FROM mfg.shift_summary WHERE status = 'POSTED')
                   || ', reversed ' || (SELECT count(*) FROM mfg.shift_summary WHERE status = 'REVERSED') || ', lots released ' || (SELECT count(*) FROM mfg.fg_lot WHERE status = 'RELEASED')
                   || ', scrapped ' || (SELECT count(*) FROM mfg.lot_scrap) || ', deliveries ' || (SELECT count(*) FROM log.delivery WHERE status = 'DELIVERED')
                   || ', collectors settled ' || (SELECT count(*) FROM mfg.cost_collector WHERE status = 'SETTLED')
            """));
    }
}
