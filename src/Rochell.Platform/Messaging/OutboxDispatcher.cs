using System.Data.Common;
using Rochell.Platform.Data;

namespace Rochell.Platform.Messaging;

/// <summary>An event delivered from the outbox.</summary>
public sealed record DispatchedEvent(
    long OutboxId,
    Guid EventId,
    Guid CompanyId,
    string EventType,
    int SchemaVersion,
    string AggregateType,
    Guid AggregateId,
    long AggregateVersion,
    short EventSequence,
    DateTime OccurredAt,
    DateOnly BusinessDate,
    Guid CorrelationId,
    string PayloadJson);

/// <summary>
/// Internal consumer. Called at-least-once; the dispatcher records (consumer, event) in core.inbox in the same
/// transaction as <see cref="HandleAsync"/>, so a redelivered event is skipped (ADR-021).
/// </summary>
public interface IEventConsumer
{
    string Name { get; }

    bool Handles(string eventType);

    Task HandleAsync(DispatchedEvent dispatchedEvent, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken);
}

/// <summary>
/// In-process outbox dispatcher (ADR-003): claims pending rows with FOR UPDATE SKIP LOCKED, delivers them in outbox order,
/// marks them dispatched. A failing consumer leaves the row pending with a backoff; other rows continue.
/// </summary>
public sealed class OutboxDispatcher
{
    private readonly DbDataSource _dataSource;
    private readonly IReadOnlyList<IEventConsumer> _consumers;
    private readonly Action<Exception, DispatchedEvent>? _onError;

    public OutboxDispatcher(DbDataSource dataSource, IReadOnlyList<IEventConsumer> consumers, Action<Exception, DispatchedEvent>? onError = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _consumers = consumers ?? throw new ArgumentNullException(nameof(consumers));
        _onError = onError;
    }

    /// <summary>
    /// Dispatches up to <paramref name="batchSize"/> pending events per company. Returns how many were marked dispatched.
    /// Row-level security isolates companies, so each company is claimed in its own transaction with its tenant setting.
    /// </summary>
    public async Task<int> DispatchPendingAsync(int batchSize = 100, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var total = 0;
        foreach (var companyId in await CompaniesAsync(cancellationToken).ConfigureAwait(false))
        {
            total += await DispatchCompanyAsync(companyId, batchSize, cancellationToken).ConfigureAwait(false);
        }

        return total;
    }

    private async Task<List<Guid>> CompaniesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Sql.Command(connection, null, "SELECT company_id FROM md.company ORDER BY company_id");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var companies = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            companies.Add(reader.GetGuid(0));
        }

        return companies;
    }

    private static Task SetCompanyAsync(DbConnection connection, DbTransaction transaction, Guid companyId, CancellationToken cancellationToken)
        => Sql.ExecuteAsync(connection, transaction, "SELECT set_config('app.company_id', @company, true)", cancellationToken, ("company", companyId.ToString()));

    private async Task<int> DispatchCompanyAsync(Guid companyId, int batchSize, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetCompanyAsync(connection, transaction, companyId, cancellationToken).ConfigureAwait(false);

        var batch = await ClaimAsync(connection, transaction, batchSize, cancellationToken).ConfigureAwait(false);
        var dispatched = 0;
        foreach (var item in batch)
        {
            try
            {
                foreach (var consumer in _consumers.Where(c => c.Handles(item.EventType)))
                {
                    await DeliverAsync(consumer, item, cancellationToken).ConfigureAwait(false);
                }

                await Sql.ExecuteAsync(
                    connection,
                    transaction,
                    "UPDATE core.outbox SET dispatched_at = now() WHERE outbox_id = @outbox_id",
                    cancellationToken,
                    ("outbox_id", item.OutboxId)).ConfigureAwait(false);
                dispatched++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _onError?.Invoke(ex, item);
                await Sql.ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE core.outbox
                    SET attempts = attempts + 1,
                        available_at = now() + make_interval(secs => LEAST(300, 1 << LEAST(attempts, 8)))
                    WHERE outbox_id = @outbox_id
                    """,
                    cancellationToken,
                    ("outbox_id", item.OutboxId)).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return dispatched;
    }

    private static async Task<List<DispatchedEvent>> ClaimAsync(DbConnection connection, DbTransaction transaction, int batchSize, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            connection,
            transaction,
            """
            SELECT o.outbox_id, e.event_id, e.company_id, e.event_type, e.schema_version, e.aggregate_type, e.aggregate_id,
                   e.aggregate_version, e.event_sequence, e.occurred_at, e.business_date, e.correlation_id, e.payload::text
            FROM core.outbox o
            JOIN core.domain_event e ON e.company_id = o.company_id AND e.event_id = o.event_id
            WHERE o.dispatched_at IS NULL AND o.available_at <= now()
            ORDER BY o.outbox_id
            LIMIT @batch_size
            FOR UPDATE OF o SKIP LOCKED
            """,
            ("batch_size", batchSize));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<DispatchedEvent>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new DispatchedEvent(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetGuid(6),
                reader.GetInt64(7),
                reader.GetInt16(8),
                reader.GetFieldValue<DateTime>(9),
                reader.GetFieldValue<DateOnly>(10),
                reader.GetGuid(11),
                reader.GetString(12)));
        }

        return items;
    }

    private async Task DeliverAsync(IEventConsumer consumer, DispatchedEvent item, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetCompanyAsync(connection, transaction, item.CompanyId, cancellationToken).ConfigureAwait(false);

        var inserted = await Sql.ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO core.inbox (consumer, company_id, event_id) VALUES (@consumer, @company_id, @event_id) ON CONFLICT (consumer, event_id) DO NOTHING",
            cancellationToken,
            ("consumer", consumer.Name),
            ("company_id", item.CompanyId),
            ("event_id", item.EventId)).ConfigureAwait(false);

        if (inserted == 1)
        {
            await consumer.HandleAsync(item, connection, transaction, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
