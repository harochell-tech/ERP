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
/// The reconciliations of VS#1 (Frozen Baseline §9.4, E-PR16-1…4, E-PR16-9) and of the later slices (VS#2, FIN-1, VS#3 §8,
/// E-VS3-08-1…8). Except BANK-GL and FISC-DOC, which use the cutoff date, they evaluate the whole ledger as of the run
/// (they are global invariants), with zero tolerance: amounts are exact by construction (P-1), so any difference is a finding.
/// Written in SQL over the tables (the module graph lets Reconciliation depend on Platform only).
/// </summary>
public static class Reconciliations
{
    public static IReadOnlyList<string> All { get; } =
        ["AP-GL", "INV-VALUE-GL", "INV-QTY-BALANCE", "INV-VALUE-BALANCE", "VAL-RESIDUAL", "ACC-EVIDENCE", "VALUE-GL-LINK", "GRNI-AGING", "BANK-GL", "PAY-APPL",
         "MANUAL-EVIDENCE", "TB-BALANCED", "STRUCT-COVERAGE", "MIGRATION-CLEARING", "AR-GL", "CONTRACT-ASSET", "RECEIPT-APPL", "FISC-DOC", "DELIVERY-OPEN",
         "WIP-GL", "WIP-OPEN", "SHIFT-OPEN", "USAGE-TOLERANCE", "CURING-OVERDUE", "PRODUCTION-CLOSE-ORDER",
         "AUTH-CONSUMPTION", "EXEMPT-WITHOUT-AUTH", "AUTH-EXPIRY", "TAX-606", "CONTROLS-WAIVED", "PROFORMA-ASIG", "CASH-SALE", "IMPORT-CLEARING", "FX-REVAL", "FA-GL"];

    private const string Findings = "SELECT match_key, value_a, value_b, classification, severity, component FROM (";

    private static readonly Dictionary<string, (string Findings, string? Totals)> Definitions = new(StringComparer.Ordinal)
    {
        ["AP-GL"] = (
            Findings + """
            WITH ap AS (SELECT party_id, sum(open_amount) AS a FROM fin.ap_document WHERE company_id = @c GROUP BY party_id),
                 gl AS (SELECT party_id, sum(credit - debit) AS b FROM fin.gl_entry WHERE company_id = @c AND account_role IN ('AP_CONTROL', 'AP_FOREIGN') GROUP BY party_id)
            SELECT coalesce(coalesce(ap.party_id, gl.party_id)::text, '(sin proveedor)') AS match_key, coalesce(a, 0) AS value_a, coalesce(b, 0) AS value_b,
                   'AP_GL_DIFFERENCE' AS classification, 'ERROR' AS severity, 'AP-REC' AS component
            FROM ap FULL JOIN gl ON gl.party_id = ap.party_id WHERE coalesce(a, 0) <> coalesce(b, 0)
            UNION ALL
            -- E-USD1-06-4: each USD payable's open USD against the USD of its AP_FOREIGN lines.
            SELECT 'ap_usd:' || d.ap_doc_id::text, d.open_amount_fc, coalesce(u.usd, 0), 'AP_USD_DIFFERENCE', 'ERROR', 'AP-REC'
            FROM fin.ap_document d
            LEFT JOIN (SELECT subledger_ref, sum(CASE WHEN currency = 'USD' THEN sign(credit - debit) * amount_fc ELSE 0 END) AS usd
                       FROM fin.gl_entry WHERE company_id = @c AND account_role = 'AP_FOREIGN' GROUP BY subledger_ref) u ON u.subledger_ref = d.ap_doc_id
            WHERE d.company_id = @c AND d.currency = 'USD' AND d.open_amount_fc <> coalesce(u.usd, 0)) f
            """,
            """
            SELECT (SELECT coalesce(sum(open_amount), 0) FROM fin.ap_document WHERE company_id = @c),
                   (SELECT coalesce(sum(credit - debit), 0) FROM fin.gl_entry WHERE company_id = @c AND account_role IN ('AP_CONTROL', 'AP_FOREIGN'))
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
                   UNION ALL SELECT 'SI', si_id, accounting_status::text, posting_event_id, 'AP-REC' FROM pur.supplier_invoice WHERE company_id = @c
                   -- E-VS3-08-5: the VS#3 documents. A receipt answers to BANK-REC and AR-REC; a bounce has its own P-24 journal.
                   UNION ALL SELECT 'FA', invoice_id, accounting_status, posting_event_id, 'AR-REC' FROM sal.invoice WHERE company_id = @c
                   UNION ALL SELECT 'NC', credit_note_id, accounting_status, posting_event_id, 'AR-REC' FROM sal.credit_note WHERE company_id = @c
                   UNION ALL SELECT 'REC', receipt_id, CASE WHEN status = 'REVERSED' THEN 'REVERSED' ELSE 'POSTED' END, posting_event_id, k.component
                     FROM fin.receipt CROSS JOIN (VALUES ('BANK-REC'), ('AR-REC')) AS k (component) WHERE company_id = @c
                   UNION ALL SELECT 'BNC', receipt_id, 'POSTED', closing_event_id, 'BANK-REC' FROM fin.receipt WHERE company_id = @c AND status = 'BOUNCED'
                   UNION ALL SELECT 'DEP', deposit_id, 'POSTED', posting_event_id, 'BANK-REC' FROM fin.receipt_deposit WHERE company_id = @c
                   -- E-FIS1b-01-9: a released customer refund has its P-36 journal.
                   UNION ALL SELECT 'DEV', refund_id, 'POSTED', posting_event_id, k.component
                     FROM fin.customer_refund CROSS JOIN (VALUES ('BANK-REC'), ('AR-REC')) AS k (component) WHERE company_id = @c AND status IN ('RELEASED', 'CLEARED')
                   UNION ALL SELECT 'RET', withholding_id, CASE WHEN status = 'REVERSED' THEN 'REVERSED' ELSE 'POSTED' END, posting_event_id, 'AR-REC'
                     FROM fin.customer_withholding WHERE company_id = @c),
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
            -- E-VS2-06-5, global. Each released / cleared payment: Σ live applications = amount (REVERSED: 0); a payment of USD payables
            -- compares its USD (E-USD1-05-4: its pesos also carry the exchange difference).
            SELECT 'payment:' || p.payment_no AS match_key,
                   coalesce(sum(CASE WHEN a.reverses_application_id IS NULL THEN coalesce(a.amount_fc, a.amount) ELSE -coalesce(a.amount_fc, a.amount) END), 0) AS value_a,
                   CASE WHEN p.status::text = 'REVERSED' THEN 0 ELSE coalesce(p.amount_fc, p.amount) END AS value_b,
                   'PAYMENT_APPLICATION_DIFFERENCE' AS classification, 'ERROR' AS severity, NULL::text AS component
            FROM fin.payment p LEFT JOIN fin.ap_application a ON a.payment_id = p.payment_id
            WHERE p.company_id = @c AND p.status::text IN ('RELEASED', 'CLEARED', 'REVERSED')
            GROUP BY p.payment_id, p.payment_no, p.status, p.amount, p.amount_fc
            HAVING coalesce(sum(CASE WHEN a.reverses_application_id IS NULL THEN coalesce(a.amount_fc, a.amount) ELSE -coalesce(a.amount_fc, a.amount) END), 0)
                   <> CASE WHEN p.status::text = 'REVERSED' THEN 0 ELSE coalesce(p.amount_fc, p.amount) END
            UNION ALL
            -- Each AP document of a POSTED invoice or DUA: original − open = Σ live applications.
            SELECT 'ap_doc:' || d.ap_doc_id::text, d.original_amount - d.open_amount,
                   coalesce(sum(CASE WHEN a.reverses_application_id IS NULL THEN a.amount ELSE -a.amount END), 0),
                   'AP_DOCUMENT_APPLICATION_DIFFERENCE', 'ERROR', NULL
            FROM fin.ap_document d
            JOIN fin.ap_source i ON i.ap_doc_id = d.ap_doc_id AND i.accounting_status = 'POSTED'
            LEFT JOIN fin.ap_application a ON a.ap_doc_id = d.ap_doc_id
            WHERE d.company_id = @c
            GROUP BY d.ap_doc_id, d.original_amount, d.open_amount
            HAVING d.original_amount - d.open_amount <> coalesce(sum(CASE WHEN a.reverses_application_id IS NULL THEN a.amount ELSE -a.amount END), 0)
            UNION ALL
            -- Each application (and each reversal row) has exactly one R-09 AP line of its event, AP document and amount.
            SELECT 'application:' || a.application_id::text, a.amount, count(e.gl_entry_id)::numeric,
                   'APPLICATION_WITHOUT_R09_LINE', 'ERROR', NULL
            FROM fin.ap_application a
            LEFT JOIN fin.gl_entry e ON e.company_id = @c AND e.source_event_id = a.event_id AND e.rule_line_code IN ('R09-DR-AP', 'P41-DR-AP')
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
        ["AR-GL"] = (
            Findings + """
            -- E-VS3-08-1: per customer, the open AR documents equal the customer's AR_CONTROL balance.
            WITH ar AS (SELECT party_id, sum(open_amount) AS a FROM fin.ar_document WHERE company_id = @c GROUP BY party_id),
                 gl AS (SELECT party_id, sum(debit - credit) AS b FROM fin.gl_entry WHERE company_id = @c AND account_role = 'AR_CONTROL' GROUP BY party_id)
            SELECT coalesce(coalesce(ar.party_id, gl.party_id)::text, '(sin cliente)') AS match_key, coalesce(a, 0) AS value_a, coalesce(b, 0) AS value_b,
                   'AR_GL_DIFFERENCE' AS classification, 'ERROR' AS severity, 'AR-REC' AS component
            FROM ar FULL JOIN gl ON gl.party_id = ar.party_id WHERE coalesce(a, 0) <> coalesce(b, 0)) f
            """,
            """
            SELECT (SELECT coalesce(sum(open_amount), 0) FROM fin.ar_document WHERE company_id = @c),
                   (SELECT coalesce(sum(debit - credit), 0) FROM fin.gl_entry WHERE company_id = @c AND account_role = 'AR_CONTROL')
            """),
        ["CONTRACT-ASSET"] = (
            Findings + """
            -- E-VS3-08-2: per delivery line with control transferred, (delivered − invoiced) × order price equals the line's CONTRACT_ASSET
            -- + UNBILLED_RECEIVABLE balance (subledger = the delivery line). E-VS3-08-3: aged when older than unbilled_aging_alert_days.
            -- E-PRS-01-3: with freight, (delivered − invoiced) × the freight price adds to the same balance.
            WITH ex AS (SELECT dl.delivery_line_id AS id,
                               CASE WHEN dl.qty_invoiced >= dl.qty_delivered THEN 0
                                    ELSE round((dl.qty_delivered - dl.qty_invoiced) * ol.unit_price, 2)
                                         + coalesce(round((dl.qty_delivered - dl.qty_invoiced) * ol.freight_unit_price, 2), 0) END AS a
                        FROM log.delivery_line dl JOIN sal.sales_order_line ol ON ol.line_id = dl.sales_order_line_id
                        WHERE dl.company_id = @c AND dl.qty_delivered > 0),
                 gl AS (SELECT subledger_ref AS id, sum(debit - credit) AS b, min(posting_date) AS since FROM fin.gl_entry
                        WHERE company_id = @c AND account_role IN ('CONTRACT_ASSET', 'UNBILLED_RECEIVABLE') GROUP BY subledger_ref)
            SELECT 'line:' || coalesce(ex.id, gl.id)::text AS match_key, coalesce(ex.a, 0) AS value_a, coalesce(gl.b, 0) AS value_b,
                   'CONTRACT_ASSET_DIFFERENCE' AS classification, 'ERROR' AS severity, 'AR-REC' AS component
            FROM ex FULL JOIN gl ON gl.id = ex.id WHERE coalesce(ex.a, 0) <> coalesce(gl.b, 0)
            UNION ALL
            SELECT 'aged:' || ex.id::text, ex.a, (@cutoff - gl.since)::numeric, 'UNBILLED_AGED', 'WARNING', NULL
            FROM ex JOIN gl ON gl.id = ex.id
            WHERE CAST(@udays AS integer) IS NOT NULL AND ex.a > 0 AND gl.since < @cutoff - CAST(@udays AS integer)) f
            """,
            """
            SELECT (SELECT coalesce(sum(CASE WHEN dl.qty_invoiced >= dl.qty_delivered THEN 0
                                             ELSE round((dl.qty_delivered - dl.qty_invoiced) * ol.unit_price, 2)
                                                  + coalesce(round((dl.qty_delivered - dl.qty_invoiced) * ol.freight_unit_price, 2), 0) END), 0)
                    FROM log.delivery_line dl JOIN sal.sales_order_line ol ON ol.line_id = dl.sales_order_line_id WHERE dl.company_id = @c),
                   (SELECT coalesce(sum(debit - credit), 0) FROM fin.gl_entry WHERE company_id = @c AND account_role IN ('CONTRACT_ASSET', 'UNBILLED_RECEIVABLE'))
            """),
        ["RECEIPT-APPL"] = (
            Findings + """
            -- E-VS3-08-4 (blocks AR-REC and BANK-REC, so its findings name no single component).
            WITH live AS (SELECT x.* FROM fin.ar_application x
                          WHERE x.company_id = @c AND x.reverses_application_id IS NULL
                            AND NOT EXISTS (SELECT 1 FROM fin.ar_application u WHERE u.reverses_application_id = x.application_id)),
                 per_receipt AS (SELECT r.receipt_no, r.status, r.amount, r.unapplied_amount,
                                        coalesce((SELECT sum(l.amount) FROM live l WHERE l.receipt_id = r.receipt_id), 0)
                                        + coalesce((SELECT sum(f.amount) FROM fin.customer_refund f WHERE f.receipt_id = r.receipt_id AND f.status IN ('RELEASED', 'CLEARED')), 0) AS applied
                                 FROM fin.receipt r WHERE r.company_id = @c)
            -- (a) a live receipt: applications + refunds paid (E-FIS1b-01-9) + unapplied = amount; a bounced or reversed one keeps neither.
            SELECT 'REC:' || receipt_no AS match_key, CASE WHEN status = 'RECORDED' THEN amount ELSE 0 END AS value_a,
                   applied + CASE WHEN status = 'RECORDED' THEN unapplied_amount ELSE 0 END AS value_b,
                   'RECEIPT_APPLICATION_DIFFERENCE' AS classification, 'ERROR' AS severity, NULL::text AS component
            FROM per_receipt
            WHERE (status = 'RECORDED' AND applied + unapplied_amount <> amount) OR (status <> 'RECORDED' AND applied <> 0)
            UNION ALL
            -- (b) an AR document: original − open = live applications + active withholdings + confirmed credit notes (or the void).
            SELECT 'AR:' || d.doc_no, d.original_amount - d.open_amount,
                   coalesce((SELECT sum(l.amount) FROM live l WHERE l.ar_doc_id = d.ar_doc_id), 0)
                   + coalesce((SELECT sum(w.amount) FROM fin.customer_withholding w WHERE w.ar_doc_id = d.ar_doc_id AND w.status = 'ACTIVE'), 0)
                   + coalesce((SELECT sum(n.total) FROM sal.credit_note n WHERE n.invoice_id = i.invoice_id AND n.commercial_status = 'CONFIRMED'), 0)
                   + CASE WHEN i.commercial_status = 'VOIDED' THEN d.original_amount ELSE 0 END,
                   'AR_DOCUMENT_SETTLEMENT_DIFFERENCE', 'ERROR', NULL
            FROM fin.ar_document d JOIN sal.invoice i ON i.ar_doc_id = d.ar_doc_id
            WHERE d.company_id = @c
              AND d.original_amount - d.open_amount
                  <> coalesce((SELECT sum(l.amount) FROM live l WHERE l.ar_doc_id = d.ar_doc_id), 0)
                   + coalesce((SELECT sum(w.amount) FROM fin.customer_withholding w WHERE w.ar_doc_id = d.ar_doc_id AND w.status = 'ACTIVE'), 0)
                   + coalesce((SELECT sum(n.total) FROM sal.credit_note n WHERE n.invoice_id = i.invoice_id AND n.commercial_status = 'CONFIRMED'), 0)
                   + CASE WHEN i.commercial_status = 'VOIDED' THEN d.original_amount ELSE 0 END
            UNION ALL
            -- (c) each application or unapply row has exactly one AR_CONTROL line of its event's P-25 (or its reversal), document and amount.
            SELECT 'APP:' || x.application_id::text, x.amount,
                   (SELECT count(*) FROM fin.gl_entry e JOIN fin.gl_journal j ON j.journal_id = e.journal_id
                    WHERE j.company_id = @c AND j.source_event_id = x.event_id AND e.account_role = 'AR_CONTROL' AND e.subledger_ref = x.ar_doc_id
                      AND e.debit + e.credit = x.amount)::numeric,
                   'APPLICATION_WITHOUT_P25_LINE', 'ERROR', NULL
            FROM fin.ar_application x
            WHERE x.company_id = @c
              AND (SELECT count(*) FROM fin.gl_entry e JOIN fin.gl_journal j ON j.journal_id = e.journal_id
                   WHERE j.company_id = @c AND j.source_event_id = x.event_id AND e.account_role = 'AR_CONTROL' AND e.subledger_ref = x.ar_doc_id
                     AND e.debit + e.credit = x.amount) <> 1
            UNION ALL
            -- (d) per receipt in the GL: UNAPPLIED_RECEIPTS = unapplied while RECORDED (else 0); CASH_IN_TRANSIT = amount while not deposited.
            SELECT 'GL:' || coalesce(r.receipt_no, g.id::text) || ':' || g.role,
                   CASE WHEN r.status IS DISTINCT FROM 'RECORDED' THEN 0
                        WHEN g.role = 'UNAPPLIED_RECEIPTS' THEN r.unapplied_amount
                        WHEN r.bank_status = 'IN_TRANSIT' THEN r.amount ELSE 0 END,
                   g.b, 'RECEIPT_GL_DIFFERENCE', 'ERROR', NULL
            FROM (SELECT subledger_ref AS id, account_role AS role,
                         sum(CASE WHEN account_role = 'UNAPPLIED_RECEIPTS' THEN credit - debit ELSE debit - credit END) AS b
                  FROM fin.gl_entry WHERE company_id = @c AND account_role IN ('UNAPPLIED_RECEIPTS', 'CASH_IN_TRANSIT') GROUP BY 1, 2
                  UNION ALL
                  SELECT r.receipt_id, k.role, 0 FROM fin.receipt r CROSS JOIN (VALUES ('UNAPPLIED_RECEIPTS'), ('CASH_IN_TRANSIT')) AS k (role)
                  WHERE r.company_id = @c AND NOT EXISTS (SELECT 1 FROM fin.gl_entry e WHERE e.subledger_ref = r.receipt_id AND e.account_role = k.role)) g
            LEFT JOIN fin.receipt r ON r.receipt_id = g.id
            WHERE g.b <> CASE WHEN r.status IS DISTINCT FROM 'RECORDED' THEN 0
                              WHEN g.role = 'UNAPPLIED_RECEIPTS' THEN r.unapplied_amount
                              WHEN r.bank_status = 'IN_TRANSIT' THEN r.amount ELSE 0 END) f
            """,
            """
            SELECT (SELECT coalesce(sum(x.amount), 0) FROM fin.ar_application x WHERE x.company_id = @c AND x.reverses_application_id IS NULL
                      AND NOT EXISTS (SELECT 1 FROM fin.ar_application u WHERE u.reverses_application_id = x.application_id))
                   + (SELECT coalesce(sum(unapplied_amount), 0) FROM fin.receipt WHERE company_id = @c AND status = 'RECORDED'),
                   (SELECT coalesce(sum(amount), 0) FROM fin.receipt WHERE company_id = @c AND status = 'RECORDED')
            """),
        ["FISC-DOC"] = (
            Findings + """
            -- E-VS3-08-6: issued invoices and credit notes dated on or before the cutoff whose e-CF is not final yet (manual or, E-VS4-03-1, gateway).
            SELECT 'FA:' || invoice_no AS match_key, total AS value_a, NULL::numeric AS value_b, 'FISCAL_DOCUMENT_PENDING' AS classification,
                   'ERROR' AS severity, 'AR-REC' AS component
            FROM sal.invoice WHERE company_id = @c AND commercial_status NOT IN ('DRAFT', 'VOIDED') AND fiscal_status IN ('PENDING_EXTERNAL', 'ECF_SENDING', 'ECF_REJECTED', 'ECF_ACTION') AND invoice_date <= @cutoff
            UNION ALL
            SELECT 'NC:' || credit_note_no, total, NULL, 'FISCAL_DOCUMENT_PENDING', 'ERROR', 'AR-REC'
            FROM sal.credit_note WHERE company_id = @c AND commercial_status = 'CONFIRMED' AND fiscal_status IN ('PENDING_EXTERNAL', 'ECF_SENDING', 'ECF_REJECTED', 'ECF_ACTION') AND credit_date <= @cutoff) f
            """,
            null),
        ["DELIVERY-OPEN"] = (
            Findings + """
            -- E-VS3-08-7: deliveries in transit longer than delivery_open_alert_hours (a warning).
            SELECT 'CD:' || delivery_no AS match_key, NULL::numeric AS value_a, round(extract(epoch FROM @asOf - gate_out_at) / 3600)::numeric AS value_b,
                   'DELIVERY_OPEN' AS classification, 'WARNING' AS severity, NULL::text AS component
            FROM log.delivery WHERE company_id = @c AND status = 'IN_TRANSIT' AND gate_out_at < @asOf - make_interval(hours => @hours)) f
            """,
            null),
        ["WIP-GL"] = (
            Findings + """
            -- E-MFG1-05-4: WIP by collector in the GL = consumption of posted summaries − their material standard − settled variances.
            WITH g AS (SELECT subledger_ref AS col, sum(debit - credit) AS b FROM fin.gl_entry WHERE company_id = @c AND account_role = 'WIP' GROUP BY 1),
                 s AS (SELECT r.collector_id AS col, sum(ci.value) AS cons
                       FROM mfg.consumption_issue ci JOIN mfg.shift_summary ss ON ss.summary_id = ci.summary_id JOIN mfg.production_run r ON r.run_id = ss.run_id
                       WHERE ss.company_id = @c AND ss.status = 'POSTED' GROUP BY 1),
                 m AS (SELECT r.collector_id AS col, sum(round(ss.good_units * c.material_cost, 2)) AS std
                       FROM mfg.shift_summary ss JOIN mfg.production_run r ON r.run_id = ss.run_id JOIN md.standard_cost_version c ON c.cost_version_id = r.cost_version_id
                       WHERE ss.company_id = @c AND ss.status = 'POSTED' GROUP BY 1),
                 k AS (SELECT collector_id AS col, coalesce(usage_variance, 0) + coalesce(price_variance, 0) AS settled FROM mfg.cost_collector WHERE company_id = @c),
                 a AS (SELECT k.col, coalesce(s.cons, 0) - coalesce(m.std, 0) - k.settled AS a FROM k LEFT JOIN s ON s.col = k.col LEFT JOIN m ON m.col = k.col)
            SELECT 'collector:' || coalesce(a.col, g.col)::text AS match_key, coalesce(a.a, 0) AS value_a, coalesce(g.b, 0) AS value_b,
                   'WIP_GL_DIFFERENCE' AS classification, 'ERROR' AS severity, 'COST-SET' AS component
            FROM a FULL JOIN g ON g.col = a.col WHERE coalesce(a.a, 0) <> coalesce(g.b, 0)) f
            """,
            """
            SELECT (SELECT coalesce(sum(ci.value), 0) FROM mfg.consumption_issue ci JOIN mfg.shift_summary ss ON ss.summary_id = ci.summary_id WHERE ss.company_id = @c AND ss.status = 'POSTED')
                   - (SELECT coalesce(sum(round(ss.good_units * c.material_cost, 2)), 0) FROM mfg.shift_summary ss JOIN mfg.production_run r ON r.run_id = ss.run_id
                      JOIN md.standard_cost_version c ON c.cost_version_id = r.cost_version_id WHERE ss.company_id = @c AND ss.status = 'POSTED')
                   - (SELECT coalesce(sum(coalesce(usage_variance, 0) + coalesce(price_variance, 0)), 0) FROM mfg.cost_collector WHERE company_id = @c),
                   (SELECT coalesce(sum(debit - credit), 0) FROM fin.gl_entry WHERE company_id = @c AND account_role = 'WIP')
            """),
        ["WIP-OPEN"] = (
            Findings + """
            -- E-MFG1-05-4: collectors of months ended by the cutoff that are not settled.
            SELECT 'collector:' || collector_id::text AS match_key, NULL::numeric AS value_a, NULL::numeric AS value_b,
                   'COLLECTOR_NOT_SETTLED' AS classification, 'ERROR' AS severity, 'COST-SET' AS component
            FROM mfg.cost_collector WHERE company_id = @c AND status = 'OPEN' AND (period_month + interval '1 month')::date <= @cutoff) f
            """,
            null),
        ["SHIFT-OPEN"] = (
            Findings + """
            -- E-MFG1-05-4: runs up to the cutoff still IN_PROGRESS (blocks OP-DAY and COST-SET through rec.recon_blocking).
            SELECT 'run:' || run_no AS match_key, NULL::numeric AS value_a, NULL::numeric AS value_b,
                   'RUN_NOT_POSTED' AS classification, 'ERROR' AS severity, NULL::text AS component
            FROM mfg.production_run WHERE company_id = @c AND status = 'IN_PROGRESS' AND business_date <= @cutoff) f
            """,
            null),
        ["USAGE-TOLERANCE"] = (
            Findings + """
            -- E-MFG1-05-4/6: real consumption beyond usage_tolerance_pct of the theoretical (a warning).
            SELECT 'run:' || r.run_no || '/' || i.code AS match_key, c.theoretical_qty AS value_a, c.qty AS value_b,
                   'USAGE_OUT_OF_TOLERANCE' AS classification, 'WARNING' AS severity, NULL::text AS component
            FROM mfg.material_consumption c
            JOIN mfg.shift_summary ss ON ss.summary_id = c.summary_id AND ss.status = 'POSTED'
            JOIN mfg.production_run r ON r.run_id = ss.run_id
            JOIN md.item i ON i.item_id = c.material_item_id
            WHERE c.company_id = @c AND c.theoretical_qty > 0 AND abs(c.qty - c.theoretical_qty) > @tol * c.theoretical_qty) f
            """,
            null),
        ["CURING-OVERDUE"] = (
            Findings + """
            -- E-MFG1-05-4: lots still CURING after the maximum curing hours of their recipe (a warning).
            SELECT 'lot:' || l.lot_code AS match_key, v.max_curing_hours::numeric AS value_a, round(extract(epoch FROM @asOf - f.curing_from) / 3600)::numeric AS value_b,
                   'CURING_OVERDUE' AS classification, 'WARNING' AS severity, NULL::text AS component
            FROM mfg.fg_lot f
            JOIN inv.lot l ON l.lot_id = f.lot_id
            JOIN mfg.production_run r ON r.run_id = f.run_id
            JOIN mfg.recipe_version v ON v.recipe_version_id = r.recipe_version_id
            WHERE f.company_id = @c AND f.status = 'CURING' AND f.curing_from + make_interval(hours => v.max_curing_hours) < @asOf) f
            """,
            null),
        ["PRODUCTION-CLOSE-ORDER"] = (
            Findings + """
            -- E-MFG1-05-5: in a period with production, OP-DAY closes before COST-SET and COST-SET before INV-MOV.
            SELECT s.component || ':' || p.period_id::text AS match_key, NULL::numeric AS value_a, NULL::numeric AS value_b,
                   'CLOSE_ORDER' AS classification, 'ERROR' AS severity, CASE s.component WHEN 'OP-DAY' THEN 'COST-SET' ELSE 'INV-MOV' END AS component
            FROM fin.period p
            JOIN fin.close_component_state s ON s.period_id = p.period_id AND s.component IN ('OP-DAY', 'COST-SET') AND s.status <> 'CLOSED'
            WHERE p.company_id = @c AND @cutoff BETWEEN p.starts_on AND p.ends_on
              AND EXISTS (SELECT 1 FROM mfg.production_run r WHERE r.company_id = @c AND r.status <> 'CANCELLED' AND r.business_date BETWEEN p.starts_on AND p.ends_on)) f
            """,
            null),
        ["AUTH-CONSUMPTION"] = (
            Findings + """
            -- E-FIS1-04-1: each scope line's consumed totals = consumptions − releases; each e-CF 44 line's live consumption = its net −
            -- what confirmed credit notes took (0 once the invoice is voided).
            WITH c AS (SELECT authorization_id, line_no,
                              sum(CASE WHEN reverses_consumption_id IS NULL THEN qty ELSE -qty END) AS qty,
                              sum(CASE WHEN reverses_consumption_id IS NULL THEN net ELSE -net END) AS net
                       FROM tax.fiscal_authorization_consumption WHERE company_id = @c GROUP BY 1, 2),
                 il AS (SELECT il.invoice_line_id,
                               CASE WHEN i.commercial_status = 'VOIDED' THEN 0
                                    ELSE il.net_amount - coalesce((SELECT sum(nl.net_amount) FROM sal.credit_note_line nl JOIN sal.credit_note n ON n.credit_note_id = nl.credit_note_id
                                                                   WHERE nl.invoice_line_id = il.invoice_line_id AND n.commercial_status NOT IN ('DRAFT', 'VOIDED')), 0) END AS expected,
                               coalesce((SELECT sum(CASE WHEN x.reverses_consumption_id IS NULL THEN x.net ELSE -x.net END) FROM tax.fiscal_authorization_consumption x
                                         WHERE x.invoice_line_id = il.invoice_line_id), 0) AS consumed
                        FROM sal.invoice_line il JOIN sal.invoice i ON i.invoice_id = il.invoice_id
                        WHERE i.company_id = @c AND i.ecf_type = '44' AND i.commercial_status <> 'DRAFT')
            SELECT 'auth-line:' || l.authorization_id::text || '/' || l.line_no AS match_key, l.net_consumed AS value_a, coalesce(c.net, 0) AS value_b,
                   'AUTH_LINE_CONSUMPTION_DIFFERENCE' AS classification, 'ERROR' AS severity, 'AR-REC' AS component
            FROM tax.fiscal_authorization_line l LEFT JOIN c ON c.authorization_id = l.authorization_id AND c.line_no = l.line_no
            WHERE l.company_id = @c AND (l.net_consumed <> coalesce(c.net, 0) OR l.qty_consumed <> coalesce(c.qty, 0))
            UNION ALL
            SELECT 'invoice-line:' || invoice_line_id::text, expected, consumed, 'INVOICE_CONSUMPTION_DIFFERENCE', 'ERROR', 'AR-REC' FROM il WHERE expected <> consumed) f
            """,
            null),
        ["PROFORMA-ASIG"] = (
            Findings + """
            -- E-FIS1b-01-12: what each proforma and each receipt say is allocated = their live allocations; an OPEN proforma's net =
            -- what its delivery still has delivered and not invoiced, and an INVOICED one leaves nothing unbilled.
            WITH live AS (SELECT x.* FROM fin.proforma_allocation x
                          WHERE x.company_id = @c AND x.reverses_allocation_id IS NULL
                            AND NOT EXISTS (SELECT 1 FROM fin.proforma_allocation u WHERE u.reverses_allocation_id = x.allocation_id)),
                 unbilled AS (SELECT l.proforma_id, sum(round((dl.qty_delivered - dl.qty_invoiced) * l.unit_price, 2)) AS net
                              FROM sal.proforma_line l JOIN log.delivery_line dl ON dl.delivery_line_id = l.delivery_line_id
                              WHERE l.company_id = @c GROUP BY l.proforma_id)
            SELECT 'PF:' || f.proforma_no AS match_key, f.allocated_amount AS value_a,
                   coalesce((SELECT sum(l.amount) FROM live l WHERE l.proforma_id = f.proforma_id), 0) AS value_b,
                   'PROFORMA_ALLOCATION_DIFFERENCE' AS classification, 'ERROR' AS severity, 'AR-REC' AS component
            FROM sal.proforma f
            WHERE f.company_id = @c AND f.allocated_amount <> coalesce((SELECT sum(l.amount) FROM live l WHERE l.proforma_id = f.proforma_id), 0)
            UNION ALL
            -- CF1-04 (E-CF1-01-5): a receipt's allocated amount counts its live assignments to proformas and to cash orders.
            SELECT 'REC:' || r.receipt_no, r.allocated_amount, x.assigned, 'RECEIPT_ALLOCATION_DIFFERENCE', 'ERROR', 'AR-REC'
            FROM fin.receipt r
            CROSS JOIN LATERAL (SELECT coalesce((SELECT sum(l.amount) FROM live l WHERE l.receipt_id = r.receipt_id), 0)
                                     + coalesce((SELECT sum(o.amount) FROM fin.order_allocation o
                                                 WHERE o.receipt_id = r.receipt_id AND o.reverses_allocation_id IS NULL
                                                   AND NOT EXISTS (SELECT 1 FROM fin.order_allocation u WHERE u.reverses_allocation_id = o.allocation_id)), 0) AS assigned) x
            WHERE r.company_id = @c AND r.allocated_amount <> x.assigned
            UNION ALL
            SELECT 'PF:' || f.proforma_no, CASE WHEN f.status = 'OPEN' THEN f.net_total ELSE 0 END, coalesce(u.net, 0), 'PROFORMA_UNBILLED_DIFFERENCE', 'ERROR', 'AR-REC'
            FROM sal.proforma f LEFT JOIN unbilled u ON u.proforma_id = f.proforma_id
            WHERE f.company_id = @c AND f.status IN ('OPEN', 'INVOICED') AND CASE WHEN f.status = 'OPEN' THEN f.net_total ELSE 0 END <> coalesce(u.net, 0)) f
            """,
            null),
        ["CASH-SALE"] = (
            Findings + """
            -- E-CF1-10, E-CF1-02-2: per cash order, what was delivered (at its price, with the ITBIS share of what had to be paid)
            -- against the money that counts — live assignments of RECORDED receipts (a cheque only once its deposit is matched) plus
            -- what its invoices took; what the order says is assigned against its live assignments; and (E-CF1-11) cash or cheques
            -- still in transit after cash_deposit_alert_days.
            WITH live AS (SELECT x.* FROM fin.order_allocation x
                          WHERE x.company_id = @c AND x.reverses_allocation_id IS NULL
                            AND NOT EXISTS (SELECT 1 FROM fin.order_allocation u WHERE u.reverses_allocation_id = x.allocation_id)),
                 sale AS (SELECT o.sales_order_id, o.order_no, o.allocated_amount,
                                 -- PRS-04: the products carry their share of the ITBIS paid; the freight carries none (E-SRV1-15).
                                 round((SELECT coalesce(sum(l.qty_delivered * l.unit_price), 0) FROM sal.sales_order_line l
                                        WHERE l.sales_order_id = o.sales_order_id AND l.lines_version = o.lines_version)
                                       * (o.payment_total - f.freight) / nullif(o.total_net - f.freight, 0)
                                       + (SELECT coalesce(sum(l.qty_delivered * l.freight_unit_price), 0) FROM sal.sales_order_line l
                                          WHERE l.sales_order_id = o.sales_order_id AND l.lines_version = o.lines_version), 2) AS delivered,
                                 coalesce((SELECT sum(l.amount) FROM live l JOIN fin.receipt r ON r.receipt_id = l.receipt_id
                                           WHERE l.sales_order_id = o.sales_order_id AND r.status = 'RECORDED' AND (r.method <> 'CHEQUE' OR r.bank_status = 'MATCHED')), 0)
                                 + coalesce((SELECT sum(a.amount) FROM fin.ar_application a JOIN sal.invoice i ON i.ar_doc_id = a.ar_doc_id
                                             WHERE a.reverses_application_id IS NULL
                                               AND NOT EXISTS (SELECT 1 FROM fin.ar_application u WHERE u.reverses_application_id = a.application_id)
                                               AND EXISTS (SELECT 1 FROM sal.invoice_line il JOIN log.delivery_line dl ON dl.delivery_line_id = il.delivery_line_id
                                                           JOIN log.delivery d ON d.delivery_id = dl.delivery_id
                                                           WHERE il.invoice_id = i.invoice_id AND d.sales_order_id = o.sales_order_id)), 0) AS paid,
                                 coalesce((SELECT sum(l.amount) FROM live l WHERE l.sales_order_id = o.sales_order_id), 0) AS assigned
                          FROM sal.sales_order o
                          CROSS JOIN LATERAL (SELECT coalesce(sum(l.freight_amount), 0) AS freight FROM sal.sales_order_line l
                                              WHERE l.sales_order_id = o.sales_order_id AND l.lines_version = o.lines_version) f
                          WHERE o.company_id = @c AND o.cash_sale AND o.payment_total IS NOT NULL)
            SELECT 'PV:' || s.order_no AS match_key, s.delivered AS value_a, s.paid AS value_b, 'CASH_SALE_UNPAID' AS classification, 'ERROR' AS severity, 'AR-REC' AS component
            FROM sale s WHERE s.delivered > s.paid
            UNION ALL
            SELECT 'PV:' || s.order_no, s.allocated_amount, s.assigned, 'ORDER_ALLOCATION_DIFFERENCE', 'ERROR', 'AR-REC'
            FROM sale s WHERE s.allocated_amount <> s.assigned
            UNION ALL
            SELECT 'PV:' || o.order_no, o.allocated_amount, coalesce((SELECT sum(l.amount) FROM live l WHERE l.sales_order_id = o.sales_order_id), 0), 'ORDER_ALLOCATION_DIFFERENCE', 'ERROR', 'AR-REC'
            FROM sal.sales_order o
            WHERE o.company_id = @c AND o.payment_total IS NULL
              AND o.allocated_amount <> coalesce((SELECT sum(l.amount) FROM live l WHERE l.sales_order_id = o.sales_order_id), 0)
            UNION ALL
            SELECT 'REC:' || r.receipt_no, r.amount, (@cutoff - r.receipt_date)::numeric, 'CASH_UNDEPOSITED', 'WARNING', NULL::text
            FROM fin.receipt r
            WHERE r.company_id = @c AND r.status = 'RECORDED' AND r.method IN ('CASH', 'CHEQUE') AND r.bank_status = 'IN_TRANSIT' AND r.receipt_date <= @cutoff
              AND @cutoff - r.receipt_date > @cdays) f
            """,
            null),
        ["IMPORT-CLEARING"] = (
            Findings + """
            -- E-USD1-06-4: what «Importaciones por liquidar» holds per DUA (its IMPORT subledger) against its duties and other charges while no
            -- POSTED settlement holds it, zero afterwards; and DUAs unsettled after import_settlement_alert_days.
            WITH settled AS (SELECT d.document_id FROM pur.import_settlement_document d JOIN pur.import_settlement s ON s.settlement_id = d.settlement_id
                             WHERE d.company_id = @c AND d.document_kind = 'CUSTOMS_DECLARATION' AND s.status = 'POSTED'),
                 gl AS (SELECT subledger_ref AS dua_id, sum(debit - credit) AS b FROM fin.gl_entry WHERE company_id = @c AND account_role = 'IMPORT_CLEARING' GROUP BY subledger_ref),
                 due AS (SELECT c.dua_id, c.dua_no,
                                CASE WHEN c.status = 'POSTED' AND c.dua_id NOT IN (SELECT document_id FROM settled) THEN c.duties_amount + c.other_amount ELSE 0 END AS a
                         FROM pur.customs_declaration c WHERE c.company_id = @c)
            SELECT 'DUA ' || coalesce(due.dua_no, gl.dua_id::text) AS match_key, coalesce(due.a, 0) AS value_a, coalesce(gl.b, 0) AS value_b,
                   'IMPORT_CLEARING_DIFFERENCE' AS classification, 'ERROR' AS severity, 'AP-REC' AS component
            FROM due FULL JOIN gl ON gl.dua_id = due.dua_id WHERE coalesce(due.a, 0) <> coalesce(gl.b, 0)
            UNION ALL
            SELECT 'DUA ' || c.dua_no, c.duties_amount + c.other_amount, (@cutoff - c.dua_date)::numeric, 'IMPORT_SETTLEMENT_OVERDUE', 'WARNING', NULL::text
            FROM pur.customs_declaration c
            WHERE c.company_id = @c AND c.status = 'POSTED' AND c.duties_amount + c.other_amount > 0 AND c.dua_id NOT IN (SELECT document_id FROM settled)
              AND c.dua_date <= @cutoff AND @cutoff - c.dua_date > @idays) f
            """,
            null),
        ["FA-GL"] = (
            Findings + """
            -- E-AF-10, E-AF1-04-7/8: per account, the live cards' cost against the fixed-asset accounts and their accumulated depreciation
            -- against the classes' accounts; each ended month a card still had to depreciate (an error for the cutoff's own month).
            WITH live AS (SELECT x.* FROM fa.asset x WHERE x.company_id = @c AND x.status IN ('AWAITING_SERVICE', 'IN_SERVICE')),
                 cost_accounts AS (SELECT DISTINCT c.account_id FROM pur.expense_category c JOIN fin.account a ON a.account_id = c.account_id
                                   WHERE c.company_id = @c AND c.goods_type_606 = '04' AND a.account_class = 'ASSET'),
                 cost_cards AS (SELECT c.account_id, sum(x.cost) AS a FROM live x JOIN pur.expense_category c ON c.expense_category_id = x.expense_category_id GROUP BY c.account_id),
                 cost_gl AS (SELECT account_id, sum(debit - credit) AS b FROM fin.gl_entry WHERE company_id = @c AND account_id IN (SELECT account_id FROM cost_accounts) GROUP BY account_id),
                 acc_accounts AS (SELECT DISTINCT accumulated_account_id AS account_id FROM fa.asset_class WHERE company_id = @c AND status IN ('ACTIVE', 'SUPERSEDED')),
                 acc_cards AS (SELECT k.accumulated_account_id AS account_id, sum(x.accumulated) AS a FROM live x JOIN fa.asset_class k ON k.asset_class_id = x.asset_class_id
                               GROUP BY k.accumulated_account_id),
                 acc_gl AS (SELECT account_id, sum(credit - debit) AS b FROM fin.gl_entry WHERE company_id = @c AND account_id IN (SELECT account_id FROM acc_accounts) GROUP BY account_id),
                 due AS (SELECT x.asset_id, (date_trunc('month', x.in_service_on) + make_interval(months => 1 + x.months_depreciated))::date AS next,
                                x.useful_life_months - x.months_depreciated AS left_months
                         FROM live x
                         WHERE x.status = 'IN_SERVICE' AND x.months_depreciated < x.useful_life_months
                           AND x.cost - round(x.cost * x.residual_pct / 100, 2) - x.accumulated > 0),
                 missing AS (SELECT gs::date AS m, count(*) AS n
                             FROM due CROSS JOIN LATERAL generate_series(
                               due.next,
                               least(date_trunc('month', CAST(@cutoff AS date) + 1) - interval '1 month', due.next + make_interval(months => due.left_months - 1)),
                               interval '1 month') AS gs
                             GROUP BY gs)
            SELECT 'account:' || a.code AS match_key, coalesce(cc.a, 0) AS value_a, coalesce(g.b, 0) AS value_b,
                   'FA_COST_DIFFERENCE' AS classification, 'ERROR' AS severity, 'FA-REC' AS component
            FROM cost_accounts ca JOIN fin.account a ON a.account_id = ca.account_id
            LEFT JOIN cost_cards cc ON cc.account_id = ca.account_id LEFT JOIN cost_gl g ON g.account_id = ca.account_id
            WHERE coalesce(cc.a, 0) <> coalesce(g.b, 0)
            UNION ALL
            SELECT 'account:' || a.code, coalesce(ac.a, 0), coalesce(g.b, 0), 'FA_ACCUMULATED_DIFFERENCE', 'ERROR', 'FA-REC'
            FROM acc_accounts aa JOIN fin.account a ON a.account_id = aa.account_id
            LEFT JOIN acc_cards ac ON ac.account_id = aa.account_id LEFT JOIN acc_gl g ON g.account_id = aa.account_id
            WHERE coalesce(ac.a, 0) <> coalesce(g.b, 0)
            UNION ALL
            SELECT 'month:' || to_char(m.m, 'YYYY-MM'), m.n::numeric, NULL::numeric, 'FA_DEPRECIATION_MISSING',
                   CASE WHEN m.m = date_trunc('month', CAST(@cutoff AS date)) THEN 'ERROR' ELSE 'WARNING' END,
                   CASE WHEN m.m = date_trunc('month', CAST(@cutoff AS date)) THEN 'FA-REC' END
            FROM missing m) f
            """,
            null),
        ["FX-REVAL"] = (
            Findings + """
            -- E-USD1-06-4: each month ended before the cutoff that closed with a USD payable or bank balance and has no POSTED revaluation.
            WITH months AS (SELECT generate_series(date_trunc('month', min(posting_date)), date_trunc('month', CAST(@cutoff AS date)) - interval '1 month', interval '1 month')::date AS m
                            FROM fin.gl_entry WHERE company_id = @c AND currency = 'USD')
            SELECT 'month:' || to_char(m.m, 'YYYY-MM') AS match_key, open.n::numeric AS value_a, NULL::numeric AS value_b,
                   'FX_REVALUATION_MISSING' AS classification, 'WARNING' AS severity, NULL::text AS component
            FROM months m
            CROSS JOIN LATERAL (SELECT count(*) AS n FROM (
                SELECT subledger_ref FROM fin.gl_entry
                WHERE company_id = @c AND currency = 'USD' AND account_role IN ('AP_FOREIGN', 'BANK') AND posting_date < (m.m + interval '1 month')::date
                GROUP BY subledger_ref HAVING sum(sign(debit - credit) * amount_fc) <> 0) x) open
            WHERE open.n > 0 AND NOT EXISTS (SELECT 1 FROM fin.fx_revaluation r WHERE r.company_id = @c AND r.month = m.m AND r.status = 'POSTED')) f
            """,
            null),
        ["EXEMPT-WITHOUT-AUTH"] = (
            Findings + """
            -- E-FIS1-04-2: an issued invoice without ITBIS that is not an e-CF 44, with an item the applied SALES_ITBIS rule taxes.
            SELECT 'invoice:' || i.invoice_no AS match_key, i.net_total AS value_a, i.tax_total AS value_b,
                   'EXEMPT_WITHOUT_AUTHORIZATION' AS classification, 'ERROR' AS severity, 'AR-REC' AS component
            FROM sal.invoice i JOIN tax.tax_determination d ON d.determination_id = i.tax_determination_id
            WHERE i.company_id = @c AND i.commercial_status NOT IN ('DRAFT', 'VOIDED') AND i.ecf_type <> '44' AND i.tax_total = 0
              AND EXISTS (SELECT 1 FROM sal.invoice_line il JOIN md.item it ON it.item_id = il.item_id
                          WHERE il.invoice_id = i.invoice_id
                            AND NOT EXISTS (SELECT 1 FROM tax.fiscal_rule_version v JOIN tax.fiscal_rule r ON r.rule_id = v.rule_id
                                            WHERE v.rule_version_id = ANY (d.rule_version_ids) AND r.rule_kind = 'SALES_ITBIS'
                                              AND v.definition -> 'exempt_item_categories' ? it.item_category))) f
            """,
            null),
        ["AUTH-EXPIRY"] = (
            Findings + """
            -- E-FIS1-04-3: authorizations in use that expire within authorization_expiry_alert_days, or whose project term ended (a warning).
            SELECT 'auth:' || certificate_no AS match_key, NULL::numeric AS value_a, (valid_until - @cutoff)::numeric AS value_b,
                   CASE WHEN project_term_ends_on < @cutoff THEN 'PROJECT_TERM_ENDED' ELSE 'AUTHORIZATION_EXPIRING' END AS classification,
                   'WARNING' AS severity, NULL::text AS component
            FROM tax.fiscal_authorization
            WHERE company_id = @c AND status IN ('ACTIVE', 'EXHAUSTED', 'SUSPENDED')
              AND ((valid_until IS NOT NULL AND valid_until <= @cutoff + @adays) OR (project_term_ends_on IS NOT NULL AND project_term_ends_on < @cutoff))) f
            """,
            null),
        ["TAX-606"] = (
            Findings + """
            -- E-FIS2-02-8: the month of the cutoff — the 606's ITBIS to advance (NCF records) against ITBIS_RECOVERABLE posted in that
            -- month, and each record's warnings (a withholding without its ISR type, a purchase without classification).
            WITH r AS (SELECT * FROM tax.report_606(@c, @cutoff)),
                 gl AS (SELECT coalesce(sum(e.debit - e.credit), 0) AS amount FROM fin.gl_entry e
                        WHERE e.company_id = @c AND e.account_role = 'ITBIS_RECOVERABLE'
                          AND e.posting_date >= date_trunc('month', @cutoff)::date AND e.posting_date < (date_trunc('month', @cutoff) + interval '1 month')::date)
            SELECT 'itbis:' || to_char(@cutoff, 'YYYYMM') AS match_key, (SELECT coalesce(sum(itbis_to_advance), 0) FROM r WHERE record_kind = 'NCF') AS value_a,
                   gl.amount AS value_b, 'TAX606_ITBIS_DIFFERENCE' AS classification, 'WARNING' AS severity, NULL::text AS component
            FROM gl WHERE (SELECT coalesce(sum(itbis_to_advance), 0) FROM r WHERE record_kind = 'NCF') <> gl.amount
            UNION ALL
            -- E-GAS-06-5: the selective tax, other taxes and legal tip of the month's NCF records against their expense roles.
            SELECT x.tax || ':' || to_char(@cutoff, 'YYYYMM'), x.reported, x.posted, x.classification, 'WARNING', NULL::text
            FROM (SELECT v.tax, v.classification,
                         (SELECT coalesce(sum(CASE v.tax WHEN 'selective' THEN selective_tax WHEN 'other' THEN other_taxes ELSE legal_tip END), 0) FROM r WHERE record_kind = 'NCF') AS reported,
                         (SELECT coalesce(sum(e.debit - e.credit), 0) FROM fin.gl_entry e
                          WHERE e.company_id = @c AND e.account_role = v.role
                            AND e.posting_date >= date_trunc('month', @cutoff)::date AND e.posting_date < (date_trunc('month', @cutoff) + interval '1 month')::date) AS posted
                  FROM (VALUES ('selective', 'SELECTIVE_TAX_EXPENSE', 'TAX606_SELECTIVE_DIFFERENCE'), ('other', 'OTHER_TAX_EXPENSE', 'TAX606_OTHER_DIFFERENCE'),
                               ('tip', 'LEGAL_TIP_EXPENSE', 'TAX606_TIP_DIFFERENCE')) AS v (tax, role, classification)) x
            WHERE x.reported <> x.posted
            UNION ALL
            SELECT 'ncf:' || r.ncf, r.total_amount, NULL::numeric, w, 'WARNING', NULL::text FROM r CROSS JOIN unnest(r.warnings) AS w) f
            """,
            null),
        ["CONTROLS-WAIVED"] = (
            Findings + """
            -- E-ADM-2-5: the commands of the cutoff's month in which a superadministrator waived a four-eyes control (a warning).
            SELECT 'command:' || l.command_type || ':' || l.command_id::text AS match_key, NULL::numeric AS value_a, NULL::numeric AS value_b,
                   'CONTROL_WAIVED' AS classification, 'WARNING' AS severity, NULL::text AS component
            FROM core.command_log l
            WHERE l.company_id = @c AND l.controls_waived
              AND (l.committed_at AT TIME ZONE 'America/Santo_Domingo')::date >= date_trunc('month', @cutoff)::date
              AND (l.committed_at AT TIME ZONE 'America/Santo_Domingo')::date < (date_trunc('month', @cutoff) + interval '1 month')::date) f
            """,
            null),
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
            int? openHours = null;
            if (code is "GRNI-AGING" or "DELIVERY-OPEN")
            {
                agingDays = code == "GRNI-AGING" ? await PolicyIntegerAsync(context, asOf, "INVENTORY", "grni_aging_alert_days", cancellationToken).ConfigureAwait(false) : null;
                openHours = code == "DELIVERY-OPEN" ? await PolicyIntegerAsync(context, asOf, "REVENUE_ACCOUNTING", "delivery_open_alert_hours", cancellationToken).ConfigureAwait(false) : null;
                if (agingDays is null && openHours is null)
                {
                    runs.Add(await StoreAsync(context, runId, code, asOf, cutoff, "FAILED", null, null, [], cancellationToken).ConfigureAwait(false));
                    continue;
                }
            }

            decimal? tolerance = null;
            if (code == "USAGE-TOLERANCE")
            {
                tolerance = await PolicyDecimalAsync(context, asOf, "PRODUCTION", "usage_tolerance_pct", cancellationToken).ConfigureAwait(false);

                // Without production there is nothing to compare, so the missing policy only fails a run that has posted consumption.
                await using var posted = Sql.Command(
                    context.Connection, context.Transaction, "SELECT EXISTS (SELECT 1 FROM mfg.shift_summary WHERE company_id = @c AND status = 'POSTED')", ("c", context.CompanyId));
                if (tolerance is null && await posted.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
                {
                    runs.Add(await StoreAsync(context, runId, code, asOf, cutoff, "FAILED", null, null, [], cancellationToken).ConfigureAwait(false));
                    continue;
                }
            }

            int? alertDays = null;
            if (code == "AUTH-EXPIRY")
            {
                alertDays = await PolicyIntegerAsync(context, asOf, "REVENUE_ACCOUNTING", "authorization_expiry_alert_days", cancellationToken).ConfigureAwait(false);

                // E-FIS1-04-4: without authorizations there is nothing to warn about, so the missing policy only fails a run that has some.
                await using var any = Sql.Command(
                    context.Connection, context.Transaction, "SELECT EXISTS (SELECT 1 FROM tax.fiscal_authorization WHERE company_id = @c)", ("c", context.CompanyId));
                if (alertDays is null && await any.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
                {
                    runs.Add(await StoreAsync(context, runId, code, asOf, cutoff, "FAILED", null, null, [], cancellationToken).ConfigureAwait(false));
                    continue;
                }
            }

            int? importDays = null;
            if (code == "IMPORT-CLEARING")
            {
                importDays = await PolicyIntegerAsync(context, asOf, "PURCHASING", "import_settlement_alert_days", cancellationToken).ConfigureAwait(false);

                // E-USD1-06-4: without DUAs there is nothing to warn about, so the missing policy only fails a run that has some.
                await using var duas = Sql.Command(
                    context.Connection, context.Transaction, "SELECT EXISTS (SELECT 1 FROM pur.customs_declaration WHERE company_id = @c AND status = 'POSTED')", ("c", context.CompanyId));
                if (importDays is null && await duas.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
                {
                    runs.Add(await StoreAsync(context, runId, code, asOf, cutoff, "FAILED", null, null, [], cancellationToken).ConfigureAwait(false));
                    continue;
                }
            }

            int? cashDays = null;
            if (code == "CASH-SALE")
            {
                cashDays = await PolicyIntegerAsync(context, asOf, "REVENUE_ACCOUNTING", "cash_deposit_alert_days", cancellationToken).ConfigureAwait(false);

                // E-CF1-11: without cash or cheques in transit there is nothing to warn about, so the missing policy only fails a run that has some.
                await using var inTransit = Sql.Command(
                    context.Connection, context.Transaction,
                    "SELECT EXISTS (SELECT 1 FROM fin.receipt WHERE company_id = @c AND status = 'RECORDED' AND method IN ('CASH', 'CHEQUE') AND bank_status = 'IN_TRANSIT')",
                    ("c", context.CompanyId));
                if (cashDays is null && await inTransit.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
                {
                    runs.Add(await StoreAsync(context, runId, code, asOf, cutoff, "FAILED", null, null, [], cancellationToken).ConfigureAwait(false));
                    continue;
                }
            }

            var unbilledDays = code == "CONTRACT-ASSET"
                ? await PolicyIntegerAsync(context, asOf, "REVENUE_ACCOUNTING", "unbilled_aging_alert_days", cancellationToken).ConfigureAwait(false)
                : null;
            var findings = new List<ReconFinding>();
            await using (var command = Sql.Command(
                context.Connection,
                context.Transaction,
                definition.Findings,
                ("c", context.CompanyId),
                ("asOf", asOf),
                ("days", agingDays ?? 0),
                ("hours", openHours ?? 0),
                ("udays", unbilledDays),
                ("tol", tolerance ?? 0m),
                ("adays", alertDays ?? 0),
                ("cdays", cashDays ?? 0),
                ("idays", importDays ?? 0),
                ("cutoff", cutoff ?? Rochell.Platform.Time.BusinessCalendar.DefaultBusinessDate(asOf))))
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

    /// <summary>A DECIMAL_PERCENT parameter of a policy in force (a fraction); null without it.</summary>
    private static async Task<decimal?> PolicyDecimalAsync(CommandContext context, DateTime asOf, string policy, string parameter, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT p.value #>> '{}' FROM acc.accounting_policy_version v
            JOIN acc.accounting_policy_parameter p ON p.policy_version_id = v.policy_version_id
            WHERE v.company_id = @c AND v.policy_code = @policy AND v.status = 'ACTIVE' AND p.param_code = @param
              AND v.effective_from <= @d AND (v.effective_to IS NULL OR v.effective_to > @d)
            """,
            ("c", context.CompanyId),
            ("policy", policy),
            ("param", parameter),
            ("d", Rochell.Platform.Time.BusinessCalendar.DefaultBusinessDate(asOf)));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string value
            ? decimal.Parse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>An INTEGER parameter of a policy in force (read directly: Reconciliation depends on Platform only); null without it.</summary>
    private static async Task<int?> PolicyIntegerAsync(CommandContext context, DateTime asOf, string policy, string parameter, CancellationToken cancellationToken)
    {
        await using var command = Sql.Command(
            context.Connection,
            context.Transaction,
            """
            SELECT p.value #>> '{}' FROM acc.accounting_policy_version v
            JOIN acc.accounting_policy_parameter p ON p.policy_version_id = v.policy_version_id
            WHERE v.company_id = @c AND v.policy_code = @policy AND v.status = 'ACTIVE' AND p.param_code = @param
              AND v.effective_from <= @d AND (v.effective_to IS NULL OR v.effective_to > @d)
            """,
            ("c", context.CompanyId),
            ("policy", policy),
            ("param", parameter),
            ("d", Rochell.Platform.Time.BusinessCalendar.DefaultBusinessDate(asOf)));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string days
            ? int.Parse(days, NumberStyles.None, CultureInfo.InvariantCulture)
            : null;
    }
}
