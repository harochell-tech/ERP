using System.Text;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Reconciliation;
using Rochell.Sales.Opening;
using Rochell.Sales.Pricing;
using Rochell.Sales.Queries;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>VS3-02b: opening finished goods (v2.1 §6, E-VS3-02b-1…10).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class OpeningInventoryTests(PostgresFixture postgres)
{
    private const string OpenInv = "0192f001-0000-7000-8000-000000000011";

    private sealed record Setup(Guid Controller, Guid Approver, Guid Plant, string PlantCode, Guid Block, Guid Paver, DateOnly Cutover, Guid FgAccount, Guid ClearingAccount);

    private static async Task<Setup> SetupAsync(TestHarness h)
    {
        var today = BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow);
        for (var y = today.Year - 1; y <= today.Year + 1; y++)
        {
            await h.OpenPeriodsAsync(y);
        }

        var plant = await h.CreatePlantAsync(code: "HIGUEY");
        await h.CreateLocationAsync(plant, "PATIO");
        var area = await h.ScalarAsync<Guid>("SELECT valuation_area_id FROM md.plant WHERE plant_id = @p", ("p", plant));
        var block = Guid.CreateVersion7();
        var paver = Guid.CreateVersion7();
        await h.AdminRequireAsync(
            $"""
            INSERT INTO md.item VALUES ('{block}', '{h.CompanyId}', 'BLOQUE-6', 'Bloque de 6 pulgadas', 'FINISHED_GOOD', 'un', 'BLOQUE', 'ACTIVE', 1);
            INSERT INTO md.item VALUES ('{paver}', '{h.CompanyId}', 'ADOQUIN-H', 'Adoquín holandés', 'FINISHED_GOOD', 'un', 'ADOQUIN', 'ACTIVE', 1);
            INSERT INTO md.item VALUES (gen_random_uuid(), '{h.CompanyId}', 'ARENA', 'Arena', 'RAW_MATERIAL', 't', 'AGREGADO', 'ACTIVE', 1);
            UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '{OpenInv}' AND version = 1;
            """);
        var fg = await h.CreateAccountAsync("1350", "Inventario de producto terminado", isControl: true);
        var clearing = await h.CreateAccountAsync("3990", "Contrapartida de migración", isControl: false);
        await h.CreateActiveMapAsync("FINISHED_GOODS", fg);
        await h.CreateActiveMapAsync("MIGRATION_CLEARING", clearing);
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var approver = await h.SessionWithRolesAsync("APROBADOR_POLITICAS");
        var cost = JsonDocument.Parse((await h.RunAsync(new PrepareStandardCost(h.CompanyId, controller, "cost", block, area, 32.75m), new PrepareStandardCostHandler())).ResultPayload)
            .RootElement.GetProperty("costVersionId").GetGuid();
        await h.RunAsync(new ApproveStandardCost(h.CompanyId, approver, "cost-a", cost), new ApproveStandardCostHandler());
        return new Setup(controller, approver, plant, "HIGUEY", block, paver, new DateOnly(today.Year, today.Month, 1), fg, clearing);
    }

    private static string Csv(params string[] rows) => Convert.ToBase64String(Encoding.UTF8.GetBytes("﻿planta,ubicacion,producto,cantidad,documento\n" + string.Join("\n", rows) + "\n"));

    private static Task<CommandResult> Prepare(TestHarness h, Setup s, string key, string content, string file = "apertura.csv")
        => h.RunAsync(new PrepareOpeningInventory(h.CompanyId, s.Controller, key, file, content, s.Cutover), new PrepareOpeningInventoryHandler());

    private static Task<CommandResult> Post(TestHarness h, Setup s, Guid batch, string key, long version = 1)
        => h.RunAsync(new PostOpeningInventory(h.CompanyId, s.Approver, key, batch, version), new PostOpeningInventoryHandler());

    [Fact]
    public async Task A_batch_prepared_by_the_controller_and_posted_by_the_approver_opens_stock_at_standard_cost_against_migration_clearing()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        var batch = (await Prepare(h, s, "p", Csv("higuey,patio,bloque-6,1000,ADM-001", "HIGUEY,PATIO,BLOQUE-6,250.5,ADM-002"))).ResultRef;

        var controllerPosts = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new PostOpeningInventory(h.CompanyId, s.Controller, "self", batch, 1), new PostOpeningInventoryHandler()));
        var posted = JsonDocument.Parse((await Post(h, s, batch, "post")).ResultPayload).RootElement;

        var postingDate = s.Cutover.AddDays(-1);
        Assert.Equal(AuthorizationErrors.NotAuthorized, controllerPosts.Code);
        Assert.Equal(("POSTED", postingDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)), (posted.GetProperty("status").GetString(), posted.GetProperty("postingDate").GetString()));
        // 1000 × 32.75 = 32,750.00; 250.5 × 32.75 = 8,203.875 → 8,203.88 (half away from zero, as receipts).
        Assert.Equal("1250.500000:40953.8800", await h.ScalarAsync<string>($"SELECT quantity::text || ':' || value::text FROM inv.inv_valuation_balance WHERE item_id = '{s.Block}'"));
        Assert.Equal($"40953.88:-40953.88", await h.ScalarAsync<string>(
            $"SELECT (SELECT sum(debit - credit) FROM fin.gl_entry WHERE account_id = '{s.FgAccount}')::numeric(19,2)::text || ':' || (SELECT sum(debit - credit) FROM fin.gl_entry WHERE account_id = '{s.ClearingAccount}')::numeric(19,2)::text"));
        Assert.Equal($"AP-BLOQUE-6-{s.Cutover:yyyyMMdd}-1:OPENING|AP-BLOQUE-6-{s.Cutover:yyyyMMdd}-2:OPENING", await h.ScalarAsync<string>(
            "SELECT string_agg(l.lot_code || ':' || q.movement_type::text, '|' ORDER BY l.lot_code) FROM inv.inv_quantity_entry q JOIN inv.lot l ON l.lot_id = q.lot_id"));
        Assert.Equal(postingDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), await h.ScalarAsync<string>("SELECT DISTINCT posting_date::text FROM fin.gl_journal"));
        Assert.Equal(0L, await h.CountAsync("tax.tax_determination")); // E-VS3-02b-10: no tax consequence
        var run = JsonDocument.Parse((await h.RunAsync(new RunReconciliation(h.CompanyId, s.Controller, "rec", ["MIGRATION-CLEARING", "INV-VALUE-GL", "VALUE-GL-LINK"]), new RunReconciliationHandler())).ResultPayload).RootElement;
        Assert.Equal("INV-VALUE-GL:MATCHED,MIGRATION-CLEARING:EXCEPTIONS,VALUE-GL-LINK:MATCHED", string.Join(',', run.GetProperty("runs").EnumerateArray()
            .Select(r => $"{r.GetProperty("code").GetString()}:{r.GetProperty("status").GetString()}").Order(StringComparer.Ordinal)));
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetOpeningBatch(h.CompanyId, s.Approver, batch), new GetOpeningBatchHandler())).RootElement;
        Assert.Equal(("40953.88", 2, "DRAFT,POSTED"), (detail.GetProperty("header").GetProperty("total").GetString(), detail.GetProperty("lines").GetArrayLength(),
            string.Join(',', detail.GetProperty("history").EnumerateArray().Select(x => x.GetProperty("to").GetString()))));
    }

    [Fact]
    public async Task Lines_need_an_active_finished_good_with_an_approved_cost_and_each_file_and_document_is_posted_once()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);

        var header = await Assert.ThrowsAsync<DomainException>(() => Prepare(h, s, "h", Convert.ToBase64String(Encoding.UTF8.GetBytes("a,b\n1,2\n"))));
        var noCost = await Assert.ThrowsAsync<DomainException>(() => Prepare(h, s, "c", Csv("HIGUEY,PATIO,ADOQUIN-H,10,ADM-9")));
        var raw = await Assert.ThrowsAsync<DomainException>(() => Prepare(h, s, "r", Csv("HIGUEY,PATIO,ARENA,10,ADM-9")));
        var location = await Assert.ThrowsAsync<DomainException>(() => Prepare(h, s, "l", Csv("HIGUEY,BODEGA,BLOQUE-6,10,ADM-9")));
        var twice = await Assert.ThrowsAsync<DomainException>(() => Prepare(h, s, "d", Csv("HIGUEY,PATIO,BLOQUE-6,10,ADM-9", "HIGUEY,PATIO,BLOQUE-6,5,ADM-9")));
        var file = Csv("HIGUEY,PATIO,BLOQUE-6,10,ADM-1");
        var batch = (await Prepare(h, s, "ok", file)).ResultRef;
        await Post(h, s, batch, "post");
        var sameFile = await Assert.ThrowsAsync<DomainException>(() => Prepare(h, s, "again", file));
        var sameDocument = await Assert.ThrowsAsync<DomainException>(() => Prepare(h, s, "doc", Csv("HIGUEY,PATIO,BLOQUE-6,99,ADM-1")));

        Assert.Equal(
            (OpeningErrors.FileInvalid, OpeningErrors.CostMissing, SalesErrors.NotFinishedGood, OpeningErrors.FileInvalid, OpeningErrors.FileInvalid, OpeningErrors.FileAlreadyPosted, OpeningErrors.DocumentAlreadyLoaded),
            (header.Code, noCost.Code, raw.Code, location.Code, twice.Code, sameFile.Code, sameDocument.Code));
    }

    [Fact]
    public async Task A_closed_period_or_a_changed_cost_stops_the_posting()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        var area = await h.ScalarAsync<Guid>("SELECT valuation_area_id FROM md.plant WHERE plant_id = @p", ("p", s.Plant));
        var first = (await Prepare(h, s, "p1", Csv("HIGUEY,PATIO,BLOQUE-6,10,ADM-1"))).ResultRef;
        var cost = JsonDocument.Parse((await h.RunAsync(new PrepareStandardCost(h.CompanyId, s.Controller, "cost2", s.Block, area, 35.00m), new PrepareStandardCostHandler())).ResultPayload)
            .RootElement.GetProperty("costVersionId").GetGuid();
        await h.RunAsync(new ApproveStandardCost(h.CompanyId, s.Approver, "cost2-a", cost), new ApproveStandardCostHandler());
        var second = (await Prepare(h, s, "p2", Csv("HIGUEY,PATIO,BLOQUE-6,20,ADM-2"))).ResultRef; // at the new cost

        var changed = await Assert.ThrowsAsync<DomainException>(() => Post(h, s, first, "post1"));
        await h.SetComponentAsync(s.Cutover.AddDays(-1), "INV-MOV", "CLOSED");
        var closed = await Assert.ThrowsAsync<DomainException>(() => Post(h, s, second, "post2"));

        Assert.Equal((OpeningErrors.CostChanged, OpeningErrors.PeriodClosed), (changed.Code, closed.Code));
        Assert.Equal(0L, await h.CountAsync("inv.inv_quantity_entry"));
    }

    [Fact]
    public async Task A_posted_batch_is_reversed_exactly_with_a_reason_and_its_file_can_be_loaded_again()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await SetupAsync(h);
        var file = Csv("HIGUEY,PATIO,BLOQUE-6,100,ADM-1");
        var batch = (await Prepare(h, s, "p", file)).ResultRef;
        await Post(h, s, batch, "post");

        var noReason = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ReverseOpeningInventory(h.CompanyId, s.Approver, "r0", batch, 2, " "), new ReverseOpeningInventoryHandler()));
        await h.RunAsync(new ReverseOpeningInventory(h.CompanyId, s.Approver, "r", batch, 2, "Conteo físico equivocado"), new ReverseOpeningInventoryHandler());
        var again = (await Prepare(h, s, "p2", file)).ResultRef;
        await Post(h, s, again, "post2");

        Assert.Equal(OpeningErrors.ReasonRequired, noReason.Code);
        Assert.Equal("REVERSED,POSTED", await h.ScalarAsync<string>($"SELECT string_agg(status, ',' ORDER BY batch_id) FROM mig.migration_batch"));
        Assert.Equal("100.000000:3275.0000", await h.ScalarAsync<string>($"SELECT quantity::text || ':' || value::text FROM inv.inv_valuation_balance WHERE item_id = '{s.Block}'"));
        Assert.Equal("3275.00", await h.ScalarAsync<string>($"SELECT sum(debit - credit)::numeric(19,2)::text FROM fin.gl_entry WHERE account_id = '{s.FgAccount}'"));
        Assert.Equal($"AP-BLOQUE-6-{s.Cutover:yyyyMMdd}-1,AP-BLOQUE-6-{s.Cutover:yyyyMMdd}-2", await h.ScalarAsync<string>("SELECT string_agg(lot_code, ',' ORDER BY lot_code) FROM inv.lot"));
        var list = JsonDocument.Parse(await h.QueryAsync(new ListOpeningBatches(h.CompanyId, s.Controller, "REVERSED"), new ListOpeningBatchesHandler())).RootElement.GetProperty("items");
        Assert.Equal("Conteo físico equivocado", list[0].GetProperty("reversalReason").GetString());
    }
}
