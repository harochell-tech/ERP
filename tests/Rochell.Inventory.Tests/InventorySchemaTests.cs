using Npgsql;
using Rochell.Platform.Data;
using Rochell.Platform.Time;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Inventory.Tests;

/// <summary>P-1 (value entry ↔ GL line at COMMIT), P-3 (subledger = GL), append-only ledgers, company-safe keys, RLS.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class InventorySchemaTests(PostgresFixture postgres)
{
    private const string InsufficientPrivilege = "42501";

    private static string Inv(decimal value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<(TestHarness H, TestStock S, Guid Lot, Guid EventId, Guid PeriodId)> ReceivedAsync(PostgresFixture postgres)
    {
        var h = await TestHarness.CreateAsync(postgres);
        var s = await h.CreateStockSetupAsync();
        var result = await h.RunAsync(
            new TestReceiveStock(h.CompanyId, h.SessionId, "seed", s.LocationA, s.ItemId, 10m, 100.00m, BusinessCalendar.DefaultBusinessDate(h.Clock.UtcNow)),
            new TestReceiveStockHandler());
        var lot = System.Text.Json.JsonDocument.Parse(result.ResultPayload).RootElement.GetProperty("lotId").GetGuid();
        var eventId = (await h.ScalarAsync<Guid>("SELECT source_event_id FROM inv.inv_value_entry LIMIT 1"));
        var period = (await h.ScalarAsync<Guid>("SELECT period_id FROM fin.gl_journal LIMIT 1"));
        return (h, s, lot, eventId, period);
    }

    /// <summary>Runs a raw transaction as the owner (who bypasses RLS and privileges): only the deferred triggers can stop it.</summary>
    private static async Task<PostgresException?> OwnerTransactionAsync(TestHarness h, string sql)
    {
        await using var connection = await h.Admin.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        try
        {
#pragma warning disable CA2100 // Test SQL.
            await using var command = new NpgsqlCommand(sql, connection, tx);
#pragma warning restore CA2100
            await command.ExecuteNonQueryAsync();
            await tx.CommitAsync();
            return null;
        }
        catch (PostgresException ex)
        {
            return ex;
        }
    }

    private static string ValueAndBalances(TestHarness h, TestStock s, Guid eventId, Guid valueEntry, decimal amount) =>
        $$"""
        INSERT INTO inv.inv_value_entry (value_entry_id, company_id, movement_type, valuation_area_id, plant_id, item_id, amount, source_event_id, occurred_at, recorded_at, business_date, posting_date, row_hash)
        SELECT '{{valueEntry}}', '{{h.CompanyId}}', 'VALUATION_ADJUSTMENT', valuation_area_id, plant_id, '{{s.ItemId}}', {{Inv(amount)}}, '{{eventId}}', now(), now(), (SELECT posting_date FROM fin.gl_journal ORDER BY posting_date LIMIT 1), (SELECT posting_date FROM fin.gl_journal ORDER BY posting_date LIMIT 1), sha256('v')
        FROM md.plant WHERE plant_id = '{{s.PlantId}}';
        UPDATE inv.inv_valuation_balance SET value = value + {{Inv(amount)}} WHERE item_id = '{{s.ItemId}}';
        """;

    private static string Journal(TestHarness h, Guid eventId, Guid periodId, Guid journal) =>
        $"""
        INSERT INTO fin.gl_journal (journal_id, company_id, posting_date, period_id, source_event_id, posting_rule_id, posting_rule_version, posting_generation, journal_type, occurred_at, row_hash)
        SELECT '{journal}', '{h.CompanyId}', posting_date, '{periodId}', '{eventId}', '0192f000-0000-7000-8000-0000000000f1', 1, 7, 'AUTO', now(), sha256('j') FROM fin.gl_journal WHERE period_id = '{periodId}' ORDER BY posting_date LIMIT 1;
        """;

    private static string Line(TestHarness h, TestStock s, Guid eventId, Guid journal, int lineNo, decimal debit, decimal credit, Guid? valueEntry) =>
        valueEntry is null
            ? $$"""
              INSERT INTO fin.gl_entry (gl_entry_id, journal_id, line_no, company_id, posting_date, account_id, account_role, debit, credit, source_event_id, rule_line_code, determination_inputs, row_hash)
              VALUES (gen_random_uuid(), '{{journal}}', {{lineNo}}, '{{h.CompanyId}}', (SELECT posting_date FROM fin.gl_journal WHERE journal_id = '{{journal}}'), '{{s.Ledger.IncomeAccount}}', 'TEST_INCOME', {{Inv(debit)}}, {{Inv(credit)}}, '{{eventId}}', 'x', '{}', sha256('e'));
              """
            : $$"""
              INSERT INTO fin.gl_entry (gl_entry_id, journal_id, line_no, company_id, posting_date, account_id, account_role, debit, credit, plant_id, item_id, subledger_type, subledger_ref, inv_value_entry_id, source_event_id, rule_line_code, determination_inputs, row_hash)
              VALUES (gen_random_uuid(), '{{journal}}', {{lineNo}}, '{{h.CompanyId}}', (SELECT posting_date FROM fin.gl_journal WHERE journal_id = '{{journal}}'), '{{s.RawMaterialAccount}}', 'RAW_MATERIAL', {{Inv(debit)}}, {{Inv(credit)}}, '{{s.PlantId}}', '{{s.ItemId}}', 'INV', '{{valueEntry}}', '{{valueEntry}}', '{{eventId}}', 'x', '{}', sha256('e'));
              """;

    [Trait("Acceptance", "VAL-02")]
    [Fact]
    public async Task P1_value_entry_without_its_GL_line_fails_at_commit()
    {
        var (h, s, _, eventId, _) = await ReceivedAsync(postgres);
        await using (h)
        {
            var ex = await OwnerTransactionAsync(h, ValueAndBalances(h, s, eventId, Guid.CreateVersion7(), 5m));

            // The deferred P-1 trigger raises at COMMIT and nothing of the transaction remains.
            Assert.Equal(SqlStates.RaiseException, ex?.SqlState);
            Assert.Contains("needs exactly one GL line", ex?.MessageText, StringComparison.Ordinal);
            Assert.Equal(1L, await h.CountAsync("inv.inv_value_entry"));
        }
    }

    [Fact]
    public async Task P1_GL_line_with_a_different_amount_fails_at_commit()
    {
        var (h, s, _, eventId, periodId) = await ReceivedAsync(postgres);
        await using (h)
        {
            var valueEntry = Guid.CreateVersion7();
            var journal = Guid.CreateVersion7();
            var ex = await OwnerTransactionAsync(
                h,
                ValueAndBalances(h, s, eventId, valueEntry, 5m) + Journal(h, eventId, periodId, journal)
                + Line(h, s, eventId, journal, 1, 4.99m, 0, valueEntry) + Line(h, s, eventId, journal, 2, 0, 4.99m, null));

            Assert.NotNull(ex);
            Assert.Equal(1L, await h.CountAsync("inv.inv_value_entry"));
        }
    }

    [Fact]
    public async Task P1_P3_matching_value_entry_GL_line_and_balance_commit()
    {
        var (h, s, _, eventId, periodId) = await ReceivedAsync(postgres);
        await using (h)
        {
            var valueEntry = Guid.CreateVersion7();
            var journal = Guid.CreateVersion7();
            var ex = await OwnerTransactionAsync(
                h,
                ValueAndBalances(h, s, eventId, valueEntry, 5m) + Journal(h, eventId, periodId, journal)
                + Line(h, s, eventId, journal, 1, 5m, 0, valueEntry) + Line(h, s, eventId, journal, 2, 0, 5m, null));

            Assert.Null(ex);
            Assert.Equal(105.00m, await h.ScalarAsync<decimal>("SELECT value FROM inv.inv_valuation_balance"));
        }
    }

    [Fact]
    public async Task P3_valuation_balance_out_of_sync_with_its_ledger_fails_at_commit()
    {
        var (h, s, _, eventId, periodId) = await ReceivedAsync(postgres);
        await using (h)
        {
            var valueEntry = Guid.CreateVersion7();
            var journal = Guid.CreateVersion7();
            var sql = ValueAndBalances(h, s, eventId, valueEntry, 5m).Replace("SET value = value + 5", "SET value = value + 6", StringComparison.Ordinal)
                + Journal(h, eventId, periodId, journal) + Line(h, s, eventId, journal, 1, 5m, 0, valueEntry) + Line(h, s, eventId, journal, 2, 0, 5m, null);

            var ex = await OwnerTransactionAsync(h, sql);

            Assert.Contains("out of balance", ex!.MessageText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Inventory_GL_lines_must_reference_their_value_entry()
    {
        var (h, s, _, eventId, periodId) = await ReceivedAsync(postgres);
        await using (h)
        {
            var journal = Guid.CreateVersion7();
            var ex = await OwnerTransactionAsync(
                h,
                Journal(h, eventId, periodId, journal)
                + $"""
                  INSERT INTO fin.gl_entry (gl_entry_id, journal_id, line_no, company_id, posting_date, account_id, account_role, debit, credit, plant_id, item_id, subledger_type, subledger_ref, source_event_id, rule_line_code, determination_inputs, row_hash)
                  VALUES (gen_random_uuid(), '{journal}', 1, '{h.CompanyId}', (SELECT posting_date FROM fin.gl_journal WHERE journal_id = '{journal}'), '{s.RawMaterialAccount}', 'RAW_MATERIAL', 1, 0, '{s.PlantId}', '{s.ItemId}', 'INV', gen_random_uuid(), '{eventId}', 'x', '{"{}"}', sha256('e'));
                  """);

            Assert.Equal(SqlStates.CheckViolation, ex?.SqlState);
        }
    }

    [Theory]
    [InlineData("UPDATE inv.inv_quantity_entry SET quantity = quantity")]
    [InlineData("DELETE FROM inv.inv_value_entry")]
    [InlineData("TRUNCATE inv.inv_quantity_entry CASCADE")]
    [InlineData("UPDATE inv.lot SET lot_code = 'X'")]
    public async Task Ledgers_and_lots_are_append_only_even_for_the_owner(string sql)
    {
        var (h, _, _, _, _) = await ReceivedAsync(postgres);
        await using (h)
        {
            Assert.Equal(SqlStates.RaiseException, (await h.AdminExecuteAsync(sql))?.SqlState);
        }
    }

    [Theory]
    [InlineData("UPDATE inv.inv_value_entry SET amount = amount")]
    [InlineData("DELETE FROM inv.inv_stock_balance")]
    [InlineData("UPDATE inv.inv_stock_balance SET lot_id = lot_id")]
    [InlineData("UPDATE inv.inv_valuation_balance SET item_id = item_id")]
    public async Task Application_role_cannot_bypass_the_ledger(string sql)
    {
        var (h, _, _, _, _) = await ReceivedAsync(postgres);
        await using (h)
        {
            Assert.Equal(InsufficientPrivilege, (await h.AppExecuteAsync(sql))?.SqlState);
        }
    }

    [Fact]
    public async Task Stock_balance_cannot_go_negative_even_for_the_owner()
    {
        var (h, _, _, _, _) = await ReceivedAsync(postgres);
        await using (h)
        {
            Assert.Equal(SqlStates.CheckViolation, (await h.AdminExecuteAsync("UPDATE inv.inv_stock_balance SET quantity = quantity - 10.000001"))?.SqlState);
        }
    }

    [Fact]
    public async Task Entries_cannot_mix_lots_items_locations_or_plants()
    {
        var (h, s, lot, eventId, _) = await ReceivedAsync(postgres);
        await using (h)
        {
            var otherItem = await h.CreateActiveItemAsync("CEMENTO-GRIS", "t", "CEMENTO");
            var otherPlant = await h.CreatePlantAsync();
            var foreignLocation = await h.CreateLocationAsync(otherPlant, "OTRA");
            string Entry(Guid plant, Guid location, Guid item) =>
                $"INSERT INTO inv.inv_quantity_entry VALUES (gen_random_uuid(), '{h.CompanyId}', 'RECEIPT', '{plant}', '{location}', '{item}', '{lot}', 1, '{eventId}', 'T', gen_random_uuid(), NULL, NULL, now(), now(), (SELECT posting_date FROM fin.gl_journal LIMIT 1), (SELECT posting_date FROM fin.gl_journal LIMIT 1), sha256('q'))";

            var wrongItem = await OwnerTransactionAsync(h, Entry(s.PlantId, s.LocationA, otherItem));
            var wrongPlant = await OwnerTransactionAsync(h, Entry(s.PlantId, foreignLocation, s.ItemId));

            Assert.Equal(SqlStates.ForeignKeyViolation, wrongItem?.SqlState);
            Assert.Equal(SqlStates.ForeignKeyViolation, wrongPlant?.SqlState);
        }
    }

    [Fact]
    public async Task Row_level_security_isolates_inventory()
    {
        var (h, _, _, _, _) = await ReceivedAsync(postgres);
        await using (h)
        {
            var other = await h.CreateCompanyAsync();
            var (connection, tx) = await h.OpenAppTransactionAsync(other);
            await using (connection)
            await using (tx)
            {
                await using var count = new NpgsqlCommand(
                    "SELECT (SELECT count(*) FROM inv.lot) + (SELECT count(*) FROM inv.inv_quantity_entry) + (SELECT count(*) FROM inv.inv_value_entry) + (SELECT count(*) FROM inv.inv_stock_balance) + (SELECT count(*) FROM inv.inv_valuation_balance)",
                    connection,
                    tx);
                Assert.Equal(0L, await count.ExecuteScalarAsync());
            }
        }
    }
}
