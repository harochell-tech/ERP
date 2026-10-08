using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Tax.Ecf;

// VS4-04 (E-VS4-04-2/3/6): Fiscal › e-CF — the inbox of e-CF, the detail with every call to Alanube and the signed files, the alerts
// for Inicio, and how a person resolves an e-CF that needs attention.

/// <summary>
/// E-VS4-04-3: resolve an e-CF in REQUIRES_ACTION. <c>IN_ALANUBE</c>: Alanube holds it under <paramref name="ProviderId"/> (seen in its
/// portal) and its status is queried again; <c>NOT_ISSUED</c>: it never reached the DGII — the attempt closes as rejected (the document is
/// then resent or voided) and its e-NCF is noted for annulment with the DGII when the range closes.
/// </summary>
public sealed record ResolveEcfDocument(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid DocumentId, long ExpectedVersion, string Resolution, string? ProviderId, string Note)
    : ICommand;

public static class EcfResolutions
{
    public const string InAlanube = "IN_ALANUBE";
    public const string NotIssued = "NOT_ISSUED";
    public const string NotIssuedPrefix = "No emitido (anular el e-NCF ante la DGII): ";
}

[RequiresPermission("ecf:resolve", StepUp = true)]
public sealed class ResolveEcfDocumentHandler(IEnumerable<IEcfSourceUpdater> updaters) : ICommandHandler<ResolveEcfDocument>
{
    private const int NoteMin = 10;
    private const int NoteMax = 300;
    private const int ProviderIdMax = 40;

    public string CommandType => "Tax.ResolveEcfDocument";

    public async Task<string> HandleAsync(ResolveEcfDocument command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var note = (command.Note ?? string.Empty).Trim();
        if (note.Length is < NoteMin or > NoteMax)
        {
            throw new DomainException(EcfErrors.ResolutionInvalid, $"Explain the resolution in {NoteMin} to {NoteMax} characters.");
        }

        var row = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT source_kind, source_id, ecf_type, encf, status, version FROM tax.ecf_document WHERE company_id = @c AND document_id = @d FOR UPDATE",
            r => (Kind: r.GetString(0), Source: r.GetGuid(1), Type: r.GetString(2), Encf: r.GetString(3), Status: r.GetString(4), Version: r.GetInt64(5)),
            cancellationToken,
            ("c", context.CompanyId),
            ("d", command.DocumentId)).ConfigureAwait(false)).SingleOrDefault();
        if (row == default)
        {
            throw new DomainException(EcfErrors.DocumentNotFound, "The e-CF does not exist.");
        }

        if (row.Version != command.ExpectedVersion)
        {
            throw new DomainException(EcfErrors.VersionConflict, $"The e-CF changed (version {row.Version}, expected {command.ExpectedVersion}); reload and retry.");
        }

        if (row.Status != EcfStatuses.RequiresAction)
        {
            throw new DomainException(EcfErrors.InvalidState, $"Only an e-CF that requires attention is resolved (it is {row.Status}).");
        }

        string to;
        string reason;
        string? providerId = null;
        switch (command.Resolution)
        {
            case EcfResolutions.InAlanube:
                providerId = (command.ProviderId ?? string.Empty).Trim().ToUpperInvariant();
                if (providerId.Length is 0 or > ProviderIdMax || !providerId.All(char.IsAsciiLetterOrDigit))
                {
                    throw new DomainException(EcfErrors.ResolutionInvalid, "Enter the e-CF's id as Alanube's portal shows it (letters and digits).");
                }

                to = EcfStatuses.Submitted;
                reason = $"En Alanube con el id {providerId}: {note}";
                break;
            case EcfResolutions.NotIssued:
                to = EcfStatuses.Rejected;
                reason = EcfResolutions.NotIssuedPrefix + note;
                break;
            default:
                throw new DomainException(EcfErrors.ResolutionInvalid, "The resolution is IN_ALANUBE or NOT_ISSUED.");
        }

        var version = row.Version + 1;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "EcfDocumentResolved", 1, EcfQueue.Aggregate, command.DocumentId, version,
                JsonSerializer.Serialize(new { documentId = command.DocumentId, encf = row.Encf, resolution = command.Resolution, providerId, from = row.Status, to, note }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            UPDATE tax.ecf_document SET status = @s, reason = @r, provider_id = coalesce(@pid, provider_id), polls = 0, version = @v,
              next_poll_at = CASE WHEN @s = 'SUBMITTED' THEN @now ELSE NULL END,
              finished_at = CASE WHEN @s = 'REJECTED' THEN @now ELSE finished_at END
            WHERE document_id = @d
            """,
            cancellationToken,
            ("s", to),
            ("r", reason),
            ("pid", (object?)providerId ?? DBNull.Value),
            ("v", version),
            ("now", context.Clock.UtcNow),
            ("d", command.DocumentId)).ConfigureAwait(false);
        await context.AppendStateAsync(EcfQueue.Aggregate, command.DocumentId, "DOCUMENT", row.Status, to, CommandType, eventId, cancellationToken, reason).ConfigureAwait(false);
        if (to == EcfStatuses.Rejected)
        {
            var snapshot = new EcfDocumentSnapshot(command.DocumentId, row.Kind, row.Source, row.Type, row.Encf, to, null, null, null, reason);
            foreach (var updater in updaters.Where(u => u.SourceKind == row.Kind))
            {
                await updater.OnStatusAsync(context, snapshot, cancellationToken).ConfigureAwait(false);
            }
        }

        return JsonSerializer.Serialize(new { documentId = command.DocumentId, status = to, version });
    }
}

/// <summary>E-VS4-04-2: the e-CF, newest first; <paramref name="Status"/> filters (PENDING, SUBMITTED, UNKNOWN_OUTCOME, REQUIRES_ACTION, REJECTED, CONTINGENCY, ACCEPTED…).</summary>
public sealed record ListEcfDocuments(Guid CompanyId, Guid SessionId, string? Status = null, string? Search = null, int Limit = 50, int Offset = 0) : IQuery;

public sealed record EcfDocumentRow(
    Guid DocumentId, string SourceKind, Guid SourceId, string? SourceNo, string? PartyName, string EcfType, string Encf, int AttemptNo, string Status, string? Reason, DateTime CreatedAt,
    DateTime? FinishedAt, decimal? Total, long Version);

public sealed record EcfDocumentList(IReadOnlyList<EcfDocumentRow> Items, int Total);

/// <summary>E-VS4-04-2: one e-CF — its answer, every call to Alanube (never the token) and the signed files it holds.</summary>
public sealed record GetEcfDocument(Guid CompanyId, Guid SessionId, Guid DocumentId) : IQuery;

public sealed record EcfCallView(string Operation, string Mode, int? HttpStatus, string Outcome, string? ProviderCode, string? Message, DateTime CalledAt, int DurationMs);

public sealed record EcfFileView(Guid FileId, string Kind, int Bytes, DateTime FetchedAt);

public sealed record EcfDocumentDetail(
    EcfDocumentRow Document, string? ProviderId, string? TrackId, string? SecurityCode, DateTime? SignatureDate, string? StampUrl, string? GovernmentResponse, int Polls,
    DateTime? NextPollAt, IReadOnlyList<EcfCallView> Calls, IReadOnlyList<EcfFileView> Files, IReadOnlyList<EcfDocumentRow> OtherAttempts);

/// <summary>E-VS4-04-2: the signed XML or PDF of an e-CF.</summary>
public sealed record GetEcfFile(Guid CompanyId, Guid SessionId, Guid FileId) : IQuery;

public sealed record EcfFileContent(string Kind, string FileName, string ContentBase64);

/// <summary>E-VS4-04-1/6: what Inicio warns about — e-CF needing attention, rejected, contingency, ranges running out or expiring.</summary>
public sealed record GetEcfAlerts(Guid CompanyId, Guid SessionId) : IQuery;

public sealed record EcfRangeAlert(string EcfType, string Next, long Remaining, DateOnly ValidUntil, bool Low, bool Expiring);

public sealed record EcfAlerts(int RequiresAction, int Rejected, int Sending, bool InContingency, DateTime? FailingSince, IReadOnlyList<EcfRangeAlert> Ranges);

internal static class EcfReading
{
    public const string RowSelect = """
        SELECT d.document_id, d.source_kind, d.source_id, coalesce(i.invoice_no, n.credit_note_no), p.legal_name, d.ecf_type, d.encf, d.attempt_no, d.status, d.reason,
               d.created_at, d.finished_at, coalesce(i.total, n.total)::numeric(19,2), d.version
        FROM tax.ecf_document d
        LEFT JOIN sal.invoice i ON d.source_kind = 'INVOICE' AND i.invoice_id = d.source_id
        LEFT JOIN sal.credit_note n ON d.source_kind = 'CREDIT_NOTE' AND n.credit_note_id = d.source_id
        LEFT JOIN md.party p ON p.party_id = coalesce(i.party_id, n.party_id)
        """;

    public static EcfDocumentRow Row(System.Data.Common.DbDataReader r)
        => new(
            r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.NullableString(3), r.NullableString(4), r.GetString(5), r.GetString(6), r.GetInt32(7), r.GetString(8), r.NullableString(9),
            r.Utc(10), r.IsDBNull(11) ? null : r.Utc(11), r.IsDBNull(12) ? null : r.GetDecimal(12), r.GetInt64(13));
}

[RequiresPermission("sales:read")]
public sealed class ListEcfDocumentsHandler : IQueryHandler<ListEcfDocuments>
{
    public string QueryType => "Tax.ListEcfDocuments";

    public async Task<string> HandleAsync(ListEcfDocuments query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, query.Offset);
        var status = string.IsNullOrWhiteSpace(query.Status) ? null : query.Status.Trim().ToUpperInvariant();
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : $"%{query.Search.Trim()}%";
        const string Filter = """
            WHERE d.company_id = @c AND (CAST(@s AS text) IS NULL OR d.status = CAST(@s AS text))
              AND (CAST(@q AS text) IS NULL OR d.encf ILIKE CAST(@q AS text) OR i.invoice_no ILIKE CAST(@q AS text) OR n.credit_note_no ILIKE CAST(@q AS text)
                   OR p.legal_name ILIKE CAST(@q AS text))
            """;
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            EcfReading.RowSelect + Filter + " ORDER BY d.created_at DESC, d.document_id DESC LIMIT @limit OFFSET @offset",
            EcfReading.Row,
            cancellationToken,
            ("c", context.CompanyId),
            ("s", (object?)status ?? DBNull.Value),
            ("q", (object?)search ?? DBNull.Value),
            ("limit", query.Limit),
            ("offset", query.Offset)).ConfigureAwait(false);
        var total = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT count(*)::int FROM tax.ecf_document d
            LEFT JOIN sal.invoice i ON d.source_kind = 'INVOICE' AND i.invoice_id = d.source_id
            LEFT JOIN sal.credit_note n ON d.source_kind = 'CREDIT_NOTE' AND n.credit_note_id = d.source_id
            LEFT JOIN md.party p ON p.party_id = coalesce(i.party_id, n.party_id)
            """ + Filter,
            r => r.GetInt32(0),
            cancellationToken,
            ("c", context.CompanyId),
            ("s", (object?)status ?? DBNull.Value),
            ("q", (object?)search ?? DBNull.Value)).ConfigureAwait(false)).Single();
        return ApiJson.Serialize(new EcfDocumentList(items, total));
    }
}

[RequiresPermission("sales:read")]
public sealed class GetEcfDocumentHandler : IQueryHandler<GetEcfDocument>
{
    public string QueryType => "Tax.GetEcfDocument";

    public async Task<string> HandleAsync(GetEcfDocument query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var document = (await Reading.ListAsync(
            context.Connection, context.Transaction, EcfReading.RowSelect + " WHERE d.company_id = @c AND d.document_id = @d", EcfReading.Row, cancellationToken,
            ("c", context.CompanyId), ("d", query.DocumentId)).ConfigureAwait(false)).SingleOrDefault()
            ?? throw new DomainException(EcfErrors.DocumentNotFound, "The e-CF does not exist.");
        var extra = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT provider_id, track_id, security_code, signature_date, stamp_url, government_response::text, polls, next_poll_at FROM tax.ecf_document WHERE document_id = @d",
            r => (ProviderId: r.NullableString(0), TrackId: r.NullableString(1), SecurityCode: r.NullableString(2), SignatureDate: r.IsDBNull(3) ? (DateTime?)null : r.Utc(3),
                StampUrl: r.NullableString(4), Response: r.NullableString(5), Polls: r.GetInt32(6), Next: r.IsDBNull(7) ? (DateTime?)null : r.Utc(7)),
            cancellationToken,
            ("d", query.DocumentId)).ConfigureAwait(false)).Single();
        var calls = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT operation, mode, http_status, outcome, provider_code, message, called_at, duration_ms FROM tax.ecf_call WHERE document_id = @d ORDER BY called_at, call_id",
            r => new EcfCallView(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.GetString(3), r.NullableString(4), r.NullableString(5), r.Utc(6), r.GetInt32(7)),
            cancellationToken,
            ("d", query.DocumentId)).ConfigureAwait(false);
        var files = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT file_id, kind, octet_length(content), fetched_at FROM tax.ecf_file WHERE document_id = @d ORDER BY kind",
            r => new EcfFileView(r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.Utc(3)),
            cancellationToken,
            ("d", query.DocumentId)).ConfigureAwait(false);
        var others = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            EcfReading.RowSelect + " WHERE d.company_id = @c AND d.source_id = @s AND d.document_id <> @d ORDER BY d.attempt_no",
            EcfReading.Row,
            cancellationToken,
            ("c", context.CompanyId),
            ("s", document.SourceId),
            ("d", query.DocumentId)).ConfigureAwait(false);
        return ApiJson.Serialize(new EcfDocumentDetail(
            document, extra.ProviderId, extra.TrackId, extra.SecurityCode, extra.SignatureDate, extra.StampUrl, extra.Response, extra.Polls, extra.Next, calls, files, others));
    }
}

[RequiresPermission("sales:read")]
public sealed class GetEcfFileHandler : IQueryHandler<GetEcfFile>
{
    public string QueryType => "Tax.GetEcfFile";

    public async Task<string> HandleAsync(GetEcfFile query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var file = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT f.kind, d.encf, f.content FROM tax.ecf_file f JOIN tax.ecf_document d ON d.document_id = f.document_id WHERE f.company_id = @c AND f.file_id = @f",
            r => (Kind: r.GetString(0), Encf: r.GetString(1), Content: (byte[])r.GetValue(2)),
            cancellationToken,
            ("c", context.CompanyId),
            ("f", query.FileId)).ConfigureAwait(false)).SingleOrDefault();
        return file == default
            ? throw new DomainException(EcfErrors.DocumentNotFound, "The file does not exist.")
            : ApiJson.Serialize(new EcfFileContent(file.Kind, $"{file.Encf}.{file.Kind.ToLowerInvariant()}", Convert.ToBase64String(file.Content)));
    }
}

[RequiresPermission("sales:read")]
public sealed class GetEcfAlertsHandler : IQueryHandler<GetEcfAlerts>
{
    public string QueryType => "Tax.GetEcfAlerts";

    public async Task<string> HandleAsync(GetEcfAlerts query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var counts = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT count(*) FILTER (WHERE d.status = 'REQUIRES_ACTION')::int,
                   count(*) FILTER (WHERE d.status = 'REJECTED' AND (i.fiscal_status = 'ECF_REJECTED' AND i.commercial_status <> 'VOIDED' OR n.fiscal_status = 'ECF_REJECTED'))::int,
                   count(*) FILTER (WHERE d.status IN ('PENDING', 'SUBMITTED', 'UNKNOWN_OUTCOME', 'CONTINGENCY'))::int
            FROM tax.ecf_document d
            LEFT JOIN sal.invoice i ON d.source_kind = 'INVOICE' AND i.invoice_id = d.source_id
            LEFT JOIN sal.credit_note n ON d.source_kind = 'CREDIT_NOTE' AND n.credit_note_id = d.source_id
            WHERE d.company_id = @c AND NOT EXISTS (SELECT 1 FROM tax.ecf_document x WHERE x.source_id = d.source_id AND x.attempt_no > d.attempt_no)
            """,
            r => (Action: r.GetInt32(0), Rejected: r.GetInt32(1), Sending: r.GetInt32(2)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false)).Single();
        var gateway = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT in_contingency, failing_since FROM tax.ecf_gateway_state WHERE company_id = @c",
            r => (InContingency: r.GetBoolean(0), Since: r.IsDBNull(1) ? (DateTime?)null : r.Utc(1)),
            cancellationToken,
            ("c", context.CompanyId)).ConfigureAwait(false)).SingleOrDefault();
        var today = Rochell.Platform.Time.BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow);
        var ranges = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            WITH p AS (
              SELECT max(CASE WHEN x.param_code = 'ecf_range_alert_pct' THEN (x.value #>> '{}')::numeric END) AS pct,
                     max(CASE WHEN x.param_code = 'ecf_range_alert_days' THEN (x.value #>> '{}')::int END) AS days
              FROM acc.accounting_policy_version v JOIN acc.accounting_policy_parameter x ON x.policy_version_id = v.policy_version_id
              WHERE v.company_id = @c AND v.policy_code = 'REVENUE_ACCOUNTING' AND v.status = 'ACTIVE' AND v.effective_from <= @d AND (v.effective_to IS NULL OR v.effective_to > @d))
            SELECT s.ecf_type, least(s.next_number, s.range_to), s.range_to - s.next_number + 1, s.valid_until,
                   coalesce((s.range_to - s.next_number + 1) <= p.pct * (s.range_to - s.range_from + 1), false),
                   coalesce(s.valid_until <= @d + p.days, false)
            FROM tax.ecf_series s CROSS JOIN p
            WHERE s.company_id = @c AND s.status = 'ACTIVE'
            ORDER BY s.ecf_type
            """,
            r => new EcfRangeAlert(r.GetString(0), EcfSeriesBook.Encf(r.GetString(0), r.GetInt64(1)), r.GetInt64(2), r.Date(3), r.GetBoolean(4), r.GetBoolean(5)),
            cancellationToken,
            ("c", context.CompanyId),
            ("d", today)).ConfigureAwait(false);
        return ApiJson.Serialize(new EcfAlerts(
            counts.Action, counts.Rejected, counts.Sending, gateway.InContingency, gateway.InContingency ? gateway.Since : null, [.. ranges.Where(r => r.Low || r.Expiring)]));
    }
}
