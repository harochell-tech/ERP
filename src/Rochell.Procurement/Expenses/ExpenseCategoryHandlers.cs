using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Procurement.PurchaseOrders;

namespace Rochell.Procurement.Expenses;

/// <summary>Shared rules of the expense category commands (E-GAS-2, E-GAS-01-3/4, E-GAS-03-1…3).</summary>
internal static partial class ExpenseCategories
{
    public const string Aggregate = "ExpenseCategory";

    public sealed record Row(Guid Id, string Code, string Name, string Status, Guid PreparedBy, Guid? ApprovedBy, long Version);

    public static (string Name, string GoodsType, string LineClass) Validate(string? name, string? goodsType, string? lineClass)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > 100)
        {
            throw new DomainException(ExpenseErrors.CategoryInvalid, "The name has 1 to 100 characters.");
        }

        if (goodsType is null || !GoodsType().IsMatch(goodsType))
        {
            throw new DomainException(ExpenseErrors.CategoryInvalid, "The 606 type of goods and services is \"01\"…\"11\".");
        }

        return lineClass is ExpenseLineClasses.Service or ExpenseLineClasses.Goods
            ? (trimmed, goodsType, lineClass)
            : throw new DomainException(ExpenseErrors.CategoryInvalid, "The class is SERVICE or GOODS.");
    }

    public static async Task<Row> LockAsync(CommandContext context, Guid id, long? expectedVersion, CancellationToken cancellationToken)
    {
        var row = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT expense_category_id, code, name, status, prepared_by, approved_by, version FROM pur.expense_category WHERE company_id = @c AND expense_category_id = @id FOR UPDATE",
            r => new Row(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetGuid(4), r.IsDBNull(5) ? null : r.GetGuid(5), r.GetInt64(6)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", id)).ConfigureAwait(false)
            ?? throw new DomainException(ExpenseErrors.CategoryNotFound, "The expense category does not exist.");
        return expectedVersion is null || expectedVersion == row.Version
            ? row
            : throw new DomainException(ProcurementErrors.VersionConflict, $"The category changed (version {row.Version}, expected {expectedVersion}); reload and retry.");
    }

    /// <summary>Moves a locked category to <paramref name="to"/> with its event and history row; returns the new version.</summary>
    public static async Task<long> TransitionAsync(
        CommandContext context, Row row, string to, string eventType, Guid? approvedBy, string commandType, CancellationToken cancellationToken)
    {
        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(eventType, 1, Aggregate, row.Id, version, JsonSerializer.Serialize(new { expenseCategoryId = row.Id, code = row.Code, status = to }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE pur.expense_category SET status = @s, approved_by = coalesce(approved_by, @by), version = @v WHERE expense_category_id = @id",
                cancellationToken,
                ("s", to),
                ("by", (object?)approvedBy ?? DBNull.Value),
                ("v", version),
                ("id", row.Id)).ConfigureAwait(false);
        }
        catch (DbException ex) when (ex.SqlState == SqlStates.UniqueViolation)
        {
            throw new DomainException(ExpenseErrors.CategoryCodeUsed, $"Another category in use has the code {row.Code}.");
        }

        await context.AppendStateAsync(Aggregate, row.Id, "DOCUMENT", row.Status, to, commandType, eventId, cancellationToken).ConfigureAwait(false);
        return version;
    }

    [GeneratedRegex("^(0[1-9]|1[01])$")]
    private static partial Regex GoodsType();

    [GeneratedRegex("^[A-Z][A-Z0-9_]{1,39}$")]
    public static partial Regex Code();
}

[RequiresPermission("expense_category:prepare")]
public sealed class PrepareExpenseCategoryHandler : ICommandHandler<PrepareExpenseCategory>
{
    public string CommandType => "Procurement.PrepareExpenseCategory";

    public async Task<string> HandleAsync(PrepareExpenseCategory command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var code = (command.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (!ExpenseCategories.Code().IsMatch(code))
        {
            throw new DomainException(ExpenseErrors.CategoryInvalid, "The code is 2 to 40 capitals, digits and underscores, starting with a letter (E-GAS-03-5).");
        }

        var (name, goodsType, lineClass) = ExpenseCategories.Validate(command.Name, command.GoodsType606, command.LineClass);
        var account = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT is_control, status, account_class FROM fin.account WHERE company_id = @c AND account_id = @a",
            r => (Control: r.GetBoolean(0), Status: r.GetString(1), Class: r.IsDBNull(2) ? null : r.GetString(2)),
            cancellationToken,
            ("c", context.CompanyId),
            ("a", command.AccountId)).ConfigureAwait(false)).FirstOrDefault();
        if (account is not { Control: false, Status: "ACTIVE", Class: "EXPENSE" })
        {
            throw new DomainException(ExpenseErrors.AccountNotExpense, "The account must be an ACTIVE expense account that is not a control account (E-GAS-2).");
        }

        var preparer = await PurchaseOrderStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "ExpenseCategoryPrepared",
                1,
                ExpenseCategories.Aggregate,
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new { expenseCategoryId = context.ResultRef, code, name, accountId = command.AccountId, goodsType606 = goodsType, lineClass }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO pur.expense_category (expense_category_id, company_id, code, name, account_id, goods_type_606, line_class, status, prepared_by, approved_by, version)
                VALUES (@id, @c, @code, @name, @account, @type, @class, 'DRAFT', @by, NULL, 1)
                """,
                cancellationToken,
                ("id", context.ResultRef),
                ("c", context.CompanyId),
                ("code", code),
                ("name", name),
                ("account", command.AccountId),
                ("type", goodsType),
                ("class", lineClass),
                ("by", preparer)).ConfigureAwait(false);
        }
        catch (DbException ex) when (ex.SqlState == SqlStates.UniqueViolation)
        {
            throw new DomainException(ExpenseErrors.CategoryCodeUsed, $"A category in use already has the code {code}.");
        }

        await context.AppendStateAsync(ExpenseCategories.Aggregate, context.ResultRef, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { expenseCategoryId = context.ResultRef, code, status = "DRAFT", version = 1 });
    }
}

[RequiresPermission("expense_category:prepare")]
public sealed class UpdateExpenseCategoryDraftHandler : ICommandHandler<UpdateExpenseCategoryDraft>
{
    public string CommandType => "Procurement.UpdateExpenseCategoryDraft";

    public async Task<string> HandleAsync(UpdateExpenseCategoryDraft command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var (name, goodsType, lineClass) = ExpenseCategories.Validate(command.Name, command.GoodsType606, command.LineClass);
        var row = await ExpenseCategories.LockAsync(context, command.ExpenseCategoryId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "DRAFT")
        {
            throw new DomainException(ProcurementErrors.InvalidState, $"The category is {row.Status}: only a draft is corrected (E-GAS-01-3).");
        }

        var version = row.Version + 1;
        await context.AppendEventAsync(
            new EventDraft(
                "ExpenseCategoryDraftUpdated", 1, ExpenseCategories.Aggregate, row.Id, version,
                JsonSerializer.Serialize(new { expenseCategoryId = row.Id, name, goodsType606 = goodsType, lineClass }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE pur.expense_category SET name = @n, goods_type_606 = @t, line_class = @l, version = @v WHERE expense_category_id = @id",
            cancellationToken, ("n", name), ("t", goodsType), ("l", lineClass), ("v", version), ("id", row.Id)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { expenseCategoryId = row.Id, version });
    }
}

[RequiresPermission("expense_category:approve", StepUp = true)]
public sealed class ApproveExpenseCategoriesHandler : ICommandHandler<ApproveExpenseCategories>
{
    public string CommandType => "Procurement.ApproveExpenseCategories";

    public async Task<string> HandleAsync(ApproveExpenseCategories command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var ids = (command.ExpenseCategoryIds ?? []).Distinct().ToList();
        if (ids.Count is 0 or > 500)
        {
            throw new DomainException(ExpenseErrors.CategoryInvalid, "Approve 1 to 500 categories at a time.");
        }

        var approver = await PurchaseOrderStore.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var waived = await ControlWaiver.WaivedAsync(context, cancellationToken).ConfigureAwait(false);
        var items = new List<object>(ids.Count);
        var approved = 0;
        foreach (var id in ids.Order())
        {
            try
            {
                // Every check before any write: a refused category leaves nothing behind.
                var row = await ExpenseCategories.LockAsync(context, id, null, cancellationToken).ConfigureAwait(false);
                if (row.Status != "DRAFT")
                {
                    throw new DomainException(ProcurementErrors.InvalidState, $"{row.Code} is {row.Status}.");
                }

                if (row.PreparedBy == approver && !waived)
                {
                    throw new DomainException(ProcurementErrors.ApproverIsCreator, $"{row.Code}: who prepared a category does not approve it (E-GAS-01-4).");
                }

                if ((await Reading.ListAsync(
                        context.Connection, context.Transaction,
                        "SELECT 1 FROM pur.expense_category WHERE company_id = @c AND code = @code AND status = 'ACTIVE'",
                        r => r.GetInt32(0), cancellationToken, ("c", context.CompanyId), ("code", row.Code)).ConfigureAwait(false)).Count > 0)
                {
                    throw new DomainException(ExpenseErrors.CategoryCodeUsed, $"Another category in use has the code {row.Code}.");
                }

                var version = await ExpenseCategories.TransitionAsync(context, row, "ACTIVE", "ExpenseCategoryApproved", approver, CommandType, cancellationToken).ConfigureAwait(false);
                items.Add(new { expenseCategoryId = id, code = row.Code, outcome = "DONE", version });
                approved++;
            }
            catch (DomainException ex)
            {
                items.Add(new { expenseCategoryId = id, outcome = "SKIPPED", code = ex.Code, message = ex.Message });
            }
        }

        return JsonSerializer.Serialize(new { requested = ids.Count, approved, skipped = ids.Count - approved, items });
    }
}

[RequiresPermission("expense_category:prepare")]
public sealed class DeactivateExpenseCategoryHandler : ICommandHandler<DeactivateExpenseCategory>
{
    public string CommandType => "Procurement.DeactivateExpenseCategory";

    public async Task<string> HandleAsync(DeactivateExpenseCategory command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await ExpenseCategories.LockAsync(context, command.ExpenseCategoryId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status == "INACTIVE")
        {
            throw new DomainException(ProcurementErrors.InvalidState, $"{row.Code} is already INACTIVE.");
        }

        var version = await ExpenseCategories.TransitionAsync(context, row, "INACTIVE", "ExpenseCategoryDeactivated", null, CommandType, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { expenseCategoryId = row.Id, status = "INACTIVE", version });
    }
}

[RequiresPermission("expense_category:approve")]
public sealed class ReactivateExpenseCategoryHandler : ICommandHandler<ReactivateExpenseCategory>
{
    public string CommandType => "Procurement.ReactivateExpenseCategory";

    public async Task<string> HandleAsync(ReactivateExpenseCategory command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await ExpenseCategories.LockAsync(context, command.ExpenseCategoryId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "INACTIVE" || row.ApprovedBy is null)
        {
            throw new DomainException(ProcurementErrors.InvalidState, $"{row.Code} is {row.Status}{(row.ApprovedBy is null ? " and was never approved" : string.Empty)}: only an approved category that was taken out of use is reactivated.");
        }

        var version = await ExpenseCategories.TransitionAsync(context, row, "ACTIVE", "ExpenseCategoryReactivated", null, CommandType, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { expenseCategoryId = row.Id, status = "ACTIVE", version });
    }
}
