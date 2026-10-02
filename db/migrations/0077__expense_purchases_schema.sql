-- GAS1-01 · Purchases of expenses and services (approved errata E-GAS-1…12, E-GAS-01-1…11): purchase orders and supplier invoices
-- take expense lines — a free description, a quantity, a price, an expense category and a tax type — that never go through the
-- warehouse. Raw materials keep their registered item, their receipt and their automatic ITBIS. Schema only: the commands come in
-- the next PRs, so nothing here changes how today's documents behave (every existing row is of class INVENTORY).

-- ---------------------------------------------------------------------------------------------
-- Tax types (E-GAS-3, E-GAS-9, E-GAS-01-5): a fiscal rule kind of which several are in force at once, one per code, each with its
-- components (ITBIS, selective tax, other taxes, legal tip). The new effects are what is not recoverable and goes to its own
-- expense account and 606 column (E-GAS-10).
-- ---------------------------------------------------------------------------------------------
ALTER TABLE tax.fiscal_rule DROP CONSTRAINT fiscal_rule_kind,
  ADD CONSTRAINT fiscal_rule_kind CHECK (rule_kind IN (
    'PURCHASE_ITBIS', 'PURCHASE_WITHHOLDING', 'SALES_ITBIS', 'REPORT_606_CLASSIFICATION', 'CONSUMER_ID_THRESHOLD', 'PURCHASE_TAX_TYPE'));
ALTER TABLE tax.tax_determination_line DROP CONSTRAINT tax_determination_line_effect,
  ADD CONSTRAINT tax_determination_line_effect CHECK (effect IN (
    'RECOVERABLE_INPUT', 'NON_RECOVERABLE_INPUT', 'WITHHOLDING', 'OUTPUT', 'SELECTIVE_TAX', 'OTHER_TAX', 'LEGAL_TIP'));

-- ---------------------------------------------------------------------------------------------
-- Account roles (E-GAS-10): where selective tax, other taxes (CDT) and the legal tip are expensed, and the technical role of an
-- expense line, whose account is its category's and is never mapped.
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.account_role (role_code, is_control, description, name) VALUES
  ('SELECTIVE_TAX_EXPENSE', false, 'Impuesto selectivo al consumo pagado en compras de gastos y servicios (no acreditable)', 'Gasto de impuesto selectivo al consumo'),
  ('OTHER_TAX_EXPENSE', false, 'Otros impuestos y tasas pagados en compras de gastos y servicios (CDT y similares)', 'Gasto por otros impuestos y tasas'),
  ('LEGAL_TIP_EXPENSE', false, 'Propina legal pagada en compras de gastos y servicios', 'Gasto de propina legal'),
  ('PURCHASE_EXPENSE', false, 'Línea de gasto de una factura de proveedor (P-37); rol técnico: la cuenta es la de la categoría de gasto, nunca se mapea',
   'Gasto de la categoría (factura de gastos)');

CREATE FUNCTION fin.account_role_map_not_expense() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.account_role = 'PURCHASE_EXPENSE' THEN
    RAISE EXCEPTION 'fin.account_role_map: PURCHASE_EXPENSE is a technical role and is never mapped; the account is the expense category''s (E-GAS-2)';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER account_role_map_not_expense BEFORE INSERT ON fin.account_role_map
  FOR EACH ROW EXECUTE FUNCTION fin.account_role_map_not_expense();

-- ---------------------------------------------------------------------------------------------
-- Expense categories (E-GAS-2, E-GAS-11, E-GAS-01-3, E-GAS-01-4): what whoever registers chooses instead of an account. Prepared
-- by one person and approved by another; once ACTIVE its account, 606 type and class never change — it is made INACTIVE and
-- another is created, so what was posted keeps its meaning.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE pur.expense_category (
  expense_category_id  uuid   NOT NULL,
  company_id           uuid   NOT NULL,
  code                 text   NOT NULL,
  name                 text   NOT NULL,
  account_id           uuid   NOT NULL,
  goods_type_606       text   NOT NULL,
  line_class           text   NOT NULL,
  status               text   NOT NULL,
  prepared_by          uuid   NOT NULL,
  approved_by          uuid,
  version              bigint NOT NULL,
  CONSTRAINT expense_category_pk PRIMARY KEY (expense_category_id),
  CONSTRAINT expense_category_company_uq UNIQUE (company_id, expense_category_id),
  CONSTRAINT expense_category_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT expense_category_account_fk FOREIGN KEY (company_id, account_id) REFERENCES fin.account (company_id, account_id),
  CONSTRAINT expense_category_prepared_by_fk FOREIGN KEY (prepared_by) REFERENCES iam.user (user_id),
  CONSTRAINT expense_category_approved_by_fk FOREIGN KEY (approved_by) REFERENCES iam.user (user_id),
  CONSTRAINT expense_category_code_format CHECK (code ~ '^[A-Z][A-Z0-9_]{1,39}$'),
  CONSTRAINT expense_category_name CHECK (length(btrim(name)) BETWEEN 1 AND 100 AND name = btrim(name)),
  CONSTRAINT expense_category_goods_type CHECK (goods_type_606 ~ '^(0[1-9]|1[01])$'),
  CONSTRAINT expense_category_line_class CHECK (line_class IN ('SERVICE', 'GOODS')),
  CONSTRAINT expense_category_status CHECK (status IN ('DRAFT', 'ACTIVE', 'INACTIVE')),
  CONSTRAINT expense_category_approval CHECK (status = 'DRAFT' OR approved_by IS NOT NULL OR status = 'INACTIVE'),
  CONSTRAINT expense_category_version_positive CHECK (version >= 1)
);
-- A code names one category in use; an INACTIVE one frees its code for its replacement.
CREATE UNIQUE INDEX expense_category_code_uq ON pur.expense_category (company_id, code) WHERE status <> 'INACTIVE';

CREATE FUNCTION pur.expense_category_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  account record;
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'pur.expense_category rows cannot be deleted; make the category INACTIVE';
  END IF;
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'DRAFT' OR NEW.version <> 1 OR NEW.approved_by IS NOT NULL THEN
      RAISE EXCEPTION 'pur.expense_category: a category is prepared DRAFT, version 1, without approval';
    END IF;
    SELECT a.is_control, a.status, a.account_class INTO account FROM fin.account a WHERE a.account_id = NEW.account_id;
    IF account.is_control OR account.status <> 'ACTIVE' OR account.account_class IS DISTINCT FROM 'EXPENSE' THEN
      RAISE EXCEPTION 'pur.expense_category: the account must be an ACTIVE expense account that is not a control account (E-GAS-2)';
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
CREATE TRIGGER expense_category_guard BEFORE INSERT OR UPDATE OR DELETE ON pur.expense_category FOR EACH ROW EXECUTE FUNCTION pur.expense_category_guard();
CREATE TRIGGER expense_category_no_truncate BEFORE TRUNCATE ON pur.expense_category FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
-- E-GAS-01-4: who approves is not who prepared (waivable only by a superadministrator, E-ADM-2).
CREATE TRIGGER expense_category_four_eyes BEFORE INSERT OR UPDATE ON pur.expense_category
  FOR EACH ROW EXECUTE FUNCTION core.four_eyes('approved_by', 'prepared_by', 'expense_category_four_eyes');
CREATE CONSTRAINT TRIGGER expense_category_evidence_on_insert AFTER INSERT ON pur.expense_category
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('ExpenseCategory', 'expense_category_id');
CREATE CONSTRAINT TRIGGER expense_category_evidence_on_change AFTER UPDATE ON pur.expense_category
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('ExpenseCategory', 'expense_category_id');

ALTER TABLE pur.expense_category ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON pur.expense_category
  USING (company_id = nullif(current_setting('app.company_id', true), '')::uuid)
  WITH CHECK (company_id = nullif(current_setting('app.company_id', true), '')::uuid);
GRANT SELECT, INSERT ON pur.expense_category TO rochell_app;
GRANT UPDATE (name, goods_type_606, line_class, status, approved_by, version) ON pur.expense_category TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- Purchase order (E-GAS-1, E-GAS-01-1, E-GAS-01-2, E-GAS-01-10): the class of the document, and the expense line — description,
-- category and tax type instead of item and unit. An expense order is never received: it is CLOSED once billed in full, or by
-- Compras with a reason.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE pur.purchase_order
  ADD COLUMN doc_class text NOT NULL DEFAULT 'INVENTORY',
  ADD CONSTRAINT purchase_order_doc_class CHECK (doc_class IN ('INVENTORY', 'EXPENSE')),
  ADD CONSTRAINT purchase_order_expense_not_received CHECK (doc_class = 'INVENTORY' OR status NOT IN ('PARTIALLY_RECEIVED', 'RECEIVED'));

CREATE OR REPLACE FUNCTION pur.purchase_order_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'UPDATE' THEN
    RAISE EXCEPTION 'pur.purchase_order rows cannot be deleted; cancel the order';
  END IF;
  IF ROW(NEW.po_id, NEW.company_id, NEW.po_no, NEW.party_id, NEW.plant_id, NEW.order_date, NEW.created_by, NEW.doc_class)
     IS DISTINCT FROM ROW(OLD.po_id, OLD.company_id, OLD.po_no, OLD.party_id, OLD.plant_id, OLD.order_date, OLD.created_by, OLD.doc_class) THEN
    RAISE EXCEPTION 'pur.purchase_order: identity columns are immutable';
  END IF;
  IF NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'pur.purchase_order: version must increase by exactly 1';
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
       (OLD.status = 'DRAFT' AND NEW.status IN ('PENDING_APPROVAL', 'CANCELLED')) OR
       (OLD.status = 'PENDING_APPROVAL' AND NEW.status IN ('APPROVED', 'DRAFT', 'CANCELLED')) OR
       (OLD.status = 'APPROVED' AND NEW.status IN ('PARTIALLY_RECEIVED', 'RECEIVED', 'CANCELLED')) OR
       -- E-GAS-01-10: an expense order goes from APPROVED to CLOSED, and back if its invoice is reversed.
       (NEW.doc_class = 'EXPENSE' AND ((OLD.status = 'APPROVED' AND NEW.status = 'CLOSED') OR (OLD.status = 'CLOSED' AND NEW.status = 'APPROVED'))) OR
       (OLD.status = 'PARTIALLY_RECEIVED' AND NEW.status IN ('RECEIVED', 'APPROVED', 'CLOSED')) OR
       (OLD.status = 'RECEIVED' AND NEW.status IN ('PARTIALLY_RECEIVED', 'APPROVED', 'CLOSED'))) THEN
    RAISE EXCEPTION 'pur.purchase_order: transition % → % is not allowed (§11.1)', OLD.status, NEW.status;
  END IF;
  IF OLD.approved_by IS NOT NULL AND ROW(NEW.approved_by, NEW.approved_at, NEW.policy_version_id)
     IS DISTINCT FROM ROW(OLD.approved_by, OLD.approved_at, OLD.policy_version_id) THEN
    RAISE EXCEPTION 'pur.purchase_order: approval data is immutable';
  END IF;
  RETURN NEW;
END $$;

ALTER TABLE pur.purchase_order_line
  ALTER COLUMN item_id DROP NOT NULL,
  ALTER COLUMN uom DROP NOT NULL,
  ADD COLUMN description text,
  ADD COLUMN expense_category_id uuid,
  ADD COLUMN tax_rule_id uuid,
  ADD CONSTRAINT purchase_order_line_category_fk FOREIGN KEY (company_id, expense_category_id) REFERENCES pur.expense_category (company_id, expense_category_id),
  ADD CONSTRAINT purchase_order_line_tax_rule_fk FOREIGN KEY (company_id, tax_rule_id) REFERENCES tax.fiscal_rule (company_id, rule_id),
  -- An inventory line has its item and unit; an expense line its description, category and tax type, and is never received.
  ADD CONSTRAINT purchase_order_line_shape CHECK (
    (item_id IS NOT NULL AND uom IS NOT NULL AND description IS NULL AND expense_category_id IS NULL AND tax_rule_id IS NULL)
    OR (item_id IS NULL AND uom IS NULL AND description IS NOT NULL AND length(btrim(description)) BETWEEN 1 AND 200 AND description = btrim(description)
        AND expense_category_id IS NOT NULL AND tax_rule_id IS NOT NULL AND qty_received = 0 AND qty_over_receipt_approved = 0)),
  DROP CONSTRAINT purchase_order_line_invoiced,
  ADD CONSTRAINT purchase_order_line_invoiced CHECK (qty_invoiced >= 0 AND qty_invoiced <= CASE WHEN item_id IS NULL THEN qty_ordered ELSE qty_received END);

-- An expense line names an ACTIVE category and a tax type (a PURCHASE_TAX_TYPE rule); the class of the line is the class of its
-- document (E-GAS-01-1).
CREATE FUNCTION pur.expense_line_valid(company uuid, category uuid, tax_rule uuid) RETURNS boolean
  LANGUAGE sql STABLE AS $$
  SELECT EXISTS (SELECT 1 FROM pur.expense_category c WHERE c.company_id = company AND c.expense_category_id = category AND c.status = 'ACTIVE')
     AND EXISTS (SELECT 1 FROM tax.fiscal_rule r WHERE r.company_id = company AND r.rule_id = tax_rule AND r.rule_kind = 'PURCHASE_TAX_TYPE')
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
    SELECT status, doc_class INTO o FROM pur.purchase_order WHERE po_id = NEW.po_id;
    IF o.status <> 'DRAFT' THEN
      RAISE EXCEPTION 'pur.purchase_order_line: lines can only be added while the order is DRAFT';
    END IF;
    IF (o.doc_class = 'EXPENSE') <> (NEW.item_id IS NULL) THEN
      RAISE EXCEPTION 'pur.purchase_order_line: an order is of inventory or of expenses; it does not mix lines (E-GAS-01-1)';
    END IF;
    IF NEW.item_id IS NULL AND NOT pur.expense_line_valid(NEW.company_id, NEW.expense_category_id, NEW.tax_rule_id) THEN
      RAISE EXCEPTION 'pur.purchase_order_line: an expense line needs an ACTIVE category and a tax type';
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

-- ---------------------------------------------------------------------------------------------
-- Supplier invoice (E-GAS-1, E-GAS-5, E-GAS-6, E-GAS-01-8): the class of the document; an expense invoice names its plant. Its
-- lines carry description, category and tax type, with the order line they bill when there is an order.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE pur.supplier_invoice
  ADD COLUMN doc_class text NOT NULL DEFAULT 'INVENTORY',
  ADD COLUMN plant_id uuid,
  ADD CONSTRAINT supplier_invoice_doc_class CHECK (doc_class IN ('INVENTORY', 'EXPENSE')),
  ADD CONSTRAINT supplier_invoice_plant_fk FOREIGN KEY (company_id, plant_id) REFERENCES md.plant (company_id, plant_id),
  ADD CONSTRAINT supplier_invoice_expense_plant CHECK ((doc_class = 'EXPENSE') = (plant_id IS NOT NULL));

CREATE FUNCTION pur.supplier_invoice_class_immutable() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF ROW(NEW.doc_class, NEW.plant_id) IS DISTINCT FROM ROW(OLD.doc_class, OLD.plant_id) THEN
    RAISE EXCEPTION 'pur.supplier_invoice: class and plant are immutable';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER supplier_invoice_class_immutable BEFORE UPDATE ON pur.supplier_invoice FOR EACH ROW EXECUTE FUNCTION pur.supplier_invoice_class_immutable();

ALTER TABLE pur.supplier_invoice_line
  ALTER COLUMN po_line_id DROP NOT NULL,
  ADD COLUMN description text,
  ADD COLUMN expense_category_id uuid,
  ADD COLUMN tax_rule_id uuid,
  ADD CONSTRAINT supplier_invoice_line_category_fk FOREIGN KEY (company_id, expense_category_id) REFERENCES pur.expense_category (company_id, expense_category_id),
  ADD CONSTRAINT supplier_invoice_line_tax_rule_fk FOREIGN KEY (company_id, tax_rule_id) REFERENCES tax.fiscal_rule (company_id, rule_id),
  DROP CONSTRAINT supplier_invoice_line_kind,
  ADD CONSTRAINT supplier_invoice_line_kind CHECK (line_kind IN ('INVENTORY_PO', 'EXPENSE')),
  ADD CONSTRAINT supplier_invoice_line_shape CHECK (
    (line_kind = 'INVENTORY_PO' AND po_line_id IS NOT NULL AND description IS NULL AND expense_category_id IS NULL AND tax_rule_id IS NULL)
    OR (line_kind = 'EXPENSE' AND description IS NOT NULL AND length(btrim(description)) BETWEEN 1 AND 200 AND description = btrim(description)
        AND expense_category_id IS NOT NULL AND tax_rule_id IS NOT NULL));

-- A line is added only to a DRAFT invoice and is of its class. With an order line: of the invoice's supplier and of the same
-- class, and an expense line bills it with the order line's own category and tax type.
CREATE OR REPLACE FUNCTION pur.supplier_invoice_line_before_insert() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  si record;
  pol record;
BEGIN
  SELECT party_id, document_status, doc_class INTO si FROM pur.supplier_invoice WHERE si_id = NEW.si_id;
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
  RETURN NEW;
END $$;

-- ---------------------------------------------------------------------------------------------
-- P-37 (E-GAS-10, baseline §3): the expense invoice. Seeded DRAFT; the Controller approves it (A-01). Its reversal is the exact
-- inverse of its journal, as R-07 is of R-04.
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type) VALUES
  ('0192f001-0000-7000-8000-000000000029', 'P-37', 'ExpenseInvoicePosted');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, effective_from, status) VALUES
  ('0192f001-0000-7000-8000-000000000029', 1,
   '{"lines": [
      {"code": "P37-DR-EXP", "side": "DEBIT", "account_role": "PURCHASE_EXPENSE", "amount": "expense_net", "dimensions": ["plant", "party"]},
      {"code": "P37-DR-ITBIS", "side": "DEBIT", "account_role": "ITBIS_RECOVERABLE", "amount": "recoverable_itbis", "dimensions": ["plant"]},
      {"code": "P37-DR-ISC", "side": "DEBIT", "account_role": "SELECTIVE_TAX_EXPENSE", "amount": "selective_tax", "dimensions": ["plant"]},
      {"code": "P37-DR-OTHER", "side": "DEBIT", "account_role": "OTHER_TAX_EXPENSE", "amount": "other_tax", "dimensions": ["plant"]},
      {"code": "P37-DR-TIP", "side": "DEBIT", "account_role": "LEGAL_TIP_EXPENSE", "amount": "legal_tip", "dimensions": ["plant"]},
      {"code": "P37-CR-AP", "side": "CREDIT", "account_role": "AP_CONTROL", "amount": "payable", "dimensions": ["party"], "subledger": "AP"},
      {"code": "P37-CR-WHT", "side": "CREDIT", "account_role": "WITHHOLDING_PAYABLE", "amount": "withholding", "dimensions": ["party"]}
    ]}',
   '{"P37-DR-EXP": "Factura {ncf}: gasto de la categoría {category} ({description}).",
     "P37-DR-ITBIS": "Factura {ncf}: ITBIS adelantado según el tipo de impuesto de sus líneas.",
     "P37-DR-ISC": "Factura {ncf}: impuesto selectivo al consumo, que no se acredita.",
     "P37-DR-OTHER": "Factura {ncf}: otros impuestos y tasas (CDT), que no se acreditan.",
     "P37-DR-TIP": "Factura {ncf}: propina legal.",
     "P37-CR-AP": "Factura {ncf}: cuenta por pagar al proveedor: neto más impuestos, menos retenciones.",
     "P37-CR-WHT": "Factura {ncf}: retención a pagar a la DGII según la regla fiscal activa."}',
   'AP-REC', DATE '2026-01-01', 'DRAFT');

-- ---------------------------------------------------------------------------------------------
-- E-GAS-6, E-GAS-01-9: from this total an expense invoice without a purchase order needs another person's approval.
-- ---------------------------------------------------------------------------------------------
INSERT INTO acc.policy_parameter_definition (param_code, policy_code, value_type, min_value, max_value, allowed_values, description, label, unit, example, affects) VALUES
  ('expense_invoice_approval_threshold', 'PURCHASING', 'AMOUNT', 0, 999999999999999.9999, NULL,
   'Total de una factura de gastos sin orden de compra desde el cual necesita la aprobación de otra persona (DOP)',
   'Aprobación de facturas de gastos sin orden', 'AMOUNT', 'RD$ 25,000.00',
   'Total, con impuestos, desde el cual una factura de gastos sin orden de compra queda pendiente de la aprobación del Controller antes de contabilizarse.');

-- ---------------------------------------------------------------------------------------------
-- E-GAS-01-4, E-GAS-01-11: who prepares and who approves expense categories. SUPERADMIN receives both through its trigger.
-- ---------------------------------------------------------------------------------------------
INSERT INTO iam.permission (permission_code, access) VALUES ('expense_category:prepare', 'WRITE'), ('expense_category:approve', 'WRITE');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES ('CONTADOR', 'expense_category:prepare'), ('CONTROLLER', 'expense_category:prepare'), ('CONTROLLER', 'expense_category:approve')) AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;
