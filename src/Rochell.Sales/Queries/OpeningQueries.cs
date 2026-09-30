using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Sales.Queries;

// E-VS3-02b-2: opening batches, read with configuration:read (the Controller who prepares, the Aprobador de políticas who posts,
// the Auditor and the Director).

public sealed record ListOpeningBatches(Guid CompanyId, Guid SessionId, string? Status = null) : IQuery;

public sealed record OpeningBatchSummary(
    Guid BatchId, string FileName, string Sha256, DateOnly CutoverDate, string Status, int Lines, decimal Total, string? PreparedBy, string? PostedBy, string? ReversalReason,
    Guid? PostingEventId, long Version);

public sealed record OpeningBatchList(IReadOnlyList<OpeningBatchSummary> Items);

internal static class OpeningReading
{
    public const string Select = """
        SELECT b.batch_id, b.file_name, encode(b.source_file_sha256, 'hex'), b.cutover_date, b.status,
               (SELECT count(*) FROM mig.opening_inventory_line l WHERE l.batch_id = b.batch_id)::int,
               (SELECT coalesce(sum(l.value), 0) FROM mig.opening_inventory_line l WHERE l.batch_id = b.batch_id)::numeric(19,2),
               coalesce(pu.display_name, pu.email), coalesce(qu.display_name, qu.email), b.reversal_reason, b.posting_event_id, b.version
        FROM mig.migration_batch b
        JOIN iam.user pu ON pu.user_id = b.prepared_by
        LEFT JOIN iam.user qu ON qu.user_id = b.posted_by
        """;

    public static OpeningBatchSummary Map(System.Data.Common.DbDataReader r)
        => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.Date(3), r.GetString(4), r.GetInt32(5), r.GetDecimal(6), r.NullableString(7), r.NullableString(8), r.NullableString(9),
            r.NullableGuid(10), r.GetInt64(11));
}

[RequiresPermission("configuration:read")]
public sealed class ListOpeningBatchesHandler : IQueryHandler<ListOpeningBatches>
{
    public string QueryType => "Sales.ListOpeningBatches";

    public async Task<string> HandleAsync(ListOpeningBatches query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            OpeningReading.Select + " WHERE b.company_id = @c AND (CAST(@s AS text) IS NULL OR b.status = CAST(@s AS text)) ORDER BY b.cutover_date DESC, b.batch_id DESC",
            OpeningReading.Map,
            cancellationToken,
            ("c", context.CompanyId),
            ("s", query.Status)).ConfigureAwait(false);
        return ApiJson.Serialize(new OpeningBatchList(items));
    }
}

public sealed record GetOpeningBatch(Guid CompanyId, Guid SessionId, Guid BatchId) : IQuery;

public sealed record OpeningLineView(
    int LineNo, string SourceDocumentNumber, string PlantCode, string LocationCode, Guid ItemId, string ItemCode, string ItemDescription, decimal Quantity, decimal UnitCost,
    decimal Value, string? LotCode);

public sealed record OpeningBatchDetail(OpeningBatchSummary Header, IReadOnlyList<OpeningLineView> Lines, IReadOnlyList<StateChange> History);

[RequiresPermission("configuration:read")]
public sealed class GetOpeningBatchHandler : IQueryHandler<GetOpeningBatch>
{
    public string QueryType => "Sales.GetOpeningBatch";

    public async Task<string> HandleAsync(GetOpeningBatch query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var header = await Reading.SingleOrDefaultAsync(
            context.Connection, context.Transaction, OpeningReading.Select + " WHERE b.company_id = @c AND b.batch_id = @b", OpeningReading.Map, cancellationToken,
            ("c", context.CompanyId), ("b", query.BatchId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The opening batch does not exist.");
        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT l.line_no, l.source_document_number, p.code, loc.code, l.item_id, i.code, i.description, l.quantity, l.unit_cost, l.value::numeric(19,2), lot.lot_code
            FROM mig.opening_inventory_line l
            JOIN md.plant p ON p.plant_id = l.plant_id
            JOIN md.location loc ON loc.location_id = l.location_id
            JOIN md.item i ON i.item_id = l.item_id
            LEFT JOIN inv.lot lot ON lot.lot_id = l.lot_id
            WHERE l.batch_id = @b ORDER BY l.line_no
            """,
            r => new OpeningLineView(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetGuid(4), r.GetString(5), r.GetString(6), r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9), r.NullableString(10)),
            cancellationToken,
            ("b", query.BatchId)).ConfigureAwait(false);
        var history = await StateHistory.ReadAsync(context, "OpeningInventory", query.BatchId, cancellationToken).ConfigureAwait(false);
        return ApiJson.Serialize(new OpeningBatchDetail(header, lines, history));
    }
}
