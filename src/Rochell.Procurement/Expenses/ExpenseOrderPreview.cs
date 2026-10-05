using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;
using Rochell.Procurement.SupplierInvoices;
using Rochell.Tax;

namespace Rochell.Procurement.Expenses;

/// <summary>
/// E-GAS-05-6: a draft expense order priced while it is typed — net per line and the taxes of each line's type in force on
/// <paramref name="OrderDate"/> — without writing anything. A tax type not in force leaves the taxes null with the reason.
/// </summary>
public sealed record PreviewExpensePurchaseOrder(Guid CompanyId, Guid SessionId, DateOnly OrderDate, IReadOnlyList<ExpenseOrderLineInput> Lines) : IQuery;

/// <summary>The HTTP body of the preview.</summary>
public sealed record ExpenseOrderPreviewRequest(DateOnly OrderDate, IReadOnlyList<ExpenseOrderLineInput> Lines);

public sealed record ExpenseOrderPreviewLine(int LineNo, decimal NetAmount, decimal? Taxes);

public sealed record ExpenseOrderPreview(IReadOnlyList<ExpenseOrderPreviewLine> Lines, decimal NetTotal, decimal? TaxTotal, decimal? Total, string? TaxesUnavailableCode, string? TaxesUnavailableReason);

[RequiresPermission("purchase_order:create")]
public sealed class PreviewExpensePurchaseOrderHandler : IQueryHandler<PreviewExpensePurchaseOrder>
{
    public string QueryType => "Procurement.PreviewExpensePurchaseOrder";

    public async Task<string> HandleAsync(PreviewExpensePurchaseOrder query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        if (query.Lines is not { Count: > 0 and <= 200 })
        {
            throw new DomainException(ProcurementErrors.LinesRequired, "An expense order has 1 to 200 lines.");
        }

        var valid = query.Lines.Select(l => ExpenseInvoices.ValidLine(new ExpenseLineInput(l.Description, l.ExpenseCategoryId, l.TaxTypeId, l.Quantity, l.UnitPrice))).ToList();
        var classes = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT expense_category_id, line_class FROM pur.expense_category WHERE company_id = @c AND expense_category_id = ANY(@ids)",
            r => (Id: r.GetGuid(0), Class: r.GetString(1)),
            cancellationToken,
            ("c", context.CompanyId),
            ("ids", query.Lines.Select(l => l.ExpenseCategoryId).Distinct().ToArray())).ConfigureAwait(false)).ToDictionary(c => c.Id, c => c.Class);
        if (query.Lines.Any(l => !classes.ContainsKey(l.ExpenseCategoryId)))
        {
            throw new DomainException(ExpenseErrors.CategoryNotFound, "Every line needs an expense category.");
        }

        var ids = query.Lines.Select(_ => Guid.NewGuid()).ToList();
        var cents = new decimal(0, 0, 0, false, 2); // amounts as the API writes them: 2 decimals
        if (query.Lines.All(l => l.TaxTypeId is null))
        {
            // E-USD1-03-3: a foreign supplier's lines are in USD without taxes.
            var usd = cents + valid.Sum(v => v.Net);
            return ApiJson.Serialize(new ExpenseOrderPreview([.. valid.Select((v, i) => new ExpenseOrderPreviewLine(i + 1, cents + v.Net, cents))], usd, cents, usd, null, null));
        }

        var estimate = await TaxEngine.EstimateItbisAsync(
            context.Connection,
            context.Transaction,
            context.CompanyId,
            query.OrderDate,
            false,
            [.. query.Lines.Select((l, i) => new TaxLineInput(
                ids[i], null, valid[i].Net, l.TaxTypeId, classes[l.ExpenseCategoryId] == ExpenseLineClasses.Service ? TaxLineScopes.ExpenseService : TaxLineScopes.ExpenseGoods))],
            cancellationToken).ConfigureAwait(false);
        var net = cents + valid.Sum(v => v.Net);
        return ApiJson.Serialize(new ExpenseOrderPreview(
            [.. valid.Select((v, i) => new ExpenseOrderPreviewLine(i + 1, cents + v.Net, estimate.ByLine?[ids[i]]))],
            net,
            estimate.Total,
            estimate.Total is { } taxes ? net + taxes : null,
            estimate.UnavailableCode,
            estimate.UnavailableReason));
    }
}

/// <summary>
/// E-GAS-07-6: an expense invoice priced while it is typed (the same computation as the order's preview, on the invoice's date),
/// for Cuentas por pagar (<c>supplier_invoice:register</c>).
/// </summary>
public sealed record PreviewExpenseInvoice(Guid CompanyId, Guid SessionId, DateOnly DocDate, IReadOnlyList<ExpenseOrderLineInput> Lines) : IQuery;

[RequiresPermission("supplier_invoice:register")]
public sealed class PreviewExpenseInvoiceHandler : IQueryHandler<PreviewExpenseInvoice>
{
    public string QueryType => "Procurement.PreviewExpenseInvoice";

    public Task<string> HandleAsync(PreviewExpenseInvoice query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return new PreviewExpensePurchaseOrderHandler().HandleAsync(
            new PreviewExpensePurchaseOrder(query.CompanyId, query.SessionId, query.DocDate, query.Lines), context, cancellationToken);
    }
}
