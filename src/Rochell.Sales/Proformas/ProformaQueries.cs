using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;
using Rochell.Platform.Time;

namespace Rochell.Sales.Proformas;

// E-FIS1b-9, E-FIS1b-01-13: the proformas of the company (one per delivery of an order whose exemption is in process), read with
// sales:read. Balance = what the proforma collects (its total, or its net when it collects without ITBIS) less what receipts were
// allocated to it; Deposit = what was allocated above the net (the ITBIS the customer advanced).

public sealed record ListProformas(Guid CompanyId, Guid SessionId, Guid? PartyId = null, string? Status = null, int Limit = 50, int Offset = 0) : IQuery;

/// <summary><c>Certification</c>: NONE, IN_PROCESS (listed in an authorization not yet ACTIVE) or CERTIFIED.</summary>
public sealed record ProformaSummary(
    Guid ProformaId, string ProformaNo, Guid PartyId, string CustomerName, string? CustomerRnc, Guid SalesOrderId, string OrderNo, Guid DeliveryId, string DeliveryNo,
    DateOnly ProformaDate, DateOnly DueDate, int DaysOverdue, bool CollectsItbis, decimal Net, decimal Itbis, decimal Total, decimal Allocated, decimal Deposit, decimal Balance,
    string Status, string Certification, Guid? InvoiceId, string? InvoiceNo, long Version);

public sealed record ProformaList(IReadOnlyList<ProformaSummary> Items, int Limit, int Offset);

internal static class ProformaReading
{
    public const string Select = """
        SELECT pf.proforma_id, pf.proforma_no, pf.party_id, p.legal_name, p.rnc, pf.sales_order_id, o.order_no, pf.delivery_id, d.delivery_no, pf.proforma_date, pf.due_date,
               pf.collects_itbis, pf.net_total::numeric(19,2), pf.itbis_total::numeric(19,2), pf.total::numeric(19,2), pf.allocated_amount::numeric(19,2),
               greatest(pf.allocated_amount - pf.net_total, 0)::numeric(19,2),
               (CASE WHEN pf.status = 'OPEN' THEN (CASE WHEN pf.collects_itbis THEN pf.total ELSE pf.net_total END) - pf.allocated_amount ELSE 0 END)::numeric(19,2),
               pf.status,
               CASE WHEN EXISTS (SELECT 1 FROM tax.fiscal_authorization_proforma x JOIN tax.fiscal_authorization a ON a.authorization_id = x.authorization_id
                                 WHERE x.proforma_id = pf.proforma_id AND a.status IN ('ACTIVE', 'EXHAUSTED')) THEN 'CERTIFIED'
                    WHEN EXISTS (SELECT 1 FROM tax.fiscal_authorization_proforma x JOIN tax.fiscal_authorization a ON a.authorization_id = x.authorization_id
                                 WHERE x.proforma_id = pf.proforma_id AND a.status IN ('DRAFT', 'PENDING_VERIFICATION', 'SUSPENDED')) THEN 'IN_PROCESS'
                    ELSE 'NONE' END,
               pf.invoice_id, i.invoice_no, pf.version
        FROM sal.proforma pf
        JOIN md.party p ON p.party_id = pf.party_id
        JOIN sal.sales_order o ON o.sales_order_id = pf.sales_order_id
        JOIN log.delivery d ON d.delivery_id = pf.delivery_id
        LEFT JOIN sal.invoice i ON i.invoice_id = pf.invoice_id
        """;

    public static ProformaSummary Map(System.Data.Common.DbDataReader r, DateOnly today)
    {
        var due = r.Date(10);
        var balance = r.GetDecimal(17);
        return new ProformaSummary(
            r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.NullableString(4), r.GetGuid(5), r.GetString(6), r.GetGuid(7), r.GetString(8), r.Date(9), due,
            balance > 0m && today > due ? today.DayNumber - due.DayNumber : 0, r.GetBoolean(11), r.GetDecimal(12), r.GetDecimal(13), r.GetDecimal(14), r.GetDecimal(15), r.GetDecimal(16),
            balance, r.GetString(18), r.GetString(19), r.NullableGuid(20), r.NullableString(21), r.GetInt64(22));
    }
}

[RequiresPermission("sales:read")]
public sealed class ListProformasHandler : IQueryHandler<ListProformas>
{
    public string QueryType => "Sales.ListProformas";

    public async Task<string> HandleAsync(ListProformas query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var today = BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            ProformaReading.Select + """

            WHERE pf.company_id = @c AND (CAST(@p AS uuid) IS NULL OR pf.party_id = CAST(@p AS uuid)) AND (CAST(@s AS text) IS NULL OR pf.status = CAST(@s AS text))
            ORDER BY pf.proforma_no DESC LIMIT @limit OFFSET @offset
            """,
            r => ProformaReading.Map(r, today),
            cancellationToken,
            ("c", context.CompanyId),
            ("p", query.PartyId),
            ("s", query.Status),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new ProformaList(items, query.Limit, query.Offset));
    }
}

public sealed record GetProforma(Guid CompanyId, Guid SessionId, Guid ProformaId) : IQuery;

public sealed record ProformaLineView(int LineNo, string ItemCode, string ItemName, string Uom, decimal Quantity, decimal UnitPrice, decimal Net, decimal Itbis, decimal Total);

/// <summary>E-FIS1b-01-13: what prints — issuer, customer, the delivery's lines with their ITBIS and the totals — and the document's history.</summary>
/// <summary>A live allocation of a receipt to the proforma.</summary>
public sealed record ProformaCollectionView(Guid AllocationId, Guid EventId, Guid ReceiptId, string ReceiptNo, string Method, decimal Amount, DateTime At);

public sealed record ProformaDetail(
    ProformaSummary Header, string IssuerRnc, string IssuerName, string? SiteAddress, string? VoidReason, IReadOnlyList<ProformaLineView> Lines, IReadOnlyList<StateChange> History,
    IReadOnlyList<ProformaCollectionView> Collections);

[RequiresPermission("sales:read")]
public sealed class GetProformaHandler : IQueryHandler<GetProforma>
{
    public string QueryType => "Sales.GetProforma";

    private sealed record Extra(string IssuerRnc, string IssuerName, string? Site, string? VoidReason);

    public async Task<string> HandleAsync(GetProforma query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var today = BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);
        var header = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, ProformaReading.Select + " WHERE pf.company_id = @c AND pf.proforma_id = @id", r => ProformaReading.Map(r, today), cancellationToken,
            ("c", context.CompanyId), ("id", query.ProformaId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The proforma does not exist.");
        var extra = (await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT c.rnc, c.legal_name, o.site_address, pf.void_reason
            FROM sal.proforma pf JOIN md.company c ON c.company_id = pf.company_id JOIN sal.sales_order o ON o.sales_order_id = pf.sales_order_id
            WHERE pf.proforma_id = @id
            """,
            r => new Extra(r.GetString(0), r.GetString(1), r.NullableString(2), r.NullableString(3)),
            cancellationToken,
            ("id", query.ProformaId)).ConfigureAwait(false))!;
        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.line_no, i.code, i.description, l.uom, l.quantity, l.unit_price, l.net_amount::numeric(19,2), l.itbis_amount::numeric(19,2),
                   (l.net_amount + l.itbis_amount)::numeric(19,2)
            FROM sal.proforma_line l JOIN md.item i ON i.item_id = l.item_id
            WHERE l.proforma_id = @id ORDER BY l.line_no
            """,
            r => new ProformaLineView(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8)),
            cancellationToken,
            ("id", query.ProformaId)).ConfigureAwait(false);
        var history = await StateHistory.ReadAsync(context, Proformas.Aggregate, query.ProformaId, cancellationToken).ConfigureAwait(false);
        var collections = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT x.allocation_id, x.event_id, r.receipt_id, r.receipt_no, r.method, x.amount::numeric(19,2), e.occurred_at
            FROM fin.proforma_allocation x
            JOIN fin.receipt r ON r.receipt_id = x.receipt_id
            JOIN core.domain_event e ON e.company_id = x.company_id AND e.event_id = x.event_id
            WHERE x.proforma_id = @id AND x.reverses_allocation_id IS NULL
              AND NOT EXISTS (SELECT 1 FROM fin.proforma_allocation u WHERE u.reverses_allocation_id = x.allocation_id)
            ORDER BY e.occurred_at, x.allocation_id
            """,
            r => new ProformaCollectionView(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetDecimal(5), r.GetFieldValue<DateTime>(6)),
            cancellationToken,
            ("id", query.ProformaId)).ConfigureAwait(false);
        return ApiJson.Serialize(new ProformaDetail(header, extra.IssuerRnc, extra.IssuerName, extra.Site, extra.VoidReason, lines, history, collections));
    }
}
