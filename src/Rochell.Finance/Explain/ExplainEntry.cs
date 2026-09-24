using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rochell.Finance.Posting;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Queries;

namespace Rochell.Finance.Explain;

/// <summary>EX-01: why a GL line exists — event, document, rule and version, mapping, policy, fiscal determination, text.</summary>
public sealed record ExplainEntry(Guid CompanyId, Guid SessionId, Guid GlEntryId) : IQuery;

/// <summary>
/// E-PR17-1…5. Everything comes from the journal and what it references (E-11: never from document screens). The text is the rule
/// line's template with placeholders filled from the source document and the line's determination inputs; reversals, reposts,
/// rounding and late entries get the generic wording below. A placeholder without a value stays visible and complete = false.
/// </summary>
[RequiresPermission("audit:read")]
public sealed partial class ExplainEntryHandler : IQueryHandler<ExplainEntry>
{
    public string QueryType => "Finance.ExplainEntry";

    public async Task<string> HandleAsync(ExplainEntry query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var entry = await ReadAsync(context, query.GlEntryId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(FinanceErrors.JournalNotFound, $"GL entry {query.GlEntryId} does not exist.");
        var document = await DocumentAsync(context, entry.EventId, entry.EventType, entry.AggregateType, entry.AggregateId, entry.Payload, cancellationToken).ConfigureAwait(false);
        var (text, complete) = await RenderAsync(context, entry, document, cancellationToken).ConfigureAwait(false);

        using var inputs = JsonDocument.Parse(entry.DeterminationInputs);
        var mapId = inputs.RootElement.TryGetProperty("account_role_map_id", out var m) && m.ValueKind == JsonValueKind.String ? Guid.Parse(m.GetString()!) : (Guid?)null;
        var policyIds = PolicyVersionIds(inputs.RootElement).ToList();

        return JsonSerializer.Serialize(new
        {
            entry = new
            {
                glEntryId = entry.GlEntryId,
                lineNo = entry.LineNo,
                ruleLineCode = entry.RuleLineCode,
                account = new { code = entry.AccountCode, name = entry.AccountName, role = entry.AccountRole },
                debit = entry.Debit,
                credit = entry.Credit,
                plantId = entry.PlantId,
                itemId = entry.ItemId,
                partyId = entry.PartyId,
                subledger = entry.SubledgerType is null ? null : new { type = entry.SubledgerType, reference = entry.SubledgerRef },
            },
            journal = new
            {
                journalId = entry.JournalId,
                type = entry.JournalType,
                generation = entry.Generation,
                postingDate = Date(entry.PostingDate),
                lateEntry = entry.LateEntry,
                reversesJournalId = entry.ReversesJournalId,
                reversedByJournalId = entry.ReversedBy,
            },
            @event = new
            {
                eventId = entry.EventId,
                type = entry.EventType,
                occurredAt = entry.OccurredAt,
                recordedAt = entry.RecordedAt,
                businessDate = Date(entry.BusinessDate),
                command = entry.CommandType,
                user = entry.UserEmail,
                payload = JsonDocument.Parse(entry.Payload).RootElement,
            },
            document = document is null ? null : new { kind = document.Kind, id = document.Id, number = document.Number, purchaseOrder = document.PurchaseOrderNumber },
            rule = new { code = entry.RuleCode, version = entry.RuleVersion, closeComponent = entry.CloseComponent },
            mapping = mapId is null ? null : await MappingAsync(context, mapId.Value, cancellationToken).ConfigureAwait(false),
            policies = await PoliciesAsync(context, policyIds, cancellationToken).ConfigureAwait(false),
            fiscal = document?.Kind == "SUPPLIER_INVOICE" ? await FiscalAsync(context, document.Id, cancellationToken).ConfigureAwait(false) : null,
            determinationInputs = inputs.RootElement,
            integrity = new { status = entry.IntegrityStatus, ledgerSequence = entry.LedgerSequence },
            explanation = text,
            complete,
        });
    }

    private async Task<(string Text, bool Complete)> RenderAsync(QueryContext context, EntryRow entry, DocumentRef? document, CancellationToken cancellationToken)
    {
        using var inputs = JsonDocument.Parse(entry.DeterminationInputs);
        var root = inputs.RootElement;
        string text;
        var complete = true;

        if (entry.JournalType == "REVERSAL" && root.TryGetProperty("reverses_entry_id", out var reversed) && Guid.TryParse(reversed.GetString(), out var originalId)
            && await ReadAsync(context, originalId, cancellationToken).ConfigureAwait(false) is { } original)
        {
            var originalDocument = await DocumentAsync(context, original.EventId, original.EventType, original.AggregateType, original.AggregateId, original.Payload, cancellationToken).ConfigureAwait(false);
            var (originalText, originalComplete) = await RenderAsync(context, original, originalDocument, cancellationToken).ConfigureAwait(false);
            var why = entry.EventType == "JournalReposted" ? " (repost por corrección de mapeo)" : string.Empty;
            text = string.Create(CultureInfo.InvariantCulture, $"Reversa exacta{why} de la línea {original.LineNo} del asiento {original.JournalId}: {originalText}");
            complete = originalComplete;
        }
        else if (entry.RuleLineCode == PostingEngine.RoundingLineCode)
        {
            text = "Diferencia de redondeo del asiento, dentro de la tolerancia de la política contable POSTING.";
        }
        else
        {
            using var templates = JsonDocument.Parse(entry.Templates);
            if (templates.RootElement.TryGetProperty(entry.RuleLineCode, out var template) && template.ValueKind == JsonValueKind.String)
            {
                (text, complete) = Fill(template.GetString()!, Values(document, root));
            }
            else
            {
                (text, complete) = ($"(La regla {entry.RuleCode} v{entry.RuleVersion} no tiene plantilla para la línea {entry.RuleLineCode}.)", false);
            }

            if (entry.Generation > 1)
            {
                text = string.Create(CultureInfo.InvariantCulture, $"Generación {entry.Generation} (repost por corrección de mapeo): {text}");
            }
        }

        if (entry.LateEntry)
        {
            text += string.Create(
                CultureInfo.InvariantCulture,
                $" Registro tardío: el hecho es del {Date(entry.BusinessDate)} y se contabilizó el {Date(entry.PostingDate)} porque ese período estaba cerrado.");
        }

        return (text, complete);
    }

    private static Dictionary<string, string> Values(DocumentRef? document, JsonElement determinationInputs)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (determinationInputs.TryGetProperty("inputs", out var lineInputs) && lineInputs.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in lineInputs.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String))
            {
                values[p.Name] = p.Value.GetString()!;
            }
        }

        if (document is not null)
        {
            if (document.PurchaseOrderNumber is not null)
            {
                values["po_no"] = document.PurchaseOrderNumber;
            }

            switch (document.Kind)
            {
                case "GOODS_RECEIPT":
                    values["gr_id"] = document.Id.ToString();
                    values["gr_no"] = document.Number;
                    break;
                case "GOODS_RECEIPT_REVERSAL":
                    values["grr_id"] = document.Id.ToString();
                    values["gr_no"] = document.Number;
                    break;
                case "RECEIPT_CORRECTION":
                    values["rc_id"] = document.Id.ToString();
                    values["gr_no"] = document.Number;
                    break;
                case "SUPPLIER_INVOICE":
                    values["si_id"] = document.Id.ToString();
                    values["ncf"] = document.Number;
                    break;
            }
        }

        return values;
    }

    private static (string Text, bool Complete) Fill(string template, Dictionary<string, string> values)
    {
        var complete = true;
        var text = Placeholder().Replace(template, match =>
        {
            if (values.TryGetValue(match.Groups[1].Value, out var value))
            {
                return value;
            }

            complete = false;
            return match.Value;
        });
        return (text, complete);
    }

    [GeneratedRegex(@"\{([a-z_]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    private static IEnumerable<Guid> PolicyVersionIds(JsonElement root)
    {
        foreach (var scope in new[] { root, root.TryGetProperty("inputs", out var i) ? i : default })
        {
            if (scope.ValueKind == JsonValueKind.Object && scope.TryGetProperty("policy_version_id", out var v)
                && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var id))
            {
                yield return id;
            }
        }
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed record DocumentRef(string Kind, Guid Id, string Number, string? PurchaseOrderNumber);

    private sealed record EntryRow(
        Guid GlEntryId, int LineNo, string RuleLineCode, string AccountCode, string AccountName, string AccountRole, string Debit, string Credit,
        Guid? PlantId, Guid? ItemId, Guid? PartyId, string? SubledgerType, Guid? SubledgerRef, string DeterminationInputs,
        Guid JournalId, string JournalType, int Generation, DateOnly PostingDate, bool LateEntry, Guid? ReversesJournalId, Guid? ReversedBy,
        string RuleCode, int RuleVersion, string Templates, string CloseComponent,
        Guid EventId, string EventType, DateTime OccurredAt, DateTime RecordedAt, DateOnly BusinessDate, string AggregateType, Guid AggregateId, string Payload,
        string? CommandType, string? UserEmail, string? IntegrityStatus, long? LedgerSequence);

    private static async Task<EntryRow?> ReadAsync(QueryContext context, Guid glEntryId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT e.gl_entry_id, e.line_no, e.rule_line_code, a.code, a.name, e.account_role, e.debit::text, e.credit::text,
                   e.plant_id, e.item_id, e.party_id, e.subledger_type, e.subledger_ref, e.determination_inputs::text,
                   j.journal_id, j.journal_type, j.posting_generation, j.posting_date, j.late_entry, j.reverses_journal_id,
                   (SELECT r.journal_id FROM fin.gl_journal r WHERE r.company_id = e.company_id AND r.reverses_journal_id = j.journal_id),
                   pr.code, j.posting_rule_version, v.explanation_templates::text, v.close_component,
                   ev.event_id, ev.event_type, ev.occurred_at, ev.recorded_at, ev.business_date, ev.aggregate_type, ev.aggregate_id, ev.payload::text,
                   cl.command_type, u.email, i.integrity_status, i.ledger_sequence
            FROM fin.gl_entry e
            JOIN fin.gl_journal j ON j.journal_id = e.journal_id
            JOIN fin.account a ON a.account_id = e.account_id
            JOIN fin.posting_rule pr ON pr.posting_rule_id = j.posting_rule_id
            JOIN fin.posting_rule_version v ON v.posting_rule_id = j.posting_rule_id AND v.version = j.posting_rule_version
            JOIN core.domain_event ev ON ev.event_id = j.source_event_id
            LEFT JOIN core.command_log cl ON cl.command_id = ev.command_id
            LEFT JOIN iam.session s ON s.session_id = ev.session_id
            LEFT JOIN iam.user u ON u.user_id = s.user_id
            LEFT JOIN audit.integrity_state i ON i.company_id = e.company_id AND i.ledger = 'GL' AND i.group_ref = j.journal_id
            WHERE e.company_id = @c AND e.gl_entry_id = @id
            """,
            ("c", context.CompanyId),
            ("id", glEntryId));
        await using var r = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new EntryRow(
            r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7),
            G(r, 8), G(r, 9), G(r, 10), S(r, 11), G(r, 12), r.GetString(13),
            r.GetGuid(14), r.GetString(15), r.GetInt32(16), r.GetFieldValue<DateOnly>(17), r.GetBoolean(18), G(r, 19), G(r, 20),
            r.GetString(21), r.GetInt32(22), r.GetString(23), r.GetString(24),
            r.GetGuid(25), r.GetString(26), r.GetDateTime(27), r.GetDateTime(28), r.GetFieldValue<DateOnly>(29), r.GetString(30), r.GetGuid(31), r.GetString(32),
            S(r, 33), S(r, 34), S(r, 35), r.IsDBNull(36) ? null : r.GetInt64(36));
    }

    /// <summary>The document behind an event: by its posting event, a supplier invoice by its aggregate (reversal), a repost by the event it reposts.</summary>
    private static async Task<DocumentRef?> DocumentAsync(QueryContext context, Guid eventId, string eventType, string aggregateType, Guid aggregateId, string payload, CancellationToken cancellationToken)
    {
        if (eventType == "JournalReposted")
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("sourceEventId", out var source) && Guid.TryParse(source.GetString(), out var sourceEvent))
            {
                await using var ev = Sql.Command(
                    context.Connection,
                    context.Transaction,
                    "SELECT event_type, aggregate_type, aggregate_id, payload::text FROM core.domain_event WHERE company_id = @c AND event_id = @e",
                    ("c", context.CompanyId),
                    ("e", sourceEvent));
                await using var reader = await ev.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var (t, a, i, p) = (reader.GetString(0), reader.GetString(1), reader.GetGuid(2), reader.GetString(3));
                    await reader.DisposeAsync().ConfigureAwait(false);
                    return await DocumentAsync(context, sourceEvent, t, a, i, p, cancellationToken).ConfigureAwait(false);
                }
            }

            return null;
        }

        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT 'GOODS_RECEIPT', g.gr_id, g.gr_no, po.po_no FROM pur.goods_receipt g JOIN pur.purchase_order po ON po.po_id = g.po_id
            WHERE g.company_id = @c AND g.posting_event_id = @e
            UNION ALL
            SELECT 'GOODS_RECEIPT_REVERSAL', r.grr_id, g.gr_no, po.po_no FROM pur.goods_receipt_reversal r
            JOIN pur.goods_receipt g ON g.gr_id = r.reversed_gr_id JOIN pur.purchase_order po ON po.po_id = g.po_id
            WHERE r.company_id = @c AND r.posting_event_id = @e
            UNION ALL
            SELECT 'RECEIPT_CORRECTION', rc.rc_id, g.gr_no, po.po_no FROM pur.receipt_correction rc
            JOIN pur.goods_receipt g ON g.gr_id = rc.gr_id JOIN pur.purchase_order po ON po.po_id = g.po_id
            WHERE rc.company_id = @c AND rc.posting_event_id = @e
            UNION ALL
            SELECT 'SUPPLIER_INVOICE', si_id, supplier_fiscal_number, NULL FROM pur.supplier_invoice
            WHERE company_id = @c AND (posting_event_id = @e OR (@agg = 'SupplierInvoice' AND si_id = @aggId))
            LIMIT 1
            """,
            ("c", context.CompanyId),
            ("e", eventId),
            ("agg", aggregateType),
            ("aggId", aggregateId));
        await using var r = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await r.ReadAsync(cancellationToken).ConfigureAwait(false) ? new DocumentRef(r.GetString(0), r.GetGuid(1), r.GetString(2), S(r, 3)) : null;
    }

    private static async Task<object?> MappingAsync(QueryContext context, Guid mapId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT m.map_id, m.account_role, m.item_category, m.effective_from, m.effective_to, u.email
            FROM fin.account_role_map m LEFT JOIN iam.user u ON u.user_id = m.approved_by
            WHERE m.company_id = @c AND m.map_id = @m
            """,
            ("c", context.CompanyId),
            ("m", mapId));
        await using var r = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await r.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new
            {
                mapId = r.GetGuid(0),
                accountRole = r.GetString(1),
                itemCategory = S(r, 2),
                effectiveFrom = Date(r.GetFieldValue<DateOnly>(3)),
                effectiveTo = r.IsDBNull(4) ? null : Date(r.GetFieldValue<DateOnly>(4)),
                approvedBy = S(r, 5),
            }
            : null;
    }

    private static async Task<List<object>> PoliciesAsync(QueryContext context, List<Guid> ids, CancellationToken cancellationToken)
    {
        var policies = new List<object>();
        foreach (var id in ids.Distinct())
        {
            await using var command = Sql.Command(
                context.Connection,
                context.Transaction,
                "SELECT policy_code, version, effective_from FROM acc.accounting_policy_version WHERE company_id = @c AND policy_version_id = @p",
                ("c", context.CompanyId),
                ("p", id));
            await using var r = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                policies.Add(new { policyVersionId = id, policy = r.GetString(0), version = r.GetInt32(1), effectiveFrom = Date(r.GetFieldValue<DateOnly>(2)) });
            }
        }

        return policies;
    }

    private static async Task<object?> FiscalAsync(QueryContext context, Guid supplierInvoiceId, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT d.determination_id, d.determination_date, l.line_no, l.tax_code, l.base::text, l.rate::text, l.amount::text, l.effect
            FROM pur.supplier_invoice si
            JOIN tax.tax_determination d ON d.determination_id = si.tax_determination_id
            JOIN tax.tax_determination_line l ON l.determination_id = d.determination_id
            WHERE si.company_id = @c AND si.si_id = @s ORDER BY l.line_no
            """,
            ("c", context.CompanyId),
            ("s", supplierInvoiceId));
        await using var r = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        Guid? determination = null;
        DateOnly date = default;
        var lines = new List<object>();
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            determination = r.GetGuid(0);
            date = r.GetFieldValue<DateOnly>(1);
            lines.Add(new { lineNo = r.GetInt32(2), taxCode = r.GetString(3), @base = r.GetString(4), rate = r.GetString(5), amount = r.GetString(6), effect = r.GetString(7) });
        }

        return determination is null ? null : new { determinationId = determination, date = Date(date), lines };
    }

    private static Guid? G(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);

    private static string? S(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
