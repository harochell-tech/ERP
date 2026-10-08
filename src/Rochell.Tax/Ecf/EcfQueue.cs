using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Tax.Ecf;

public sealed record EcfEnqueued(Guid DocumentId, string Encf, int Attempt);

/// <summary>
/// E-VS4-1/3: what Sales calls inside the command that issues an invoice or credit note — the next e-NCF of the type's ACTIVE range
/// (never given back), the e-CF built with it, and a PENDING document in the queue the worker sends. A range that gives its last
/// number closes; an expired range or none refuses the issuance.
/// </summary>
public static class EcfQueue
{
    public const string Aggregate = "EcfDocument";

    /// <summary>E-VS4-03-1: a document goes through the gateway when it is on and its e-CF type has an ACTIVE range; else the manual channel.</summary>
    public static async Task<bool> UsesGatewayAsync(CommandContext context, EcfSwitch gateway, string ecfType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(gateway);
        return gateway.On && (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT 1 FROM tax.ecf_series WHERE company_id = @c AND ecf_type = @t AND status = 'ACTIVE'",
            r => r.GetInt32(0),
            cancellationToken,
            ("c", context.CompanyId),
            ("t", ecfType)).ConfigureAwait(false)).Count > 0;
    }

    /// <param name="build">The e-CF from its e-NCF and the range's due date (Alanube's <c>sequenceDueDate</c>).</param>
    public static async Task<EcfEnqueued> EnqueueAsync(
        CommandContext context, string sourceKind, Guid sourceId, string ecfType, DateOnly issueDate, Func<string, DateOnly, JsonObject> build, string commandType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(build);
        var series = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            "SELECT series_id FROM tax.ecf_series WHERE company_id = @c AND ecf_type = @t AND status = 'ACTIVE'",
            r => r.GetGuid(0),
            cancellationToken,
            ("c", context.CompanyId),
            ("t", ecfType)).ConfigureAwait(false)).SingleOrDefault();
        if (series == Guid.Empty)
        {
            throw new DomainException(EcfErrors.SeriesMissing, $"There is no ACTIVE e-NCF range for e-CF {ecfType}; register the one the DGII authorized (E-VS4-1).");
        }

        var row = await EcfSeriesBook.LockAsync(context, series, null, cancellationToken).ConfigureAwait(false);
        if (row.ValidUntil < issueDate)
        {
            throw new DomainException(EcfErrors.SeriesExpired, $"The e-NCF range of e-CF {ecfType} expired on {row.ValidUntil:yyyy-MM-dd}.");
        }

        var encf = EcfSeriesBook.Encf(ecfType, row.Next);
        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE tax.ecf_series SET next_number = next_number + 1, version = version + 1 WHERE series_id = @s", cancellationToken,
            ("s", series)).ConfigureAwait(false);
        if (row.Next == row.To)
        {
            // Its last number: the range closes (a new one is approved before the next issuance).
            await EcfSeriesBook.TransitionAsync(context, row with { Version = row.Version + 1 }, "CLOSED", "EcfSeriesExhausted", null, commandType, cancellationToken)
                .ConfigureAwait(false);
        }

        var attempt = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT coalesce(max(attempt_no), 0) + 1 FROM tax.ecf_document WHERE source_kind = @k AND source_id = @s", r => r.GetInt32(0),
            cancellationToken, ("k", sourceKind), ("s", sourceId)).ConfigureAwait(false)).Single();
        var payload = build(encf, row.ValidUntil);
        var text = payload.ToJsonString();
        var documentId = context.Ids.NewId();
        var now = context.Clock.UtcNow;
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                "EcfDocumentQueued", 1, Aggregate, documentId, 1,
                JsonSerializer.Serialize(new { documentId, sourceKind, sourceId, ecfType, encf, attempt, seriesId = series }), Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO tax.ecf_document (document_id, company_id, source_kind, source_id, attempt_no, ecf_type, encf, series_id, status, payload, payload_sha256, polls,
                                          next_poll_at, created_at, version)
            VALUES (@id, @c, @k, @s, @a, @t, @e, @series, 'PENDING', CAST(@p AS jsonb), @h, 0, @now, @now, 1)
            """,
            cancellationToken,
            ("id", documentId),
            ("c", context.CompanyId),
            ("k", sourceKind),
            ("s", sourceId),
            ("a", attempt),
            ("t", ecfType),
            ("e", encf),
            ("series", series),
            ("p", text),
            ("h", SHA256.HashData(Encoding.UTF8.GetBytes(text))),
            ("now", now)).ConfigureAwait(false);
        await context.AppendStateAsync(Aggregate, documentId, "DOCUMENT", null, EcfStatuses.Pending, commandType, eventId, cancellationToken).ConfigureAwait(false);
        return new EcfEnqueued(documentId, encf, attempt);
    }
}
