using System.Data;
using System.Data.Common;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Ids;
using Rochell.Platform.Time;

namespace Rochell.Platform.Queries;

/// <summary>A read request of a session in a company (E-PR17-1). No idempotency key: reading has no effect to repeat.</summary>
public interface IQuery
{
    Guid CompanyId { get; }

    Guid SessionId { get; }
}

public interface IQueryHandler<in TQuery>
    where TQuery : IQuery
{
    string QueryType { get; }

    /// <summary>Returns the result as JSON. Runs in a read-only transaction: any write fails.</summary>
    Task<string> HandleAsync(TQuery query, QueryContext context, CancellationToken cancellationToken);
}

public sealed class QueryContext(DbConnection connection, DbTransaction transaction, Guid companyId, Guid sessionId, IClock clock)
{
    public DbConnection Connection { get; } = connection;

    public DbTransaction Transaction { get; } = transaction;

    public Guid CompanyId { get; } = companyId;

    public Guid SessionId { get; } = sessionId;

    public IClock Clock { get; } = clock;
}

/// <summary>
/// E-PR17-1: queries get the same session, permission (<see cref="RequiresPermissionAttribute"/>, step-up included) and RLS checks
/// as commands, then the transaction is switched to READ ONLY before the handler runs. Authorization may still refresh the
/// session's last activity (idle timeout) before the switch. No command_log, events or request_log row.
/// </summary>
public sealed class QueryPipeline(DbDataSource dataSource, ICommandAuthorizer authorizer, IClock? clock = null)
{
    private readonly IClock _clock = clock ?? SystemClock.Instance;

    public async Task<string> ExecuteAsync<TQuery>(TQuery query, IQueryHandler<TQuery> handler, CancellationToken cancellationToken = default)
        where TQuery : IQuery
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(handler);
        var requirement = handler.GetType().GetCustomAttributes(typeof(RequiresPermissionAttribute), inherit: false) is [RequiresPermissionAttribute r]
            ? r
            : throw new InvalidOperationException($"Query handler {handler.GetType().FullName} does not declare [RequiresPermission].");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        var principal = new QueryPrincipal(query.CompanyId, query.SessionId);
        await CommandPipeline.SetTenantAsync(connection, transaction, principal, UuidV7Generator.Instance.NewId(), cancellationToken).ConfigureAwait(false);
        await authorizer.AuthorizeAsync(connection, transaction, principal, requirement, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(connection, transaction, "SET TRANSACTION READ ONLY", cancellationToken).ConfigureAwait(false);

        var result = await handler.HandleAsync(query, new QueryContext(connection, transaction, query.CompanyId, query.SessionId, _clock), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>The authorizer's view of a query: who and where, with no idempotency key.</summary>
    private sealed record QueryPrincipal(Guid CompanyId, Guid SessionId) : ICommand
    {
        public string IdempotencyKey => "query";
    }
}
