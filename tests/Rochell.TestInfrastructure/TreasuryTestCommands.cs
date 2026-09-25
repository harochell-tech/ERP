using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.TestInfrastructure;

/// <summary>A status the fixture records in core.state_history before its SQL runs (ADR-027).</summary>
public sealed record TestState(string AggregateType, Guid AggregateId, string? FromState, string ToState);

/// <summary>
/// VS2-01 fixture (tests only): runs SQL as the application role inside the command pipeline, after appending one event and the
/// given state_history rows. The SQL may use @event (that event's id) and @user (the acting user). VS2-02+ replace it with the
/// real commands.
/// </summary>
public sealed record TestTreasurySql(Guid CompanyId, Guid SessionId, string IdempotencyKey, string Sql, IReadOnlyList<TestState> States) : ICommand;

[RequiresPermission("test:ping")]
public sealed class TestTreasurySqlHandler : ICommandHandler<TestTreasurySql>
{
    public string CommandType => "Test.TreasurySql";

    public async Task<string> HandleAsync(TestTreasurySql command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var eventId = await context.AppendEventAsync(new EventDraft("TestTreasuryChanged", 1, "TestTreasury", context.ResultRef, 1, "{}", Publish: false), cancellationToken);
        foreach (var state in command.States)
        {
            await context.AppendStateAsync(state.AggregateType, state.AggregateId, "DOCUMENT", state.FromState, state.ToState, CommandType, eventId, cancellationToken);
        }

        await using var session = Sql.Command(context.Connection, context.Transaction, "SELECT user_id FROM iam.session WHERE session_id = @s", ("s", context.SessionId));
        var user = (Guid)(await session.ExecuteScalarAsync(cancellationToken))!;
        await Sql.ExecuteAsync(context.Connection, context.Transaction, command.Sql, cancellationToken, ("event", eventId), ("user", user));
        return "{}";
    }
}
