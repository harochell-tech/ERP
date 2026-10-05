using Npgsql;
using Rochell.Sales.Orders;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>
/// PRS-01 schema guarantees (E-PRC1-1…11, E-SRV1-1…19, E-PRS-01-1…8), written as the application role through the fixture SQL
/// command (the commands arrive in PRS-02…04): named lists with GENERAL fixed, the customer's list in its terms, zones, the freight
/// item, freight prices of a DRAFT version, the zone and freight on orders, and product / freight lines of invoices and proformas.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PriceListFreightSchemaTests(PostgresFixture postgres)
{
    private static Task Run(TestHarness h, string key, string sql, params TestState[] states)
        => h.RunAsync(new TestTreasurySql(h.CompanyId, h.SessionId, key, sql, states), new TestTreasurySqlHandler());

    private static async Task<string?> Fails(TestHarness h, string key, string sql, params TestState[] states)
    {
        var ex = await Record.ExceptionAsync(() => Run(h, key, sql, states));
        return ex is PostgresException pg ? pg.SqlState : ex?.GetType().Name;
    }

    private static string List(TestHarness h, Guid id, string code) =>
        $"INSERT INTO sal.price_list (price_list_id, company_id, code, name, status, created_by, version) VALUES ('{id}', '{h.CompanyId}', '{code}', '{code}', 'ACTIVE', @user, 1)";

    private static string Zone(TestHarness h, Guid id, string name) =>
        $"INSERT INTO sal.delivery_zone (zone_id, company_id, name, status, version) VALUES ('{id}', '{h.CompanyId}', '{name}', 'ACTIVE', 1)";

    private static string Version(TestHarness h, Guid id, Guid list, int version) =>
        "INSERT INTO sal.price_list_version (price_list_version_id, company_id, version, effective_from, status, prepared_by, price_list_id) " +
        $"VALUES ('{id}', '{h.CompanyId}', {version}, current_date, 'DRAFT', @user, '{list}')";

    private static string Freight(TestHarness h, Guid version, Guid item, Guid zone, string price = "3.00") =>
        "INSERT INTO sal.price_list_freight (price_list_version_id, company_id, item_id, uom, zone_id, unit_price) " +
        $"VALUES ('{version}', '{h.CompanyId}', '{item}', (SELECT base_uom FROM md.item WHERE item_id = '{item}'), '{zone}', {price})";

    [Trait("AcceptancePrs1", "PRC-01")]
    [Fact]
    public async Task GENERAL_holds_the_list_of_today_and_every_customer_and_is_never_deactivated()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await DeliveryTests.SetupAsync(h);

        Assert.Equal(
            "GENERAL:ACTIVE:1:1",
            await h.ScalarAsync<string>(
                """
                SELECT l.code || ':' || l.status || ':' || (SELECT count(*) FROM sal.price_list_version v WHERE v.price_list_id = l.price_list_id) || ':' ||
                       (SELECT count(*) FROM sal.customer_terms_version t WHERE t.price_list_id = l.price_list_id)
                FROM sal.price_list l
                """));
        var general = await h.ScalarAsync<Guid>("SELECT price_list_id FROM sal.price_list WHERE code = 'GENERAL'");
        // Refused by the guard while customers are on it, and by the CHECK for the owner even without them.
        Assert.Equal("P0001", await Fails(h, "off", $"UPDATE sal.price_list SET status = 'INACTIVE', version = 2 WHERE price_list_id = '{general}'", new TestState("PriceListHeader", general, "ACTIVE", "INACTIVE")));
        Assert.Contains("price_list_general_active", (await h.AdminExecuteAsync(
            $"SET LOCAL session_replication_role = replica; UPDATE sal.price_list SET status = 'INACTIVE', version = 2 WHERE price_list_id = '{general}'"))?.MessageText, StringComparison.Ordinal);
        Assert.Equal("42501", await Fails(h, "del", $"DELETE FROM sal.price_list WHERE price_list_id = '{general}'")); // no DELETE grant
        Assert.Equal("23514", await Fails(h, "anon", $"INSERT INTO sal.price_list (price_list_id, company_id, code, name, status, created_by, version) VALUES (gen_random_uuid(), '{h.CompanyId}', 'HOTELES', 'Hoteles', 'ACTIVE', NULL, 1)"));
        Assert.Equal("23514", await Fails(h, "code", List(h, Guid.CreateVersion7(), "hoteles")));
        _ = s;
    }

    [Fact]
    public async Task A_list_numbers_its_own_versions_and_goes_inactive_only_when_no_customer_terms_name_it()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await DeliveryTests.SetupAsync(h);
        var hoteles = Guid.CreateVersion7();
        await Run(h, "list", List(h, hoteles, "HOTELES"));

        // Version 1 of HOTELES beside version 1 of GENERAL; a second version 1 of HOTELES is refused.
        var v1 = Guid.CreateVersion7();
        await Run(h, "v1", Version(h, v1, hoteles, 1), new TestState("PriceList", v1, null, "DRAFT"));
        var v1b = Guid.CreateVersion7();
        var twice = await Fails(h, "v1b", Version(h, v1b, hoteles, 1), new TestState("PriceList", v1b, null, "DRAFT"));

        // The customer's pending terms name HOTELES: it cannot go inactive; without them it can, and then takes no new version or terms.
        var terms = Guid.CreateVersion7();
        await Run(h, "terms",
            "INSERT INTO sal.customer_terms_version (terms_version_id, company_id, party_id, version, effective_from, payment_terms_days, credit_limit, credit_hold, status, prepared_by, price_list_id) " +
            $"VALUES ('{terms}', '{h.CompanyId}', '{s.Customer}', 2, current_date, 30, 1000.00, false, 'DRAFT', @user, '{hoteles}')", new TestState("CustomerTerms", terms, null, "DRAFT"));
        var inUse = await Fails(h, "off1", $"UPDATE sal.price_list SET status = 'INACTIVE', version = 2 WHERE price_list_id = '{hoteles}'", new TestState("PriceListHeader", hoteles, "ACTIVE", "INACTIVE"));
        await Run(h, "back", $"UPDATE sal.customer_terms_version SET price_list_id = sal.general_price_list(company_id) WHERE terms_version_id = '{terms}'");
        await Run(h, "off2", $"UPDATE sal.price_list SET status = 'INACTIVE', version = 2 WHERE price_list_id = '{hoteles}'", new TestState("PriceListHeader", hoteles, "ACTIVE", "INACTIVE"));
        var v2 = Guid.CreateVersion7();
        var newVersion = await Fails(h, "v2", Version(h, v2, hoteles, 2), new TestState("PriceList", v2, null, "DRAFT"));
        var newTerms = await Fails(h, "terms2", $"UPDATE sal.customer_terms_version SET price_list_id = '{hoteles}' WHERE terms_version_id = '{terms}'");
        var approvedChange = await Fails(h, "approved", $"UPDATE sal.customer_terms_version SET price_list_id = '{hoteles}' WHERE status = 'ACTIVE'");

        Assert.Equal(("23505", "P0001"), (twice, inUse));
        Assert.Equal(("P0001", "P0001", "P0001"), (newVersion, newTerms, approvedChange));
    }

    [Fact]
    public async Task Zones_freight_item_and_freight_prices_are_guarded()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await DeliveryTests.SetupAsync(h);
        var bavaro = Guid.CreateVersion7();
        await Run(h, "zone", Zone(h, bavaro, "Bávaro"));
        var sameName = await Fails(h, "zone2", Zone(h, Guid.CreateVersion7(), "BÁVARO"));
        var deleted = await Fails(h, "zdel", $"DELETE FROM sal.delivery_zone WHERE zone_id = '{bavaro}'");

        // The freight item: SERVICE / TRANSPORTE, one per company, of no other category.
        var freight = Guid.CreateVersion7();
        string Item(Guid id, string code, string type, string category) =>
            "INSERT INTO md.item (item_id, company_id, code, description, item_type, base_uom, item_category, status, version) " +
            $"VALUES ('{id}', '{h.CompanyId}', '{code}', 'Transporte de blocks', '{type}', 'un', '{category}', 'ACTIVE', 1)";
        var wrongCategory = await Fails(h, "i0", Item(Guid.CreateVersion7(), "FLETE", "SERVICE", "BLOQUE"));
        await Run(h, "i1", Item(freight, "TRANSPORTE", "SERVICE", "TRANSPORTE"));
        var second = await Fails(h, "i2", Item(Guid.CreateVersion7(), "TRANSPORTE-2", "SERVICE", "TRANSPORTE"));

        // Freight prices: a DRAFT version only, of a finished good, never changed; the ACTIVE version of today takes none.
        var draft = Guid.CreateVersion7();
        var general = await h.ScalarAsync<Guid>("SELECT price_list_id FROM sal.price_list WHERE code = 'GENERAL'");
        await Run(h, "v", Version(h, draft, general, 2), new TestState("PriceList", draft, null, "DRAFT"));
        await Run(h, "f", Freight(h, draft, s.Block, bavaro));
        var active = await h.ScalarAsync<Guid>("SELECT price_list_version_id FROM sal.price_list_version WHERE status = 'ACTIVE'");
        var onActive = await Fails(h, "f2", Freight(h, active, s.Block, bavaro));
        var ofService = await Fails(h, "f3", Freight(h, draft, freight, bavaro));
        var changed = await Fails(h, "f4", $"UPDATE sal.price_list_freight SET unit_price = 4.00 WHERE price_list_version_id = '{draft}'");
        var zero = await Fails(h, "f5", Freight(h, draft, s.Block, Guid.Empty, "0"));

        Assert.Equal(("23505", "42501"), (sameName, deleted)); // no DELETE grant; the guard refuses the owner too
        Assert.Equal(("23514", "23505"), (wrongCategory, second));
        Assert.Equal(("P0001", "P0001"), (onActive, ofService));
        Assert.NotNull(changed); // no UPDATE grant, and the guard refuses it for the owner too
        Assert.NotNull(zero);
    }

    [Fact]
    public async Task Freight_rides_an_own_truck_order_with_a_zone_and_its_invoice_lines_are_of_their_kind()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await DeliveryTests.SetupAsync(h);
        var bavaro = Guid.CreateVersion7();
        var miches = Guid.CreateVersion7();
        await Run(h, "z1", Zone(h, bavaro, "Bávaro"));
        await Run(h, "z2", Zone(h, miches, "Miches"));
        await Run(h, "z2off", $"UPDATE sal.delivery_zone SET status = 'INACTIVE', version = 2 WHERE zone_id = '{miches}'");

        string Order(Guid id, string no, string term, string zone, string site = "'Obra Bávaro'") =>
            "INSERT INTO sal.sales_order (sales_order_id, company_id, order_no, party_id, plant_id, order_date, delivery_term_code, site_address, price_list_version_id, " +
            $"status, total_net, lines_version, created_by, version, delivery_zone_id) VALUES ('{id}', '{h.CompanyId}', '{no}', '{s.Customer}', '{s.Plant}', current_date, '{term}', {site}, " +
            $"(SELECT price_list_version_id FROM sal.price_list_version WHERE status = 'ACTIVE'), 'DRAFT', 53000.00, 1, @user, 1, {zone})";
        string Line(Guid order, string freight) =>
            "INSERT INTO sal.sales_order_line (line_id, company_id, sales_order_id, lines_version, line_no, item_id, uom, qty_ordered, unit_price, net_amount, qty_delivered, qty_invoiced, " +
            $"price_list_version_id, freight_unit_price, freight_amount) VALUES (gen_random_uuid(), '{h.CompanyId}', '{order}', 1, 1, '{s.Block}', 'un', 1000, 50.00, 50000.00, 0, 0, " +
            $"(SELECT price_list_version_id FROM sal.price_list_version WHERE status = 'ACTIVE'), {freight})";
        TestState Draft(Guid id) => new("SalesOrder", id, null, "DRAFT");

        var pickupWithZone = await Fails(h, "o1", Order(Guid.CreateVersion7(), "PV-000951", "PICKUP_AT_PLANT", $"'{bavaro}'", "NULL"), Draft(Guid.Empty));
        var inactiveZone = await Fails(h, "o2", Order(Guid.CreateVersion7(), "PV-000952", "DELIVERED_OWN_TRANSPORT", $"'{miches}'"), Draft(Guid.Empty));
        var own = Guid.CreateVersion7();
        await Run(h, "o3", Order(own, "PV-000953", "DELIVERED_OWN_TRANSPORT", $"'{bavaro}'"), Draft(own));
        await Run(h, "l3", Line(own, "3.00, 3000.00"));
        var withoutZone = Guid.CreateVersion7();
        await Run(h, "o4", Order(withoutZone, "PV-000954", "DELIVERED_OWN_TRANSPORT", "NULL"), Draft(withoutZone));
        var freightWithoutZone = await Fails(h, "l4", Line(withoutZone, "3.00, 3000.00"));
        var halfFreight = await Fails(h, "l5", Line(own, "3.00, NULL")); // the CHECK refuses it before the unique key is reached
        await Run(h, "pending", $"UPDATE sal.sales_order SET exemption_pending = true, proforma_collects_itbis = false, delivery_zone_id = '{bavaro}', version = 2 WHERE sales_order_id = '{withoutZone}'");
        var freightOnExemption = await Fails(h, "l6", Line(withoutZone, "3.00, 3000.00"));

        // The zone changes only while DRAFT.
        await Run(h, "confirm", $"UPDATE sal.sales_order SET status = 'CONFIRMED', version = 2 WHERE sales_order_id = '{own}'", new TestState("SalesOrder", own, "DRAFT", "CONFIRMED"));
        var zoneAfter = await Fails(h, "zchg", $"UPDATE sal.sales_order SET delivery_zone_id = NULL, version = 3 WHERE sales_order_id = '{own}'");

        Assert.Equal(("23514", "P0001"), (pickupWithZone, inactiveZone));
        Assert.Equal(("P0001", "23514", "P0001", "P0001"), (freightWithoutZone, halfFreight, freightOnExemption, zoneAfter));
        Assert.Equal("3000.0000", await h.ScalarAsync<string>($"SELECT freight_amount::text FROM sal.sales_order_line WHERE sales_order_id = '{own}'"));
        Assert.Equal(
            "PRODUCT:invoice_line_delivery_uq:proforma_line_delivery_uq",
            await h.ScalarAsync<string>(
                """
                SELECT (SELECT column_default FROM information_schema.columns WHERE table_schema = 'sal' AND table_name = 'invoice_line' AND column_name = 'line_kind')::text
                       || ':' || string_agg(conname, ':' ORDER BY conname)
                FROM pg_constraint WHERE conname IN ('invoice_line_delivery_uq', 'proforma_line_delivery_uq') AND pg_get_constraintdef(oid) LIKE '%line_kind%'
                """).ContinueWith(t => t.Result?.Replace("'PRODUCT'::text", "PRODUCT", StringComparison.Ordinal), TaskScheduler.Default));
        Assert.Equal("FREIGHT_REVENUE:false", await h.ScalarAsync<string>("SELECT role_code || ':' || is_control FROM fin.account_role WHERE role_code = 'FREIGHT_REVENUE'"));
    }
}
