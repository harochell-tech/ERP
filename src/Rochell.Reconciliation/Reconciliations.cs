using System.Globalization;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Reconciliation;

/// <summary>One finding: what was compared, both sides, what kind of difference, how serious, which component it blocks.</summary>
public sealed record ReconFinding(string MatchKey, decimal? ValueA, decimal? ValueB, string Classification, string Severity, string? Component);

/// <summary>The outcome of one reconciliation run, as stored in rec.recon_run and rec.recon_exception.</summary>
public sealed record ReconRun(Guid RunId, string Code, string Status, decimal? TotalA, decimal? TotalB, IReadOnlyList<ReconFinding> Findings)
{
    public bool Blocks(string component)
        => Findings.Any(f => f.Severity == "ERROR" && (f.Component is null || f.Component == component));
}

/// <summary>
/// The eight reconciliations of VS#1 (Frozen Baseline §9.4, E-PR16-1…4, E-PR16-9). They evaluate the whole ledger as of the run
/// (they are global invariants), with zero tolerance: amounts are exact by construction (P-1), so any difference is a finding.
/// Written in SQL over the tables (the module graph lets Reconciliation depend on Platform only).
/// </summary>
public static class Reconciliations
{
    public static IReadOnlyList<string> All { get; } =
        ["AP-GL", "INV-VALUE-GL", "INV-QTY-BALANCE", "INV-VALUE-BALANCE", "VAL-RESIDUAL", "ACC-EVIDENCE", "VALUE-GL-LINK", "GRNI-AGING"];

    private const string Findings = "SELECT match_key, value_a, value_b, classification, severity, component FROM (";

    private static readonly Dictionary<string, (string Findings, string? Totals)> Definitions = new(StringComparer.Ordinal)
    {
        ["AP-GL"] = (
            Findings + """
            WITH ap AS (SELECT party_id, sum(open_amount) AS a FROM fin.ap_document WHERE company_id = @c GROUP BY party_id),
                 gl AS (SELECT party_id, sum(credit - debit) AS b FROM fin.gl_entry WHERE company_id = @c AND account_role = 'AP_CONTROL' GROUP BY party_id)
            SELECT coalesce(coalesce(ap.party_id, gl.party_id)::text, '(sin proveedor)') AS match_key, coalesce(a, 0) AS value_a, coalesce(b, 0) AS value_b,
                   'AP_GL_DIFFERENCE' AS classification, 'ERROR' AS severity, 'AP-REC' AS component
            FROM ap FULL JOIN gl ON gl.party_id = ap.party_id WHERE coalesce(a, 0) <> coalesce(b, 0)) f
            """,
            """
            SELECT (SELECT coalesce(sum(open_amount), 0) FROM fin.ap_document WHERE company_id = @c),
                   (SELECT coalesce(sum(credit - debit), 0) FROM fin.gl_entry WHERE company_id = @c AND account_role = 'AP_CONTROL')
            """),
        ["INV-VALUE-GL"] = (
            Findings + """
            WITH v AS (SELECT valuation_area_id AS area, item_id, value AS a FROM inv.inv_valuation_balance WHERE company_id = @c),
                 g AS (SELECT p.valuation_area_id AS area, e.item_id, sum(e.debit - e.credit) AS b
                       FROM fin.gl_entry e JOIN md.plant p ON p.plant_id = e.plant_id
                       WHERE e.company_id = @c AND e.account_role = 'RAW_MATERIAL' GROUP BY 1, 2)
            SELECT coalesce(v.area, g.area)::text || '/' || coalesce(v.item_id, g.item_id)::text AS match_key, coalesce(a, 0) AS value_a,
                   coalesce(b, 0) AS value_b, 'VALUE_GL_DIFFERENCE' AS classification, 'ERROR' AS severity, 'INV-MOV' AS component
            FROM v FULL JOIN g ON g.area = v.area AND g.item_id = v.item_id WHERE coalesce(a, 0) <> coalesce(b, 0)) f
            """,
            """
            SELECT (SELECT coalesce(sum(value), 0) FROM inv.inv_valuation_balance WHERE company_id = @c),
                   (SELECT coalesce(sum(debit - credit), 0) FROM fin.gl_entry WHERE company_id = @c AND account_role = 'RAW_MATERIAL')
            """),
        ["INV-QTY-BALANCE"] = (
            Findings + """
            WITH s AS (SELECT location_id, item_id, lot_id, quantity AS a FROM inv.inv_stock_balance WHERE company_id = @c),
                 q AS (SELECT location_id, item_id, lot_id, sum(quantity) AS b FROM inv.inv_quantity_entry WHERE company_id = @c GROUP BY 1, 2, 3)
            SELECT 'stock:' || coalesce(s.location_id, q.location_id)::text || '/' || coalesce(s.item_id, q.item_id)::text || '/'
                     || coalesce(s.lot_id, q.lot_id)::text AS match_key, coalesce(a, 0) AS value_a, coalesce(b, 0) AS value_b,
                   'STOCK_LEDGER_DIFFERENCE' AS classification, 'ERROR' AS severity, 'INV-MOV' AS component
            FROM s FULL JOIN q ON q.location_id = s.location_id AND q.item_id = s.item_id AND q.lot_id = s.lot_id
            WHERE coalesce(a, 0) <> coalesce(b, 0)
            UNION ALL
            SELECT 'area:' || coalesce(v.area, t.area)::text || '/' || coalesce(v.item_id, t.item_id)::text, coalesce(v.a, 0), coalesce(t.b, 0),
                   'VALUATION_QUANTITY_DIFFERENCE', 'ERROR', 'INV-MOV'
            FROM (SELECT valuation_area_id AS area, item_id, quantity AS a FROM inv.inv_valuation_balance WHERE company_id = @c) v
            FULL JOIN (SELECT p.valuation_area_id AS area, s.item_id, sum(s.quantity) AS b
                       FROM inv.inv_stock_balance s JOIN md.plant p ON p.plant_id = s.plant_id WHERE s.company_id = @c GROUP BY 1, 2) t
              ON t.area = v.area AND t.item_id = v.item_id
            WHERE coalesce(v.a, 0) <> coalesce(t.b, 0)) f
            """,
            """
            SELECT (SELECT coalesce(sum(quantity), 0) FROM inv.inv_stock_balance WHERE company_id = @c),
                   (SELECT coalesce(sum(quantity), 0) FROM inv.inv_quantity_entry WHERE company_id = @c)
            """),
        ["INV-VALUE-BALANCE"] = (
            Findings + """
            WITH v AS (SELECT valuation_area_id AS area, item_id, value AS a FROM inv.inv_valuation_balance WHERE company_id = @c),
                 e AS (SELECT valuation_area_id AS area, item_id, sum(amount) AS b FROM inv.inv_value_entry WHERE company_id = @c GROUP BY 1, 2)
            SELECT coalesce(v.area, e.area)::text || '/' || coalesce(v.item_id, e.item_id)::text AS match_key, coalesce(a, 0) AS value_a,
                   coalesce(b, 0) AS value_b, 'VALUATION_LEDGER_DIFFERENCE' AS classification, 'ERROR' AS severity, 'INV-MOV' AS component
            FROM v FULL JOIN e ON e.area = v.area AND e.item_id = v.item_id WHERE coalesce(a, 0) <> coalesce(b, 0)) f
            """,
            """
            SELECT (SELECT coalesce(sum(value), 0) FROM inv.inv_valuation_balance WHERE company_id = @c),
                   (SELECT coalesce(sum(amount), 0) FROM inv.inv_value_entry WHERE company_id = @c)
            """),
        ["VAL-RESIDUAL"] = (
            Findings + """
            SELECT valuation_area_id::text || '/' || item_id::text AS match_key, quantity AS value_a, value AS value_b,
                   CASE WHEN quantity = 0 THEN 'ORPHAN_VALUE' ELSE 'NON_POSITIVE_VALUE' END AS classification,
                   CASE WHEN quantity = 0 THEN 'ERROR' ELSE 'WARNING' END AS severity, 'INV-MOV' AS component
            FROM inv.inv_valuation_balance
            WHERE company_id = @c AND ((quantity = 0 AND value <> 0) OR (quantity > 0 AND value <= 0))) f
            """,
            null),
        ["ACC-EVIDENCE"] = (
            Findings + """
            WITH docs AS (
                   SELECT 'GR' AS kind, gr_id AS id, accounting_status::text AS status, posting_event_id AS event, 'INV-MOV' AS component
                   FROM pur.goods_receipt WHERE company_id = @c
                   UNION ALL SELECT 'GRR', grr_id, accounting_status::text, posting_event_id, 'INV-MOV' FROM pur.goods_receipt_reversal WHERE company_id = @c
                   UNION ALL SELECT 'RC', rc_id, accounting_status::text, posting_event_id, 'INV-MOV' FROM pur.receipt_correction WHERE company_id = @c
                   UNION ALL SELECT 'SI', si_id, accounting_status::text, posting_event_id, 'AP-REC' FROM pur.supplier_invoice WHERE company_id = @c),
                 evidence AS (
                   SELECT d.kind, d.id, d.status, d.component, count(j.journal_id) AS journals,
                          count(j.journal_id) FILTER (WHERE r.journal_id IS NULL) AS live,
                          count(j.journal_id) FILTER (WHERE r.journal_id IS NULL AND j.journal_type = 'AUTO') AS live_auto
                   FROM docs d
                   LEFT JOIN fin.gl_journal j ON j.company_id = @c AND j.source_event_id = d.event
                   LEFT JOIN fin.gl_journal r ON r.company_id = @c AND r.reverses_journal_id = j.journal_id
                   GROUP BY 1, 2, 3, 4)
            SELECT kind || ':' || id::text AS match_key, journals::numeric AS value_a, live::numeric AS value_b,
                   CASE WHEN status = 'POSTED' THEN 'POSTED_WITHOUT_JOURNAL'
                        WHEN status = 'REVERSED' AND journals = 0 THEN 'REVERSED_WITHOUT_JOURNAL'
                        WHEN status = 'REVERSED' THEN 'REVERSED_WITH_LIVE_JOURNAL'
                        ELSE 'NOT_POSTED_WITH_JOURNAL' END AS classification,
                   'ERROR' AS severity, component
            FROM evidence
            WHERE (status = 'POSTED' AND live = 0) OR (status = 'REVERSED' AND (journals = 0 OR live_auto > 0))
               OR (status IN ('NOT_POSTED', 'POSTING_BLOCKED') AND live > 0)) f
            """,
            null),
        ["VALUE-GL-LINK"] = (
            Findings + """
            SELECT v.value_entry_id::text AS match_key, v.amount AS value_a, coalesce(sum(e.debit - e.credit), 0) AS value_b,
                   CASE WHEN count(e.gl_entry_id) = 0 THEN 'VALUE_WITHOUT_GL' WHEN count(e.gl_entry_id) > 1 THEN 'VALUE_WITH_SEVERAL_GL'
                        ELSE 'VALUE_GL_AMOUNT' END AS classification, 'ERROR' AS severity, 'INV-MOV' AS component
            FROM inv.inv_value_entry v LEFT JOIN fin.gl_entry e ON e.company_id = @c AND e.inv_value_entry_id = v.value_entry_id
            WHERE v.company_id = @c
            GROUP BY v.value_entry_id, v.amount
            HAVING count(e.gl_entry_id) <> 1 OR coalesce(sum(e.debit - e.credit), 0) <> v.amount
            UNION ALL
            SELECT gl_entry_id::text, NULL, debit - credit, 'INVENTORY_GL_WITHOUT_VALUE', 'ERROR', 'INV-MOV'
            FROM fin.gl_entry WHERE company_id = @c AND account_role = 'RAW_MATERIAL' AND inv_value_entry_id IS NULL) f
            """,
            """
            SELECT (SELECT coalesce(sum(amount), 0) FROM inv.inv_value_entry WHERE company_id = @c),
                   (SELECT coalesce(sum(debit - credit), 0) FROM fin.gl_entry WHERE company_id = @c AND inv_value_entry_id IS NOT NULL)
            """),
        ["GRNI-AGING"] = (
            Findings + """
            SELECT l.po_line_id::text AS match_key, l.qty_received - l.qty_invoiced AS value_a,
                   (@asOf::date - min(g.occurred_at)::date)::numeric AS value_b, 'GRNI_AGED' AS classification, 'WARNING' AS severity,
                   NULL::text AS component
            FROM pur.purchase_order_line l
            JOIN pur.goods_receipt_line gl ON gl.po_line_id = l.po_line_id
            JOIN pur.goods_receipt g ON g.gr_id = gl.gr_id AND g.document_status::text <> 'REVERSED'
            WHERE g.company_id = @c
            GROUP BY l.po_line_id, l.qty_received, l.qty_invoiced
            HAVING l.qty_received > l.qty_invoiced AND min(g.occurred_at) < @asOf - make_interval(days => @days)) f
            """,
            null),
    };

    /// <summary>Runs the given reconciliations now and stores each run with its findings.</summary>
    public static async Task<IReadOnlyList<ReconRun>> RunAsync(CommandContext context, IEnumerable<string> codes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(codes);
        var asOf = context.Clock.UtcNow;
        var runs = new List<ReconRun>();
        foreach (var code in codes)
        {
            if (!Definitions.TryGetValue(code, out var definition))
            {
                throw new DomainException(ReconciliationErrors.UnknownReconciliation, $"Unknown reconciliation {code}.");
            }

            var runId = context.Ids.NewId();
            int? agingDays = null;
            if (code == "GRNI-AGING")
            {
                agingDays = await AgingDaysAsync(context, asOf, cancellationToken).ConfigureAwait(false);
                if (agingDays is null)
                {
                    runs.Add(await StoreAsync(context, runId, code, asOf, "FAILED", null, null, [], cancellationToken).ConfigureAwait(false));
                    continue;
                }
            }

            var findings = new List<ReconFinding>();
            await using (var command = Sql.Command(context.Connection, context.Transaction, definition.Findings, ("c", context.CompanyId), ("asOf", asOf), ("days", agingDays ?? 0)))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    findings.Add(new ReconFinding(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetDecimal(1),
                        reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5)));
                }
            }

            decimal? totalA = null;
            decimal? totalB = null;
            if (definition.Totals is not null)
            {
                await using var command = Sql.Command(context.Connection, context.Transaction, definition.Totals, ("c", context.CompanyId));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                (totalA, totalB) = (reader.GetDecimal(0), reader.GetDecimal(1));
            }

            runs.Add(await StoreAsync(context, runId, code, asOf, findings.Count == 0 ? "MATCHED" : "EXCEPTIONS", totalA, totalB, findings, cancellationToken).ConfigureAwait(false));
        }

        return runs;
    }

    private static async Task<ReconRun> StoreAsync(
        CommandContext context, Guid runId, string code, DateTime asOf, string status, decimal? totalA, decimal? totalB, List<ReconFinding> findings, CancellationToken cancellationToken)
    {
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO rec.recon_run (run_id, company_id, recon_code, as_of, total_a, total_b, difference, status, command_id)
            VALUES (@r, @c, @code, @asOf, @a, @b, @d, @s, (SELECT command_id FROM core.command_log WHERE result_ref = @ref))
            """,
            cancellationToken,
            ("r", runId),
            ("c", context.CompanyId),
            ("code", code),
            ("asOf", asOf),
            ("a", totalA),
            ("b", totalB),
            ("d", totalA is null || totalB is null ? null : totalA - totalB),
            ("s", status),
            ("ref", context.ResultRef)).ConfigureAwait(false);
        foreach (var f in findings)
        {
            await Sql.ExecuteAsync(
                context.Connection,
                context.Transaction,
                """
                INSERT INTO rec.recon_exception (exception_id, company_id, run_id, match_key, value_a, value_b, classification, severity, component, status)
                VALUES (@id, @c, @r, @k, round(@a, 4), round(@b, 4), @cl, @sv, @cp, 'OPEN')
                """,
                cancellationToken,
                ("id", context.Ids.NewId()),
                ("c", context.CompanyId),
                ("r", runId),
                ("k", f.MatchKey),
                ("a", f.ValueA),
                ("b", f.ValueB),
                ("cl", f.Classification),
                ("sv", f.Severity),
                ("cp", f.Component)).ConfigureAwait(false);
        }

        return new ReconRun(runId, code, status, totalA, totalB, findings);
    }

    /// <summary>grni_aging_alert_days of the INVENTORY policy in force (read directly: Reconciliation depends on Platform only).</summary>
    private static async Task<int?> AgingDaysAsync(CommandContext context, DateTime asOf, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT p.value #>> '{}' FROM acc.accounting_policy_version v
            JOIN acc.accounting_policy_parameter p ON p.policy_version_id = v.policy_version_id
            WHERE v.company_id = @c AND v.policy_code = 'INVENTORY' AND v.status = 'ACTIVE' AND p.param_code = 'grni_aging_alert_days'
              AND v.effective_from <= @d AND (v.effective_to IS NULL OR v.effective_to > @d)
            """,
            ("c", context.CompanyId),
            ("d", Rochell.Platform.Time.BusinessCalendar.DefaultBusinessDate(asOf)));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string days
            ? int.Parse(days, NumberStyles.None, CultureInfo.InvariantCulture)
            : null;
    }
}
