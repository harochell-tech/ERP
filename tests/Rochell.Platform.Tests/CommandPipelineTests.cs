using Rochell.Platform.Commands;
using Rochell.Platform.Observability;
using Rochell.Platform.Tests.Infrastructure;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Platform.Tests;

/// <summary>Idempotency and atomicity: ID-01…ID-05 (Errata §15.4) and CMD-03 (Patch 1 §7.2).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class CommandPipelineTests(PostgresFixture postgres)
{
    [Trait("Acceptance", "ID-01")]
    [Fact]
    public async Task ID01_duplicate_returns_original_result_and_writes_nothing()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var handler = new PingHandler();
        var command = h.Ping("id-01");

        var first = await h.Pipeline.ExecuteAsync(command, handler, Guid.CreateVersion7());
        var countsAfterFirst = await h.CountsAsync();
        var second = await h.Pipeline.ExecuteAsync(command, handler, Guid.CreateVersion7());

        Assert.False(first.Duplicate);
        Assert.True(second.Duplicate);
        Assert.Equal(first.CommandId, second.CommandId);
        Assert.Equal(first.ResultRef, second.ResultRef);
        Assert.Equal(first.ResultPayload, second.ResultPayload);
        Assert.Equal(1, handler.Executions);
        Assert.Equal((1L, 2L, 1L), countsAfterFirst);
        Assert.Equal(countsAfterFirst, await h.CountsAsync());
        Assert.Equal([RequestOutcome.DuplicateReturned, RequestOutcome.Succeeded], (await h.OutcomesAsync()).Order(StringComparer.Ordinal));
    }

    [Trait("Acceptance", "CMD-03")]
    [Trait("Acceptance", "ID-02")]
    [Fact]
    public async Task ID02_CMD03_concurrent_same_key_executes_once_and_second_waits()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var handler = new PingHandler { HoldTransaction = TimeSpan.FromSeconds(1) };
        var command = h.Ping("id-02");

        var results = await Task.WhenAll(
            h.Pipeline.ExecuteAsync(command, handler, Guid.CreateVersion7()),
            h.Pipeline.ExecuteAsync(command, handler, Guid.CreateVersion7()));

        Assert.Equal(1, handler.Executions);
        Assert.Single(results, r => r.Duplicate);
        Assert.Single(results, r => !r.Duplicate);
        Assert.Equal(results[0].CommandId, results[1].CommandId);
        Assert.Equal(results[0].ResultPayload, results[1].ResultPayload);
        Assert.Equal(1L, await h.CountAsync("core.command_log"));
    }

    [Trait("Acceptance", "ID-03")]
    [Fact]
    public async Task ID03_domain_rejection_leaves_no_command_log_and_allows_retry()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var handler = new PingHandler();

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => h.Pipeline.ExecuteAsync(h.Ping("id-03", mode: PingMode.RejectDomain), handler, Guid.CreateVersion7()));

        Assert.Equal("PING_REJECTED", ex.Code);
        Assert.Equal((0L, 0L, 0L), await h.CountsAsync());
        Assert.Equal([RequestOutcome.RejectedDomain], await h.OutcomesAsync());

        var retry = await h.Pipeline.ExecuteAsync(h.Ping("id-03"), handler, Guid.CreateVersion7());

        Assert.False(retry.Duplicate);
        Assert.Equal((1L, 2L, 1L), await h.CountsAsync());
    }

    [Trait("Acceptance", "ID-04")]
    [Fact]
    public async Task ID04_technical_failure_rolls_back_everything()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Pipeline.ExecuteAsync(h.Ping("id-04", mode: PingMode.FailAfterEvents, sideEvents: 2), new PingHandler(), Guid.CreateVersion7()));

        Assert.Equal((0L, 0L, 0L), await h.CountsAsync());
        Assert.Equal([RequestOutcome.FailedTechnical], await h.OutcomesAsync());
    }

    [Trait("Acceptance", "ID-05")]
    [Fact]
    public async Task ID05_request_log_failure_never_affects_the_command()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        Assert.Null(await h.AdminExecuteAsync("REVOKE INSERT ON obs.request_log FROM rochell_app"));

        var result = await h.Pipeline.ExecuteAsync(h.Ping("id-05"), new PingHandler(), Guid.CreateVersion7());
        var written = await h.RequestLog.FlushAsync();

        Assert.False(result.Duplicate);
        Assert.Equal(1L, await h.CountAsync("core.command_log"));
        Assert.Equal(0, written);
        Assert.Single(h.RequestLogErrors);
        Assert.Equal(0L, await h.CountAsync("obs.request_log"));
    }

    [Fact]
    public async Task Result_payload_is_stored_and_committed_at_is_set()
    {
        await using var h = await TestHarness.CreateAsync(postgres);

        var result = await h.Pipeline.ExecuteAsync(h.Ping("result", message: "bloque 6"), new PingHandler(), Guid.CreateVersion7());

        Assert.Equal(result.ResultRef, await h.ScalarAsync<Guid>("SELECT result_ref FROM core.command_log WHERE command_id = @id", ("id", result.CommandId)));
        Assert.Equal("bloque 6", await h.ScalarAsync<string>("SELECT result_payload->>'message' FROM core.command_log WHERE command_id = @id", ("id", result.CommandId)));
        Assert.True(await h.ScalarAsync<bool>("SELECT committed_at IS NOT NULL FROM core.command_log WHERE command_id = @id", ("id", result.CommandId)));
    }

    [Fact]
    public async Task Same_key_in_another_company_is_a_different_command()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        var otherCompany = await h.CreateCompanyAsync();
        await h.GrantAsync(otherCompany, h.UserId, "TEST_PINGER");
        var handler = new PingHandler();

        var a = await h.Pipeline.ExecuteAsync(h.Ping("shared-key"), handler, Guid.CreateVersion7());
        var b = await h.Pipeline.ExecuteAsync(h.Ping("shared-key") with { CompanyId = otherCompany }, handler, Guid.CreateVersion7());

        Assert.False(b.Duplicate);
        Assert.NotEqual(a.CommandId, b.CommandId);
        Assert.Equal(2, handler.Executions);
    }
}
