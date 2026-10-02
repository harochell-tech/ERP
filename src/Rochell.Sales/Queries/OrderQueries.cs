using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;
using Rochell.Sales.Orders;

namespace Rochell.Sales.Queries;

// E-VS3-03-10: sales orders and credit exposure, read with sales:read.

/// <summary>
/// <paramref name="From"/> / <paramref name="To"/> (E-VS3-09-5): the order date, both optional. <paramref name="CashSale"/>
/// (E-CF1-05-1): only cash sales to the final consumer, or only the others.
/// </summary>
public sealed record ListSalesOrders(
    Guid CompanyId, Guid SessionId, string? Status = null, Guid? PartyId = null, int Limit = 50, int Offset = 0, DateOnly? From = null, DateOnly? To = null, bool? CashSale = null) : IQuery;

/// <param name="QuoteId">E-QUO1-03-8: the quote the order came from (QUOTED_AS), if any.</param>
public sealed record SalesOrderSummary(
    Guid SalesOrderId, string OrderNo, DateOnly OrderDate, Guid PartyId, string CustomerName, string PlantCode, string DeliveryTermCode, decimal TotalNet, string Status,
    string? CreatedBy, long Version, Guid? QuoteId, string? QuoteNo, bool CashSale, string? BuyerName, decimal? PaymentTotal);

public sealed record SalesOrderList(IReadOnlyList<SalesOrderSummary> Items, int Limit, int Offset);

internal static class OrderReading
{
    public const string Select = """
        SELECT o.sales_order_id, o.order_no, o.order_date, o.party_id, p.legal_name, pl.code, o.delivery_term_code, o.total_net::numeric(19,2), o.status, coalesce(u.display_name, u.email), o.version,
               o.quote_id, q.quote_no, o.cash_sale, o.buyer_name, o.payment_total::numeric(19,2)
        FROM sal.sales_order o
        JOIN md.party p ON p.party_id = o.party_id
        JOIN md.plant pl ON pl.plant_id = o.plant_id
        JOIN iam.user u ON u.user_id = o.created_by
        LEFT JOIN sal.quote q ON q.quote_id = o.quote_id
        """;

    public static SalesOrderSummary Map(System.Data.Common.DbDataReader r)
        => new(r.GetGuid(0), r.GetString(1), r.Date(2), r.GetGuid(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetDecimal(7), r.GetString(8), r.NullableString(9), r.GetInt64(10),
            r.IsDBNull(11) ? null : r.GetGuid(11), r.NullableString(12), r.GetBoolean(13), r.NullableString(14), r.IsDBNull(15) ? null : r.GetDecimal(15));
}

[RequiresPermission("sales:read")]
public sealed class ListSalesOrdersHandler : IQueryHandler<ListSalesOrders>
{
    public string QueryType => "Sales.ListSalesOrders";

    public async Task<string> HandleAsync(ListSalesOrders query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            OrderReading.Select + """
             WHERE o.company_id = @c AND (CAST(@s AS text) IS NULL OR o.status = CAST(@s AS text)) AND (CAST(@p AS uuid) IS NULL OR o.party_id = CAST(@p AS uuid))
              AND (CAST(@from AS date) IS NULL OR o.order_date >= CAST(@from AS date)) AND (CAST(@to AS date) IS NULL OR o.order_date <= CAST(@to AS date))
              AND (CAST(@cash AS boolean) IS NULL OR o.cash_sale = CAST(@cash AS boolean))
            ORDER BY o.order_no DESC
            LIMIT @limit OFFSET @offset
            """,
            OrderReading.Map,
            cancellationToken,
            ("c", context.CompanyId),
            ("s", query.Status),
            ("p", query.PartyId),
            ("from", query.From),
            ("to", query.To),
            ("cash", query.CashSale),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new SalesOrderList(items, query.Limit, query.Offset));
    }
}

public sealed record GetSalesOrder(Guid CompanyId, Guid SessionId, Guid SalesOrderId) : IQuery;

/// <summary><paramref name="SalesOrderLineId"/> (VS3-09): what a delivery plan names.</summary>
public sealed record SalesOrderLineView(
    int LineNo, Guid ItemId, string ItemCode, string ItemDescription, string Uom, decimal QtyOrdered, decimal UnitPrice, decimal NetAmount, decimal QtyDelivered, decimal QtyInvoiced,
    Guid SalesOrderLineId);

public sealed record CreditCheckView(
    Guid CreditCheckId, DateTime CheckedAt, decimal OrderAmount, decimal ExposureAr, decimal ExposureOrders, decimal ExposureUninvoiced, decimal CreditLimit, bool CreditHold,
    int OverdueDays, int OverdueDaysBlock, string Decision, string? Outcome, string? DecidedBy, string? Reason);

/// <summary>
/// A receipt assigned to a cash sale (E-CF1-05-4). <see cref="Counts"/>: its money counts towards the sale — a RECORDED receipt and,
/// for a cheque, one whose deposit is matched with the bank statement (E-CF1-4).
/// </summary>
public sealed record CashSalePayment(
    Guid ReceiptId, string ReceiptNo, DateOnly ReceiptDate, string Method, decimal Amount, bool Counts, string ReceiptStatus, string BankStatus, Guid AllocationEventId);

/// <summary>E-CF1-05-3: a receipt of the final consumer with money neither applied nor assigned, which may be assigned to the sale.</summary>
public sealed record CashSaleUnassignedReceipt(Guid ReceiptId, string ReceiptNo, DateOnly ReceiptDate, string Method, decimal Available, long Version);

/// <summary>
/// E-CF1-05-2…4: what the «Venta de contado» screen shows, computed here (the screen does no arithmetic). <see cref="PaymentTotal"/>
/// and <see cref="Itbis"/> exist once the sale was sent to payment; <see cref="Invoiced"/> is what its invoices already took;
/// <see cref="Counted"/> the money that counts; <see cref="StillToPay"/> = payment total − assigned − invoiced; <see cref="Covered"/>
/// when the money that counts reaches the payment total.
/// </summary>
public sealed record CashSaleView(
    string? BuyerName, string? BuyerPhone, string? BuyerIdKind, string? BuyerId, decimal? Itbis, decimal? PaymentTotal, decimal Assigned, decimal Invoiced, decimal Counted,
    decimal? StillToPay, bool Covered, IReadOnlyList<CashSalePayment> Payments, IReadOnlyList<CashSaleUnassignedReceipt> Unassigned);

public sealed record SalesOrderDetail(
    SalesOrderSummary Header, Guid PlantId, string? SiteAddress, DateOnly? RequestedDate, string? CustomerPoRef, Guid PriceListVersionId, string? CancelReason,
    IReadOnlyList<SalesOrderLineView> Lines, IReadOnlyList<CreditCheckView> CreditChecks, IReadOnlyList<StateChange> History, bool ExemptionPending, bool? ProformaCollectsItbis,
    CashSaleView? CashSale);

[RequiresPermission("sales:read")]
public sealed class GetSalesOrderHandler : IQueryHandler<GetSalesOrder>
{
    public string QueryType => "Sales.GetSalesOrder";

    private sealed record Extra(Guid PlantId, string? Site, DateOnly? Requested, string? PoRef, Guid ListId, string? CancelReason, bool ExemptionPending, bool? CollectsItbis);

    public async Task<string> HandleAsync(GetSalesOrder query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var header = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, OrderReading.Select + " WHERE o.company_id = @c AND o.sales_order_id = @o", OrderReading.Map, cancellationToken,
            ("c", context.CompanyId), ("o", query.SalesOrderId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The sales order does not exist.");
        var extra = (await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT plant_id, site_address, requested_date, customer_po_ref, price_list_version_id, cancel_reason, exemption_pending, proforma_collects_itbis FROM sal.sales_order WHERE sales_order_id = @o",
            r => new Extra(r.GetGuid(0), r.NullableString(1), r.IsDBNull(2) ? null : r.Date(2), r.NullableString(3), r.GetGuid(4), r.NullableString(5), r.GetBoolean(6), r.IsDBNull(7) ? null : r.GetBoolean(7)),
            cancellationToken,
            ("o", query.SalesOrderId)).ConfigureAwait(false))!;
        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.line_no, l.item_id, i.code, i.description, l.uom, l.qty_ordered, l.unit_price, l.net_amount::numeric(19,2), l.qty_delivered, l.qty_invoiced, l.line_id
            FROM sal.sales_order o
            JOIN sal.sales_order_line l ON l.sales_order_id = o.sales_order_id AND l.lines_version = o.lines_version
            JOIN md.item i ON i.item_id = l.item_id
            WHERE o.sales_order_id = @o ORDER BY l.line_no
            """,
            r => new SalesOrderLineView(r.GetInt32(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9), r.GetGuid(10)),
            cancellationToken,
            ("o", query.SalesOrderId)).ConfigureAwait(false);
        var checks = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT k.credit_check_id, k.checked_at, k.order_amount::numeric(19,2), k.exposure_ar::numeric(19,2), k.exposure_orders::numeric(19,2), k.exposure_uninvoiced::numeric(19,2),
                   k.credit_limit::numeric(19,2), k.credit_hold, k.overdue_days, k.overdue_days_block, k.decision, k.outcome, coalesce(u.display_name, u.email), k.reason
            FROM sal.credit_check k LEFT JOIN iam.user u ON u.user_id = k.decided_by
            WHERE k.sales_order_id = @o ORDER BY k.checked_at
            """,
            r => new CreditCheckView(
                r.GetGuid(0), r.GetFieldValue<DateTime>(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6), r.GetBoolean(7), r.GetInt32(8), r.GetInt32(9),
                r.GetString(10), r.NullableString(11), r.NullableString(12), r.NullableString(13)),
            cancellationToken,
            ("o", query.SalesOrderId)).ConfigureAwait(false);
        var history = await StateHistory.ReadAsync(context, "SalesOrder", query.SalesOrderId, cancellationToken).ConfigureAwait(false);
        var cash = header.CashSale ? await CashSaleAsync(query.SalesOrderId, header, context, cancellationToken).ConfigureAwait(false) : null;
        return ApiJson.Serialize(new SalesOrderDetail(
            header, extra.PlantId, extra.Site, extra.Requested, extra.PoRef, extra.ListId, extra.CancelReason, lines, checks, history, extra.ExemptionPending, extra.CollectsItbis, cash));
    }

    private static async Task<CashSaleView> CashSaleAsync(Guid orderId, SalesOrderSummary header, QueryContext context, CancellationToken cancellationToken)
    {
        var payments = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT r.receipt_id, r.receipt_no, r.receipt_date, r.method, x.amount::numeric(19,2), r.status = 'RECORDED' AND (r.method <> 'CHEQUE' OR r.bank_status = 'MATCHED'), r.status,
                   r.bank_status, x.event_id
            FROM fin.order_allocation x JOIN fin.receipt r ON r.receipt_id = x.receipt_id
            WHERE x.sales_order_id = @o AND x.reverses_allocation_id IS NULL
              AND NOT EXISTS (SELECT 1 FROM fin.order_allocation u WHERE u.reverses_allocation_id = x.allocation_id)
            ORDER BY x.allocation_id
            """,
            r => new CashSalePayment(r.GetGuid(0), r.GetString(1), r.Date(2), r.GetString(3), r.GetDecimal(4), r.GetBoolean(5), r.GetString(6), r.GetString(7), r.GetGuid(8)),
            cancellationToken,
            ("o", orderId)).ConfigureAwait(false);
        var unassigned = header.Status is "PENDING_PAYMENT" or "CONFIRMED" or "PARTIALLY_DELIVERED"
            ? await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT r.receipt_id, r.receipt_no, r.receipt_date, r.method, (r.unapplied_amount - r.allocated_amount)::numeric(19,2), r.version
                FROM fin.receipt r
                WHERE r.company_id = @c AND r.party_id = @p AND r.status = 'RECORDED' AND r.unapplied_amount - r.allocated_amount > 0
                ORDER BY r.receipt_no
                """,
                r => new CashSaleUnassignedReceipt(r.GetGuid(0), r.GetString(1), r.Date(2), r.GetString(3), r.GetDecimal(4), r.GetInt64(5)),
                cancellationToken,
                ("c", context.CompanyId),
                ("p", header.PartyId)).ConfigureAwait(false)
            : [];
        return (await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT o.buyer_name, o.buyer_phone, o.buyer_id_kind, o.buyer_id, (o.payment_total - o.total_net)::numeric(19,2), o.payment_total::numeric(19,2), o.allocated_amount::numeric(19,2),
                   t.taken::numeric(19,2), (t.counted + t.taken)::numeric(19,2), (o.payment_total - o.allocated_amount - t.taken)::numeric(19,2),
                   o.payment_total IS NOT NULL AND t.counted + t.taken >= o.payment_total
            FROM sal.sales_order o
            CROSS JOIN LATERAL (
              SELECT coalesce((SELECT sum(a.amount) FROM fin.ar_application a JOIN sal.invoice i ON i.ar_doc_id = a.ar_doc_id
                               WHERE a.reverses_application_id IS NULL
                                 AND NOT EXISTS (SELECT 1 FROM fin.ar_application u WHERE u.reverses_application_id = a.application_id)
                                 AND EXISTS (SELECT 1 FROM sal.invoice_line il JOIN log.delivery_line dl ON dl.delivery_line_id = il.delivery_line_id
                                             JOIN log.delivery d ON d.delivery_id = dl.delivery_id
                                             WHERE il.invoice_id = i.invoice_id AND d.sales_order_id = o.sales_order_id)), 0) AS taken,
                     coalesce((SELECT sum(x.amount) FROM fin.order_allocation x JOIN fin.receipt r ON r.receipt_id = x.receipt_id
                               WHERE x.sales_order_id = o.sales_order_id AND x.reverses_allocation_id IS NULL
                                 AND NOT EXISTS (SELECT 1 FROM fin.order_allocation u WHERE u.reverses_allocation_id = x.allocation_id)
                                 AND r.status = 'RECORDED' AND (r.method <> 'CHEQUE' OR r.bank_status = 'MATCHED')), 0) AS counted) t
            WHERE o.sales_order_id = @o
            """,
            r => new CashSaleView(
                r.NullableString(0), r.NullableString(1), r.NullableString(2), r.NullableString(3), r.NullableDecimal(4), r.NullableDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8),
                r.NullableDecimal(9), r.GetBoolean(10), payments, unassigned),
            cancellationToken,
            ("o", orderId)).ConfigureAwait(false))!;
    }
}

public sealed record GetCustomerExposure(Guid CompanyId, Guid SessionId, Guid PartyId) : IQuery;

/// <summary>E-VS3-14: the customer's exposure against its terms; <see cref="Available"/> = limit − exposure (may be negative).</summary>
public sealed record CustomerExposure(
    Guid PartyId, decimal OpenAr, decimal UndeliveredOrders, decimal DeliveredUninvoiced, decimal Exposure, decimal? CreditLimit, bool? CreditHold, decimal? Available, int OverdueDays);

[RequiresPermission("sales:read")]
public sealed class GetCustomerExposureHandler : IQueryHandler<GetCustomerExposure>
{
    public string QueryType => "Sales.GetCustomerExposure";

    public async Task<string> HandleAsync(GetCustomerExposure query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var terms = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT p.is_customer, t.credit_limit::numeric(19,2), t.credit_hold
            FROM md.party p LEFT JOIN sal.customer_terms_version t ON t.party_id = p.party_id AND t.status = 'ACTIVE'
            WHERE p.company_id = @c AND p.party_id = @p
            """,
            r => (IsCustomer: r.GetBoolean(0), Limit: r.IsDBNull(1) ? (decimal?)null : r.GetDecimal(1), Hold: r.IsDBNull(2) ? (bool?)null : r.GetBoolean(2)),
            cancellationToken,
            ("c", context.CompanyId),
            ("p", query.PartyId)).ConfigureAwait(false);
        if (terms.Count == 0 || !terms[0].IsCustomer)
        {
            throw new DomainException(QueryErrors.NotFound, "The customer does not exist.");
        }

        var parts = await CreditExposure.ComputeAsync(context.Connection, context.Transaction, context.CompanyId, query.PartyId, null, Platform.Time.BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow), cancellationToken).ConfigureAwait(false);
        var (_, limit, hold) = terms[0];
        return ApiJson.Serialize(new CustomerExposure(query.PartyId, parts.OpenAr, parts.UndeliveredOrders, parts.DeliveredUninvoiced, parts.Total, limit, hold, limit - parts.Total, parts.OverdueDays));
    }
}

public sealed record GetCashSaleSetup(Guid CompanyId, Guid SessionId) : IQuery;

/// <summary>
/// E-CF1-05-6: what the cashier needs before selling — the amount from which the buyer's identification is mandatory, from the
/// CONSUMER_ID_THRESHOLD rule in force on <see cref="BusinessDate"/>; null when no rule is in force (no sale goes to payment).
/// </summary>
public sealed record CashSaleSetup(DateOnly BusinessDate, decimal? BuyerIdRequiredFrom);

[RequiresPermission("sales:read")]
public sealed class GetCashSaleSetupHandler : IQueryHandler<GetCashSaleSetup>
{
    public string QueryType => "Sales.GetCashSaleSetup";

    public async Task<string> HandleAsync(GetCashSaleSetup query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var today = Platform.Time.BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);
        return ApiJson.Serialize(new CashSaleSetup(
            today, await Tax.TaxEngine.ConsumerIdThresholdAsync(context.Connection, context.Transaction, context.CompanyId, today, cancellationToken).ConfigureAwait(false)));
    }
}
