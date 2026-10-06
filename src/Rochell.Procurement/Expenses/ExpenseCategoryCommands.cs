using Rochell.Platform.Commands;

namespace Rochell.Procurement.Expenses;

/// <summary>
/// E-GAS-2, E-GAS-01-3/4: prepares a DRAFT expense category — what whoever registers an expense line chooses instead of an account.
/// <paramref name="AccountId"/> is an ACTIVE expense account that is not a control account; <paramref name="GoodsType606"/> the 606
/// type of goods and services ("01"…"11"); <paramref name="LineClass"/> SERVICE or GOODS (withholdings and the 606 amount columns).
/// </summary>
public sealed record PrepareExpenseCategory(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, string Code, string Name, Guid AccountId, string GoodsType606, string LineClass) : ICommand;

/// <summary>Corrects a DRAFT category: name, 606 type and class (code and account never change, E-GAS-01-3).</summary>
public sealed record UpdateExpenseCategoryDraft(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ExpenseCategoryId, long ExpectedVersion, string Name, string GoodsType606, string LineClass) : ICommand;

/// <summary>
/// E-GAS-03-1/3: approves one or several DRAFT categories (step-up); each by someone other than who prepared it. A category that
/// cannot be approved is reported and the others go on.
/// </summary>
public sealed record ApproveExpenseCategories(Guid CompanyId, Guid SessionId, string IdempotencyKey, IReadOnlyList<Guid> ExpenseCategoryIds) : ICommand;

/// <summary>Takes a category out of use (ACTIVE → INACTIVE) or discards a draft (DRAFT → INACTIVE); what was posted keeps it.</summary>
public sealed record DeactivateExpenseCategory(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ExpenseCategoryId, long ExpectedVersion) : ICommand;

/// <summary>INACTIVE → ACTIVE for a category that was approved before; its code must be free again.</summary>
public sealed record ReactivateExpenseCategory(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ExpenseCategoryId, long ExpectedVersion) : ICommand;

public static class ExpenseErrors
{
    public const string CategoryNotFound = "EXPENSE_CATEGORY_NOT_FOUND";
    public const string CategoryInvalid = "EXPENSE_CATEGORY_INVALID";
    public const string CategoryCodeUsed = "EXPENSE_CATEGORY_CODE_USED";

    /// <summary>E-GAS-2: an ACTIVE expense account that is not a control account.</summary>
    public const string AccountNotExpense = "EXPENSE_ACCOUNT_INVALID";

    /// <summary>E-USD1-03-1: a foreign supplier is bought from with expense orders and invoices only.</summary>
    public const string ForeignSupplierExpensesOnly = "FOREIGN_SUPPLIER_EXPENSES_ONLY";

    /// <summary>E-USD1-03-3: a line in USD carries no tax type; one in pesos always does.</summary>
    public const string TaxTypeCurrency = "EXPENSE_TAX_TYPE_CURRENCY";

    /// <summary>E-USD1-03-3: the foreign supplier's own invoice number, 1 to 40 characters.</summary>
    public const string ForeignNumberInvalid = "FOREIGN_INVOICE_NUMBER_INVALID";
}

public static class ExpenseLineClasses
{
    public const string Service = "SERVICE";
    public const string Goods = "GOODS";
}
