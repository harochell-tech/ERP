using System.Text.Json;
using Rochell.Platform.Commands;

namespace Rochell.Platform.Tests.Infrastructure;

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
