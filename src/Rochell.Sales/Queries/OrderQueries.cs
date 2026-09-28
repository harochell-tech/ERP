using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;
using Rochell.Sales.Orders;

namespace Rochell.Sales.Queries;

// E-VS3-03-10: sales orders and credit exposure, read with sales:read.

public sealed record ListSalesOrders(Guid CompanyId, Guid SessionId, string? Status = null, Guid? PartyId = null, int Limit = 50, int Offset = 0) : IQuery;

public sealed record SalesOrderSummary(
    Guid SalesOrderId, string OrderNo, DateOnly OrderDate, Guid PartyId, string CustomerName, string PlantCode, string DeliveryTermCode, decimal TotalNet, string Status,
    string? CreatedBy, long Version);

public sealed record SalesOrderList(IReadOnlyList<SalesOrderSummary> Items, int Limit, int Offset);

internal static class OrderReading
{
    public const string Select = """
        SELECT o.sales_order_id, o.order_no, o.order_date, o.party_id, p.legal_name, pl.code, o.delivery_term_code, o.total_net::numeric(19,2), o.status, u.email, o.version
        FROM sal.sales_order o
        JOIN md.party p ON p.party_id = o.party_id
        JOIN md.plant pl ON pl.plant_id = o.plant_id
        JOIN iam.user u ON u.user_id = o.created_by
        """;

    public static SalesOrderSummary Map(System.Data.Common.DbDataReader r)
        => new(r.GetGuid(0), r.GetString(1), r.Date(2), r.GetGuid(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetDecimal(7), r.GetString(8), r.NullableString(9), r.GetInt64(10));
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
            ORDER BY o.order_no DESC
            LIMIT @limit OFFSET @offset
            """,
            OrderReading.Map,
            cancellationToken,
            ("c", context.CompanyId),
            ("s", query.Status),
            ("p", query.PartyId),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new SalesOrderList(items, query.Limit, query.Offset));
    }
}

public sealed record GetSalesOrder(Guid CompanyId, Guid SessionId, Guid SalesOrderId) : IQuery;

public sealed record SalesOrderLineView(int LineNo, Guid ItemId, string ItemCode, string ItemDescription, string Uom, decimal QtyOrdered, decimal UnitPrice, decimal NetAmount, decimal QtyDelivered, decimal QtyInvoiced);

public sealed record CreditCheckView(
    Guid CreditCheckId, DateTime CheckedAt, decimal OrderAmount, decimal ExposureAr, decimal ExposureOrders, decimal ExposureUninvoiced, decimal CreditLimit, bool CreditHold,
    int OverdueDays, int OverdueDaysBlock, string Decision, string? Outcome, string? DecidedBy, string? Reason);

public sealed record SalesOrderDetail(
    SalesOrderSummary Header, Guid PlantId, string? SiteAddress, DateOnly? RequestedDate, string? CustomerPoRef, Guid PriceListVersionId, string? CancelReason,
    IReadOnlyList<SalesOrderLineView> Lines, IReadOnlyList<CreditCheckView> CreditChecks, IReadOnlyList<StateChange> History);

[RequiresPermission("sales:read")]
public sealed class GetSalesOrderHandler : IQueryHandler<GetSalesOrder>
{
    public string QueryType => "Sales.GetSalesOrder";

    private sealed record Extra(Guid PlantId, string? Site, DateOnly? Requested, string? PoRef, Guid ListId, string? CancelReason);

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
            "SELECT plant_id, site_address, requested_date, customer_po_ref, price_list_version_id, cancel_reason FROM sal.sales_order WHERE sales_order_id = @o",
            r => new Extra(r.GetGuid(0), r.NullableString(1), r.IsDBNull(2) ? null : r.Date(2), r.NullableString(3), r.GetGuid(4), r.NullableString(5)),
            cancellationToken,
            ("o", query.SalesOrderId)).ConfigureAwait(false))!;
        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.line_no, l.item_id, i.code, i.description, l.uom, l.qty_ordered, l.unit_price, l.net_amount::numeric(19,2), l.qty_delivered, l.qty_invoiced
            FROM sal.sales_order o
            JOIN sal.sales_order_line l ON l.sales_order_id = o.sales_order_id AND l.lines_version = o.lines_version
            JOIN md.item i ON i.item_id = l.item_id
            WHERE o.sales_order_id = @o ORDER BY l.line_no
            """,
            r => new SalesOrderLineView(r.GetInt32(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9)),
            cancellationToken,
            ("o", query.SalesOrderId)).ConfigureAwait(false);
        var checks = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT k.credit_check_id, k.checked_at, k.order_amount::numeric(19,2), k.exposure_ar::numeric(19,2), k.exposure_orders::numeric(19,2), k.exposure_uninvoiced::numeric(19,2),
                   k.credit_limit::numeric(19,2), k.credit_hold, k.overdue_days, k.overdue_days_block, k.decision, k.outcome, u.email, k.reason
            FROM sal.credit_check k LEFT JOIN iam.user u ON u.user_id = k.decided_by
            WHERE k.sales_order_id = @o ORDER BY k.checked_at
            """,
            r => new CreditCheckView(
                r.GetGuid(0), r.GetFieldValue<DateTime>(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6), r.GetBoolean(7), r.GetInt32(8), r.GetInt32(9),
                r.GetString(10), r.NullableString(11), r.NullableString(12), r.NullableString(13)),
            cancellationToken,
            ("o", query.SalesOrderId)).ConfigureAwait(false);
        var history = await StateHistory.ReadAsync(context, "SalesOrder", query.SalesOrderId, cancellationToken).ConfigureAwait(false);
        return ApiJson.Serialize(new SalesOrderDetail(header, extra.PlantId, extra.Site, extra.Requested, extra.PoRef, extra.ListId, extra.CancelReason, lines, checks, history));
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
