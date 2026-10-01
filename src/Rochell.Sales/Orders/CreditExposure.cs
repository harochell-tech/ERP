using System.Data.Common;
using Rochell.Platform.Data;

namespace Rochell.Sales.Orders;

/// <summary>
/// E-VS3-14, E-VS3-03-1/2: exposure = open AR (with ITBIS) + confirmed orders not yet delivered + delivered not yet invoiced
/// (orders net of ITBIS). Open AR and its overdue days read <c>fin.ar_document</c> (VS3-05); the order parts read the current
/// lines of CONFIRMED, PARTIALLY_DELIVERED and DELIVERED orders. E-FIS1b-01-5: delivered-not-invoiced is net of what was
/// collected on its proformas, and overdue days also come from overdue proformas with a balance.
/// </summary>
public static class CreditExposure
{
    public sealed record Parts(decimal OpenAr, decimal UndeliveredOrders, decimal DeliveredUninvoiced, int OverdueDays)
    {
        public decimal Total => OpenAr + UndeliveredOrders + DeliveredUninvoiced;
    }

    public static async Task<Parts> ComputeAsync(
        DbConnection connection, DbTransaction transaction, Guid companyId, Guid partyId, Guid? excludeOrderId, DateOnly today, CancellationToken cancellationToken)
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
        var ar = await Reading.ListAsync(
            connection,
            transaction,
            """
            SELECT coalesce(sum(open_amount), 0)::numeric(19,2),
                   coalesce(max(CAST(@today AS date) - due_date) FILTER (WHERE open_amount > 0 AND due_date < CAST(@today AS date)), 0)
            FROM fin.ar_document WHERE company_id = @c AND party_id = @p
            """,
            r => (r.GetDecimal(0), r.GetInt32(1)),
            cancellationToken,
            ("c", companyId),
            ("p", partyId),
            ("today", today)).ConfigureAwait(false);
        // E-FIS1b-01-5: what receipts were allocated to open proformas (up to their net) no longer uses credit, and an overdue
        // proforma with a balance counts as overdue days like an overdue invoice.
        var proformas = await Reading.ListAsync(
            connection,
            transaction,
            """
            SELECT coalesce(sum(least(pf.allocated_amount, pf.net_total)) FILTER (WHERE o.status IN ('CONFIRMED', 'PARTIALLY_DELIVERED', 'DELIVERED')
                              AND (CAST(@x AS uuid) IS NULL OR o.sales_order_id <> CAST(@x AS uuid))), 0)::numeric(19,2),
                   coalesce(max(CAST(@today AS date) - pf.due_date) FILTER (WHERE pf.due_date < CAST(@today AS date)
                              AND pf.allocated_amount < CASE WHEN pf.collects_itbis THEN pf.total ELSE pf.net_total END), 0)
            FROM sal.proforma pf JOIN sal.sales_order o ON o.sales_order_id = pf.sales_order_id
            WHERE pf.company_id = @c AND pf.party_id = @p AND pf.status = 'OPEN'
            """,
            r => (r.GetDecimal(0), r.GetInt32(1)),
            cancellationToken,
            ("c", companyId),
            ("p", partyId),
            ("x", excludeOrderId),
            ("today", today)).ConfigureAwait(false);
        var (undelivered, uninvoiced) = parts[0];
        var (openAr, overdue) = ar[0];
        var (collected, proformaOverdue) = proformas[0];
        return new Parts(openAr, undelivered, Math.Max(uninvoiced - collected, 0m), Math.Max(overdue, proformaOverdue));
    }
}
