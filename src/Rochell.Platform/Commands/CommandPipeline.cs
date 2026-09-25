using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Rochell.Platform.Data;
using Rochell.Platform.Hashing;
using Rochell.Platform.Ids;
using Rochell.Platform.Observability;
using Rochell.Platform.Time;

namespace Rochell.Platform.Commands;

/// <summary>
/// Transaction template of Frozen Baseline Patch 1 §5.2, platform part (steps 1–3, 6, 8, 14–17):
/// pre-assign ids → BEGIN → tenant settings (RLS) → authorization → command_log (result NULL; duplicates wait on the unique index) → handler
/// (events, documents, ledgers) → outbox → single UPDATE of the result → COMMIT → request_log outside the TX.
/// Retries the whole command on 40001/40P01 with the same idempotency key.
/// </summary>
public sealed class CommandPipeline
{
    private static readonly int[] BackoffMs = [50, 200, 800];
    private readonly DbDataSource _dataSource;
    private readonly ICommandAuthorizer _authorizer;
    private readonly IRequestLogSink _requestLog;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;

    public CommandPipeline(DbDataSource dataSource, ICommandAuthorizer authorizer, IRequestLogSink requestLog, IClock? clock = null, IIdGenerator? ids = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _requestLog = requestLog ?? throw new ArgumentNullException(nameof(requestLog));
        _clock = clock ?? SystemClock.Instance;
        _ids = ids ?? UuidV7Generator.Instance;
    }

    public async Task<CommandResult> ExecuteAsync<TCommand>(
        TCommand command,
        ICommandHandler<TCommand> handler,
        Guid correlationId,
        CancellationToken cancellationToken = default)
        where TCommand : ICommand
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.IdempotencyKey);

        var receivedAt = _clock.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var result = await ExecuteOnceAsync(command, handler, correlationId, cancellationToken).ConfigureAwait(false);
                Log(result.Duplicate ? RequestOutcome.DuplicateReturned : RequestOutcome.Succeeded, null, null);
                return result;
            }
            catch (DbException ex) when (SqlStates.IsRetryable(ex.SqlState) && attempt < BackoffMs.Length)
            {
                await Task.Delay(BackoffMs[attempt] + Random.Shared.Next(0, 50), cancellationToken).ConfigureAwait(false);
            }
            catch (DomainException ex)
            {
                Log(RequestOutcome.RejectedDomain, ex.Code, ex.Message);
                throw;
            }
            catch (DbException ex) when (SqlStates.IsRetryable(ex.SqlState))
            {
                Log(RequestOutcome.ConflictRetryable, ex.SqlState, ex.Message);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log(RequestOutcome.FailedTechnical, (ex as DbException)?.SqlState ?? ex.GetType().Name, ex.Message);
                throw;
            }
        }

        void Log(string outcome, string? errorCode, string? errorMessage) => _requestLog.Enqueue(new RequestLogEntry(
            _ids.NewId(),
            command.CompanyId,
            handler.CommandType,
            command.IdempotencyKey,
            command.SessionId,
            correlationId,
            outcome,
            errorCode,
            errorMessage,
            (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue),
            receivedAt));
    }

    private async Task<CommandResult> ExecuteOnceAsync<TCommand>(
        TCommand command,
        ICommandHandler<TCommand> handler,
        Guid correlationId,
        CancellationToken cancellationToken)
        where TCommand : ICommand
    {
        // Step 1: every id is generated before any INSERT (Errata E-4).
        var commandId = _ids.NewId();
        var resultRef = _ids.NewId();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // E-VS1-7: locks some handlers need before their transaction (and its snapshot) exists; released when the command ends.
        var held = new List<string>();
        try
        {
            if (handler is IPreTransactionLocks<TCommand> locking)
            {
                foreach (var key in locking.ExclusiveLocksBeforeTransaction(command))
                {
                    await Sql.ExecuteAsync(connection, null, "SELECT pg_advisory_lock(hashtextextended(@key, 0))", cancellationToken, ("key", key)).ConfigureAwait(false);
                    held.Add(key);
                }
            }

            return await ExecuteInTransactionAsync(connection, command, handler, correlationId, commandId, resultRef, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var key in held)
            {
                try
                {
                    await Sql.ExecuteAsync(connection, null, "SELECT pg_advisory_unlock(hashtextextended(@key, 0))", CancellationToken.None, ("key", key)).ConfigureAwait(false);
                }
                catch (DbException)
                {
                    // A broken connection is discarded by the pool and PostgreSQL releases its session locks.
                }
            }
        }
    }

    private async Task<CommandResult> ExecuteInTransactionAsync<TCommand>(
        DbConnection connection,
        TCommand command,
        ICommandHandler<TCommand> handler,
        Guid correlationId,
        Guid commandId,
        Guid resultRef,
        CancellationToken cancellationToken)
        where TCommand : ICommand
    {
        var isolation = handler.GetType().IsDefined(typeof(SerializableTransactionAttribute), inherit: false) ? IsolationLevel.Serializable : IsolationLevel.ReadCommitted;
        await using var transaction = await connection.BeginTransactionAsync(isolation, cancellationToken).ConfigureAwait(false);

        await SetTenantAsync(connection, transaction, command, correlationId, cancellationToken).ConfigureAwait(false);

        // Step 6 (authorization part): session, permission, plant scope and step-up, before any write.
        var requirement = Requirement(handler);
        await _authorizer.AuthorizeAsync(connection, transaction, command, requirement, cancellationToken).ConfigureAwait(false);

        // Step 3: idempotency row. A concurrent command with the same key blocks here until the first one ends.
        try
        {
            await Sql.ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO core.command_log (company_id, command_id, command_type, idempotency_key, session_id, result_ref)
                VALUES (@company_id, @command_id, @command_type, @idempotency_key, @session_id, @result_ref)
                """,
                cancellationToken,
                ("company_id", command.CompanyId),
                ("command_id", commandId),
                ("command_type", handler.CommandType),
                ("idempotency_key", command.IdempotencyKey),
                ("session_id", command.SessionId),
                ("result_ref", resultRef)).ConfigureAwait(false);
        }
        catch (DbException ex) when (ex.SqlState == SqlStates.UniqueViolation)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return await ReadCommittedResultAsync(connection, command, handler.CommandType, correlationId, cancellationToken).ConfigureAwait(false);
        }

        var context = new CommandContext(connection, transaction, command, commandId, resultRef, correlationId, _clock, _ids)
        {
            StepUpCheck = ct => _authorizer.EnsureStepUpAsync(connection, transaction, command, ct),
        };
        var payload = await handler.HandleAsync(command, context, cancellationToken).ConfigureAwait(false);
        var canonicalPayload = CanonicalResult(payload);

        // Step 14: outbox rows for published events, in emission order.
        foreach (var eventId in context.PublishedEventIds)
        {
            await Sql.ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO core.outbox (company_id, event_id) VALUES (@company_id, @event_id)",
                cancellationToken,
                ("company_id", command.CompanyId),
                ("event_id", eventId)).ConfigureAwait(false);
        }

        // Step 15: the only UPDATE of command_log, immediately before COMMIT (Patch 1, P-3).
        await Sql.ExecuteAsync(
            connection,
            transaction,
            "UPDATE core.command_log SET result_payload = CAST(@payload AS jsonb), committed_at = @committed_at WHERE command_id = @command_id",
            cancellationToken,
            ("payload", canonicalPayload),
            ("committed_at", _clock.UtcNow),
            ("command_id", commandId)).ConfigureAwait(false);

        // Step 16: deferred constraint triggers run here.
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CommandResult(commandId, resultRef, canonicalPayload, Duplicate: false);
    }

    /// <summary>Transaction-local tenant settings read by row-level security policies and triggers.</summary>
    public static Task SetTenantAsync(DbConnection connection, DbTransaction transaction, ICommand command, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Sql.ExecuteAsync(
            connection,
            transaction,
            "SELECT set_config('app.company_id', @company, true), set_config('app.session_id', @session, true), set_config('app.correlation_id', @correlation, true)",
            cancellationToken,
            ("company", command.CompanyId.ToString()),
            ("session", command.SessionId.ToString()),
            ("correlation", correlationId.ToString()));
    }

    private static RequiresPermissionAttribute Requirement(object handler)
        => handler.GetType().GetCustomAttributes(typeof(RequiresPermissionAttribute), inherit: false) is [RequiresPermissionAttribute requirement]
            ? requirement
            : throw new InvalidOperationException($"Command handler {handler.GetType().FullName} does not declare [RequiresPermission].");

    private static async Task<CommandResult> ReadCommittedResultAsync(
        DbConnection connection,
        ICommand command,
        string commandType,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        // Row-level security: the read needs the tenant setting, so it runs in its own short transaction.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetTenantAsync(connection, transaction, command, correlationId, cancellationToken).ConfigureAwait(false);
        await using var select = Sql.Command(
            connection,
            transaction,
            """
            SELECT command_id, result_ref, result_payload::text
            FROM core.command_log
            WHERE company_id = @company_id AND command_type = @command_type AND idempotency_key = @idempotency_key
            """,
            ("company_id", command.CompanyId),
            ("command_type", commandType),
            ("idempotency_key", command.IdempotencyKey));
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Idempotency conflict reported but no committed command_log row was found.");
        }

        var result = new CommandResult(reader.GetGuid(0), reader.GetGuid(1), JsonCanonicalizer.Canonicalize(reader.GetString(2)), Duplicate: true);
        await reader.CloseAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static string CanonicalResult(string payload)
    {
        try
        {
            return JsonCanonicalizer.Canonicalize(payload);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Command handlers must return a valid JSON result payload.", ex);
        }
    }
}
