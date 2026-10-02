using Npgsql;
using Rochell.Sales.Customers;
using Rochell.Sales.Receipts;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>
/// CF1-01 schema guarantees (E-CF1-1…14, E-CF1-01-1…7), written as the application role through a fixture command (the commands
/// arrive in CF1-02 and CF1-03): the final consumer, the cash order and its payment, the buyer on the invoice's fiscal record.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class CashSaleSchemaTests(PostgresFixture postgres)
{
    private static Task Run(TestHarness h, string key, string sql, params TestState[] states)
        => h.RunAsync(new TestTreasurySql(h.CompanyId, h.SessionId, key, sql, states), new TestTreasurySqlHandler());

    private static async Task<string?> Fails(TestHarness h, string key, string sql, params TestState[] states)
    {
        var ex = await Record.ExceptionAsync(() => Run(h, key, sql, states));
        return ex is PostgresException pg ? pg.SqlState : ex?.GetType().Name;
    }

    private static string Consumer(TestHarness h, Guid id, string rnc = "NULL", string supplier = "false", string name = "Consumidor final") =>
        "INSERT INTO md.party (party_id, company_id, party_kind, rnc, legal_name, is_supplier, status, version, is_customer, customer_status) " +
        $"VALUES ('{id}', '{h.CompanyId}', 'CONSUMER', {rnc}, '{name}', {supplier}, 'ACTIVE', 1, true, 'ACTIVE')";

    private static TestState Customer(Guid id) => new("Customer", id, null, "ACTIVE");

    private static string Order(TestHarness h, ReceiptTests.World w, Guid id, string no, Guid party, string cash, string buyer = "NULL, NULL, NULL, NULL") =>
        "INSERT INTO sal.sales_order (sales_order_id, company_id, order_no, party_id, plant_id, order_date, delivery_term_code, price_list_version_id, status, total_net, lines_version, " +
        $"created_by, version, cash_sale, buyer_name, buyer_phone, buyer_id_kind, buyer_id) VALUES ('{id}', '{h.CompanyId}', '{no}', '{party}', '{w.S.Plant}', current_date, 'PICKUP_AT_PLANT', " +
        $"(SELECT price_list_version_id FROM sal.price_list_version WHERE status = 'ACTIVE'), 'DRAFT', 5000.00, 1, @user, 1, {cash}, {buyer})";

    private static TestState State(Guid order, string? from, string to) => new("SalesOrder", order, from, to);

    [Fact]
    public async Task The_final_consumer_is_one_party_without_RNC_never_a_supplier_never_renamed_and_without_terms_or_emails()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await ReceiptTests.WorldAsync(h, 100m);
        var consumer = Guid.CreateVersion7();

        var withRnc = await Fails(h, "rnc", Consumer(h, Guid.CreateVersion7(), "'40212345678'"), Customer(Guid.Empty));
        var asSupplier = await Fails(h, "supplier", Consumer(h, Guid.CreateVersion7(), supplier: "true"), Customer(Guid.Empty));
        await Run(h, "consumer", Consumer(h, consumer), Customer(consumer));
        var second = Guid.CreateVersion7();
        var twice = await Fails(h, "second", Consumer(h, second), Customer(second));
        var renamed = await Fails(h, "rename", $"UPDATE md.party SET legal_name = 'Otro', version = 2 WHERE party_id = '{consumer}'");
        var becomes = await Fails(h, "becomes", $"UPDATE md.party SET party_kind = 'CONSUMER', version = version + 1 WHERE party_id = '{w.S.Customer}'");
        var contact = await Fails(h, "contact", $"UPDATE md.party SET phone = '809-555-0101', version = 2 WHERE party_id = '{consumer}'");
        var email = await Fails(h, "email", $"INSERT INTO md.party_email (company_id, party_id, position, email) VALUES ('{h.CompanyId}', '{consumer}', 1, 'a@b.do')");
        var terms = await Record.ExceptionAsync(() => h.RunAsync(new PrepareCustomerTerms(h.CompanyId, w.S.Credit, "terms", consumer, 30, 1000.00m, false), new PrepareCustomerTermsHandler()));

        Assert.Equal(("23514", "23514", "23505"), (withRnc, asSupplier, twice));
        Assert.Equal(("P0001", "23514", "P0001"), (renamed, contact, email));
        Assert.NotNull(becomes); // not a column the application role may write, and the guard refuses it for the owner too
        Assert.Contains("the final consumer has no credit terms", Assert.IsType<PostgresException>(terms).MessageText, StringComparison.Ordinal);
        Assert.Contains("does not become", (await h.AdminExecuteAsync($"UPDATE md.party SET party_kind = 'CONSUMER', rnc = NULL, version = version + 1 WHERE party_id = '{w.S.Customer}'"))?.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cash_order_is_the_consumers_carries_its_buyer_and_is_confirmed_only_when_paid_in_full()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await ReceiptTests.WorldAsync(h, 100m);
        var consumer = Guid.CreateVersion7();
        await Run(h, "consumer", Consumer(h, consumer), Customer(consumer));
        var order = Guid.CreateVersion7();
        const string Buyer = "'María Pérez', '809-555-0101', 'CEDULA', '40212345678'";

        // Only the consumer's order is a cash sale, and only a cash sale names a buyer; the identification has the format of its kind.
        var creditForConsumer = await Fails(h, "o1", Order(h, w, Guid.CreateVersion7(), "PV-000901", consumer, "false"), State(Guid.Empty, null, "DRAFT"));
        var cashForCustomer = await Fails(h, "o2", Order(h, w, Guid.CreateVersion7(), "PV-000902", w.S.Customer, "true"), State(Guid.Empty, null, "DRAFT"));
        var buyerOnCredit = await Fails(h, "o3", Order(h, w, Guid.CreateVersion7(), "PV-000903", w.S.Customer, "false", "'Juan', NULL, NULL, NULL"), State(Guid.Empty, null, "DRAFT"));
        var shortCedula = await Fails(h, "o4", Order(h, w, Guid.CreateVersion7(), "PV-000904", consumer, "true", "NULL, NULL, 'CEDULA', '4021234567'"), State(Guid.Empty, null, "DRAFT"));
        var kindWithoutId = await Fails(h, "o5", Order(h, w, Guid.CreateVersion7(), "PV-000905", consumer, "true", "NULL, NULL, 'PASAPORTE', NULL"), State(Guid.Empty, null, "DRAFT"));
        await Run(h, "order", Order(h, w, order, "PV-000906", consumer, "true", Buyer), State(order, null, "DRAFT"));

        // Never through credit, never with an exemption in process; sent to payment with what must be paid (5,000.00 + 900.00).
        var toCredit = await Fails(h, "credit", $"UPDATE sal.sales_order SET status = 'PENDING_CREDIT', version = 2 WHERE sales_order_id = '{order}'", State(order, "DRAFT", "PENDING_CREDIT"));
        var exemption = await Fails(h, "exempt", $"UPDATE sal.sales_order SET exemption_pending = true, proforma_collects_itbis = true, version = 2 WHERE sales_order_id = '{order}'");
        var noTotal = await Fails(h, "no-total", $"UPDATE sal.sales_order SET status = 'PENDING_PAYMENT', version = 2 WHERE sales_order_id = '{order}'", State(order, "DRAFT", "PENDING_PAYMENT"));
        var straight = await Fails(h, "straight", $"UPDATE sal.sales_order SET status = 'CONFIRMED', payment_total = 5900.00, version = 2 WHERE sales_order_id = '{order}'", State(order, "DRAFT", "CONFIRMED"));
        await Run(h, "pay", $"UPDATE sal.sales_order SET status = 'PENDING_PAYMENT', payment_total = 5900.00, version = 2 WHERE sales_order_id = '{order}'", State(order, "DRAFT", "PENDING_PAYMENT"));
        var newBuyer = await Fails(h, "buyer", $"UPDATE sal.sales_order SET buyer_name = 'Otro', version = 3 WHERE sales_order_id = '{order}'");
        var newTotal = await Fails(h, "total", $"UPDATE sal.sales_order SET payment_total = 5000.00, version = 3 WHERE sales_order_id = '{order}'");
        var unpaid = await Fails(h, "unpaid", $"UPDATE sal.sales_order SET status = 'CONFIRMED', version = 3 WHERE sales_order_id = '{order}'", State(order, "PENDING_PAYMENT", "CONFIRMED"));

        // A receipt of the consumer is assigned to the order; another customer's is not, nor is one assigned to a credit order.
        var receipt = (await h.RunAsync(new RecordReceipt(h.CompanyId, w.Cobros, "r", consumer, "CASH", 5900.00m, ReceiptTests.Today(h), null, null), new RecordReceiptHandler())).ResultRef;
        var other = (await ReceiptTests.Transfer(h, w, "r-other", 5900.00m)).ResultRef;
        var creditOrder = await h.ScalarAsync<Guid>("SELECT sales_order_id FROM sal.sales_order WHERE party_id = @p LIMIT 1", ("p", w.S.Customer));
        string Allocation(Guid id, Guid ofReceipt, Guid toOrder, string amount, string reverses = "NULL") =>
            $"INSERT INTO fin.order_allocation VALUES ('{id}', '{h.CompanyId}', '{ofReceipt}', '{toOrder}', {amount}, (SELECT event_id FROM core.domain_event ORDER BY recorded_at DESC LIMIT 1), {reverses})";
        var foreign = await Fails(h, "a-foreign", Allocation(Guid.CreateVersion7(), other, order, "5900.00"));
        var notCash = await Fails(h, "a-credit", Allocation(Guid.CreateVersion7(), other, creditOrder, "100.00"));
        var allocation = Guid.CreateVersion7();
        await Run(h, "allocate", Allocation(allocation, receipt, order, "5000.00")
            + $"; UPDATE sal.sales_order SET allocated_amount = 5000.00, version = 3 WHERE sales_order_id = '{order}'; UPDATE fin.receipt SET allocated_amount = 5000.00, version = version + 1 WHERE receipt_id = '{receipt}'");
        var partly = await Fails(h, "partly", $"UPDATE sal.sales_order SET status = 'CONFIRMED', version = 4 WHERE sales_order_id = '{order}'", State(order, "PENDING_PAYMENT", "CONFIRMED"));
        var wrongRelease = await Fails(h, "a-wrong", Allocation(Guid.CreateVersion7(), receipt, order, "4000.00", $"'{allocation}'"));
        var changed = await Fails(h, "a-change", $"UPDATE fin.order_allocation SET amount = 1 WHERE allocation_id = '{allocation}'");
        var cancelPaid = await Fails(h, "cancel", $"UPDATE sal.sales_order SET status = 'CANCELLED', cancel_reason = 'x', version = 4 WHERE sales_order_id = '{order}'", State(order, "PENDING_PAYMENT", "CANCELLED"));
        await Run(h, "allocate-2", Allocation(Guid.CreateVersion7(), receipt, order, "900.00")
            + $"; UPDATE sal.sales_order SET allocated_amount = 5900.00, version = 4 WHERE sales_order_id = '{order}'; UPDATE fin.receipt SET allocated_amount = 5900.00, version = version + 1 WHERE receipt_id = '{receipt}'");
        await Run(h, "confirm", $"UPDATE sal.sales_order SET status = 'CONFIRMED', version = 5 WHERE sales_order_id = '{order}'", State(order, "PENDING_PAYMENT", "CONFIRMED"));

        Assert.Equal(("P0001", "P0001", "23514", "23514", "23514"), (creditForConsumer, cashForCustomer, buyerOnCredit, shortCedula, kindWithoutId));
        Assert.Equal(("23514", "23514", "23514", "P0001"), (toCredit, exemption, noTotal, straight));
        Assert.Equal(("P0001", "P0001", "P0001"), (newBuyer, newTotal, unpaid));
        Assert.Equal(("P0001", "P0001", "P0001", "P0001", "42501", "P0001"), (foreign, notCash, partly, wrongRelease, changed, cancelPaid)); // 42501: no UPDATE privilege
        Assert.Equal("CONFIRMED:5900.0000:5900.0000:María Pérez:CEDULA:40212345678", await h.ScalarAsync<string>(
            $"SELECT status || ':' || payment_total || ':' || allocated_amount || ':' || buyer_name || ':' || buyer_id_kind || ':' || buyer_id FROM sal.sales_order WHERE sales_order_id = '{order}'"));
    }

    [Fact]
    public async Task Only_the_consumers_eCF_32_is_recorded_without_a_receiver_and_the_identification_threshold_is_a_fiscal_rule_kind()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await ReceiptTests.WorldAsync(h, 100m);
        string Record(Guid invoice, string encf, string receiver, string passport = "NULL") =>
            "INSERT INTO tax.external_fiscal_record (record_id, company_id, invoice_id, encf, issued_at, security_code, evidence_ref, evidence_sha256, receiver_rnc, net_total, tax_total, total, " +
            $"recorded_by, event_id, receiver_passport) VALUES ('{Guid.CreateVersion7()}', '{h.CompanyId}', '{invoice}', '{encf}', now(), 'A1', 'e.xml', decode(repeat('ab', 32), 'hex'), {receiver}, " +
            $"5000.00, 900.00, 5900.00, @user, (SELECT event_id FROM core.domain_event ORDER BY recorded_at DESC LIMIT 1), {passport})";

        // FA-000001 is an e-CF 31 of a customer with RNC: it always names its receiver.
        var noReceiver = await Fails(h, "none", Record(w.Invoice, "E310000000001", "NULL"));
        var both = await Fails(h, "both", Record(w.Invoice, "E310000000001", "'131925332'", "'AB123456'"));
        var kinds = await h.ScalarAsync<string>("SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'fiscal_rule_kind'");
        var buyerOn31 = await h.AdminExecuteAsync($"UPDATE sal.invoice SET buyer_name = 'Juan' WHERE invoice_id = '{w.Invoice}'");
        var caja = await h.ScalarAsync<string>(
            "SELECT string_agg(rp.permission_code, ',' ORDER BY rp.permission_code) FROM iam.role r JOIN iam.role_permission rp USING (role_id) WHERE r.code = 'CAJA'");

        Assert.Equal(("P0001", "23514"), (noReceiver, both));
        Assert.Contains("'CONSUMER_ID_THRESHOLD'", kinds, StringComparison.Ordinal);
        Assert.NotNull(buyerOn31);
        Assert.Equal("cash_sale:create,receipt:apply,receipt:record,sales:read", caja);
    }
}
