using System.Collections.Concurrent;
using System.Data.Common;
using Rochell.Platform.Data;

namespace Rochell.Platform.Observability;

/// <summary>Outcome of one command attempt (Errata E-3).</summary>
public static class RequestOutcome
{
    public const string Succeeded = "SUCCEEDED";
    public const string DuplicateReturned = "DUPLICATE_RETURNED";
    public const string RejectedDomain = "REJECTED_DOMAIN";
    public const string ConflictRetryable = "CONFLICT_RETRYABLE";
    public const string FailedTechnical = "FAILED_TECHNICAL";
}

public sealed record RequestLogEntry(
    Guid RequestId,
    Guid? CompanyId,
    string? CommandType,
    string? IdempotencyKey,
    Guid? SessionId,
    Guid? CorrelationId,
    string Outcome,
    string? ErrorCode,
    string? ErrorMessage,
    int DurationMs,
    DateTime ReceivedAt);

/// <summary>Receives request log entries. Must never throw into the command path.</summary>
public interface IRequestLogSink
{
    void Enqueue(RequestLogEntry entry);
}

/// <summary>
/// Writes obs.request_log outside the command transaction, on its own connection, in batches.
/// Failures are reported to <c>onError</c> and never affect commands. The host calls <see cref="FlushAsync"/> periodically.
/// </summary>
public sealed class RequestLogWriter : IRequestLogSink
{
    private const int MaxMessageLength = 2000;
    private readonly ConcurrentQueue<RequestLogEntry> _queue = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly DbDataSource _dataSource;
    private readonly Action<Exception> _onError;

    public RequestLogWriter(DbDataSource dataSource, Action<Exception> onError)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _onError = onError ?? throw new ArgumentNullException(nameof(onError));
    }

    public int Pending => _queue.Count;

    public void Enqueue(RequestLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _queue.Enqueue(entry);
    }

    /// <summary>Writes everything queued so far. Returns the number of entries written.</summary>
    public async Task<int> FlushAsync(CancellationToken cancellationToken = default)
    {
        await _flushLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var batch = new List<RequestLogEntry>();
            while (_queue.TryDequeue(out var entry))
            {
                batch.Add(entry);
            }

            if (batch.Count == 0)
            {
                return 0;
            }

            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                foreach (var e in batch)
                {
                    await Sql.ExecuteAsync(
                        connection,
                        transaction,
                        """
                        INSERT INTO obs.request_log
                          (request_id, company_id, command_type, idempotency_key, session_id, correlation_id,
                           outcome, error_code, error_message, duration_ms, received_at)
                        VALUES (@request_id, @company_id, @command_type, @idempotency_key, @session_id, @correlation_id,
                                @outcome, @error_code, @error_message, @duration_ms, @received_at)
                        """,
                        cancellationToken,
                        ("request_id", e.RequestId),
                        ("company_id", e.CompanyId),
                        ("command_type", e.CommandType),
                        ("idempotency_key", e.IdempotencyKey),
                        ("session_id", e.SessionId),
                        ("correlation_id", e.CorrelationId),
                        ("outcome", e.Outcome),
                        ("error_code", e.ErrorCode),
                        ("error_message", Truncate(e.ErrorMessage)),
                        ("duration_ms", e.DurationMs),
                        ("received_at", e.ReceivedAt)).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return batch.Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Observability must never interfere with commands (E-3): report and drop this batch.
                _onError(ex);
                return 0;
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private static string? Truncate(string? message)
        => message is null || message.Length <= MaxMessageLength ? message : message[..MaxMessageLength];
}
