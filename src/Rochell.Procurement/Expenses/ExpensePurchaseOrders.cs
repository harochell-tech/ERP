using System.Globalization;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Procurement.PurchaseOrders;
using Rochell.Procurement.SupplierInvoices;
using Rochell.Tax;

namespace Rochell.Procurement.Expenses;

/// <summary>
/// E-GAS-05-1: one line of an expense order — what is bought, its category, its tax type, the quantity and the agreed price. A foreign
/// supplier's order is in USD and its lines carry no tax type (E-USD1-03-2/3).
/// </summary>
public sealed record ExpenseOrderLineInput(string Description, Guid ExpenseCategoryId, Guid? TaxTypeId, decimal Quantity, decimal UnitPrice);

/// <summary>
/// E-GAS-05-1: a DRAFT purchase order of expenses — never received in the warehouse (E-GAS-05-5); submitted, approved, rejected and
/// cancelled with the commands of any order.
/// </summary>
public sealed record CreateExpensePurchaseOrder(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid PartyId, DateOnly OrderDate, IReadOnlyList<ExpenseOrderLineInput> Lines) : IPlantScopedCommand;

/// <summary>Replaces the lines of a DRAFT expense order.</summary>
public sealed record UpdateExpensePurchaseOrderDraft(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid PurchaseOrderId, long ExpectedVersion, IReadOnlyList<ExpenseOrderLineInput> Lines) : IPlantScopedCommand;

/// <summary>E-GAS-05-4: Compras closes an APPROVED expense order before it is billed in full, with a reason.</summary>
public sealed record CloseExpensePurchaseOrder(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, Guid PurchaseOrderId, long ExpectedVersion, string Reason)
    : IPlantScopedCommand;

/// <summary>Shared rules of expense orders.</summary>
internal static class ExpensePurchaseOrders
{
    /// <summary>
    /// An ACTIVE supplier, 1 to 200 lines, an ACTIVE category and a tax type in force on <paramref name="date"/> for every line (none for a
    /// foreign supplier). Returns the lines and the order's currency.
    /// </summary>
    public static async Task<(List<(string Description, decimal Quantity, decimal UnitPrice, decimal Net)> Lines, string Currency)> ValidateAsync(
        CommandContext context, Guid partyId, DateOnly date, IReadOnlyList<ExpenseOrderLineInput> lines, CancellationToken cancellationToken)
    {
        if (lines is not { Count: > 0 and <= 200 })
        {
            throw new DomainException(ProcurementErrors.LinesRequired, "An expense order has 1 to 200 lines.");
        }

        var valid = lines.Select(l => ExpenseInvoices.ValidLine(new ExpenseLineInput(l.Description, l.ExpenseCategoryId, l.TaxTypeId, l.Quantity, l.UnitPrice))).ToList();
        var currency = await ExpenseInvoices.RequireSupplierAndTypesAsync(
            context, partyId, date, lines.Select(l => l.ExpenseCategoryId), [.. lines.Select(l => l.TaxTypeId)], cancellationToken).ConfigureAwait(false);
        return (valid, currency);
    }

    public static async Task InsertLinesAsync(
        CommandContext context, Guid poId, IReadOnlyList<ExpenseOrderLineInput> lines, IReadOnlyList<(string Description, decimal Quantity, decimal UnitPrice, decimal Net)> valid,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO pur.purchase_order_line (po_line_id, company_id, po_id, line_no, item_id, uom, qty_ordered, unit_price, receipt_tolerance_pct, version, description, expense_category_id, tax_rule_id)
                VALUES (@id, @c, @po, @no, NULL, NULL, @qty, @price, 0, 1, @description, @category, @tax)
                """,
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("po", poId),
                ("no", i + 1),
                ("qty", valid[i].Quantity),
                ("price", valid[i].UnitPrice),
                ("description", valid[i].Description),
                ("category", lines[i].ExpenseCategoryId),
                ("tax", lines[i].TaxTypeId)).ConfigureAwait(false);
        }
    }

    public static object Payload(IReadOnlyList<ExpenseOrderLineInput> lines)
        => lines.Select(l => new
        {
            description = l.Description,
            expenseCategoryId = l.ExpenseCategoryId,
            taxTypeId = l.TaxTypeId,
            quantity = l.Quantity.ToString(CultureInfo.InvariantCulture),
            unitPrice = l.UnitPrice.ToString(CultureInfo.InvariantCulture),
        });
}

[RequiresPermission("purchase_order:create")]
public sealed class CreateExpensePurchaseOrderHandler : ICommandHandler<CreateExpensePurchaseOrder>
{
    public string CommandType => "Procurement.CreateExpensePurchaseOrder";

    public async Task<string> HandleAsync(CreateExpensePurchaseOrder command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var (valid, currency) = await ExpensePurchaseOrders.ValidateAsync(context, command.PartyId, command.OrderDate, command.Lines, cancellationToken).ConfigureAwait(false);
        var creator = await PurchaseOrderStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var poId = context.ResultRef;
        var poNo = await DocumentNumbers.NextAsync(context, DocumentNumbers.PurchaseOrder, command.OrderDate.Year, cancellationToken).ConfigureAwait(false);
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
                    docClass = SupplierInvoiceClasses.Expense,
                    partyId = command.PartyId,
                    plantId = command.PlantId,
                    orderDate = command.OrderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    currency,
                    lines = ExpensePurchaseOrders.Payload(command.Lines),
                }),
                Publish: false),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO pur.purchase_order (po_id, company_id, po_no, party_id, plant_id, order_date, status, created_by, version, doc_class, currency)
            VALUES (@id, @c, @no, @party, @plant, @date, 'DRAFT', @creator, 1, 'EXPENSE', @currency)
            """,
            cancellationToken,
            ("id", poId),
            ("c", context.CompanyId),
            ("no", poNo),
            ("party", command.PartyId),
            ("plant", command.PlantId),
            ("date", command.OrderDate),
            ("creator", creator),
            ("currency", currency)).ConfigureAwait(false);
        await ExpensePurchaseOrders.InsertLinesAsync(context, poId, command.Lines, valid, cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(PurchaseOrderStore.Aggregate, poId, "DOCUMENT", null, PurchaseOrderStatus.Draft, CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { purchaseOrderId = poId, poNo, status = PurchaseOrderStatus.Draft, currency, version = 1 });
    }
}

[RequiresPermission("purchase_order:create")]
public sealed class UpdateExpensePurchaseOrderDraftHandler : ICommandHandler<UpdateExpensePurchaseOrderDraft>
{
    public string CommandType => "Procurement.UpdateExpensePurchaseOrderDraft";

    public async Task<string> HandleAsync(UpdateExpensePurchaseOrderDraft command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var header = await PurchaseOrderStore.LockAsync(context, command.PurchaseOrderId, command.PlantId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        PurchaseOrderStore.RequireStatus(header, PurchaseOrderStatus.Draft);
        if (header.DocClass != SupplierInvoiceClasses.Expense)
        {
            throw new DomainException(ProcurementErrors.InvalidState, "An inventory order is corrected with UpdatePurchaseOrderDraft (E-GAS-01-1).");
        }

        var (valid, _) = await ExpensePurchaseOrders.ValidateAsync(context, header.PartyId, header.OrderDate, command.Lines, cancellationToken).ConfigureAwait(false);
        var version = header.Version + 1;
        await context.AppendEventAsync(
            new EventDraft(
                "PurchaseOrderDraftUpdated", 1, PurchaseOrderStore.Aggregate, header.Id, version,
                JsonSerializer.Serialize(new { poId = header.Id, lines = ExpensePurchaseOrders.Payload(command.Lines) }), Publish: false),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "DELETE FROM pur.purchase_order_line WHERE po_id = @p", cancellationToken, ("p", header.Id)).ConfigureAwait(false);
        await ExpensePurchaseOrders.InsertLinesAsync(context, header.Id, command.Lines, valid, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(context.Connection, context.Transaction, "UPDATE pur.purchase_order SET version = @v WHERE po_id = @p", cancellationToken, ("v", version), ("p", header.Id)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { purchaseOrderId = header.Id, status = header.Status, version });
    }
}

[RequiresPermission("purchase_order:cancel")]
public sealed class CloseExpensePurchaseOrderHandler : ICommandHandler<CloseExpensePurchaseOrder>
{
    public string CommandType => "Procurement.CloseExpensePurchaseOrder";

    public async Task<string> HandleAsync(CloseExpensePurchaseOrder command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = PurchaseOrderStore.RequireReason(command.Reason);
        var header = await PurchaseOrderStore.LockAsync(context, command.PurchaseOrderId, command.PlantId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        PurchaseOrderStore.RequireStatus(header, PurchaseOrderStatus.Approved);
        if (header.DocClass != SupplierInvoiceClasses.Expense)
        {
            throw new DomainException(ProcurementErrors.InvalidState, "Only an expense order is closed by hand; an inventory order closes through its receipts.");
        }

        await PurchaseOrderStore.TransitionAsync(
            context, header, PurchaseOrderStatus.Closed, CommandType, "PurchaseOrderClosed", new { poId = header.Id, reason }, publish: true, cancellationToken, reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { purchaseOrderId = header.Id, status = PurchaseOrderStatus.Closed, version = header.Version + 1 });
    }
}
