using Npgsql;
using Rochell.Platform.Hashing;
using Rochell.Platform.Tests.Infrastructure;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Platform.Tests;

/// <summary>ID-06 (Errata E-2) and row hash determinism from the stored row.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class DomainEventTests(PostgresFixture postgres)
{
    [Fact]
    public async Task ID06_several_events_per_version_with_deterministic_order()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        var result = await h.Pipeline.ExecuteAsync(h.Ping("id-06", sideEvents: 1), new PingHandler(), Guid.CreateVersion7());

        await using var command = h.Admin.CreateCommand(
            "SELECT aggregate_type, aggregate_version, event_sequence, command_event_index, event_type FROM core.domain_event WHERE command_id = @id ORDER BY command_event_index");
        command.Parameters.AddWithValue("id", result.CommandId);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add($"{reader.GetString(0)}/v{reader.GetInt64(1)}/s{reader.GetInt16(2)}/i{reader.GetInt32(3)}/{reader.GetString(4)}");
        }

        Assert.Equal(["Ping/v1/s1/i1/Pinged", "Ping/v1/s2/i2/PingEchoed", "PingSide/v1/s1/i3/PingSide"], rows);
    }

    [Fact]
    public async Task Row_hash_recomputed_from_stored_row_matches()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.Pipeline.ExecuteAsync(h.Ping("hash", message: "Bloque 6\" – ñ", sideEvents: 2), new PingHandler(), Guid.CreateVersion7());

        await using var command = h.Admin.CreateCommand(
            """
            SELECT event_id, company_id, command_id, command_event_index, event_type, schema_version, aggregate_type,
                   aggregate_id, aggregate_version, event_sequence, occurred_at, recorded_at, business_date, session_id,
                   correlation_id, causation_id, payload::text, row_hash
            FROM core.domain_event
            """);
        await using var reader = await command.ExecuteReaderAsync();
        var checkedRows = 0;
        while (await reader.ReadAsync())
        {
            var row = new DomainEventRow(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetInt32(3), reader.GetString(4), reader.GetInt32(5),
                reader.GetString(6), reader.GetGuid(7), reader.GetInt64(8), reader.GetInt16(9),
                reader.GetFieldValue<DateTime>(10), reader.GetFieldValue<DateTime>(11), reader.GetFieldValue<DateOnly>(12),
                reader.GetGuid(13), reader.GetGuid(14), reader.IsDBNull(15) ? null : reader.GetGuid(15), reader.GetString(16));

            Assert.Equal(reader.GetFieldValue<byte[]>(17), row.ComputeRowHash());
            checkedRows++;
        }

        Assert.Equal(4, checkedRows);
    }

    [Fact]
    public async Task Business_date_defaults_to_dominican_calendar_date()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var occurredAt = new DateTime(2026, 9, 23, 3, 30, 0, DateTimeKind.Utc); // 23:30 on 2026-09-22 in Santo Domingo

        var result = await h.Pipeline.ExecuteAsync(h.Ping("bdate", occurredAt: occurredAt), new PingHandler(), Guid.CreateVersion7());

        Assert.Equal("2026-09-22", await h.ScalarAsync<string>(
            "SELECT business_date::text FROM core.domain_event WHERE command_id = @id AND command_event_index = 1", ("id", result.CommandId)));
    }

    [Fact]
    public async Task Decimal_json_numbers_in_payload_are_rejected()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => h.Pipeline.ExecuteAsync(
            h.Ping("decimal"), new DecimalPayloadHandler(), Guid.CreateVersion7()));

        Assert.Equal(0L, await h.CountAsync("core.domain_event"));
    }

    [Rochell.Platform.Commands.RequiresPermission("test:ping")]
    private sealed class DecimalPayloadHandler : Rochell.Platform.Commands.ICommandHandler<PingCommand>
    {
        public string CommandType => "Test.DecimalPayload";

        public async Task<string> HandleAsync(PingCommand command, Rochell.Platform.Commands.CommandContext context, CancellationToken cancellationToken)
        {
            await context.AppendEventAsync(new Rochell.Platform.Commands.EventDraft("Bad", 1, "Ping", context.ResultRef, 1, "{\"amount\":1250.5}", Publish: false), cancellationToken);
            return "{}";
        }
    }
}
