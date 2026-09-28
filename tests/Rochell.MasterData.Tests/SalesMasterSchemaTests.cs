using Npgsql;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.MasterData.Tests;

/// <summary>
/// VS3-01 schema guarantees (Frozen Baseline VS#3 §2, §7; E-VS3-01-1…17), written as the application role through a fixture
/// command (the commands arrive in VS3-02).
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SalesMasterSchemaTests(PostgresFixture postgres)
{
    private static Task Run(TestHarness h, string key, string sql, params TestState[] states)
        => h.RunAsync(new TestTreasurySql(h.CompanyId, h.SessionId, key, sql, states), new TestTreasurySqlHandler());

    private static async Task<string?> Fails(TestHarness h, string key, string sql, params TestState[] states)
    {
        var ex = await Record.ExceptionAsync(() => Run(h, key, sql, states));
        return ex is PostgresException pg ? pg.SqlState : ex?.GetType().Name;
    }

    private static async Task<Guid> FinishedGoodAsync(TestHarness h, string code = "BLOQUE-6")
    {
        var id = Guid.CreateVersion7();
        await h.AdminRequireAsync($"INSERT INTO md.item VALUES ('{id}', '{h.CompanyId}', '{code}', 'Bloque de 6 pulgadas', 'FINISHED_GOOD', 'un', 'BLOQUE', 'ACTIVE', 1)");
        return id;
    }

    [Fact]
    public async Task An_active_supplier_becomes_a_customer_without_touching_its_supplier_identity()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var party = await h.CreateActiveSupplierAsync("131925332", "Constructora Uno");

        await Run(h, "customer",
            $"UPDATE md.party SET is_customer = true, customer_status = 'DRAFT', phone = '809-555-0101', version = 2 WHERE party_id = '{party}'",
            new TestState("Customer", party, null, "DRAFT"));
        await Run(h, "activate", $"UPDATE md.party SET customer_status = 'ACTIVE', email = 'compras@uno.do', version = 3 WHERE party_id = '{party}'",
            new TestState("Customer", party, "DRAFT", "ACTIVE"));
        var rename = await Fails(h, "rename", $"UPDATE md.party SET legal_name = 'Otra', version = 4 WHERE party_id = '{party}'");
        var backToDraft = await Fails(h, "draft", $"UPDATE md.party SET customer_status = 'DRAFT', version = 4 WHERE party_id = '{party}'",
            new TestState("Customer", party, "ACTIVE", "DRAFT"));
        var notCustomer = await Fails(h, "uncustomer", $"UPDATE md.party SET is_customer = false, customer_status = NULL, version = 4 WHERE party_id = '{party}'");
        var noHistory = await Fails(h, "block", $"UPDATE md.party SET customer_status = 'BLOCKED', version = 4 WHERE party_id = '{party}'");
        var badEmail = await Fails(h, "email", $"UPDATE md.party SET email = 'sin-arroba', version = 4 WHERE party_id = '{party}'");

        Assert.Equal(("P0001", "P0001", "P0001", "P0001", "23514"), (rename, backToDraft, notCustomer, noHistory, badEmail));
        Assert.Equal("ACTIVE:true:ACTIVE:3", await h.ScalarAsync<string>(
            $"SELECT status::text || ':' || is_supplier || ':' || customer_status || ':' || version FROM md.party WHERE party_id = '{party}'"));
    }

    [Fact]
    public async Task Customer_terms_are_versioned_approved_by_another_person_and_one_is_active()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var supplierOnly = await h.CreateActiveSupplierAsync("101000001", "Solo proveedor");
        var customer = await h.CreateActiveSupplierAsync("131925332", "Constructora Uno");
        await Run(h, "customer",
            $"UPDATE md.party SET is_customer = true, customer_status = 'DRAFT', version = 2 WHERE party_id = '{customer}'; UPDATE md.party SET customer_status = 'ACTIVE', version = 3 WHERE party_id = '{customer}'",
            new TestState("Customer", customer, null, "DRAFT"), new TestState("Customer", customer, "DRAFT", "ACTIVE"));
        var approver = await h.CreateUserAsync();
        var v1 = Guid.CreateVersion7();
        var v2 = Guid.CreateVersion7();
        string Terms(Guid id, Guid party, int version, string limit) =>
            $"INSERT INTO sal.customer_terms_version VALUES ('{id}', '{h.CompanyId}', '{party}', {version}, current_date, 30, {limit}, false, 'DRAFT', @user, NULL)";

        var notCustomer = await Fails(h, "supplier", Terms(Guid.CreateVersion7(), supplierOnly, 1, "1000"), new TestState("CustomerTerms", Guid.Empty, null, "DRAFT"));
        await Run(h, "v1", Terms(v1, customer, 1, "100000"), new TestState("CustomerTerms", v1, null, "DRAFT"));
        var self = await Fails(h, "self", $"UPDATE sal.customer_terms_version SET status = 'ACTIVE', approved_by = @user WHERE terms_version_id = '{v1}'",
            new TestState("CustomerTerms", v1, "DRAFT", "ACTIVE"));
        await Run(h, "approve", $"UPDATE sal.customer_terms_version SET status = 'ACTIVE', approved_by = '{approver}' WHERE terms_version_id = '{v1}'",
            new TestState("CustomerTerms", v1, "DRAFT", "ACTIVE"));
        var changeApproved = await Fails(h, "change", $"UPDATE sal.customer_terms_version SET credit_limit = 1 WHERE terms_version_id = '{v1}'");
        await Run(h, "v2", Terms(v2, customer, 2, "150000"), new TestState("CustomerTerms", v2, null, "DRAFT"));
        var twoActive = await Fails(h, "two", $"UPDATE sal.customer_terms_version SET status = 'ACTIVE', approved_by = '{approver}' WHERE terms_version_id = '{v2}'",
            new TestState("CustomerTerms", v2, "DRAFT", "ACTIVE"));
        await Run(h, "supersede",
            $"UPDATE sal.customer_terms_version SET status = 'SUPERSEDED' WHERE terms_version_id = '{v1}'; UPDATE sal.customer_terms_version SET status = 'ACTIVE', approved_by = '{approver}' WHERE terms_version_id = '{v2}'",
            new TestState("CustomerTerms", v1, "ACTIVE", "SUPERSEDED"), new TestState("CustomerTerms", v2, "DRAFT", "ACTIVE"));
        var negative = await Fails(h, "negative", Terms(Guid.CreateVersion7(), customer, 3, "-1"), new TestState("CustomerTerms", Guid.Empty, null, "DRAFT"));

        Assert.Equal(("P0001", "23514", "P0001", "23505", "23514"), (notCustomer, self, changeApproved, twoActive, negative));
        Assert.Equal("1:SUPERSEDED,2:ACTIVE", await h.ScalarAsync<string>("SELECT string_agg(version || ':' || status, ',' ORDER BY version) FROM sal.customer_terms_version"));
    }

    [Fact]
    public async Task Standard_cost_and_price_list_take_only_finished_goods_and_lines_only_while_draft()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var plant = await h.CreatePlantAsync();
        var area = await h.ScalarAsync<Guid>("SELECT valuation_area_id FROM md.plant WHERE plant_id = @p", ("p", plant));
        var block = await FinishedGoodAsync(h);
        var sand = Guid.CreateVersion7();
        await h.AdminRequireAsync($"INSERT INTO md.item VALUES ('{sand}', '{h.CompanyId}', 'ARENA', 'Arena', 'RAW_MATERIAL', 't', 'AGREGADO', 'ACTIVE', 1)");
        var badCategory = await h.AdminExecuteAsync($"INSERT INTO md.item VALUES (gen_random_uuid(), '{h.CompanyId}', 'PT-X', 'X', 'FINISHED_GOOD', 'un', 'CEMENTO', 'ACTIVE', 1)");
        var approver = await h.CreateUserAsync();
        var cost = Guid.CreateVersion7();
        var list = Guid.CreateVersion7();

        var rawCost = await Fails(h, "raw-cost",
            $"INSERT INTO md.standard_cost_version VALUES (gen_random_uuid(), '{h.CompanyId}', '{sand}', '{area}', 1, current_date, 10.5, 'DRAFT', @user, NULL)",
            new TestState("StandardCost", Guid.Empty, null, "DRAFT"));
        await Run(h, "cost",
            $"INSERT INTO md.standard_cost_version VALUES ('{cost}', '{h.CompanyId}', '{block}', '{area}', 1, current_date, 32.75, 'DRAFT', @user, NULL)",
            new TestState("StandardCost", cost, null, "DRAFT"));
        await Run(h, "list",
            $"""
            INSERT INTO sal.price_list_version VALUES ('{list}', '{h.CompanyId}', 1, current_date, 'DRAFT', @user, NULL);
            INSERT INTO sal.price_list_line VALUES ('{list}', '{h.CompanyId}', '{block}', 'un', 45.00);
            """,
            new TestState("PriceList", list, null, "DRAFT"));
        var twice = await Fails(h, "twice", $"INSERT INTO sal.price_list_line VALUES ('{list}', '{h.CompanyId}', '{block}', 'un', 46.00)");
        var rawPrice = await Fails(h, "raw-price", $"INSERT INTO sal.price_list_line VALUES ('{list}', '{h.CompanyId}', '{sand}', 't', 900.00)");
        await Run(h, "approve", $"UPDATE sal.price_list_version SET status = 'ACTIVE', approved_by = '{approver}' WHERE price_list_version_id = '{list}'",
            new TestState("PriceList", list, "DRAFT", "ACTIVE"));
        var late = await Fails(h, "late", $"INSERT INTO sal.price_list_line VALUES ('{list}', '{h.CompanyId}', '{block}', 'kg', 1.00)");
        var edit = await Fails(h, "edit", $"UPDATE sal.price_list_line SET unit_price = 1 WHERE price_list_version_id = '{list}'");

        Assert.Equal("23514", badCategory?.SqlState);
        Assert.Equal(("P0001", "23505", "P0001", "P0001", "42501"), (rawCost, twice, rawPrice, late, edit)); // price lines: no UPDATE grant
    }

    [Fact]
    public async Task Vehicles_and_drivers_keep_plate_and_cedula_are_never_deleted_and_record_their_status()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var truck = Guid.CreateVersion7();
        var driver = Guid.CreateVersion7();
        await Run(h, "fleet",
            $"""
            INSERT INTO log.vehicle VALUES ('{truck}', '{h.CompanyId}', 'L123456', 12000, 'ACTIVE', 1);
            INSERT INTO log.driver VALUES ('{driver}', '{h.CompanyId}', 'Juan Pérez', '00112345678', 'ACTIVE', 1);
            """,
            new TestState("Vehicle", truck, null, "ACTIVE"), new TestState("Driver", driver, null, "ACTIVE"));

        var badPlate = await Fails(h, "plate", $"INSERT INTO log.vehicle VALUES (gen_random_uuid(), '{h.CompanyId}', 'l-12', 1000, 'ACTIVE', 1)");
        var samePlate = await Fails(h, "same", $"INSERT INTO log.vehicle VALUES (gen_random_uuid(), '{h.CompanyId}', 'L123456', 1000, 'ACTIVE', 1)", new TestState("Vehicle", Guid.Empty, null, "ACTIVE"));
        var noHistory = await Fails(h, "inactive", $"UPDATE log.vehicle SET status = 'INACTIVE', version = 2 WHERE vehicle_id = '{truck}'");
        await Run(h, "inactive-ok", $"UPDATE log.vehicle SET status = 'INACTIVE', version = 2 WHERE vehicle_id = '{truck}'", new TestState("Vehicle", truck, "ACTIVE", "INACTIVE"));
        var cedula = await Fails(h, "cedula", $"UPDATE log.driver SET national_id = '00199999999', version = 2 WHERE driver_id = '{driver}'");
        var delete = await Fails(h, "delete", $"DELETE FROM log.driver WHERE driver_id = '{driver}'");

        Assert.Equal(("23514", "23505", "P0001", "42501", "42501"), (badPlate, samePlate, noHistory, cedula, delete)); // no column grant, no DELETE grant
        Assert.Equal("INACTIVE:2", await h.ScalarAsync<string>($"SELECT status || ':' || version FROM log.vehicle WHERE vehicle_id = '{truck}'"));
    }

    [Fact]
    public async Task The_sales_account_roles_are_seeded_unmapped_with_their_subledgers()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        Assert.Equal(
            "AR_CONTROL:true,CASH_IN_TRANSIT:true,COGS:false,CONTRACT_ASSET:true,FINISHED_GOODS:true,FINISHED_GOODS_IN_TRANSIT:true,ITBIS_PAYABLE:false,"
            + "MIGRATION_CLEARING:false,REVENUE_PRODUCT:false,SALES_DISCOUNTS:false,TRANSIT_LOSS:false,UNAPPLIED_RECEIPTS:true,WITHHOLDING_RECEIVABLE:false",
            await h.ScalarAsync<string>(
                """
                SELECT string_agg(role_code || ':' || is_control, ',' ORDER BY role_code) FROM fin.account_role
                WHERE role_code IN ('AR_CONTROL', 'CONTRACT_ASSET', 'UNAPPLIED_RECEIPTS', 'CASH_IN_TRANSIT', 'FINISHED_GOODS', 'FINISHED_GOODS_IN_TRANSIT', 'COGS',
                  'REVENUE_PRODUCT', 'SALES_DISCOUNTS', 'ITBIS_PAYABLE', 'WITHHOLDING_RECEIVABLE', 'TRANSIT_LOSS', 'MIGRATION_CLEARING')
                """));
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM fin.account_role_map WHERE account_role IN ('AR_CONTROL', 'FINISHED_GOODS', 'REVENUE_PRODUCT')"));
    }
}
