using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Procurement.Imports;

/// <summary>E-USD1-04-1: the DUAs of the company, newest first, with what they owe and which settlement (if any) holds them.</summary>
public sealed record ListCustomsDeclarations(Guid CompanyId, Guid SessionId, string? Status = null, int Limit = 50, int Offset = 0) : IQuery;

public sealed record CustomsDeclarationView(
    Guid DuaId, string DuaNo, DateOnly DuaDate, DateOnly DueDate, Guid SupplierId, string SupplierName, Guid PlantId, decimal CifAmount, decimal DutiesAmount,
    decimal ItbisAmount, decimal OtherAmount, decimal Payable, decimal? OpenAmount, string Status, string? SettlementNo, long Version);

public sealed record CustomsDeclarationList(IReadOnlyList<CustomsDeclarationView> Items, int Limit, int Offset);

[RequiresPermission("supplier_invoice:read")]
public sealed class ListCustomsDeclarationsHandler : IQueryHandler<ListCustomsDeclarations>
{
    public string QueryType => "Procurement.ListCustomsDeclarations";

    public async Task<string> HandleAsync(ListCustomsDeclarations query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT c.dua_id, c.dua_no, c.dua_date, c.due_date, c.party_id, p.legal_name, c.plant_id, c.cif_amount, c.duties_amount, c.itbis_amount, c.other_amount,
                   c.duties_amount + c.itbis_amount + c.other_amount, ap.open_amount, c.status,
                   (SELECT s.settlement_no FROM pur.import_settlement_document d JOIN pur.import_settlement s ON s.settlement_id = d.settlement_id
                    WHERE d.document_kind = 'CUSTOMS_DECLARATION' AND d.document_id = c.dua_id AND s.status IN ('DRAFT', 'POSTED')),
                   c.version
            FROM pur.customs_declaration c
            JOIN md.party p ON p.party_id = c.party_id
            LEFT JOIN fin.ap_document ap ON ap.doc_type = 'CUSTOMS_DECLARATION' AND ap.source_doc_id = c.dua_id
            WHERE c.company_id = @c AND (CAST(@status AS text) IS NULL OR c.status = CAST(@status AS text))
            ORDER BY c.dua_date DESC, c.dua_no DESC
            LIMIT @limit OFFSET @offset
            """,
            r => new CustomsDeclarationView(
                r.GetGuid(0), r.GetString(1), r.Date(2), r.Date(3), r.GetGuid(4), r.GetString(5), r.GetGuid(6), r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9), r.GetDecimal(10),
                r.GetDecimal(11), r.NullableDecimal(12), r.GetString(13), r.NullableString(14), r.GetInt64(15)),
            cancellationToken,
            ("c", context.CompanyId),
            ("status", query.Status),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new CustomsDeclarationList(items, query.Limit, query.Offset));
    }
}

/// <summary>E-USD1-04-6: the import settlements, newest first.</summary>
public sealed record ListImportSettlements(Guid CompanyId, Guid SessionId, string? Status = null, int Limit = 50, int Offset = 0) : IQuery;

public sealed record ImportSettlementSummary(
    Guid SettlementId, string SettlementNo, Guid PlantId, DateOnly SettlementDate, string? Reference, string Status, decimal TotalCost, int Documents, string? PreparedBy,
    string? ApprovedBy, long Version);

public sealed record ImportSettlementList(IReadOnlyList<ImportSettlementSummary> Items, int Limit, int Offset);

[RequiresPermission("supplier_invoice:read")]
public sealed class ListImportSettlementsHandler : IQueryHandler<ListImportSettlements>
{
    public string QueryType => "Procurement.ListImportSettlements";

    public async Task<string> HandleAsync(ListImportSettlements query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            ImportSettlementSql.Summary + """
            WHERE s.company_id = @c AND (CAST(@status AS text) IS NULL OR s.status = CAST(@status AS text))
            ORDER BY s.settlement_date DESC, s.settlement_no DESC
            LIMIT @limit OFFSET @offset
            """,
            ImportSettlementSql.ReadSummary,
            cancellationToken,
            ("c", context.CompanyId),
            ("status", query.Status),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new ImportSettlementList(items, query.Limit, query.Offset));
    }
}

/// <summary>E-USD1-04-4/6: one settlement with its documents, the allocation over the goods lines and its status history.</summary>
public sealed record GetImportSettlement(Guid CompanyId, Guid SessionId, Guid SettlementId) : IQuery;

public sealed record ImportSettlementDocumentView(string Kind, Guid DocumentId, string Number, string SupplierName, DateOnly DocDate, decimal Cost);

public sealed record ImportSettlementAllocationView(Guid SiLineId, string InvoiceNumber, string Description, string Category, decimal BaseValue, decimal AddedCost, decimal TotalCost);

public sealed record ImportSettlementDetail(
    ImportSettlementSummary Settlement, IReadOnlyList<ImportSettlementDocumentView> Documents, IReadOnlyList<ImportSettlementAllocationView> Allocation,
    IReadOnlyList<StateChange> History);

[RequiresPermission("supplier_invoice:read")]
public sealed class GetImportSettlementHandler : IQueryHandler<GetImportSettlement>
{
    public string QueryType => "Procurement.GetImportSettlement";

    public async Task<string> HandleAsync(GetImportSettlement query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var settlement = (await Reading.ListAsync(
            context.Connection, context.Transaction, ImportSettlementSql.Summary + "WHERE s.company_id = @c AND s.settlement_id = @s", ImportSettlementSql.ReadSummary, cancellationToken,
            ("c", context.CompanyId), ("s", query.SettlementId)).ConfigureAwait(false)).SingleOrDefault()
            ?? throw new DomainException(QueryErrors.NotFound, "The settlement does not exist.");
        var documents = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT d.document_kind, d.document_id, coalesce(i.supplier_fiscal_number, 'DUA ' || c.dua_no), p.legal_name, coalesce(i.doc_date, c.dua_date), d.cost_amount
            FROM pur.import_settlement_document d
            LEFT JOIN pur.supplier_invoice i ON d.document_kind <> 'CUSTOMS_DECLARATION' AND i.si_id = d.document_id
            LEFT JOIN pur.customs_declaration c ON d.document_kind = 'CUSTOMS_DECLARATION' AND c.dua_id = d.document_id
            JOIN md.party p ON p.party_id = coalesce(i.party_id, c.party_id)
            WHERE d.settlement_id = @s
            ORDER BY d.document_kind DESC, coalesce(i.doc_date, c.dua_date), d.document_id
            """,
            r => new ImportSettlementDocumentView(r.GetString(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.Date(4), r.GetDecimal(5)),
            cancellationToken,
            ("s", query.SettlementId)).ConfigureAwait(false);
        var allocation = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT a.target_id, i.supplier_fiscal_number, l.description, c.name, a.base_value, a.added_cost, a.base_value + a.added_cost
            FROM pur.import_settlement_allocation a
            JOIN pur.supplier_invoice_line l ON l.si_line_id = a.target_id
            JOIN pur.supplier_invoice i ON i.si_id = l.si_id
            JOIN pur.expense_category c ON c.expense_category_id = l.expense_category_id
            WHERE a.settlement_id = @s
            ORDER BY i.supplier_fiscal_number, l.line_no
            """,
            r => new ImportSettlementAllocationView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6)),
            cancellationToken,
            ("s", query.SettlementId)).ConfigureAwait(false);
        var history = await StateHistory.ReadAsync(context, ImportSettlements.Aggregate, query.SettlementId, cancellationToken).ConfigureAwait(false);
        return ApiJson.Serialize(new ImportSettlementDetail(settlement, documents, allocation, history));
    }
}

internal static class ImportSettlementSql
{
    public const string Summary = """
        SELECT s.settlement_id, s.settlement_no, s.plant_id, s.settlement_date, s.reference, s.status,
               coalesce((SELECT sum(a.added_cost) FROM pur.import_settlement_allocation a WHERE a.settlement_id = s.settlement_id), 0),
               (SELECT count(*)::int FROM pur.import_settlement_document d WHERE d.settlement_id = s.settlement_id),
               coalesce(pu.display_name, pu.email), coalesce(au.display_name, au.email), s.version
        FROM pur.import_settlement s
        LEFT JOIN iam.user pu ON pu.user_id = s.prepared_by
        LEFT JOIN iam.user au ON au.user_id = s.approved_by

        """;

    public static ImportSettlementSummary ReadSummary(System.Data.Common.DbDataReader r)
        => new(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.Date(3), r.NullableString(4), r.GetString(5), r.GetDecimal(6), r.GetInt32(7), r.NullableString(8), r.NullableString(9), r.GetInt64(10));
}
