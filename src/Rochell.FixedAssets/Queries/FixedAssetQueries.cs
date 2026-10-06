using Rochell.FixedAssets.Cards;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.FixedAssets.Queries;

/// <summary>E-AF1-02-9: the asset classes, by status when given, by category and newest version first.</summary>
public sealed record ListAssetClasses(Guid CompanyId, Guid SessionId, string? Status = null) : IQuery;

public sealed record AssetClassView(
    Guid AssetClassId, Guid ExpenseCategoryId, string CategoryCode, string CategoryName, string AssetAccountCode, int ClassVersion, int UsefulLifeMonths,
    decimal ResidualPct, Guid AccumulatedAccountId, string AccumulatedAccountCode, string AccumulatedAccountName, Guid ExpenseAccountId, string ExpenseAccountCode,
    string ExpenseAccountName, string Status, string? PreparedByName, string? ApprovedByName, long Version);

public sealed record AssetClassList(IReadOnlyList<AssetClassView> Items);

[RequiresPermission("ledger:read")]
public sealed class ListAssetClassesHandler : IQueryHandler<ListAssetClasses>
{
    public string QueryType => "FixedAssets.ListAssetClasses";

    public async Task<string> HandleAsync(ListAssetClasses query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        if (query.Status is not (null or "DRAFT" or "ACTIVE" or "SUPERSEDED" or "DISCARDED"))
        {
            throw new DomainException(QueryErrors.InvalidParameter, "status is DRAFT, ACTIVE, SUPERSEDED or DISCARDED.");
        }

        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT k.asset_class_id, k.expense_category_id, c.code, c.name, ca.code, k.class_version, k.useful_life_months, k.residual_pct,
                   k.accumulated_account_id, aa.code, aa.name, k.expense_account_id, ea.code, ea.name, k.status,
                   coalesce(p.display_name, p.email), coalesce(v.display_name, v.email), k.version
            FROM fa.asset_class k
            JOIN pur.expense_category c ON c.expense_category_id = k.expense_category_id
            JOIN fin.account ca ON ca.account_id = c.account_id
            JOIN fin.account aa ON aa.account_id = k.accumulated_account_id
            JOIN fin.account ea ON ea.account_id = k.expense_account_id
            JOIN iam.user p ON p.user_id = k.prepared_by
            LEFT JOIN iam.user v ON v.user_id = k.approved_by
            WHERE k.company_id = @c AND (CAST(@s AS text) IS NULL OR k.status = CAST(@s AS text))
            ORDER BY c.name, k.class_version DESC
            """,
            r => new AssetClassView(
                r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetInt32(6), r.GetDecimal(7), r.GetGuid(8), r.GetString(9),
                r.GetString(10), r.GetGuid(11), r.GetString(12), r.GetString(13), r.GetString(14), r.NullableString(15), r.NullableString(16), r.GetInt64(17)),
            cancellationToken,
            ("c", context.CompanyId),
            ("s", query.Status)).ConfigureAwait(false);
        return ApiJson.Serialize(new AssetClassList(items));
    }
}

/// <summary>E-AF1-02-9: the cards, filtered by status, plant and category, by number.</summary>
public sealed record ListFixedAssets(
    Guid CompanyId, Guid SessionId, string? Status = null, Guid? PlantId = null, Guid? ExpenseCategoryId = null, int Limit = 50, int Offset = 0) : IQuery;

public sealed record FixedAssetSummary(
    Guid AssetId, string AssetNo, string Description, Guid ExpenseCategoryId, string CategoryName, Guid PlantId, string PlantName, string Status, DateOnly AcquiredOn,
    DateOnly? InServiceOn, string? Responsible, decimal Cost, decimal Accumulated, decimal BookValue, int MonthsDepreciated, int? UsefulLifeMonths, long Version);

public sealed record FixedAssetList(IReadOnlyList<FixedAssetSummary> Items, int Limit, int Offset);

[RequiresPermission("ledger:read")]
public sealed class ListFixedAssetsHandler : IQueryHandler<ListFixedAssets>
{
    public string QueryType => "FixedAssets.ListFixedAssets";

    public async Task<string> HandleAsync(ListFixedAssets query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        if (query.Status is not (null or FixedAssetCards.AwaitingService or FixedAssetCards.InService or FixedAssetCards.Disposed or FixedAssetCards.Cancelled))
        {
            throw new DomainException(QueryErrors.InvalidParameter, "status is AWAITING_SERVICE, IN_SERVICE, DISPOSED or CANCELLED.");
        }

        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            FixedAssetSql.Summary + """
            WHERE x.company_id = @c AND (CAST(@s AS text) IS NULL OR x.status = CAST(@s AS text))
              AND (CAST(@p AS uuid) IS NULL OR x.plant_id = CAST(@p AS uuid)) AND (CAST(@k AS uuid) IS NULL OR x.expense_category_id = CAST(@k AS uuid))
            ORDER BY x.asset_no
            LIMIT @limit OFFSET @offset
            """,
            FixedAssetSql.ReadSummary,
            cancellationToken,
            ("c", context.CompanyId),
            ("s", query.Status),
            ("p", query.PlantId),
            ("k", query.ExpenseCategoryId),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new FixedAssetList(items, query.Limit, query.Offset));
    }
}

/// <summary>E-AF1-02-9: one card — where it came from, its class, residual and what is left to depreciate, its movements and history.</summary>
public sealed record GetFixedAsset(Guid CompanyId, Guid SessionId, Guid AssetId) : IQuery;

public sealed record FixedAssetMovementView(string Kind, DateOnly Date, decimal? Amount, string? PlantName, DateTime RecordedAt);

public sealed record FixedAssetDetail(
    FixedAssetSummary Asset, string SourceKind, Guid? SupplierInvoiceId, string? InvoiceNumber, string? SupplierName, string? ExternalCode, decimal Quantity,
    Guid? AssetClassId, decimal? ResidualPct, decimal? ResidualValue, decimal? Depreciable, int? MonthsRemaining, IReadOnlyList<FixedAssetMovementView> Movements,
    IReadOnlyList<StateChange> History);

[RequiresPermission("ledger:read")]
public sealed class GetFixedAssetHandler : IQueryHandler<GetFixedAsset>
{
    public string QueryType => "FixedAssets.GetFixedAsset";

    public async Task<string> HandleAsync(GetFixedAsset query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var summary = (await Reading.ListAsync(
            context.Connection, context.Transaction, FixedAssetSql.Summary + "WHERE x.company_id = @c AND x.asset_id = @a", FixedAssetSql.ReadSummary, cancellationToken,
            ("c", context.CompanyId), ("a", query.AssetId)).ConfigureAwait(false)).SingleOrDefault()
            ?? throw new DomainException(QueryErrors.NotFound, "The fixed asset does not exist.");
        var source = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT x.source_kind, si.si_id, si.supplier_fiscal_number, p.legal_name, x.external_code, x.quantity, x.asset_class_id, x.residual_pct
            FROM fa.asset x
            LEFT JOIN pur.supplier_invoice_line l ON l.si_line_id = x.si_line_id
            LEFT JOIN pur.supplier_invoice si ON si.si_id = l.si_id
            LEFT JOIN md.party p ON p.party_id = si.party_id
            WHERE x.asset_id = @a
            """,
            r => (Kind: r.GetString(0), SiId: r.NullableGuid(1), Number: r.NullableString(2), Supplier: r.NullableString(3), External: r.NullableString(4),
                Quantity: r.GetDecimal(5), ClassId: r.NullableGuid(6), Residual: r.NullableDecimal(7)),
            cancellationToken,
            ("a", query.AssetId)).ConfigureAwait(false)).Single();
        var movements = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT m.kind, m.movement_date, m.amount::numeric(19,2), coalesce(pl.name, pl.code), e.recorded_at
            FROM fa.asset_movement m
            JOIN core.domain_event e ON e.event_id = m.event_id
            LEFT JOIN md.plant pl ON pl.plant_id = m.plant_id
            WHERE m.asset_id = @a
            ORDER BY m.movement_date, e.recorded_at, m.movement_id
            """,
            r => new FixedAssetMovementView(r.GetString(0), r.Date(1), r.NullableDecimal(2), r.NullableString(3), r.Utc(4)),
            cancellationToken,
            ("a", query.AssetId)).ConfigureAwait(false);
        var history = await StateHistory.ReadAsync(context, FixedAssetCards.Aggregate, query.AssetId, cancellationToken).ConfigureAwait(false);
        decimal? residual = source.Residual is { } pct ? FixedAssetCards.Residual(summary.Cost, pct) : null;
        return ApiJson.Serialize(new FixedAssetDetail(
            summary,
            source.Kind,
            source.SiId,
            source.Number,
            source.Supplier,
            source.External,
            source.Quantity,
            source.ClassId,
            source.Residual,
            residual,
            residual is { } r ? summary.Cost - r - summary.Accumulated : null,
            summary.UsefulLifeMonths is { } life ? life - summary.MonthsDepreciated : null,
            movements,
            history));
    }
}

internal static class FixedAssetSql
{
    public const string Summary = """
        SELECT x.asset_id, x.asset_no, x.description, x.expense_category_id, c.name, x.plant_id, coalesce(pl.name, pl.code), x.status, x.acquired_on, x.in_service_on,
               x.responsible, x.cost::numeric(19,2), x.accumulated::numeric(19,2), (x.cost - x.accumulated)::numeric(19,2), x.months_depreciated, x.useful_life_months, x.version
        FROM fa.asset x
        JOIN pur.expense_category c ON c.expense_category_id = x.expense_category_id
        JOIN md.plant pl ON pl.plant_id = x.plant_id

        """;

    public static FixedAssetSummary ReadSummary(System.Data.Common.DbDataReader r)
        => new(
            r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetGuid(3), r.GetString(4), r.GetGuid(5), r.GetString(6), r.GetString(7), r.Date(8),
            r.IsDBNull(9) ? null : r.Date(9), r.NullableString(10), r.GetDecimal(11), r.GetDecimal(12), r.GetDecimal(13), r.GetInt32(14),
            r.IsDBNull(15) ? null : r.GetInt32(15), r.GetInt64(16));
}
