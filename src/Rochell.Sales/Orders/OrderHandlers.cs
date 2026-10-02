using System.Globalization;
using System.Text.Json;
using Rochell.Finance.Policies;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Sales.Orders;

public static class OrderErrors
{
    public const string PriceListMissing = "PRICE_LIST_MISSING";
    public const string PriceMissing = "PRICE_MISSING";
    public const string CustomerNotActive = "CUSTOMER_NOT_ACTIVE";
    public const string TermsMissing = "CUSTOMER_TERMS_REQUIRED";
    public const string SiteRequired = "SITE_ADDRESS_REQUIRED";
    public const string ReasonRequired = "REASON_REQUIRED";

    /// <summary>E-CF1-4: the final consumer's orders are cash sales, with their own commands.</summary>
    public const string UseCashSale = "USE_CASH_SALE";
}

internal static class Orders
{
    public const string Aggregate = "SalesOrder";
    public const string CreditPolicy = "CREDIT";
    public const string OverdueDaysBlock = "overdue_days_block";

    public sealed record Header(Guid PlantId, string Term, string? Site, DateOnly? Requested, string? PoRef);

    public sealed record PricedLine(int LineNo, Guid ItemId, string Uom, decimal Quantity, decimal UnitPrice, decimal Net);

    public static Header ValidateHeader(string? term, string? site, DateOnly? requested, string? poRef, Guid plantId)
    {
        var t = (term ?? string.Empty).Trim().ToUpperInvariant();
        if (t is not (DeliveryTerms.PickupAtPlant or DeliveryTerms.DeliveredOwnTransport))
        {
            throw new DomainException(SalesErrors.FieldInvalid, "The delivery term is PICKUP_AT_PLANT or DELIVERED_OWN_TRANSPORT (E-VS3-5).");
        }

        var s = SalesSql.Optional(site, 300, "The site address");
        if (t == DeliveryTerms.DeliveredOwnTransport && s is null)
        {
            throw new DomainException(OrderErrors.SiteRequired, "An order delivered to the site needs the site address.");
        }

        return new Header(plantId, t, s, requested, SalesSql.Optional(poRef, 60, "The customer's PO reference"));
    }

    /// <summary>
    /// E-VS3-03-5: prices from the list in force for (item, unit); a missing price refuses the order. E-QUO1-03-4: an (item, unit) of
    /// the quote an order came from keeps its quoted price in <paramref name="quoted"/>.
    /// </summary>
    public static Task<(Guid PriceListVersionId, List<PricedLine> Lines, decimal Total)> PriceAsync(
        CommandContext context, Guid plantId, IReadOnlyList<SalesOrderLineInput>? input, CancellationToken cancellationToken,
        IReadOnlyDictionary<(Guid ItemId, string Uom), decimal>? quoted = null)
        => PriceAsync(context.Connection, context.Transaction, context.CompanyId, plantId, input, cancellationToken, quoted);

    /// <summary>The same pricing on any connection (E-UX4-3: the read-only preview of an order runs it too).</summary>
    public static async Task<(Guid PriceListVersionId, List<PricedLine> Lines, decimal Total)> PriceAsync(
        System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction, Guid companyId, Guid plantId, IReadOnlyList<SalesOrderLineInput>? input,
        CancellationToken cancellationToken, IReadOnlyDictionary<(Guid ItemId, string Uom), decimal>? quoted = null)
    {
        var lines = input ?? [];
        if (lines.Count == 0)
        {
            throw new DomainException(SalesErrors.LinesRequired, "An order has at least one line.");
        }

        if (await SalesSql.ScalarAsync<Guid?>(connection, transaction, "SELECT plant_id FROM md.plant WHERE company_id = @c AND plant_id = @p", cancellationToken, ("c", companyId), ("p", plantId)).ConfigureAwait(false) is null)
        {
            throw new DomainException(SalesErrors.NotFound, "The plant does not exist.");
        }

        var list = await SalesSql.ScalarAsync<Guid?>(
            connection, transaction, "SELECT price_list_version_id FROM sal.price_list_version WHERE company_id = @c AND status = 'ACTIVE'", cancellationToken, ("c", companyId)).ConfigureAwait(false)
            ?? throw new DomainException(OrderErrors.PriceListMissing, "There is no approved price list.");
        var seen = new HashSet<(Guid, string)>();
        var priced = new List<PricedLine>();
        foreach (var line in lines)
        {
            var uom = (line.Uom ?? string.Empty).Trim();
            if (!seen.Add((line.ItemId, uom)))
            {
                throw new DomainException(SalesErrors.DuplicateLine, "Each item and unit appears once in an order.");
            }

            if (line.Quantity <= 0m || decimal.Round(line.Quantity, 6) != line.Quantity)
            {
                throw new DomainException(SalesErrors.AmountInvalid, "Quantities are positive with at most 6 decimals.");
            }

            var price = quoted is not null && quoted.TryGetValue((line.ItemId, uom), out var quotedPrice)
                ? quotedPrice
                : await SalesSql.ScalarAsync<decimal?>(
                    connection, transaction, "SELECT unit_price FROM sal.price_list_line WHERE price_list_version_id = @l AND item_id = @i AND uom = @u", cancellationToken,
                    ("l", list), ("i", line.ItemId), ("u", uom)).ConfigureAwait(false)
                  ?? throw new DomainException(OrderErrors.PriceMissing, $"The price list in force has no price for item {line.ItemId} in {uom}.");
            var net = decimal.Round(line.Quantity * price, 2, MidpointRounding.AwayFromZero);
            if (net <= 0m)
            {
                throw new DomainException(SalesErrors.AmountInvalid, "The quantity is too small to be priced.");
            }

            priced.Add(new PricedLine(priced.Count + 1, line.ItemId, uom, line.Quantity, price, net));
        }

        return (list, priced, priced.Sum(l => l.Net));
    }

    /// <summary>E-QUO1-03-4: the quoted price of each (item, unit) of the quote the order came from (empty for an order without one).</summary>
    public static async Task<Dictionary<(Guid ItemId, string Uom), decimal>> QuotedPricesAsync(CommandContext context, Guid orderId, CancellationToken cancellationToken)
    {
        var rows = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.item_id, l.uom, l.unit_price
            FROM sal.sales_order o JOIN sal.quote q ON q.quote_id = o.quote_id JOIN sal.quote_line l ON l.quote_id = q.quote_id AND l.lines_version = q.lines_version
            WHERE o.sales_order_id = @o
            """,
            r => (Key: (r.GetGuid(0), r.GetString(1)), Price: r.GetDecimal(2)),
            cancellationToken,
            ("o", orderId)).ConfigureAwait(false);
        return rows.ToDictionary(r => r.Key, r => r.Price);
    }

    /// <summary>Creates a DRAFT order PV-… (numbered under a lock) with its lines, event and state history; <paramref name="quoteId"/> links a converted quote.</summary>
    public static async Task<string> InsertAsync(
        CommandContext context, Guid orderId, Guid partyId, Header header, Guid priceList, List<PricedLine> lines, decimal total, Guid? quoteId, string commandType,
        CancellationToken cancellationToken, (bool Pending, bool? CollectsItbis) exemption = default, Buyer? buyer = null)
    {
        // E-CF1-4: the order of the final consumer is a cash sale, and only it (a quote of the consumer converts into one).
        var cash = await SalesSql.ScalarAsync<string>(context, "SELECT party_kind FROM md.party WHERE company_id = @c AND party_id = @p", cancellationToken, ("c", context.CompanyId), ("p", partyId))
            .ConfigureAwait(false) == "CONSUMER";
        var creator = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        await SalesSql.LockAsync(context, "sales-order-no", cancellationToken).ConfigureAwait(false);
        var last = await SalesSql.ScalarAsync<int?>(
            context, "SELECT max(substring(order_no from 4)::int) FROM sal.sales_order WHERE company_id = @c", cancellationToken, ("c", context.CompanyId)).ConfigureAwait(false) ?? 0;
        var orderNo = "PV-" + (last + 1).ToString("D6", CultureInfo.InvariantCulture);
        var today = SalesSql.Today(context);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "SalesOrderCreated",
                1,
                Aggregate,
                orderId,
                1,
                JsonSerializer.Serialize(new
                {
                    salesOrderId = orderId,
                    orderNo,
                    partyId,
                    plantId = header.PlantId,
                    deliveryTermCode = header.Term,
                    priceListVersionId = priceList,
                    quoteId,
                    exemptionPending = exemption.Pending,
                    proformaCollectsItbis = exemption.CollectsItbis,
                    cashSale = cash,
                    buyer = buyer is null ? null : new { name = buyer.Name, phone = buyer.Phone, idKind = buyer.IdKind, id = buyer.Id },
                    totalNet = M(total),
                    lines = lines.Select(l => new { itemId = l.ItemId, uom = l.Uom, quantity = M(l.Quantity), unitPrice = M(l.UnitPrice), net = M(l.Net) }),
                }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO sal.sales_order (sales_order_id, company_id, order_no, party_id, plant_id, order_date, delivery_term_code, site_address, requested_date, customer_po_ref,
              price_list_version_id, status, total_net, lines_version, created_by, version, quote_id, exemption_pending, proforma_collects_itbis,
              cash_sale, buyer_name, buyer_phone, buyer_id_kind, buyer_id)
            VALUES (@id, @c, @no, @p, @plant, @date, @term, @site, @req, @po, @list, 'DRAFT', @total, 1, @by, 1, @quote, @pending, @collects,
              @cash, @bname, @bphone, @bkind, @bid)
            """,
            cancellationToken,
            ("id", orderId),
            ("c", context.CompanyId),
            ("no", orderNo),
            ("p", partyId),
            ("plant", header.PlantId),
            ("date", today),
            ("term", header.Term),
            ("site", header.Site),
            ("req", header.Requested),
            ("po", header.PoRef),
            ("list", priceList),
            ("total", total),
            ("by", creator),
            ("quote", quoteId),
            ("pending", exemption.Pending),
            ("collects", exemption.CollectsItbis),
            ("cash", cash),
            ("bname", buyer?.Name),
            ("bphone", buyer?.Phone),
            ("bkind", buyer?.IdKind),
            ("bid", buyer?.Id)).ConfigureAwait(false);
        await WriteLinesAsync(context, orderId, 1, lines, cancellationToken).ConfigureAwait(false);
        await context.AppendStateAsync(Aggregate, orderId, "DOCUMENT", null, "DRAFT", commandType, eventId, cancellationToken).ConfigureAwait(false);
        return orderNo;
    }

    public static async Task WriteLinesAsync(CommandContext context, Guid orderId, int linesVersion, IEnumerable<PricedLine> lines, CancellationToken cancellationToken)
    {
        foreach (var l in lines)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO sal.sales_order_line (line_id, company_id, sales_order_id, lines_version, line_no, item_id, uom, qty_ordered, unit_price, net_amount)
                VALUES (@id, @c, @o, @v, @no, @i, @u, @q, @p, @n)
                """,
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("o", orderId),
                ("v", linesVersion),
                ("no", l.LineNo),
                ("i", l.ItemId),
                ("u", l.Uom),
                ("q", l.Quantity),
                ("p", l.UnitPrice),
                ("n", l.Net)).ConfigureAwait(false);
        }
    }

    /// <summary>E-CF1-2: who buys on a cash sale; every member optional.</summary>
    public sealed record Buyer(string? Name, string? Phone, string? IdKind, string? Id);

    /// <remarks>E-CF1-01-3…5: <c>CashSale</c>, what must be paid and what receipts are assigned to it (zero and null on a credit order).</remarks>
    public sealed record Row(
        Guid PartyId, string Status, decimal Total, int LinesVersion, Guid CreatedBy, long Version, string OrderNo, bool CashSale = false, decimal? PaymentTotal = null, decimal Allocated = 0m,
        string? BuyerId = null);

    public static async Task<Row> LockAsync(CommandContext context, Guid orderId, long expectedVersion, CancellationToken cancellationToken)
    {
        var row = await LockCurrentAsync(context, orderId, cancellationToken).ConfigureAwait(false);
        return row.Version == expectedVersion
            ? row
            : throw new DomainException(SalesErrors.VersionConflict, $"The order changed (version {row.Version}, expected {expectedVersion}); reload and retry.");
    }

    /// <summary>
    /// Locks an order and returns it as it stands once locked, without a version check (deliveries move it; lock order: sales order →
    /// delivery → lines). VS3-11 (INV-S): reading the version first and then comparing it under the lock refused a second gate-out
    /// of the same order that had only waited for the first.
    /// </summary>
    public static async Task<Row> LockCurrentAsync(CommandContext context, Guid orderId, CancellationToken cancellationToken)
        => await Reading.SingleOrDefaultAsync(
               context.Connection,
               context.Transaction,
               """
               SELECT party_id, status, total_net, lines_version, created_by, version, order_no, cash_sale, payment_total::numeric(19,2), allocated_amount::numeric(19,2), buyer_id
               FROM sal.sales_order WHERE company_id = @c AND sales_order_id = @o FOR UPDATE
               """,
               r => new Row(
                   r.GetGuid(0), r.GetString(1), r.GetDecimal(2), r.GetInt32(3), r.GetGuid(4), r.GetInt64(5), r.GetString(6), r.GetBoolean(7), r.NullableDecimal(8), r.GetDecimal(9), r.NullableString(10)),
               cancellationToken,
               ("c", context.CompanyId),
               ("o", orderId)).ConfigureAwait(false)
           ?? throw new DomainException(SalesErrors.NotFound, "The sales order does not exist.");

    /// <summary>E-VS3-04-13: adds delivered quantities to the order's current lines and moves it to PARTIALLY_DELIVERED / DELIVERED.</summary>
    public static async Task AddDeliveredAsync(CommandContext context, Row order, Guid orderId, IReadOnlyDictionary<Guid, decimal> delivered, string commandType, CancellationToken cancellationToken)
    {
        foreach (var (line, quantity) in delivered.Where(d => d.Value > 0m))
        {
            await Sql.ExecuteAsync(
                context.Connection, context.Transaction, "UPDATE sal.sales_order_line SET qty_delivered = qty_delivered + @q WHERE line_id = @l", cancellationToken,
                ("q", quantity), ("l", line)).ConfigureAwait(false);
        }

        var complete = await SalesSql.ScalarAsync<bool?>(
            context,
            """
            SELECT bool_and(l.qty_delivered >= l.qty_ordered) FROM sal.sales_order o
            JOIN sal.sales_order_line l ON l.sales_order_id = o.sales_order_id AND l.lines_version = o.lines_version WHERE o.sales_order_id = @o
            """,
            cancellationToken,
            ("o", orderId)).ConfigureAwait(false) == true;
        var to = complete ? "DELIVERED" : "PARTIALLY_DELIVERED";
        if (to != order.Status && delivered.Values.Any(q => q > 0m))
        {
            await TransitionAsync(context, orderId, order, to, complete ? "SalesOrderDelivered" : "SalesOrderPartiallyDelivered", new { salesOrderId = orderId, orderNo = order.OrderNo, status = to }, commandType, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<string?> CustomerStatusAsync(CommandContext context, Guid partyId, CancellationToken cancellationToken)
        => await SalesSql.ScalarAsync<string>(
            context, "SELECT customer_status FROM md.party WHERE company_id = @c AND party_id = @p AND is_customer", cancellationToken, ("c", context.CompanyId), ("p", partyId)).ConfigureAwait(false);

    /// <summary>E-VS3-03-9: a customer's credit decisions are serialized, so auto-approvals never add up above the limit.</summary>
    public static Task LockCustomerCreditAsync(CommandContext context, Guid partyId, CancellationToken cancellationToken)
        => SalesSql.LockAsync(context, "credit:" + partyId.ToString(), cancellationToken);

    public static async Task<Guid> TransitionAsync(
        CommandContext context, Guid orderId, Row row, string to, string eventType, object payload, string commandType, CancellationToken cancellationToken, string? reason = null, string? cancelReason = null,
        string? closeReason = null)
    {
        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(eventType, 1, Aggregate, orderId, version, JsonSerializer.Serialize(payload), Publish: true), cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE sal.sales_order SET status = @s, cancel_reason = coalesce(@cr, cancel_reason), close_reason = coalesce(@clr, close_reason), version = @v WHERE sales_order_id = @o",
            cancellationToken,
            ("s", to),
            ("cr", cancelReason),
            ("clr", closeReason),
            ("v", version),
            ("o", orderId)).ConfigureAwait(false);
        await context.AppendStateAsync(Aggregate, orderId, "DOCUMENT", row.Status, to, commandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        return eventId;
    }

    public static string M(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}

[RequiresPermission("sales_order:create")]
public sealed class CreateSalesOrderHandler : ICommandHandler<CreateSalesOrder>
{
    public string CommandType => "Sales.CreateSalesOrder";

    public async Task<string> HandleAsync(CreateSalesOrder command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var header = Orders.ValidateHeader(command.DeliveryTermCode, command.SiteAddress, command.RequestedDate, command.CustomerPoRef, command.PlantId);
        var exemption = Proformas.Proformas.Validate(command.ExemptionPending, command.ProformaCollectsItbis);
        var customer = await Orders.CustomerStatusAsync(context, command.PartyId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(SalesErrors.NotCustomer, "The party is not a customer.");
        if (customer == "BLOCKED")
        {
            throw new DomainException(OrderErrors.CustomerNotActive, "The customer is blocked.");
        }

        if (await SalesSql.ScalarAsync<string>(context, "SELECT party_kind FROM md.party WHERE party_id = @p", cancellationToken, ("p", command.PartyId)).ConfigureAwait(false) == "CONSUMER")
        {
            throw new DomainException(OrderErrors.UseCashSale, "A sale to the final consumer is a cash sale: use CreateCashSale (E-CF1-4).");
        }

        var (list, lines, total) = await Orders.PriceAsync(context, header.PlantId, command.Lines, cancellationToken).ConfigureAwait(false);
        var orderNo = await Orders.InsertAsync(context, context.ResultRef, command.PartyId, header, list, lines, total, null, CommandType, cancellationToken, exemption).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { salesOrderId = context.ResultRef, orderNo, status = "DRAFT", totalNet = Orders.M(total), version = 1 });
    }
}

[RequiresPermission("sales_order:create")]
public sealed class UpdateSalesOrderDraftHandler : ICommandHandler<UpdateSalesOrderDraft>
{
    public string CommandType => "Sales.UpdateSalesOrderDraft";

    public async Task<string> HandleAsync(UpdateSalesOrderDraft command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var header = Orders.ValidateHeader(command.DeliveryTermCode, command.SiteAddress, command.RequestedDate, command.CustomerPoRef, command.PlantId);
        var exemption = Proformas.Proformas.Validate(command.ExemptionPending, command.ProformaCollectsItbis);
        var row = await Orders.LockAsync(context, command.SalesOrderId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "DRAFT")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The order is {row.Status}; only a DRAFT changes.");
        }

        if (row.CashSale)
        {
            throw new DomainException(OrderErrors.UseCashSale, "A cash sale is edited with UpdateCashSaleDraft (E-CF1-4).");
        }

        var quoted = await Orders.QuotedPricesAsync(context, command.SalesOrderId, cancellationToken).ConfigureAwait(false);
        var (list, lines, total) = await Orders.PriceAsync(context, header.PlantId, command.Lines, cancellationToken, quoted).ConfigureAwait(false);
        var version = row.Version + 1;
        var linesVersion = row.LinesVersion + 1;
        await context.AppendEventAsync(
            new EventDraft(
                "SalesOrderDraftUpdated",
                1,
                Orders.Aggregate,
                command.SalesOrderId,
                version,
                JsonSerializer.Serialize(new { salesOrderId = command.SalesOrderId, linesVersion, totalNet = Orders.M(total), lines = lines.Count }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            UPDATE sal.sales_order SET plant_id = @plant, delivery_term_code = @term, site_address = @site, requested_date = @req, customer_po_ref = @po,
              price_list_version_id = @list, total_net = @total, lines_version = @lv, exemption_pending = @pending, proforma_collects_itbis = @collects, version = @v
            WHERE sales_order_id = @o
            """,
            cancellationToken,
            ("plant", header.PlantId),
            ("term", header.Term),
            ("site", header.Site),
            ("req", header.Requested),
            ("po", header.PoRef),
            ("list", list),
            ("total", total),
            ("lv", linesVersion),
            ("pending", exemption.Pending),
            ("collects", exemption.CollectsItbis),
            ("v", version),
            ("o", command.SalesOrderId)).ConfigureAwait(false);
        await Orders.WriteLinesAsync(context, command.SalesOrderId, linesVersion, lines, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { salesOrderId = command.SalesOrderId, status = "DRAFT", totalNet = Orders.M(total), version });
    }
}

[RequiresPermission("sales_order:create")]
public sealed class SubmitForCreditHandler : ICommandHandler<SubmitForCredit>
{
    public string CommandType => "Sales.SubmitForCredit";

    private sealed record Terms(decimal Limit, bool Hold);

    public async Task<string> HandleAsync(SubmitForCredit command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await Orders.LockAsync(context, command.SalesOrderId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "DRAFT")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The order is {row.Status}.");
        }

        if (row.CashSale)
        {
            throw new DomainException(OrderErrors.UseCashSale, "A cash sale does not go through credit: it is sent to payment (E-CF1-4).");
        }

        if (await Orders.CustomerStatusAsync(context, row.PartyId, cancellationToken).ConfigureAwait(false) != "ACTIVE")
        {
            throw new DomainException(OrderErrors.CustomerNotActive, "Only an ACTIVE customer's orders go to credit (E-VS3-03-4).");
        }

        await Orders.LockCustomerCreditAsync(context, row.PartyId, cancellationToken).ConfigureAwait(false);
        var terms = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT credit_limit::numeric(19,2), credit_hold FROM sal.customer_terms_version WHERE company_id = @c AND party_id = @p AND status = 'ACTIVE'",
            r => new Terms(r.GetDecimal(0), r.GetBoolean(1)),
            cancellationToken,
            ("c", context.CompanyId),
            ("p", row.PartyId)).ConfigureAwait(false)
            ?? throw new DomainException(OrderErrors.TermsMissing, "The customer has no approved terms.");
        var today = SalesSql.Today(context);
        var overdueBlock = (await PolicyResolver.ResolveAsync(context, Orders.CreditPolicy, today, cancellationToken).ConfigureAwait(false)).Integer(Orders.OverdueDaysBlock);
        var exposure = await CreditExposure.ComputeAsync(context.Connection, context.Transaction, context.CompanyId, row.PartyId, command.SalesOrderId, today, cancellationToken).ConfigureAwait(false);
        var auto = !terms.Hold && exposure.Total + row.Total <= terms.Limit && exposure.OverdueDays <= overdueBlock;
        var checkId = context.Ids.NewId();
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO sal.credit_check (credit_check_id, company_id, sales_order_id, lines_version, checked_at, order_amount, exposure_ar, exposure_orders, exposure_uninvoiced,
              credit_limit, credit_hold, overdue_days, overdue_days_block, decision)
            VALUES (@id, @c, @o, @lv, @at, @amount, @ar, @orders, @uninvoiced, @limit, @hold, @overdue, @block, @decision)
            """,
            cancellationToken,
            ("id", checkId),
            ("c", context.CompanyId),
            ("o", command.SalesOrderId),
            ("lv", row.LinesVersion),
            ("at", context.Clock.UtcNow),
            ("amount", row.Total),
            ("ar", exposure.OpenAr),
            ("orders", exposure.UndeliveredOrders),
            ("uninvoiced", exposure.DeliveredUninvoiced),
            ("limit", terms.Limit),
            ("hold", terms.Hold),
            ("overdue", exposure.OverdueDays),
            ("block", overdueBlock),
            ("decision", auto ? "AUTO_APPROVED" : "NEEDS_APPROVAL")).ConfigureAwait(false);
        var to = auto ? "CONFIRMED" : "PENDING_CREDIT";
        var payload = new
        {
            salesOrderId = command.SalesOrderId,
            orderNo = row.OrderNo,
            creditCheckId = checkId,
            decision = auto ? "AUTO_APPROVED" : "NEEDS_APPROVAL",
            orderAmount = Orders.M(row.Total),
            exposure = Orders.M(exposure.Total),
            creditLimit = Orders.M(terms.Limit),
            creditHold = terms.Hold,
        };
        await Orders.TransitionAsync(context, command.SalesOrderId, row, to, auto ? "SalesOrderConfirmed" : "CreditBlocked", payload, CommandType, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { salesOrderId = command.SalesOrderId, status = to, decision = payload.decision, exposure = payload.exposure, creditLimit = payload.creditLimit, version = row.Version + 1 });
    }
}

internal static class CreditDecisions
{
    public static async Task<(Orders.Row Row, Guid CheckId, Guid Decider)> OpenAsync(CommandContext context, Guid orderId, long expectedVersion, CancellationToken cancellationToken)
    {
        var row = await Orders.LockAsync(context, orderId, expectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "PENDING_CREDIT")
        {
            throw new DomainException(SalesErrors.InvalidState, $"The order is {row.Status}.");
        }

        await Orders.LockCustomerCreditAsync(context, row.PartyId, cancellationToken).ConfigureAwait(false);
        var decider = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (decider == row.CreatedBy && !await ControlWaiver.WaivedAsync(context, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(SalesErrors.FourEyes, "Credit is decided by someone other than who created the order (SAL-02).");
        }

        var checkId = await SalesSql.ScalarAsync<Guid?>(
            context, "SELECT credit_check_id FROM sal.credit_check WHERE sales_order_id = @o AND decision = 'NEEDS_APPROVAL' AND outcome IS NULL", cancellationToken, ("o", orderId)).ConfigureAwait(false)
            ?? throw new DomainException(SalesErrors.InvalidState, "The order has no credit evaluation waiting for a decision.");
        return (row, checkId, decider);
    }

    public static Task DecideAsync(CommandContext context, Guid checkId, string outcome, Guid decider, string? reason, CancellationToken cancellationToken)
        => Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE sal.credit_check SET outcome = @o, decided_by = @by, decided_at = @at, reason = @r WHERE credit_check_id = @id",
            cancellationToken,
            ("o", outcome),
            ("by", decider),
            ("at", context.Clock.UtcNow),
            ("r", reason),
            ("id", checkId));
}

[RequiresPermission("credit:approve", StepUp = true)]
public sealed class ApproveCreditHandler : ICommandHandler<ApproveCredit>
{
    public string CommandType => "Sales.ApproveCredit";

    public async Task<string> HandleAsync(ApproveCredit command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var (row, checkId, decider) = await CreditDecisions.OpenAsync(context, command.SalesOrderId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        await CreditDecisions.DecideAsync(context, checkId, "APPROVED", decider, null, cancellationToken).ConfigureAwait(false);
        await Orders.TransitionAsync(context, command.SalesOrderId, row, "CONFIRMED", "CreditApproved", new { salesOrderId = command.SalesOrderId, orderNo = row.OrderNo, creditCheckId = checkId }, CommandType, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { salesOrderId = command.SalesOrderId, status = "CONFIRMED", version = row.Version + 1 });
    }
}

[RequiresPermission("credit:approve")]
public sealed class RejectCreditHandler : ICommandHandler<RejectCredit>
{
    public string CommandType => "Sales.RejectCredit";

    public async Task<string> HandleAsync(RejectCredit command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = (command.Reason ?? string.Empty).Trim();
        if (reason.Length == 0)
        {
            throw new DomainException(OrderErrors.ReasonRequired, "Rejecting credit needs a reason.");
        }

        var (row, checkId, decider) = await CreditDecisions.OpenAsync(context, command.SalesOrderId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        await CreditDecisions.DecideAsync(context, checkId, "REJECTED", decider, reason, cancellationToken).ConfigureAwait(false);
        await Orders.TransitionAsync(context, command.SalesOrderId, row, "DRAFT", "CreditRejected", new { salesOrderId = command.SalesOrderId, orderNo = row.OrderNo, creditCheckId = checkId, reason }, CommandType, cancellationToken, reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { salesOrderId = command.SalesOrderId, status = "DRAFT", version = row.Version + 1 });
    }
}

[RequiresPermission("sales_order:cancel")]
public sealed class CancelSalesOrderHandler : ICommandHandler<CancelSalesOrder>
{
    public string CommandType => "Sales.CancelSalesOrder";

    public async Task<string> HandleAsync(CancelSalesOrder command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var reason = (command.Reason ?? string.Empty).Trim();
        if (reason.Length == 0)
        {
            throw new DomainException(OrderErrors.ReasonRequired, "Cancelling an order needs a reason.");
        }

        var row = await Orders.LockAsync(context, command.SalesOrderId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status is not ("DRAFT" or "PENDING_CREDIT" or "PENDING_PAYMENT" or "CONFIRMED"))
        {
            throw new DomainException(SalesErrors.InvalidState, $"The order is {row.Status}; only DRAFT, PENDING_CREDIT, PENDING_PAYMENT or CONFIRMED orders without deliveries are cancelled.");
        }

        if (row.Allocated > 0m)
        {
            throw new DomainException(SalesErrors.InvalidState, $"{row.OrderNo} has receipts assigned: release them before cancelling the order (E-CF1-01-8).");
        }

        if (await SalesSql.ScalarAsync<bool?>(
                context, "SELECT EXISTS (SELECT 1 FROM sal.sales_order_line WHERE sales_order_id = @o AND qty_delivered > 0)", cancellationToken, ("o", command.SalesOrderId)).ConfigureAwait(false) == true)
        {
            throw new DomainException(SalesErrors.InvalidState, "The order has deliveries; it is closed short, not cancelled.");
        }

        await Orders.TransitionAsync(context, command.SalesOrderId, row, "CANCELLED", "SalesOrderCancelled", new { salesOrderId = command.SalesOrderId, orderNo = row.OrderNo, reason }, CommandType, cancellationToken, reason, reason).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { salesOrderId = command.SalesOrderId, status = "CANCELLED", version = row.Version + 1 });
    }
}
