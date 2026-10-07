using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Json;
using Rochell.Platform.Queries;

namespace Rochell.Tax.Ecf;

// VS4-02 (E-VS4-3…6, E-VS4-02-1…7): the worker's steps, run by the daily process (PROCESO_DIARIO) through the command pipeline. Each
// step takes one e-CF one move forward — send it, ask for its status, fetch its signed files — records the call, and tells the invoice
// or credit note when it reaches an answer. Nothing is resent blindly: without Alanube's id an unknown outcome is sent again with the
// SAME e-NCF, which Alanube refuses as a duplicate naming its id.

/// <summary>E-VS4-02-1: one step of one e-CF (the worker's idempotency key is the document and its version).</summary>
public sealed record AdvanceEcfDocument(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid DocumentId) : ICommand;

/// <summary>E-VS4-02-5: a valid webhook moves forward the status query of the e-CF it names (Alanube's id), or of every one in flight.</summary>
public sealed record NudgeEcfDocuments(Guid CompanyId, Guid SessionId, string IdempotencyKey, string? ProviderId) : ICommand;

/// <summary>E-VS4-02-1: the e-CF due for a step: their next query has come, or their signed files are still missing.</summary>
public sealed record ListDueEcfDocuments(Guid CompanyId, Guid SessionId, int Limit = 50) : IQuery;

public sealed record DueEcfDocument(Guid DocumentId, long Version);

public sealed record DueEcfDocumentList(IReadOnlyList<DueEcfDocument> Items);

internal static class EcfSchedule
{
    /// <summary>E-VS4-02-2: after sending, 10 s, 30 s, 1 min, 2 min, 5 min, then every 15 min.</summary>
    private static readonly TimeSpan[] Steps =
        [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    public static readonly TimeSpan Settled = TimeSpan.FromMinutes(15);

    /// <summary>E-VS4-02-2: an e-CF without a final answer this long after it was queued needs attention.</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromHours(24);

    /// <summary>How long to wait after the step numbered <paramref name="polls"/> (1 = the first after sending).</summary>
    public static TimeSpan After(int polls) => polls >= 1 && polls <= Steps.Length ? Steps[polls - 1] : Settled;
}

internal sealed record EcfRow(
    Guid Id, string SourceKind, Guid SourceId, string EcfType, string Encf, string Status, JsonObject Payload, string? ProviderId, int Polls, DateTime CreatedAt, long Version,
    bool HasFiles);

[RequiresPermission("ecf:process")]
public sealed class AdvanceEcfDocumentHandler(IEcfProvider provider, IEnumerable<IEcfSourceUpdater> updaters) : ICommandHandler<AdvanceEcfDocument>
{
    public string CommandType => "Tax.AdvanceEcfDocument";

    public async Task<string> HandleAsync(AdvanceEcfDocument command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var row = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT d.document_id, d.source_kind, d.source_id, d.ecf_type, d.encf, d.status, d.payload::text, d.provider_id, d.polls, d.created_at, d.version,
                   (SELECT count(*) FROM tax.ecf_file f WHERE f.document_id = d.document_id) = 2
            FROM tax.ecf_document d WHERE d.company_id = @c AND d.document_id = @d FOR UPDATE
            """,
            r => new EcfRow(
                r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetString(5), (JsonObject)JsonNode.Parse(r.GetString(6))!, r.NullableString(7),
                r.GetInt32(8), r.Utc(9), r.GetInt64(10), r.GetBoolean(11)),
            cancellationToken,
            ("c", context.CompanyId),
            ("d", command.DocumentId)).ConfigureAwait(false)).SingleOrDefault()
            ?? throw new DomainException(EcfErrors.DocumentNotFound, "The e-CF does not exist.");
        var step = new Step(context, provider, updaters, row, CommandType, cancellationToken);
        var outcome = row.Status switch
        {
            EcfStatuses.Accepted or EcfStatuses.AcceptedConditional when !row.HasFiles => await step.FetchFilesAsync().ConfigureAwait(false),
            EcfStatuses.Accepted or EcfStatuses.AcceptedConditional or EcfStatuses.Rejected or EcfStatuses.RequiresAction => "nothing to do",
            _ when context.Clock.UtcNow - row.CreatedAt > EcfSchedule.Horizon
                => await step.ChangeAsync(EcfStatuses.RequiresAction, null, "Sin respuesta final de la DGII en 24 horas: verifique el e-CF en el portal de Alanube.").ConfigureAwait(false),
            _ when row.ProviderId is null => await step.SubmitAsync().ConfigureAwait(false),
            _ => await step.QueryAsync().ConfigureAwait(false),
        };
        return JsonSerializer.Serialize(new { documentId = row.Id, encf = row.Encf, outcome });
    }

    private sealed class Step(CommandContext context, IEcfProvider provider, IEnumerable<IEcfSourceUpdater> updaters, EcfRow row, string commandType, CancellationToken ct)
    {
        /// <summary>The row's current status and version: a contingency switch during the step moves it too.</summary>
        private async Task<(string Status, long Version)> CurrentAsync()
            => (await Reading.ListAsync(
                context.Connection, context.Transaction, "SELECT status, version FROM tax.ecf_document WHERE document_id = @d", r => (r.GetString(0), r.GetInt64(1)), ct, ("d", row.Id))
                .ConfigureAwait(false)).Single();

        public async Task<string> SubmitAsync()
        {
            var watch = Stopwatch.StartNew();
            var result = await provider.SubmitAsync(row.EcfType, row.Payload, ct).ConfigureAwait(false);
            switch (result.Kind)
            {
                case SubmitKind.Registered:
                    await CallAsync("SUBMIT", result.HttpStatus, "OK", null, null, watch).ConfigureAwait(false);
                    await Health.SucceededAsync(context, updaters, commandType, ct).ConfigureAwait(false);
                    await UpdateAsync("provider_id = @pid", ("pid", result.ProviderId!)).ConfigureAwait(false);
                    return result.Document?.LegalStatus is not null
                        ? await AnswerAsync(result.Document).ConfigureAwait(false)
                        : await ScheduleAsync(EcfStatuses.Submitted, 1).ConfigureAwait(false);
                case SubmitKind.Duplicate when result.ProviderId is not null:
                    // E-VS4-4: Alanube already holds this e-NCF — the earlier send that gave no answer; follow that one.
                    await CallAsync("SUBMIT", result.HttpStatus, "REJECTED", result.Code, result.Message, watch).ConfigureAwait(false);
                    await Health.SucceededAsync(context, updaters, commandType, ct).ConfigureAwait(false);
                    await UpdateAsync("provider_id = @pid", ("pid", result.ProviderId)).ConfigureAwait(false);
                    return await ScheduleAsync(EcfStatuses.Submitted, 1).ConfigureAwait(false);
                case SubmitKind.Duplicate:
                    await CallAsync("SUBMIT", result.HttpStatus, "REJECTED", result.Code, result.Message, watch).ConfigureAwait(false);
                    await Health.SucceededAsync(context, updaters, commandType, ct).ConfigureAwait(false);
                    return await ChangeAsync(
                        EcfStatuses.RequiresAction, null, $"Alanube ya registra el e-NCF {row.Encf} ({result.Code}) y no dijo cuál documento: verifíquelo en su portal.").ConfigureAwait(false);
                case SubmitKind.Invalid:
                    await CallAsync("SUBMIT", result.HttpStatus, "REJECTED", result.Code, result.Message, watch).ConfigureAwait(false);
                    await Health.SucceededAsync(context, updaters, commandType, ct).ConfigureAwait(false);
                    return await ChangeAsync(EcfStatuses.RequiresAction, null, $"Alanube rechazó el contenido del e-CF: {result.Code} {result.Message}".Trim()).ConfigureAwait(false);
                default:
                    await CallAsync("SUBMIT", result.HttpStatus, result.HttpStatus is null ? "TIMEOUT" : "ERROR", result.Code, result.Message, watch).ConfigureAwait(false);
                    var contingency = await Health.FailedAsync(context, updaters, commandType, ct).ConfigureAwait(false);
                    return await ScheduleAsync(contingency ? EcfStatuses.Contingency : EcfStatuses.Unknown, row.Polls + 1).ConfigureAwait(false);
            }
        }

        public async Task<string> QueryAsync()
        {
            var watch = Stopwatch.StartNew();
            var result = await provider.QueryAsync(row.EcfType, row.ProviderId!, ct).ConfigureAwait(false);
            switch (result.Kind)
            {
                case QueryKind.Found:
                    await CallAsync("QUERY", result.HttpStatus, "OK", result.Document!.Status, null, watch).ConfigureAwait(false);
                    await Health.SucceededAsync(context, updaters, commandType, ct).ConfigureAwait(false);
                    return result.Document.LegalStatus is not null || result.Document.Status == "FAILED"
                        ? await AnswerAsync(result.Document).ConfigureAwait(false)
                        : await ScheduleAsync(EcfStatuses.Submitted, row.Polls + 1).ConfigureAwait(false);
                case QueryKind.NotFound:
                    await CallAsync("QUERY", result.HttpStatus, "REJECTED", result.Code, result.Message, watch).ConfigureAwait(false);
                    await Health.SucceededAsync(context, updaters, commandType, ct).ConfigureAwait(false);
                    return await ChangeAsync(EcfStatuses.RequiresAction, null, "Alanube no encuentra el e-CF que había registrado: verifíquelo en su portal.").ConfigureAwait(false);
                default:
                    await CallAsync("QUERY", result.HttpStatus, result.HttpStatus is null ? "TIMEOUT" : "ERROR", result.Code, result.Message, watch).ConfigureAwait(false);
                    var contingency = await Health.FailedAsync(context, updaters, commandType, ct).ConfigureAwait(false);
                    return await ScheduleAsync(contingency ? EcfStatuses.Contingency : EcfStatuses.Submitted, row.Polls + 1).ConfigureAwait(false);
            }
        }

        /// <summary>E-VS4-02-7: the signed XML and PDF of an accepted e-CF, with fresh links from a status query; retried at the next pass.</summary>
        public async Task<string> FetchFilesAsync()
        {
            var watch = Stopwatch.StartNew();
            var result = await provider.QueryAsync(row.EcfType, row.ProviderId!, ct).ConfigureAwait(false);
            await CallAsync("QUERY", result.HttpStatus, result.Kind == QueryKind.Found ? "OK" : "ERROR", result.Code, result.Message, watch).ConfigureAwait(false);
            if (result.Document is null)
            {
                return "files pending";
            }

            var stored = 0;
            foreach (var (kind, url) in new[] { ("XML", result.Document.XmlUrl), ("PDF", result.Document.PdfUrl) })
            {
                var exists = (await Reading.ListAsync(
                    context.Connection, context.Transaction, "SELECT 1 FROM tax.ecf_file WHERE document_id = @d AND kind = @k", r => r.GetInt32(0), ct, ("d", row.Id), ("k", kind))
                    .ConfigureAwait(false)).Count > 0;
                if (exists || url is null)
                {
                    stored += exists ? 1 : 0;
                    continue;
                }

                var download = Stopwatch.StartNew();
                var bytes = await provider.DownloadAsync(url, ct).ConfigureAwait(false);
                await CallAsync("DOWNLOAD", null, bytes is null ? "ERROR" : "OK", kind, null, download).ConfigureAwait(false);
                if (bytes is { Length: > 0 })
                {
                    await Sql.ExecuteAsync(
                        context.Connection,
                        context.Transaction,
                        "INSERT INTO tax.ecf_file (file_id, company_id, document_id, kind, content, sha256, fetched_at) VALUES (@id, @c, @d, @k, @b, @h, @at)",
                        ct,
                        ("id", context.Ids.NewId()),
                        ("c", context.CompanyId),
                        ("d", row.Id),
                        ("k", kind),
                        ("b", bytes),
                        ("h", SHA256.HashData(bytes)),
                        ("at", context.Clock.UtcNow)).ConfigureAwait(false);
                    stored++;
                }
            }

            return stored == 2 ? "files stored" : "files pending";
        }

        /// <summary>E-VS4-5/6: the DGII's answer (or Alanube's own refusal, FAILED) — final.</summary>
        private async Task<string> AnswerAsync(ProviderDocument document)
        {
            var status = document.LegalStatus switch
            {
                "ACCEPTED" => EcfStatuses.Accepted,
                "ACCEPTED_WITH_OBSERVATIONS" => EcfStatuses.AcceptedConditional,
                _ => EcfStatuses.Rejected,
            };
            string? reason = null;
            if (status == EcfStatuses.Rejected)
            {
                var messages = (document.GovernmentResponse?["value"] as JsonArray)?.Select(v => v?["valor"]?.ToString()).Where(v => !string.IsNullOrWhiteSpace(v)).ToList() ?? [];
                reason = messages.Count > 0 ? "DGII: " + string.Join(" · ", messages)
                    : document.ErrorMessage is not null ? $"Alanube: {document.ErrorCode} {document.ErrorMessage}".Trim()
                    : "Rechazado por la DGII sin detalle.";
            }

            await UpdateAsync(
                "track_id = @tid, security_code = @sc, signature_date = @sd, stamp_url = @su, government_response = CAST(@gr AS jsonb)",
                ("tid", (object?)document.TrackId ?? DBNull.Value),
                ("sc", (object?)document.SecurityCode ?? DBNull.Value),
                ("sd", (object?)document.SignatureDate ?? DBNull.Value),
                ("su", (object?)document.StampUrl ?? DBNull.Value),
                ("gr", (object?)document.GovernmentResponse?.ToJsonString() ?? DBNull.Value)).ConfigureAwait(false);
            var result = await ChangeAsync(status, null, reason, document).ConfigureAwait(false);
            if (status != EcfStatuses.Rejected)
            {
                result += "; " + await FetchFilesAsync().ConfigureAwait(false);
            }

            return result;
        }

        private async Task<string> ScheduleAsync(string status, int polls)
        {
            var next = context.Clock.UtcNow + EcfSchedule.After(status == EcfStatuses.Contingency ? int.MaxValue : polls);
            await UpdateAsync("polls = @polls, next_poll_at = @next", ("polls", polls), ("next", next)).ConfigureAwait(false);
            var (current, _) = await CurrentAsync().ConfigureAwait(false);
            return status == current ? $"{status}, next at {next:O}" : await ChangeAsync(status, next, null).ConfigureAwait(false);
        }

        /// <summary>A status change with its event, history row and the source's update (E-VS4-01-2/3).</summary>
        public async Task<string> ChangeAsync(string status, DateTime? next, string? reason, ProviderDocument? document = null)
        {
            var (from, version) = await CurrentAsync().ConfigureAwait(false);
            var eventId = await context.AppendEventAsync(
                new EventDraft(
                    "EcfDocumentStatusChanged", 1, EcfQueue.Aggregate, row.Id, version + 1,
                    JsonSerializer.Serialize(new { documentId = row.Id, encf = row.Encf, from, to = status, reason }), Publish: true),
                ct).ConfigureAwait(false);
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                UPDATE tax.ecf_document SET status = @s, reason = coalesce(@r, reason), next_poll_at = @n, version = version + 1,
                  finished_at = CASE WHEN @s IN ('ACCEPTED', 'ACCEPTED_CONDITIONAL', 'REJECTED') THEN @now ELSE finished_at END
                WHERE document_id = @d
                """,
                ct,
                ("s", status),
                ("now", context.Clock.UtcNow),
                ("r", (object?)reason ?? DBNull.Value),
                ("n", (object?)next ?? DBNull.Value),
                ("d", row.Id)).ConfigureAwait(false);
            await context.AppendStateAsync(EcfQueue.Aggregate, row.Id, "DOCUMENT", from, status, commandType, eventId, ct, reason).ConfigureAwait(false);
            if (status != EcfStatuses.Submitted && status != EcfStatuses.Unknown && status != EcfStatuses.Contingency)
            {
                var snapshot = new EcfDocumentSnapshot(
                    row.Id, row.SourceKind, row.SourceId, row.EcfType, row.Encf, status, document?.SecurityCode, document?.StampUrl, document?.SignatureDate, reason);
                foreach (var updater in updaters.Where(u => u.SourceKind == row.SourceKind))
                {
                    await updater.OnStatusAsync(context, snapshot, ct).ConfigureAwait(false);
                }
            }

            return status;
        }

        private Task UpdateAsync(string set, params (string Name, object Value)[] values)
            => Sql.ExecuteAsync(
                context.Connection, context.Transaction, $"UPDATE tax.ecf_document SET {set}, version = version + 1 WHERE document_id = @d", ct,
                [.. values.Select(v => (v.Name, (object?)v.Value)), ("d", row.Id)]);

        private Task CallAsync(string operation, int? httpStatus, string outcome, string? code, string? message, Stopwatch watch)
            => Health.CallAsync(context, provider.Mode, row.Id, operation, httpStatus, outcome, code, message, watch, ct);
    }
}

/// <summary>E-VS4-02-4: Alanube's health per company — consecutive failures since when, and contingency.</summary>
internal static class Health
{
    public static Task CallAsync(
        CommandContext context, string mode, Guid? documentId, string operation, int? httpStatus, string outcome, string? code, string? message, Stopwatch watch, CancellationToken ct)
        => Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO tax.ecf_call (call_id, company_id, document_id, operation, mode, http_status, outcome, provider_code, message, called_at, duration_ms)
            VALUES (@id, @c, @d, @op, @m, @hs, @o, @pc, @msg, @at, @ms)
            """,
            ct,
            ("id", context.Ids.NewId()),
            ("c", context.CompanyId),
            ("d", (object?)documentId ?? DBNull.Value),
            ("op", operation),
            ("m", mode),
            ("hs", (object?)httpStatus ?? DBNull.Value),
            ("o", outcome),
            ("pc", (object?)code?[..Math.Min(code.Length, 40)] ?? DBNull.Value),
            ("msg", (object?)message?[..Math.Min(message.Length, 2000)] ?? DBNull.Value),
            ("at", context.Clock.UtcNow),
            ("ms", (int)Math.Min(watch.ElapsedMilliseconds, int.MaxValue)));

    /// <summary>A good answer: the failures end; out of contingency, the e-CF held go back to the queue now.</summary>
    public static async Task SucceededAsync(CommandContext context, IEnumerable<IEcfSourceUpdater> updaters, string commandType, CancellationToken ct)
    {
        var wasInContingency = (await Reading.ListAsync(
            context.Connection, context.Transaction, "SELECT in_contingency FROM tax.ecf_gateway_state WHERE company_id = @c FOR UPDATE", r => r.GetBoolean(0), ct,
            ("c", context.CompanyId)).ConfigureAwait(false)).SingleOrDefault();
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO tax.ecf_gateway_state (company_id, consecutive_failures, failing_since, last_ok_at, in_contingency, version) VALUES (@c, 0, NULL, @now, false, 1)
            ON CONFLICT (company_id) DO UPDATE SET consecutive_failures = 0, failing_since = NULL, last_ok_at = @now, in_contingency = false,
              version = tax.ecf_gateway_state.version + 1
            """,
            ct,
            ("c", context.CompanyId),
            ("now", context.Clock.UtcNow)).ConfigureAwait(false);
        if (wasInContingency)
        {
            await MoveAllAsync(context, EcfStatuses.Contingency, null, commandType, ct).ConfigureAwait(false);
        }
    }

    /// <summary>A failure: counted; past ecf_contingency_minutes without an answer the company's e-CF in flight move to contingency. Returns whether it is in contingency.</summary>
    public static async Task<bool> FailedAsync(CommandContext context, IEnumerable<IEcfSourceUpdater> updaters, string commandType, CancellationToken ct)
    {
        var now = context.Clock.UtcNow;
        var state = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO tax.ecf_gateway_state (company_id, consecutive_failures, failing_since, last_ok_at, in_contingency, version) VALUES (@c, 1, @now, NULL, false, 1)
            ON CONFLICT (company_id) DO UPDATE SET consecutive_failures = tax.ecf_gateway_state.consecutive_failures + 1,
              failing_since = coalesce(tax.ecf_gateway_state.failing_since, @now), version = tax.ecf_gateway_state.version + 1
            RETURNING failing_since, in_contingency
            """,
            r => (Since: r.Utc(0), InContingency: r.GetBoolean(1)),
            ct,
            ("c", context.CompanyId),
            ("now", now)).ConfigureAwait(false)).Single();
        if (state.InContingency)
        {
            return true;
        }

        var minutes = await ContingencyMinutesAsync(context, ct).ConfigureAwait(false);
        if (minutes is null || now - state.Since < TimeSpan.FromMinutes(minutes.Value))
        {
            return false;
        }

        await Sql.ExecuteAsync(
            context.Connection, context.Transaction, "UPDATE tax.ecf_gateway_state SET in_contingency = true, version = version + 1 WHERE company_id = @c", ct, ("c", context.CompanyId))
            .ConfigureAwait(false);
        await MoveAllAsync(context, null, EcfStatuses.Contingency, commandType, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Into contingency (<paramref name="from"/> null: every e-CF in flight) or back out of it (<paramref name="to"/> null: SUBMITTED when Alanube
    /// holds it, else PENDING), each with its event and history row; the e-CF being stepped is locked already and moves too.
    /// </summary>
    private static async Task MoveAllAsync(CommandContext context, string? from, string? to, string commandType, CancellationToken ct)
    {
        var rows = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            from is null
                ? "SELECT document_id, status, provider_id, version FROM tax.ecf_document WHERE company_id = @c AND status IN ('PENDING', 'SUBMITTED', 'UNKNOWN_OUTCOME') ORDER BY document_id FOR UPDATE"
                : "SELECT document_id, status, provider_id, version FROM tax.ecf_document WHERE company_id = @c AND status = 'CONTINGENCY' ORDER BY document_id FOR UPDATE",
            r => (Id: r.GetGuid(0), Status: r.GetString(1), ProviderId: r.NullableString(2), Version: r.GetInt64(3)),
            ct,
            ("c", context.CompanyId)).ConfigureAwait(false);
        var now = context.Clock.UtcNow;
        foreach (var d in rows)
        {
            var target = to ?? (d.ProviderId is null ? EcfStatuses.Pending : EcfStatuses.Submitted);
            var eventId = await context.AppendEventAsync(
                new EventDraft("EcfDocumentStatusChanged", 1, EcfQueue.Aggregate, d.Id, d.Version + 1, JsonSerializer.Serialize(new { documentId = d.Id, from = d.Status, to = target }),
                    Publish: true),
                ct).ConfigureAwait(false);
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                "UPDATE tax.ecf_document SET status = @s, next_poll_at = @n, version = version + 1 WHERE document_id = @d",
                ct,
                ("s", target),
                ("n", to is null ? now : now + EcfSchedule.Settled),
                ("d", d.Id)).ConfigureAwait(false);
            await context.AppendStateAsync(EcfQueue.Aggregate, d.Id, "DOCUMENT", d.Status, target, commandType, eventId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>REVENUE_ACCOUNTING ecf_contingency_minutes in force; null when the policy does not set it (contingency is then never automatic).</summary>
    private static async Task<int?> ContingencyMinutesAsync(CommandContext context, CancellationToken ct)
    {
        var value = (await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT p.value #>> '{}' FROM acc.accounting_policy_version v JOIN acc.accounting_policy_parameter p ON p.policy_version_id = v.policy_version_id
            WHERE v.company_id = @c AND v.policy_code = 'REVENUE_ACCOUNTING' AND v.status = 'ACTIVE' AND p.param_code = 'ecf_contingency_minutes'
              AND v.effective_from <= @d AND (v.effective_to IS NULL OR v.effective_to > @d)
            """,
            r => r.GetString(0),
            ct,
            ("c", context.CompanyId),
            ("d", Rochell.Platform.Time.BusinessCalendar.DefaultBusinessDate(context.Clock.UtcNow))).ConfigureAwait(false)).SingleOrDefault();
        return int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minutes) ? minutes : null;
    }
}

[RequiresPermission("ecf:process")]
public sealed class NudgeEcfDocumentsHandler(IEcfProvider provider) : ICommandHandler<NudgeEcfDocuments>
{
    public string CommandType => "Tax.NudgeEcfDocuments";

    public async Task<string> HandleAsync(NudgeEcfDocuments command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var now = context.Clock.UtcNow;
        var named = string.IsNullOrWhiteSpace(command.ProviderId) ? null : command.ProviderId.Trim();
        var moved = await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            UPDATE tax.ecf_document SET next_poll_at = @now, version = version + 1
            WHERE company_id = @c AND status IN ('SUBMITTED', 'UNKNOWN_OUTCOME', 'CONTINGENCY')
              AND (CAST(@pid AS text) IS NULL OR provider_id = CAST(@pid AS text)
                   OR NOT EXISTS (SELECT 1 FROM tax.ecf_document x WHERE x.company_id = @c AND x.provider_id = CAST(@pid AS text)))
            """,
            cancellationToken,
            ("now", now),
            ("c", context.CompanyId),
            ("pid", (object?)named ?? DBNull.Value)).ConfigureAwait(false);
        await Health.CallAsync(context, provider.Mode, null, "WEBHOOK", null, "OK", null, named, Stopwatch.StartNew(), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { nudged = moved });
    }
}

[RequiresPermission("ecf:process")]
public sealed class ListDueEcfDocumentsHandler : IQueryHandler<ListDueEcfDocuments>
{
    public string QueryType => "Tax.ListDueEcfDocuments";

    public async Task<string> HandleAsync(ListDueEcfDocuments query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        QueryErrors.EnsurePaging(query.Limit, 0);
        var items = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT d.document_id, d.version FROM tax.ecf_document d
            WHERE d.company_id = @c AND (
              (d.status IN ('PENDING', 'SUBMITTED', 'UNKNOWN_OUTCOME', 'CONTINGENCY') AND d.next_poll_at <= @now)
              OR (d.status IN ('ACCEPTED', 'ACCEPTED_CONDITIONAL') AND d.finished_at > @now - interval '7 days'
                  AND (SELECT count(*) FROM tax.ecf_file f WHERE f.document_id = d.document_id) < 2))
            ORDER BY d.next_poll_at NULLS LAST, d.created_at
            LIMIT @limit
            """,
            r => new DueEcfDocument(r.GetGuid(0), r.GetInt64(1)),
            cancellationToken,
            ("c", context.CompanyId),
            ("now", context.Clock.UtcNow),
            ("limit", query.Limit)).ConfigureAwait(false);
        return ApiJson.Serialize(new DueEcfDocumentList(items));
    }
}
