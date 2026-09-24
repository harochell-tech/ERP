-- PR-09 · Goods receipt. Frozen Baseline §9.2, §11.2, rule R-01 (§13), Patch 1 (P-1, P-5, K-25), ADR-027,
-- approved errata E-PR07-1…4, E-PR09-1…6.

CREATE TYPE pur.gr_status AS ENUM ('POSTED', 'CORRECTED', 'REVERSED');
CREATE TYPE fin.accounting_status AS ENUM ('NOT_POSTED', 'POSTED', 'POSTING_BLOCKED', 'REVERSED');

-- ---------------------------------------------------------------------------------------------
-- Header. gr_no readable, not sequential (E-PR09-3). Weigh ticket optional, unique while active (E-PR09-5).
-- P-1: a goods receipt is always POSTED (no POSTING_BLOCKED in VS#1) until reversed.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE pur.goods_receipt (
  gr_id              uuid                  NOT NULL,
  company_id         uuid                  NOT NULL,
  gr_no              text                  NOT NULL,
  po_id              uuid                  NOT NULL,
  location_id        uuid                  NOT NULL,
  weigh_ticket_ref   text,
  document_status    pur.gr_status         NOT NULL,
  accounting_status  fin.accounting_status NOT NULL,
  posting_event_id   uuid                  NOT NULL,
  occurred_at        timestamptz           NOT NULL,
  version            bigint                NOT NULL,
  CONSTRAINT goods_receipt_pk PRIMARY KEY (gr_id),
  CONSTRAINT goods_receipt_company_uq UNIQUE (company_id, gr_id),
  CONSTRAINT goods_receipt_no_uq UNIQUE (company_id, gr_no),
  CONSTRAINT goods_receipt_po_fk FOREIGN KEY (company_id, po_id) REFERENCES pur.purchase_order (company_id, po_id),
  CONSTRAINT goods_receipt_location_fk FOREIGN KEY (company_id, location_id) REFERENCES md.location (company_id, location_id),
  CONSTRAINT goods_receipt_event_fk FOREIGN KEY (company_id, posting_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT goods_receipt_no_format CHECK (gr_no ~ '^RM-[0-9]{4}-[0-9A-F]{8}$'),
  CONSTRAINT goods_receipt_ticket_present CHECK (weigh_ticket_ref IS NULL OR length(btrim(weigh_ticket_ref)) > 0),
  CONSTRAINT goods_receipt_accounting_vs1 CHECK (accounting_status IN ('POSTED', 'REVERSED')),
  CONSTRAINT goods_receipt_reversal_pair CHECK ((document_status = 'REVERSED') = (accounting_status = 'REVERSED')),
  CONSTRAINT goods_receipt_version_positive CHECK (version >= 1)
);
CREATE UNIQUE INDEX gr_ticket_uq ON pur.goods_receipt (company_id, weigh_ticket_ref)
  WHERE weigh_ticket_ref IS NOT NULL AND document_status <> 'REVERSED';

-- ---------------------------------------------------------------------------------------------
-- Lines: quantity and price in the PO line UOM (E-PR08-4); one lot per line (E-PR07-3). Append-only.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE pur.goods_receipt_line (
  gr_line_id  uuid          NOT NULL,
  company_id  uuid          NOT NULL,
  gr_id       uuid          NOT NULL,
  po_line_id  uuid          NOT NULL,
  lot_id      uuid          NOT NULL,
  qty         numeric(18,6) NOT NULL,
  unit_price  numeric(19,6) NOT NULL,
  CONSTRAINT goods_receipt_line_pk PRIMARY KEY (gr_line_id),
  CONSTRAINT goods_receipt_line_company_uq UNIQUE (company_id, gr_line_id),
  CONSTRAINT goods_receipt_line_po_line_uq UNIQUE (gr_id, po_line_id),
  CONSTRAINT goods_receipt_line_lot_uq UNIQUE (lot_id),
  CONSTRAINT goods_receipt_line_gr_fk FOREIGN KEY (company_id, gr_id) REFERENCES pur.goods_receipt (company_id, gr_id),
  CONSTRAINT goods_receipt_line_po_line_fk FOREIGN KEY (company_id, po_line_id) REFERENCES pur.purchase_order_line (company_id, po_line_id),
  CONSTRAINT goods_receipt_line_lot_fk FOREIGN KEY (company_id, lot_id) REFERENCES inv.lot (company_id, lot_id),
  CONSTRAINT goods_receipt_line_qty_positive CHECK (qty > 0),
  CONSTRAINT goods_receipt_line_price_positive CHECK (unit_price > 0)
);

-- Consistency across documents: location in the PO's plant; lines of the receipt's own PO.
CREATE FUNCTION pur.goods_receipt_before_insert() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pur.purchase_order po JOIN md.location l ON l.plant_id = po.plant_id
    WHERE po.po_id = NEW.po_id AND l.location_id = NEW.location_id) THEN
    RAISE EXCEPTION 'pur.goods_receipt: location % is not in the plant of purchase order %', NEW.location_id, NEW.po_id;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER goods_receipt_before_insert BEFORE INSERT ON pur.goods_receipt FOR EACH ROW EXECUTE FUNCTION pur.goods_receipt_before_insert();

CREATE FUNCTION pur.goods_receipt_line_before_insert() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pur.goods_receipt gr JOIN pur.purchase_order_line pol ON pol.po_id = gr.po_id
    WHERE gr.gr_id = NEW.gr_id AND pol.po_line_id = NEW.po_line_id) THEN
    RAISE EXCEPTION 'pur.goods_receipt_line: PO line % does not belong to the purchase order of the receipt', NEW.po_line_id;
  END IF;
  IF NOT EXISTS (
    SELECT 1 FROM pur.purchase_order_line pol JOIN inv.lot lot ON lot.item_id = pol.item_id
    WHERE pol.po_line_id = NEW.po_line_id AND lot.lot_id = NEW.lot_id) THEN
    RAISE EXCEPTION 'pur.goods_receipt_line: lot % is not of the item of PO line %', NEW.lot_id, NEW.po_line_id;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER goods_receipt_line_before_insert BEFORE INSERT ON pur.goods_receipt_line FOR EACH ROW EXECUTE FUNCTION pur.goods_receipt_line_before_insert();

-- Header: identity immutable, version +1, status transitions of §11.2, never deleted. Lines append-only.
CREATE FUNCTION pur.goods_receipt_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'UPDATE' THEN
    RAISE EXCEPTION 'pur.goods_receipt rows cannot be deleted; reverse the receipt';
  END IF;
  IF ROW(NEW.gr_id, NEW.company_id, NEW.gr_no, NEW.po_id, NEW.location_id, NEW.weigh_ticket_ref, NEW.posting_event_id, NEW.occurred_at)
     IS DISTINCT FROM ROW(OLD.gr_id, OLD.company_id, OLD.gr_no, OLD.po_id, OLD.location_id, OLD.weigh_ticket_ref, OLD.posting_event_id, OLD.occurred_at) THEN
    RAISE EXCEPTION 'pur.goods_receipt: identity columns are immutable';
  END IF;
  IF NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'pur.goods_receipt: version must increase by exactly 1';
  END IF;
  IF NEW.document_status IS DISTINCT FROM OLD.document_status AND NOT (
       (OLD.document_status IN ('POSTED', 'CORRECTED') AND NEW.document_status IN ('CORRECTED', 'REVERSED'))) THEN
    RAISE EXCEPTION 'pur.goods_receipt: transition % → % is not allowed (§11.2)', OLD.document_status, NEW.document_status;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER goods_receipt_guard BEFORE UPDATE OR DELETE ON pur.goods_receipt FOR EACH ROW EXECUTE FUNCTION pur.goods_receipt_guard();
CREATE TRIGGER goods_receipt_no_truncate BEFORE TRUNCATE ON pur.goods_receipt FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER goods_receipt_line_append_only BEFORE UPDATE OR DELETE ON pur.goods_receipt_line FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER goods_receipt_line_no_truncate BEFORE TRUNCATE ON pur.goods_receipt_line FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- K-25: accounting_status POSTED needs its journal (source event = posting event); ADR-027: status needs its history row.
CREATE FUNCTION pur.goods_receipt_evidence() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.accounting_status = 'POSTED' AND NOT EXISTS (
    SELECT 1 FROM fin.gl_journal WHERE company_id = NEW.company_id AND source_event_id = NEW.posting_event_id AND journal_type = 'AUTO') THEN
    RAISE EXCEPTION 'pur.goods_receipt %: accounting_status POSTED without its journal (K-25)', NEW.gr_id;
  END IF;
  IF NOT EXISTS (
    SELECT 1 FROM core.state_history
    WHERE aggregate_type = 'GoodsReceipt' AND aggregate_id = NEW.gr_id AND to_state = NEW.document_status::text
      AND xmin = pg_current_xact_id()::xid) THEN
    RAISE EXCEPTION 'pur.goods_receipt %: status % without its state_history row (ADR-027)', NEW.gr_id, NEW.document_status;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER goods_receipt_evidence_on_insert AFTER INSERT ON pur.goods_receipt
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION pur.goods_receipt_evidence();
CREATE CONSTRAINT TRIGGER goods_receipt_evidence_on_change AFTER UPDATE ON pur.goods_receipt
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.document_status IS DISTINCT FROM NEW.document_status)
  EXECUTE FUNCTION pur.goods_receipt_evidence();

-- ---------------------------------------------------------------------------------------------
-- Rule R-01 (E-PR09-1, E-PR09-2): DRAFT, approved by the Controller in the application.
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type)
VALUES ('0192f001-0000-7000-8000-000000000001', 'R-01', 'GoodsReceiptPosted');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, effective_from, status)
VALUES ('0192f001-0000-7000-8000-000000000001', 1,
  '{"lines": [
     {"code": "R01-DR-INV", "side": "DEBIT", "account_role": "RAW_MATERIAL", "amount": "receipt_value", "dimensions": ["plant", "item"], "subledger": "INV"},
     {"code": "R01-CR-GRNI", "side": "CREDIT", "account_role": "GRNI", "amount": "receipt_value", "dimensions": ["plant", "party"]}
   ]}',
  '{"R01-DR-INV": "Entrada de materia prima al inventario por la recepción {gr_no}, línea de la orden de compra {po_no}: cantidad recibida × precio de la OC.",
    "R01-CR-GRNI": "Mercancía recibida pendiente de factura del proveedor (GRNI) por la recepción {gr_no}."}',
  'INV-MOV', DATE '2026-01-01', 'DRAFT');

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['pur.goods_receipt', 'pur.goods_receipt_line'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON pur.goods_receipt, pur.goods_receipt_line TO rochell_app;
GRANT UPDATE (document_status, accounting_status, version) ON pur.goods_receipt TO rochell_app;
