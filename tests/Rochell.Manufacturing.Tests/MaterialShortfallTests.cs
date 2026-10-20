using System.Globalization;
using System.Text;
using System.Text.Json;
using Rochell.Manufacturing.Runs;
using Rochell.Manufacturing.Shortfalls;
using Rochell.Platform.Commands;
using Rochell.Reconciliation;
using Rochell.Sales.Opening;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Manufacturing.Tests;

/// <summary>
/// VS3-02c (E-VS3-02b-11…14, E-VS3-02c-1…5): a shift summary consumes 25,000 kg of cement with 20,000 kg in the patio. Without the PRODUCTION
/// policy's date it is refused; with it the missing 5,000 kg are a shortfall at the standard price 8.00 (40,000.00, Dr WIP / Cr
/// MATERIAL_SHORTFALL) and the patio is left at zero. Reversing the summary cancels it; posted again, an opening of 3,000 kg of cement in the
/// patio covers 3,000 kg of it: the area holds also 1,000 kg at 10.00 elsewhere, so the moving average is 34,000 ÷ 4,000 = 8.50 and the
/// settlement costs 25,500.00 against 24,000.00 at standard — 1,500.00 to price variance. 2,000 kg (16,000.00) stay open: MP-NEGATIVO
/// blocks INV-MOV until justified, the summary can no longer be reversed, and the inventory and WIP (with the shortfall) still match the ledger.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class MaterialShortfallTests(PostgresFixture postgres)
{
    private const string P47 = "0192f001-0000-7000-8000-00000000003a";
    private const string P48 = "0192f001-0000-7000-8000-00000000003b";
    private const string OpenInv = "0192f001-0000-7000-8000-000000000011";

    [Fact]
    public async Task Cement_consumed_without_stock_waits_as_a_shortfall_until_material_enters()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ProductionRunTests.SetupAsync(h);
        var shortAccount = await h.CreateAccountAsync("2190", "Materia prima consumida por regularizar", isControl: false);
        var variance = await h.CreateAccountAsync("5160", "Variación de precio de materiales", isControl: false);
        var clearing = await h.CreateAccountAsync("3990", "Contrapartida de migración", isControl: false);
        await h.CreateActiveMapAsync("MATERIAL_SHORTFALL", shortAccount);
        await h.CreateActiveMapAsync("MATERIAL_PRICE_VARIANCE", variance);
        await h.CreateActiveMapAsync("MIGRATION_CLEARING", clearing);
        await h.AdminRequireAsync(
            $"""
            UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE (posting_rule_id IN ('{P47}', '{P48}') AND version = 1) OR (posting_rule_id = '{OpenInv}' AND version = 2);
            """);
        var run = (await ProductionRunTests.Start(h, s, "start")).ResultRef;
        var recorded = JsonDocument.Parse((await ProductionRunTests.Record(h, s, run, "rec", cement: 25000m)).ResultPayload).RootElement;
        var version = recorded.GetProperty("version").GetInt64();

        // Without the policy's date: not enough cement.
        var refused = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new PostShiftSummary(h.CompanyId, s.Manager, "post-0", s.Plant, run, version), new PostShiftSummaryHandler()));
        var until = s.Today.AddDays(30).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        await h.CreateActivePolicyAsync("PRODUCTION", new Dictionary<string, string> { ["usage_tolerance_pct"] = "0.025", ["raw_material_negative_until"] = until }, s.Today.AddDays(-5));

        // Posted with the date: 5,000 kg short at 8.00.
        var posted = JsonDocument.Parse((await h.RunAsync(new PostShiftSummary(h.CompanyId, s.Manager, "post-1", s.Plant, run, version), new PostShiftSummaryHandler())).ResultPayload).RootElement;
        Assert.Equal(Inventory.InventoryErrors.InsufficientStock, refused.Code);
        Assert.Equal("CEMENTO-GU:5000.000000:40000.00", string.Join(',', posted.GetProperty("shortfalls").EnumerateArray()
            .Select(f => $"{f.GetProperty("materialCode").GetString()}:{f.GetProperty("quantity").GetString()}:{f.GetProperty("value").GetString()}")));
        Assert.Equal("-40000.00", await h.ScalarAsync<string>("SELECT " + ProductionRunTests.Balance(shortAccount)));
        Assert.Equal(0m, await h.ScalarAsync<decimal>("SELECT coalesce(sum(quantity), 0) FROM inv.inv_stock_balance WHERE item_id = @i", ("i", s.Cement)));
        var list = JsonDocument.Parse(await h.QueryAsync(new ListMaterialShortfalls(h.CompanyId, s.Manager), new ListMaterialShortfallsHandler())).RootElement;
        Assert.Equal((1, "40000.0000", true, "-5000.000000"), (list.GetProperty("openCount").GetInt32(), list.GetProperty("openValue").GetString(), list.GetProperty("allowed").GetBoolean(),
            list.GetProperty("balances")[0].GetProperty("balance").GetString()));

        // Reversed before any material entered: the shortfall is cancelled with its journal and the cement is back.
        await h.RunAsync(new ReverseShiftSummary(h.CompanyId, s.Manager, "rev", s.Plant, run, version + 1, "Conteo equivocado"), new ReverseShiftSummaryHandler());
        Assert.Equal(("CANCELLED", "0.00", 20000m), (await h.ScalarAsync<string>("SELECT string_agg(status, ',') FROM inv.material_shortfall"), await h.ScalarAsync<string>("SELECT " + ProductionRunTests.Balance(shortAccount)),
            await h.ScalarAsync<decimal>("SELECT sum(quantity) FROM inv.inv_stock_balance WHERE item_id = @i", ("i", s.Cement))));
        await h.RunAsync(new PostShiftSummary(h.CompanyId, s.Manager, "post-2", s.Plant, run, 1), new PostShiftSummaryHandler());

        // 1,000 kg at 10.00 in the other location of the area, then an opening of 3,000 kg in the patio.
        var stock = await h.ScalarAsync<Guid>("SELECT location_id FROM md.location WHERE plant_id = @p AND location_id <> @a AND NOT is_curing ORDER BY code LIMIT 1", ("p", s.Plant), ("a", s.Patio));
        await h.RunAsync(new TestReceiveStock(h.CompanyId, h.SessionId, "r-b", stock, s.Cement, 1000m, 10000.00m, s.Today), new TestReceiveStockHandler());
        var plantCode = await h.ScalarAsync<string>("SELECT code FROM md.plant WHERE plant_id = @p", ("p", s.Plant));
        var patioCode = await h.ScalarAsync<string>("SELECT code FROM md.location WHERE location_id = @l", ("l", s.Patio));
        var csv = Convert.ToBase64String(Encoding.UTF8.GetBytes($"planta,ubicacion,producto,cantidad,documento\n{plantCode},{patioCode},CEMENTO-GU,3000,CONTEO-MP-1\n"));
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var approver = await h.SessionWithRolesAsync("APROBADOR_POLITICAS");
        var batch = (await h.RunAsync(new PrepareOpeningInventory(h.CompanyId, controller, "open", "mp.csv", csv, s.Today.AddDays(1)), new PrepareOpeningInventoryHandler())).ResultRef;
        var opened = JsonDocument.Parse((await h.RunAsync(new PostOpeningInventory(h.CompanyId, approver, "open-post", batch, 1), new PostOpeningInventoryHandler())).ResultPayload).RootElement;

        Assert.True(opened.GetProperty("settlementJournalId").GetString() is not null);
        Assert.Equal("24000.0000", await h.ScalarAsync<string>("SELECT value::text FROM mig.opening_inventory_line"));
        Assert.Equal("3000.000000:24000.0000:25500.0000", await h.ScalarAsync<string>("SELECT qty || ':' || std_value || ':' || actual_value FROM inv.material_shortfall_settlement"));
        Assert.Equal(("OPEN:3000.000000:24000.0000", "-16000.00", "1500.00"), (
            await h.ScalarAsync<string>("SELECT status || ':' || settled_qty || ':' || settled_value FROM inv.material_shortfall WHERE status <> 'CANCELLED'"),
            await h.ScalarAsync<string>("SELECT " + ProductionRunTests.Balance(shortAccount)), await h.ScalarAsync<string>("SELECT " + ProductionRunTests.Balance(variance))));
        Assert.Equal((0m, 1000m, 8500.00m), (
            await h.ScalarAsync<decimal>("SELECT coalesce(sum(quantity), 0) FROM inv.inv_stock_balance WHERE item_id = @i AND location_id = @l", ("i", s.Cement), ("l", s.Patio)),
            await h.ScalarAsync<decimal>("SELECT quantity FROM inv.inv_valuation_balance WHERE item_id = @i", ("i", s.Cement)),
            await h.ScalarAsync<decimal>("SELECT value FROM inv.inv_valuation_balance WHERE item_id = @i", ("i", s.Cement))));

        // Covered in part: the summary can no longer be reversed; MP-NEGATIVO blocks INV-MOV until justified; the inventory matches the ledger.
        var noReverse = await Assert.ThrowsAsync<DomainException>(
            () => h.RunAsync(new ReverseShiftSummary(h.CompanyId, s.Manager, "rev-2", s.Plant, run, 2, "Tarde"), new ReverseShiftSummaryHandler()));
        var recon = await Reconcile(h, controller, "recon-1");
        var shortfall = await h.ScalarAsync<Guid>("SELECT shortfall_id FROM inv.material_shortfall WHERE status = 'OPEN'");
        var noReason = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new JustifyMaterialShortfall(h.CompanyId, s.Manager, "j-0", shortfall, " "), new JustifyMaterialShortfallHandler()));
        await h.RunAsync(new JustifyMaterialShortfall(h.CompanyId, s.Manager, "j", shortfall, "Llega el cemento del pedido 0045 el lunes"), new JustifyMaterialShortfallHandler());
        var after = await Reconcile(h, controller, "recon-2");

        Assert.Equal((ManufacturingErrors.ShortfallSettled, ManufacturingErrors.ReasonRequired), (noReverse.Code, noReason.Code));
        Assert.Equal("INV-VALUE-GL:MATCHED,MP-NEGATIVO:EXCEPTIONS,VALUE-GL-LINK:MATCHED,WIP-GL:MATCHED", recon);
        Assert.Equal("INV-VALUE-GL:MATCHED,MP-NEGATIVO:EXCEPTIONS,VALUE-GL-LINK:MATCHED,WIP-GL:MATCHED", after);
        Assert.Equal("MATERIAL_SHORTFALL_JUSTIFIED:WARNING:-:16000.0000", await h.ScalarAsync<string>(
            """
            SELECT x.classification || ':' || x.severity || ':' || coalesce(x.component, '-') || ':' || x.value_a FROM rec.recon_exception x JOIN rec.recon_run r ON r.run_id = x.run_id
            WHERE r.recon_code = 'MP-NEGATIVO' ORDER BY r.run_id DESC LIMIT 1
            """));
    }

    private static async Task<string> Reconcile(TestHarness h, Guid controller, string key)
    {
        var result = JsonDocument.Parse((await h.RunAsync(new RunReconciliation(h.CompanyId, controller, key, ["MP-NEGATIVO", "INV-VALUE-GL", "VALUE-GL-LINK", "WIP-GL"]), new RunReconciliationHandler()))
            .ResultPayload).RootElement;
        return string.Join(',', result.GetProperty("runs").EnumerateArray().Select(r => $"{r.GetProperty("code").GetString()}:{r.GetProperty("status").GetString()}").Order(StringComparer.Ordinal));
    }
}
