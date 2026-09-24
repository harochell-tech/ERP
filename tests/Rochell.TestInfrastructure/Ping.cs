using System.Text.Json;
using Rochell.Platform.Commands;

namespace Rochell.TestInfrastructure;

public enum PingMode
{
    Normal,
    RejectDomain,
    FailAfterEvents,
}

/// <summary>Test-only command (baseline §17 PR-02). Never exists in production assemblies (ArchitectureTests).</summary>
public sealed record PingCommand(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    string Message,
    PingMode Mode = PingMode.Normal,
    int SideEvents = 0,
    DateTime? OccurredAt = null) : ICommand;

/// <summary>Plant-scoped test command.</summary>
public sealed record PlantPingCommand(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId) : IPlantScopedCommand;

[RequiresPermission("test:ping")]
public sealed class PingHandler : ICommandHandler<PingCommand>
{
    private int _executions;

    public string CommandType => "Test.Ping";

    public int Executions => _executions;

    /// <summary>Holds the transaction open, to exercise concurrent duplicates.</summary>
    public TimeSpan HoldTransaction { get; init; } = TimeSpan.Zero;

    public async Task<string> HandleAsync(PingCommand command, CommandContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _executions);
        if (command.Mode == PingMode.RejectDomain)
        {
            throw new DomainException("PING_REJECTED", "Ping rejected by a business rule.");
        }

        if (HoldTransaction > TimeSpan.Zero)
        {
            await Task.Delay(HoldTransaction, cancellationToken);
        }

        var payload = JsonSerializer.Serialize(new { message = command.Message });
        await context.AppendEventAsync(new EventDraft("Pinged", 1, "Ping", context.ResultRef, 1, payload, Publish: true, OccurredAt: command.OccurredAt), cancellationToken);
        await context.AppendEventAsync(new EventDraft("PingEchoed", 1, "Ping", context.ResultRef, 1, "{}", Publish: false, OccurredAt: command.OccurredAt), cancellationToken);
        for (var i = 0; i < command.SideEvents; i++)
        {
            await context.AppendEventAsync(new EventDraft("PingSide", 1, "PingSide", context.Ids.NewId(), 1, "{}", Publish: true), cancellationToken);
        }

        if (command.Mode == PingMode.FailAfterEvents)
        {
            throw new InvalidOperationException("Simulated technical failure after writing events.");
        }

        return JsonSerializer.Serialize(new { pingId = context.ResultRef, message = command.Message });
    }
}

[RequiresPermission("test:ping_step_up", StepUp = true)]
public sealed class StepUpPingHandler : ICommandHandler<PingCommand>
{
    private readonly PingHandler _inner = new();

    public string CommandType => "Test.StepUpPing";

    public Task<string> HandleAsync(PingCommand command, CommandContext context, CancellationToken cancellationToken)
        => _inner.HandleAsync(command, context, cancellationToken);
}

[RequiresPermission("test:ping")]
public sealed class PlantPingHandler : ICommandHandler<PlantPingCommand>
{
    public string CommandType => "Test.PlantPing";

    public async Task<string> HandleAsync(PlantPingCommand command, CommandContext context, CancellationToken cancellationToken)
    {
        await context.AppendEventAsync(new EventDraft("PlantPinged", 1, "Ping", context.ResultRef, 1, "{}", Publish: false), cancellationToken);
        return "{}";
    }
}

/// <summary>Test-only query that tries to write: the query pipeline must refuse it (read-only transaction, E-PR17-1).</summary>
public sealed record WritingQuery(Guid CompanyId, Guid SessionId) : Rochell.Platform.Queries.IQuery;

[RequiresPermission("test:ping")]
public sealed class WritingQueryHandler : Rochell.Platform.Queries.IQueryHandler<WritingQuery>
{
    public string QueryType => "Test.WritingQuery";

    public async Task<string> HandleAsync(WritingQuery query, Rochell.Platform.Queries.QueryContext context, CancellationToken cancellationToken)
    {
        await Rochell.Platform.Data.Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "INSERT INTO obs.request_log (request_id, outcome, received_at) VALUES (gen_random_uuid(), 'SUCCEEDED', now())",
            cancellationToken);
        return "{}";
    }
}
