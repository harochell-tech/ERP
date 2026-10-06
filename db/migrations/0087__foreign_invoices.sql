-- USD1-03 · Foreign supplier orders and invoices in USD (approved errata E-USD1-03-1…8).
--   - an expense category may point to a fixed-asset account (ASSET, not control) when its 606 type is 04 (E-USD1-03-6);
--   - an expense line of a USD order or invoice carries no tax type; one in pesos always does (E-USD1-03-3);
--   - P-38 «Factura del exterior», seeded DRAFT: each line to its category's account, the payable to AP_FOREIGN with its USD amount
--     (E-USD1-03-5);
--   - the 606 leaves out USD invoices: they have no NCF; the import reaches the 606 through its customs declaration (E-USD1-03-7).

-- ---------------------------------------------------------------------------------------------
-- Categories of fixed assets (E-USD1-03-6).
-- ---------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION pur.expense_category_account_valid(account uuid, goods_type text) RETURNS boolean
  LANGUAGE sql STABLE AS $$
  SELECT EXISTS (SELECT 1 FROM fin.account a WHERE a.account_id = account AND NOT a.is_control AND a.status = 'ACTIVE'
                   AND (a.account_class = 'EXPENSE' OR (a.account_class = 'ASSET' AND goods_type = '04')))
$$;

CREATE OR REPLACE FUNCTION pur.expense_category_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'pur.expense_category rows cannot be deleted; make the category INACTIVE';
  END IF;
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'DRAFT' OR NEW.version <> 1 OR NEW.approved_by IS NOT NULL THEN
      RAISE EXCEPTION 'pur.expense_category: a category is prepared DRAFT, version 1, without approval';
    END IF;
    IF NOT pur.expense_category_account_valid(NEW.account_id, NEW.goods_type_606) THEN
      RAISE EXCEPTION 'pur.expense_category: the account must be an ACTIVE expense account, or a fixed-asset account with 606 type 04, that is not a control account (E-GAS-2, E-USD1-03-6)';
    END IF;
    RETURN NEW;
  END IF;
  IF ROW(NEW.expense_category_id, NEW.company_id, NEW.code, NEW.account_id, NEW.prepared_by)
     IS DISTINCT FROM ROW(OLD.expense_category_id, OLD.company_id, OLD.code, OLD.account_id, OLD.prepared_by) THEN
    RAISE EXCEPTION 'pur.expense_category: code and account are immutable (E-GAS-01-3)';
  END IF;
  IF OLD.status <> 'DRAFT' AND ROW(NEW.name, NEW.goods_type_606, NEW.line_class) IS DISTINCT FROM ROW(OLD.name, OLD.goods_type_606, OLD.line_class) THEN
    RAISE EXCEPTION 'pur.expense_category: name, 606 type and class change only while DRAFT (E-GAS-01-3)';
  END IF;
  IF NEW.goods_type_606 IS DISTINCT FROM OLD.goods_type_606
     AND (SELECT account_class FROM fin.account WHERE account_id = NEW.account_id) = 'ASSET' AND NEW.goods_type_606 <> '04' THEN
    RAISE EXCEPTION 'pur.expense_category: a fixed-asset category keeps 606 type 04 (E-USD1-03-6)';
  END IF;
  IF NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'pur.expense_category: version must increase by exactly 1';
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
       (OLD.status = 'DRAFT' AND NEW.status IN ('ACTIVE', 'INACTIVE')) OR
       (OLD.status = 'ACTIVE' AND NEW.status = 'INACTIVE') OR
       (OLD.status = 'INACTIVE' AND NEW.status = 'ACTIVE' AND OLD.approved_by IS NOT NULL)) THEN
    RAISE EXCEPTION 'pur.expense_category: transition % → % is not allowed', OLD.status, NEW.status;
  END IF;
  IF OLD.approved_by IS NOT NULL AND NEW.approved_by IS DISTINCT FROM OLD.approved_by THEN
    RAISE EXCEPTION 'pur.expense_category: the approval is immutable';
  END IF;
  IF NEW.status = 'ACTIVE' AND NEW.approved_by IS NULL THEN
    RAISE EXCEPTION 'pur.expense_category: an ACTIVE category has who approved it';
  END IF;
  RETURN NEW;
END $$;

-- ---------------------------------------------------------------------------------------------
-- Expense lines in USD carry no tax type (E-USD1-03-3).
-- ---------------------------------------------------------------------------------------------
ALTER TABLE pur.purchase_order_line
  DROP CONSTRAINT purchase_order_line_shape,
  ADD CONSTRAINT purchase_order_line_shape CHECK (
    (item_id IS NOT NULL AND uom IS NOT NULL AND description IS NULL AND expense_category_id IS NULL AND tax_rule_id IS NULL)
    OR (item_id IS NULL AND uom IS NULL AND description IS NOT NULL AND length(btrim(description)) BETWEEN 1 AND 200 AND description = btrim(description)
        AND expense_category_id IS NOT NULL AND qty_received = 0 AND qty_over_receipt_approved = 0));

ALTER TABLE pur.supplier_invoice_line
  DROP CONSTRAINT supplier_invoice_line_shape,
  ADD CONSTRAINT supplier_invoice_line_shape CHECK (
    (line_kind = 'INVENTORY_PO' AND po_line_id IS NOT NULL AND description IS NULL AND expense_category_id IS NULL AND tax_rule_id IS NULL)
    OR (line_kind = 'EXPENSE' AND description IS NOT NULL AND length(btrim(description)) BETWEEN 1 AND 200 AND description = btrim(description)
        AND expense_category_id IS NOT NULL));

-- A USD invoice has no tax determination (no ITBIS or withholding, E-USD-3); its lines' peso net is the USD net at the invoice's rate,
-- with the rounding cent on the largest line (E-USD1-03-4), so only the USD net is the product of quantity and price.
ALTER TABLE pur.supplier_invoice
  DROP CONSTRAINT supplier_invoice_posted_evidence,
  ADD CONSTRAINT supplier_invoice_posted_evidence CHECK (
    accounting_status <> 'POSTED' OR (posting_event_id IS NOT NULL AND (tax_determination_id IS NOT NULL OR currency = 'USD')));

ALTER TABLE pur.supplier_invoice_line
  DROP CONSTRAINT supplier_invoice_line_net,
  ADD CONSTRAINT supplier_invoice_line_net CHECK (
    (net_amount_fc IS NULL AND net_amount = round(qty * unit_price, 2))
    OR (net_amount_fc IS NOT NULL AND net_amount_fc = round(qty * unit_price_fc, 2) AND net_amount > 0));

CREATE OR REPLACE FUNCTION pur.expense_line_valid(company uuid, category uuid, tax_rule uuid) RETURNS boolean
  LANGUAGE sql STABLE AS $$
  SELECT EXISTS (SELECT 1 FROM pur.expense_category c WHERE c.company_id = company AND c.expense_category_id = category AND c.status = 'ACTIVE')
     AND (tax_rule IS NULL OR EXISTS (SELECT 1 FROM tax.fiscal_rule r WHERE r.company_id = company AND r.rule_id = tax_rule AND r.rule_kind = 'PURCHASE_TAX_TYPE'))
$$;

CREATE OR REPLACE FUNCTION pur.purchase_order_line_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  o record;
BEGIN
  IF TG_OP = 'DELETE' THEN
    IF (SELECT status FROM pur.purchase_order WHERE po_id = OLD.po_id) <> 'DRAFT' THEN
      RAISE EXCEPTION 'pur.purchase_order_line: lines can only be removed while the order is DRAFT';
    END IF;
    RETURN OLD;
  END IF;
  IF TG_OP = 'INSERT' THEN
    SELECT status, doc_class, currency INTO o FROM pur.purchase_order WHERE po_id = NEW.po_id;
    IF o.status <> 'DRAFT' THEN
      RAISE EXCEPTION 'pur.purchase_order_line: lines can only be added while the order is DRAFT';
    END IF;
    IF (o.doc_class = 'EXPENSE') <> (NEW.item_id IS NULL) THEN
      RAISE EXCEPTION 'pur.purchase_order_line: an order is of inventory or of expenses; it does not mix lines (E-GAS-01-1)';
    END IF;
    IF NEW.item_id IS NULL AND NOT pur.expense_line_valid(NEW.company_id, NEW.expense_category_id, NEW.tax_rule_id) THEN
      RAISE EXCEPTION 'pur.purchase_order_line: an expense line needs an ACTIVE category and a tax type';
    END IF;
    -- E-USD1-03-3: a USD expense line has no tax type; a peso one always has it.
    IF NEW.item_id IS NULL AND (NEW.tax_rule_id IS NULL) <> (o.currency = 'USD') THEN
      RAISE EXCEPTION 'pur.purchase_order_line: an expense line in USD has no tax type, one in pesos has one (E-USD1-03-3)';
    END IF;
    RETURN NEW;
  END IF;
  IF ROW(NEW.po_line_id, NEW.company_id, NEW.po_id, NEW.line_no, NEW.item_id, NEW.uom, NEW.qty_ordered, NEW.unit_price, NEW.description, NEW.expense_category_id, NEW.tax_rule_id)
     IS DISTINCT FROM ROW(OLD.po_line_id, OLD.company_id, OLD.po_id, OLD.line_no, OLD.item_id, OLD.uom, OLD.qty_ordered, OLD.unit_price, OLD.description, OLD.expense_category_id,
                          OLD.tax_rule_id) THEN
    RAISE EXCEPTION 'pur.purchase_order_line: item, UOM, quantity ordered and price are immutable';
  END IF;
  IF NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'pur.purchase_order_line: version must increase by exactly 1';
  END IF;
  RETURN NEW;
END $$;

CREATE OR REPLACE FUNCTION pur.supplier_invoice_line_before_insert() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  si record;
  pol record;
BEGIN
  SELECT party_id, document_status, doc_class, currency INTO si FROM pur.supplier_invoice WHERE si_id = NEW.si_id;
  IF si.document_status IS DISTINCT FROM 'DRAFT' THEN
    RAISE EXCEPTION 'pur.supplier_invoice_line: PO line % is not of the invoice supplier, or the invoice is not DRAFT', NEW.po_line_id;
  END IF;
  IF (si.doc_class = 'EXPENSE') <> (NEW.line_kind = 'EXPENSE') THEN
    RAISE EXCEPTION 'pur.supplier_invoice_line: an invoice is of inventory or of expenses; it does not mix lines (E-GAS-01-1)';
  END IF;
  IF NEW.po_line_id IS NOT NULL THEN
    SELECT po.party_id, po.doc_class, l.expense_category_id, l.tax_rule_id INTO pol
    FROM pur.purchase_order_line l JOIN pur.purchase_order po ON po.po_id = l.po_id WHERE l.po_line_id = NEW.po_line_id;
    IF pol.party_id IS DISTINCT FROM si.party_id OR pol.doc_class IS DISTINCT FROM si.doc_class THEN
      RAISE EXCEPTION 'pur.supplier_invoice_line: PO line % is not of the invoice supplier, or the invoice is not DRAFT', NEW.po_line_id;
    END IF;
    IF NEW.line_kind = 'EXPENSE' AND ROW(NEW.expense_category_id, NEW.tax_rule_id) IS DISTINCT FROM ROW(pol.expense_category_id, pol.tax_rule_id) THEN
      RAISE EXCEPTION 'pur.supplier_invoice_line: an expense line bills its order line with the order''s category and tax type';
    END IF;
  END IF;
  IF NEW.line_kind = 'EXPENSE' AND NOT pur.expense_line_valid(NEW.company_id, NEW.expense_category_id, NEW.tax_rule_id) THEN
    RAISE EXCEPTION 'pur.supplier_invoice_line: an expense line needs an ACTIVE category and a tax type';
  END IF;
  -- E-USD1-03-3/4: a USD invoice has expense lines without tax type and with their USD price and net; a peso one never has USD amounts.
  IF (si.currency = 'USD') <> (NEW.net_amount_fc IS NOT NULL) OR (si.currency = 'USD' AND (NEW.line_kind <> 'EXPENSE' OR NEW.tax_rule_id IS NOT NULL))
     OR (si.currency = 'DOP' AND NEW.line_kind = 'EXPENSE' AND NEW.tax_rule_id IS NULL) THEN
    RAISE EXCEPTION 'pur.supplier_invoice_line: a USD invoice has expense lines in USD without tax type; a peso invoice has no USD amounts (E-USD1-03-3)';
  END IF;
  RETURN NEW;
END $$;

-- ---------------------------------------------------------------------------------------------
-- P-38 (E-USD1-03-5): the foreign invoice. Seeded DRAFT; the Controller approves it with the AP_FOREIGN map (A-01). Its reversal is the
-- exact inverse of its journal.
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type) VALUES
  ('0192f001-0000-7000-8000-000000000030', 'P-38', 'ForeignInvoicePosted');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, effective_from, status) VALUES
  ('0192f001-0000-7000-8000-000000000030', 1,
   '{"lines": [
      {"code": "P38-DR-EXP", "side": "DEBIT", "account_role": "PURCHASE_EXPENSE", "amount": "expense_net", "dimensions": ["plant", "party"]},
      {"code": "P38-CR-AP", "side": "CREDIT", "account_role": "AP_FOREIGN", "amount": "payable", "dimensions": ["party"], "subledger": "AP"}
    ]}',
   '{"P38-DR-EXP": "Factura del exterior {number}: {category} ({description}), USD {amount_usd} a la tasa {rate}.",
     "P38-CR-AP": "Factura del exterior {number}: cuenta por pagar al proveedor, USD {amount_usd} a la tasa {rate}."}',
   'AP-REC', DATE '2026-01-01', 'DRAFT');

-- ---------------------------------------------------------------------------------------------
-- The 606 leaves out invoices in USD (E-USD1-03-7); otherwise as in 0080.
-- ---------------------------------------------------------------------------------------------
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
    WHERE si.company_id = p_company AND si.accounting_status = 'POSTED' AND si.currency = 'DOP'
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

-- ---------------------------------------------------------------------------------------------
-- The foreign supplier (E-USD1-03-9): its country (ISO 3166-1 alpha-2) and, optionally, its tax id abroad; no RNC. One supplier per
-- country and foreign tax id.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE md.party
  ADD COLUMN country char(2),
  ADD COLUMN foreign_tax_id text,
  ADD CONSTRAINT party_country CHECK ((party_kind = 'FOREIGN') = (country IS NOT NULL)),
  ADD CONSTRAINT party_country_format CHECK (country IS NULL OR country ~ '^[A-Z]{2}$'),
  ADD CONSTRAINT party_foreign_tax_id CHECK (foreign_tax_id IS NULL OR (party_kind = 'FOREIGN' AND length(foreign_tax_id) BETWEEN 1 AND 40
    AND foreign_tax_id = btrim(foreign_tax_id)));
CREATE UNIQUE INDEX party_foreign_tax_id_uq ON md.party (company_id, country, foreign_tax_id) WHERE foreign_tax_id IS NOT NULL;
GRANT UPDATE (country, foreign_tax_id) ON md.party TO rochell_app;
