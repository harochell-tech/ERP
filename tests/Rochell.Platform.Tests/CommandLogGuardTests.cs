using Npgsql;
using Rochell.Platform.Data;
using Rochell.Platform.Tests.Infrastructure;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Platform.Tests;

/// <summary>CMD-01 and CMD-02 (Frozen Baseline Patch 1, P-3), exercised as the application role with the tenant set.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class CommandLogGuardTests(PostgresFixture postgres)
{
    [Fact]
    public async Task CMD01_result_cannot_be_updated_after_commit()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var result = await h.Pipeline.ExecuteAsync(h.Ping("cmd-01"), new PingHandler(), Guid.CreateVersion7());

        var ex = await h.AppExecuteAsync(
            $"UPDATE core.command_log SET result_payload = '{{}}', committed_at = now() WHERE command_id = '{result.CommandId}'");

        Assert.Equal(SqlStates.RaiseException, ex?.SqlState);
        Assert.Contains("immutable once written", ex!.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CMD01_only_result_columns_may_be_written_even_inside_the_inserting_transaction()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (connection, tx) = await h.OpenAppTransactionAsync();
        await using (connection)
        await using (tx)
        {
            var commandId = Guid.CreateVersion7();
            await InsertCommandLogAsync(connection, tx, h, commandId, "cmd-01b");

            var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var update = new NpgsqlCommand("UPDATE core.command_log SET idempotency_key = 'other' WHERE command_id = @id", connection, tx);
                update.Parameters.AddWithValue("id", commandId);
                await update.ExecuteNonQueryAsync();
            });

            Assert.Equal("42501", ex.SqlState); // column privilege: only result_payload/committed_at are updatable by rochell_app
        }
    }

    [Fact]
    public async Task CMD02_commit_without_result_is_rejected()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var (connection, tx) = await h.OpenAppTransactionAsync();
        await using (connection)
        await using (tx)
        {
            await InsertCommandLogAsync(connection, tx, h, Guid.CreateVersion7(), "cmd-02");

            var ex = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());

            Assert.Contains("without its result", ex.MessageText, StringComparison.Ordinal);
        }

        Assert.Equal(0L, await h.CountAsync("core.command_log"));
    }

    [Theory]
    [InlineData("DELETE FROM core.command_log")]
    [InlineData("TRUNCATE core.command_log")]
    public async Task Command_log_cannot_be_deleted_by_the_application(string sql)
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.Pipeline.ExecuteAsync(h.Ping("del"), new PingHandler(), Guid.CreateVersion7());

        Assert.Equal("42501", (await h.AppExecuteAsync(sql))?.SqlState);
        Assert.Equal(1L, await h.CountAsync("core.command_log"));
    }

    [Fact]
    public async Task Command_log_requires_an_existing_session()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        var ex = await h.AdminExecuteAsync(
            $"INSERT INTO core.command_log (company_id, command_id, command_type, idempotency_key, session_id, result_ref, result_payload, committed_at) VALUES ('{h.CompanyId}', gen_random_uuid(), 'X', 'k', gen_random_uuid(), gen_random_uuid(), '{{}}', now())");

        Assert.Equal(SqlStates.ForeignKeyViolation, ex?.SqlState);
    }

    private static async Task InsertCommandLogAsync(NpgsqlConnection connection, NpgsqlTransaction tx, TestHarness h, Guid commandId, string key)
    {
        await using var insert = new NpgsqlCommand(
            "INSERT INTO core.command_log (company_id, command_id, command_type, idempotency_key, session_id, result_ref) VALUES (@c, @id, 'Test.Manual', @k, @s, @r)",
            connection,
            tx);
        insert.Parameters.AddWithValue("c", h.CompanyId);
        insert.Parameters.AddWithValue("id", commandId);
        insert.Parameters.AddWithValue("k", key);
        insert.Parameters.AddWithValue("s", h.SessionId);
        insert.Parameters.AddWithValue("r", Guid.CreateVersion7());
        await insert.ExecuteNonQueryAsync();
    }
}
