using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Procurement.Expenses;

/// <summary>E-GAS-03-2: the expense categories, by status when given (DRAFT, ACTIVE, INACTIVE), ordered by name.</summary>
public sealed record ListExpenseCategories(Guid CompanyId, Guid SessionId, string? Status = null) : IQuery;

public sealed record ExpenseCategoryView(
    Guid ExpenseCategoryId, string Code, string Name, Guid AccountId, string AccountCode, string AccountName, string GoodsType606, string LineClass, string Status,
    Guid PreparedBy, string? PreparedByName, Guid? ApprovedBy, string? ApprovedByName, long Version);

public sealed record ExpenseCategoryList(IReadOnlyList<ExpenseCategoryView> Items);

[RequiresPermission("master_data:read")]
public sealed class ListExpenseCategoriesHandler : IQueryHandler<ListExpenseCategories>
{
    public string QueryType => "Procurement.ListExpenseCategories";

    public async Task<string> HandleAsync(ListExpenseCategories query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        if (query.Status is not (null or "DRAFT" or "ACTIVE" or "INACTIVE"))
        {
            throw new DomainException(QueryErrors.InvalidParameter, "status is DRAFT, ACTIVE or INACTIVE.");
        }

        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT c.expense_category_id, c.code, c.name, c.account_id, a.code, a.name, c.goods_type_606, c.line_class, c.status,
                   c.prepared_by, coalesce(p.display_name, p.email), c.approved_by, coalesce(v.display_name, v.email), c.version
            FROM pur.expense_category c
            JOIN fin.account a ON a.account_id = c.account_id
            JOIN iam.user p ON p.user_id = c.prepared_by
            LEFT JOIN iam.user v ON v.user_id = c.approved_by
            WHERE c.company_id = @c AND (CAST(@s AS text) IS NULL OR c.status = CAST(@s AS text))
            ORDER BY c.name, c.code
            """,
            r => new ExpenseCategoryView(
                r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetGuid(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8),
                r.GetGuid(9), r.NullableString(10), r.IsDBNull(11) ? null : r.GetGuid(11), r.NullableString(12), r.GetInt64(13)),
            cancellationToken,
            ("c", context.CompanyId),
            ("s", query.Status)).ConfigureAwait(false);
        return ApiJson.Serialize(new ExpenseCategoryList(items));
    }
}
