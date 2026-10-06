using System.Text.Json;
using Rochell.Finance.Ledger;
using Rochell.FixedAssets;
using Rochell.FixedAssets.Cards;
using Rochell.FixedAssets.Classes;
using Rochell.FixedAssets.Depreciation;
using Rochell.FixedAssets.Disposals;
using Rochell.Platform.Commands;
using Rochell.Platform.Time;
using Rochell.Procurement.Imports;
using Rochell.TestInfrastructure;
using Xunit;
using static Rochell.Procurement.Tests.ExpenseInvoiceTests;
using static Rochell.Procurement.Tests.ImportSettlementTests;

namespace Rochell.Procurement.Tests;

/// <summary>
/// AF1-03 (AF-05…08, E-AF1-03-1…10): the month's depreciation (P-44) and the disposal or sale (P-45), with the clock moved month by month.
/// The forklift costs 564,000.00 with a class of 60 months and 10 % residual: 507,600.00 ÷ 60 = 8,460.00 a month; after 12 months
/// (101,520.00) it is sold for 450,000.00 against a book value of 462,480.00 — a loss of 12,480.00.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class FixedAssetDepreciationTests(PostgresFixture postgres)
{
    private const string P44 = "0192f001-0000-7000-8000-000000000037";
    private const string P45 = "0192f001-0000-7000-8000-000000000038";

    private sealed record Setup(TestHarness H, FakeClock Clock, Shipment S, Guid AssetId);

    private static DateOnly MonthOf(DateOnly date) => new(date.Year, date.Month, 1);

    /// <summary>Moves the clock forward to noon (Santo Domingo) of <paramref name="date"/>; never back.</summary>
    private static void MoveTo(FakeClock clock, DateOnly date)
    {
        var target = date.ToDateTime(new TimeOnly(16, 0), DateTimeKind.Utc);
        if (target > clock.UtcNow)
        {
            clock.Advance(target - clock.UtcNow);
        }
    }

    /// <summary>The shipment, the class, P-44 / P-45 and their accounts; the forklift in service today (settled first when <paramref name="settle"/>).</summary>
    private static async Task<Setup> InServiceAsync(PostgresFixture postgres, int life, bool settle)
    {
        var clock = new FakeClock();
        var h = await TestHarness.CreateAsync(postgres, clock);
        await h.OpenPeriodsAsync(Today(h).Year + 1);
        var s = await ShipmentAsync(h);
        if (settle)
        {
            await SettleAsync(h, s, "li");
        }

        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id IN ('{P44}', '{P45}') AND version = 1");
        await h.CreateActiveMapAsync("ASSET_SALE_RECEIVABLE", await h.CreateAccountAsync("13800", "Venta de activos por cobrar", isControl: false));
        await h.CreateActiveMapAsync("ASSET_DISPOSAL_GAIN", await h.CreateAccountAsync("71500", "Ganancia en venta de activos", isControl: false));
        await h.CreateActiveMapAsync("ASSET_DISPOSAL_LOSS", await h.CreateAccountAsync("81500", "Pérdida en baja de activos", isControl: false));
        var accumulated = (await h.RunAsync(new CreateAccount(h.CompanyId, s.F.W.Controller, "acc-dep", "15390", "Depreciación acumulada", "ASSET", false), new CreateAccountHandler())).ResultRef;
        var expense = (await h.RunAsync(new CreateAccount(h.CompanyId, s.F.W.Controller, "acc-gdep", "64100", "Gasto de depreciación", "EXPENSE", false), new CreateAccountHandler())).ResultRef;
        var contador = await h.SessionWithRolesAsync("CONTADOR");
        var prepared = await h.RunAsync(new PrepareAssetClass(h.CompanyId, contador, "k", s.F.Forklift, life, 10m, accumulated, expense), new PrepareAssetClassHandler());
        await h.RunAsync(new ApproveAssetClass(h.CompanyId, s.F.W.Controller, "k-a", prepared.ResultRef, 1), new ApproveAssetClassHandler());
        var assetId = await h.ScalarAsync<Guid>("SELECT asset_id FROM fa.asset");
        var version = await h.ScalarAsync<long>("SELECT version FROM fa.asset");
        await h.RunAsync(new PutFixedAssetInService(h.CompanyId, contador, "svc", assetId, version, Today(h), s.F.W.Plant, "Encargado de almacén"), new PutFixedAssetInServiceHandler());
        return new Setup(h, clock, s, assetId);
    }

    private static async Task SettleAsync(TestHarness h, Shipment s, string key)
    {
        ImportSettlementDocumentInput[] documents =
            [new("SUPPLIER_INVOICE", s.Goods), new("EXPENSE_INVOICE", s.Freight), new("EXPENSE_INVOICE", s.Agent), new("CUSTOMS_DECLARATION", s.Dua)];
        var clerk = await h.SessionWithRolesAsync("CUENTAS_POR_PAGAR");
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var prepared = await h.RunAsync(new PrepareImportSettlement(h.CompanyId, clerk, key, s.F.W.Plant, Today(h), null, documents), new PrepareImportSettlementHandler());
        await h.RunAsync(new ApproveImportSettlement(h.CompanyId, controller, key + "-a", prepared.ResultRef, 1), new ApproveImportSettlementHandler());
    }

    /// <summary>Moves to the first day after <paramref name="month"/> and posts it as a fresh Contador.</summary>
    private static async Task<JsonElement> DepreciateAsync(Setup x, DateOnly month)
    {
        MoveTo(x.Clock, month.AddMonths(1));
        var contador = await x.H.SessionWithRolesAsync("CONTADOR");
        var result = await x.H.RunAsync(new PostDepreciation(x.H.CompanyId, contador, $"dep-{month:yyyyMM}", month), new PostDepreciationHandler());
        return JsonDocument.Parse(result.ResultPayload).RootElement;
    }

    private static Task<string?> BooksAsync(TestHarness h, params string[] codes)
        => h.ScalarAsync<string>(
            $"""
            SELECT string_agg(a.code || ':' || coalesce((SELECT sum(e.debit - e.credit) FROM fin.gl_entry e WHERE e.account_id = a.account_id), 0)::numeric(19,2), ',' ORDER BY a.code)
            FROM fin.account a WHERE a.code IN ({string.Join(',', codes.Select(c => $"'{c}'"))})
            """);

    private static Task<string?> CardAsync(TestHarness h)
        => h.ScalarAsync<string>("SELECT status || '|' || cost::numeric(19,2) || '|' || accumulated::numeric(19,2) || '|' || months_depreciated FROM fa.asset");

    [Trait("AcceptanceAf1", "AF-05")]
    [Fact]
    public async Task AF05_the_forklift_depreciates_8460_a_month_once_in_order_and_the_latest_month_can_be_undone()
    {
        var x = await InServiceAsync(postgres, 60, settle: true);
        await using var h = x.H;
        var service = MonthOf(Today(h));
        var first = service.AddMonths(1);

        // The month of the service is not depreciated, nor a month that has not ended.
        var serviceMonth = await Assert.ThrowsAsync<DomainException>(() => DepreciateAsync(x, service));
        var notEnded = await Assert.ThrowsAsync<DomainException>(async () =>
        {
            var contador = await h.SessionWithRolesAsync("CONTADOR");
            await h.RunAsync(new PostDepreciation(h.CompanyId, contador, "early", first), new PostDepreciationHandler());
        });
        var posted = await DepreciateAsync(x, first);
        var books = await BooksAsync(h, "15300", "15390", "64100");
        var twice = await Assert.ThrowsAsync<DomainException>(async () =>
        {
            var contador = await h.SessionWithRolesAsync("CONTADOR");
            await h.RunAsync(new PostDepreciation(h.CompanyId, contador, "twice", first), new PostDepreciationHandler());
        });
        var plant = await h.ScalarAsync<bool>("SELECT bool_and(e.plant_id = @p) FROM fin.gl_entry e WHERE e.rule_line_code LIKE 'P44-%'", ("p", x.S.F.W.Plant));

        // Undone, the card is back to nothing depreciated and the month is posted again.
        var undoer = await h.SessionWithRolesAsync("CONTADOR");
        await h.RunAsync(new UndoDepreciation(h.CompanyId, undoer, "undo", posted.GetProperty("runId").GetGuid(), 1, "Faltó una factura del mes"), new UndoDepreciationHandler());
        var undone = await CardAsync(h);
        var again = await h.RunAsync(new PostDepreciation(h.CompanyId, undoer, "again", first), new PostDepreciationHandler());

        // A month is not skipped (E-AF1-03-2).
        MoveTo(x.Clock, first.AddMonths(3));
        var skipper = await h.SessionWithRolesAsync("CONTADOR");
        var skipped = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PostDepreciation(h.CompanyId, skipper, "skip", first.AddMonths(2)), new PostDepreciationHandler()));

        Assert.Equal(DepreciationErrors.Nothing, serviceMonth.Code);
        Assert.Equal(DepreciationErrors.MonthNotEnded, notEnded.Code);
        Assert.Equal("1|8460.00", $"{posted.GetProperty("assets").GetInt32()}|{posted.GetProperty("total").GetString()}");
        Assert.Equal("15300:564000.00,15390:-8460.00,64100:8460.00", books);
        Assert.Equal(DepreciationErrors.AlreadyPosted, twice.Code);
        Assert.True(plant);
        Assert.Equal("IN_SERVICE|564000.00|0.00|0", undone);
        Assert.Equal("IN_SERVICE|564000.00|8460.00|1", await CardAsync(h));
        Assert.Equal("15300:564000.00,15390:-8460.00,64100:8460.00", await BooksAsync(h, "15300", "15390", "64100"));
        Assert.Equal(DepreciationErrors.MonthSkipped, skipped.Code);
        Assert.NotEqual(Guid.Empty, again.ResultRef);
    }

    [Trait("AcceptanceAf1", "AF-06")]
    [Trait("AcceptanceAf1", "AF-07")]
    [Fact]
    public async Task AF06_a_cost_added_while_depreciating_is_spread_over_the_months_left_and_the_last_month_closes_exact()
    {
        // 7 months so the quotas round: 432,000.00 ÷ 7 on 480,000.00, then 445,885.71 ÷ 6 once the settlement adds 84,000.00.
        var x = await InServiceAsync(postgres, 7, settle: false);
        await using var h = x.H;
        var first = MonthOf(Today(h)).AddMonths(1);
        await DepreciateAsync(x, first);
        await SettleAsync(h, x.S, "li-late");
        for (var m = 1; m < 7; m++)
        {
            await DepreciateAsync(x, first.AddMonths(m));
        }

        var nothing = await Assert.ThrowsAsync<DomainException>(() => DepreciateAsync(x, first.AddMonths(7)));

        Assert.Equal(
            "61714.29,74314.29,74314.28,74314.29,74314.28,74314.29,74314.28",
            await h.ScalarAsync<string>(
                "SELECT string_agg(l.amount::numeric(19,2)::text, ',' ORDER BY r.month) FROM fa.depreciation_line l JOIN fa.depreciation_run r ON r.run_id = l.run_id WHERE r.status = 'POSTED'"));
        Assert.Equal("IN_SERVICE|564000.00|507600.00|7", await CardAsync(h)); // cost − residual 56,400.00, exactly
        Assert.Equal(DepreciationErrors.Nothing, nothing.Code);
    }

    [Trait("AcceptanceAf1", "AF-08")]
    [Fact]
    public async Task AF08_after_12_months_the_forklift_is_sold_for_450000_with_a_loss_of_12480_approved_by_the_Controller()
    {
        var x = await InServiceAsync(postgres, 60, settle: true);
        await using var h = x.H;
        var first = MonthOf(Today(h)).AddMonths(1);
        await DepreciateAsync(x, first);

        // Not before it is depreciated up to the month before the sale.
        MoveTo(x.Clock, first.AddMonths(2).AddDays(3));
        var early = await Assert.ThrowsAsync<DomainException>(async () =>
        {
            var contador = await h.SessionWithRolesAsync("CONTADOR");
            await h.RunAsync(new PrepareAssetDisposal(h.CompanyId, contador, "early", x.AssetId, "SALE", Today(h), 450000m, "Venta del montacargas"), new PrepareAssetDisposalHandler());
        });
        for (var m = 1; m < 12; m++)
        {
            await DepreciateAsync(x, first.AddMonths(m));
        }

        var depreciated = await CardAsync(h);
        var saleDay = Today(h);
        var preparer = await h.SessionWithRolesAsync("CONTADOR");
        var noPrice = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareAssetDisposal(h.CompanyId, preparer, "np", x.AssetId, "SALE", saleDay, null, "Venta del montacargas"), new PrepareAssetDisposalHandler()));
        var prepared = await h.RunAsync(
            new PrepareAssetDisposal(h.CompanyId, preparer, "sale", x.AssetId, "SALE", saleDay, 450000m, "Venta del montacargas a Ferretería del Este"), new PrepareAssetDisposalHandler());
        var own = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveAssetDisposal(h.CompanyId, preparer, "own", prepared.ResultRef, 1), new ApproveAssetDisposalHandler()));
        var controller = await h.SessionWithRolesAsync("CONTROLLER");
        var approved = JsonDocument.Parse((await h.RunAsync(new ApproveAssetDisposal(h.CompanyId, controller, "sale-a", prepared.ResultRef, 1), new ApproveAssetDisposalHandler())).ResultPayload)
            .RootElement;

        // Disposed: no more depreciation and the last month is no longer undone.
        var nothing = await Assert.ThrowsAsync<DomainException>(() => DepreciateAsync(x, first.AddMonths(12)));
        var lastRun = await h.ScalarAsync<Guid>("SELECT run_id FROM fa.depreciation_run WHERE status = 'POSTED' ORDER BY month DESC LIMIT 1");
        var undo = await Assert.ThrowsAsync<DomainException>(async () =>
        {
            var contador = await h.SessionWithRolesAsync("CONTADOR");
            await h.RunAsync(new UndoDepreciation(h.CompanyId, contador, "undo", lastRun, 1, "Corrección del último mes"), new UndoDepreciationHandler());
        });
        var reader = await h.SessionWithRolesAsync("CONTADOR");
        var disposals = JsonDocument.Parse(await h.QueryAsync(new ListAssetDisposals(h.CompanyId, reader), new ListAssetDisposalsHandler())).RootElement.GetProperty("items");

        Assert.Equal(DisposalErrors.DepreciationPending, early.Code);
        Assert.Equal("IN_SERVICE|564000.00|101520.00|12", depreciated);
        Assert.Equal(DisposalErrors.Invalid, noPrice.Code);
        Assert.Equal(AuthorizationErrors.NotAuthorized, own.Code); // the Contador prepares, the Controller approves
        Assert.Equal("462480.00|0.00|12480.00", $"{approved.GetProperty("bookValue").GetString()}|{approved.GetProperty("gain").GetString()}|{approved.GetProperty("loss").GetString()}");
        Assert.Equal("13800:450000.00,15300:0.00,15390:0.00,64100:101520.00,71500:0.00,81500:12480.00", await BooksAsync(h, "13800", "15300", "15390", "64100", "71500", "81500"));
        Assert.Equal("DISPOSED|564000.00|101520.00|12", await CardAsync(h));
        Assert.Equal(DepreciationErrors.Nothing, nothing.Code);
        Assert.Equal(FixedAssetErrors.AssetDisposed, undo.Code);
        Assert.Equal("SALE|POSTED|450000.00", $"{disposals[0].GetProperty("kind").GetString()}|{disposals[0].GetProperty("status").GetString()}|{disposals[0].GetProperty("price").GetString()}");
    }

    [Fact]
    public async Task A_card_is_scrapped_in_its_first_month_at_its_cost_and_a_draft_disposal_can_be_cancelled()
    {
        var x = await InServiceAsync(postgres, 60, settle: false);
        await using var h = x.H;
        var contador = await h.SessionWithRolesAsync("CONTADOR");

        var priced = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareAssetDisposal(h.CompanyId, contador, "p", x.AssetId, "SCRAP", Today(h), 10m, "Se dañó en la descarga"), new PrepareAssetDisposalHandler()));
        var future = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareAssetDisposal(h.CompanyId, contador, "f", x.AssetId, "SCRAP", Today(h).AddDays(1), null, "Se dañó en la descarga"), new PrepareAssetDisposalHandler()));
        var draft = await h.RunAsync(new PrepareAssetDisposal(h.CompanyId, contador, "d", x.AssetId, "SCRAP", Today(h), null, "Se dañó en la descarga"), new PrepareAssetDisposalHandler());
        var second = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareAssetDisposal(h.CompanyId, contador, "d2", x.AssetId, "SCRAP", Today(h), null, "Se dañó en la descarga"), new PrepareAssetDisposalHandler()));
        await h.RunAsync(new CancelAssetDisposal(h.CompanyId, contador, "c", draft.ResultRef, 1), new CancelAssetDisposalHandler());
        var scrap = await h.RunAsync(new PrepareAssetDisposal(h.CompanyId, contador, "d3", x.AssetId, "SCRAP", Today(h), null, "Se dañó en la descarga, sin arreglo"), new PrepareAssetDisposalHandler());
        await h.RunAsync(new ApproveAssetDisposal(h.CompanyId, x.S.F.W.Controller, "a", scrap.ResultRef, 1), new ApproveAssetDisposalHandler());

        Assert.Equal(DisposalErrors.Invalid, priced.Code);
        Assert.Equal(DisposalErrors.Invalid, future.Code);
        Assert.Equal(FixedAssetErrors.InvalidState, second.Code);
        Assert.Equal("15300:0.00,81500:480000.00", await BooksAsync(h, "15300", "81500"));
        Assert.Equal("DISPOSED|480000.00|0.00|0", await CardAsync(h));
    }
}
