using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.Platform.Commands;

namespace Rochell.TestInfrastructure;

/// <summary>Test-only command posting TEST.POSTING: debit (optionally to the control account), credit, both through the engine.</summary>
public sealed record TestPostingCommand(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid PlantId,
    decimal Amount,
    DateOnly BusinessDate,
    decimal? CreditAmount = null,
    decimal ControlAmount = 0) : ICommand;

[RequiresPermission("test:ping")]
public sealed class TestPostingHandler : ICommandHandler<TestPostingCommand>
{
    private readonly PostingEngine _engine = new();

    public string CommandType => "Test.Posting";

    public async Task<string> HandleAsync(TestPostingCommand command, CommandContext context, CancellationToken cancellationToken)
    {
        var lines = new List<PostingLineInput>
        {
            new("T-DR", "amount", command.Amount, PlantId: command.PlantId),
            new("T-CR", "credit_amount", command.CreditAmount ?? command.Amount + command.ControlAmount),
        };
        if (command.ControlAmount > 0)
        {
            lines.Add(new PostingLineInput("T-CTL", "amount", command.ControlAmount, SubledgerRef: context.ResultRef));
        }

        // Patch 1 §5.2 step 7: every prerequisite is validated before anything is written.
        var plan = await _engine.PrepareAsync(context, new PostingRequest(FinanceSetup.TestRule, command.BusinessDate, context.Clock.UtcNow, lines), cancellationToken);
        var eventId = await context.AppendEventAsync(
            new EventDraft("TestPosted", 1, "TestDocument", context.ResultRef, 1, "{}", Publish: false, BusinessDate: command.BusinessDate),
            cancellationToken);
        var journal = await _engine.WriteAsync(context, plan, eventId, cancellationToken);
        return JsonSerializer.Serialize(new { journalId = journal.JournalId, postingDate = journal.PostingDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), lateEntry = journal.LateEntry });
    }
}

/// <summary>Test-only command reversing a journal exactly (Patch 1 P-4).</summary>
public sealed record TestReversalCommand(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid JournalId, DateOnly BusinessDate) : ICommand;

[RequiresPermission("test:ping")]
public sealed class TestReversalHandler : ICommandHandler<TestReversalCommand>
{
    private readonly PostingEngine _engine = new();

    public string CommandType => "Test.Reversal";

    public async Task<string> HandleAsync(TestReversalCommand command, CommandContext context, CancellationToken cancellationToken)
    {
        var eventId = await context.AppendEventAsync(
            new EventDraft("TestReversed", 1, "TestReversal", context.ResultRef, 1, "{}", Publish: false, BusinessDate: command.BusinessDate),
            cancellationToken);
        var journal = await _engine.ReverseAsync(context, command.JournalId, eventId, command.BusinessDate, context.Clock.UtcNow, cancellationToken);
        return JsonSerializer.Serialize(new { journalId = journal.JournalId });
    }
}
