using System.Text.Json;
using System.Text.RegularExpressions;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Sales.Customers;
using Rochell.Sales.Orders;
using Rochell.Sales.Proformas;
using Rochell.Sales.Receipts;
using Rochell.Tax;

namespace Rochell.Sales.CashSales;

public static class CashSaleErrors
{
    public const string NotCashSale = "NOT_A_CASH_SALE";
    public const string BuyerInvalid = "BUYER_INVALID";

    /// <summary>E-CF1-01-6: without the CONSUMER_ID_THRESHOLD rule in force no sale to the final consumer goes to payment.</summary>
    public const string ThresholdRuleMissing = "CONSUMER_ID_RULE_MISSING";

    /// <summary>E-CF1-3: from the rule's amount the buyer's identification is mandatory.</summary>
    public const string BuyerIdRequired = "BUYER_ID_REQUIRED";

    public const string ExceedsDue = "ALLOCATION_EXCEEDS_DUE";

    /// <summary>E-CF1-4: nothing is confirmed or dispatched before the order is paid in full with money that counts.</summary>
    public const string NotPaid = "CASH_SALE_NOT_PAID";
}

/// <summary>
/// E-CF1-1…7, E-CF1-01-1…3: a sale to the final consumer — a DRAFT order of the company's «Consumidor final» (created by the first
/// sale), with who buys. Prices come from the list in force, as on any order.
/// </summary>
public sealed record CreateCashSale(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid PlantId, string DeliveryTermCode, string? SiteAddress, DateOnly? RequestedDate, IReadOnlyList<SalesOrderLineInput> Lines,
    string? BuyerName = null, string? BuyerPhone = null, string? BuyerIdKind = null, string? BuyerId = null) : ICommand;

/// <summary>Replaces a DRAFT cash sale's header, lines and buyer.</summary>
public sealed record UpdateCashSaleDraft(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SalesOrderId, long ExpectedVersion, Guid PlantId, string DeliveryTermCode, string? SiteAddress, DateOnly? RequestedDate,
    IReadOnlyList<SalesOrderLineInput> Lines, string? BuyerName = null, string? BuyerPhone = null, string? BuyerIdKind = null, string? BuyerId = null) : ICommand;

/// <summary>
/// E-CF1-01-3/4/6: DRAFT → PENDING_PAYMENT. What must be paid is fixed here: the net plus the ITBIS of the rules in force today.
/// From the amount of the CONSUMER_ID_THRESHOLD rule the buyer's identification is required (E-CF1-02-3: compared with the order's
/// total with ITBIS).
/// </summary>
public sealed record SubmitCashSaleForPayment(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SalesOrderId, long ExpectedVersion) : ICommand;

/// <summary>PENDING_PAYMENT → DRAFT, to correct the sale; nothing may be assigned to it.</summary>
public sealed record ReturnCashSaleToDraft(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SalesOrderId, long ExpectedVersion) : ICommand;

/// <summary>
/// E-CF1-5, E-CF1-01-5: assigns a receipt of the final consumer to a cash order, up to what is still to pay; no journal. When the
/// money that counts covers the order it is CONFIRMED at once (E-CF1-02-1).
/// </summary>
public sealed record AllocateReceiptToOrder(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ReceiptId, long ExpectedVersion, Guid SalesOrderId, decimal Amount) : ICommand;

/// <summary>Releases one assignment (its event) of a receipt to an order that is still PENDING_PAYMENT, with a reason.</summary>
public sealed record ReleaseOrderAllocation(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ReceiptId, Guid AllocationEventId, string Reason) : ICommand;

/// <summary>
/// E-CF1-02-1: «Verificar pago». A cheque counts only once its deposit is matched with the bank statement, which happens in
/// Treasury; this confirms the PENDING_PAYMENT order when the money that counts covers it.
/// </summary>
public sealed record ConfirmCashSale(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid SalesOrderId) : ICommand;

/// <summary>Shared reads and writes of cash sales. Lock order: sales order → receipt.</summary>
internal static partial class CashSaleStore
{
    public const string ConsumerName = "Consumidor final";

    /// <summary>A live assignment of a receipt to an order: never released (no mirror row).</summary>
    public sealed record Live(Guid AllocationId, Guid ReceiptId, Guid SalesOrderId, string OrderNo, decimal Amount, Guid EventId);

    /// <summary>
    /// The company's final consumer (E-CF1-01-1), created on first use: an ACTIVE customer of kind CONSUMER, without RNC. Serialized
    /// by an advisory lock so two first sales create one.
    /// </summary>
    public static async Task<Guid> ConsumerAsync(CommandContext context, string commandType, CancellationToken cancellationToken)
    {
        await SalesSql.LockAsync(context, "final-consumer", cancellationToken).ConfigureAwait(false);
        var existing = await SalesSql.ScalarAsync<Guid?>(
            context, "SELECT party_id FROM md.party WHERE company_id = @c AND party_kind = 'CONSUMER'", cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false);
        if (existing is { } known)
        {
            return known;
        }

        var id = context.Ids.NewId();
        var eventId = await context.AppendEventAsync(
            new EventDraft("FinalConsumerCreated", 1, Customers.Customers.Aggregate, id, 1, JsonSerializer.Serialize(new { partyId = id, legalName = ConsumerName }), Publish: true), cancellationToken)
            .ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO md.party (party_id, company_id, party_kind, rnc, legal_name, is_supplier, status, version, is_customer, customer_status)
            VALUES (@id, @c, 'CONSUMER', NULL, @name, false, 'ACTIVE', 1, true, 'ACTIVE')
            """,
            cancellationToken,
            ("id", id),
            ("c", context.CompanyId),
            ("name", ConsumerName)).ConfigureAwait(false);
        await context.AppendStateAsync(Customers.Customers.PartyAggregate, id, "DOCUMENT", null, "ACTIVE", commandType, eventId, cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(Customers.Customers.Aggregate, id, "DOCUMENT", null, "ACTIVE", commandType, eventId, cancellationToken).ConfigureAwait(false);
        return id;
    }

    /// <summary>E-CF1-2, E-CF1-01-2: name and phone are free text; the identification is a cédula (11 digits), an RNC (9) or a passport.</summary>
    public static Orders.Orders.Buyer Buyer(string? name, string? phone, string? idKind, string? id)
    {
        var kind = string.IsNullOrWhiteSpace(idKind) ? null : idKind.Trim().ToUpperInvariant();
        var number = string.IsNullOrWhiteSpace(id) ? null : id.Trim().ToUpperInvariant();
        if (kind is "CEDULA" or "RNC")
        {
            number = number is null ? null : new string([.. number.Where(char.IsAsciiDigit)]);
        }

        var valid = (kind, number) switch
        {
            (null, null) => true,
            ("CEDULA", { Length: 11 }) => true,
            ("RNC", { Length: 9 }) => true,
            ("PASAPORTE", not null) => Passport().IsMatch(number),
            _ => false,
        };
        return valid
            ? new Orders.Orders.Buyer(SalesSql.Optional(name, 150, "The buyer's name"), SalesSql.Optional(phone, 30, "The buyer's phone"), kind, number)
            : throw new DomainException(CashSaleErrors.BuyerInvalid, "The buyer's identification is a cédula (11 digits), an RNC (9 digits) or a passport (5 to 20 letters and digits), with its kind.");
    }

    public static Orders.Orders.Row RequireCash(Orders.Orders.Row row)
        => row.CashSale ? row : throw new DomainException(CashSaleErrors.NotCashSale, $"{row.OrderNo} is not a cash sale.");

    /// <summary>
    /// E-CF1-4, E-CF1-02-1: the money that counts towards the order — its live assignments of receipts that are RECORDED and, for a
    /// cheque, whose deposit is matched with the bank statement, plus what its invoices already took (live applications).
    /// </summary>
    public static async Task<decimal> PaidAsync(CommandContext context, Guid orderId, CancellationToken cancellationToken)
        => await SalesSql.ScalarAsync<decimal?>(
            context,
            """
            SELECT (coalesce((SELECT sum(x.amount) FROM fin.order_allocation x JOIN fin.receipt r ON r.receipt_id = x.receipt_id
                              WHERE x.sales_order_id = @o AND x.reverses_allocation_id IS NULL
                                AND NOT EXISTS (SELECT 1 FROM fin.order_allocation u WHERE u.reverses_allocation_id = x.allocation_id)
                                AND r.status = 'RECORDED' AND (r.method <> 'CHEQUE' OR r.bank_status = 'MATCHED')), 0)
                  + coalesce((SELECT sum(a.amount) FROM fin.ar_application a JOIN sal.invoice i ON i.ar_doc_id = a.ar_doc_id
                              WHERE a.reverses_application_id IS NULL
                                AND NOT EXISTS (SELECT 1 FROM fin.ar_application u WHERE u.reverses_application_id = a.application_id)
                                AND EXISTS (SELECT 1 FROM sal.invoice_line il JOIN log.delivery_line dl ON dl.delivery_line_id = il.delivery_line_id
                                            JOIN log.delivery d ON d.delivery_id = dl.delivery_id
                                            WHERE il.invoice_id = i.invoice_id AND d.sales_order_id = @o)), 0))::numeric(19,2)
            """,
            cancellationToken,
            ("o", orderId)).ConfigureAwait(false) ?? 0m;

    /// <summary>E-CF1-02-2: a cash order dispatches only while it is covered; a bounced cheque or a reversed receipt stops it.</summary>
    public static async Task EnsureCoveredAsync(CommandContext context, Guid orderId, Orders.Orders.Row order, CancellationToken cancellationToken)
    {
        if (!order.CashSale)
        {
            return;
        }

        var paid = await PaidAsync(context, orderId, cancellationToken).ConfigureAwait(false);
        if (paid < order.PaymentTotal)
        {
            throw new DomainException(
                CashSaleErrors.NotPaid,
                $"{order.OrderNo} is a cash sale: {Receipting.Money(paid)} of {Receipting.Money(order.PaymentTotal ?? 0m)} is collected with money that counts (a cheque counts once its deposit is matched with the bank statement).");
        }
    }

    private const string LiveSelect = """
        SELECT x.allocation_id, x.receipt_id, x.sales_order_id, o.order_no, x.amount::numeric(19,2), x.event_id
        FROM fin.order_allocation x JOIN sal.sales_order o ON o.sales_order_id = x.sales_order_id
        WHERE x.company_id = @c AND x.reverses_allocation_id IS NULL
          AND NOT EXISTS (SELECT 1 FROM fin.order_allocation u WHERE u.reverses_allocation_id = x.allocation_id)
        """;

    private static Live Map(System.Data.Common.DbDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetDecimal(4), r.GetGuid(5));

    public static Task<List<Live>> LiveOfReceiptAsync(CommandContext context, Guid receiptId, CancellationToken cancellationToken)
        => Reading.ListAsync(
            context.Connection, context.Transaction, LiveSelect + " AND x.receipt_id = @r ORDER BY x.event_id, x.allocation_id", Map, cancellationToken,
            ("c", context.CompanyId), ("r", receiptId));

    public static async Task LockOrdersAsync(CommandContext context, IEnumerable<Guid> orderIds, CancellationToken cancellationToken)
    {
        foreach (var id in orderIds.Distinct().Order())
        {
            await Orders.Orders.LockCurrentAsync(context, id, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Releases live assignments of one receipt: the mirror rows, the orders' and the receipt's assigned amounts back, and a
    /// ReceiptOrderAllocationReleased event with the receipt's next version. The caller holds the locks of the orders and the receipt.
    /// </summary>
    public static async Task<Guid> ReleaseAsync(
        CommandContext context, Guid receiptId, string receiptNo, IReadOnlyList<Live> allocations, long version, string reason, string commandType, CancellationToken cancellationToken)
    {
        var amount = allocations.Sum(a => a.Amount);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ReceiptOrderAllocationReleased",
                1,
                Receipting.Aggregate,
                receiptId,
                version,
                JsonSerializer.Serialize(new
                {
                    receiptId,
                    receiptNo,
                    orders = allocations.Select(a => new { salesOrderId = a.SalesOrderId, orderNo = a.OrderNo, amount = Receipting.Money(a.Amount) }),
                    amount = Receipting.Money(amount),
                    reason,
                    command = commandType,
                }),
                Publish: true,
                BusinessDate: SalesSql.Today(context)),
            cancellationToken).ConfigureAwait(false);
        foreach (var a in allocations)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO fin.order_allocation (allocation_id, company_id, receipt_id, sales_order_id, amount, event_id, reverses_allocation_id)
                VALUES (@id, @c, @r, @o, @a, @e, @original)
                """,
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("r", receiptId),
                ("o", a.SalesOrderId),
                ("a", a.Amount),
                ("e", eventId),
                ("original", a.AllocationId)).ConfigureAwait(false);
            await MoveAllocatedAsync(context, a.SalesOrderId, -a.Amount, cancellationToken).ConfigureAwait(false);
        }

        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE fin.receipt SET allocated_amount = allocated_amount - @a, version = @v WHERE receipt_id = @r", cancellationToken,
            ("a", amount), ("v", version), ("r", receiptId)).ConfigureAwait(false);
        return eventId;
    }

    public static Task MoveAllocatedAsync(CommandContext context, Guid orderId, decimal delta, CancellationToken cancellationToken)
        => Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE sal.sales_order SET allocated_amount = allocated_amount + @d, version = version + 1 WHERE sales_order_id = @o", cancellationToken,
            ("d", delta), ("o", orderId));

    /// <summary>PENDING_PAYMENT → CONFIRMED when the money that counts covers what must be paid. Returns whether it confirmed.</summary>
    public static async Task<bool> ConfirmIfPaidAsync(CommandContext context, Guid orderId, string commandType, CancellationToken cancellationToken)
    {
        var order = await Orders.Orders.LockCurrentAsync(context, orderId, cancellationToken).ConfigureAwait(false);
        if (order.Status != "PENDING_PAYMENT" || await PaidAsync(context, orderId, cancellationToken).ConfigureAwait(false) < order.PaymentTotal)
        {
            return false;
        }

        await Orders.Orders.TransitionAsync(
            context, orderId, order, "CONFIRMED", "CashSaleConfirmed", new { salesOrderId = orderId, orderNo = order.OrderNo, paymentTotal = Orders.Orders.M(order.PaymentTotal ?? 0m) }, commandType,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    [GeneratedRegex("^[A-Z0-9]{5,20}$")]
    private static partial Regex Passport();
}

[RequiresPermission("cash_sale:create")]
public sealed class CreateCashSaleHandler : ICommandHandler<CreateCashSale>
{
    public string CommandType => "Sales.CreateCashSale";

    public async Task<string> HandleAsync(CreateCashSale command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var header = Orders.Orders.ValidateHeader(command.DeliveryTermCode, command.SiteAddress, command.RequestedDate, null, command.PlantId);
        var buyer = CashSaleStore.Buyer(command.BuyerName, command.BuyerPhone, command.BuyerIdKind, command.BuyerId);
        var (list, lines, total) = await Orders.Orders.PriceAsync(context, header.PlantId, command.Lines, cancellationToken).ConfigureAwait(false);
        var consumer = await CashSaleStore.ConsumerAsync(context, CommandType, cancellationToken).ConfigureAwait(false);
        var orderNo = await Orders.Orders.InsertAsync(context, context.ResultRef, consumer, header, list, lines, total, null, CommandType, cancellationToken, buyer: buyer).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { salesOrderId = context.ResultRef, orderNo, status = "DRAFT", totalNet = Orders.Orders.M(total), version = 1 });
    }
}

[RequiresPermission("cash_sale:create")]
public sealed class UpdateCashSaleDraftHandler : ICommandHandler<UpdateCashSaleDraft>
{
    public string CommandType => "Sales.UpdateCashSaleDraft";

    public async Task<string> HandleAsync(UpdateCashSaleDraft command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var header = Orders.Orders.ValidateHeader(command.DeliveryTermCode, command.SiteAddress, command.RequestedDate, null, command.PlantId);
        var buyer = CashSaleStore.Buyer(command.BuyerName, command.BuyerPhone, command.BuyerIdKind, command.BuyerId);
        var row = CashSaleStore.RequireCash(await Orders.Orders.LockAsync(context, command.SalesOrderId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false));
        if (row.Status != "DRAFT")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The sale is {row.Status}; only a DRAFT changes.");
        }

        var quoted = await Orders.Orders.QuotedPricesAsync(context, command.SalesOrderId, cancellationToken).ConfigureAwait(false);
        var (list, lines, total) = await Orders.Orders.PriceAsync(context, header.PlantId, command.Lines, cancellationToken, quoted).ConfigureAwait(false);
        var version = row.Version + 1;
        var linesVersion = row.LinesVersion + 1;
        await context.AppendEventAsync(
            new EventDraft(
                "SalesOrderDraftUpdated",
                1,
                Orders.Orders.Aggregate,
                command.SalesOrderId,
                version,
                JsonSerializer.Serialize(new
                {
                    salesOrderId = command.SalesOrderId,
                    linesVersion,
                    totalNet = Orders.Orders.M(total),
                    lines = lines.Count,
                    buyer = new { name = buyer.Name, phone = buyer.Phone, idKind = buyer.IdKind, id = buyer.Id },
                }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            UPDATE sal.sales_order SET plant_id = @plant, delivery_term_code = @term, site_address = @site, requested_date = @req, price_list_version_id = @list, total_net = @total,
              lines_version = @lv, buyer_name = @bname, buyer_phone = @bphone, buyer_id_kind = @bkind, buyer_id = @bid, version = @v
            WHERE sales_order_id = @o
            """,
            cancellationToken,
            ("plant", header.PlantId),
            ("term", header.Term),
            ("site", header.Site),
            ("req", header.Requested),
            ("list", list),
            ("total", total),
            ("lv", linesVersion),
            ("bname", buyer.Name),
            ("bphone", buyer.Phone),
            ("bkind", buyer.IdKind),
            ("bid", buyer.Id),
            ("v", version),
            ("o", command.SalesOrderId)).ConfigureAwait(false);
        await Orders.Orders.WriteLinesAsync(context, command.SalesOrderId, linesVersion, lines, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { salesOrderId = command.SalesOrderId, status = "DRAFT", totalNet = Orders.Orders.M(total), version });
    }
}

[RequiresPermission("cash_sale:create")]
public sealed class SubmitCashSaleForPaymentHandler : ICommandHandler<SubmitCashSaleForPayment>
{
    public string CommandType => "Sales.SubmitCashSaleForPayment";

    public async Task<string> HandleAsync(SubmitCashSaleForPayment command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = CashSaleStore.RequireCash(await Orders.Orders.LockAsync(context, command.SalesOrderId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false));
        if (row.Status != "DRAFT")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The sale is {row.Status}.");
        }

        var today = SalesSql.Today(context);
        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.line_id, i.item_category, l.net_amount::numeric(19,2)
            FROM sal.sales_order o JOIN sal.sales_order_line l ON l.sales_order_id = o.sales_order_id AND l.lines_version = o.lines_version JOIN md.item i ON i.item_id = l.item_id
            WHERE o.sales_order_id = @o ORDER BY l.line_no
            """,
            r => new TaxableLine(r.GetGuid(0), r.GetString(1), r.GetDecimal(2)),
            cancellationToken,
            ("o", command.SalesOrderId)).ConfigureAwait(false);
        var itbis = (await TaxEngine.PreviewSalesItbisAsync(context.Connection, context.Transaction, context.CompanyId, today, lines, cancellationToken).ConfigureAwait(false)).Sum(t => t.Amount);
        var toPay = decimal.Round(row.Total + itbis, 2);
        var threshold = await TaxEngine.ConsumerIdThresholdAsync(context.Connection, context.Transaction, context.CompanyId, today, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(
                CashSaleErrors.ThresholdRuleMissing,
                $"No CONSUMER_ID_THRESHOLD fiscal rule is in force on {today:yyyy-MM-dd}: the amount from which a consumer's sale must identify its buyer has to be configured and activated (E-CF1-01-6).");
        if (toPay >= threshold && row.BuyerId is null)
        {
            throw new DomainException(
                CashSaleErrors.BuyerIdRequired, $"A sale of {Receipting.Money(toPay)} needs the buyer's identification: it is mandatory from {Receipting.Money(threshold)} (E-CF1-3).");
        }

        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "CashSaleSentToPayment",
                1,
                Orders.Orders.Aggregate,
                command.SalesOrderId,
                version,
                JsonSerializer.Serialize(new { salesOrderId = command.SalesOrderId, orderNo = row.OrderNo, totalNet = Orders.Orders.M(row.Total), itbis = Orders.Orders.M(itbis), paymentTotal = Orders.Orders.M(toPay) }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE sal.sales_order SET status = 'PENDING_PAYMENT', payment_total = @t, version = @v WHERE sales_order_id = @o", cancellationToken,
            ("t", toPay), ("v", version), ("o", command.SalesOrderId)).ConfigureAwait(false);
        await context.AppendStateAsync(Orders.Orders.Aggregate, command.SalesOrderId, "DOCUMENT", "DRAFT", "PENDING_PAYMENT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { salesOrderId = command.SalesOrderId, status = "PENDING_PAYMENT", itbis = Orders.Orders.M(itbis), paymentTotal = Orders.Orders.M(toPay), version });
    }
}

[RequiresPermission("cash_sale:create")]
public sealed class ReturnCashSaleToDraftHandler : ICommandHandler<ReturnCashSaleToDraft>
{
    public string CommandType => "Sales.ReturnCashSaleToDraft";

    public async Task<string> HandleAsync(ReturnCashSaleToDraft command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = CashSaleStore.RequireCash(await Orders.Orders.LockAsync(context, command.SalesOrderId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false));
        if (row.Status != "PENDING_PAYMENT" || row.Allocated > 0m)
        {
            throw new DomainException(SalesErrors.InvalidState, row.Status != "PENDING_PAYMENT" ? $"The sale is {row.Status}." : $"{row.OrderNo} has receipts assigned: release them first.");
        }

        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft("CashSaleReturnedToDraft", 1, Orders.Orders.Aggregate, command.SalesOrderId, version, JsonSerializer.Serialize(new { salesOrderId = command.SalesOrderId, orderNo = row.OrderNo }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE sal.sales_order SET status = 'DRAFT', payment_total = NULL, version = @v WHERE sales_order_id = @o", cancellationToken,
            ("v", version), ("o", command.SalesOrderId)).ConfigureAwait(false);
        await context.AppendStateAsync(Orders.Orders.Aggregate, command.SalesOrderId, "DOCUMENT", "PENDING_PAYMENT", "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { salesOrderId = command.SalesOrderId, status = "DRAFT", version });
    }
}

[RequiresPermission("receipt:apply")]
public sealed class AllocateReceiptToOrderHandler : ICommandHandler<AllocateReceiptToOrder>
{
    public string CommandType => "Sales.AllocateReceiptToOrder";

    public async Task<string> HandleAsync(AllocateReceiptToOrder command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var amount = SalesSql.Positive(command.Amount, 2, "The assigned amount");
        var order = CashSaleStore.RequireCash(await Orders.Orders.LockCurrentAsync(context, command.SalesOrderId, cancellationToken).ConfigureAwait(false));
        var receipt = await Receipting.LockAsync(context, command.ReceiptId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (order.Status is not ("PENDING_PAYMENT" or "CONFIRMED" or "PARTIALLY_DELIVERED"))
        {
            throw new DomainException(SalesErrors.InvalidState, $"{order.OrderNo} is {order.Status}: receipts are assigned to a sale sent to payment.");
        }

        if (receipt.Status != "RECORDED" || receipt.PartyId != order.PartyId)
        {
            throw new DomainException(SalesErrors.InvalidState, receipt.Status != "RECORDED" ? $"The receipt is {receipt.Status}." : $"{receipt.No} is another customer's receipt.");
        }

        var available = receipt.Unapplied - receipt.Allocated;
        if (amount > available)
        {
            throw new DomainException(AllocationErrors.ExceedsAvailable, $"{Receipting.Money(amount)} exceeds the {Receipting.Money(available)} of {receipt.No} that is neither applied nor assigned.");
        }

        // What is still to pay: what must be paid, less every live assignment (whether its money counts yet or not) and what the
        // order's invoices already took.
        var taken = await SalesSql.ScalarAsync<decimal?>(
            context,
            """
            SELECT coalesce(sum(a.amount), 0)::numeric(19,2) FROM fin.ar_application a JOIN sal.invoice i ON i.ar_doc_id = a.ar_doc_id
            WHERE a.reverses_application_id IS NULL AND NOT EXISTS (SELECT 1 FROM fin.ar_application u WHERE u.reverses_application_id = a.application_id)
              AND EXISTS (SELECT 1 FROM sal.invoice_line il JOIN log.delivery_line dl ON dl.delivery_line_id = il.delivery_line_id JOIN log.delivery d ON d.delivery_id = dl.delivery_id
                          WHERE il.invoice_id = i.invoice_id AND d.sales_order_id = @o)
            """,
            cancellationToken,
            ("o", command.SalesOrderId)).ConfigureAwait(false) ?? 0m;
        var due = (order.PaymentTotal ?? 0m) - order.Allocated - taken;
        if (amount > due)
        {
            throw new DomainException(CashSaleErrors.ExceedsDue, $"{Receipting.Money(amount)} exceeds the {Receipting.Money(due)} still to pay on {order.OrderNo}.");
        }

        var version = receipt.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ReceiptAllocatedToOrder",
                1,
                Receipting.Aggregate,
                command.ReceiptId,
                version,
                JsonSerializer.Serialize(new { receiptId = command.ReceiptId, receiptNo = receipt.No, salesOrderId = command.SalesOrderId, orderNo = order.OrderNo, amount = Receipting.Money(amount) }),
                Publish: true,
                BusinessDate: SalesSql.Today(context)),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "INSERT INTO fin.order_allocation (allocation_id, company_id, receipt_id, sales_order_id, amount, event_id) VALUES (@id, @c, @r, @o, @a, @e)",
            cancellationToken,
            ("id", context.Ids.NewId()),
            ("c", context.CompanyId),
            ("r", command.ReceiptId),
            ("o", command.SalesOrderId),
            ("a", amount),
            ("e", eventId)).ConfigureAwait(false);
        await CashSaleStore.MoveAllocatedAsync(context, command.SalesOrderId, amount, cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE fin.receipt SET allocated_amount = allocated_amount + @a, version = @v WHERE receipt_id = @r", cancellationToken,
            ("a", amount), ("v", version), ("r", command.ReceiptId)).ConfigureAwait(false);
        var confirmed = await CashSaleStore.ConfirmIfPaidAsync(context, command.SalesOrderId, CommandType, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            receiptId = command.ReceiptId,
            allocationEventId = eventId,
            salesOrderId = command.SalesOrderId,
            stillToPay = Receipting.Money(due - amount),
            orderStatus = confirmed ? "CONFIRMED" : order.Status,
            version,
        });
    }
}

[RequiresPermission("receipt:apply")]
public sealed class ReleaseOrderAllocationHandler : ICommandHandler<ReleaseOrderAllocation>
{
    public string CommandType => "Sales.ReleaseOrderAllocation";

    public async Task<string> HandleAsync(ReleaseOrderAllocation command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = Receipting.Reason(command.Reason);

        // The orders of the event are locked before the receipt; an assignment released meanwhile leaves nothing to release.
        var before = (await CashSaleStore.LiveOfReceiptAsync(context, command.ReceiptId, cancellationToken).ConfigureAwait(false)).Where(a => a.EventId == command.AllocationEventId).ToList();
        foreach (var id in before.Select(a => a.SalesOrderId).Distinct().Order())
        {
            var order = await Orders.Orders.LockCurrentAsync(context, id, cancellationToken).ConfigureAwait(false);
            if (order.Status != "PENDING_PAYMENT")
            {
                throw new DomainException(SalesErrors.InvalidState, $"{order.OrderNo} is {order.Status}: an assignment is released while the sale waits for its payment.");
            }
        }

        var receipt = await Receipting.LockAsync(context, command.ReceiptId, null, cancellationToken).ConfigureAwait(false);
        var live = (await CashSaleStore.LiveOfReceiptAsync(context, command.ReceiptId, cancellationToken).ConfigureAwait(false)).Where(a => a.EventId == command.AllocationEventId).ToList();
        if (live.Count == 0 || !live.Select(a => a.AllocationId).Order().SequenceEqual(before.Select(a => a.AllocationId).Order()))
        {
            throw new DomainException(AllocationErrors.NotFound, "The receipt has no live assignment with that event.");
        }

        var version = receipt.Version + 1;
        var eventId = await CashSaleStore.ReleaseAsync(context, command.ReceiptId, receipt.No, live, version, reason, CommandType, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { receiptId = command.ReceiptId, releaseEventId = eventId, released = Receipting.Money(live.Sum(a => a.Amount)), version });
    }
}

[RequiresPermission("cash_sale:create")]
public sealed class ConfirmCashSaleHandler : ICommandHandler<ConfirmCashSale>
{
    public string CommandType => "Sales.ConfirmCashSale";

    public async Task<string> HandleAsync(ConfirmCashSale command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var order = CashSaleStore.RequireCash(await Orders.Orders.LockCurrentAsync(context, command.SalesOrderId, cancellationToken).ConfigureAwait(false));
        if (order.Status != "PENDING_PAYMENT")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The sale is {order.Status}.");
        }

        if (!await CashSaleStore.ConfirmIfPaidAsync(context, command.SalesOrderId, CommandType, cancellationToken).ConfigureAwait(false))
        {
            await CashSaleStore.EnsureCoveredAsync(context, command.SalesOrderId, order, cancellationToken).ConfigureAwait(false);
        }

        return JsonSerializer.Serialize(new { salesOrderId = command.SalesOrderId, status = "CONFIRMED", version = order.Version + 1 });
    }
}
