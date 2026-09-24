-- PR-08 · Purchase order. Frozen Baseline §9.2, §11.1, Patch 1 (P-5, K-13, K-14), Patch 1.1 P-1 (price > 0),
-- ADR-027 (state history enforced), approved errata E-PR08-2…5.

CREATE SCHEMA pur;
REVOKE ALL ON SCHEMA pur FROM PUBLIC;
GRANT USAGE ON SCHEMA pur TO rochell_app;

CREATE TYPE pur.po_status AS ENUM ('DRAFT', 'PENDING_APPROVAL', 'APPROVED', 'PARTIALLY_RECEIVED', 'RECEIVED', 'CLOSED', 'CANCELLED');

-- E-PR08-2: approval limit of the purchasing approver (the Controller has none) and step-up threshold.
INSERT INTO acc.policy_parameter_definition (param_code, policy_code, value_type, min_value, max_value, allowed_values, description) VALUES
  ('po_approval_limit', 'PURCHASING', 'AMOUNT', 0, 999999999999999.9999, NULL, 'Total máximo de OC que aprueba el Aprobador de compras (DOP)'),
  ('po_approval_step_up_threshold', 'PURCHASING', 'AMOUNT', 0, 999999999999999.9999, NULL, 'Total de OC a partir del cual aprobar exige reautenticación (DOP)');

-- ---------------------------------------------------------------------------------------------
-- Header. po_no is readable but not sequential (E-PR08-3, ADR-029).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE pur.purchase_order (
  po_id              uuid          NOT NULL,
  company_id         uuid          NOT NULL,
  po_no              text          NOT NULL,
  party_id           uuid          NOT NULL,
  plant_id           uuid          NOT NULL,
  order_date         date          NOT NULL,
  revision           integer       NOT NULL DEFAULT 1,
  status             pur.po_status NOT NULL,
  created_by         uuid          NOT NULL,
  approved_by        uuid,
  approved_at        timestamptz,
  policy_version_id  uuid,
  version            bigint        NOT NULL,
  CONSTRAINT purchase_order_pk PRIMARY KEY (po_id),
  CONSTRAINT purchase_order_company_uq UNIQUE (company_id, po_id),
  CONSTRAINT purchase_order_no_uq UNIQUE (company_id, po_no),
  CONSTRAINT purchase_order_party_fk FOREIGN KEY (company_id, party_id) REFERENCES md.party (company_id, party_id),
  CONSTRAINT purchase_order_plant_fk FOREIGN KEY (company_id, plant_id) REFERENCES md.plant (company_id, plant_id),
  CONSTRAINT purchase_order_created_by_fk FOREIGN KEY (created_by) REFERENCES iam.user (user_id),
  CONSTRAINT purchase_order_approved_by_fk FOREIGN KEY (approved_by) REFERENCES iam.user (user_id),
  CONSTRAINT purchase_order_policy_fk FOREIGN KEY (company_id, policy_version_id) REFERENCES acc.accounting_policy_version (company_id, policy_version_id),
  CONSTRAINT purchase_order_approver_not_creator CHECK (approved_by IS NULL OR approved_by <> created_by),
  CONSTRAINT purchase_order_approved_states CHECK (
    status NOT IN ('APPROVED', 'PARTIALLY_RECEIVED', 'RECEIVED', 'CLOSED')
    OR (approved_by IS NOT NULL AND approved_at IS NOT NULL AND policy_version_id IS NOT NULL)),
  CONSTRAINT purchase_order_no_format CHECK (po_no ~ '^OC-[0-9]{4}-[0-9A-F]{8}$'),
  CONSTRAINT purchase_order_revision_positive CHECK (revision >= 1),
  CONSTRAINT purchase_order_version_positive CHECK (version >= 1)
);

-- ---------------------------------------------------------------------------------------------
-- Lines. Quantities and price in the line UOM (E-PR08-4); tolerance copied from policy on approval.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE pur.purchase_order_line (
  po_line_id                 uuid          NOT NULL,
  company_id                 uuid          NOT NULL,
  po_id                      uuid          NOT NULL,
  line_no                    integer       NOT NULL,
  item_id                    uuid          NOT NULL,
  uom                        text          NOT NULL,
  qty_ordered                numeric(18,6) NOT NULL,
  unit_price                 numeric(19,6) NOT NULL,
  receipt_tolerance_pct      numeric(9,6)  NOT NULL,
  qty_over_receipt_approved  numeric(18,6) NOT NULL DEFAULT 0,
  qty_received               numeric(18,6) NOT NULL DEFAULT 0,
  qty_invoiced               numeric(18,6) NOT NULL DEFAULT 0,
  version                    bigint        NOT NULL,
  CONSTRAINT purchase_order_line_pk PRIMARY KEY (po_line_id),
  CONSTRAINT purchase_order_line_company_uq UNIQUE (company_id, po_line_id),
  CONSTRAINT purchase_order_line_no_uq UNIQUE (po_id, line_no),
  CONSTRAINT purchase_order_line_po_fk FOREIGN KEY (company_id, po_id) REFERENCES pur.purchase_order (company_id, po_id),
  CONSTRAINT purchase_order_line_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT purchase_order_line_uom_fk FOREIGN KEY (uom) REFERENCES md.uom (uom_code),
  CONSTRAINT purchase_order_line_qty_positive CHECK (qty_ordered > 0),
  CONSTRAINT purchase_order_line_price_positive CHECK (unit_price > 0),
  CONSTRAINT purchase_order_line_tolerance CHECK (receipt_tolerance_pct >= 0),
  CONSTRAINT purchase_order_line_over_receipt CHECK (qty_over_receipt_approved >= 0),
  CONSTRAINT purchase_order_line_received CHECK (
    qty_received >= 0 AND qty_received <= qty_ordered * (1 + receipt_tolerance_pct) + qty_over_receipt_approved),
  CONSTRAINT purchase_order_line_invoiced CHECK (qty_invoiced >= 0 AND qty_invoiced <= qty_received),
  CONSTRAINT purchase_order_line_line_no_positive CHECK (line_no >= 1),
  CONSTRAINT purchase_order_line_version_positive CHECK (version >= 1)
);

-- ---------------------------------------------------------------------------------------------
-- Guards: identity immutable, version +1, valid transitions (§11.1), no deletes; lines only
-- deleted while the order is DRAFT (UpdatePurchaseOrderDraft, E-PR08-5).
-- ---------------------------------------------------------------------------------------------
CREATE FUNCTION pur.purchase_order_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'UPDATE' THEN
    RAISE EXCEPTION 'pur.purchase_order rows cannot be deleted; cancel the order';
  END IF;
  IF ROW(NEW.po_id, NEW.company_id, NEW.po_no, NEW.party_id, NEW.plant_id, NEW.order_date, NEW.created_by)
     IS DISTINCT FROM ROW(OLD.po_id, OLD.company_id, OLD.po_no, OLD.party_id, OLD.plant_id, OLD.order_date, OLD.created_by) THEN
    RAISE EXCEPTION 'pur.purchase_order: identity columns are immutable';
  END IF;
  IF NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'pur.purchase_order: version must increase by exactly 1';
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
       (OLD.status = 'DRAFT' AND NEW.status IN ('PENDING_APPROVAL', 'CANCELLED')) OR
       (OLD.status = 'PENDING_APPROVAL' AND NEW.status IN ('APPROVED', 'DRAFT', 'CANCELLED')) OR
       (OLD.status = 'APPROVED' AND NEW.status IN ('PARTIALLY_RECEIVED', 'RECEIVED', 'CANCELLED')) OR
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
CREATE TRIGGER purchase_order_guard BEFORE UPDATE OR DELETE ON pur.purchase_order FOR EACH ROW EXECUTE FUNCTION pur.purchase_order_guard();
CREATE TRIGGER purchase_order_no_truncate BEFORE TRUNCATE ON pur.purchase_order FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

CREATE FUNCTION pur.purchase_order_line_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    IF (SELECT status FROM pur.purchase_order WHERE po_id = OLD.po_id) <> 'DRAFT' THEN
      RAISE EXCEPTION 'pur.purchase_order_line: lines can only be removed while the order is DRAFT';
    END IF;
    RETURN OLD;
  END IF;
  IF TG_OP = 'INSERT' THEN
    IF (SELECT status FROM pur.purchase_order WHERE po_id = NEW.po_id) <> 'DRAFT' THEN
      RAISE EXCEPTION 'pur.purchase_order_line: lines can only be added while the order is DRAFT';
    END IF;
    RETURN NEW;
  END IF;
  IF ROW(NEW.po_line_id, NEW.company_id, NEW.po_id, NEW.line_no, NEW.item_id, NEW.uom, NEW.qty_ordered, NEW.unit_price)
     IS DISTINCT FROM ROW(OLD.po_line_id, OLD.company_id, OLD.po_id, OLD.line_no, OLD.item_id, OLD.uom, OLD.qty_ordered, OLD.unit_price) THEN
    RAISE EXCEPTION 'pur.purchase_order_line: item, UOM, quantity ordered and price are immutable';
  END IF;
  IF NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'pur.purchase_order_line: version must increase by exactly 1';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER purchase_order_line_guard BEFORE INSERT OR UPDATE OR DELETE ON pur.purchase_order_line FOR EACH ROW EXECUTE FUNCTION pur.purchase_order_line_guard();
CREATE TRIGGER purchase_order_line_no_truncate BEFORE TRUNCATE ON pur.purchase_order_line FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ADR-027: every status (on insert and on change) needs its core.state_history row in the same transaction.
CREATE FUNCTION pur.purchase_order_state_recorded() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM core.state_history
    WHERE aggregate_type = 'PurchaseOrder' AND aggregate_id = NEW.po_id AND to_state = NEW.status::text
      AND xmin = pg_current_xact_id()::xid) THEN
    RAISE EXCEPTION 'pur.purchase_order %: status % without its state_history row (ADR-027)', NEW.po_id, NEW.status;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER purchase_order_state_on_insert AFTER INSERT ON pur.purchase_order
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION pur.purchase_order_state_recorded();
CREATE CONSTRAINT TRIGGER purchase_order_state_on_change AFTER UPDATE ON pur.purchase_order
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION pur.purchase_order_state_recorded();

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['pur.purchase_order', 'pur.purchase_order_line'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON pur.purchase_order TO rochell_app;
GRANT UPDATE (status, approved_by, approved_at, policy_version_id, version) ON pur.purchase_order TO rochell_app;
GRANT SELECT, INSERT, DELETE ON pur.purchase_order_line TO rochell_app;
GRANT UPDATE (receipt_tolerance_pct, qty_over_receipt_approved, qty_received, qty_invoiced, version) ON pur.purchase_order_line TO rochell_app;
