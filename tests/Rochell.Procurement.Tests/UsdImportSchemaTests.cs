using Npgsql;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Procurement.Tests;

/// <summary>
/// USD1-01 schema guarantees (E-USD-1…9, E-USD1-01-1…7): exchange rates with four eyes, documents in USD that follow their supplier,
/// the supplier's own invoice number instead of an NCF, payables of invoices and DUAs, USD amounts on the ledger only for USD controls,
/// and import settlements written only while DRAFT.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class UsdImportSchemaTests(PostgresFixture postgres)
{
    private static Task Run(TestHarness h, Guid session, string key, string sql, params TestState[] states)
        => h.RunAsync(new TestTreasurySql(h.CompanyId, session, key, sql, states), new TestTreasurySqlHandler());

    private static async Task<string?> Fails(TestHarness h, Guid session, string key, string sql, params TestState[] states)
    {
        var ex = await Record.ExceptionAsync(() => Run(h, session, key, sql, states));
        return ex is PostgresException pg ? pg.SqlState : ex?.GetType().Name;
    }

    private static string Rate(TestHarness h, Guid id, string rate, string date = "current_date") =>
        $"INSERT INTO fin.exchange_rate (rate_id, company_id, currency, rate_date, rate, source, status, prepared_by, version) VALUES ('{id}', '{h.CompanyId}', 'USD', {date}, {rate}, 'Banco Central — tasa de venta', 'DRAFT', @user, 1)";

    [Fact]
    public async Task A_rate_is_prepared_and_approved_by_someone_else_never_changed_and_one_is_in_force_per_day()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var treasurer = await h.SessionWithRolesAsync("TESORERO", "TEST_PINGER");
        var controller = await h.SessionWithRolesAsync("CONTROLLER", "TEST_PINGER");
        var first = Guid.CreateVersion7();
        await Run(h, treasurer, "r1", Rate(h, first, "60.1234"), new TestState("ExchangeRate", first, null, "DRAFT"));
        string Approve(Guid id) => $"UPDATE fin.exchange_rate SET status = 'ACTIVE', approved_by = @user, approved_at = now(), version = 2 WHERE rate_id = '{id}'";

        var own = await Fails(h, treasurer, "a0", Approve(first), new TestState("ExchangeRate", first, "DRAFT", "ACTIVE"));
        await Run(h, controller, "a1", Approve(first), new TestState("ExchangeRate", first, "DRAFT", "ACTIVE"));
        var changed = await Fails(h, controller, "c1", $"UPDATE fin.exchange_rate SET rate = 61, version = 3 WHERE rate_id = '{first}'");
        var second = Guid.CreateVersion7();
        await Run(h, treasurer, "r2", Rate(h, second, "60.2000"), new TestState("ExchangeRate", second, null, "DRAFT"));
        var twoInForce = await Fails(h, controller, "a2", Approve(second), new TestState("ExchangeRate", second, "DRAFT", "ACTIVE"));
        await Run(
            h, controller, "a3",
            $"UPDATE fin.exchange_rate SET status = 'SUPERSEDED', version = 3 WHERE rate_id = '{first}'; " + Approve(second),
            new TestState("ExchangeRate", first, "ACTIVE", "SUPERSEDED"), new TestState("ExchangeRate", second, "DRAFT", "ACTIVE"));
        var euro = await Fails(h, treasurer, "eur", Rate(h, Guid.CreateVersion7(), "65").Replace("'USD'", "'EUR'", StringComparison.Ordinal));

        Assert.Equal(("23514", "42501", "23505"), (own, changed, twoInForce)); // four eyes (core.four_eyes) answers as a check violation
        Assert.Equal("23514", euro);
        Assert.Equal("60.2000", await h.ScalarAsync<string>("SELECT rate::text FROM fin.exchange_rate WHERE status = 'ACTIVE'"));
    }

    [Fact]
    public async Task A_foreign_supplier_deals_in_USD_with_its_own_invoice_number_and_a_local_one_in_pesos_with_an_NCF()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePurchasingSetupAsync();
        var foreign = Guid.CreateVersion7();
        await h.AdminRequireAsync($"INSERT INTO md.party VALUES ('{foreign}', '{h.CompanyId}', 'FOREIGN', NULL, 'Additives Corp. (USA)', true, 'ACTIVE', NULL, 1)");
        var user = await h.ScalarAsync<Guid>("SELECT user_id FROM iam.user ORDER BY user_id LIMIT 1");
        var poId = Guid.CreateVersion7();
        string Order(Guid party, string no, string currency) =>
            "INSERT INTO pur.purchase_order (po_id, company_id, po_no, party_id, plant_id, order_date, status, created_by, version, currency) " +
            $"VALUES ('{poId}', '{h.CompanyId}', '{no}', '{party}', '{p.PlantId}', current_date, 'DRAFT', '{user}', 1, '{currency}')";
        var buyer = await h.SessionWithRolesAsync("COMPRADOR", "TEST_PINGER");
        string Invoice(Guid party, string number, string currency, string rate, string fc, Guid? id = null) =>
            "INSERT INTO pur.supplier_invoice (si_id, company_id, party_id, supplier_fiscal_number, doc_date, due_date, document_status, accounting_status, total_amount, created_by, version, " +
            $"currency, exchange_rate, total_amount_fc) VALUES ('{id ?? Guid.CreateVersion7()}', '{h.CompanyId}', '{party}', '{number}', current_date, current_date + 30, 'DRAFT', 'NOT_POSTED', 600000, '{user}', 1, " +
            $"'{currency}', {rate}, {fc})";

        await Run(h, buyer, "po", Order(foreign, "OC-2026-900001", "USD"), new TestState("PurchaseOrder", poId, null, "DRAFT"));
        Assert.Contains("foreign supplier deals in USD", (await h.AdminExecuteAsync(Order(foreign, "OC-2026-900002", "DOP")))?.MessageText, StringComparison.Ordinal);
        Assert.Contains("foreign supplier deals in USD", (await h.AdminExecuteAsync(Order(p.SupplierId, "OC-2026-900003", "USD")))?.MessageText, StringComparison.Ordinal);
        var usd = Guid.CreateVersion7();
        await Run(h, buyer, "si1", Invoice(foreign, "INV-2026-0042", "USD", "60.0000", "10000.00", usd), new TestState("SupplierInvoice", usd, null, "DRAFT"));
        Assert.Equal("23514", (await h.AdminExecuteAsync(Invoice(foreign, "INV-2026-0043", "USD", "NULL", "10000.00")))?.SqlState);
        Assert.Equal("23514", (await h.AdminExecuteAsync(Invoice(p.SupplierId, "INV-2026-0044", "DOP", "NULL", "NULL")))?.SqlState);
        var dop = Guid.CreateVersion7();
        await Run(h, buyer, "si2", Invoice(p.SupplierId, "B0100000044", "DOP", "NULL", "NULL", dop), new TestState("SupplierInvoice", dop, null, "DRAFT"));
    }

    [Fact]
    public async Task The_ledger_keeps_USD_only_on_USD_controls_and_the_new_roles_and_permissions_are_seeded()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        Assert.Contains(
            "account_role = ANY (ARRAY['AP_FOREIGN'::text, 'BANK'::text])",
            await h.ScalarAsync<string>("SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'gl_entry_currency'"),
            StringComparison.Ordinal);
        Assert.Equal(
            "AP_FOREIGN:true,FX_GAIN:false,FX_LOSS:false,FX_UNREALIZED:false,IMPORT_CLEARING:true",
            await h.ScalarAsync<string>(
                "SELECT string_agg(role_code || ':' || is_control, ',' ORDER BY role_code) FROM fin.account_role WHERE role_code IN ('AP_FOREIGN', 'FX_GAIN', 'FX_LOSS', 'FX_UNREALIZED', 'IMPORT_CLEARING')"));
        Assert.Equal(
            "CONTADOR:exchange_rate:prepare,CONTROLLER:exchange_rate:approve,CONTROLLER:import_settlement:approve,CUENTAS_POR_PAGAR:import_settlement:prepare,TESORERO:exchange_rate:prepare",
            await h.ScalarAsync<string>(
                """
                SELECT string_agg(r.code || ':' || rp.permission_code, ',' ORDER BY r.code, rp.permission_code)
                FROM iam.role_permission rp JOIN iam.role r ON r.role_id = rp.role_id
                WHERE rp.permission_code IN ('exchange_rate:prepare', 'exchange_rate:approve', 'import_settlement:prepare', 'import_settlement:approve') AND r.code <> 'SUPERADMIN'
                """));
    }

    [Fact]
    public async Task A_DUA_is_owed_to_the_DGA_in_pesos_and_a_settlement_takes_documents_only_while_DRAFT()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var p = await h.CreatePurchasingSetupAsync();
        var dga = await h.CreateActiveSupplierAsync("401007551", "Dirección General de Aduanas");
        var payables = await h.SessionWithRolesAsync("CUENTAS_POR_PAGAR", "TEST_PINGER");
        var controller = await h.SessionWithRolesAsync("CONTROLLER", "TEST_PINGER");
        var dua = Guid.CreateVersion7();
        await Run(
            h, payables, "dua",
            "INSERT INTO pur.customs_declaration (dua_id, company_id, dua_no, dua_date, party_id, cif_amount, duties_amount, itbis_amount, other_amount, due_date, status, accounting_status, created_by, version) " +
            $"VALUES ('{dua}', '{h.CompanyId}', '10-2026-IC01-000123', current_date, '{dga}', 660000.00, 30000.00, 124200.00, 0, current_date, 'DRAFT', 'NOT_POSTED', @user, 1)",
            new TestState("CustomsDeclaration", dua, null, "DRAFT"));
        var usdPayable = await Fails(
            h, payables, "ap",
            "INSERT INTO fin.ap_document (ap_doc_id, company_id, party_id, doc_type, source_doc_id, doc_date, due_date, original_amount, open_amount, version, currency, original_amount_fc, open_amount_fc) " +
            $"VALUES (gen_random_uuid(), '{h.CompanyId}', '{dga}', 'CUSTOMS_DECLARATION', '{dua}', current_date, current_date, 154200, 154200, 1, 'USD', 2570, 2570)");

        var settlement = Guid.CreateVersion7();
        await Run(
            h, payables, "li",
            "INSERT INTO pur.import_settlement (settlement_id, company_id, settlement_no, plant_id, settlement_date, status, accounting_status, prepared_by, version) " +
            $"VALUES ('{settlement}', '{h.CompanyId}', 'LI-2026-000001', '{p.PlantId}', current_date, 'DRAFT', 'NOT_POSTED', @user, 1); " +
            $"INSERT INTO pur.import_settlement_document VALUES ('{settlement}', '{h.CompanyId}', 'CUSTOMS_DECLARATION', '{dua}', 30000.00)",
            new TestState("ImportSettlement", settlement, null, "DRAFT"));
        var ownApproval = await Fails(
            h, payables, "own",
            $"UPDATE pur.import_settlement SET status = 'POSTED', approved_by = @user, approved_at = now(), version = 2 WHERE settlement_id = '{settlement}'",
            new TestState("ImportSettlement", settlement, "DRAFT", "POSTED"));
        await Run(
            h, controller, "post",
            $"UPDATE pur.import_settlement SET status = 'POSTED', approved_by = @user, approved_at = now(), version = 2 WHERE settlement_id = '{settlement}'",
            new TestState("ImportSettlement", settlement, "DRAFT", "POSTED"));
        var late = await Fails(h, controller, "late", $"DELETE FROM pur.import_settlement_document WHERE settlement_id = '{settlement}'");

        Assert.NotNull(usdPayable); // a DUA is owed in pesos (and the application role writes payables only through the posting)
        Assert.Equal(("23514", "P0001"), (ownApproval, late));
    }
}
