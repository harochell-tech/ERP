using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Procurement.PurchaseOrders;

internal sealed record PurchaseOrderHeader(Guid Id, string PoNo, Guid PartyId, Guid PlantId, DateOnly OrderDate, string Status, Guid CreatedBy, long Version);

/// <summary>Shared rules of the purchase order handlers (§11.1): locking, checks and state transitions with history (ADR-027).</summary>
internal static class PurchaseOrderStore
{
    public const string Aggregate = "PurchaseOrder";
    private const decimal MaxQuantity = 999_999_999_999.999999m; // type-limit: numeric(18,6)
    private const decimal MaxUnitPrice = 9_999_999_999_999.999999m; // type-limit: numeric(19,6)

    public static async Task<Guid> SessionUserAsync(CommandContext context, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(context.Connection, context.Transaction, "SELECT user_id FROM iam.session WHERE session_id = @s", ("s", context.SessionId));
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>Locks the header (lock order level 3), checks plant scope and optimistic version.</summary>
    public static async Task<PurchaseOrderHeader> LockAsync(CommandContext context, Guid poId, Guid plantId, long? expectedVersion, CancellationToken cancellationToken)
    {
        PurchaseOrderHeader header;
        await using (var command = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT po_id, po_no, party_id, plant_id, order_date, status::text, created_by, version FROM pur.purchase_order WHERE company_id = @c AND po_id = @p FOR UPDATE",
            ("c", context.CompanyId),
            ("p", poId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new DomainException(ProcurementErrors.NotFound, "The purchase order does not exist.");
            }

            header = new PurchaseOrderHeader(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetFieldValue<DateOnly>(4), reader.GetString(5), reader.GetGuid(6), reader.GetInt64(7));
        }

        if (header.PlantId != plantId)
        {
            throw new DomainException(ProcurementErrors.PlantMismatch, "The purchase order belongs to another plant.");
        }

        if (expectedVersion is not null && header.Version != expectedVersion)
        {
            throw new DomainException(ProcurementErrors.VersionConflict, $"The purchase order changed (version {header.Version}, expected {expectedVersion}); reload and retry.");
        }

        return header;
    }

    public static void RequireStatus(PurchaseOrderHeader header, params string[] allowed)
    {
        if (!allowed.Contains(header.Status, StringComparer.Ordinal))
        {
            throw new DomainException(ProcurementErrors.InvalidState, $"The purchase order is {header.Status}; this action needs {string.Join(" or ", allowed)}.");
        }
    }

    public static string RequireReason(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? throw new DomainException(ProcurementErrors.ReasonRequired, "A reason is required.") : reason.Trim();

    /// <summary>Validates supplier and lines for create/update (Patch 1.1 P-1: price &gt; 0; E-PR08-4: UOM convertible to base).</summary>
    public static async Task ValidateAsync(CommandContext context, Guid partyId, DateOnly orderDate, IReadOnlyList<PurchaseOrderLineInput> lines, CancellationToken cancellationToken)
    {
        await using (var supplier = Sql.Command(
            context.Connection,
            context.Transaction,
            "SELECT EXISTS (SELECT 1 FROM md.party WHERE company_id = @c AND party_id = @p AND status = 'ACTIVE' AND is_supplier)",
            ("c", context.CompanyId),
            ("p", partyId)))
        {
            if (await supplier.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                throw new DomainException(ProcurementErrors.SupplierNotActive, "The supplier does not exist or is not ACTIVE.");
            }
        }

        if (lines is null || lines.Count == 0)
        {
            throw new DomainException(ProcurementErrors.LinesRequired, "A purchase order needs at least one line.");
        }

        foreach (var line in lines)
        {
            if (line.Quantity <= 0 || line.Quantity > MaxQuantity || decimal.Round(line.Quantity, 6) != line.Quantity)
            {
                throw new DomainException(ProcurementErrors.QuantityInvalid, "Quantities must be positive with at most 6 decimals.");
            }

            if (line.UnitPrice <= 0 || line.UnitPrice > MaxUnitPrice || decimal.Round(line.UnitPrice, 6) != line.UnitPrice)
            {
                throw new DomainException(ProcurementErrors.PriceInvalid, "Unit prices must be greater than zero with at most 6 decimals (Patch 1.1).");
            }

            await using var item = Sql.Command(
                context.Connection,
                context.Transaction,
                """
                SELECT i.status::text = 'ACTIVE',
                       i.base_uom = @uom OR EXISTS (
                         SELECT 1 FROM md.uom_conversion c
                         WHERE c.company_id = i.company_id AND c.item_id = i.item_id AND c.from_uom = @uom AND c.to_uom = i.base_uom
                           AND c.effective_from <= @date AND (c.effective_to IS NULL OR c.effective_to > @date))
                FROM md.item i WHERE i.company_id = @c AND i.item_id = @i
                """,
                ("uom", line.Uom),
                ("date", orderDate),
                ("c", context.CompanyId),
                ("i", line.ItemId));
            await using var reader = await item.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || !reader.GetBoolean(0))
            {
                throw new DomainException(ProcurementErrors.ItemNotActive, "Every item must exist and be ACTIVE.");
            }

            if (!reader.GetBoolean(1))
            {
                throw new DomainException(ProcurementErrors.UomNotConvertible, $"Unit '{line.Uom}' is not the item's base unit and has no conversion valid on {orderDate:yyyy-MM-dd}.");
            }
        }
    }

    public static async Task InsertLinesAsync(CommandContext context, Guid poId, IReadOnlyList<PurchaseOrderLineInput> lines, CancellationToken cancellationToken)
    {
        var lineNo = 0;
        foreach (var line in lines)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO pur.purchase_order_line (po_line_id, company_id, po_id, line_no, item_id, uom, qty_ordered, unit_price, receipt_tolerance_pct, version)
                VALUES (@id, @c, @po, @no, @item, @uom, @qty, @price, 0, 1)
                """,
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("po", poId),
                ("no", ++lineNo),
                ("item", line.ItemId),
                ("uom", line.Uom),
                ("qty", line.Quantity),
                ("price", line.UnitPrice)).ConfigureAwait(false);
        }
    }

    /// <summary>§11.1 receipt-driven status: APPROVED when nothing remains received, RECEIVED when every line is complete, otherwise PARTIALLY_RECEIVED.</summary>
    public static async Task<string> StatusFromReceiptsAsync(CommandContext context, Guid poId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT CASE WHEN bool_and(qty_received = 0) THEN 'APPROVED'
                        WHEN bool_and(qty_received >= qty_ordered) THEN 'RECEIVED'
                        ELSE 'PARTIALLY_RECEIVED' END
            FROM pur.purchase_order_line WHERE po_id = @p
            """,
            ("p", poId));
        return (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>Changes status with its event and state_history row (ADR-027). Extra SET fragments are constant SQL.</summary>
    public static async Task TransitionAsync(
        CommandContext context,
        PurchaseOrderHeader header,
        string toStatus,
        string commandType,
        string eventType,
        object payload,
        bool publish,
        CancellationToken cancellationToken,
        string? reason = null,
        string extraSet = "",
        params (string Name, object? Value)[] extraParameters)
    {
        var newVersion = header.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(eventType, 1, Aggregate, header.Id, newVersion, JsonSerializer.Serialize(payload), publish),
            cancellationToken).ConfigureAwait(false);
        var parameters = new List<(string Name, object? Value)> { ("status", toStatus), ("version", newVersion), ("id", header.Id) };
        parameters.AddRange(extraParameters);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE pur.purchase_order SET status = CAST(@status AS pur.po_status), version = @version" + extraSet + " WHERE po_id = @id",
            cancellationToken,
            [.. parameters]).ConfigureAwait(false);
        await context.AppendStateAsync(Aggregate, header.Id, "DOCUMENT", header.Status, toStatus, commandType, eventId, cancellationToken, reason).ConfigureAwait(false);
    }
}
