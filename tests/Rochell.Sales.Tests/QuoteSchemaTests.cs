using Npgsql;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Sales.Tests;

/// <summary>
/// QUO1-01 schema guarantees (Frozen Baseline QUO-1 §2, §4; E-QUO1-01-1…12), written as the application role through a fixture
/// command (the commands arrive in QUO1-02 and QUO1-03).
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class QuoteSchemaTests(PostgresFixture postgres)
{
    private static Task Run(TestHarness h, string key, string sql, params TestState[] states)
        => h.RunAsync(new TestTreasurySql(h.CompanyId, h.SessionId, key, sql, states), new TestTreasurySqlHandler());

    private static async Task<string?> Fails(TestHarness h, string key, string sql, params TestState[] states)
    {
        var ex = await Record.ExceptionAsync(() => Run(h, key, sql, states));
        return ex is PostgresException pg ? pg.SqlState : ex?.GetType().Name;
    }

    private sealed record World(DeliveryTests.Setup S, Guid PriceList, Guid Sand);

    private static async Task<World> WorldAsync(TestHarness h)
    {
        var s = await DeliveryTests.SetupAsync(h);
        var priceList = await h.ScalarAsync<Guid>("SELECT price_list_version_id FROM sal.price_list_version WHERE company_id = @c AND status = 'ACTIVE'", ("c", h.CompanyId));
        return new World(s, priceList, await h.CreateActiveItemAsync("ARENA", "t", "AGREGADO"));
    }

    private static string Quote(TestHarness h, World w, Guid id, string no, string validUntil = "current_date + 30", string total = "50000.00") =>
        $"INSERT INTO sal.quote (quote_id, company_id, quote_no, party_id, plant_id, quote_date, valid_until, delivery_term_code, price_list_version_id, status, total_net, " +
        $"lines_version, created_by, version) VALUES ('{id}', '{h.CompanyId}', '{no}', '{w.S.Customer}', '{w.S.Plant}', current_date, {validUntil}, 'PICKUP_AT_PLANT', " +
        $"'{w.PriceList}', 'DRAFT', {total}, 1, @user, 1)";

    private static string Line(TestHarness h, Guid quote, int no, Guid item, string unitPrice = "50.00", int linesVersion = 1) =>
        $"INSERT INTO sal.quote_line VALUES ('{Guid.CreateVersion7()}', '{h.CompanyId}', '{quote}', {linesVersion}, {no}, '{item}', 'un', 1000, 50.00, {unitPrice}, 1000 * {unitPrice})";

    private static TestState State(Guid quote, string? from, string to) => new("Quote", quote, from, to);

    [Fact]
    public async Task A_price_below_the_list_is_sent_only_after_another_person_approves_the_current_lines()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var approver = await h.CreateUserAsync();
        var quote = Guid.CreateVersion7();

        var badNo = await Fails(h, "no", Quote(h, w, Guid.CreateVersion7(), "Q-1"), State(Guid.Empty, null, "DRAFT"));
        var expiredBeforeIssued = await Fails(h, "valid", Quote(h, w, Guid.CreateVersion7(), "COT-000009", "current_date - 1"), State(Guid.Empty, null, "DRAFT"));
        await Run(h, "quote", Quote(h, w, quote, "COT-000001", total: "45000.00") + "; " + Line(h, quote, 1, w.S.Block, "45.00"), State(quote, null, "DRAFT"));
        var rawMaterial = await Fails(h, "raw", Line(h, quote, 2, w.Sand));
        var unapproved = await Fails(h, "send-0", $"UPDATE sal.quote SET status = 'SENT', version = 2 WHERE quote_id = '{quote}'", State(quote, "DRAFT", "SENT"));
        var approvedInDraft = await Fails(h, "approve-0",
            $"UPDATE sal.quote SET price_approved_by = '{approver}', price_approved_at = now(), approved_lines_version = 1, version = 2 WHERE quote_id = '{quote}'");
        await Run(h, "submit", $"UPDATE sal.quote SET status = 'PENDING_APPROVAL', version = 2 WHERE quote_id = '{quote}'", State(quote, "DRAFT", "PENDING_APPROVAL"));
        var selfApproved = await Fails(h, "self",
            $"UPDATE sal.quote SET status = 'DRAFT', price_approved_by = @user, price_approved_at = now(), approved_lines_version = 1, version = 3 WHERE quote_id = '{quote}'",
            State(quote, "PENDING_APPROVAL", "DRAFT"));
        await Run(h, "approve",
            $"UPDATE sal.quote SET status = 'DRAFT', price_approved_by = '{approver}', price_approved_at = now(), approved_lines_version = 1, version = 3 WHERE quote_id = '{quote}'",
            State(quote, "PENDING_APPROVAL", "DRAFT"));
        await Run(h, "send", $"UPDATE sal.quote SET status = 'SENT', version = 4 WHERE quote_id = '{quote}'", State(quote, "DRAFT", "SENT"));
        var edited = await Fails(h, "edit", $"UPDATE sal.quote SET valid_until = current_date + 60, version = 5 WHERE quote_id = '{quote}'");
        var lateLine = await Fails(h, "late", Line(h, quote, 2, w.S.Block, linesVersion: 2));
        var changedLine = await Fails(h, "line", $"UPDATE sal.quote_line SET unit_price = 40.00 WHERE quote_id = '{quote}'");

        Assert.Equal(("23514", "23514", "P0001", "P0001", "P0001"), (badNo, expiredBeforeIssued, rawMaterial, unapproved, approvedInDraft));
        Assert.Equal(("23514", "P0001", "P0001", "42501"), (selfApproved, edited, lateLine, changedLine));
        Assert.Equal("SENT:1", await h.ScalarAsync<string>($"SELECT status || ':' || approved_lines_version FROM sal.quote WHERE quote_id = '{quote}'"));
    }

    [Fact]
    public async Task Edited_lines_need_a_new_approval_and_a_quote_closes_with_a_reason_or_an_order()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var approver = await h.CreateUserAsync();
        var quote = Guid.CreateVersion7();
        await Run(h, "quote", Quote(h, w, quote, "COT-000001", total: "45000.00") + "; " + Line(h, quote, 1, w.S.Block, "45.00"), State(quote, null, "DRAFT"));
        await Run(h, "submit", $"UPDATE sal.quote SET status = 'PENDING_APPROVAL', version = 2 WHERE quote_id = '{quote}'", State(quote, "DRAFT", "PENDING_APPROVAL"));
        await Run(h, "approve",
            $"UPDATE sal.quote SET status = 'DRAFT', price_approved_by = '{approver}', price_approved_at = now(), approved_lines_version = 1, version = 3 WHERE quote_id = '{quote}'",
            State(quote, "PENDING_APPROVAL", "DRAFT"));

        // New lines (version 2) at 44.00: the approval of version 1 no longer lets the quote go out.
        await Run(h, "edit", $"UPDATE sal.quote SET lines_version = 2, total_net = 44000.00, version = 4 WHERE quote_id = '{quote}'; " + Line(h, quote, 1, w.S.Block, "44.00", 2));
        var stale = await Fails(h, "send-0", $"UPDATE sal.quote SET status = 'SENT', version = 5 WHERE quote_id = '{quote}'", State(quote, "DRAFT", "SENT"));

        // A copy at the list price goes out without approval; it is lost only with a reason, and a converted quote names its order.
        var copy = Guid.CreateVersion7();
        await Run(h, "copy", Quote(h, w, copy, "COT-000002").Replace("lines_version, created_by, version)", "lines_version, created_by, copied_from_quote_id, version)", StringComparison.Ordinal)
            .Replace("1, @user, 1)", $"1, @user, '{quote}', 1)", StringComparison.Ordinal) + "; " + Line(h, copy, 1, w.S.Block), State(copy, null, "DRAFT"));
        await Run(h, "send", $"UPDATE sal.quote SET status = 'SENT', version = 2 WHERE quote_id = '{copy}'", State(copy, "DRAFT", "SENT"));
        var lostWithoutReason = await Fails(h, "lost-0", $"UPDATE sal.quote SET status = 'LOST', version = 3 WHERE quote_id = '{copy}'", State(copy, "SENT", "LOST"));
        var convertedWithoutOrder = await Fails(h, "conv-0", $"UPDATE sal.quote SET status = 'CONVERTED', version = 3 WHERE quote_id = '{copy}'", State(copy, "SENT", "CONVERTED"));
        await Run(h, "cancel", $"UPDATE sal.quote SET status = 'CANCELLED', closing_reason = 'Reemplazada por COT-000002', version = 5 WHERE quote_id = '{quote}'",
            State(quote, "DRAFT", "CANCELLED"));
        var reopened = await Fails(h, "reopen", $"UPDATE sal.quote SET status = 'DRAFT', closing_reason = NULL, version = 6 WHERE quote_id = '{quote}'",
            State(quote, "CANCELLED", "DRAFT"));
        var deleted = await Fails(h, "delete", $"DELETE FROM sal.quote WHERE quote_id = '{copy}'");

        Assert.Equal(("P0001", "23514", "23514", "P0001", "42501"), (stale, lostWithoutReason, convertedWithoutOrder, reopened, deleted));
        Assert.Equal($"{quote}", await h.ScalarAsync<string>($"SELECT copied_from_quote_id::text FROM sal.quote WHERE quote_id = '{copy}'"));
    }

    [Fact]
    public async Task A_quote_becomes_at_most_one_order_and_the_link_never_changes()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var w = await WorldAsync(h);
        var quote = Guid.CreateVersion7();
        await Run(h, "quote", Quote(h, w, quote, "COT-000001") + "; " + Line(h, quote, 1, w.S.Block), State(quote, null, "DRAFT"));
        await Run(h, "send", $"UPDATE sal.quote SET status = 'SENT', version = 2 WHERE quote_id = '{quote}'", State(quote, "DRAFT", "SENT"));
        string Order(Guid id, string no) =>
            $"INSERT INTO sal.sales_order (sales_order_id, company_id, order_no, party_id, plant_id, order_date, delivery_term_code, price_list_version_id, status, total_net, " +
            $"lines_version, created_by, version, quote_id) VALUES ('{id}', '{h.CompanyId}', '{no}', '{w.S.Customer}', '{w.S.Plant}', current_date, 'PICKUP_AT_PLANT', " +
            $"'{w.PriceList}', 'DRAFT', 50000.00, 1, @user, 1, '{quote}')";
        var order = Guid.CreateVersion7();

        await Run(h, "order", Order(order, "PV-000901") + $"; UPDATE sal.quote SET status = 'CONVERTED', sales_order_id = '{order}', version = 3 WHERE quote_id = '{quote}'",
            new TestState("SalesOrder", order, null, "DRAFT"), State(quote, "SENT", "CONVERTED"));
        var second = Guid.CreateVersion7();
        var twice = await Fails(h, "order-2", Order(second, "PV-000902"), new TestState("SalesOrder", second, null, "DRAFT"));
        var relinked = await Fails(h, "relink", $"UPDATE sal.sales_order SET quote_id = NULL, version = 2 WHERE sales_order_id = '{order}'");

        Assert.Equal(("23505", "42501"), (twice, relinked));
        Assert.Equal("CONVERTED:PV-000901", await h.ScalarAsync<string>(
            $"SELECT q.status || ':' || o.order_no FROM sal.quote q JOIN sal.sales_order o ON o.quote_id = q.quote_id WHERE q.quote_id = '{quote}'"));
    }
}
