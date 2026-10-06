using System.Text.Json;
using Rochell.Finance.Ledger;
using Rochell.FixedAssets;
using Rochell.FixedAssets.Cards;
using Rochell.FixedAssets.Classes;
using Rochell.FixedAssets.Queries;
using Rochell.Platform.Commands;
using Rochell.Procurement.Imports;
using Rochell.Procurement.SupplierInvoices;
using Rochell.TestInfrastructure;
using Xunit;
using static Rochell.Procurement.Tests.ExpenseInvoiceTests;
using static Rochell.Procurement.Tests.ForeignInvoiceTests;
using static Rochell.Procurement.Tests.ImportSettlementTests;

namespace Rochell.Procurement.Tests;

/// <summary>
/// AF1-02 (AF-01…04, AF-10, E-AF1-02-1…10): asset classes, the card born from the posted invoice, the settlement's cost on it, service,
/// transfer and the cards of invoices posted before AF-1. The baseline's forklift: USD 8,000.00 at 60.00 (480,000.00) plus 84,000.00 of
/// the settlement = 564,000.00; class «Montacargas y equipos» of 60 months and 10 % residual (56,400.00), so 507,600.00 to depreciate.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class FixedAssetCardTests(PostgresFixture postgres)
{
    private sealed record Accounts(Guid Accumulated, Guid Depreciation);

    private static async Task<Accounts> AccountsAsync(TestHarness h, Foreign f)
        => new(
            (await h.RunAsync(new CreateAccount(h.CompanyId, f.W.Controller, "acc-dep", "15390", "Depreciación acumulada de montacargas", "ASSET", false), new CreateAccountHandler())).ResultRef,
            (await h.RunAsync(new CreateAccount(h.CompanyId, f.W.Controller, "acc-gdep", "64100", "Gasto de depreciación", "EXPENSE", false), new CreateAccountHandler())).ResultRef);

    private static async Task<Guid> ApprovedClassAsync(TestHarness h, Foreign f, Accounts a, int life = 60, decimal residual = 10m, string key = "k")
    {
        var contador = await h.SessionWithRolesAsync("CONTADOR");
        var prepared = await h.RunAsync(new PrepareAssetClass(h.CompanyId, contador, key, f.Forklift, life, residual, a.Accumulated, a.Depreciation), new PrepareAssetClassHandler());
        await h.RunAsync(new ApproveAssetClass(h.CompanyId, f.W.Controller, key + "-a", prepared.ResultRef, 1), new ApproveAssetClassHandler());
        return prepared.ResultRef;
    }

    private static Task<string?> CardsAsync(TestHarness h)
        => h.ScalarAsync<string>("SELECT string_agg(asset_no || '|' || description || '|' || status || '|' || cost::numeric(19,2), ',' ORDER BY asset_no) FROM fa.asset");

    private static Task<Guid> CardAsync(TestHarness h)
        => h.ScalarAsync<Guid>("SELECT asset_id FROM fa.asset WHERE status <> 'CANCELLED'");

    private static async Task<Guid> SettledAsync(TestHarness h, Shipment s, string key = "li")
    {
        ImportSettlementDocumentInput[] documents =
            [new("SUPPLIER_INVOICE", s.Goods), new("EXPENSE_INVOICE", s.Freight), new("EXPENSE_INVOICE", s.Agent), new("CUSTOMS_DECLARATION", s.Dua)];
        var prepared = await h.RunAsync(new PrepareImportSettlement(h.CompanyId, s.F.W.Clerk, key, s.F.W.Plant, Today(h), null, documents), new PrepareImportSettlementHandler());
        await h.RunAsync(new ApproveImportSettlement(h.CompanyId, s.F.W.Controller, key + "-a", prepared.ResultRef, 1), new ApproveImportSettlementHandler());
        return prepared.ResultRef;
    }

    [Trait("AcceptanceAf1", "AF-01")]
    [Fact]
    public async Task AF01_a_class_is_prepared_by_the_Contador_approved_by_the_Controller_and_a_new_version_replaces_it()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var f = await ForeignAsync(h);
        var a = await AccountsAsync(h, f);
        var contador = await h.SessionWithRolesAsync("CONTADOR");

        var notAsset = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareAssetClass(h.CompanyId, contador, "x1", f.Parts, 60, 10m, a.Accumulated, a.Depreciation), new PrepareAssetClassHandler()));
        var noLife = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareAssetClass(h.CompanyId, contador, "x2", f.Forklift, 0, 10m, a.Accumulated, a.Depreciation), new PrepareAssetClassHandler()));
        var wholeResidual = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareAssetClass(h.CompanyId, contador, "x3", f.Forklift, 60, 100m, a.Accumulated, a.Depreciation), new PrepareAssetClassHandler()));
        var swapped = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareAssetClass(h.CompanyId, contador, "x4", f.Forklift, 60, 10m, a.Depreciation, a.Accumulated), new PrepareAssetClassHandler()));

        var first = await h.RunAsync(new PrepareAssetClass(h.CompanyId, contador, "k1", f.Forklift, 60, 10m, a.Accumulated, a.Depreciation), new PrepareAssetClassHandler());
        var second = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareAssetClass(h.CompanyId, contador, "k1b", f.Forklift, 48, 5m, a.Accumulated, a.Depreciation), new PrepareAssetClassHandler()));
        var own = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveAssetClass(h.CompanyId, contador, "k1-own", first.ResultRef, 1), new ApproveAssetClassHandler()));
        await h.RunAsync(new ApproveAssetClass(h.CompanyId, f.W.Controller, "k1-a", first.ResultRef, 1), new ApproveAssetClassHandler());

        // A new version waits as a draft while the first stays in force, and replaces it once approved.
        var next = await h.RunAsync(new PrepareAssetClass(h.CompanyId, contador, "k2", f.Forklift, 48, 5m, a.Accumulated, a.Depreciation), new PrepareAssetClassHandler());
        var whileDraft = await h.ScalarAsync<string>("SELECT string_agg(class_version || ':' || status, ',' ORDER BY class_version) FROM fa.asset_class");
        await h.RunAsync(new ApproveAssetClass(h.CompanyId, f.W.Controller, "k2-a", next.ResultRef, 1), new ApproveAssetClassHandler());
        var list = JsonDocument.Parse(await h.QueryAsync(new ListAssetClasses(h.CompanyId, contador), new ListAssetClassesHandler())).RootElement.GetProperty("items");

        Assert.Equal(FixedAssetErrors.ClassInvalid, notAsset.Code); // «Repuestos» is an expense category
        Assert.Equal(FixedAssetErrors.ClassInvalid, noLife.Code);
        Assert.Equal(FixedAssetErrors.ClassInvalid, wholeResidual.Code);
        Assert.Equal(FixedAssetErrors.ClassInvalid, swapped.Code); // accumulated is an asset account, the depreciation an expense
        Assert.Equal(FixedAssetErrors.InvalidState, second.Code); // one draft per category
        Assert.Equal(AuthorizationErrors.NotAuthorized, own.Code); // the Contador prepares, never approves (SoD)
        Assert.Equal("1:ACTIVE,2:DRAFT", whileDraft);
        Assert.Equal("1:60:10.00:SUPERSEDED,2:48:5.00:ACTIVE", await h.ScalarAsync<string>(
            "SELECT string_agg(class_version || ':' || useful_life_months || ':' || residual_pct || ':' || status, ',' ORDER BY class_version) FROM fa.asset_class"));
        Assert.Equal(
            "MONTACARGAS:2:48:5.00:ACTIVE:15390:64100,MONTACARGAS:1:60:10.00:SUPERSEDED:15390:64100",
            string.Join(',', list.EnumerateArray().Select(c =>
                $"{c.GetProperty("categoryCode").GetString()}:{c.GetProperty("classVersion").GetInt32()}:{c.GetProperty("usefulLifeMonths").GetInt32()}:{c.GetProperty("residualPct").GetString()}:"
                + $"{c.GetProperty("status").GetString()}:{c.GetProperty("accumulatedAccountCode").GetString()}:{c.GetProperty("expenseAccountCode").GetString()}")));
    }

    [Trait("AcceptanceAf1", "AF-02")]
    [Trait("AcceptanceAf1", "AF-03")]
    [Trait("AcceptanceAf1", "AF-04")]
    [Fact]
    public async Task AF02_the_forklift_is_born_with_its_invoice_takes_the_settlements_cost_and_is_put_into_service_with_its_class()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ShipmentAsync(h);
        var contador = await h.SessionWithRolesAsync("CONTADOR");

        // AF-02: the forklift line has a card awaiting service at its peso cost; the parts (an expense category) do not.
        var born = await CardsAsync(h);
        var assetId = await CardAsync(h);

        // AF-03: the settlement adds its 84,000.00; its reversal takes them back; a new settlement adds them again.
        var settlement = await SettledAsync(h, s);
        var settled = await CardsAsync(h);
        await h.RunAsync(new ReverseImportSettlement(h.CompanyId, s.F.W.Controller, "li-x", settlement, 2, "Falta el seguro"), new ReverseImportSettlementHandler());
        var reversed = await CardsAsync(h);
        await SettledAsync(h, s, "li2");

        // AF-04: not into service before its class is approved, nor before it was bought.
        var noClass = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PutFixedAssetInService(h.CompanyId, contador, "svc0", assetId, 4, Today(h), s.F.W.Plant, "Encargado de almacén"), new PutFixedAssetInServiceHandler()));
        var a = await AccountsAsync(h, s.F);
        var classId = await ApprovedClassAsync(h, s.F, a);
        var early = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PutFixedAssetInService(h.CompanyId, contador, "svc1", assetId, 4, Today(h).AddDays(-1), s.F.W.Plant, "Encargado de almacén"), new PutFixedAssetInServiceHandler()));
        var noOne = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PutFixedAssetInService(h.CompanyId, contador, "svc2", assetId, 4, Today(h), s.F.W.Plant, " "), new PutFixedAssetInServiceHandler()));
        await h.RunAsync(new PutFixedAssetInService(h.CompanyId, contador, "svc", assetId, 4, Today(h), s.F.W.Plant, "Encargado de almacén"), new PutFixedAssetInServiceHandler());
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetFixedAsset(h.CompanyId, contador, assetId), new GetFixedAssetHandler())).RootElement;

        Assert.Matches("^AF-[0-9]{4}-000001\\|Montacargas usado Toyota 8FGU25\\|AWAITING_SERVICE\\|480000.00$", born);
        Assert.EndsWith("|AWAITING_SERVICE|564000.00", settled);
        Assert.EndsWith("|AWAITING_SERVICE|480000.00", reversed);
        Assert.Equal(FixedAssetErrors.ClassNotApproved, noClass.Code);
        Assert.Equal(FixedAssetErrors.AssetInvalid, early.Code);
        Assert.Equal(FixedAssetErrors.AssetInvalid, noOne.Code);
        Assert.Equal(
            $"IN_SERVICE|{classId}|60|10.00|564000.00|56400.00|507600.00|60|INV-2026-0147|Forklift Parts Inc.|Encargado de almacén",
            string.Join(
                '|',
                detail.GetProperty("asset").GetProperty("status").GetString(),
                detail.GetProperty("assetClassId").GetString(),
                detail.GetProperty("asset").GetProperty("usefulLifeMonths").GetInt32(),
                detail.GetProperty("residualPct").GetString(),
                detail.GetProperty("asset").GetProperty("cost").GetString(),
                detail.GetProperty("residualValue").GetString(),
                detail.GetProperty("depreciable").GetString(),
                detail.GetProperty("monthsRemaining").GetInt32(),
                detail.GetProperty("invoiceNumber").GetString(),
                detail.GetProperty("supplierName").GetString(),
                detail.GetProperty("asset").GetProperty("responsible").GetString()));
        Assert.Equal(
            "ACQUISITION:480000.00,COST_ADDED:84000.00,COST_REMOVED:84000.00,COST_ADDED:84000.00,IN_SERVICE:-",
            string.Join(',', detail.GetProperty("movements").EnumerateArray().Select(m =>
                $"{m.GetProperty("kind").GetString()}:{(m.GetProperty("amount").ValueKind == JsonValueKind.Null ? "-" : m.GetProperty("amount").GetString())}")));
        Assert.Equal("-:AWAITING_SERVICE,AWAITING_SERVICE:IN_SERVICE", string.Join(',', detail.GetProperty("history").EnumerateArray().Select(c =>
            $"{c.GetProperty("from").GetString() ?? "-"}:{c.GetProperty("to").GetString()}")));
    }

    [Trait("AcceptanceAf1", "AF-10")]
    [Fact]
    public async Task AF10_a_card_moves_to_another_plant_without_a_journal_and_its_invoice_reversal_cancels_it()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ShipmentAsync(h);
        var contador = await h.SessionWithRolesAsync("CONTADOR");
        var assetId = await CardAsync(h);
        await ApprovedClassAsync(h, s.F, await AccountsAsync(h, s.F));
        await h.RunAsync(new PutFixedAssetInService(h.CompanyId, contador, "svc", assetId, 1, Today(h), s.F.W.Plant, "Encargado"), new PutFixedAssetInServiceHandler());
        var other = await h.CreatePlantAsync();
        var journals = await h.CountAsync("fin.gl_journal");

        var same = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new TransferFixedAsset(h.CompanyId, contador, "t0", assetId, 2, s.F.W.Plant, Today(h)), new TransferFixedAssetHandler()));
        var future = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new TransferFixedAsset(h.CompanyId, contador, "t1", assetId, 2, other, Today(h).AddDays(1)), new TransferFixedAssetHandler()));
        await h.RunAsync(new TransferFixedAsset(h.CompanyId, contador, "t", assetId, 2, other, Today(h)), new TransferFixedAssetHandler());
        await h.RunAsync(new UpdateFixedAsset(h.CompanyId, contador, "u", assetId, 3, "Montacargas Toyota 8FGU25 (serie 12345)", "Jefe de patio"), new UpdateFixedAssetHandler());
        var moved = await h.ScalarAsync<string>("SELECT (plant_id = @p)::text || '|' || description || '|' || responsible FROM fa.asset WHERE asset_id = @a", ("p", other), ("a", assetId));
        var journalsAfter = await h.CountAsync("fin.gl_journal");

        // E-AF1-02-4: in service but without depreciation, the card is cancelled with its invoice's reversal.
        var siVersion = await h.ScalarAsync<long>("SELECT version FROM pur.supplier_invoice WHERE si_id = @s", ("s", s.Goods));
        await h.RunAsync(new ReverseSupplierInvoice(h.CompanyId, s.F.W.Controller, "rev", s.Goods, siVersion, "Factura duplicada"), new ReverseSupplierInvoiceHandler());
        var list = JsonDocument.Parse(await h.QueryAsync(new ListFixedAssets(h.CompanyId, contador, Status: FixedAssetCards.Cancelled), new ListFixedAssetsHandler())).RootElement;
        var afterCancel = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new UpdateFixedAsset(h.CompanyId, contador, "u2", assetId, 5, "Otro", null), new UpdateFixedAssetHandler()));

        Assert.Equal(FixedAssetErrors.AssetInvalid, same.Code);
        Assert.Equal(FixedAssetErrors.AssetInvalid, future.Code);
        Assert.Equal("true|Montacargas Toyota 8FGU25 (serie 12345)|Jefe de patio", moved);
        Assert.Equal(journals, journalsAfter); // a transfer posts nothing (E-AF-9)
        Assert.Equal("CANCELLED:1", $"{list.GetProperty("items")[0].GetProperty("status").GetString()}:{list.GetProperty("items").GetArrayLength()}");
        Assert.Equal(FixedAssetErrors.InvalidState, afterCancel.Code);
    }

    [Fact]
    public async Task A_fixed_asset_invoice_is_not_settled_as_the_cost_of_other_goods()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ShipmentAsync(h);
        var second = await PostedAsync(h, s.F, "fa2", s.F.Supplier, "INV-2026-0200", Usd(s.F.Forklift, "Montacargas eléctrico", 1m, 1000m));
        ImportSettlementDocumentInput[] documents = [new("SUPPLIER_INVOICE", s.Goods), new("EXPENSE_INVOICE", second), new("CUSTOMS_DECLARATION", s.Dua)];
        var prepared = await h.RunAsync(new PrepareImportSettlement(h.CompanyId, s.F.W.Clerk, "li", s.F.W.Plant, Today(h), null, documents), new PrepareImportSettlementHandler());

        var refused = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new ApproveImportSettlement(h.CompanyId, s.F.W.Controller, "li-a", prepared.ResultRef, 1), new ApproveImportSettlementHandler()));

        Assert.Equal(FixedAssetErrors.AssetInSettlement, refused.Code);
        Assert.Equal("480000.00,60000.00", await h.ScalarAsync<string>("SELECT string_agg(cost::numeric(19,2)::text, ',' ORDER BY asset_no) FROM fa.asset"));
    }

    [Fact]
    public async Task Invoices_posted_before_AF1_get_their_cards_once_with_their_settlements_cost()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ShipmentAsync(h);
        await SettledAsync(h, s);
        var contador = await h.SessionWithRolesAsync("CONTADOR");

        // Before AF-1 the invoice had no card: the one its posting created is taken away, with its history row.
        await h.AdminRequireAsync(
            """
            INSERT INTO core.state_history (state_history_id, company_id, aggregate_type, aggregate_id, status_kind, from_state, to_state, command, event_id)
            SELECT gen_random_uuid(), company_id, 'FixedAsset', asset_id, 'DOCUMENT', status, 'CANCELLED', 'test', (SELECT event_id FROM core.domain_event ORDER BY recorded_at LIMIT 1)
            FROM fa.asset;
            UPDATE fa.asset SET status = 'CANCELLED', version = version + 1;
            """);

        var first = JsonDocument.Parse((await h.RunAsync(new CreateCardsForPostedInvoices(h.CompanyId, contador, "bf"), new CreateCardsForPostedInvoicesHandler())).ResultPayload).RootElement;
        var again = JsonDocument.Parse((await h.RunAsync(new CreateCardsForPostedInvoices(h.CompanyId, contador, "bf2"), new CreateCardsForPostedInvoicesHandler())).ResultPayload).RootElement;

        Assert.Equal(1, first.GetProperty("created").GetInt32());
        Assert.Equal(0, again.GetProperty("created").GetInt32());
        Assert.Matches("-000001\\|Montacargas usado Toyota 8FGU25\\|CANCELLED\\|564000.00,AF-[0-9]{4}-000002\\|Montacargas usado Toyota 8FGU25\\|AWAITING_SERVICE\\|564000.00$", await CardsAsync(h));
        Assert.Equal("ACQUISITION:480000.00,COST_ADDED:84000.00", await h.ScalarAsync<string>(
            "SELECT string_agg(m.kind || ':' || m.amount::numeric(19,2), ',' ORDER BY m.kind) FROM fa.asset_movement m JOIN fa.asset x USING (asset_id) WHERE x.status = 'AWAITING_SERVICE'"));
    }
}
