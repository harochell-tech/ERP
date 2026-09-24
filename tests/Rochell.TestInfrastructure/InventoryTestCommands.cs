using System.Text.Json;
using Rochell.Finance.Posting;
using Rochell.Inventory;
using Rochell.Platform.Commands;

namespace Rochell.TestInfrastructure;

/// <summary>R-T1 fixture (tests only, TST-01): receives stock into a new lot and posts TEST.RECEIPT.</summary>
public sealed record TestReceiveStock(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid LocationId, Guid ItemId, decimal Quantity, decimal Value, DateOnly BusinessDate) : ICommand;

[RequiresPermission("test:ping")]
public sealed class TestReceiveStockHandler : ICommandHandler<TestReceiveStock>
{
    private readonly PostingEngine _engine = new();
    private readonly InventoryLedger _ledger = new();

    public string CommandType => "Test.ReceiveStock";

    public async Task<string> HandleAsync(TestReceiveStock command, CommandContext context, CancellationToken cancellationToken)
    {
        var valueEntryId = context.Ids.NewId();
        var plantId = await PlantOfAsync(context, command.LocationId, cancellationToken);
        var plan = await _engine.PrepareAsync(
            context,
            new PostingRequest("TEST.RECEIPT", command.BusinessDate, context.Clock.UtcNow,
            [
                new PostingLineInput("TR-DR-INV", "receipt_value", command.Value, PlantId: plantId, ItemId: command.ItemId, SubledgerRef: valueEntryId, InvValueEntryId: valueEntryId),
                new PostingLineInput("TR-CR", "receipt_value", command.Value),
            ]),
            cancellationToken);
        var eventId = await context.AppendEventAsync(new EventDraft("TestStockReceived", 1, "TestReceipt", context.ResultRef, 1, "{}", Publish: false, BusinessDate: command.BusinessDate), cancellationToken);
        var lotId = await _ledger.CreateLotAsync(context, command.ItemId, null, null, eventId, command.BusinessDate, cancellationToken);
        await _ledger.ReceiveAsync(
            context,
            new ReceiptMovement(command.LocationId, command.ItemId, lotId, command.Quantity, command.Value, valueEntryId),
            new MovementSource(eventId, "TEST_RECEIPT", context.ResultRef),
            new MovementDates(context.Clock.UtcNow, command.BusinessDate, plan.PostingDate),
            cancellationToken);
        await _engine.WriteAsync(context, plan, eventId, cancellationToken);
        return JsonSerializer.Serialize(new { lotId, valueEntryId });
    }

    internal static async Task<Guid> PlantOfAsync(CommandContext context, Guid locationId, CancellationToken cancellationToken)
    {
        await using var command = Platform.Data.Sql.Command(context.Connection, context.Transaction, "SELECT plant_id FROM md.location WHERE location_id = @l", ("l", locationId));
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}

/// <summary>R-T1 fixture (tests only, TST-01): issues stock at moving average and posts TEST.ISSUE.</summary>
public sealed record TestIssueStock(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid LocationId, Guid ItemId, Guid LotId, decimal Quantity, DateOnly BusinessDate) : ICommand;

[RequiresPermission("test:ping")]
public sealed class TestIssueStockHandler : ICommandHandler<TestIssueStock>
{
    private readonly PostingEngine _engine = new();
    private readonly InventoryLedger _ledger = new();

    public string CommandType => "Test.IssueStock";

    public async Task<string> HandleAsync(TestIssueStock command, CommandContext context, CancellationToken cancellationToken)
    {
        var reservation = await _ledger.ReserveIssueAsync(context, command.LocationId, command.ItemId, command.LotId, command.Quantity, cancellationToken);
        Guid? valueEntryId = reservation.Value == 0 ? null : context.Ids.NewId();
        PostingPlan? plan = null;
        if (valueEntryId is not null)
        {
            plan = await _engine.PrepareAsync(
                context,
                new PostingRequest("TEST.ISSUE", command.BusinessDate, context.Clock.UtcNow,
                [
                    new PostingLineInput("TI-DR", "issue_value", reservation.Value, PlantId: reservation.PlantId),
                    new PostingLineInput("TI-CR-INV", "issue_value", reservation.Value, PlantId: reservation.PlantId, ItemId: command.ItemId, SubledgerRef: valueEntryId, InvValueEntryId: valueEntryId),
                ]),
                cancellationToken);
        }

        var eventId = await context.AppendEventAsync(new EventDraft("TestStockIssued", 1, "TestIssue", context.ResultRef, 1, "{}", Publish: false, BusinessDate: command.BusinessDate), cancellationToken);
        await _ledger.WriteIssueAsync(
            context,
            reservation,
            valueEntryId,
            new MovementSource(eventId, "TEST_ISSUE", context.ResultRef),
            new MovementDates(context.Clock.UtcNow, command.BusinessDate, plan?.PostingDate ?? command.BusinessDate),
            cancellationToken);
        if (plan is not null)
        {
            await _engine.WriteAsync(context, plan, eventId, cancellationToken);
        }

        return JsonSerializer.Serialize(new { value = reservation.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    }
}
