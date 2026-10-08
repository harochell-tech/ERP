using System.Text.Json;
using Rochell.Manufacturing.Runs;
using Rochell.Platform.Data;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Manufacturing.Tests;

/// <summary>
/// MFG2-01 (E-MFG2-01-1…8): what the database keeps of the machines' portal — readings and batch-plant posts never change; a
/// summary from the portal names its reading; a summary whose consumption is still pending (the recipe's theoretical) is never posted.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PortalSchemaTests(PostgresFixture postgres)
{
    private static string Reading(Guid id, Guid company) =>
        $$"""
        INSERT INTO mfg.portal_reading (reading_id, company_id, portal_code, shift_date, shift_no, window_from, window_to, closed, cycles, cycles_without_mould,
                                        maintenance_cycles, dead_minutes, first_cycle, last_cycle, moulds, content_sha256, fetched_at)
        VALUES ('{{id}}', '{{company}}', 'planta2', current_date, 1, current_date + time '08:00', current_date + time '17:00', false, 15, 0, 3, 5,
                current_date + time '09:00', current_date + time '13:04', '[{"molde":"6","bloques_por_ciclo":4,"ciclos":15,"bloques":60}]', decode(repeat('ab', 32), 'hex'), now())
        """;

    [Fact]
    public async Task Readings_and_batch_plant_posts_are_kept_and_never_changed()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var reading = Guid.CreateVersion7();
        var tx = await h.OpenAppTransactionAsync();
        await using (tx.Connection)
        {
            await Sql.ExecuteAsync(tx.Connection, tx.Transaction, Reading(reading, h.CompanyId), CancellationToken.None);
            await Sql.ExecuteAsync(
                tx.Connection,
                tx.Transaction,
                $$"""
                INSERT INTO mfg.portal_consumption (consumption_id, company_id, portal_id, batch_plant, shift_date, shift_no, batches, materials, received_at, fetched_at)
                VALUES ('{{Guid.CreateVersion7()}}', '{{h.CompanyId}}', 2, 'dosificadora2', current_date, 1, 42, '[{"codigo":"CEMENTO","cantidad":"5100","unidad":"kg"}]', now(), now())
                """,
                CancellationToken.None);
            await tx.Transaction.CommitAsync();
        }

        var update = await h.AppExecuteAsync("UPDATE mfg.portal_reading SET cycles = 99");
        var delete = await h.AppExecuteAsync("DELETE FROM mfg.portal_consumption");
        var twice = await h.AppExecuteAsync(
            $"INSERT INTO mfg.portal_consumption (consumption_id, company_id, portal_id, batch_plant, shift_date, shift_no, materials, received_at, fetched_at) VALUES ('{Guid.CreateVersion7()}', '{h.CompanyId}', 2, 'dosificadora2', current_date, 1, '[]', now(), now())");

        Assert.Equal("42501", update?.SqlState); // insufficient_privilege (and the append-only trigger behind it)
        Assert.Equal("42501", delete?.SqlState);
        Assert.Equal("23505", twice?.SqlState); // the portal's post id is taken once
    }

    [Fact]
    public async Task Pairings_take_valid_codes_and_one_portal_code_per_machine()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ProductionRunTests.SetupAsync(h, withCost: false);

        var first = await h.AppExecuteAsync($"INSERT INTO mfg.portal_machine VALUES ('{h.CompanyId}', 'planta2', '{s.Machine}', 'dosificadora2', 1)");
        var again = await h.AppExecuteAsync($"INSERT INTO mfg.portal_machine VALUES ('{h.CompanyId}', 'planta3', '{s.Machine}', NULL, 1)");
        var bad = await h.AppExecuteAsync($"INSERT INTO mfg.portal_mould VALUES ('{h.CompanyId}', '6\"', '{s.Block}', 1)");
        var material = await h.AppExecuteAsync($"INSERT INTO mfg.portal_material VALUES ('{h.CompanyId}', 'dosificadora2', 'cemento', '{s.Cement}', 'kg', '{s.Patio}', 1)");

        Assert.Null(first);
        Assert.Equal("23505", again?.SqlState);
        Assert.Equal("23514", bad?.SqlState);
        Assert.Equal("23514", material?.SqlState); // material codes are upper case, as the portal stores them
    }

    [Fact]
    public async Task A_summary_with_pending_consumption_is_never_posted_and_a_portal_summary_names_its_reading()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var s = await ProductionRunTests.SetupAsync(h);
        var run = (await ProductionRunTests.Start(h, s, "start")).ResultRef;
        var recorded = JsonDocument.Parse((await ProductionRunTests.Record(h, s, run, "rec")).ResultPayload).RootElement;

        var noReading = await h.AppExecuteAsync($"UPDATE mfg.shift_summary SET source = 'PORTAL', version = version + 1 WHERE run_id = '{run}'");
        var pending = await h.AppExecuteAsync($"UPDATE mfg.shift_summary SET consumption_source = 'PENDING', version = version + 1 WHERE run_id = '{run}'");
        var version = await h.ScalarAsync<long>("SELECT version FROM mfg.shift_summary WHERE run_id = @r", ("r", run));
        var post = await Record.ExceptionAsync(() => h.RunAsync(new PostShiftSummary(h.CompanyId, s.Manager, "post", s.Plant, run, version), new PostShiftSummaryHandler()));

        Assert.True(recorded.GetProperty("version").GetInt64() >= 1);
        Assert.Equal("42501", noReading?.SqlState); // source is written once, on insert
        Assert.Null(pending);
        Assert.Equal("23514", (post as Npgsql.PostgresException ?? post?.InnerException as Npgsql.PostgresException)?.SqlState);
        Assert.Equal("DRAFT", await h.ScalarAsync<string>("SELECT status FROM mfg.shift_summary WHERE run_id = @r", ("r", run)));
    }
}
