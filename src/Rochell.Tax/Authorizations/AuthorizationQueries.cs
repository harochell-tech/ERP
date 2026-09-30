using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;
using Rochell.Platform.Time;

namespace Rochell.Tax.Authorizations;

// E-FIS1-02-7/8: fiscal authorizations and the order's proforma, read with sales:read.

public sealed record ListFiscalAuthorizations(Guid CompanyId, Guid SessionId, Guid? PartyId = null, string? Status = null) : IQuery;

/// <summary>E-UX4-2: <see cref="DaysToExpiry"/> = valid until − today's business date (0 on its last day, negative once past); null without a date.</summary>
public sealed record FiscalAuthorizationSummary(
    Guid AuthorizationId, Guid PartyId, string CustomerRnc, string CustomerName, string Regime, string CertificateNo, DateOnly IssuedOn, DateOnly? ValidUntil, string ProjectName,
    string Status, decimal NetAuthorized, decimal NetConsumed, long Version, int? DaysToExpiry);

public sealed record FiscalAuthorizationList(IReadOnlyList<FiscalAuthorizationSummary> Items);

internal static class AuthorizationSql
{
    public const string Summary = """
        SELECT a.authorization_id, a.party_id, p.rnc, p.legal_name, a.regime, a.certificate_no, a.issued_on, a.valid_until, a.project_name, a.status,
               coalesce((SELECT sum(l.net_authorized) FROM tax.fiscal_authorization_line l WHERE l.authorization_id = a.authorization_id), 0),
               coalesce((SELECT sum(l.net_consumed) FROM tax.fiscal_authorization_line l WHERE l.authorization_id = a.authorization_id), 0),
               a.version, a.valid_until - CAST(@today AS date)
        FROM tax.fiscal_authorization a JOIN md.party p ON p.party_id = a.party_id
        """;

    public static FiscalAuthorizationSummary Map(System.Data.Common.DbDataReader r)
        => new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.Date(6), r.IsDBNull(7) ? null : r.Date(7), r.GetString(8),
            r.GetString(9), r.GetDecimal(10), r.GetDecimal(11), r.GetInt64(12), r.IsDBNull(13) ? null : r.GetInt32(13));
}

[RequiresPermission("sales:read")]
public sealed class ListFiscalAuthorizationsHandler : IQueryHandler<ListFiscalAuthorizations>
{
    public string QueryType => "Tax.ListFiscalAuthorizations";

    public async Task<string> HandleAsync(ListFiscalAuthorizations query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            AuthorizationSql.Summary + """

            WHERE a.company_id = @c AND (CAST(@p AS uuid) IS NULL OR a.party_id = CAST(@p AS uuid)) AND (CAST(@s AS text) IS NULL OR a.status = CAST(@s AS text))
            ORDER BY a.issued_on DESC, a.certificate_no
            """,
            AuthorizationSql.Map,
            cancellationToken,
            ("c", context.CompanyId),
            ("p", query.PartyId),
            ("s", query.Status),
            ("today", BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow))).ConfigureAwait(false);
        return ApiJson.Serialize(new FiscalAuthorizationList(items));
    }
}

public sealed record GetFiscalAuthorization(Guid CompanyId, Guid SessionId, Guid AuthorizationId) : IQuery;

public sealed record FiscalAuthorizationLineView(
    int LineNo, Guid ItemId, string ItemCode, string ItemName, string Uom, decimal QtyAuthorized, decimal NetAuthorized, decimal QtyConsumed, decimal NetConsumed,
    decimal QtyAvailable, decimal NetAvailable);

public sealed record FiscalAuthorizationDocumentView(Guid DocumentId, string Kind, string EvidenceRef, string EvidenceSha256, DateTime AddedAt);

public sealed record FiscalAuthorizationHistoryView(string? From, string To, DateTime At, string? Reason);

/// <summary>E-FIS1-05-6: a consumption at an invoice's issue, or a release (void, credit note) of one.</summary>
public sealed record FiscalAuthorizationConsumptionView(Guid ConsumptionId, int LineNo, Guid InvoiceId, string InvoiceNo, decimal Quantity, decimal Net, bool Release, DateTime At);

public sealed record FiscalAuthorizationDetail(
    FiscalAuthorizationSummary Header, string ConfoturResolutionNo, DateOnly? ProjectTermEndsOn, Guid? SalesOrderId, Guid RegisteredBy, Guid? VerifiedBy,
    IReadOnlyList<FiscalAuthorizationLineView> Lines, IReadOnlyList<FiscalAuthorizationDocumentView> Documents, IReadOnlyList<FiscalAuthorizationHistoryView> History,
    IReadOnlyList<FiscalAuthorizationConsumptionView> Consumptions);

[RequiresPermission("sales:read")]
public sealed class GetFiscalAuthorizationHandler : IQueryHandler<GetFiscalAuthorization>
{
    public string QueryType => "Tax.GetFiscalAuthorization";

    private sealed record Extra(string ResolutionNo, DateOnly? TermEndsOn, Guid? SalesOrderId, Guid RegisteredBy, Guid? VerifiedBy);

    public async Task<string> HandleAsync(GetFiscalAuthorization query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var header = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, AuthorizationSql.Summary + "\nWHERE a.company_id = @c AND a.authorization_id = @id", AuthorizationSql.Map, cancellationToken,
            ("c", context.CompanyId), ("id", query.AuthorizationId), ("today", BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow))).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The fiscal authorization does not exist.");
        var extra = (await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT confotur_resolution_no, project_term_ends_on, sales_order_id, registered_by, verified_by FROM tax.fiscal_authorization WHERE authorization_id = @id",
            r => new Extra(r.GetString(0), r.IsDBNull(1) ? null : r.Date(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.GetGuid(3), r.IsDBNull(4) ? null : r.GetGuid(4)),
            cancellationToken,
            ("id", query.AuthorizationId)).ConfigureAwait(false))!;
        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.line_no, l.item_id, i.code, i.description, l.uom, l.qty_authorized, l.net_authorized, l.qty_consumed, l.net_consumed,
                   l.qty_authorized - l.qty_consumed, l.net_authorized - l.net_consumed
            FROM tax.fiscal_authorization_line l JOIN md.item i ON i.item_id = l.item_id
            WHERE l.authorization_id = @id ORDER BY l.line_no
            """,
            r => new FiscalAuthorizationLineView(r.GetInt32(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7),
                r.GetDecimal(8), r.GetDecimal(9), r.GetDecimal(10)),
            cancellationToken,
            ("id", query.AuthorizationId)).ConfigureAwait(false);
        var documents = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT document_id, kind, evidence_ref, evidence_sha256, added_at FROM tax.fiscal_authorization_document WHERE authorization_id = @id ORDER BY added_at, document_id",
            r => new FiscalAuthorizationDocumentView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTime>(4)),
            cancellationToken,
            ("id", query.AuthorizationId)).ConfigureAwait(false);
        var history = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT h.from_state, h.to_state, e.occurred_at, h.reason
            FROM core.state_history h JOIN core.domain_event e ON e.event_id = h.event_id
            WHERE h.company_id = @c AND h.aggregate_type = 'FiscalAuthorization' AND h.aggregate_id = @id ORDER BY e.aggregate_version
            """,
            r => new FiscalAuthorizationHistoryView(r.NullableString(0), r.GetString(1), r.GetFieldValue<DateTime>(2), r.NullableString(3)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", query.AuthorizationId)).ConfigureAwait(false);
        var consumptions = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT c.consumption_id, c.line_no, i.invoice_id, i.invoice_no, c.qty, c.net, c.reverses_consumption_id IS NOT NULL, e.occurred_at
            FROM tax.fiscal_authorization_consumption c
            JOIN sal.invoice_line il ON il.invoice_line_id = c.invoice_line_id
            JOIN sal.invoice i ON i.invoice_id = il.invoice_id
            JOIN core.domain_event e ON e.event_id = c.event_id
            WHERE c.company_id = @c AND c.authorization_id = @id ORDER BY e.occurred_at, c.consumption_id
            """,
            r => new FiscalAuthorizationConsumptionView(r.GetGuid(0), r.GetInt32(1), r.GetGuid(2), r.GetString(3), r.GetDecimal(4), r.GetDecimal(5), r.GetBoolean(6), r.GetFieldValue<DateTime>(7)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", query.AuthorizationId)).ConfigureAwait(false);
        return ApiJson.Serialize(new FiscalAuthorizationDetail(
            header, extra.ResolutionNo, extra.TermEndsOn, extra.SalesOrderId, extra.RegisteredBy, extra.VerifiedBy, lines, documents, history, consumptions));
    }
}

public sealed record GetSalesOrderProforma(Guid CompanyId, Guid SessionId, Guid SalesOrderId) : IQuery;

public sealed record ProformaLine(int LineNo, string ItemCode, string ItemName, string Uom, decimal Quantity, decimal UnitPrice, decimal Net, decimal Itbis, decimal Total);

/// <summary>E-FIS1-02-8 (D-04): what the customer takes to the DGII — issuer, customer, the order's lines with ITBIS at the rule in force today.</summary>
public sealed record SalesOrderProforma(
    string OrderNo, DateOnly OrderDate, DateOnly ProformaDate, string IssuerRnc, string IssuerName, string CustomerRnc, string CustomerName, string? SiteAddress,
    IReadOnlyList<ProformaLine> Lines, decimal NetTotal, decimal ItbisTotal, decimal Total);

[RequiresPermission("sales:read")]
public sealed class GetSalesOrderProformaHandler : IQueryHandler<GetSalesOrderProforma>
{
    public string QueryType => "Tax.GetSalesOrderProforma";

    private sealed record Head(string OrderNo, DateOnly OrderDate, string IssuerRnc, string IssuerName, string CustomerRnc, string CustomerName, string? SiteAddress, int LinesVersion);

    private sealed record Row(Guid LineId, int LineNo, string ItemCode, string ItemName, string Category, string Uom, decimal Quantity, decimal UnitPrice, decimal Net);

    public async Task<string> HandleAsync(GetSalesOrderProforma query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var head = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT o.order_no, o.order_date, c.rnc, c.legal_name, p.rnc, p.legal_name, o.site_address, o.lines_version
            FROM sal.sales_order o JOIN md.company c ON c.company_id = o.company_id JOIN md.party p ON p.party_id = o.party_id
            WHERE o.company_id = @c AND o.sales_order_id = @o
            """,
            r => new Head(r.GetString(0), r.Date(1), r.GetString(2), r.GetString(3), r.NullableString(4) ?? string.Empty, r.GetString(5), r.NullableString(6), r.GetInt32(7)),
            cancellationToken,
            ("c", context.CompanyId),
            ("o", query.SalesOrderId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The sales order does not exist.");
        var rows = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.line_id, l.line_no, i.code, i.description, i.item_category, l.uom, l.qty_ordered, l.unit_price, l.net_amount
            FROM sal.sales_order_line l JOIN md.item i ON i.item_id = l.item_id
            WHERE l.sales_order_id = @o AND l.lines_version = @v ORDER BY l.line_no
            """,
            r => new Row(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8)),
            cancellationToken,
            ("o", query.SalesOrderId),
            ("v", head.LinesVersion)).ConfigureAwait(false);
        var today = BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);
        var taxes = await TaxEngine.PreviewSalesItbisAsync(
            context.Connection, context.Transaction, context.CompanyId, today, [.. rows.Select(r => new TaxableLine(r.LineId, r.Category, r.Net))], cancellationToken).ConfigureAwait(false);
        var lines = rows.Select(r =>
        {
            var itbis = taxes.Where(t => t.LineId == r.LineId && t.Effect == TaxEffects.Output).Sum(t => t.Amount);
            return new ProformaLine(r.LineNo, r.ItemCode, r.ItemName, r.Uom, r.Quantity, r.UnitPrice, r.Net, itbis, r.Net + itbis);
        }).ToList();
        return ApiJson.Serialize(new SalesOrderProforma(
            head.OrderNo, head.OrderDate, today, head.IssuerRnc, head.IssuerName, head.CustomerRnc, head.CustomerName, head.SiteAddress, lines,
            lines.Sum(l => l.Net), lines.Sum(l => l.Itbis), lines.Sum(l => l.Total)));
    }
}
