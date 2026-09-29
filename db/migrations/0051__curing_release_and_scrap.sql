-- MFG1-04 · Curing release, block and unblock, finished-goods scrap (P-12). Frozen Baseline MFG-1 §4–§6; approved errata E-MFG1-8,
-- E-MFG1-13 and E-MFG1-04-1…7.

-- ---------------------------------------------------------------------------------------------
-- E-MFG1-04-1/3: who released the lot, where to and when; the reason of the last block.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE mfg.fg_lot
  ADD COLUMN released_by uuid,
  ADD COLUMN released_at timestamptz,
  ADD COLUMN released_to uuid,
  ADD COLUMN block_reason text,
  ADD CONSTRAINT fg_lot_released_by_fk FOREIGN KEY (released_by) REFERENCES iam.user (user_id),
  ADD CONSTRAINT fg_lot_released_to_fk FOREIGN KEY (company_id, released_to) REFERENCES md.location (company_id, location_id),
  ADD CONSTRAINT fg_lot_release_data CHECK ((released_by IS NULL) = (released_at IS NULL) AND (released_by IS NULL) = (released_to IS NULL)),
  ADD CONSTRAINT fg_lot_block_reason CHECK ((status = 'BLOCKED') = (block_reason IS NOT NULL));

-- Lot lifecycle (E-MFG1-8, E-MFG1-03-9): CURING → RELEASED | BLOCKED | SCRAPPED | VOIDED; BLOCKED → CURING | SCRAPPED; RELEASED → SCRAPPED.
CREATE FUNCTION mfg.fg_lot_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF ROW(NEW.lot_id, NEW.company_id, NEW.summary_id, NEW.run_id, NEW.curing_from, NEW.releasable_at)
     IS DISTINCT FROM ROW(OLD.lot_id, OLD.company_id, OLD.summary_id, OLD.run_id, OLD.curing_from, OLD.releasable_at)
     OR NEW.version <> OLD.version + 1
     OR NOT ((OLD.status = 'CURING' AND NEW.status IN ('RELEASED', 'BLOCKED', 'SCRAPPED', 'VOIDED'))
             OR (OLD.status = 'BLOCKED' AND NEW.status IN ('CURING', 'SCRAPPED'))
             OR (OLD.status = 'RELEASED' AND NEW.status = 'SCRAPPED')) THEN
    RAISE EXCEPTION 'mfg.fg_lot: % → % or a change of the lot''s identity is not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER fg_lot_guard BEFORE UPDATE ON mfg.fg_lot FOR EACH ROW EXECUTE FUNCTION mfg.fg_lot_guard();

GRANT UPDATE (released_by, released_at, released_to, block_reason) ON mfg.fg_lot TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- E-MFG1-13, E-MFG1-04-4: finished goods scrapped in curing (CURING) or in the yard (YARD), at the area's valuation cost.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE mfg.lot_scrap (
  scrap_id      uuid          NOT NULL,
  company_id    uuid          NOT NULL,
  lot_id        uuid          NOT NULL,
  location_id   uuid          NOT NULL,
  point         text          NOT NULL,
  qty           numeric(18,6) NOT NULL,
  value         numeric(19,4) NOT NULL,
  reason        text          NOT NULL,
  event_id      uuid          NOT NULL,
  scrapped_by   uuid          NOT NULL,
  CONSTRAINT lot_scrap_pk PRIMARY KEY (scrap_id),
  CONSTRAINT lot_scrap_lot_fk FOREIGN KEY (company_id, lot_id) REFERENCES mfg.fg_lot (company_id, lot_id),
  CONSTRAINT lot_scrap_location_fk FOREIGN KEY (company_id, location_id) REFERENCES md.location (company_id, location_id),
  CONSTRAINT lot_scrap_by_fk FOREIGN KEY (scrapped_by) REFERENCES iam.user (user_id),
  CONSTRAINT lot_scrap_point CHECK (point IN ('CURING', 'YARD')),
  CONSTRAINT lot_scrap_qty CHECK (qty > 0 AND value > 0),
  CONSTRAINT lot_scrap_reason CHECK (length(btrim(reason)) > 0)
);
CREATE TRIGGER lot_scrap_immutable BEFORE UPDATE OR DELETE ON mfg.lot_scrap FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- E-MFG1-04-4: P-12 ScrapRecorded — Dr PRODUCTION_SCRAP / Cr FINISHED_GOODS at the area's valuation cost. DRAFT until A-01.
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type)
VALUES ('0192f001-0000-7000-8000-000000000026', 'P-12', 'ScrapRecorded');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, also_requires_components, effective_from, status)
VALUES ('0192f001-0000-7000-8000-000000000026', 1,
   '{"lines": [
      {"code": "P12-DR-SCRAP", "side": "DEBIT", "account_role": "PRODUCTION_SCRAP", "amount": "scrap_value", "dimensions": ["plant"]},
      {"code": "P12-CR-FG", "side": "CREDIT", "account_role": "FINISHED_GOODS", "amount": "scrap_value", "dimensions": ["plant", "item"], "subledger": "INV"}
    ]}',
   '{"P12-DR-SCRAP": "Scrap de {quantity} unidades del lote {lot_code} en {point}: {reason}.",
     "P12-CR-FG": "Baja de producto terminado del lote {lot_code} a costo de valuación del área."}',
   'INV-MOV', ARRAY[]::text[], DATE '2026-01-01', 'DRAFT');

ALTER TABLE mfg.lot_scrap ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON mfg.lot_scrap USING (company_id = nullif(current_setting('app.company_id', true), '')::uuid)
  WITH CHECK (company_id = nullif(current_setting('app.company_id', true), '')::uuid);
GRANT SELECT, INSERT ON mfg.lot_scrap TO rochell_app;
