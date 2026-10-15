using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Rochell.Finance.Policies;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Time;
using Rochell.Procurement.PurchaseOrders;

namespace Rochell.Procurement.GoodsReceipts;

// REC-02 · Direct receipt without a purchase order (E-REC-6…8, E-REC-02-1…7).

/// <summary>One material received without an order: quantity and unit price in <paramref name="Uom"/> (base unit or a valid conversion).</summary>
public sealed record DirectReceiptLineInput(Guid ItemId, string Uom, decimal Quantity, decimal UnitPrice);

/// <summary>
/// E-REC-6: receives raw material without a purchase order. Core creates behind it an order marked DIRECT_RECEIPT at that price and
/// quantity. When every line's price is within <c>direct_receipt_price_deviation_pct</c> of its reference (E-REC-02-2) the service
/// identity «Recepción directa» approves the order and the receipt posts in the same step (E-REC-02-1); otherwise the order waits for
/// approval and nothing enters inventory until it is approved (E-REC-02-4). Inventory items in pesos only (E-REC-02-5).
/// </summary>
public sealed record PostDirectGoodsReceipt(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid PlantId,
    Guid PartyId,
    Guid LocationId,
    DateTime OccurredAt,
    IReadOnlyList<DirectReceiptLineInput> Lines,
    Guid? VehicleId,
    Guid? DriverId,
    string? SupplierDeliveryNote,
    string? WeighTicketRef = null) : IPlantScopedCommand;

/// <summary>E-REC-02-2: where a line's reference price comes from.</summary>
public static class DirectReceiptPriceSource
{
    public const string LastPurchase = "LAST_PURCHASE";
    public const string Standard = "STANDARD";
}

public sealed record DirectReceiptReference(decimal? Price, string? Source, string? SourcePoNo);

/// <summary>Shared rules of the direct receipt: the reference price, the service identity, posting on approval and release.</summary>
public static class DirectReceipts
{
    /// <summary>E-REC-02-1: the service identity that approves a direct receipt's order within the price policy (migration 0126).</summary>
    public static readonly Guid ServiceUserId = Guid.Parse("00000000-0000-7000-8000-00000000d006");

    /// <summary>E-REC-02-3: PURCHASING parameter; without it every direct receipt waits for approval.</summary>
    public const string PriceDeviationParameter = "direct_receipt_price_deviation_pct";

    public const string Origin = "DIRECT_RECEIPT";

    /// <summary>
    /// E-REC-02-2: the last price of the item from that supplier in that unit on an approved order in pesos (not cancelled); without
    /// one, the material's standard price in the most recent ACTIVE standard cost that uses it, when the unit is its base unit.
    /// </summary>
    public static async Task<DirectReceiptReference> ReferenceAsync(
        DbConnection connection, DbTransaction transaction, Guid companyId, Guid partyId, Guid itemId, string uom, CancellationToken cancellationToken)
    {
        var last = await Reading.ListAsync(
            connection,
            transaction,
            """
            SELECT l.unit_price, po.po_no
            FROM pur.purchase_order_line l
            JOIN pur.purchase_order po ON po.po_id = l.po_id
            WHERE po.company_id = @c AND po.party_id = @p AND l.item_id = @i AND l.uom = @u AND po.currency = 'DOP'
              AND po.status IN ('APPROVED', 'PARTIALLY_RECEIVED', 'RECEIVED', 'CLOSED')
            ORDER BY po.approved_at DESC, po.po_no DESC
            LIMIT 1
            """,
            r => (r.GetDecimal(0), r.GetString(1)),
            cancellationToken,
            ("c", companyId),
            ("p", partyId),
            ("i", itemId),
            ("u", uom)).ConfigureAwait(false);
        if (last.Count == 1)
        {
            return new DirectReceiptReference(last[0].Item1, DirectReceiptPriceSource.LastPurchase, last[0].Item2);
        }

        var standard = await Reading.ListAsync(
            connection,
            transaction,
            """
            SELECT m.std_price
            FROM md.standard_cost_material m
            JOIN md.standard_cost_version v ON v.cost_version_id = m.cost_version_id
            JOIN md.item i ON i.item_id = m.material_item_id
            WHERE m.company_id = @c AND m.material_item_id = @i AND v.status = 'ACTIVE' AND i.base_uom = @u
            ORDER BY v.effective_from DESC, v.cost_version_id DESC
            LIMIT 1
            """,
            r => r.GetDecimal(0),
            cancellationToken,
            ("c", companyId),
            ("i", itemId),
            ("u", uom)).ConfigureAwait(false);
        return standard.Count == 1 ? new DirectReceiptReference(standard[0], DirectReceiptPriceSource.Standard, null) : new DirectReceiptReference(null, null, null);
    }

    /// <summary>|price − reference| ≤ reference × allowed; no reference or no allowed deviation means approval (E-REC-02-2/3).</summary>
    public static bool WithinPolicy(decimal price, decimal? reference, decimal? allowed)
        => reference is { } r && allowed is { } a && Math.Abs(price - r) <= r * a;

    /// <summary>The deviation as a fraction of the reference, 4 decimals, or null without a reference.</summary>
    public static decimal? Deviation(decimal price, decimal? reference)
        => reference is { } r && r > 0 ? decimal.Round((price - r) / r, 4, MidpointRounding.AwayFromZero) : null;

    /// <summary>Whether the order has a direct receipt still open (waiting for approval).</summary>
    internal static async Task<bool> IsOpenAsync(CommandContext context, Guid poId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection, context.Transaction, "SELECT EXISTS (SELECT 1 FROM pur.direct_receipt WHERE po_id = @p AND closed_at IS NULL)", ("p", poId));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    /// <summary>Closes the order's open direct receipt: posted as <paramref name="grId"/>, or released (rejected or cancelled) with null.</summary>
    internal static Task CloseAsync(CommandContext context, Guid poId, Guid? grId, CancellationToken cancellationToken)
        => Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE pur.direct_receipt SET gr_id = @gr, closed_at = @now, version = version + 1 WHERE po_id = @p AND closed_at IS NULL",
            cancellationToken,
            ("gr", (object?)grId ?? DBNull.Value),
            ("now", context.Clock.UtcNow),
            ("p", poId));

    /// <summary>
    /// E-REC-02-4: once its order is approved, the open direct receipt posts in the same command with the arrival time it was entered
    /// with and every line at its ordered quantity (as <paramref name="goodsReceiptId"/> when given). Returns the receipt's result, or null when the order has no open direct receipt.
    /// </summary>
    internal static async Task<JsonElement?> PostOnApprovalAsync(
        CommandContext context, Guid poId, Guid plantId, string idempotencyKey, string commandType, CancellationToken cancellationToken, Guid? goodsReceiptId = null)
    {
        var held = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT location_id, occurred_at, vehicle_id, driver_id, supplier_delivery_note, weigh_ticket_ref FROM pur.direct_receipt WHERE po_id = @p AND closed_at IS NULL FOR UPDATE",
            r => (Location: r.GetGuid(0), OccurredAt: r.GetFieldValue<DateTime>(1), Vehicle: r.GetGuid(2), Driver: r.GetGuid(3), Note: r.GetString(4), Ticket: r.NullableString(5)),
            cancellationToken,
            ("p", poId)).ConfigureAwait(false);
        if (held.Count == 0)
        {
            return null;
        }

        var d = held[0];
        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT po_line_id, qty_ordered FROM pur.purchase_order_line WHERE po_id = @p ORDER BY line_no",
            r => new GoodsReceiptLineInput(r.GetGuid(0), r.GetDecimal(1)),
            cancellationToken,
            ("p", poId)).ConfigureAwait(false);
        var grId = goodsReceiptId ?? context.Ids.NewId();
        var result = await new PostGoodsReceiptHandler().PostAsync(
            new PostGoodsReceipt(context.CompanyId, context.SessionId, idempotencyKey, plantId, poId, d.Location, d.OccurredAt, lines, d.Ticket, d.Vehicle, d.Driver, d.Note),
            context,
            grId,
            commandType,
            poId,
            cancellationToken).ConfigureAwait(false);
        await CloseAsync(context, poId, grId, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(result).RootElement.Clone();
    }
}

[RequiresPermission("goods_receipt:direct")]
public sealed class PostDirectGoodsReceiptHandler : ICommandHandler<PostDirectGoodsReceipt>
{
    public string CommandType => "Procurement.PostDirectGoodsReceipt";

    public async Task<string> HandleAsync(PostDirectGoodsReceipt command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var occurredAt = Precision.ToMicroseconds(command.OccurredAt);
        if (occurredAt > context.Clock.UtcNow)
        {
            throw new DomainException(ProcurementErrors.OccurredInFuture, "The receipt time cannot be in the future.");
        }

        var businessDate = BusinessCalendar.DefaultBusinessDate(occurredAt);
        var weighTicket = string.IsNullOrWhiteSpace(command.WeighTicketRef) ? null : command.WeighTicketRef.Trim();
        var deliveryNote = await PostGoodsReceiptHandler.ValidateTransportAsync(context, command.VehicleId, command.DriverId, command.SupplierDeliveryNote, cancellationToken).ConfigureAwait(false);
        var lines = command.Lines ?? [];
        var orderLines = lines.Select(l => new PurchaseOrderLineInput(l.ItemId, (l.Uom ?? string.Empty).Trim(), l.Quantity, l.UnitPrice)).ToList();
        await PurchaseOrderStore.ValidateAsync(context, command.PartyId, businessDate, orderLines, cancellationToken).ConfigureAwait(false);
        if (orderLines.Select(l => (l.ItemId, l.Uom)).Distinct().Count() != orderLines.Count)
        {
            throw new DomainException(ProcurementErrors.DuplicateLine, "Each material and unit can appear only once per direct receipt.");
        }

        await PostGoodsReceiptHandler.EnsureLocationInPlantAsync(context, command.LocationId, command.PlantId, cancellationToken).ConfigureAwait(false);
        await PostGoodsReceiptHandler.EnsureDeliveryNoteUnusedAsync(context, command.PartyId, deliveryNote, null, cancellationToken).ConfigureAwait(false);

        // E-REC-02-2/3: each line against its reference price.
        var today = BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);
        var policy = await PolicyResolver.ResolveAsync(context, PolicyCodes.Purchasing, today, cancellationToken).ConfigureAwait(false);
        decimal? allowed = policy.Has(DirectReceipts.PriceDeviationParameter) ? policy.Decimal(DirectReceipts.PriceDeviationParameter) : null;
        var checks = new List<(PurchaseOrderLineInput Line, DirectReceiptReference Reference, bool Within)>();
        foreach (var line in orderLines)
        {
            var reference = await DirectReceipts.ReferenceAsync(context.Connection, context.Transaction, context.CompanyId, command.PartyId, line.ItemId, line.Uom, cancellationToken).ConfigureAwait(false);
            checks.Add((line, reference, DirectReceipts.WithinPolicy(line.UnitPrice, reference.Price, allowed)));
        }

        var requiresApproval = checks.Any(c => !c.Within);
        var priceCheck = checks.Select((c, i) => new
        {
            lineNo = i + 1,
            itemId = c.Line.ItemId,
            unitPrice = c.Line.UnitPrice.ToString(CultureInfo.InvariantCulture),
            referencePrice = c.Reference.Price?.ToString(CultureInfo.InvariantCulture),
            source = c.Reference.Source,
            sourcePoNo = c.Reference.SourcePoNo,
            deviationPct = DirectReceipts.Deviation(c.Line.UnitPrice, c.Reference.Price)?.ToString(CultureInfo.InvariantCulture),
            allowedPct = allowed?.ToString(CultureInfo.InvariantCulture),
            withinPolicy = c.Within,
        }).ToList();

        // E-REC-6: the order behind the receipt, created by the receiver and sent to approval at once.
        var creator = await PurchaseOrderStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var poId = context.Ids.NewId();
        var poNo = await DocumentNumbers.NextAsync(context, DocumentNumbers.PurchaseOrder, businessDate.Year, cancellationToken).ConfigureAwait(false);
        var createdEvent = await context.AppendEventAsync(
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
                    orderDate = businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    origin = DirectReceipts.Origin,
                    lines = orderLines.Select(l => new { itemId = l.ItemId, uom = l.Uom, quantity = l.Quantity.ToString(CultureInfo.InvariantCulture), unitPrice = l.UnitPrice.ToString(CultureInfo.InvariantCulture) }),
                }),
                Publish: false),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO pur.purchase_order (po_id, company_id, po_no, party_id, plant_id, order_date, status, created_by, version, origin)
            VALUES (@id, @c, @no, @party, @plant, @date, 'DRAFT', @creator, 1, @origin)
            """,
            cancellationToken,
            ("id", poId),
            ("c", context.CompanyId),
            ("no", poNo),
            ("party", command.PartyId),
            ("plant", command.PlantId),
            ("date", businessDate),
            ("creator", creator),
            ("origin", DirectReceipts.Origin)).ConfigureAwait(false);
        await PurchaseOrderStore.InsertLinesAsync(context, poId, orderLines, cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(PurchaseOrderStore.Aggregate, poId, "DOCUMENT", null, PurchaseOrderStatus.Draft, CommandType, createdEvent, cancellationToken).ConfigureAwait(false);
        var header = new PurchaseOrderHeader(poId, poNo, command.PartyId, command.PlantId, businessDate, PurchaseOrderStatus.Draft, creator, 1);
        await PurchaseOrderStore.TransitionAsync(context, header, PurchaseOrderStatus.PendingApproval, CommandType, "PurchaseOrderSubmitted", new { poId, origin = DirectReceipts.Origin }, publish: true, cancellationToken).ConfigureAwait(false);

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO pur.direct_receipt (po_id, company_id, location_id, occurred_at, vehicle_id, driver_id, supplier_party_id, supplier_delivery_note, weigh_ticket_ref,
                                            received_by, requires_approval, price_check, version)
            VALUES (@po, @c, @location, @occurred, @vehicle, @driver, @supplier, @note, @ticket, @by, @requires, CAST(@check AS jsonb), 1)
            """,
            cancellationToken,
            ("po", poId),
            ("c", context.CompanyId),
            ("location", command.LocationId),
            ("occurred", occurredAt),
            ("vehicle", command.VehicleId),
            ("driver", command.DriverId),
            ("supplier", command.PartyId),
            ("note", deliveryNote),
            ("ticket", weighTicket),
            ("by", creator),
            ("requires", requiresApproval),
            ("check", JsonSerializer.Serialize(priceCheck))).ConfigureAwait(false);

        if (requiresApproval)
        {
            return JsonSerializer.Serialize(new { purchaseOrderId = poId, poNo, status = PurchaseOrderStatus.PendingApproval, requiresApproval, priceCheck });
        }

        // E-REC-02-1: within the policy the service identity approves, and the receipt posts now.
        var tolerance = policy.Decimal(PolicyParameters.ReceiptTolerancePct);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE pur.purchase_order_line SET receipt_tolerance_pct = @t, version = version + 1 WHERE po_id = @p",
            cancellationToken,
            ("t", tolerance),
            ("p", poId)).ConfigureAwait(false);
        await PurchaseOrderStore.TransitionAsync(
            context,
            header with { Status = PurchaseOrderStatus.PendingApproval, Version = 2 },
            PurchaseOrderStatus.Approved,
            CommandType,
            "PurchaseOrderApproved",
            new { poId, approvedBy = DirectReceipts.ServiceUserId, policyVersionId = policy.PolicyVersionId, receiptTolerancePct = tolerance.ToString(CultureInfo.InvariantCulture), origin = DirectReceipts.Origin },
            publish: true,
            cancellationToken,
            extraSet: ", approved_by = @approver, approved_at = @at, policy_version_id = @policy",
            extraParameters: [("approver", DirectReceipts.ServiceUserId), ("at", context.Clock.UtcNow), ("policy", policy.PolicyVersionId)]).ConfigureAwait(false);

        var receipt = await DirectReceipts.PostOnApprovalAsync(context, poId, command.PlantId, command.IdempotencyKey, CommandType, cancellationToken, context.ResultRef).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { purchaseOrderId = poId, poNo, status = "POSTED", requiresApproval, priceCheck, receipt });
    }
}
