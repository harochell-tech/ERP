-- VS3-02b · Opening finished goods (v2.1 §6, ADR-035). Approved errata E-VS3-02b-1…10 (E-VS3-02b-1 amends E-VS3-01-5: the
-- opening is a command, not a CLI insert).

-- ---------------------------------------------------------------------------------------------
-- E-VS3-02b-7: movement type OPENING (an inflow). As in 0011/0015/0016 the CHECKs compare the enum as text, because the new
-- value cannot be used as a literal in the transaction that adds it.
-- ---------------------------------------------------------------------------------------------
ALTER TYPE inv.movement_type ADD VALUE 'OPENING';

ALTER TABLE inv.inv_quantity_entry DROP CONSTRAINT inv_quantity_entry_sign;
ALTER TABLE inv.inv_quantity_entry ADD CONSTRAINT inv_quantity_entry_sign CHECK (
  (movement_type::text IN ('RECEIPT', 'OPENING') AND quantity > 0) OR
  (movement_type::text IN ('ISSUE', 'RECEIPT_REVERSAL') AND quantity < 0) OR
  movement_type::text = 'RECEIPT_CORRECTION');
ALTER TABLE inv.inv_value_entry DROP CONSTRAINT inv_value_entry_sign;
ALTER TABLE inv.inv_value_entry ADD CONSTRAINT inv_value_entry_sign CHECK (
  (movement_type::text IN ('RECEIPT', 'OPENING') AND amount > 0) OR
  (movement_type::text IN ('ISSUE', 'RECEIPT_REVERSAL') AND amount < 0) OR
  movement_type::text IN ('RECEIPT_CORRECTION', 'VALUATION_ADJUSTMENT', 'VALUATION_REALLOCATION', 'PRICE_ADJUSTMENT', 'REPOST', 'RESIDUAL_ADJUSTMENT'));

-- P-3 for finished goods: the GL side of a valuation position is every inventory role of the item (raw material, finished goods
-- and finished goods in transit), not only RAW_MATERIAL (E-PR19-10 control totals, 0021).
DROP TRIGGER gl_entry_raw_material_accumulate ON fin.gl_entry;
CREATE TRIGGER gl_entry_inventory_accumulate AFTER INSERT ON fin.gl_entry
  FOR EACH ROW WHEN (NEW.account_role IN ('RAW_MATERIAL', 'FINISHED_GOODS', 'FINISHED_GOODS_IN_TRANSIT'))
  EXECUTE FUNCTION inv.accumulate_raw_material_line();

-- ---------------------------------------------------------------------------------------------
-- Migration batches (v2.1 §6.1, E-VS3-02b-2…6, E-VS3-02b-9). In VS#3 one kind: OPENING_INVENTORY of finished goods.
-- DRAFT (Controller) → POSTED (Aprobador de políticas, not the preparer) → REVERSED (whole batch, with a reason).
-- A batch is unique by its file hash among POSTED batches; a line by its source document among live (posted, not reversed) lines.
-- ---------------------------------------------------------------------------------------------
CREATE SCHEMA mig;
GRANT USAGE ON SCHEMA mig TO rochell_app;

CREATE TABLE mig.migration_batch (
  batch_id            uuid    NOT NULL,
  company_id          uuid    NOT NULL,
  kind                text    NOT NULL,
  source_system       text    NOT NULL,
  file_name           text    NOT NULL,
  source_file_sha256  bytea   NOT NULL,
  cutover_date        date    NOT NULL,
  status              text    NOT NULL,
  prepared_by         uuid    NOT NULL,
  posted_by           uuid,
  posting_event_id    uuid,
  reversal_event_id   uuid,
  reversal_reason     text,
  version             bigint  NOT NULL,
  CONSTRAINT migration_batch_pk PRIMARY KEY (batch_id),
  CONSTRAINT migration_batch_company_uq UNIQUE (company_id, batch_id),
  CONSTRAINT migration_batch_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT migration_batch_prepared_fk FOREIGN KEY (prepared_by) REFERENCES iam.user (user_id),
  CONSTRAINT migration_batch_posted_fk FOREIGN KEY (posted_by) REFERENCES iam.user (user_id),
  CONSTRAINT migration_batch_posting_event_fk FOREIGN KEY (company_id, posting_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT migration_batch_reversal_event_fk FOREIGN KEY (company_id, reversal_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT migration_batch_kind CHECK (kind = 'OPENING_INVENTORY'),
  CONSTRAINT migration_batch_source_system CHECK (source_system = 'ADM_CLOUD'),
  CONSTRAINT migration_batch_file_name CHECK (length(btrim(file_name)) BETWEEN 1 AND 200),
  CONSTRAINT migration_batch_hash CHECK (octet_length(source_file_sha256) = 32),
  CONSTRAINT migration_batch_status CHECK (status IN ('DRAFT', 'POSTED', 'REVERSED')),
  CONSTRAINT migration_batch_four_eyes CHECK (posted_by IS NULL OR posted_by <> prepared_by),
  CONSTRAINT migration_batch_posted CHECK ((status IN ('POSTED', 'REVERSED')) = (posted_by IS NOT NULL AND posting_event_id IS NOT NULL)),
  CONSTRAINT migration_batch_reversed CHECK ((status = 'REVERSED') = (reversal_event_id IS NOT NULL AND length(btrim(reversal_reason)) > 0)),
  CONSTRAINT migration_batch_version_positive CHECK (version >= 1)
);
CREATE UNIQUE INDEX migration_batch_posted_file_uq ON mig.migration_batch (company_id, source_file_sha256) WHERE status = 'POSTED';

CREATE TABLE mig.opening_inventory_line (
  line_id                 uuid          NOT NULL,
  company_id              uuid          NOT NULL,
  batch_id                uuid          NOT NULL,
  line_no                 integer       NOT NULL,
  source_document_number  text          NOT NULL,
  plant_id                uuid          NOT NULL,
  location_id             uuid          NOT NULL,
  item_id                 uuid          NOT NULL,
  quantity                numeric(18,6) NOT NULL,
  unit_cost               numeric(19,4) NOT NULL,
  value                   numeric(19,4) NOT NULL,
  lot_id                  uuid,
  live                    boolean       NOT NULL DEFAULT false,
  CONSTRAINT opening_inventory_line_pk PRIMARY KEY (line_id),
  CONSTRAINT opening_inventory_line_no_uq UNIQUE (batch_id, line_no),
  CONSTRAINT opening_inventory_line_batch_fk FOREIGN KEY (company_id, batch_id) REFERENCES mig.migration_batch (company_id, batch_id),
  CONSTRAINT opening_inventory_line_location_fk FOREIGN KEY (plant_id, location_id) REFERENCES md.location (plant_id, location_id),
  CONSTRAINT opening_inventory_line_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT opening_inventory_line_lot_fk FOREIGN KEY (company_id, lot_id) REFERENCES inv.lot (company_id, lot_id),
  CONSTRAINT opening_inventory_line_document CHECK (length(btrim(source_document_number)) BETWEEN 1 AND 100),
  CONSTRAINT opening_inventory_line_quantity CHECK (quantity > 0),
  CONSTRAINT opening_inventory_line_cost CHECK (unit_cost > 0),
  CONSTRAINT opening_inventory_line_value CHECK (value > 0 AND value = round(value, 2)),
  CONSTRAINT opening_inventory_line_live_lot CHECK (NOT live OR lot_id IS NOT NULL)
);
CREATE UNIQUE INDEX opening_inventory_line_live_document_uq ON mig.opening_inventory_line (company_id, source_document_number) WHERE live;

CREATE FUNCTION mig.migration_batch_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'DRAFT' OR NEW.version <> 1 THEN
      RAISE EXCEPTION 'mig.migration_batch: a batch is prepared as DRAFT with version 1';
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'mig.migration_batch rows cannot be deleted';
  END IF;
  IF ROW(NEW.batch_id, NEW.company_id, NEW.kind, NEW.source_system, NEW.file_name, NEW.source_file_sha256, NEW.cutover_date, NEW.prepared_by)
     IS DISTINCT FROM ROW(OLD.batch_id, OLD.company_id, OLD.kind, OLD.source_system, OLD.file_name, OLD.source_file_sha256, OLD.cutover_date, OLD.prepared_by)
     OR NEW.version <> OLD.version + 1
     OR NOT ((OLD.status = 'DRAFT' AND NEW.status = 'POSTED') OR (OLD.status = 'POSTED' AND NEW.status = 'REVERSED')) THEN
    RAISE EXCEPTION 'mig.migration_batch: % → % (or a change of its identity) is not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER migration_batch_guard BEFORE INSERT OR UPDATE OR DELETE ON mig.migration_batch FOR EACH ROW EXECUTE FUNCTION mig.migration_batch_guard();
CREATE TRIGGER migration_batch_no_truncate BEFORE TRUNCATE ON mig.migration_batch FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
CREATE CONSTRAINT TRIGGER migration_batch_evidence_on_insert AFTER INSERT ON mig.migration_batch
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('OpeningInventory', 'batch_id');
CREATE CONSTRAINT TRIGGER migration_batch_evidence_on_change AFTER UPDATE ON mig.migration_batch
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('OpeningInventory', 'batch_id');

-- K-25 for batches: POSTED ⇔ a live journal of its posting event; REVERSED ⇔ that journal reversed by the reversal event.
CREATE FUNCTION mig.migration_batch_journal_evidence() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  live integer;
  reversed integer;
BEGIN
  IF NEW.status = 'DRAFT' THEN
    RETURN NULL;
  END IF;
  SELECT count(*) FILTER (WHERE NOT EXISTS (SELECT 1 FROM fin.gl_journal r WHERE r.reverses_journal_id = j.journal_id)),
         count(*) FILTER (WHERE EXISTS (SELECT 1 FROM fin.gl_journal r WHERE r.reverses_journal_id = j.journal_id AND r.source_event_id = NEW.reversal_event_id))
    INTO live, reversed
    FROM fin.gl_journal j WHERE j.company_id = NEW.company_id AND j.source_event_id = NEW.posting_event_id AND j.journal_type = 'AUTO';
  IF (NEW.status = 'POSTED' AND live <> 1) OR (NEW.status = 'REVERSED' AND (live <> 0 OR reversed <> 1)) THEN
    RAISE EXCEPTION 'mig.migration_batch %: status % without its journal (K-25): live %, reversed %', NEW.batch_id, NEW.status, live, reversed;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER migration_batch_journal_evidence AFTER UPDATE ON mig.migration_batch
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION mig.migration_batch_journal_evidence();

-- Lines: added only to a DRAFT batch, finished goods only; afterwards only the lot (once, at posting) and the live flag change.
CREATE FUNCTION mig.opening_inventory_line_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NOT EXISTS (SELECT 1 FROM mig.migration_batch b WHERE b.batch_id = NEW.batch_id AND b.status = 'DRAFT') THEN
      RAISE EXCEPTION 'mig.opening_inventory_line: lines are added only to a DRAFT batch';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM md.item i WHERE i.item_id = NEW.item_id AND i.item_type = 'FINISHED_GOOD') THEN
      RAISE EXCEPTION 'mig.opening_inventory_line: item % is not a finished good (E-VS3-02b-3)', NEW.item_id;
    END IF;
    IF NEW.lot_id IS NOT NULL OR NEW.live THEN
      RAISE EXCEPTION 'mig.opening_inventory_line: lot and live flag are set when the batch is posted';
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'mig.opening_inventory_line rows cannot be deleted';
  END IF;
  IF ROW(NEW.line_id, NEW.company_id, NEW.batch_id, NEW.line_no, NEW.source_document_number, NEW.plant_id, NEW.location_id, NEW.item_id, NEW.quantity, NEW.unit_cost, NEW.value)
     IS DISTINCT FROM ROW(OLD.line_id, OLD.company_id, OLD.batch_id, OLD.line_no, OLD.source_document_number, OLD.plant_id, OLD.location_id, OLD.item_id, OLD.quantity, OLD.unit_cost, OLD.value)
     OR (OLD.lot_id IS NOT NULL AND NEW.lot_id IS DISTINCT FROM OLD.lot_id) THEN
    RAISE EXCEPTION 'mig.opening_inventory_line: only the lot (once) and the live flag change';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER opening_inventory_line_guard BEFORE INSERT OR UPDATE OR DELETE ON mig.opening_inventory_line
  FOR EACH ROW EXECUTE FUNCTION mig.opening_inventory_line_guard();
CREATE TRIGGER opening_inventory_line_no_truncate BEFORE TRUNCATE ON mig.opening_inventory_line FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- E-VS3-02b-7: OPEN-INV, DRAFT until the Controller approves it (A-01). Dr FINISHED_GOODS [plant, item, INV] / Cr MIGRATION_CLEARING.
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type)
VALUES ('0192f001-0000-7000-8000-000000000011', 'OPEN-INV', 'OpeningInventoryPosted');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, also_requires_components, effective_from, status)
VALUES ('0192f001-0000-7000-8000-000000000011', 1,
   '{"lines": [
      {"code": "OPENINV-DR-FG", "side": "DEBIT", "account_role": "FINISHED_GOODS", "amount": "opening_value", "dimensions": ["plant", "item"], "subledger": "INV"},
      {"code": "OPENINV-CR-MIG", "side": "CREDIT", "account_role": "MIGRATION_CLEARING", "amount": "opening_value", "dimensions": ["plant"]}
    ]}',
   '{"OPENINV-DR-FG": "Saldo inicial de producto terminado (documento {source_document_number}, corte {cutover_date}): cantidad × costo estándar aprobado.",
     "OPENINV-CR-MIG": "Contrapartida de migración del saldo inicial (documento {source_document_number}); debe quedar en cero al completar la migración."}',
   'INV-MOV', ARRAY[]::text[], DATE '2026-01-01', 'DRAFT');

-- E-VS3-02b-8: the migration clearing balance, a warning until the full migration arrives.
INSERT INTO rec.recon_definition (recon_code, description, severity) VALUES
  ('MIGRATION-CLEARING', 'El saldo de MIGRATION_CLEARING debe quedar en cero al completar la migración (E-VS3-02b-8)', 'WARNING');

-- ---------------------------------------------------------------------------------------------
-- Permissions (E-VS3-02b-2): the Controller prepares, the Aprobador de políticas posts and reverses.
-- ---------------------------------------------------------------------------------------------
INSERT INTO iam.permission (permission_code, access) VALUES ('opening_inventory:prepare', 'WRITE'), ('opening_inventory:post', 'WRITE');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES ('CONTROLLER', 'opening_inventory:prepare'), ('APROBADOR_POLITICAS', 'opening_inventory:post')) AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;

INSERT INTO iam.sod_rule (permission_a, permission_b) VALUES ('opening_inventory:post', 'opening_inventory:prepare');

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['mig.migration_batch', 'mig.opening_inventory_line'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON mig.migration_batch, mig.opening_inventory_line TO rochell_app;
GRANT UPDATE (status, posted_by, posting_event_id, reversal_event_id, reversal_reason, version) ON mig.migration_batch TO rochell_app;
GRANT UPDATE (lot_id, live) ON mig.opening_inventory_line TO rochell_app;
