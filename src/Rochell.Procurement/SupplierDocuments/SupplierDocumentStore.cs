using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Tax.Ecf;

namespace Rochell.Procurement.SupplierDocuments;

/// <summary>OCR1-01 (E-OCR1-01-1): a captured supplier document waits (CAPTURED), has its invoice (REGISTERED) or was discarded.</summary>
public static class SupplierDocumentStatus
{
    public const string Captured = "CAPTURED";
    public const string Registered = "REGISTERED";
    public const string Discarded = "DISCARDED";
}

internal sealed record SupplierDocumentRow(
    Guid Id, string IssuerRnc, string FiscalNumber, string Status, Guid? SiId, string? ProviderId, string? ReceivedStatus, string Response, string? ResponseReason,
    DateTime? ResponseSentAt, long Version);

/// <summary>Reading, locking and changing captured supplier documents, each change with its event (and its history row when the status moves).</summary>
internal static class SupplierDocumentStore
{
    public const string Aggregate = "SupplierDocument";

    private const string Columns =
        "supplier_document_id, issuer_rnc, fiscal_number, status, si_id, provider_id, received_status, commercial_response, response_reason, response_sent_at, version";

    private static SupplierDocumentRow Read(DbDataReader r)
        => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.NullableGuid(4), r.NullableString(5), r.NullableString(6), r.GetString(7), r.NullableString(8),
            r.NullableUtc(9), r.GetInt64(10));

    public static async Task<SupplierDocumentRow?> LockAsync(CommandContext context, string where, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
        => (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            $"SELECT {Columns} FROM pur.supplier_document WHERE company_id = @company AND {where} FOR UPDATE",
            Read,
            cancellationToken,
            [("company", context.CompanyId), .. parameters]).ConfigureAwait(false)).SingleOrDefault();

    public static async Task<SupplierDocumentRow> LockAsync(CommandContext context, Guid id, long? expectedVersion, CancellationToken cancellationToken)
    {
        var row = await LockAsync(context, "supplier_document_id = @id", cancellationToken, ("id", id)).ConfigureAwait(false)
            ?? throw new DomainException(ProcurementErrors.SupplierDocumentNotFound, "The supplier document does not exist.");
        return expectedVersion is null || row.Version == expectedVersion
            ? row
            : throw new DomainException(ProcurementErrors.VersionConflict, $"The supplier document is at version {row.Version}, not {expectedVersion}.");
    }

    /// <summary>
    /// An update of the document with its event; <paramref name="set"/> is constant SQL. When <paramref name="toStatus"/> differs from the
    /// row's, the history row goes with it (ADR-027). Returns the new version.
    /// </summary>
    public static async Task<long> ChangeAsync(
        CommandContext context,
        SupplierDocumentRow row,
        string commandType,
        string eventType,
        object payload,
        string set,
        CancellationToken cancellationToken,
        string? toStatus = null,
        string? reason = null,
        params (string Name, object? Value)[] parameters)
    {
        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(new EventDraft(eventType, 1, Aggregate, row.Id, version, JsonSerializer.Serialize(payload), Publish: true), cancellationToken)
            .ConfigureAwait(false);
        if (toStatus is not null && toStatus != row.Status)
        {
            await context.AppendStateAsync(Aggregate, row.Id, "DOCUMENT", row.Status, toStatus, commandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        }

        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            $"UPDATE pur.supplier_document SET {set}, version = @version WHERE supplier_document_id = @id",
            cancellationToken,
            [("version", version), ("id", row.Id), .. parameters]).ConfigureAwait(false);
        return version;
    }

    /// <summary>The XML once per document, in the database (E-OCR1-01-7).</summary>
    public static async Task AddXmlAsync(CommandContext context, Guid documentId, byte[] xml, Guid user, CancellationToken cancellationToken)
        => await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO pur.supplier_document_file (file_id, company_id, supplier_document_id, kind, content_type, content, storage_key, size_bytes, sha256, added_by, added_at)
            SELECT @id, @c, @d, 'XML', 'application/xml', @content, NULL, @size, @sha, @u, @now
            WHERE NOT EXISTS (SELECT 1 FROM pur.supplier_document_file WHERE supplier_document_id = @d AND kind = 'XML')
            """,
            cancellationToken,
            ("id", context.Ids.NewId()),
            ("c", context.CompanyId),
            ("d", documentId),
            ("content", xml),
            ("size", xml.Length),
            ("sha", SHA256.HashData(xml)),
            ("u", user),
            ("now", context.Clock.UtcNow)).ConfigureAwait(false);

    /// <summary>The lines of one source, once (insert-only; the XML's set wins when shown).</summary>
    public static async Task AddLinesAsync(CommandContext context, Guid documentId, string source, IReadOnlyList<ReceivedEcfLine> lines, CancellationToken cancellationToken)
    {
        var exists = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT 1 FROM pur.supplier_document_line WHERE supplier_document_id = @d AND source = @s LIMIT 1", r => r.GetInt32(0), cancellationToken,
            ("d", documentId), ("s", source)).ConfigureAwait(false)).Count > 0;
        if (exists)
        {
            return;
        }

        var lineNo = 0;
        foreach (var line in lines)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO pur.supplier_document_line
                  (supplier_document_id, company_id, source, line_no, item_code, description, quantity, unit_code, unit_price, itbis_amount, amount, billing_indicator, added_at)
                VALUES (@d, @c, @s, @n, @code, @description, @quantity, @unit, @price, NULL, @amount, @indicator, @now)
                """,
                cancellationToken,
                ("d", documentId),
                ("c", context.CompanyId),
                ("s", source),
                ("n", ++lineNo),
                ("code", line.ItemCode),
                ("description", line.Description),
                ("quantity", line.Quantity),
                ("unit", line.UnitCode),
                ("price", line.UnitPrice),
                ("amount", line.Amount),
                ("indicator", line.BillingIndicator is >= 0 and <= 4 ? line.BillingIndicator : null),
                ("now", context.Clock.UtcNow)).ConfigureAwait(false);
        }
    }
}
