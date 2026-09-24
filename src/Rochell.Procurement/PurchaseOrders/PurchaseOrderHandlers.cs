using System.Globalization;
using System.Text.Json;
using Rochell.Finance.Policies;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Time;

namespace Rochell.Procurement.PurchaseOrders;

[RequiresPermission("purchase_order:create")]
public sealed class CreatePurchaseOrderHandler : ICommandHandler<CreatePurchaseOrder>
{
    public string CommandType => "Procurement.CreatePurchaseOrder";

    public async Task<string> HandleAsync(CreatePurchaseOrder command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        await PurchaseOrderStore.ValidateAsync(context, command.PartyId, command.OrderDate, command.Lines, cancellationToken).ConfigureAwait(false);
        var creator = await PurchaseOrderStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var poId = context.ResultRef;
        var poNo = string.Create(CultureInfo.InvariantCulture, $"OC-{command.OrderDate.Year:D4}-{poId.ToString("N")[^8..].ToUpperInvariant()}");

        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "PurchaseOrderCreated",
                1,
                PurchaseOrderStore.Aggregate,
                poId,
                1,
                JsonSerializer.Serialize(new
                {
                    poId,
                    poNo,
                    partyId = command.PartyId,
                    plantId = command.PlantId,
                    orderDate = command.OrderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    lines = command.Lines.Select(l => new { itemId = l.ItemId, uom = l.Uom, quantity = l.Quantity.ToString(CultureInfo.InvariantCulture), unitPrice = l.UnitPrice.ToString(CultureInfo.InvariantCulture) }),
                }),
                Publish: false),
            cancellationToken).ConfigureAwait(false);

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO pur.purchase_order (po_id, company_id, po_no, party_id, plant_id, order_date, status, created_by, version)
            VALUES (@id, @c, @no, @party, @plant, @date, 'DRAFT', @creator, 1)
            """,
            cancellationToken,
            ("id", poId),
            ("c", context.CompanyId),
            ("no", poNo),
            ("party", command.PartyId),
            ("plant", command.PlantId),
            ("date", command.OrderDate),
            ("creator", creator)).ConfigureAwait(false);
        await PurchaseOrderStore.InsertLinesAsync(context, poId, command.Lines, cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(PurchaseOrderStore.Aggregate, poId, "DOCUMENT", null, PurchaseOrderStatus.Draft, CommandType, eventId, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Serialize(new { purchaseOrderId = poId, poNo, status = PurchaseOrderStatus.Draft, version = 1 });
    }
}

[RequiresPermission("purchase_order:create")]
public sealed class UpdatePurchaseOrderDraftHandler : ICommandHandler<UpdatePurchaseOrderDraft>
{
    public string CommandType => "Procurement.UpdatePurchaseOrderDraft";

    public async Task<string> HandleAsync(UpdatePurchaseOrderDraft command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var header = await PurchaseOrderStore.LockAsync(context, command.PurchaseOrderId, command.PlantId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        PurchaseOrderStore.RequireStatus(header, PurchaseOrderStatus.Draft);
        await PurchaseOrderStore.ValidateAsync(context, header.PartyId, header.OrderDate, command.Lines, cancellationToken).ConfigureAwait(false);

        var newVersion = header.Version + 1;
        await context.AppendEventAsync(
            new EventDraft(
                "PurchaseOrderDraftUpdated",
                1,
                PurchaseOrderStore.Aggregate,
                header.Id,
                newVersion,
                JsonSerializer.Serialize(new { poId = header.Id, lines = command.Lines.Select(l => new { itemId = l.ItemId, uom = l.Uom, quantity = l.Quantity.ToString(CultureInfo.InvariantCulture), unitPrice = l.UnitPrice.ToString(CultureInfo.InvariantCulture) }) }),
                Publish: false),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "DELETE FROM pur.purchase_order_line WHERE po_id = @p", cancellationToken, ("p", header.Id)).ConfigureAwait(false);
        await PurchaseOrderStore.InsertLinesAsync(context, header.Id, command.Lines, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE pur.purchase_order SET version = @v WHERE po_id = @p", cancellationToken, ("v", newVersion), ("p", header.Id)).ConfigureAwait(false);

        return JsonSerializer.Serialize(new { purchaseOrderId = header.Id, status = header.Status, version = newVersion });
    }
}

[RequiresPermission("purchase_order:submit")]
public sealed class SubmitPurchaseOrderHandler : ICommandHandler<SubmitPurchaseOrder>
{
    public string CommandType => "Procurement.SubmitPurchaseOrder";

    public async Task<string> HandleAsync(SubmitPurchaseOrder command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var header = await PurchaseOrderStore.LockAsync(context, command.PurchaseOrderId, command.PlantId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        PurchaseOrderStore.RequireStatus(header, PurchaseOrderStatus.Draft);
        await PurchaseOrderStore.TransitionAsync(context, header, PurchaseOrderStatus.PendingApproval, CommandType, "PurchaseOrderSubmitted", new { poId = header.Id }, publish: true, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { purchaseOrderId = header.Id, status = PurchaseOrderStatus.PendingApproval, version = header.Version + 1 });
    }
}

/// <summary>
/// §11.1 Approve: approver ≠ creator; the purchasing approver is limited by <c>po_approval_limit</c> (the Controller is not);
/// re-authentication above <c>po_approval_step_up_threshold</c> (E-PR08-2); copies the receipt tolerance and policy version.
/// </summary>
[RequiresPermission("purchase_order:approve")]
public sealed class ApprovePurchaseOrderHandler : ICommandHandler<ApprovePurchaseOrder>
{
    public string CommandType => "Procurement.ApprovePurchaseOrder";

    public async Task<string> HandleAsync(ApprovePurchaseOrder command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var header = await PurchaseOrderStore.LockAsync(context, command.PurchaseOrderId, command.PlantId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        PurchaseOrderStore.RequireStatus(header, PurchaseOrderStatus.PendingApproval);
        var approver = await PurchaseOrderStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (approver == header.CreatedBy)
        {
            throw new DomainException(ProcurementErrors.ApproverIsCreator, "The person who created the purchase order cannot approve it (SC-01).");
        }

        var today = BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);
        var policy = await PolicyResolver.ResolveAsync(context, PolicyCodes.Purchasing, today, cancellationToken).ConfigureAwait(false);
        var total = await TotalAsync(context, header.Id, cancellationToken).ConfigureAwait(false);
        if (!await IsControllerAsync(context, approver, header.PlantId, cancellationToken).ConfigureAwait(false)
            && total > policy.Decimal(PolicyParameters.PoApprovalLimit))
        {
            throw new DomainException(ProcurementErrors.ApprovalLimitExceeded, $"The order total {total} exceeds the approval limit of the purchasing approver.");
        }

        if (total > policy.Decimal(PolicyParameters.PoApprovalStepUpThreshold))
        {
            await context.RequireStepUpAsync(cancellationToken).ConfigureAwait(false);
        }

        var tolerance = policy.Decimal(PolicyParameters.ReceiptTolerancePct);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE pur.purchase_order_line SET receipt_tolerance_pct = @t, version = version + 1 WHERE po_id = @p",
            cancellationToken,
            ("t", tolerance),
            ("p", header.Id)).ConfigureAwait(false);
        await PurchaseOrderStore.TransitionAsync(
            context,
            header,
            PurchaseOrderStatus.Approved,
            CommandType,
            "PurchaseOrderApproved",
            new { poId = header.Id, approvedBy = approver, total = total.ToString(CultureInfo.InvariantCulture), policyVersionId = policy.PolicyVersionId, receiptTolerancePct = tolerance.ToString(CultureInfo.InvariantCulture) },
            publish: true,
            cancellationToken,
            extraSet: ", approved_by = @approver, approved_at = @at, policy_version_id = @policy",
            extraParameters: [("approver", approver), ("at", context.Clock.UtcNow), ("policy", policy.PolicyVersionId)]).ConfigureAwait(false);

        return JsonSerializer.Serialize(new { purchaseOrderId = header.Id, status = PurchaseOrderStatus.Approved, version = header.Version + 1 });
    }

    private static async Task<decimal> TotalAsync(CommandContext context, Guid poId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(context.Connection, context.Transaction, "SELECT coalesce(sum(qty_ordered * unit_price), 0) FROM pur.purchase_order_line WHERE po_id = @p", ("p", poId));
        return (decimal)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task<bool> IsControllerAsync(CommandContext context, Guid userId, Guid plantId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT EXISTS (
              SELECT 1 FROM iam.role_assignment ra JOIN iam.role r USING (role_id)
              WHERE ra.company_id = @c AND ra.user_id = @u AND r.code = 'CONTROLLER'
                AND ra.valid_from <= @now AND (ra.valid_to IS NULL OR ra.valid_to > @now)
                AND (ra.plant_id IS NULL OR ra.plant_id = @plant))
            """,
            ("c", context.CompanyId),
            ("u", userId),
            ("now", context.Clock.UtcNow),
            ("plant", plantId));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }
}

[RequiresPermission("purchase_order:approve")]
public sealed class RejectPurchaseOrderHandler : ICommandHandler<RejectPurchaseOrder>
{
    public string CommandType => "Procurement.RejectPurchaseOrder";

    public async Task<string> HandleAsync(RejectPurchaseOrder command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = PurchaseOrderStore.RequireReason(command.Reason);
        var header = await PurchaseOrderStore.LockAsync(context, command.PurchaseOrderId, command.PlantId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        PurchaseOrderStore.RequireStatus(header, PurchaseOrderStatus.PendingApproval);
        await PurchaseOrderStore.TransitionAsync(context, header, PurchaseOrderStatus.Draft, CommandType, "PurchaseOrderRejected", new { poId = header.Id, reason }, publish: false, cancellationToken, reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { purchaseOrderId = header.Id, status = PurchaseOrderStatus.Draft, version = header.Version + 1 });
    }
}

[RequiresPermission("purchase_order:cancel")]
public sealed class CancelPurchaseOrderHandler : ICommandHandler<CancelPurchaseOrder>
{
    public string CommandType => "Procurement.CancelPurchaseOrder";

    public async Task<string> HandleAsync(CancelPurchaseOrder command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = PurchaseOrderStore.RequireReason(command.Reason);
        var header = await PurchaseOrderStore.LockAsync(context, command.PurchaseOrderId, command.PlantId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        PurchaseOrderStore.RequireStatus(header, PurchaseOrderStatus.Draft, PurchaseOrderStatus.PendingApproval, PurchaseOrderStatus.Approved);
        await using (var received = Sql.Command(context.Connection, context.Transaction, "SELECT coalesce(sum(qty_received), 0) FROM pur.purchase_order_line WHERE po_id = @p", ("p", header.Id)))
        {
            if ((decimal)(await received.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! != 0)
            {
                throw new DomainException(ProcurementErrors.AlreadyReceived, "Orders with receipts cannot be cancelled.");
            }
        }

        await PurchaseOrderStore.TransitionAsync(context, header, PurchaseOrderStatus.Cancelled, CommandType, "PurchaseOrderCancelled", new { poId = header.Id, reason }, publish: true, cancellationToken, reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { purchaseOrderId = header.Id, status = PurchaseOrderStatus.Cancelled, version = header.Version + 1 });
    }
}

[RequiresPermission("purchase_order:approve_over_receipt", StepUp = true)]
public sealed class ApproveOverReceiptHandler : ICommandHandler<ApproveOverReceipt>
{
    public string CommandType => "Procurement.ApproveOverReceipt";

    public async Task<string> HandleAsync(ApproveOverReceipt command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = PurchaseOrderStore.RequireReason(command.Reason);
        if (command.AdditionalQuantity <= 0 || decimal.Round(command.AdditionalQuantity, 6) != command.AdditionalQuantity)
        {
            throw new DomainException(ProcurementErrors.QuantityInvalid, "The additional quantity must be positive with at most 6 decimals.");
        }

        var header = await PurchaseOrderStore.LockAsync(context, command.PurchaseOrderId, command.PlantId, null, cancellationToken).ConfigureAwait(false);
        PurchaseOrderStore.RequireStatus(header, PurchaseOrderStatus.Approved, PurchaseOrderStatus.PartiallyReceived, PurchaseOrderStatus.Received);

        long lineVersion;
        await using (var line = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT version FROM pur.purchase_order_line WHERE po_id = @p AND po_line_id = @l FOR UPDATE",
            ("p", header.Id),
            ("l", command.PurchaseOrderLineId)))
        {
            lineVersion = await line.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long v
                ? v
                : throw new DomainException(ProcurementErrors.LineNotFound, "The line does not belong to this purchase order.");
        }

        await context.AppendEventAsync(
            new EventDraft(
                "PurchaseOrderOverReceiptApproved",
                1,
                "PurchaseOrderLine",
                command.PurchaseOrderLineId,
                lineVersion + 1,
                JsonSerializer.Serialize(new { poId = header.Id, poLineId = command.PurchaseOrderLineId, additionalQuantity = command.AdditionalQuantity.ToString(CultureInfo.InvariantCulture), reason }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE pur.purchase_order_line SET qty_over_receipt_approved = qty_over_receipt_approved + @q, version = version + 1 WHERE po_line_id = @l",
            cancellationToken,
            ("q", command.AdditionalQuantity),
            ("l", command.PurchaseOrderLineId)).ConfigureAwait(false);

        return JsonSerializer.Serialize(new { purchaseOrderId = header.Id, poLineId = command.PurchaseOrderLineId });
    }
}
