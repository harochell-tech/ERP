-- FIS2-02 · Report 606 and reconciliation TAX-606 (Frozen Baseline FIS-2 §2; approved errata E-FIS2-1…14, E-FIS2-02-1…12).
-- The 606 rows of a month come from one read-only function, used by the query and by TAX-606 alike.

-- ---------------------------------------------------------------------------------------------
-- tax.report_606(company, any day of the month): one row per record, in the DGII tool's field order plus what the screen needs.
--   NCF      (E-FIS2-2, E-FIS2-02-12): posted, not reversed supplier invoices whose NCF date is in the month; the payment date,
--            withholdings and method 2 only when the invoice was settled within that same month, otherwise method 4 (credit).
--   PAYMENT  (E-FIS2-3, E-FIS2-02-11): invoices of an earlier month with withholdings, settled within this month — the invoiced
--            amounts again, ITBIS invoiced / to cost / to advance 0 (never advanced twice), the payment date and the withholdings.
-- Withholdings: a PURCHASE_WITHHOLDING on the ITBIS base is ITBIS withheld; on the NET base it is ISR, with the rule's
-- isr_withholding_type (E-FIS2-01-4). The goods-and-services type is the active classification at the NCF date applied to the
-- category of the invoice's largest line (E-FIS2-02-3). Proportionality, perceived taxes, selective, other taxes and tip are 0.
-- ---------------------------------------------------------------------------------------------
CREATE FUNCTION tax.report_606(p_company uuid, p_month date)
RETURNS TABLE (
  si_id uuid, record_kind text, supplier_name text, rnc text, id_type integer, goods_type text, ncf text, ncf_modified text, ncf_date date,
  payment_date date, services_amount numeric, goods_amount numeric, total_amount numeric, itbis_billed numeric, itbis_withheld numeric,
  itbis_proportional numeric, itbis_to_cost numeric, itbis_to_advance numeric, itbis_perceived numeric, isr_withholding_type text,
  isr_withheld numeric, isr_perceived numeric, selective_tax numeric, other_taxes numeric, legal_tip numeric, payment_method integer,
  warnings text[])
LANGUAGE sql STABLE AS $$
  WITH bounds AS (
    SELECT date_trunc('month', p_month)::date AS m0, (date_trunc('month', p_month) + interval '1 month')::date AS m1
  ),
  inv AS (
    SELECT si.si_id, si.supplier_fiscal_number AS ncf, si.doc_date, si.tax_determination_id, p.rnc, p.legal_name,
           coalesce((SELECT sum(l.net_amount) FROM pur.supplier_invoice_line l WHERE l.si_id = si.si_id), 0) AS goods,
           (SELECT i.item_category
            FROM pur.supplier_invoice_line l
            JOIN pur.purchase_order_line pl ON pl.po_line_id = l.po_line_id
            JOIN md.item i ON i.item_id = pl.item_id
            WHERE l.si_id = si.si_id ORDER BY l.net_amount DESC, l.line_no LIMIT 1) AS category,
           ap.open_amount,
           (SELECT max(pm.value_date)
            FROM fin.ap_application a JOIN fin.payment pm ON pm.payment_id = a.payment_id
            WHERE a.ap_doc_id = ap.ap_doc_id AND a.reverses_application_id IS NULL
              AND NOT EXISTS (SELECT 1 FROM fin.ap_application r WHERE r.reverses_application_id = a.application_id)) AS paid_on
    FROM pur.supplier_invoice si
    JOIN md.party p ON p.party_id = si.party_id
    LEFT JOIN fin.ap_document ap ON ap.doc_type = 'SUPPLIER_INVOICE' AND ap.source_doc_id = si.si_id
    WHERE si.company_id = p_company AND si.accounting_status = 'POSTED'
  ),
  taxes AS (
    SELECT d.determination_id,
           coalesce(sum(d.amount) FILTER (WHERE d.effect IN ('RECOVERABLE_INPUT', 'NON_RECOVERABLE_INPUT')), 0) AS itbis,
           coalesce(sum(d.amount) FILTER (WHERE d.effect = 'NON_RECOVERABLE_INPUT'), 0) AS itbis_cost,
           coalesce(sum(d.amount) FILTER (WHERE d.effect = 'WITHHOLDING' AND v.definition ->> 'base' = 'ITBIS'), 0) AS itbis_withheld,
           coalesce(sum(d.amount) FILTER (WHERE d.effect = 'WITHHOLDING' AND v.definition ->> 'base' = 'NET'), 0) AS isr_withheld,
           min(v.definition ->> 'isr_withholding_type') FILTER (WHERE d.effect = 'WITHHOLDING' AND v.definition ->> 'base' = 'NET') AS isr_type
    FROM tax.tax_determination_line d JOIN tax.fiscal_rule_version v ON v.rule_version_id = d.rule_version_id
    WHERE d.company_id = p_company
    GROUP BY d.determination_id
  ),
  records AS (
    SELECT i.*, t.itbis, t.itbis_cost, t.itbis_withheld, t.isr_withheld, t.isr_type,
           (SELECT v.definition -> 'classes' ->> i.category
            FROM tax.fiscal_rule_version v JOIN tax.fiscal_rule r ON r.rule_id = v.rule_id
            WHERE r.company_id = p_company AND r.rule_kind = 'REPORT_606_CLASSIFICATION' AND v.status = 'ACTIVE'
              AND v.effective_from <= i.doc_date AND (v.effective_to IS NULL OR v.effective_to > i.doc_date)) AS goods_type,
           i.open_amount = 0 AND i.paid_on >= b.m0 AND i.paid_on < b.m1 AS settled_in_month,
           CASE WHEN i.doc_date >= b.m0 AND i.doc_date < b.m1 THEN 'NCF' ELSE 'PAYMENT' END AS kind
    FROM inv i
    CROSS JOIN bounds b
    LEFT JOIN taxes t ON t.determination_id = i.tax_determination_id
    WHERE (i.doc_date >= b.m0 AND i.doc_date < b.m1)
       OR (i.doc_date < b.m0 AND i.open_amount = 0 AND i.paid_on >= b.m0 AND i.paid_on < b.m1
           AND (coalesce(t.itbis_withheld, 0) > 0 OR coalesce(t.isr_withheld, 0) > 0))
  )
  SELECT r.si_id,
         r.kind,
         r.legal_name,
         r.rnc,
         CASE length(r.rnc) WHEN 9 THEN 1 WHEN 11 THEN 2 END,
         r.goods_type,
         r.ncf,
         NULL::text,
         r.doc_date,
         CASE WHEN r.settled_in_month THEN r.paid_on END,
         0::numeric(19,2),
         r.goods::numeric(19,2),
         r.goods::numeric(19,2),
         (CASE WHEN r.kind = 'NCF' THEN coalesce(r.itbis, 0) ELSE 0 END)::numeric(19,2),
         (CASE WHEN r.settled_in_month THEN coalesce(r.itbis_withheld, 0) ELSE 0 END)::numeric(19,2),
         0::numeric(19,2),
         (CASE WHEN r.kind = 'NCF' THEN coalesce(r.itbis_cost, 0) ELSE 0 END)::numeric(19,2),
         (CASE WHEN r.kind = 'NCF' THEN coalesce(r.itbis, 0) - coalesce(r.itbis_cost, 0) ELSE 0 END)::numeric(19,2),
         0::numeric(19,2),
         CASE WHEN r.settled_in_month AND coalesce(r.isr_withheld, 0) > 0 THEN r.isr_type END,
         (CASE WHEN r.settled_in_month THEN coalesce(r.isr_withheld, 0) ELSE 0 END)::numeric(19,2),
         0::numeric(19,2),
         0::numeric(19,2),
         0::numeric(19,2),
         0::numeric(19,2),
         CASE WHEN r.settled_in_month THEN 2 ELSE 4 END,
         array_remove(ARRAY[
           CASE WHEN r.goods_type IS NULL THEN 'CLASSIFICATION_MISSING' END,
           CASE WHEN r.settled_in_month AND coalesce(r.isr_withheld, 0) > 0 AND r.isr_type IS NULL THEN 'ISR_WITHHOLDING_TYPE_MISSING' END], NULL)
  FROM records r
  ORDER BY r.kind, r.doc_date, r.ncf
$$;
REVOKE ALL ON FUNCTION tax.report_606(uuid, date) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION tax.report_606(uuid, date) TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- E-FIS2-02-8: TAX-606 (warning, blocks nothing).
-- ---------------------------------------------------------------------------------------------
INSERT INTO rec.recon_definition (recon_code, description, severity) VALUES
  ('TAX-606', 'ITBIS por adelantar del 606 del mes = ITBIS de compras (ITBIS_RECOVERABLE) contabilizado en el mes; retenciones ISR sin tipo; compras sin clasificación (E-FIS2-02-8)', 'WARNING');
