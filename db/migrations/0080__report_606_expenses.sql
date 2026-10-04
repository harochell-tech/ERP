-- GAS1-06 · The 606 of expense purchases (approved errata E-GAS-06-1…6). tax.report_606 keeps its signature; what changes:
--   - the goods-and-services type of the invoice's largest line is its expense category's 606 type, or — for an inventory line — the
--     classification rule applied to its item's category, as before (E-GAS-06-1);
--   - services amount = the net of expense lines whose category is a service; goods amount = the rest (E-GAS-06-2); the total is both;
--   - ITBIS billed counts only ITBIS effects, so selective tax, other taxes and the legal tip never enter it (E-GAS-06-3);
--   - fields 20, 21 and 22 carry the selective tax, the other taxes (CDT) and the legal tip of NCF records (E-GAS-06-4).
-- TAX-606 also compares those three with their accounts (E-GAS-06-5).
CREATE OR REPLACE FUNCTION tax.report_606(p_company uuid, p_month date)
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
           coalesce((SELECT sum(l.net_amount) FROM pur.supplier_invoice_line l JOIN pur.expense_category c ON c.expense_category_id = l.expense_category_id
                     WHERE l.si_id = si.si_id AND c.line_class = 'SERVICE'), 0) AS services,
           coalesce((SELECT sum(l.net_amount) FROM pur.supplier_invoice_line l LEFT JOIN pur.expense_category c ON c.expense_category_id = l.expense_category_id
                     WHERE l.si_id = si.si_id AND c.line_class IS DISTINCT FROM 'SERVICE'), 0) AS goods,
           largest.item_category AS category,
           largest.expense_type,
           ap.open_amount,
           (SELECT max(pm.value_date)
            FROM fin.ap_application a JOIN fin.payment pm ON pm.payment_id = a.payment_id
            WHERE a.ap_doc_id = ap.ap_doc_id AND a.reverses_application_id IS NULL
              AND NOT EXISTS (SELECT 1 FROM fin.ap_application r WHERE r.reverses_application_id = a.application_id)) AS paid_on
    FROM pur.supplier_invoice si
    JOIN md.party p ON p.party_id = si.party_id
    LEFT JOIN fin.ap_document ap ON ap.doc_type = 'SUPPLIER_INVOICE' AND ap.source_doc_id = si.si_id
    LEFT JOIN LATERAL (
      SELECT i.item_category, c.goods_type_606 AS expense_type
      FROM pur.supplier_invoice_line l
      LEFT JOIN pur.purchase_order_line pl ON pl.po_line_id = l.po_line_id
      LEFT JOIN md.item i ON i.item_id = pl.item_id
      LEFT JOIN pur.expense_category c ON c.expense_category_id = l.expense_category_id
      WHERE l.si_id = si.si_id ORDER BY l.net_amount DESC, l.line_no LIMIT 1) largest ON true
    WHERE si.company_id = p_company AND si.accounting_status = 'POSTED'
  ),
  taxes AS (
    SELECT d.determination_id,
           coalesce(sum(d.amount) FILTER (WHERE d.effect IN ('RECOVERABLE_INPUT', 'NON_RECOVERABLE_INPUT')), 0) AS itbis,
           coalesce(sum(d.amount) FILTER (WHERE d.effect = 'NON_RECOVERABLE_INPUT'), 0) AS itbis_cost,
           coalesce(sum(d.amount) FILTER (WHERE d.effect = 'WITHHOLDING' AND v.definition ->> 'base' = 'ITBIS'), 0) AS itbis_withheld,
           coalesce(sum(d.amount) FILTER (WHERE d.effect = 'WITHHOLDING' AND v.definition ->> 'base' = 'NET'), 0) AS isr_withheld,
           min(v.definition ->> 'isr_withholding_type') FILTER (WHERE d.effect = 'WITHHOLDING' AND v.definition ->> 'base' = 'NET') AS isr_type,
           coalesce(sum(d.amount) FILTER (WHERE d.effect = 'SELECTIVE_TAX'), 0) AS selective,
           coalesce(sum(d.amount) FILTER (WHERE d.effect = 'OTHER_TAX'), 0) AS other,
           coalesce(sum(d.amount) FILTER (WHERE d.effect = 'LEGAL_TIP'), 0) AS tip
    FROM tax.tax_determination_line d JOIN tax.fiscal_rule_version v ON v.rule_version_id = d.rule_version_id
    WHERE d.company_id = p_company
    GROUP BY d.determination_id
  ),
  records AS (
    SELECT i.*, t.itbis, t.itbis_cost, t.itbis_withheld, t.isr_withheld, t.isr_type, t.selective, t.other, t.tip,
           coalesce(i.expense_type,
                    (SELECT v.definition -> 'classes' ->> i.category
                     FROM tax.fiscal_rule_version v JOIN tax.fiscal_rule r ON r.rule_id = v.rule_id
                     WHERE r.company_id = p_company AND r.rule_kind = 'REPORT_606_CLASSIFICATION' AND v.status = 'ACTIVE'
                       AND v.effective_from <= i.doc_date AND (v.effective_to IS NULL OR v.effective_to > i.doc_date))) AS goods_type,
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
         r.services::numeric(19,2),
         r.goods::numeric(19,2),
         (r.services + r.goods)::numeric(19,2),
         (CASE WHEN r.kind = 'NCF' THEN coalesce(r.itbis, 0) ELSE 0 END)::numeric(19,2),
         (CASE WHEN r.settled_in_month THEN coalesce(r.itbis_withheld, 0) ELSE 0 END)::numeric(19,2),
         0::numeric(19,2),
         (CASE WHEN r.kind = 'NCF' THEN coalesce(r.itbis_cost, 0) ELSE 0 END)::numeric(19,2),
         (CASE WHEN r.kind = 'NCF' THEN coalesce(r.itbis, 0) - coalesce(r.itbis_cost, 0) ELSE 0 END)::numeric(19,2),
         0::numeric(19,2),
         CASE WHEN r.settled_in_month AND coalesce(r.isr_withheld, 0) > 0 THEN r.isr_type END,
         (CASE WHEN r.settled_in_month THEN coalesce(r.isr_withheld, 0) ELSE 0 END)::numeric(19,2),
         0::numeric(19,2),
         (CASE WHEN r.kind = 'NCF' THEN coalesce(r.selective, 0) ELSE 0 END)::numeric(19,2),
         (CASE WHEN r.kind = 'NCF' THEN coalesce(r.other, 0) ELSE 0 END)::numeric(19,2),
         (CASE WHEN r.kind = 'NCF' THEN coalesce(r.tip, 0) ELSE 0 END)::numeric(19,2),
         CASE WHEN r.settled_in_month THEN 2 ELSE 4 END,
         array_remove(ARRAY[
           CASE WHEN r.goods_type IS NULL THEN 'CLASSIFICATION_MISSING' END,
           CASE WHEN r.settled_in_month AND coalesce(r.isr_withheld, 0) > 0 AND r.isr_type IS NULL THEN 'ISR_WITHHOLDING_TYPE_MISSING' END], NULL)
  FROM records r
  ORDER BY r.kind, r.doc_date, r.ncf
$$;

INSERT INTO rec.recon_classification (classification, name, guidance) VALUES
  ('TAX606_SELECTIVE_DIFFERENCE', 'Selectivo del 606 distinto del contabilizado',
   'El impuesto selectivo al consumo del 606 del mes no coincide con la cuenta de gasto de selectivo contabilizada en el mes. Revise las facturas de gastos contabilizadas en un mes distinto al de su comprobante y los ajustes manuales a esa cuenta.'),
  ('TAX606_OTHER_DIFFERENCE', 'Otros impuestos del 606 distintos de lo contabilizado',
   'Los otros impuestos y tasas (CDT) del 606 del mes no coinciden con la cuenta de otros impuestos contabilizada en el mes.'),
  ('TAX606_TIP_DIFFERENCE', 'Propina del 606 distinta de la contabilizada',
   'La propina legal del 606 del mes no coincide con la cuenta de propinas contabilizada en el mes.');

UPDATE rec.recon_definition SET guidance = guidance || ' También compara el selectivo, los otros impuestos y la propina del 606 con sus cuentas.'
WHERE recon_code = 'TAX-606';
