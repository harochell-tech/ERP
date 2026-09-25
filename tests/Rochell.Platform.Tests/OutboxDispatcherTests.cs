using System.Data.Common;
using Rochell.Platform.Messaging;
using Rochell.Platform.Tests.Infrastructure;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Platform.Tests;

/// <summary>ID-07 (Errata §15.4): at-least-once delivery, effect exactly once through core.inbox.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class OutboxDispatcherTests(PostgresFixture postgres)
{
    [Trait("Acceptance", "ID-07")]
    [Fact]
    public async Task ID07_redelivered_event_is_applied_once()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.Pipeline.ExecuteAsync(h.Ping("id-07"), new PingHandler(), Guid.CreateVersion7());
        var consumer = new RecordingConsumer("test.recorder");
        var dispatcher = new OutboxDispatcher(h.App, [consumer]);

        Assert.Equal(1, await dispatcher.DispatchPendingAsync());
        for (var redelivery = 0; redelivery < 2; redelivery++)
        {
            // Simulate the crash window "consumer committed, outbox not yet marked" by re-opening the row.
            Assert.Null(await h.AdminExecuteAsync("UPDATE core.outbox SET dispatched_at = NULL"));
            Assert.Equal(1, await dispatcher.DispatchPendingAsync());
        }

        Assert.Equal(["Pinged"], consumer.Handled);
        Assert.Equal(1L, await h.CountAsync("core.inbox"));
    }

    [Fact]
    public async Task Events_are_delivered_in_outbox_order_and_unpublished_events_are_not_delivered()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.Pipeline.ExecuteAsync(h.Ping("order-1"), new PingHandler(), Guid.CreateVersion7());
        await h.Pipeline.ExecuteAsync(h.Ping("order-2", sideEvents: 1), new PingHandler(), Guid.CreateVersion7());
        var consumer = new RecordingConsumer("test.order");

        var dispatched = await new OutboxDispatcher(h.App, [consumer]).DispatchPendingAsync();

        Assert.Equal(3, dispatched);
        Assert.Equal(["Pinged", "Pinged", "PingSide"], consumer.Handled);
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM core.outbox WHERE dispatched_at IS NULL"));
    }

    [Fact]
    public async Task Failing_consumer_leaves_event_pending_with_backoff()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        await h.Pipeline.ExecuteAsync(h.Ping("fail"), new PingHandler(), Guid.CreateVersion7());
        var errors = new List<Exception>();

        var dispatched = await new OutboxDispatcher(h.App, [new FailingConsumer()], (ex, _) => errors.Add(ex)).DispatchPendingAsync();

        Assert.Equal(0, dispatched);
        Assert.Single(errors);
        Assert.Equal(1, await h.ScalarAsync<int>("SELECT attempts FROM core.outbox"));
        Assert.True(await h.ScalarAsync<bool>("SELECT dispatched_at IS NULL AND available_at > now() FROM core.outbox"));
        Assert.Equal(0L, await h.CountAsync("core.inbox"));
    }

    [Fact]
    public async Task Concurrent_dispatchers_never_deliver_the_same_row_twice()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        for (var i = 0; i < 20; i++)
        {
            await h.Pipeline.ExecuteAsync(h.Ping($"concurrent-{i}"), new PingHandler(), Guid.CreateVersion7());
        }

        var consumer = new RecordingConsumer("test.concurrent");
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => new OutboxDispatcher(h.App, [consumer]).DispatchPendingAsync(batchSize: 5)));
        var total = results.Sum();
        int more;
        while ((more = await new OutboxDispatcher(h.App, [consumer]).DispatchPendingAsync()) > 0)
        {
            total += more;
        }

        // SKIP LOCKED: every outbox row is marked dispatched exactly once across all dispatchers.
        Assert.Equal(20, total);
        Assert.Equal(20, consumer.Handled.Count);
        Assert.Equal(20L, await h.CountAsync("core.inbox"));
    }

    private sealed class RecordingConsumer(string name) : IEventConsumer
    {
        private readonly List<string> _handled = [];

        public string Name => name;

        public List<string> Handled
        {
            get
            {
                lock (_handled)
                {
                    return [.. _handled];
                }
            }
        }

        public bool Handles(string eventType) => true;

        public Task HandleAsync(DispatchedEvent dispatchedEvent, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
        {
            lock (_handled)
            {
                _handled.Add(dispatchedEvent.EventType);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FailingConsumer : IEventConsumer
    {
        public string Name => "test.failing";

        public bool Handles(string eventType) => true;

        public Task HandleAsync(DispatchedEvent dispatchedEvent, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
            => throw new InvalidOperationException("consumer failure");
    }
}
