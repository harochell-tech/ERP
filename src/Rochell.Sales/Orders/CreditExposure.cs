using System.Data.Common;
using Rochell.Platform.Data;

namespace Rochell.Sales.Orders;

/// <summary>
/// E-VS3-14, E-VS3-03-1/2: exposure = open AR (with ITBIS) + confirmed orders not yet delivered + delivered not yet invoiced
/// (orders net of ITBIS). Open AR and its overdue days arrive with the receivables of VS3-05/07 (no AR exists before);
/// the order parts read the current lines of CONFIRMED and PARTIALLY_DELIVERED orders.
/// </summary>
public static class CreditExposure
{
    public sealed record Parts(decimal OpenAr, decimal UndeliveredOrders, decimal DeliveredUninvoiced, int OverdueDays)
    {
        public decimal Total => OpenAr + UndeliveredOrders + DeliveredUninvoiced;
    }

    public static async Task<Parts> ComputeAsync(DbConnection connection, DbTransaction transaction, Guid companyId, Guid partyId, Guid? excludeOrderId, CancellationToken cancellationToken)
    {
        var parts = await Reading.ListAsync(
            connection,
            transaction,
            """
            SELECT coalesce(sum(round((l.qty_ordered - l.qty_delivered) * l.unit_price, 2)), 0)::numeric(19,2),
                   coalesce(sum(round((l.qty_delivered - l.qty_invoiced) * l.unit_price, 2)), 0)::numeric(19,2)
            FROM sal.sales_order o
            JOIN sal.sales_order_line l ON l.sales_order_id = o.sales_order_id AND l.lines_version = o.lines_version
            WHERE o.company_id = @c AND o.party_id = @p AND o.status IN ('CONFIRMED', 'PARTIALLY_DELIVERED', 'DELIVERED')
              AND (CAST(@x AS uuid) IS NULL OR o.sales_order_id <> CAST(@x AS uuid))
            """,
            r => (r.GetDecimal(0), r.GetDecimal(1)),
            cancellationToken,
            ("c", companyId),
            ("p", partyId),
            ("x", excludeOrderId)).ConfigureAwait(false);
        var (undelivered, uninvoiced) = parts[0];
        return new Parts(SalesSql.Zero, undelivered, uninvoiced, 0);
    }
}
