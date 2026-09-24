-- PR-11 · Receipt correction (quantity). Frozen Baseline §9.2, §11.3, Errata E-8 §5.4, rules R-03a/b, Patch 1 (P-1, P-5),
-- ADR-027, approved errata E-PR11-1…5.

-- Lets a correction prove that its line belongs to its receipt with a plain FK.
ALTER TABLE pur.goods_receipt_line ADD CONSTRAINT goods_receipt_line_gr_pair_uq UNIQUE (gr_line_id, gr_id);

-- ---------------------------------------------------------------------------------------------
-- Corrections: several per receipt; DRAFT (under materiality) or PENDING_APPROVAL (above) → POSTED | REJECTED.
-- delta_qty in the PO line UOM. evidence_object_key is a mandatory text reference in VS#1 (E-PR11-1).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE pur.receipt_correction (
  rc_id                uuid                  NOT NULL,
  company_id           uuid                  NOT NULL,
  gr_id                uuid                  NOT NULL,
  gr_line_id           uuid                  NOT NULL,
  delta_qty            numeric(18,6)         NOT NULL,
  reason               text                  NOT NULL,
  evidence_object_key  text                  NOT NULL,
  document_status      text                  NOT NULL,
  accounting_status    fin.accounting_status NOT NULL,
  created_by           uuid                  NOT NULL,
  approved_by          uuid,
  posting_event_id     uuid,
  version              bigint                NOT NULL,
  CONSTRAINT receipt_correction_pk PRIMARY KEY (rc_id),
  CONSTRAINT receipt_correction_company_uq UNIQUE (company_id, rc_id),
  CONSTRAINT receipt_correction_gr_fk FOREIGN KEY (company_id, gr_id) REFERENCES pur.goods_receipt (company_id, gr_id),
  CONSTRAINT receipt_correction_line_fk FOREIGN KEY (company_id, gr_line_id) REFERENCES pur.goods_receipt_line (company_id, gr_line_id),
  CONSTRAINT receipt_correction_line_of_receipt_fk FOREIGN KEY (gr_line_id, gr_id) REFERENCES pur.goods_receipt_line (gr_line_id, gr_id),
  CONSTRAINT receipt_correction_event_fk FOREIGN KEY (company_id, posting_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT receipt_correction_created_by_fk FOREIGN KEY (created_by) REFERENCES iam.user (user_id),
  CONSTRAINT receipt_correction_approved_by_fk FOREIGN KEY (approved_by) REFERENCES iam.user (user_id),
  CONSTRAINT receipt_correction_delta_nonzero CHECK (delta_qty <> 0),
  CONSTRAINT receipt_correction_reason CHECK (length(btrim(reason)) > 0),
  CONSTRAINT receipt_correction_evidence CHECK (length(btrim(evidence_object_key)) > 0),
  CONSTRAINT receipt_correction_status CHECK (document_status IN ('DRAFT', 'PENDING_APPROVAL', 'POSTED', 'REJECTED')),
  CONSTRAINT receipt_correction_approver_not_creator CHECK (approved_by IS NULL OR approved_by <> created_by),
  CONSTRAINT receipt_correction_posted_event CHECK (document_status <> 'POSTED' OR (posting_event_id IS NOT NULL AND approved_by IS NOT NULL)),
  CONSTRAINT receipt_correction_accounting_vs1 CHECK (
    (document_status = 'POSTED' AND accounting_status = 'POSTED') OR
    (document_status <> 'POSTED' AND accounting_status = 'NOT_POSTED')),
  CONSTRAINT receipt_correction_version_positive CHECK (version >= 1)
);

CREATE FUNCTION pur.receipt_correction_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'UPDATE' THEN
    RAISE EXCEPTION 'pur.receipt_correction rows cannot be deleted; reject the correction';
  END IF;
  IF ROW(NEW.rc_id, NEW.company_id, NEW.gr_id, NEW.gr_line_id, NEW.delta_qty, NEW.reason, NEW.evidence_object_key, NEW.created_by)
     IS DISTINCT FROM ROW(OLD.rc_id, OLD.company_id, OLD.gr_id, OLD.gr_line_id, OLD.delta_qty, OLD.reason, OLD.evidence_object_key, OLD.created_by) THEN
    RAISE EXCEPTION 'pur.receipt_correction: identity columns are immutable';
  END IF;
  IF NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'pur.receipt_correction: version must increase by exactly 1';
  END IF;
  IF NEW.document_status IS DISTINCT FROM OLD.document_status AND NOT (
       (OLD.document_status = 'DRAFT' AND NEW.document_status = 'POSTED') OR
       (OLD.document_status = 'PENDING_APPROVAL' AND NEW.document_status IN ('POSTED', 'REJECTED'))) THEN
    RAISE EXCEPTION 'pur.receipt_correction: transition % → % is not allowed (§11.3)', OLD.document_status, NEW.document_status;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER receipt_correction_guard BEFORE UPDATE OR DELETE ON pur.receipt_correction FOR EACH ROW EXECUTE FUNCTION pur.receipt_correction_guard();
CREATE TRIGGER receipt_correction_no_truncate BEFORE TRUNCATE ON pur.receipt_correction FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- K-25 (POSTED needs its AUTO journal) and ADR-027 (every status needs its history row in the same transaction).
CREATE FUNCTION pur.receipt_correction_evidence() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.accounting_status = 'POSTED' AND NOT EXISTS (
    SELECT 1 FROM fin.gl_journal WHERE company_id = NEW.company_id AND source_event_id = NEW.posting_event_id AND journal_type = 'AUTO') THEN
    RAISE EXCEPTION 'pur.receipt_correction %: accounting_status POSTED without its journal (K-25)', NEW.rc_id;
  END IF;
  IF NOT EXISTS (
    SELECT 1 FROM core.state_history
    WHERE aggregate_type = 'ReceiptCorrection' AND aggregate_id = NEW.rc_id AND to_state = NEW.document_status AND xmin = pg_current_xact_id()::xid) THEN
    RAISE EXCEPTION 'pur.receipt_correction %: status % without its state_history row (ADR-027)', NEW.rc_id, NEW.document_status;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER receipt_correction_evidence_on_insert AFTER INSERT ON pur.receipt_correction
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION pur.receipt_correction_evidence();
CREATE CONSTRAINT TRIGGER receipt_correction_evidence_on_change AFTER UPDATE ON pur.receipt_correction
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.document_status IS DISTINCT FROM NEW.document_status)
  EXECUTE FUNCTION pur.receipt_correction_evidence();

-- ---------------------------------------------------------------------------------------------
-- Rules R-03a (Δq > 0) and R-03b (Δq < 0), DRAFT until the Controller approves them (E-PR11-5).
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type) VALUES
  ('0192f001-0000-7000-8000-000000000003', 'R-03A', 'ReceiptCorrectionPosted'),
  ('0192f001-0000-7000-8000-000000000004', 'R-03B', 'ReceiptCorrectionPosted');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, effective_from, status) VALUES
  ('0192f001-0000-7000-8000-000000000003', 1,
   '{"lines": [
      {"code": "R03A-DR-INV", "side": "DEBIT", "account_role": "RAW_MATERIAL", "amount": "correction_value", "dimensions": ["plant", "item"], "subledger": "INV"},
      {"code": "R03A-CR-GRNI", "side": "CREDIT", "account_role": "GRNI", "amount": "correction_value", "dimensions": ["plant", "party"]}
    ]}',
   '{"R03A-DR-INV": "Corrección de recepción {rc_id}: se recibió más de lo registrado; entra Δq × precio de la OC al inventario.",
     "R03A-CR-GRNI": "Corrección de recepción {rc_id}: aumenta lo recibido pendiente de factura (GRNI)."}',
   'INV-MOV', DATE '2026-01-01', 'DRAFT'),
  ('0192f001-0000-7000-8000-000000000004', 1,
   '{"lines": [
      {"code": "R03B-DR-GRNI", "side": "DEBIT", "account_role": "GRNI", "amount": "grni_value", "dimensions": ["plant", "party"]},
      {"code": "R03B-CR-INV", "side": "CREDIT", "account_role": "RAW_MATERIAL", "amount": "stock_value", "dimensions": ["plant", "item"], "subledger": "INV"},
      {"code": "R03B-CR-PPV", "side": "CREDIT", "account_role": "PURCHASE_PRICE_VARIANCE", "amount": "price_variance", "dimensions": ["plant", "item"]},
      {"code": "R03B-DR-PPV", "side": "DEBIT", "account_role": "PURCHASE_PRICE_VARIANCE", "amount": "price_variance", "dimensions": ["plant", "item"]},
      {"code": "R03B-CR-MUV", "side": "CREDIT", "account_role": "MATERIAL_USAGE_VARIANCE", "amount": "usage_variance", "dimensions": ["plant", "item"]}
    ]}',
   '{"R03B-DR-GRNI": "Corrección de recepción {rc_id}: se registró más de lo recibido; se reduce GRNI por |Δq| × precio de la OC.",
     "R03B-CR-INV": "Corrección de recepción {rc_id}: sale del inventario la parte aún existente (q₁) al costo promedio.",
     "R03B-CR-PPV": "Corrección de recepción {rc_id}: diferencia entre precio de la OC y costo promedio de q₁.",
     "R03B-DR-PPV": "Corrección de recepción {rc_id}: diferencia entre costo promedio y precio de la OC de q₁.",
     "R03B-CR-MUV": "Corrección de recepción {rc_id}: parte ya consumida en libros (q₂); el consumo estuvo sobrestimado."}',
   'INV-MOV', DATE '2026-01-01', 'DRAFT');

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE pur.receipt_correction ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON pur.receipt_correction
  USING (company_id = nullif(current_setting('app.company_id', true), '')::uuid)
  WITH CHECK (company_id = nullif(current_setting('app.company_id', true), '')::uuid);
GRANT SELECT, INSERT ON pur.receipt_correction TO rochell_app;
GRANT UPDATE (document_status, accounting_status, approved_by, posting_event_id, version) ON pur.receipt_correction TO rochell_app;
