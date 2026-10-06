using System.Data.Common;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.FixedAssets.Cards;

namespace Rochell.FixedAssets.Classes;

/// <summary>
/// E-AF-2, E-AF1-01-3: prepares a DRAFT class for a fixed-asset expense category — useful life in months (1…600), residual % (0 to
/// under 100, 2 decimals), the accumulated depreciation account (asset) and the depreciation account (expense or cost). A category
/// with an ACTIVE class gets a new version, which replaces it when approved.
/// </summary>
public sealed record PrepareAssetClass(
    Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid ExpenseCategoryId, int UsefulLifeMonths, decimal ResidualPct, Guid AccumulatedAccountId,
    Guid ExpenseAccountId) : ICommand;

/// <summary>E-AF-2: the Controller approves a DRAFT class (step-up), never who prepared it; the category's ACTIVE class becomes SUPERSEDED.</summary>
public sealed record ApproveAssetClass(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid AssetClassId, long ExpectedVersion) : ICommand;

/// <summary>Discards a DRAFT class; the ACTIVE one, if any, stays in force.</summary>
public sealed record DiscardAssetClass(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid AssetClassId, long ExpectedVersion) : ICommand;

internal static class AssetClasses
{
    public const string Aggregate = "AssetClass";

    public sealed record Row(Guid Id, Guid CategoryId, int ClassVersion, string Status, Guid PreparedBy, long Version);

    public static async Task<Row> LockAsync(CommandContext context, Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var row = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT asset_class_id, expense_category_id, class_version, status, prepared_by, version FROM fa.asset_class WHERE company_id = @c AND asset_class_id = @id FOR UPDATE",
            r => new Row(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetGuid(4), r.GetInt64(5)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", id)).ConfigureAwait(false)
            ?? throw new DomainException(FixedAssetErrors.ClassNotFound, "The asset class does not exist.");
        return expectedVersion == row.Version
            ? row
            : throw new DomainException(FixedAssetErrors.VersionConflict, $"The class changed (version {row.Version}, expected {expectedVersion}); reload and retry.");
    }

    public static async Task<long> TransitionAsync(
        CommandContext context, Row row, string to, Guid eventId, Guid? approvedBy, string commandType, CancellationToken cancellationToken)
    {
        var version = row.Version + 1;
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE fa.asset_class SET status = @s, approved_by = coalesce(approved_by, @by), approved_at = CASE WHEN CAST(@by AS uuid) IS NULL THEN approved_at ELSE now() END, version = @v WHERE asset_class_id = @id",
            cancellationToken,
            ("s", to),
            ("by", (object?)approvedBy ?? DBNull.Value),
            ("v", version),
            ("id", row.Id)).ConfigureAwait(false);
        await context.AppendStateAsync(Aggregate, row.Id, "DOCUMENT", row.Status, to, commandType, eventId, cancellationToken).ConfigureAwait(false);
        return version;
    }
}

[RequiresPermission("fixed_asset:manage")]
public sealed class PrepareAssetClassHandler : ICommandHandler<PrepareAssetClass>
{
    private const int MaxLifeMonths = 600; // type-limit: 50 years, the column's CHECK

    public string CommandType => "FixedAssets.PrepareAssetClass";

    public async Task<string> HandleAsync(PrepareAssetClass command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        if (command.UsefulLifeMonths is < 1 or > MaxLifeMonths)
        {
            throw new DomainException(FixedAssetErrors.ClassInvalid, "The useful life is 1 to 600 months.");
        }

        if (command.ResidualPct < 0 || command.ResidualPct >= 100m || command.ResidualPct != decimal.Round(command.ResidualPct, 2))
        {
            throw new DomainException(FixedAssetErrors.ClassInvalid, "The residual value is 0 to under 100 %, with up to 2 decimals.");
        }

        if (command.AccumulatedAccountId == command.ExpenseAccountId)
        {
            throw new DomainException(FixedAssetErrors.ClassInvalid, "Accumulated depreciation and the depreciation expense are different accounts.");
        }

        var category = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT c.code, c.goods_type_606 = '04' AND a.account_class = 'ASSET'
            FROM pur.expense_category c JOIN fin.account a ON a.account_id = c.account_id
            WHERE c.company_id = @c AND c.expense_category_id = @id
            """,
            r => (Code: r.GetString(0), FixedAsset: !r.IsDBNull(1) && r.GetBoolean(1)),
            cancellationToken,
            ("c", context.CompanyId),
            ("id", command.ExpenseCategoryId)).ConfigureAwait(false)).FirstOrDefault();
        if (category is not { FixedAsset: true })
        {
            throw new DomainException(FixedAssetErrors.ClassInvalid, "The category is not of fixed assets (an asset account with 606 type 04).");
        }

        await RequireAccountAsync(context, command.AccumulatedAccountId, ["ASSET"], "Accumulated depreciation is an ACTIVE asset account that is not a control account.", cancellationToken)
            .ConfigureAwait(false);
        await RequireAccountAsync(
            context, command.ExpenseAccountId, ["EXPENSE", "COST"], "The depreciation account is an ACTIVE expense or cost account that is not a control account.", cancellationToken)
            .ConfigureAwait(false);

        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", cancellationToken,
            ("k", $"asset-class:{command.ExpenseCategoryId}")).ConfigureAwait(false);
        var classVersion = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT coalesce(max(class_version), 0) + 1 FROM fa.asset_class WHERE expense_category_id = @id",
            r => r.GetInt32(0), cancellationToken, ("id", command.ExpenseCategoryId)).ConfigureAwait(false)).Single();
        var preparer = await FixedAssetCards.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "AssetClassPrepared",
                1,
                AssetClasses.Aggregate,
                context.ResultRef,
                1,
                JsonSerializer.Serialize(new
                {
                    assetClassId = context.ResultRef,
                    expenseCategoryId = command.ExpenseCategoryId,
                    classVersion,
                    usefulLifeMonths = command.UsefulLifeMonths,
                    residualPct = FixedAssetCards.Text(command.ResidualPct),
                    accumulatedAccountId = command.AccumulatedAccountId,
                    expenseAccountId = command.ExpenseAccountId,
                }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO fa.asset_class (asset_class_id, company_id, expense_category_id, class_version, useful_life_months, residual_pct, accumulated_account_id,
                                            expense_account_id, status, prepared_by, version)
                VALUES (@id, @c, @cat, @cv, @life, @pct, @acc, @exp, 'DRAFT', @by, 1)
                """,
                cancellationToken,
                ("id", context.ResultRef),
                ("c", context.CompanyId),
                ("cat", command.ExpenseCategoryId),
                ("cv", classVersion),
                ("life", command.UsefulLifeMonths),
                ("pct", command.ResidualPct),
                ("acc", command.AccumulatedAccountId),
                ("exp", command.ExpenseAccountId),
                ("by", preparer)).ConfigureAwait(false);
        }
        catch (DbException ex) when (ex.SqlState == SqlStates.UniqueViolation)
        {
            throw new DomainException(FixedAssetErrors.InvalidState, $"{category.Code} already has a class waiting for approval; approve or discard it first.");
        }

        await context.AppendStateAsync(AssetClasses.Aggregate, context.ResultRef, "DOCUMENT", null, "DRAFT", CommandType, eventId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { assetClassId = context.ResultRef, classVersion, status = "DRAFT", version = 1 });
    }

    private static async Task RequireAccountAsync(CommandContext context, Guid accountId, string[] classes, string message, CancellationToken cancellationToken)
    {
        var ok = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT 1 FROM fin.account WHERE company_id = @c AND account_id = @a AND status = 'ACTIVE' AND NOT is_control AND account_class = ANY(@k)",
            r => r.GetInt32(0),
            cancellationToken,
            ("c", context.CompanyId),
            ("a", accountId),
            ("k", classes)).ConfigureAwait(false);
        if (ok.Count == 0)
        {
            throw new DomainException(FixedAssetErrors.ClassInvalid, message);
        }
    }
}

[RequiresPermission("fixed_asset:approve", StepUp = true)]
public sealed class ApproveAssetClassHandler : ICommandHandler<ApproveAssetClass>
{
    public string CommandType => "FixedAssets.ApproveAssetClass";

    public async Task<string> HandleAsync(ApproveAssetClass command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await AssetClasses.LockAsync(context, command.AssetClassId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "DRAFT")
        {
            throw new DomainException(FixedAssetErrors.InvalidState, $"The class is {row.Status}: only a draft is approved.");
        }

        var approver = await FixedAssetCards.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        if (approver == row.PreparedBy && !await ControlWaiver.WaivedAsync(context, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(FixedAssetErrors.ApproverIsCreator, "Who prepared a class does not approve it (E-AF-2).");
        }

        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "AssetClassApproved", 1, AssetClasses.Aggregate, row.Id, row.Version + 1,
                JsonSerializer.Serialize(new { assetClassId = row.Id, expenseCategoryId = row.CategoryId, classVersion = row.ClassVersion, approvedBy = approver }), Publish: true),
            cancellationToken).ConfigureAwait(false);

        // E-AF1-01-3: the version in force gives way first (one ACTIVE class per category).
        var active = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT asset_class_id, expense_category_id, class_version, status, prepared_by, version FROM fa.asset_class WHERE expense_category_id = @cat AND status = 'ACTIVE' FOR UPDATE",
            r => new AssetClasses.Row(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetGuid(4), r.GetInt64(5)),
            cancellationToken,
            ("cat", row.CategoryId)).ConfigureAwait(false);
        if (active is not null)
        {
            await AssetClasses.TransitionAsync(context, active, "SUPERSEDED", eventId, null, CommandType, cancellationToken).ConfigureAwait(false);
        }

        var version = await AssetClasses.TransitionAsync(context, row, "ACTIVE", eventId, approver, CommandType, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { assetClassId = row.Id, status = "ACTIVE", supersededClassId = active?.Id, version });
    }
}

[RequiresPermission("fixed_asset:manage")]
public sealed class DiscardAssetClassHandler : ICommandHandler<DiscardAssetClass>
{
    public string CommandType => "FixedAssets.DiscardAssetClass";

    public async Task<string> HandleAsync(DiscardAssetClass command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = await AssetClasses.LockAsync(context, command.AssetClassId, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (row.Status != "DRAFT")
        {
            throw new DomainException(FixedAssetErrors.InvalidState, $"The class is {row.Status}: only a draft is discarded.");
        }

        var eventId = await context.AppendEventAsync(
            new EventDraft("AssetClassDiscarded", 1, AssetClasses.Aggregate, row.Id, row.Version + 1, JsonSerializer.Serialize(new { assetClassId = row.Id }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        var version = await AssetClasses.TransitionAsync(context, row, "DISCARDED", eventId, null, CommandType, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { assetClassId = row.Id, status = "DISCARDED", version });
    }
}
