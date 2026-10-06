using System.Text.Json;
using Rochell.Finance.Ledger;
using Rochell.Platform.Commands;
using Rochell.Procurement.Expenses;
using Rochell.Procurement.Imports;
using Rochell.Procurement.SupplierInvoices;
using Rochell.TestInfrastructure;
using Xunit;
using static Rochell.Procurement.Tests.ExpenseInvoiceTests;
using static Rochell.Procurement.Tests.ForeignInvoiceTests;

namespace Rochell.Procurement.Tests;

/// <summary>
/// USD1-04 (USD-05, E-USD1-04-1…9): the DUA (P-39) and the import settlement (P-40). The baseline's shipment: a forklift of USD 8,000.00
/// and spare parts of USD 2,000.00 at 60.00 (480,000.00 + 120,000.00); freight USD 1,000.00 (60,000.00), duties 30,000.00 on the DUA
/// and the customs agent 15,000.00 + ITBIS: 105,000.00 spread by value — 84,000.00 to the forklift, 21,000.00 to the parts.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ImportSettlementTests(PostgresFixture postgres)
{
    private const string P39 = "0192f001-0000-7000-8000-000000000031";
    private const string P40 = "0192f001-0000-7000-8000-000000000032";

    private sealed record Shipment(Foreign F, Guid Goods, Guid Freight, Guid Agent, Guid Dua);

    private static async Task<Guid> PostedAsync(TestHarness h, Foreign f, string key, Guid supplier, string number, params ExpenseLineInput[] lines)
    {
        var si = (await h.RunAsync(
            new RegisterExpenseInvoice(h.CompanyId, f.W.Clerk, key, supplier, number, Today(h), Today(h).AddDays(30), f.W.Plant, lines), new RegisterExpenseInvoiceHandler())).ResultRef;
        var status = JsonDocument.Parse((await h.RunAsync(new MatchSupplierInvoice(h.CompanyId, f.W.Clerk, key + "-m", si, 1), new MatchSupplierInvoiceHandler())).ResultPayload)
            .RootElement.GetProperty("status").GetString();
        var version = 2L;
        if (status == SupplierInvoiceStatus.MatchException)
        {
            await h.RunAsync(new ApproveMatchException(h.CompanyId, f.W.Controller, key + "-a", si, 2, "Importación"), new ApproveMatchExceptionHandler());
            version = 3;
        }

        await h.RunAsync(new PostSupplierInvoice(h.CompanyId, f.W.Clerk, key + "-p", si, version), new PostSupplierInvoiceHandler());
        return si;
    }

    private static async Task<Guid> CategoryAsync(TestHarness h, Foreign f, string code, string account, string name, string lineClass)
    {
        var accountId = (await h.RunAsync(new CreateAccount(h.CompanyId, f.W.Controller, "acc-" + code, account, name, "EXPENSE", false), new CreateAccountHandler())).ResultRef;
        var contador = await h.SessionWithRolesAsync("CONTADOR");
        var category = (await h.RunAsync(new PrepareExpenseCategory(h.CompanyId, contador, "cat-" + code, code, name, accountId, "09", lineClass), new PrepareExpenseCategoryHandler())).ResultRef;
        await h.RunAsync(new ApproveExpenseCategories(h.CompanyId, f.W.Controller, "cat-a-" + code, [category]), new ApproveExpenseCategoriesHandler());
        return category;
    }

    /// <summary>The goods invoice, the freight (a second foreign supplier), the agent (local, with ITBIS) and the DUA, all posted.</summary>
    private static async Task<Shipment> ShipmentAsync(TestHarness h)
    {
        var f = await ForeignAsync(h);
        await h.CreateActiveMapAsync("IMPORT_CLEARING", await h.CreateAccountAsync("13900", "Importaciones por liquidar", isControl: true));
        await h.AdminRequireAsync($"UPDATE fin.posting_rule_version SET status = 'ACTIVE', approved_by = '{h.UserId}' WHERE posting_rule_id IN ('{P39}', '{P40}') AND version = 1");
        var freightCategory = await CategoryAsync(h, f, "FLETE_INTERNACIONAL", "63810", "Flete internacional", "SERVICE");
        var agentCategory = await CategoryAsync(h, f, "GESTION_ADUANAL", "63820", "Gestión aduanal", "SERVICE");
        var carrier = (await h.RunAsync(
            new Rochell.MasterData.Suppliers.CreateForeignSupplier(h.CompanyId, f.W.S.Purchasing.Buyer, "carrier", "Ocean Freight Ltd.", "PA"), new Rochell.MasterData.Suppliers.CreateForeignSupplierHandler()))
            .ResultRef;
        await h.RunAsync(new Rochell.MasterData.Suppliers.ActivateSupplier(h.CompanyId, f.W.Controller, "carrier-a", carrier, 1), new Rochell.MasterData.Suppliers.ActivateSupplierHandler());

        var goods = await PostedAsync(
            h, f, "g", f.Supplier, "INV-2026-0147", Usd(f.Forklift, "Montacargas usado Toyota 8FGU25", 1m, 8000m), Usd(f.Parts, "Repuestos de montacargas", 4m, 500m));

        var freight = await PostedAsync(h, f, "f", carrier, "BL-778", Usd(freightCategory, "Flete marítimo Miami–Caucedo", 1m, 1000m));
        var agent = await PostedAsync(h, f, "ag", f.W.S.Purchasing.SupplierId, "B0100000950", new ExpenseLineInput("Gestión aduanal del embarque", agentCategory, f.W.Types["ITBIS_18"], 1m, 15000m));
        var dua = (await h.RunAsync(
            new RegisterCustomsDeclaration(h.CompanyId, f.W.Clerk, "dua", f.W.S.Purchasing.SupplierId, f.W.Plant, "10020-IM-2610-000123", Today(h), Today(h).AddDays(5), 660000m, 30000m, 124200m, 0m),
            new RegisterCustomsDeclarationHandler())).ResultRef;
        return new Shipment(f, goods, freight, agent, dua);
    }

    private static Task<string?> AccountsAsync(TestHarness h, params string[] codes)
        => h.ScalarAsync<string>(
            $"""
            SELECT string_agg(a.code || ':' || coalesce((SELECT sum(e.debit - e.credit) FROM fin.gl_entry e WHERE e.account_id = a.account_id), 0)::numeric(19,2), ',' ORDER BY a.code)
            FROM fin.account a WHERE a.code IN ({string.Join(',', codes.Select(c => $"'{c}'"))})
            """);

    [Trait("AcceptanceUsd1", "USD-05")]
    [Fact]
    public async Task USD05_the_shipments_105000_of_costs_go_84000_to_the_forklift_and_21000_to_the_parts()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ShipmentAsync(h);
        ImportSettlementDocumentInput[] documents =
        [
            new("SUPPLIER_INVOICE", s.Goods), new("EXPENSE_INVOICE", s.Freight), new("EXPENSE_INVOICE", s.Agent), new("CUSTOMS_DECLARATION", s.Dua),
        ];

        // The DUA: duties to «Importaciones por liquidar», its ITBIS recoverable, 154,200.00 owed to the DGA and visible to Treasury.
        var duaBooks = await AccountsAsync(h, "13900");
        var source = await h.ScalarAsync<string>(
            "SELECT doc_number || '|' || accounting_status || '|' || (SELECT original_amount::numeric(19,2) FROM fin.ap_document d WHERE d.ap_doc_id = s.ap_doc_id) FROM fin.ap_source s WHERE doc_type = 'CUSTOMS_DECLARATION'");

        var prepared = await h.RunAsync(new PrepareImportSettlement(h.CompanyId, s.F.W.Clerk, "li", s.F.W.Plant, Today(h), "Embarque montacargas", documents), new PrepareImportSettlementHandler());
        var own = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(new ApproveImportSettlement(h.CompanyId, s.F.W.Clerk, "li-own", prepared.ResultRef, 1), new ApproveImportSettlementHandler()));
        var duaReverse = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new ReverseCustomsDeclaration(h.CompanyId, s.F.W.Controller, "dua-x", s.Dua, 1, "Error"), new ReverseCustomsDeclarationHandler()));
        var approved = JsonDocument.Parse((await h.RunAsync(new ApproveImportSettlement(h.CompanyId, s.F.W.Controller, "li-a", prepared.ResultRef, 1), new ApproveImportSettlementHandler()))
            .ResultPayload).RootElement;
        var detail = JsonDocument.Parse(await h.QueryAsync(new GetImportSettlement(h.CompanyId, s.F.W.Clerk, prepared.ResultRef), new GetImportSettlementHandler())).RootElement;

        Assert.Equal("13900:30000.00", duaBooks);
        Assert.Equal("DUA 10020-IM-2610-000123|POSTED|154200.00", source);
        Assert.Matches("^LI-[0-9]{4}-000001$", JsonDocument.Parse(prepared.ResultPayload).RootElement.GetProperty("settlementNo").GetString());
        Assert.Equal(AuthorizationErrors.NotAuthorized, own.Code); // Cuentas por pagar prepares, never approves (SoD)
        Assert.Equal(ImportErrors.InSettlement, duaReverse.Code);
        Assert.Equal("POSTED|105000.00", $"{approved.GetProperty("status").GetString()}|{approved.GetProperty("totalCost").GetString()}");
        Assert.Equal(
            "Montacargas usado Toyota 8FGU25:480000.0000+84000.0000=564000.0000,Repuestos de montacargas:120000.0000+21000.0000=141000.0000",
            string.Join(',', detail.GetProperty("allocation").EnumerateArray().Select(a =>
                $"{a.GetProperty("description").GetString()}:{a.GetProperty("baseValue").GetString()}+{a.GetProperty("addedCost").GetString()}={a.GetProperty("totalCost").GetString()}")));
        Assert.Equal("13900:0.00,15300:564000.00,63810:0.00,63820:0.00,66150:141000.00", await AccountsAsync(h, "13900", "15300", "63810", "63820", "66150"));

        // Reversed, the costs go back to their accounts and the documents are free for a new settlement.
        await h.RunAsync(new ReverseImportSettlement(h.CompanyId, s.F.W.Controller, "li-x", prepared.ResultRef, 2, "Falta el seguro"), new ReverseImportSettlementHandler());
        Assert.Equal("13900:30000.00,15300:480000.00,63810:60000.00,63820:15000.00,66150:120000.00", await AccountsAsync(h, "13900", "15300", "63810", "63820", "66150"));
        var again = await h.RunAsync(new PrepareImportSettlement(h.CompanyId, s.F.W.Clerk, "li2", s.F.W.Plant, Today(h), null, documents), new PrepareImportSettlementHandler());
        Assert.Matches("-000002$", JsonDocument.Parse(again.ResultPayload).RootElement.GetProperty("settlementNo").GetString());
    }

    [Fact]
    public async Task A_settlement_needs_goods_and_cost_and_a_document_lives_in_one_settlement_at_a_time()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ShipmentAsync(h);
        var clerk = s.F.W.Clerk;

        var noGoods = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareImportSettlement(h.CompanyId, clerk, "a", s.F.W.Plant, Today(h), null, [new("CUSTOMS_DECLARATION", s.Dua)]), new PrepareImportSettlementHandler()));
        var localGoods = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareImportSettlement(h.CompanyId, clerk, "b", s.F.W.Plant, Today(h), null, [new("SUPPLIER_INVOICE", s.Agent), new("CUSTOMS_DECLARATION", s.Dua)]),
            new PrepareImportSettlementHandler()));
        var first = await h.RunAsync(
            new PrepareImportSettlement(h.CompanyId, clerk, "c", s.F.W.Plant, Today(h), null, [new("SUPPLIER_INVOICE", s.Goods), new("CUSTOMS_DECLARATION", s.Dua)]), new PrepareImportSettlementHandler());
        var twice = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new PrepareImportSettlement(h.CompanyId, clerk, "d", s.F.W.Plant, Today(h), null, [new("SUPPLIER_INVOICE", s.Goods), new("EXPENSE_INVOICE", s.Freight)]),
            new PrepareImportSettlementHandler()));
        var reverseGoods = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new ReverseSupplierInvoice(h.CompanyId, s.F.W.Controller, "e", s.Goods, 4, "Error"), new ReverseSupplierInvoiceHandler()));

        // The draft takes the freight too; cancelled, its documents are free again.
        await h.RunAsync(
            new UpdateImportSettlementDraft(h.CompanyId, clerk, "f", first.ResultRef, 1, Today(h), "Con flete",
                [new("SUPPLIER_INVOICE", s.Goods), new("EXPENSE_INVOICE", s.Freight), new("CUSTOMS_DECLARATION", s.Dua)]),
            new UpdateImportSettlementDraftHandler());
        var drafted = await h.ScalarAsync<decimal>("SELECT sum(added_cost) FROM pur.import_settlement_allocation WHERE settlement_id = @s", ("s", first.ResultRef));
        await h.RunAsync(new CancelImportSettlement(h.CompanyId, clerk, "g", first.ResultRef, 2), new CancelImportSettlementHandler());
        await h.RunAsync(
            new PrepareImportSettlement(h.CompanyId, clerk, "h", s.F.W.Plant, Today(h), null, [new("SUPPLIER_INVOICE", s.Goods), new("EXPENSE_INVOICE", s.Freight)]),
            new PrepareImportSettlementHandler());

        Assert.Equal(
            (ImportErrors.SettlementInvalid, ImportErrors.SettlementDocumentInvalid, ImportErrors.InSettlement, ImportErrors.InSettlement),
            (noGoods.Code, localGoods.Code, twice.Code, reverseGoods.Code));
        Assert.Equal(90000m, drafted); // 30,000.00 of duties + 60,000.00 of freight
    }

    [Fact]
    public async Task A_DUA_is_owed_to_a_local_supplier_once_per_number_and_reversed_while_unpaid()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ShipmentAsync(h);

        var duplicate = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new RegisterCustomsDeclaration(h.CompanyId, s.F.W.Clerk, "d2", s.F.W.S.Purchasing.SupplierId, s.F.W.Plant, "10020-im-2610-000123", Today(h), Today(h), 1m, 1m, 0m, 0m),
            new RegisterCustomsDeclarationHandler()));
        var foreignParty = await Assert.ThrowsAsync<DomainException>(() => h.RunAsync(
            new RegisterCustomsDeclaration(h.CompanyId, s.F.W.Clerk, "d3", s.F.Supplier, s.F.W.Plant, "X-1", Today(h), Today(h), 1m, 1m, 0m, 0m), new RegisterCustomsDeclarationHandler()));
        var second = await h.RunAsync(
            new RegisterCustomsDeclaration(h.CompanyId, s.F.W.Clerk, "d4", s.F.W.S.Purchasing.SupplierId, s.F.W.Plant, "X-2", Today(h), Today(h), 1000m, 100m, 180m, 20m),
            new RegisterCustomsDeclarationHandler());
        await h.RunAsync(new ReverseCustomsDeclaration(h.CompanyId, s.F.W.Controller, "d4-x", second.ResultRef, 1, "Duplicado"), new ReverseCustomsDeclarationHandler());

        // Treasury's proposal lists the DGA's payable under the DUA's number, and the USD invoices with their currency (E-USD1-05-2).
        var proposal = await h.QueryAsync(
            new Rochell.Treasury.Queries.GetPaymentProposal(h.CompanyId, await h.SessionWithRolesAsync("TESORERO"), Today(h).AddDays(60)),
            new Rochell.Treasury.Queries.GetPaymentProposalHandler());

        Assert.Equal((ImportErrors.DuaNumberUsed, ImportErrors.DuaPartyInvalid), (duplicate.Code, foreignParty.Code));
        Assert.Contains("DUA 10020-IM-2610-000123", proposal, StringComparison.Ordinal);
        Assert.Contains("\"supplierFiscalNumber\":\"BL-778\"", proposal, StringComparison.Ordinal);
        Assert.Contains("\"currency\":\"USD\"", proposal, StringComparison.Ordinal);
        Assert.Equal(
            "REVERSED|0.00|P39-DR-CLR,P39-DR-ITBIS,P39-CR-AP,P39-DR-CLR,P39-DR-ITBIS,P39-CR-AP",
            await h.ScalarAsync<string>(
                """
                SELECT c.status || '|' || ap.open_amount::numeric(19,2) || '|' ||
                       (SELECT string_agg(e.rule_line_code, ',' ORDER BY j.journal_type, e.line_no) FROM fin.gl_journal j JOIN fin.gl_entry e ON e.journal_id = j.journal_id
                        WHERE j.source_event_id IN (SELECT event_id FROM core.domain_event WHERE aggregate_id = c.dua_id))
                FROM pur.customs_declaration c JOIN fin.ap_document ap ON ap.source_doc_id = c.dua_id WHERE c.dua_no = 'X-2'
                """));
    }
}
