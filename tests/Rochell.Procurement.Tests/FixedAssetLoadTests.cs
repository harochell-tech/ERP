using System.Text;
using System.Text.Json;
using Rochell.Finance.Ledger;
using Rochell.FixedAssets;
using Rochell.FixedAssets.Cards;
using Rochell.FixedAssets.Classes;
using Rochell.FixedAssets.Depreciation;
using Rochell.FixedAssets.Load;
using Rochell.Platform.Commands;
using Rochell.Procurement.Expenses;
using Rochell.Reconciliation;
using Rochell.TestInfrastructure;
using Xunit;
using static Rochell.Procurement.Tests.ExpenseInvoiceTests;
using static Rochell.Procurement.Tests.ForeignInvoiceTests;
using static Rochell.Procurement.Tests.ImportSettlementTests;

namespace Rochell.Procurement.Tests;

/// <summary>
/// AF1-04 (AF-09, AF-11, E-AF1-04-1…9): the initial load of existing assets (P-46) and the FA-GL reconciliation. The baseline's truck:
/// cost 2,400,000.00, accumulated 960,000.00 at the cut-off, bought 36 months before; class of 96 months and 10 % residual (240,000.00):
/// 1,200,000.00 left over 60 months = 20,000.00 a month.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class FixedAssetLoadTests(PostgresFixture postgres)
{
    private const string P44 = "0192f001-0000-7000-8000-000000000037";
    private const string P46 = "0192f001-0000-7000-8000-000000000039";
    private const string Header = "Código,Descripción,Categoría,Planta,Fecha de compra,Costo,Depreciación acumulada";

    private sealed record Trucks(Foreign F, string PlantCode, DateOnly Cutoff, DateOnly Bought);

    private static string File(params string[] rows) => Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('\n', [Header, .. rows]) + "\n"));

    private static void MoveTo(FakeClock clock, DateOnly date)
    {
        var target = date.ToDateTime(new TimeOnly(16, 0), DateTimeKind.Utc);
        if (target > clock.UtcNow)
        {
            clock.Advance(target - clock.UtcNow);
        }
    }

    /// <summary>A truck category with its approved class (96 months, 10 %), P-44 / P-46 active and «Contrapartida de saldos de apertura» mapped.</summary>
    private static async Task<Trucks> TrucksAsync(TestHarness h)
    {
        var f = await ForeignAsync(h);
        var cutoff = new DateOnly(Today(h).Year, Today(h).Month, 1).AddDays(-1);
        await h.OpenPeriodsAsync(cutoff.Year);
        await h.OpenPeriodsAsync(Today(h).Year + 1);
        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id IN ('{P44}', '{P46}') AND version = 1");
        await h.CreateActiveMapAsync("MIGRATION_CLEARING", await h.CreateAccountAsync("39900", "Contrapartida de saldos de apertura", isControl: false));
        var contador = await h.SessionWithRolesAsync("CONTADOR");
        async Task<Guid> AccountAsync(string key, string code, string name, string kind)
            => (await h.RunAsync(new CreateAccount(h.CompanyId, f.W.Controller, key, code, name, kind, false), new CreateAccountHandler())).ResultRef;
        var asset = await AccountAsync("a-veh", "15400", "Vehículos", "ASSET");
        var accumulated = await AccountAsync("a-dveh", "15490", "Depreciación acumulada de vehículos", "ASSET");
        var expense = await AccountAsync("a-gdep", "64100", "Gasto de depreciación", "EXPENSE");
        var category = (await h.RunAsync(new PrepareExpenseCategory(h.CompanyId, contador, "cat-cam", "CAMIONES", "Camiones", asset, "04", "GOODS"), new PrepareExpenseCategoryHandler())).ResultRef;
        await h.RunAsync(new ApproveExpenseCategories(h.CompanyId, f.W.Controller, "cat-cam-a", [category]), new ApproveExpenseCategoriesHandler());
        var prepared = await h.RunAsync(new PrepareAssetClass(h.CompanyId, contador, "k", category, 96, 10m, accumulated, expense), new PrepareAssetClassHandler());
        await h.RunAsync(new ApproveAssetClass(h.CompanyId, f.W.Controller, "k-a", prepared.ResultRef, 1), new ApproveAssetClassHandler());
        var plant = await h.ScalarAsync<string>("SELECT code FROM md.plant WHERE plant_id = @p", ("p", f.W.Plant));
        return new Trucks(f, plant!, cutoff, new DateOnly(cutoff.Year, cutoff.Month, 10).AddMonths(-36));
    }

    private static string Truck(Trucks t, string code = "CAM-001", string category = "CAMIONES", string accumulated = "960000.00")
        => $"{code},Camión Volvo FMX 2023,{category},{t.PlantCode},{t.Bought:yyyy-MM-dd},2400000.00,{accumulated}";

    private static Task<string?> BooksAsync(TestHarness h)
        => h.ScalarAsync<string>(
            """
            SELECT string_agg(a.code || ':' || coalesce((SELECT sum(e.debit - e.credit) FROM fin.gl_entry e WHERE e.account_id = a.account_id), 0)::numeric(19,2), ',' ORDER BY a.code)
            FROM fin.account a WHERE a.code IN ('15400', '15490', '39900', '64100')
            """);

    private static async Task<string> FindingsAsync(TestHarness h, string key, DateOnly cutoff)
    {
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        await h.RunAsync(new RunReconciliation(h.CompanyId, controller, key, ["FA-GL"], cutoff), new RunReconciliationHandler());
        return await h.ScalarAsync<string>(
            """
            SELECT coalesce(string_agg(x.classification || ':' || x.match_key || ':' || x.severity, ',' ORDER BY x.match_key), '-')
            FROM rec.recon_exception x
            WHERE x.run_id = (SELECT run_id FROM rec.recon_run WHERE recon_code = 'FA-GL' ORDER BY as_of DESC, run_id DESC LIMIT 1)
            """) ?? "-";
    }

    [Trait("AcceptanceAf1", "AF-09")]
    [Fact]
    public async Task AF09_an_existing_truck_is_loaded_against_the_opening_balances_and_depreciates_20000_for_its_60_months_left()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        var t = await TrucksAsync(h);
        var contador = await h.SessionWithRolesAsync("CONTADOR");

        // The preview judges each row; one error refuses the whole file.
        var bad = File(Truck(t), Truck(t, "CAM-001"), Truck(t, "CAM-002", "NO_EXISTE"), Truck(t, "CAM-003", accumulated: "2200000.00"));
        var preview = JsonDocument.Parse(await h.QueryAsync(new PreviewAssetLoad(h.CompanyId, contador, "activos.csv", bad, t.Cutoff), new PreviewAssetLoadHandler())).RootElement;
        var refused = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareAssetLoad(h.CompanyId, contador, "bad", "activos.csv", bad, t.Cutoff), new PrepareAssetLoadHandler()));
        var midMonth = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareAssetLoad(h.CompanyId, contador, "mid", "activos.csv", File(Truck(t)), t.Cutoff.AddDays(-1)), new PrepareAssetLoadHandler()));
        var good = JsonDocument.Parse(await h.QueryAsync(new PreviewAssetLoad(h.CompanyId, contador, "activos.csv", File(Truck(t)), t.Cutoff), new PreviewAssetLoadHandler()))
            .RootElement.GetProperty("items")[0];

        // A first load is reversed before any depreciation; a second one is posted and depreciates.
        var first = await h.RunAsync(new PrepareAssetLoad(h.CompanyId, contador, "l1", "activos.csv", File(Truck(t)), t.Cutoff), new PrepareAssetLoadHandler());
        var own = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveAssetLoad(h.CompanyId, contador, "own", first.ResultRef, 1), new ApproveAssetLoadHandler()));
        await h.RunAsync(new ApproveAssetLoad(h.CompanyId, t.F.W.Controller, "l1-a", first.ResultRef, 1), new ApproveAssetLoadHandler());
        var loaded = await BooksAsync(h);
        var card = await h.ScalarAsync<string>(
            "SELECT asset_no || '|' || external_code || '|' || status || '|' || months_depreciated || '|' || accumulated::numeric(19,2) || '|' || in_service_on FROM fa.asset");
        await h.RunAsync(new ReverseAssetLoad(h.CompanyId, t.F.W.Controller, "l1-x", first.ResultRef, 2, "Costo del camión equivocado"), new ReverseAssetLoadHandler());
        var reversed = await BooksAsync(h);

        var second = await h.RunAsync(new PrepareAssetLoad(h.CompanyId, contador, "l2", "activos.csv", File(Truck(t)), t.Cutoff), new PrepareAssetLoadHandler());
        await h.RunAsync(new ApproveAssetLoad(h.CompanyId, t.F.W.Controller, "l2-a", second.ResultRef, 1), new ApproveAssetLoadHandler());
        var month = t.Cutoff.AddDays(1);
        MoveTo(clock, month.AddMonths(1));
        var poster = await h.SessionWithRolesAsync("CONTADOR");
        var depreciation = JsonDocument.Parse((await h.RunAsync(new PostDepreciation(h.CompanyId, poster, "dep", month), new PostDepreciationHandler())).ResultPayload).RootElement;
        var approver = await h.SessionWithRolesAsync("CONTROLLER");
        var late = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new ReverseAssetLoad(h.CompanyId, approver, "l2-x", second.ResultRef, 2, "Costo del camión equivocado"), new ReverseAssetLoadHandler()));

        Assert.Equal("4|1|3", $"{preview.GetProperty("rows").GetInt32()}|{preview.GetProperty("valid").GetInt32()}|{preview.GetProperty("invalid").GetInt32()}");
        Assert.Equal(AssetLoadErrors.RowsInvalid, refused.Code);
        Assert.Equal(AssetLoadErrors.CutoffInvalid, midMonth.Code);
        Assert.Equal("36|96|240000.00|20000.00", $"{good.GetProperty("monthsDepreciated").GetInt32()}|{good.GetProperty("usefulLifeMonths").GetInt32()}|{good.GetProperty("residualValue").GetString()}|{good.GetProperty("monthlyAmount").GetString()}");
        Assert.Equal(AuthorizationErrors.NotAuthorized, own.Code);
        Assert.Equal("15400:2400000.00,15490:-960000.00,39900:-1440000.00,64100:0.00", loaded);
        Assert.Matches($"^AF-{t.Cutoff.Year}-000001\\|CAM-001\\|IN_SERVICE\\|36\\|960000.00\\|{t.Bought:yyyy-MM-dd}$", card);
        Assert.Equal("15400:0.00,15490:0.00,39900:0.00,64100:0.00", reversed);
        Assert.Equal("1|20000.00", $"{depreciation.GetProperty("assets").GetInt32()}|{depreciation.GetProperty("total").GetString()}");
        Assert.Equal(FixedAssetErrors.AssetDepreciated, late.Code);
        Assert.Equal("CANCELLED|36,IN_SERVICE|37", await h.ScalarAsync<string>("SELECT string_agg(status || '|' || months_depreciated, ',' ORDER BY asset_no) FROM fa.asset"));
    }

    [Trait("AcceptanceAf1", "AF-11")]
    [Fact]
    public async Task AF11_FA_GL_squares_the_cards_with_the_ledger_and_flags_a_month_left_without_depreciation()
    {
        var clock = new FakeClock();
        await using var h = await TestHarness.CreateAsync(postgres, clock);
        await h.OpenPeriodsAsync(Today(h).Year + 1);
        var s = await ShipmentAsync(h);
        var clean = await FindingsAsync(h, "r0", Today(h));

        // A posted fixed-asset line without its card (before AF-1): the account holds more than the cards.
        await h.AdminRequireAsync(
            """
            INSERT INTO core.state_history (state_history_id, company_id, aggregate_type, aggregate_id, status_kind, from_state, to_state, command, event_id)
            SELECT gen_random_uuid(), company_id, 'FixedAsset', asset_id, 'DOCUMENT', status, 'CANCELLED', 'test', (SELECT event_id FROM core.domain_event ORDER BY recorded_at LIMIT 1)
            FROM fa.asset;
            UPDATE fa.asset SET status = 'CANCELLED', version = version + 1;
            """);
        var missingCard = await FindingsAsync(h, "r1", Today(h));
        var contador = await h.SessionWithRolesAsync("CONTADOR");
        await h.RunAsync(new CreateCardsForPostedInvoices(h.CompanyId, contador, "bf"), new CreateCardsForPostedInvoicesHandler());
        var backfilled = await FindingsAsync(h, "r2", Today(h));

        // In service with a class, the next month ends without depreciation: an error for that month's close, a warning once past.
        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id = '{P44}' AND version = 1");
        var accumulated = (await h.RunAsync(new CreateAccount(h.CompanyId, s.F.W.Controller, "acc-dep", "15390", "Depreciación acumulada", "ASSET", false), new CreateAccountHandler())).ResultRef;
        var expense = (await h.RunAsync(new CreateAccount(h.CompanyId, s.F.W.Controller, "acc-gdep", "64100", "Gasto de depreciación", "EXPENSE", false), new CreateAccountHandler())).ResultRef;
        var prepared = await h.RunAsync(new PrepareAssetClass(h.CompanyId, contador, "k", s.F.Forklift, 60, 10m, accumulated, expense), new PrepareAssetClassHandler());
        await h.RunAsync(new ApproveAssetClass(h.CompanyId, s.F.W.Controller, "k-a", prepared.ResultRef, 1), new ApproveAssetClassHandler());
        var assetId = await h.ScalarAsync<Guid>("SELECT asset_id FROM fa.asset WHERE status = 'AWAITING_SERVICE'");
        await h.RunAsync(new PutFixedAssetInService(h.CompanyId, contador, "svc", assetId, 1, Today(h), s.F.W.Plant, "Encargado"), new PutFixedAssetInServiceHandler());
        var first = new DateOnly(Today(h).Year, Today(h).Month, 1).AddMonths(1);
        var firstEnd = first.AddMonths(1).AddDays(-1);
        MoveTo(clock, first.AddMonths(2).AddDays(2));
        var atFirstEnd = await FindingsAsync(h, "r3", firstEnd);
        var periodId = await h.ScalarAsync<Guid>("SELECT period_id FROM fin.period WHERE company_id = @c AND starts_on = @s", ("c", h.CompanyId), ("s", first));
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var close = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new CloseComponent(h.CompanyId, controller, "close", periodId, "FA-REC"), new CloseComponentHandler()));
        var later = await FindingsAsync(h, "r4", first.AddMonths(2).AddDays(-1));

        Assert.Equal("-", clean);
        Assert.Equal("FA_COST_DIFFERENCE:account:15300:ERROR", missingCard);
        Assert.Equal("-", backfilled);
        Assert.Equal($"FA_DEPRECIATION_MISSING:month:{first:yyyy-MM}:ERROR", atFirstEnd);
        Assert.NotNull(close.Code);
        Assert.Equal($"FA_DEPRECIATION_MISSING:month:{first:yyyy-MM}:WARNING,FA_DEPRECIATION_MISSING:month:{first.AddMonths(1):yyyy-MM}:ERROR", later);
    }
}
