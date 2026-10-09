using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Procurement.SupplierDocuments;

// OCR1-03 (E-OCR1-03-1/8/9, E-OCR-6): the inbox of captured supplier documents, one document with its lines, files, checks and the
// suggestion for its expense lines, and its XML.

/// <summary>
/// E-OCR1-03-1: the inbox. <paramref name="Status"/> CAPTURED / REGISTERED / DISCARDED (null: all); <paramref name="Search"/> an RNC, a name
/// or a number; <paramref name="UnsentOver24Hours"/> only the answers to the DGII kept more than a day without reaching Alanube (E-OCR1-03-8).
/// </summary>
public sealed record ListSupplierDocuments(
    Guid CompanyId, Guid SessionId, string? Status = null, string? Search = null, bool UnsentOver24Hours = false, int Limit = 50, int Offset = 0) : IQuery;

/// <summary>
/// One row of the inbox: <see cref="IssuerName"/> is the supplier's name in Core, else the e-CF's, else the DGII registry's;
/// <see cref="EcfType"/> the two digits after the letter; sources: the XML, a QR read, photos; <see cref="AiRead"/> when the AI read any field.
/// </summary>
public sealed record SupplierDocumentSummary(
    Guid SupplierDocumentId,
    DateOnly? DocDate,
    string IssuerRnc,
    string? IssuerName,
    Guid? SupplierId,
    string FiscalNumber,
    string EcfType,
    decimal? TotalAmount,
    bool HasXml,
    bool QrScanned,
    int Images,
    bool AiRead,
    string? ReceivedStatus,
    string CommercialResponse,
    bool ResponseSent,
    string Status,
    Guid? SupplierInvoiceId,
    bool Registrable,
    DateTime CreatedAt,
    long Version);

public sealed record SupplierDocumentList(IReadOnlyList<SupplierDocumentSummary> Items, int Limit, int Offset);

public sealed record GetSupplierDocument(Guid CompanyId, Guid SessionId, Guid SupplierDocumentId) : IQuery;

/// <summary>A line as read; <see cref="Source"/> XML or AI (the XML's set, when there is one, is the one shown).</summary>
public sealed record SupplierDocumentLineView(
    string Source, int LineNo, string? ItemCode, string Description, decimal Quantity, string? UnitCode, decimal UnitPrice, decimal? ItbisAmount, decimal Amount, int? BillingIndicator);

public sealed record SupplierDocumentFileView(Guid FileId, string Kind, string ContentType, int SizeBytes, DateTime AddedAt);

/// <summary>E-OCR1-03-3: the category and tax type of the supplier's latest expense invoice, proposed for the lines.</summary>
public sealed record ExpenseSuggestion(Guid ExpenseCategoryId, string ExpenseCategoryName, Guid? TaxTypeId, string? TaxTypeCode);

/// <summary>
/// E-OCR-6, E-OCR1-03-9: the document, its lines (XML, else what the AI read), files, the checks shown in red (codes:
/// <c>LINES_DO_NOT_ADD_UP</c>, <c>RNC_NOT_IN_REGISTRY</c>, <c>QR_TOTAL_DIFFERS</c>, <c>SUPPLIER_NOT_IN_CORE</c>, <c>NOT_RECEIVED</c>,
/// <c>NOTE_NOT_REGISTERED</c>, <c>RESPONSE_UNSENT</c>), the linked invoice, the history and the expense suggestion.
/// </summary>
public sealed record SupplierDocumentDetail(
    Guid SupplierDocumentId,
    string IssuerRnc,
    string? IssuerName,
    string? RegistryName,
    Guid? SupplierId,
    string? SupplierName,
    string? BuyerRnc,
    string FiscalNumber,
    string EcfType,
    DateOnly? DocDate,
    decimal? TotalAmount,
    decimal? ItbisAmount,
    string? SecurityCode,
    DateTime? SignatureAt,
    string? QrUrl,
    decimal? QrTotalAmount,
    DateTime? QrScannedAt,
    IReadOnlyList<string> AiFields,
    string Status,
    string? DiscardReason,
    string? ProviderId,
    string? ReceivedStatus,
    string CommercialResponse,
    string? ResponseReason,
    string? RespondedBy,
    DateTime? RespondedAt,
    DateTime? ResponseSentAt,
    Guid? SupplierInvoiceId,
    string? SupplierInvoiceStatus,
    bool Registrable,
    DateTime CreatedAt,
    long Version,
    IReadOnlyList<SupplierDocumentLineView> Lines,
    IReadOnlyList<SupplierDocumentFileView> Files,
    IReadOnlyList<string> Checks,
    ExpenseSuggestion? Suggestion,
    IReadOnlyList<StateChange> History);

/// <summary>The XML of a document, for download (base64).</summary>
public sealed record GetSupplierDocumentFile(Guid CompanyId, Guid SessionId, Guid SupplierDocumentId, Guid FileId) : IQuery;

public sealed record SupplierDocumentFileContent(string FileName, string ContentType, string ContentBase64);

internal static class SupplierDocumentSql
{
    /// <summary>The name to show: the supplier's in Core, the e-CF's, the registry's.</summary>
    public const string Name = "coalesce(p.legal_name, d.issuer_name, (SELECT r.legal_name FROM md.rnc_registry r WHERE r.rnc = d.issuer_rnc))";

    /// <summary>An answer kept more than a day without reaching Alanube (E-OCR1-02-3).</summary>
    public const string Unsent = "(d.commercial_response <> 'NOT_DECLARED' AND d.response_sent_at IS NULL AND d.provider_id IS NOT NULL AND d.responded_at < @now - interval '24 hours')";
}

[RequiresPermission("supplier_invoice:read")]
public sealed class ListSupplierDocumentsHandler : IQueryHandler<ListSupplierDocuments>
{
    public string QueryType => "Procurement.ListSupplierDocuments";

    public async Task<string> HandleAsync(ListSupplierDocuments query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : "%" + query.Search.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal) + "%";
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            $"""
            SELECT d.supplier_document_id, d.doc_date, d.issuer_rnc, {SupplierDocumentSql.Name}, p.party_id, d.fiscal_number, d.total_amount,
                   EXISTS (SELECT 1 FROM pur.supplier_document_file f WHERE f.supplier_document_id = d.supplier_document_id AND f.kind = 'XML'),
                   d.qr_scanned_at IS NOT NULL,
                   (SELECT count(*)::int FROM pur.supplier_document_file f WHERE f.supplier_document_id = d.supplier_document_id AND f.kind IN ('IMAGE', 'PDF')),
                   cardinality(d.ai_fields) > 0, d.received_status, d.commercial_response, d.response_sent_at IS NOT NULL, d.status, d.si_id, d.created_at, d.version
            FROM pur.supplier_document d
            LEFT JOIN md.party p ON p.company_id = d.company_id AND p.rnc = d.issuer_rnc AND p.is_supplier AND p.status = 'ACTIVE'
            WHERE d.company_id = @c
              AND (CAST(@status AS text) IS NULL OR d.status = CAST(@status AS text))
              AND (CAST(@search AS text) IS NULL OR d.issuer_rnc LIKE CAST(@search AS text) OR d.fiscal_number ILIKE CAST(@search AS text)
                   OR {SupplierDocumentSql.Name} ILIKE CAST(@search AS text))
              AND (NOT @unsent OR {SupplierDocumentSql.Unsent})
            ORDER BY coalesce(d.doc_date, CAST(d.created_at AS date)) DESC, d.created_at DESC, d.supplier_document_id
            LIMIT @limit OFFSET @offset
            """,
            r =>
            {
                var fiscalNumber = r.GetString(5);
                return new SupplierDocumentSummary(
                    r.GetGuid(0), r.IsDBNull(1) ? null : r.Date(1), r.GetString(2), r.NullableString(3), r.NullableGuid(4), fiscalNumber, SupplierDocumentRules.TypeOf(fiscalNumber),
                    r.NullableDecimal(6), r.GetBoolean(7), r.GetBoolean(8), r.GetInt32(9), r.GetBoolean(10), r.NullableString(11), r.GetString(12), r.GetBoolean(13), r.GetString(14),
                    r.NullableGuid(15), SupplierDocumentRules.Registrable(fiscalNumber) && r.NullableString(11) != "NOT_RECEIVED" && r.GetString(12) != "REJECTED", r.Utc(16),
                    r.GetInt64(17));
            },
            cancellationToken,
            ("c", context.CompanyId),
            ("status", query.Status),
            ("search", search),
            ("unsent", query.UnsentOver24Hours),
            ("now", context.Clock.UtcNow),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        return ApiJson.Serialize(new SupplierDocumentList(items, query.Limit, query.Offset));
    }
}

[RequiresPermission("supplier_invoice:read")]
public sealed class GetSupplierDocumentHandler : IQueryHandler<GetSupplierDocument>
{
    public string QueryType => "Procurement.GetSupplierDocument";

    public async Task<string> HandleAsync(GetSupplierDocument query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var header = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT d.supplier_document_id, d.issuer_rnc, d.issuer_name, (SELECT r.legal_name FROM md.rnc_registry r WHERE r.rnc = d.issuer_rnc), p.party_id, p.legal_name, d.buyer_rnc,
                   d.fiscal_number, d.doc_date, d.total_amount, d.itbis_amount, d.security_code, d.signature_at, d.qr_url, d.qr_total_amount, d.qr_scanned_at, d.ai_fields,
                   d.status, d.discard_reason, d.provider_id, d.received_status, d.commercial_response, d.response_reason,
                   (SELECT coalesce(u.display_name, u.email) FROM iam.user u WHERE u.user_id = d.responded_by), d.responded_at, d.response_sent_at,
                   d.si_id, si.document_status::text, d.created_at, d.version, (SELECT count(*) FROM md.rnc_registry r WHERE r.rnc = d.issuer_rnc) > 0
            FROM pur.supplier_document d
            LEFT JOIN md.party p ON p.company_id = d.company_id AND p.rnc = d.issuer_rnc AND p.is_supplier AND p.status = 'ACTIVE'
            LEFT JOIN pur.supplier_invoice si ON si.si_id = d.si_id
            WHERE d.company_id = @c AND d.supplier_document_id = @d
            """,
            r =>
            {
                var fiscalNumber = r.GetString(7);
                return (Detail: new SupplierDocumentDetail(
                    r.GetGuid(0), r.GetString(1), r.NullableString(2), r.NullableString(3), r.NullableGuid(4), r.NullableString(5), r.NullableString(6), fiscalNumber,
                    SupplierDocumentRules.TypeOf(fiscalNumber), r.IsDBNull(8) ? null : r.Date(8), r.NullableDecimal(9), r.NullableDecimal(10), r.NullableString(11), r.NullableUtc(12),
                    r.NullableString(13), r.NullableDecimal(14), r.NullableUtc(15), r.GetFieldValue<string[]>(16), r.GetString(17), r.NullableString(18), r.NullableString(19),
                    r.NullableString(20), r.GetString(21), r.NullableString(22), r.NullableString(23), r.NullableUtc(24), r.NullableUtc(25), r.NullableGuid(26), r.NullableString(27),
                    SupplierDocumentRules.Registrable(fiscalNumber) && r.NullableString(20) != "NOT_RECEIVED" && r.GetString(21) != "REJECTED", r.Utc(28), r.GetInt64(29), [], [], [], null, []),
                    InRegistry: r.GetBoolean(30));
            },
            cancellationToken,
            ("c", context.CompanyId),
            ("d", query.SupplierDocumentId)).ConfigureAwait(false)).SingleOrDefault();
        if (header.Detail is null)
        {
            throw new DomainException(QueryErrors.NotFound, "The supplier document does not exist.");
        }

        var d = header.Detail;
        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT source, line_no, item_code, description, quantity, unit_code, unit_price, itbis_amount, amount, billing_indicator
            FROM pur.supplier_document_line
            WHERE supplier_document_id = @d
              AND source = CASE WHEN EXISTS (SELECT 1 FROM pur.supplier_document_line x WHERE x.supplier_document_id = @d AND x.source = 'XML') THEN 'XML' ELSE 'AI' END
            ORDER BY line_no
            """,
            r => new SupplierDocumentLineView(
                r.GetString(0), r.GetInt32(1), r.NullableString(2), r.GetString(3), r.GetDecimal(4), r.NullableString(5), r.GetDecimal(6), r.NullableDecimal(7), r.GetDecimal(8),
                r.IsDBNull(9) ? null : r.GetInt32(9)),
            cancellationToken,
            ("d", d.SupplierDocumentId)).ConfigureAwait(false);
        var files = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT file_id, kind, content_type, size_bytes, added_at FROM pur.supplier_document_file WHERE supplier_document_id = @d ORDER BY added_at, file_id",
            r => new SupplierDocumentFileView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.Utc(4)),
            cancellationToken,
            ("d", d.SupplierDocumentId)).ConfigureAwait(false);

        // E-OCR-6: what is flagged in red. Sums are compared, never computed into the document.
        var checks = new List<string>();
        if (d.TotalAmount is { } total && lines.Count > 0 && lines.Sum(l => l.Amount) + (d.ItbisAmount ?? 0m) != total)
        {
            checks.Add("LINES_DO_NOT_ADD_UP");
        }

        if (!header.InRegistry)
        {
            checks.Add("RNC_NOT_IN_REGISTRY");
        }

        if (d.QrTotalAmount is { } qr && d.TotalAmount is { } t && qr != t)
        {
            checks.Add("QR_TOTAL_DIFFERS");
        }

        if (d.SupplierId is null)
        {
            checks.Add("SUPPLIER_NOT_IN_CORE");
        }

        if (d.ReceivedStatus == "NOT_RECEIVED")
        {
            checks.Add("NOT_RECEIVED");
        }

        if (!SupplierDocumentRules.Registrable(d.FiscalNumber))
        {
            checks.Add("NOTE_NOT_REGISTERED");
        }

        if (d.CommercialResponse != "NOT_DECLARED" && d.ResponseSentAt is null && d.ProviderId is not null && d.RespondedAt < context.Clock.UtcNow.AddHours(-24))
        {
            checks.Add("RESPONSE_UNSENT");
        }

        // E-OCR1-03-3: the supplier's latest expense invoice proposes category and tax type.
        var suggestion = d.SupplierId is null
            ? null
            : (await Reading.ListAsync(
                context.Connection,
                context.Transaction,
                """
                SELECT l.expense_category_id, c.name, l.tax_rule_id, fr.code
                FROM pur.supplier_invoice si
                JOIN pur.supplier_invoice_line l ON l.si_id = si.si_id
                JOIN pur.expense_category c ON c.expense_category_id = l.expense_category_id
                LEFT JOIN tax.fiscal_rule fr ON fr.rule_id = l.tax_rule_id
                WHERE si.company_id = @c AND si.party_id = @p AND si.doc_class = 'EXPENSE' AND si.document_status <> 'VOIDED'
                ORDER BY si.doc_date DESC, si.si_id DESC, l.line_no
                LIMIT 1
                """,
                r => new ExpenseSuggestion(r.GetGuid(0), r.GetString(1), r.NullableGuid(2), r.NullableString(3)),
                cancellationToken,
                ("c", context.CompanyId),
                ("p", d.SupplierId)).ConfigureAwait(false)).SingleOrDefault();
        var history = await StateHistory.ReadAsync(context, SupplierDocumentStore.Aggregate, d.SupplierDocumentId, cancellationToken).ConfigureAwait(false);
        return ApiJson.Serialize(d with { Lines = lines, Files = files, Checks = checks, Suggestion = suggestion, History = history });
    }
}

[RequiresPermission("supplier_invoice:read")]
public sealed class GetSupplierDocumentFileHandler : IQueryHandler<GetSupplierDocumentFile>
{
    public string QueryType => "Procurement.GetSupplierDocumentFile";

    public async Task<string> HandleAsync(GetSupplierDocumentFile query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var file = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT d.fiscal_number, f.content_type, f.content
            FROM pur.supplier_document_file f JOIN pur.supplier_document d ON d.supplier_document_id = f.supplier_document_id
            WHERE d.company_id = @c AND f.supplier_document_id = @d AND f.file_id = @f AND f.kind = 'XML'
            """,
            r => new SupplierDocumentFileContent(r.GetString(0) + ".xml", r.GetString(1), Convert.ToBase64String(r.GetFieldValue<byte[]>(2))),
            cancellationToken,
            ("c", context.CompanyId),
            ("d", query.SupplierDocumentId),
            ("f", query.FileId)).ConfigureAwait(false)).SingleOrDefault()
            ?? throw new DomainException(QueryErrors.NotFound, "The file does not exist.");
        return ApiJson.Serialize(file);
    }
}
