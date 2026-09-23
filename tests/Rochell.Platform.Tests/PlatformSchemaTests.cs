using Rochell.Platform.Data;
using Rochell.Platform.Tests.Infrastructure;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Platform.Tests;

/// <summary>Append-only tables, application role privileges and company-safe foreign keys (Patch 1, P-5).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PlatformSchemaTests(PostgresFixture postgres)
{
    private const string InsufficientPrivilege = "42501";

    [Theory]
    [InlineData("core.domain_event")]
    [InlineData("core.inbox")]
    [InlineData("core.state_history")]
    [InlineData("core.document_link")]
    public async Task Append_only_tables_reject_update_and_delete_even_for_the_owner(string table)
    {
        await using var h = await PlatformHarness.CreateAsync(postgres);
        await SeedAllTablesAsync(h);

        Assert.Equal(SqlStates.RaiseException, (await h.AdminExecuteAsync($"UPDATE {table} SET company_id = company_id"))?.SqlState);
        Assert.Equal(SqlStates.RaiseException, (await h.AdminExecuteAsync($"DELETE FROM {table}"))?.SqlState);
        Assert.Equal(SqlStates.RaiseException, (await h.AdminExecuteAsync($"TRUNCATE {table} CASCADE"))?.SqlState);
    }

    [Theory]
    [InlineData("DELETE FROM core.domain_event")]
    [InlineData("DELETE FROM core.outbox")]
    [InlineData("DELETE FROM core.inbox")]
    [InlineData("DELETE FROM obs.request_log")]
    [InlineData("UPDATE core.domain_event SET payload = '{}'")]
    [InlineData("UPDATE core.outbox SET event_id = event_id")]
    [InlineData("UPDATE obs.request_log SET outcome = outcome")]
    [InlineData("INSERT INTO md.company VALUES (gen_random_uuid(), '999', 'x')")]
    public async Task Application_role_has_no_privileges_beyond_the_baseline(string sql)
    {
        await using var h = await PlatformHarness.CreateAsync(postgres);
        await SeedAllTablesAsync(h);

        Assert.Equal(InsufficientPrivilege, (await h.AppExecuteAsync(sql))?.SqlState);
    }

    [Fact]
    public async Task Cross_company_references_are_rejected_by_the_database()
    {
        await using var h = await PlatformHarness.CreateAsync(postgres);
        var (eventId, _) = await SeedAllTablesAsync(h);
        var other = await PlatformHarness.CreateCompanyAsync(h.Admin);

        // Company B pointing at company A's event must fail in every table (composite FKs).
        Assert.Equal(SqlStates.ForeignKeyViolation, (await h.AdminExecuteAsync(
            $"INSERT INTO core.inbox (consumer, company_id, event_id) VALUES ('x', '{other}', '{eventId}')"))?.SqlState);
        Assert.Equal(SqlStates.ForeignKeyViolation, (await h.AdminExecuteAsync(
            $"INSERT INTO core.state_history VALUES (gen_random_uuid(), '{other}', 'Ping', gen_random_uuid(), 'DOCUMENT', NULL, 'POSTED', 'Test', '{eventId}', NULL)"))?.SqlState);
        Assert.Equal(SqlStates.ForeignKeyViolation, (await h.AdminExecuteAsync(
            $"INSERT INTO core.document_link (link_id, company_id, from_type, from_id, to_type, to_id, link_type, event_id) VALUES (gen_random_uuid(), '{other}', 'A', gen_random_uuid(), 'B', gen_random_uuid(), 'RECEIVES', '{eventId}')"))?.SqlState);
        Assert.Equal(SqlStates.UniqueViolation, (await h.AdminExecuteAsync(
            $"INSERT INTO core.outbox (company_id, event_id) VALUES ('{other}', '{eventId}')"))?.SqlState);
        Assert.Equal(SqlStates.ForeignKeyViolation, (await h.AdminExecuteAsync(
            $"INSERT INTO core.outbox (company_id, event_id) VALUES ('{other}', gen_random_uuid())"))?.SqlState);
    }

    [Fact]
    public async Task Domain_event_must_belong_to_a_command_of_the_same_company()
    {
        await using var h = await PlatformHarness.CreateAsync(postgres);
        var result = await h.Pipeline.ExecuteAsync(h.Ping("fk"), new PingHandler(), Guid.CreateVersion7());
        var other = await PlatformHarness.CreateCompanyAsync(h.Admin);

        var ex = await h.AdminExecuteAsync(
            $$"""
            INSERT INTO core.domain_event (event_id, company_id, command_id, command_event_index, event_type, schema_version,
              aggregate_type, aggregate_id, aggregate_version, event_sequence, occurred_at, business_date, session_id,
              correlation_id, payload, row_hash)
            VALUES (gen_random_uuid(), '{{other}}', '{{result.CommandId}}', 99, 'X', 1, 'X', gen_random_uuid(), 1, 1, now(),
              current_date, gen_random_uuid(), gen_random_uuid(), '{}', sha256('x'))
            """);

        Assert.Equal(SqlStates.ForeignKeyViolation, ex?.SqlState);
    }

    [Fact]
    public async Task Document_link_is_unique_treating_null_lines_as_equal()
    {
        await using var h = await PlatformHarness.CreateAsync(postgres);
        var (eventId, _) = await SeedAllTablesAsync(h);
        var from = Guid.CreateVersion7();
        var to = Guid.CreateVersion7();
        var insert = $"INSERT INTO core.document_link (link_id, company_id, from_type, from_id, to_type, to_id, link_type, event_id) VALUES (gen_random_uuid(), '{h.CompanyId}', 'GR', '{from}', 'PO', '{to}', 'RECEIVES', '{eventId}')";

        Assert.Null(await h.AdminExecuteAsync(insert));
        Assert.Equal(SqlStates.UniqueViolation, (await h.AdminExecuteAsync(insert))?.SqlState);
    }

    private static async Task<(Guid EventId, Guid CommandId)> SeedAllTablesAsync(PlatformHarness h)
    {
        var result = await h.Pipeline.ExecuteAsync(h.Ping("seed-" + Guid.NewGuid().ToString("N")), new PingHandler(), Guid.CreateVersion7());
        await h.RequestLog.FlushAsync();
        var eventId = await h.ScalarAsync<Guid>("SELECT event_id FROM core.domain_event WHERE command_id = @id AND command_event_index = 1", ("id", result.CommandId));
        Assert.Null(await h.AdminExecuteAsync($"INSERT INTO core.inbox (consumer, company_id, event_id) VALUES ('seed', '{h.CompanyId}', '{eventId}')"));
        Assert.Null(await h.AdminExecuteAsync($"INSERT INTO core.state_history VALUES (gen_random_uuid(), '{h.CompanyId}', 'Ping', '{result.ResultRef}', 'DOCUMENT', NULL, 'CREATED', 'Test.Ping', '{eventId}', NULL)"));
        Assert.Null(await h.AdminExecuteAsync($"INSERT INTO core.document_link (link_id, company_id, from_type, from_id, to_type, to_id, link_type, event_id) VALUES (gen_random_uuid(), '{h.CompanyId}', 'Ping', '{result.ResultRef}', 'Ping', gen_random_uuid(), 'CORRECTS', '{eventId}')"));
        return (eventId, result.CommandId);
    }
}
