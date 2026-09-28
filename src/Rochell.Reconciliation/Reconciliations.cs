using System.Globalization;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Reconciliation;

/// <summary>One finding: what was compared, both sides, what kind of difference, how serious, which component it blocks.</summary>
public sealed record ReconFinding(string MatchKey, decimal? ValueA, decimal? ValueB, string Classification, string Severity, string? Component);

/// <summary>The outcome of one reconciliation run, as stored in rec.recon_run and rec.recon_exception.</summary>
public sealed record ReconRun(Guid RunId, string Code, string Status, decimal? TotalA, decimal? TotalB, IReadOnlyList<ReconFinding> Findings)
{
    /// <summary>BANK-GL only: each bank account's equation at the cutoff (for the BANK-REC close snapshot, E-VS2-06-7).</summary>
    public IReadOnlyList<BankAccountReconciliation> BankAccounts { get; init; } = [];

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
        ["AP-GL", "INV-VALUE-GL", "INV-QTY-BALANCE", "INV-VALUE-BALANCE", "VAL-RESIDUAL", "ACC-EVIDENCE", "VALUE-GL-LINK", "GRNI-AGING", "BANK-GL", "PAY-APPL",
         "MANUAL-EVIDENCE", "TB-BALANCED", "STRUCT-COVERAGE", "MIGRATION-CLEARING"];

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
                       WHERE e.company_id = @c AND e.account_role IN ('RAW_MATERIAL', 'FINISHED_GOODS', 'FINISHED_GOODS_IN_TRANSIT') GROUP BY 1, 2)
            SELECT coalesce(v.area, g.area)::text || '/' || coalesce(v.item_id, g.item_id)::text AS match_key, coalesce(a, 0) AS value_a,
                   coalesce(b, 0) AS value_b, 'VALUE_GL_DIFFERENCE' AS classification, 'ERROR' AS severity, 'INV-MOV' AS component
            FROM v FULL JOIN g ON g.area = v.area AND g.item_id = v.item_id WHERE coalesce(a, 0) <> coalesce(b, 0)) f
            """,
            """
            SELECT (SELECT coalesce(sum(value), 0) FROM inv.inv_valuation_balance WHERE company_id = @c),
                   (SELECT coalesce(sum(debit - credit), 0) FROM fin.gl_entry WHERE company_id = @c AND account_role IN ('RAW_MATERIAL', 'FINISHED_GOODS', 'FINISHED_GOODS_IN_TRANSIT'))
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
               OR (status IN ('NOT_POSTED', 'POSTING_BLOCKED') AND live > 0)
            UNION ALL
            -- E-VS2-06-6: a RELEASED or CLEARED payment has a live AUTO journal of its posting event, a REVERSED one none.
            SELECT 'PAY:' || p.payment_no, count(j.journal_id)::numeric, count(j.journal_id) FILTER (WHERE r.journal_id IS NULL)::numeric,
                   CASE WHEN p.status::text = 'REVERSED' THEN 'REVERSED_WITH_LIVE_JOURNAL' ELSE 'RELEASED_WITHOUT_JOURNAL' END, 'ERROR', 'BANK-REC'
            FROM fin.payment p
            LEFT JOIN fin.gl_journal j ON j.company_id = @c AND j.source_event_id = p.posting_event_id AND j.journal_type = 'AUTO'
            LEFT JOIN fin.gl_journal r ON r.company_id = @c AND r.reverses_journal_id = j.journal_id
            WHERE p.company_id = @c AND p.status::text IN ('RELEASED', 'CLEARED', 'REVERSED')
            GROUP BY p.payment_id, p.payment_no, p.status
            HAVING (p.status::text <> 'REVERSED' AND count(j.journal_id) FILTER (WHERE r.journal_id IS NULL) = 0)
                OR (p.status::text = 'REVERSED' AND count(j.journal_id) FILTER (WHERE r.journal_id IS NULL) > 0)
            UNION ALL
            -- A recognized bank charge has the live AUTO journal of its BankChargeRecognized event.
            SELECT 'CHG:' || l.line_id::text, count(j.journal_id)::numeric, count(j.journal_id) FILTER (WHERE r.journal_id IS NULL)::numeric,
                   'CHARGE_WITHOUT_JOURNAL', 'ERROR', 'BANK-REC'
            FROM fin.bank_statement_line l
            LEFT JOIN fin.gl_journal j ON j.company_id = @c AND j.source_event_id = l.charge_event_id AND j.journal_type = 'AUTO'
            LEFT JOIN fin.gl_journal r ON r.company_id = @c AND r.reverses_journal_id = j.journal_id
            WHERE l.company_id = @c AND l.status = 'CHARGE_RECOGNIZED'
            GROUP BY l.line_id
            HAVING count(j.journal_id) FILTER (WHERE r.journal_id IS NULL) = 0) f
            """,
            null),
        ["MANUAL-EVIDENCE"] = (
            Findings + """
            -- E-FIN1-01-3: every POSTED or REVERSED adjustment has its MANUAL_ADJUSTMENT journal with its lines' totals; a REVERSED or
            -- auto-reversing one has the reversal; nothing else posts without a rule.
            SELECT 'AJ:' || m.journal_no AS match_key,
                   (SELECT coalesce(sum(l.debit), 0) FROM fin.manual_journal_line l WHERE l.manual_journal_id = m.manual_journal_id
                      AND l.journal_version = (SELECT max(x.journal_version) FROM fin.manual_journal_line x WHERE x.manual_journal_id = m.manual_journal_id)) AS value_a,
                   (SELECT coalesce(sum(e.debit), 0) FROM fin.gl_entry e JOIN fin.gl_journal j ON j.journal_id = e.journal_id
                      WHERE j.source_event_id = m.posting_event_id AND j.journal_type = 'MANUAL_ADJUSTMENT') AS value_b,
                   'MANUAL_JOURNAL_EVIDENCE' AS classification, 'ERROR' AS severity, m.close_component AS component
            FROM fin.manual_journal m
            WHERE m.company_id = @c AND m.status IN ('POSTED', 'REVERSED')
              AND ((SELECT coalesce(sum(l.debit), 0) FROM fin.manual_journal_line l WHERE l.manual_journal_id = m.manual_journal_id
                      AND l.journal_version = (SELECT max(x.journal_version) FROM fin.manual_journal_line x WHERE x.manual_journal_id = m.manual_journal_id))
                   <> (SELECT coalesce(sum(e.debit), 0) FROM fin.gl_entry e JOIN fin.gl_journal j ON j.journal_id = e.journal_id
                      WHERE j.source_event_id = m.posting_event_id AND j.journal_type = 'MANUAL_ADJUSTMENT')
                OR ((m.status = 'REVERSED' OR m.auto_reverse) AND NOT EXISTS (
                      SELECT 1 FROM fin.gl_journal j JOIN fin.gl_journal r ON r.reverses_journal_id = j.journal_id
                      WHERE j.source_event_id = m.posting_event_id AND j.journal_type = 'MANUAL_ADJUSTMENT')))
            UNION ALL
            SELECT 'journal:' || j.journal_id::text, NULL, NULL, 'RULELESS_JOURNAL_WITHOUT_ADJUSTMENT', 'ERROR', NULL
            FROM fin.gl_journal j
            WHERE j.company_id = @c AND j.journal_type = 'MANUAL_ADJUSTMENT'
              AND NOT EXISTS (SELECT 1 FROM fin.manual_journal m WHERE m.posting_event_id = j.source_event_id)) f
            """,
            null),
        ["TB-BALANCED"] = (
            Findings + """
            -- The trial balance adds up: every journal and the whole ledger have equal debits and credits.
            SELECT 'journal:' || journal_id::text AS match_key, sum(debit) AS value_a, sum(credit) AS value_b,
                   'JOURNAL_UNBALANCED' AS classification, 'ERROR' AS severity, NULL::text AS component
            FROM fin.gl_entry WHERE company_id = @c GROUP BY journal_id HAVING sum(debit) <> sum(credit)) f
            """,
            """
            SELECT (SELECT coalesce(sum(debit), 0) FROM fin.gl_entry WHERE company_id = @c), (SELECT coalesce(sum(credit), 0) FROM fin.gl_entry WHERE company_id = @c)
            """),
        ["MIGRATION-CLEARING"] = (
            Findings + """
            -- E-VS3-02b-8: the migration counter-account ends at zero once every opening document is loaded; a warning until then.
            SELECT 'migration-clearing' AS match_key, coalesce(sum(debit - credit), 0) AS value_a, 0::numeric AS value_b,
                   'MIGRATION_CLEARING_NOT_ZERO' AS classification, 'WARNING' AS severity, NULL::text AS component
            FROM fin.gl_entry WHERE company_id = @c AND account_role = 'MIGRATION_CLEARING'
            HAVING coalesce(sum(debit - credit), 0) <> 0) f
            """,
            null),
        ["STRUCT-COVERAGE"] = (
            Findings + """
            -- E-FIN1-03-9, a warning that blocks no close: the statements can be produced and they balance.
            SELECT 'account:' || a.code AS match_key, 0::numeric AS value_a, 1::numeric AS value_b,
                   'ACCOUNT_WITHOUT_CLASS' AS classification, 'WARNING' AS severity, NULL::text AS component
            FROM fin.account a WHERE a.company_id = @c AND a.status = 'ACTIVE' AND a.account_class IS NULL
            UNION ALL
            SELECT 'report:' || r.report, 0, 1, 'STRUCTURE_MISSING', 'WARNING', NULL
            FROM (VALUES ('BALANCE_SHEET'), ('INCOME_STATEMENT')) AS r (report)
            WHERE NOT EXISTS (SELECT 1 FROM fin.report_structure_version v WHERE v.company_id = @c AND v.report = r.report AND v.status = 'ACTIVE')
            UNION ALL
            SELECT 'account:' || a.code, 0, 1, 'ACCOUNT_NOT_IN_STRUCTURE', 'WARNING', NULL
            FROM fin.account a
            JOIN fin.report_structure_version v ON v.company_id = a.company_id AND v.status = 'ACTIVE'
              AND v.report = CASE WHEN a.account_class IN ('ASSET', 'LIABILITY', 'EQUITY') THEN 'BALANCE_SHEET' ELSE 'INCOME_STATEMENT' END
            WHERE a.company_id = @c AND a.status = 'ACTIVE' AND a.account_class IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM fin.report_line_account x WHERE x.structure_version_id = v.structure_version_id AND x.account_id = a.account_id)
            UNION ALL
            -- Assets − liabilities − equity − results = Σ(debit − credit) of the classed accounts: 0 unless an unclassed account holds a balance.
            SELECT 'balance-sheet', coalesce(sum(e.debit - e.credit), 0), 0, 'BALANCE_SHEET_DIFFERENCE', 'WARNING', NULL
            FROM fin.gl_entry e JOIN fin.account a ON a.account_id = e.account_id
            WHERE e.company_id = @c AND a.account_class IS NOT NULL
            HAVING coalesce(sum(e.debit - e.credit), 0) <> 0) f
            """,
            null),
        ["PAY-APPL"] = (
            Findings + """
            -- E-VS2-06-5, global. Each released / cleared payment: Σ live applications = amount (REVERSED: 0).
            SELECT 'payment:' || p.payment_no AS match_key,
                   coalesce(sum(CASE WHEN a.reverses_application_id IS NULL THEN a.amount ELSE -a.amount END), 0) AS value_a,
                   CASE WHEN p.status::text = 'REVERSED' THEN 0 ELSE p.amount END AS value_b,
                   'PAYMENT_APPLICATION_DIFFERENCE' AS classification, 'ERROR' AS severity, NULL::text AS component
            FROM fin.payment p LEFT JOIN fin.ap_application a ON a.payment_id = p.payment_id
            WHERE p.company_id = @c AND p.status::text IN ('RELEASED', 'CLEARED', 'REVERSED')
            GROUP BY p.payment_id, p.payment_no, p.status, p.amount
            HAVING coalesce(sum(CASE WHEN a.reverses_application_id IS NULL THEN a.amount ELSE -a.amount END), 0)
                   <> CASE WHEN p.status::text = 'REVERSED' THEN 0 ELSE p.amount END
            UNION ALL
            -- Each AP document of a POSTED invoice: original − open = Σ live applications.
            SELECT 'ap_doc:' || d.ap_doc_id::text, d.original_amount - d.open_amount,
                   coalesce(sum(CASE WHEN a.reverses_application_id IS NULL THEN a.amount ELSE -a.amount END), 0),
                   'AP_DOCUMENT_APPLICATION_DIFFERENCE', 'ERROR', NULL
            FROM fin.ap_document d
            JOIN pur.supplier_invoice i ON i.si_id = d.source_doc_id AND i.accounting_status::text = 'POSTED'
            LEFT JOIN fin.ap_application a ON a.ap_doc_id = d.ap_doc_id
            WHERE d.company_id = @c
            GROUP BY d.ap_doc_id, d.original_amount, d.open_amount
            HAVING d.original_amount - d.open_amount <> coalesce(sum(CASE WHEN a.reverses_application_id IS NULL THEN a.amount ELSE -a.amount END), 0)
            UNION ALL
            -- Each application (and each reversal row) has exactly one R-09 AP line of its event, AP document and amount.
            SELECT 'application:' || a.application_id::text, a.amount, count(e.gl_entry_id)::numeric,
                   'APPLICATION_WITHOUT_R09_LINE', 'ERROR', NULL
            FROM fin.ap_application a
            LEFT JOIN fin.gl_entry e ON e.company_id = @c AND e.source_event_id = a.event_id AND e.rule_line_code = 'R09-DR-AP'
              AND e.subledger_ref = a.ap_doc_id
              AND (CASE WHEN a.reverses_application_id IS NULL THEN e.debit ELSE e.credit END) = a.amount
            WHERE a.company_id = @c
            GROUP BY a.application_id, a.amount
            HAVING count(e.gl_entry_id) <> 1) f
            """,
            """
            SELECT (SELECT coalesce(sum(CASE WHEN reverses_application_id IS NULL THEN amount ELSE -amount END), 0) FROM fin.ap_application WHERE company_id = @c),
                   (SELECT coalesce(sum(amount), 0) FROM fin.payment WHERE company_id = @c AND status::text IN ('RELEASED', 'CLEARED'))
            """),
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
            FROM fin.gl_entry WHERE company_id = @c AND account_role IN ('RAW_MATERIAL', 'FINISHED_GOODS', 'FINISHED_GOODS_IN_TRANSIT') AND inv_value_entry_id IS NULL) f
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

    /// <summary>Runs the given reconciliations now and stores each run with its findings and the cutoff date, if any (E-VS2-06-1).</summary>
    public static async Task<IReadOnlyList<ReconRun>> RunAsync(CommandContext context, IEnumerable<string> codes, DateOnly? cutoff, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(codes);
        var asOf = context.Clock.UtcNow;
        var runs = new List<ReconRun>();
        foreach (var code in codes)
        {
            if (code == "BANK-GL")
            {
                var bankRunId = context.Ids.NewId();
                var (bankFindings, bankAccounts) = await BankGl.RunAsync(context, cutoff ?? Rochell.Platform.Time.BusinessCalendar.DefaultBusinessDate(asOf), cancellationToken).ConfigureAwait(false);
                var gl = bankAccounts.Sum(a => a.GlBalance);
                var explained = bankAccounts.Sum(a => a.GlBalance - (a.Difference ?? a.GlBalance));
                var stored = await StoreAsync(
                    context, bankRunId, code, asOf, cutoff, bankFindings.Count == 0 ? "MATCHED" : "EXCEPTIONS", gl, explained, [.. bankFindings], cancellationToken).ConfigureAwait(false);
                runs.Add(stored with { BankAccounts = bankAccounts });
                continue;
            }

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
                    runs.Add(await StoreAsync(context, runId, code, asOf, cutoff, "FAILED", null, null, [], cancellationToken).ConfigureAwait(false));
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

            runs.Add(await StoreAsync(context, runId, code, asOf, cutoff, findings.Count == 0 ? "MATCHED" : "EXCEPTIONS", totalA, totalB, findings, cancellationToken).ConfigureAwait(false));
        }

        return runs;
    }

    private static async Task<ReconRun> StoreAsync(
        CommandContext context, Guid runId, string code, DateTime asOf, DateOnly? cutoff, string status, decimal? totalA, decimal? totalB, List<ReconFinding> findings, CancellationToken cancellationToken)
    {
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO rec.recon_run (run_id, company_id, recon_code, as_of, cutoff_date, total_a, total_b, difference, status, command_id)
            VALUES (@r, @c, @code, @asOf, @cutoff, @a, @b, @d, @s, (SELECT command_id FROM core.command_log WHERE result_ref = @ref))
            """,
            cancellationToken,
            ("r", runId),
            ("c", context.CompanyId),
            ("code", code),
            ("asOf", asOf),
            ("cutoff", cutoff),
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
