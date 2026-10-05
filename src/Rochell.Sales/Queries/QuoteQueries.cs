using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;
using Rochell.Tax;

namespace Rochell.Sales.Queries;

// E-QUO1-02-10: quotes read with sales:read. "Expired" is derived: SENT with valid_until before today (E-QUO1-4), never a status.

/// <param name="ExpiredOnly">Only SENT quotes past their validity.</param>
public sealed record ListQuotes(Guid CompanyId, Guid SessionId, string? Status = null, Guid? PartyId = null, bool ExpiredOnly = false, int Limit = 50, int Offset = 0) : IQuery;

public sealed record QuoteSummary(
    Guid QuoteId, string QuoteNo, DateOnly QuoteDate, DateOnly ValidUntil, Guid PartyId, string CustomerName, string PlantCode, string DeliveryTermCode, decimal TotalNet,
    string Status, bool Expired, bool SpecialPrices, string? CreatedBy, long Version);

public sealed record QuoteList(IReadOnlyList<QuoteSummary> Items, int Limit, int Offset);

internal static class QuoteReading
{
    public const string Select = """
        SELECT q.quote_id, q.quote_no, q.quote_date, q.valid_until, q.party_id, p.legal_name, pl.code, q.delivery_term_code, q.total_net::numeric(19,2), q.status,
               q.status = 'SENT' AND q.valid_until < @today,
               EXISTS (SELECT 1 FROM sal.quote_line l WHERE l.quote_id = q.quote_id AND l.lines_version = q.lines_version AND l.unit_price < l.list_price),
               coalesce(u.display_name, u.email), q.version
        FROM sal.quote q
        JOIN md.party p ON p.party_id = q.party_id
        JOIN md.plant pl ON pl.plant_id = q.plant_id
        JOIN iam.user u ON u.user_id = q.created_by
        """;

    public static QuoteSummary Map(System.Data.Common.DbDataReader r)
        => new(r.GetGuid(0), r.GetString(1), r.Date(2), r.Date(3), r.GetGuid(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetDecimal(8), r.GetString(9), r.GetBoolean(10),
            r.GetBoolean(11), r.NullableString(12), r.GetInt64(13));

    public static DateOnly Today(QueryContext context) => Platform.Time.BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);
}

[RequiresPermission("sales:read")]
public sealed class ListQuotesHandler : IQueryHandler<ListQuotes>
{
    public string QueryType => "Sales.ListQuotes";

    public async Task<string> HandleAsync(ListQuotes query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            QuoteReading.Select + """
             WHERE q.company_id = @c AND (CAST(@s AS text) IS NULL OR q.status = CAST(@s AS text)) AND (CAST(@p AS uuid) IS NULL OR q.party_id = CAST(@p AS uuid))
              AND (NOT @expired OR (q.status = 'SENT' AND q.valid_until < @today))
            ORDER BY q.quote_no DESC
            LIMIT @limit OFFSET @offset
            """,
            QuoteReading.Map,
            cancellationToken,
            ("c", context.CompanyId),
            ("today", QuoteReading.Today(context)),
            ("s", query.Status),
            ("p", query.PartyId),
            ("expired", query.ExpiredOnly),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new QuoteList(items, query.Limit, query.Offset));
    }
}

public sealed record GetQuote(Guid CompanyId, Guid SessionId, Guid QuoteId) : IQuery;

/// <summary>PRS-02 (E-PRS-02-6): <paramref name="PriceListCode"/> names the list the list price came from (the customer's or GENERAL).</summary>
public sealed record QuoteLineView(
    int LineNo, Guid ItemId, string ItemCode, string ItemDescription, string Uom, decimal Quantity, decimal ListPrice, decimal UnitPrice, decimal NetAmount, bool Special,
    string? PriceListCode = null, string? PriceListName = null, decimal? FreightUnitPrice = null, decimal? FreightAmount = null);

/// <summary>A quote copied from this one, or the one it was copied from.</summary>
public sealed record QuoteLink(Guid QuoteId, string QuoteNo, string Status);

/// <param name="PriceApprovalCurrent">The approval covers the current lines (E-QUO1-01-2).</param>
public sealed record QuoteDetail(
    QuoteSummary Header, Guid PlantId, string? SiteAddress, string? CustomerRef, string? Notes, Guid PriceListVersionId, string? PriceApprovedBy, DateTime? PriceApprovedAt,
    bool PriceApprovalCurrent, Guid? SalesOrderId, string? OrderNo, string? ClosingReason, QuoteLink? CopiedFrom, IReadOnlyList<QuoteLink> Copies, IReadOnlyList<QuoteLineView> Lines,
    IReadOnlyList<StateChange> History, Guid? DeliveryZoneId = null, string? DeliveryZoneName = null);

[RequiresPermission("sales:read")]
public sealed class GetQuoteHandler : IQueryHandler<GetQuote>
{
    public string QueryType => "Sales.GetQuote";

    private sealed record Extra(
        Guid PlantId, string? Site, string? CustomerRef, string? Notes, Guid ListId, string? ApprovedBy, DateTime? ApprovedAt, bool ApprovalCurrent, Guid? OrderId, string? OrderNo,
        string? ClosingReason, Guid? CopiedFromId);

    public async Task<string> HandleAsync(GetQuote query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var header = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, QuoteReading.Select + " WHERE q.company_id = @c AND q.quote_id = @q", QuoteReading.Map, cancellationToken,
            ("c", context.CompanyId), ("q", query.QuoteId), ("today", QuoteReading.Today(context))).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The quote does not exist.");
        var extra = (await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT q.plant_id, q.site_address, q.customer_ref, q.notes, q.price_list_version_id, coalesce(u.display_name, u.email), q.price_approved_at, q.approved_lines_version IS NOT DISTINCT FROM q.lines_version,
                   q.sales_order_id, o.order_no, q.closing_reason, q.copied_from_quote_id
            FROM sal.quote q LEFT JOIN iam.user u ON u.user_id = q.price_approved_by LEFT JOIN sal.sales_order o ON o.sales_order_id = q.sales_order_id
            WHERE q.quote_id = @q
            """,
            r => new Extra(
                r.GetGuid(0), r.NullableString(1), r.NullableString(2), r.NullableString(3), r.GetGuid(4), r.NullableString(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateTime>(6),
                !r.IsDBNull(6) && r.GetBoolean(7), r.IsDBNull(8) ? null : r.GetGuid(8), r.NullableString(9), r.NullableString(10), r.IsDBNull(11) ? null : r.GetGuid(11)),
            cancellationToken,
            ("q", query.QuoteId)).ConfigureAwait(false))!;
        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.line_no, l.item_id, i.code, i.description, l.uom, l.quantity, l.list_price, l.unit_price, l.net_amount::numeric(19,2), l.unit_price < l.list_price,
                   pl.code, pl.name, l.freight_unit_price, l.freight_amount::numeric(19,2)
            FROM sal.quote q
            JOIN sal.quote_line l ON l.quote_id = q.quote_id AND l.lines_version = q.lines_version
            JOIN md.item i ON i.item_id = l.item_id
            LEFT JOIN sal.price_list_version pv ON pv.price_list_version_id = l.price_list_version_id
            LEFT JOIN sal.price_list pl ON pl.price_list_id = pv.price_list_id
            WHERE q.quote_id = @q ORDER BY l.line_no
            """,
            r => new QuoteLineView(r.GetInt32(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8), r.GetBoolean(9),
                r.NullableString(10), r.NullableString(11), r.NullableDecimal(12), r.NullableDecimal(13)),
            cancellationToken,
            ("q", query.QuoteId)).ConfigureAwait(false);
        var links = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT quote_id, quote_no, status, quote_id IS NOT DISTINCT FROM CAST(@from AS uuid) FROM sal.quote WHERE company_id = @c AND (copied_from_quote_id = @q OR quote_id = CAST(@from AS uuid)) ORDER BY quote_no",
            r => (Link: new QuoteLink(r.GetGuid(0), r.GetString(1), r.GetString(2)), IsSource: r.GetBoolean(3)),
            cancellationToken,
            ("c", context.CompanyId),
            ("q", query.QuoteId),
            ("from", extra.CopiedFromId)).ConfigureAwait(false);
        var history = await StateHistory.ReadAsync(context, "Quote", query.QuoteId, cancellationToken).ConfigureAwait(false);
        return ApiJson.Serialize(new QuoteDetail(
            header, extra.PlantId, extra.Site, extra.CustomerRef, extra.Notes, extra.ListId, extra.ApprovedBy, extra.ApprovedAt, extra.ApprovalCurrent, extra.OrderId, extra.OrderNo,
            extra.ClosingReason, links.Where(l => l.IsSource).Select(l => l.Link).SingleOrDefault(), [.. links.Where(l => !l.IsSource).Select(l => l.Link)], lines, history,
            await SalesSql.ScalarAsync<Guid?>(context.Connection, context.Transaction, "SELECT delivery_zone_id FROM sal.quote WHERE quote_id = @q", cancellationToken, ("q", query.QuoteId))
                .ConfigureAwait(false),
            await SalesSql.ScalarAsync<string>(
                context.Connection, context.Transaction, "SELECT z.name FROM sal.quote q JOIN sal.delivery_zone z ON z.zone_id = q.delivery_zone_id WHERE q.quote_id = @q", cancellationToken,
                ("q", query.QuoteId)).ConfigureAwait(false)));
    }
}

public sealed record GetQuotePrint(Guid CompanyId, Guid SessionId, Guid QuoteId) : IQuery;

public sealed record QuotePrintLine(int LineNo, string ItemCode, string ItemName, string Uom, decimal Quantity, decimal UnitPrice, decimal Net, decimal Itbis, decimal Total);

/// <summary>E-QUO1-12: what the customer receives — issuer, customer, lines with informative ITBIS at the quote date's rule (E-QUO1-5), validity and notes.</summary>
public sealed record QuotePrint(
    string QuoteNo, DateOnly QuoteDate, DateOnly ValidUntil, string Status, bool Expired, string IssuerRnc, string IssuerName, string CustomerRnc, string CustomerName,
    string DeliveryTermCode, string? SiteAddress, string? CustomerRef, string? Notes, IReadOnlyList<QuotePrintLine> Lines, decimal NetTotal, decimal ItbisTotal, decimal Total);

[RequiresPermission("sales:read")]
public sealed class GetQuotePrintHandler : IQueryHandler<GetQuotePrint>
{
    public string QueryType => "Sales.GetQuotePrint";

    private sealed record Head(
        string QuoteNo, DateOnly QuoteDate, DateOnly ValidUntil, string Status, string IssuerRnc, string IssuerName, string CustomerRnc, string CustomerName, string Term, string? Site,
        string? CustomerRef, string? Notes, int LinesVersion);

    private sealed record Row(Guid LineId, int LineNo, string ItemCode, string ItemName, string Category, string Uom, decimal Quantity, decimal UnitPrice, decimal Net);

    public async Task<string> HandleAsync(GetQuotePrint query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var head = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT q.quote_no, q.quote_date, q.valid_until, q.status, c.rnc, c.legal_name, p.rnc, p.legal_name, q.delivery_term_code, q.site_address, q.customer_ref, q.notes,
                   q.lines_version
            FROM sal.quote q JOIN md.company c ON c.company_id = q.company_id JOIN md.party p ON p.party_id = q.party_id
            WHERE q.company_id = @c AND q.quote_id = @q
            """,
            r => new Head(
                r.GetString(0), r.Date(1), r.Date(2), r.GetString(3), r.GetString(4), r.GetString(5), r.NullableString(6) ?? string.Empty, r.GetString(7), r.GetString(8),
                r.NullableString(9), r.NullableString(10), r.NullableString(11), r.GetInt32(12)),
            cancellationToken,
            ("c", context.CompanyId),
            ("q", query.QuoteId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The quote does not exist.");
        var rows = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.line_id, l.line_no, i.code, i.description, i.item_category, l.uom, l.quantity, l.unit_price, l.net_amount::numeric(19,2)
            FROM sal.quote_line l JOIN md.item i ON i.item_id = l.item_id
            WHERE l.quote_id = @q AND l.lines_version = @v ORDER BY l.line_no
            """,
            r => new Row(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8)),
            cancellationToken,
            ("q", query.QuoteId),
            ("v", head.LinesVersion)).ConfigureAwait(false);
        var taxes = await TaxEngine.PreviewSalesItbisAsync(
            context.Connection, context.Transaction, context.CompanyId, head.QuoteDate, [.. rows.Select(r => new TaxableLine(r.LineId, r.Category, r.Net))], cancellationToken).ConfigureAwait(false);
        var lines = rows.Select(r =>
        {
            var itbis = taxes.Where(t => t.LineId == r.LineId && t.Effect == TaxEffects.Output).Sum(t => t.Amount);
            return new QuotePrintLine(r.LineNo, r.ItemCode, r.ItemName, r.Uom, r.Quantity, r.UnitPrice, r.Net, itbis, r.Net + itbis);
        }).ToList();
        return ApiJson.Serialize(new QuotePrint(
            head.QuoteNo, head.QuoteDate, head.ValidUntil, head.Status, head.Status == "SENT" && head.ValidUntil < QuoteReading.Today(context), head.IssuerRnc, head.IssuerName,
            head.CustomerRnc, head.CustomerName, head.Term, head.Site, head.CustomerRef, head.Notes, lines, lines.Sum(l => l.Net), lines.Sum(l => l.Itbis), lines.Sum(l => l.Total)));
    }
}
