using Rochell.Finance.Policies;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;
using Rochell.Platform.Time;
using Rochell.Sales.Orders;
using Rochell.Sales.Quotes;
using Rochell.Tax;

namespace Rochell.Sales.Queries;

// UX4-01: read-only answers the order, quote and receipt screens need before a command runs — nothing is written.

// ---------------------------------------------------------------------------------------------------------------------------
// E-UX4-3: order and quote previews

/// <summary>
/// E-UX4-3: a draft order's lines priced exactly as CreateSalesOrder prices them (the list in force, net of ITBIS, 2 decimals) and
/// the ITBIS the sales rules in force today would add. Needs <c>sales_order:create</c>, like the form it serves.
/// </summary>
public sealed record PreviewSalesOrder(Guid CompanyId, Guid SessionId, Guid PlantId, IReadOnlyList<SalesOrderLineInput> Lines) : IQuery;

/// <summary>The HTTP body of the order preview.</summary>
public sealed record SalesOrderPreviewRequest(Guid PlantId, IReadOnlyList<SalesOrderLineInput> Lines);

/// <summary>E-UX4-3: a draft quote's lines priced as CreateQuote prices them (a price below the list's is a special price). Needs <c>quote:manage</c>.</summary>
public sealed record PreviewQuote(Guid CompanyId, Guid SessionId, Guid PlantId, IReadOnlyList<QuoteLineInput> Lines) : IQuery;

/// <summary>The HTTP body of the quote preview.</summary>
public sealed record QuotePreviewRequest(Guid PlantId, IReadOnlyList<QuoteLineInput> Lines);

/// <summary><see cref="SpecialPrice"/>: the unit price is below the list price (the quote needs price approval, E-QUO1-3).</summary>
public sealed record SalesPreviewLine(
    int LineNo, Guid ItemId, string Uom, decimal Quantity, decimal ListPrice, decimal UnitPrice, bool SpecialPrice, decimal NetAmount, decimal? Itbis);

/// <summary>
/// <see cref="ItbisTotal"/> and <see cref="Total"/> are null when no sales ITBIS rule is in force today; <see cref="ItbisUnavailableCode"/>
/// (FISCAL_GATE_CLOSED) and <see cref="ItbisUnavailableReason"/> then say why. An exemption (e-CF 44, E-FIS1) is decided at invoicing.
/// </summary>
public sealed record SalesPreview(
    Guid PriceListVersionId, IReadOnlyList<SalesPreviewLine> Lines, decimal NetTotal, decimal? ItbisTotal, decimal? Total, string? ItbisUnavailableCode, string? ItbisUnavailableReason);

internal static class SalesPreviews
{
    public static async Task<string> BuildAsync(QueryContext context, Guid priceList, IReadOnlyList<SalesPreviewLine> lines, CancellationToken cancellationToken)
    {
        var ids = lines.Select(l => new Guid(l.LineNo, 0, 0, new byte[8])).ToList();
        var itbis = await TaxEngine.EstimateItbisAsync(
            context.Connection, context.Transaction, context.CompanyId, BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow), sale: true,
            [.. lines.Select((l, i) => new TaxLineInput(ids[i], l.ItemId, l.NetAmount))], cancellationToken).ConfigureAwait(false);
        var net = SalesSql.Zero + lines.Sum(l => l.NetAmount);
        return ApiJson.Serialize(new SalesPreview(
            priceList, [.. lines.Select((l, i) => l with { Itbis = itbis.ByLine?[ids[i]] })], net, itbis.Total, net + itbis.Total, itbis.UnavailableCode, itbis.UnavailableReason));
    }
}

[RequiresPermission("sales_order:create")]
public sealed class PreviewSalesOrderHandler : IQueryHandler<PreviewSalesOrder>
{
    public string QueryType => "Sales.PreviewSalesOrder";

    public async Task<string> HandleAsync(PreviewSalesOrder query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var (list, lines, _) = await Orders.Orders.PriceAsync(context.Connection, context.Transaction, context.CompanyId, query.PlantId, query.Lines, cancellationToken).ConfigureAwait(false);
        return await SalesPreviews.BuildAsync(
            context, list, [.. lines.Select(l => new SalesPreviewLine(l.LineNo, l.ItemId, l.Uom, l.Quantity, l.UnitPrice, l.UnitPrice, false, l.Net, null))], cancellationToken).ConfigureAwait(false);
    }
}

[RequiresPermission("quote:manage")]
public sealed class PreviewQuoteHandler : IQueryHandler<PreviewQuote>
{
    public string QueryType => "Sales.PreviewQuote";

    public async Task<string> HandleAsync(PreviewQuote query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var (list, lines, _) = await Quotes.Quotes.PriceAsync(context.Connection, context.Transaction, context.CompanyId, query.PlantId, query.Lines, cancellationToken).ConfigureAwait(false);
        return await SalesPreviews.BuildAsync(
            context, list, [.. lines.Select(l => new SalesPreviewLine(l.LineNo, l.ItemId, l.Uom, l.Quantity, l.ListPrice, l.UnitPrice, l.Special, l.Net, null))], cancellationToken).ConfigureAwait(false);
    }
}

// ---------------------------------------------------------------------------------------------------------------------------
// E-UX4-4: credit preview

/// <summary>
/// E-UX4-4: whether an order of <paramref name="Amount"/> (net, like the order total) would pass the credit check today, by
/// SubmitForCredit's own rule (E-VS3-03-4): no credit hold, exposure + amount ≤ limit, oldest overdue days ≤ the CREDIT policy's
/// overdue_days_block. Read-only: nothing is recorded or locked.
/// </summary>
public sealed record GetCreditPreview(Guid CompanyId, Guid SessionId, Guid PartyId, decimal Amount) : IQuery;

public static class CreditPreviewReasons
{
    public const string TermsMissing = "CUSTOMER_TERMS_REQUIRED";
    public const string CreditHold = "CREDIT_HOLD";
    public const string LimitExceeded = "CREDIT_LIMIT_EXCEEDED";
    public const string Overdue = "OVERDUE_DAYS_EXCEEDED";
}

/// <summary>
/// <see cref="Available"/> = limit − exposure; <see cref="AvailableAfter"/> = limit − exposure − amount (either may be negative; null
/// without approved terms). <see cref="Fits"/> is the automatic approval; otherwise <see cref="Reasons"/> lists why
/// (CUSTOMER_TERMS_REQUIRED, CREDIT_HOLD, CREDIT_LIMIT_EXCEEDED, OVERDUE_DAYS_EXCEEDED) and the order would go to Crédito.
/// </summary>
public sealed record CreditPreview(
    Guid PartyId, decimal Amount, decimal? CreditLimit, bool? CreditHold, decimal Exposure, decimal? Available, decimal? AvailableAfter, int OverdueDays, int OverdueDaysBlock,
    bool Fits, IReadOnlyList<string> Reasons);

[RequiresPermission("sales:read")]
public sealed class GetCreditPreviewHandler : IQueryHandler<GetCreditPreview>
{
    public string QueryType => "Sales.GetCreditPreview";

    public async Task<string> HandleAsync(GetCreditPreview query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        if (query.Amount < 0m || decimal.Round(query.Amount, 2) != query.Amount)
        {
            throw new DomainException(QueryErrors.InvalidParameter, "amount must not be negative and has at most 2 decimals.");
        }

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

        var today = BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);
        var policy = await PolicyResolver.TryResolveAsync(context.Connection, context.Transaction, context.CompanyId, Orders.Orders.CreditPolicy, today, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(Finance.FinanceErrors.PostingPrerequisiteMissing, $"No ACTIVE {Orders.Orders.CreditPolicy} accounting policy for {today:yyyy-MM-dd}.");
        var block = policy.Integer(Orders.Orders.OverdueDaysBlock);
        var exposure = await CreditExposure.ComputeAsync(context.Connection, context.Transaction, context.CompanyId, query.PartyId, null, today, cancellationToken).ConfigureAwait(false);
        var (_, limit, hold) = terms[0];
        var reasons = new List<string>();
        if (limit is null)
        {
            reasons.Add(CreditPreviewReasons.TermsMissing);
        }

        if (hold == true)
        {
            reasons.Add(CreditPreviewReasons.CreditHold);
        }

        if (limit is { } l && exposure.Total + query.Amount > l)
        {
            reasons.Add(CreditPreviewReasons.LimitExceeded);
        }

        if (exposure.OverdueDays > block)
        {
            reasons.Add(CreditPreviewReasons.Overdue);
        }

        var amount = SalesSql.Zero + query.Amount;
        return ApiJson.Serialize(new CreditPreview(
            query.PartyId, amount, limit, hold, exposure.Total, limit - exposure.Total, limit - exposure.Total - amount, exposure.OverdueDays, block, reasons.Count == 0, reasons));
    }
}

// ---------------------------------------------------------------------------------------------------------------------------
// E-UX4-10: receipt application suggestion

/// <summary>
/// E-UX4-10: how <paramref name="Amount"/> of a customer's receipt would be applied, oldest first — the customer's open invoices
/// (CONFIRMED or PARTIALLY_PAID, open amount above 0: the ones ApplyReceipt accepts) by due date, then issue date, then number, each
/// taking what is left up to its open amount. Read-only: the Cobros screen proposes it and ApplyReceipt decides.
/// </summary>
public sealed record SuggestReceiptApplication(Guid CompanyId, Guid SessionId, Guid PartyId, decimal Amount) : IQuery;

public sealed record SuggestedApplication(Guid InvoiceId, string InvoiceNo, string? Encf, DateOnly InvoiceDate, DateOnly DueDate, decimal OpenAmount, decimal Suggested);

/// <summary><see cref="Applied"/> = Σ suggested; <see cref="Unapplied"/> = amount − applied (stays in the customer's favour).</summary>
public sealed record ReceiptApplicationSuggestion(Guid PartyId, decimal Amount, IReadOnlyList<SuggestedApplication> Invoices, decimal TotalOpen, decimal Applied, decimal Unapplied);

[RequiresPermission("sales:read")]
public sealed class SuggestReceiptApplicationHandler : IQueryHandler<SuggestReceiptApplication>
{
    public string QueryType => "Sales.SuggestReceiptApplication";

    public async Task<string> HandleAsync(SuggestReceiptApplication query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        if (query.Amount <= 0m || decimal.Round(query.Amount, 2) != query.Amount)
        {
            throw new DomainException(QueryErrors.InvalidParameter, "amount must be greater than zero with at most 2 decimals.");
        }

        var invoices = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT i.invoice_id, i.invoice_no, i.encf, i.invoice_date, d.due_date, d.open_amount::numeric(19,2)
            FROM sal.invoice i JOIN fin.ar_document d ON d.ar_doc_id = i.ar_doc_id
            WHERE i.company_id = @c AND i.party_id = @p AND i.commercial_status IN ('CONFIRMED', 'PARTIALLY_PAID') AND d.open_amount > 0
            ORDER BY d.due_date, i.invoice_date, i.invoice_no
            """,
            r => new SuggestedApplication(r.GetGuid(0), r.GetString(1), r.NullableString(2), r.Date(3), r.Date(4), r.GetDecimal(5), SalesSql.Zero),
            cancellationToken,
            ("c", context.CompanyId),
            ("p", query.PartyId)).ConfigureAwait(false);
        var left = SalesSql.Zero + query.Amount;
        var suggested = new List<SuggestedApplication>();
        foreach (var invoice in invoices)
        {
            var take = Math.Min(left, invoice.OpenAmount);
            suggested.Add(invoice with { Suggested = SalesSql.Zero + take });
            left -= take;
        }

        var applied = SalesSql.Zero + suggested.Sum(s => s.Suggested);
        return ApiJson.Serialize(new ReceiptApplicationSuggestion(
            query.PartyId, SalesSql.Zero + query.Amount, suggested, SalesSql.Zero + invoices.Sum(i => i.OpenAmount), applied, SalesSql.Zero + query.Amount - applied));
    }
}
