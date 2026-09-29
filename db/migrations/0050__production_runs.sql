-- MFG1-03 · Production runs, shift summaries, material consumption, racks, finished-goods lots in curing, cost collectors and the
-- posting rules P-08 / P-10. Frozen Baseline MFG-1 §2–§6; approved errata E-MFG1-1…18 and E-MFG1-03-1…12.

-- ---------------------------------------------------------------------------------------------
-- E-MFG1-03-9: exact reversals of production movements. Text comparisons in the CHECKs (lesson 2).
-- ---------------------------------------------------------------------------------------------
ALTER TYPE inv.movement_type ADD VALUE 'PRODUCTION_ISSUE_REVERSAL';
ALTER TYPE inv.movement_type ADD VALUE 'PRODUCTION_RECEIPT_REVERSAL';

ALTER TABLE inv.inv_quantity_entry DROP CONSTRAINT inv_quantity_entry_sign;
ALTER TABLE inv.inv_quantity_entry ADD CONSTRAINT inv_quantity_entry_sign CHECK (
  (movement_type::text IN ('RECEIPT', 'OPENING', 'PRODUCTION_RECEIPT', 'PRODUCTION_ISSUE_REVERSAL') AND quantity > 0) OR
  (movement_type::text IN ('ISSUE', 'RECEIPT_REVERSAL', 'PRODUCTION_ISSUE', 'PRODUCTION_RECEIPT_REVERSAL') AND quantity < 0) OR
  movement_type::text IN ('RECEIPT_CORRECTION', 'TRANSFER'));
ALTER TABLE inv.inv_value_entry DROP CONSTRAINT inv_value_entry_sign;
ALTER TABLE inv.inv_value_entry ADD CONSTRAINT inv_value_entry_sign CHECK (
  (movement_type::text IN ('RECEIPT', 'OPENING', 'PRODUCTION_RECEIPT', 'PRODUCTION_ISSUE_REVERSAL') AND amount > 0) OR
  (movement_type::text IN ('ISSUE', 'RECEIPT_REVERSAL', 'PRODUCTION_ISSUE', 'PRODUCTION_RECEIPT_REVERSAL') AND amount < 0) OR
  movement_type::text IN ('RECEIPT_CORRECTION', 'VALUATION_ADJUSTMENT', 'VALUATION_REALLOCATION', 'PRICE_ADJUSTMENT', 'REPOST', 'RESIDUAL_ADJUSTMENT', 'TRANSFER'));

-- ---------------------------------------------------------------------------------------------
-- Cost collectors (E-MFG1-11, E-MFG1-03-6): plant × finished good × month, created OPEN by the first run; settled in MFG1-05.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE mfg.cost_collector (
  collector_id  uuid   NOT NULL,
  company_id    uuid   NOT NULL,
  plant_id      uuid   NOT NULL,
  item_id       uuid   NOT NULL,
  period_month  date   NOT NULL,
  status        text   NOT NULL,
  settled_by    uuid,
  version       bigint NOT NULL,
  CONSTRAINT cost_collector_pk PRIMARY KEY (collector_id),
  CONSTRAINT cost_collector_company_uq UNIQUE (company_id, collector_id),
  CONSTRAINT cost_collector_key_uq UNIQUE (company_id, plant_id, item_id, period_month),
  CONSTRAINT cost_collector_plant_fk FOREIGN KEY (company_id, plant_id) REFERENCES md.plant (company_id, plant_id),
  CONSTRAINT cost_collector_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT cost_collector_settled_fk FOREIGN KEY (settled_by) REFERENCES iam.user (user_id),
  CONSTRAINT cost_collector_month CHECK (period_month = date_trunc('month', period_month)::date),
  CONSTRAINT cost_collector_status CHECK (status IN ('OPEN', 'SETTLED')),
  CONSTRAINT cost_collector_settled CHECK ((status = 'SETTLED') = (settled_by IS NOT NULL)),
  CONSTRAINT cost_collector_version_positive CHECK (version >= 1)
);

-- ---------------------------------------------------------------------------------------------
-- Production runs (E-MFG1-1, E-MFG1-03-1): an operational order without cost; one live run per machine × shift × date × product.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE mfg.production_run (
  run_id             uuid   NOT NULL,
  company_id         uuid   NOT NULL,
  run_no             text   NOT NULL,
  plant_id           uuid   NOT NULL,
  machine_id         uuid   NOT NULL,
  shift_id           uuid   NOT NULL,
  business_date      date   NOT NULL,
  item_id            uuid   NOT NULL,
  recipe_version_id  uuid   NOT NULL,
  cost_version_id    uuid   NOT NULL,
  collector_id       uuid   NOT NULL,
  status             text   NOT NULL,
  started_by         uuid   NOT NULL,
  version            bigint NOT NULL,
  CONSTRAINT production_run_pk PRIMARY KEY (run_id),
  CONSTRAINT production_run_company_uq UNIQUE (company_id, run_id),
  CONSTRAINT production_run_no_uq UNIQUE (company_id, run_no),
  CONSTRAINT production_run_plant_fk FOREIGN KEY (company_id, plant_id) REFERENCES md.plant (company_id, plant_id),
  CONSTRAINT production_run_machine_fk FOREIGN KEY (company_id, machine_id) REFERENCES md.machine (company_id, machine_id),
  CONSTRAINT production_run_shift_fk FOREIGN KEY (company_id, shift_id) REFERENCES mfg.shift (company_id, shift_id),
  CONSTRAINT production_run_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT production_run_recipe_fk FOREIGN KEY (company_id, recipe_version_id) REFERENCES mfg.recipe_version (company_id, recipe_version_id),
  CONSTRAINT production_run_cost_fk FOREIGN KEY (company_id, cost_version_id) REFERENCES md.standard_cost_version (company_id, cost_version_id),
  CONSTRAINT production_run_collector_fk FOREIGN KEY (company_id, collector_id) REFERENCES mfg.cost_collector (company_id, collector_id),
  CONSTRAINT production_run_started_fk FOREIGN KEY (started_by) REFERENCES iam.user (user_id),
  CONSTRAINT production_run_no_format CHECK (run_no ~ '^PR-[0-9]{6,}$'),
  CONSTRAINT production_run_status CHECK (status IN ('IN_PROGRESS', 'COMPLETED', 'CANCELLED')),
  CONSTRAINT production_run_version_positive CHECK (version >= 1)
);
CREATE UNIQUE INDEX production_run_one_live ON mfg.production_run (machine_id, shift_id, business_date, item_id) WHERE status <> 'CANCELLED';

-- ---------------------------------------------------------------------------------------------
-- Shift summaries (E-MFG1-03-2, E-MFG1-03-5, E-MFG1-03-9): one live summary per run (DRAFT or POSTED); a reversed summary stays.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE mfg.shift_summary (
  summary_id          uuid          NOT NULL,
  company_id          uuid          NOT NULL,
  run_id              uuid          NOT NULL,
  batches             integer       NOT NULL,
  good_units          numeric(18,6) NOT NULL,
  mix_scrap_units     numeric(18,6) NOT NULL,
  fresh_scrap_units   numeric(18,6) NOT NULL,
  status              text          NOT NULL,
  recorded_by         uuid          NOT NULL,
  posted_by           uuid,
  material_event_id   uuid,
  receipt_event_id    uuid,
  reversal_event_id   uuid,
  reversal_reason     text,
  version             bigint        NOT NULL,
  CONSTRAINT shift_summary_pk PRIMARY KEY (summary_id),
  CONSTRAINT shift_summary_company_uq UNIQUE (company_id, summary_id),
  CONSTRAINT shift_summary_run_fk FOREIGN KEY (company_id, run_id) REFERENCES mfg.production_run (company_id, run_id),
  CONSTRAINT shift_summary_recorded_fk FOREIGN KEY (recorded_by) REFERENCES iam.user (user_id),
  CONSTRAINT shift_summary_posted_fk FOREIGN KEY (posted_by) REFERENCES iam.user (user_id),
  CONSTRAINT shift_summary_quantities CHECK (batches > 0 AND good_units > 0 AND mix_scrap_units >= 0 AND fresh_scrap_units >= 0),
  CONSTRAINT shift_summary_status CHECK (status IN ('DRAFT', 'POSTED', 'REVERSED')),
  CONSTRAINT shift_summary_four_eyes CHECK (posted_by IS NULL OR posted_by <> recorded_by),
  CONSTRAINT shift_summary_posted CHECK ((status = 'DRAFT') = (posted_by IS NULL) AND (status = 'DRAFT') = (material_event_id IS NULL)
    AND (status = 'DRAFT') = (receipt_event_id IS NULL)),
  CONSTRAINT shift_summary_reversed CHECK ((status = 'REVERSED') = (reversal_event_id IS NOT NULL) AND (status = 'REVERSED') = (reversal_reason IS NOT NULL)),
  CONSTRAINT shift_summary_version_positive CHECK (version >= 1)
);
CREATE UNIQUE INDEX shift_summary_one_live ON mfg.shift_summary (run_id) WHERE status IN ('DRAFT', 'POSTED');

-- Real consumption per material (E-MFG1-03-2/3/4): entered in any convertible unit, kept in the base unit; lines change only in DRAFT.
CREATE TABLE mfg.material_consumption (
  summary_id        uuid          NOT NULL,
  company_id        uuid          NOT NULL,
  material_item_id  uuid          NOT NULL,
  location_id       uuid          NOT NULL,
  entered_qty       numeric(18,6) NOT NULL,
  entered_uom       text          NOT NULL,
  qty               numeric(18,6) NOT NULL,
  theoretical_qty   numeric(18,6) NOT NULL,
  CONSTRAINT material_consumption_pk PRIMARY KEY (summary_id, material_item_id),
  CONSTRAINT material_consumption_summary_fk FOREIGN KEY (company_id, summary_id) REFERENCES mfg.shift_summary (company_id, summary_id),
  CONSTRAINT material_consumption_item_fk FOREIGN KEY (company_id, material_item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT material_consumption_location_fk FOREIGN KEY (company_id, location_id) REFERENCES md.location (company_id, location_id),
  CONSTRAINT material_consumption_uom_fk FOREIGN KEY (entered_uom) REFERENCES md.uom (uom_code),
  CONSTRAINT material_consumption_qty CHECK (entered_qty > 0 AND qty > 0 AND theoretical_qty >= 0)
);

-- The lots each material was issued from at posting (FIFO by lot code, E-MFG1-03-4) and their value entries.
CREATE TABLE mfg.consumption_issue (
  summary_id        uuid          NOT NULL,
  company_id        uuid          NOT NULL,
  material_item_id  uuid          NOT NULL,
  lot_id            uuid          NOT NULL,
  qty               numeric(18,6) NOT NULL,
  value             numeric(19,4) NOT NULL,
  value_entry_id    uuid          NOT NULL,
  CONSTRAINT consumption_issue_pk PRIMARY KEY (summary_id, material_item_id, lot_id),
  CONSTRAINT consumption_issue_line_fk FOREIGN KEY (summary_id, material_item_id) REFERENCES mfg.material_consumption (summary_id, material_item_id),
  CONSTRAINT consumption_issue_lot_fk FOREIGN KEY (lot_id) REFERENCES inv.lot (lot_id),
  CONSTRAINT consumption_issue_qty CHECK (qty > 0 AND value > 0)
);

-- Finished-goods lot of a posted summary (E-MFG1-7, E-MFG1-8, E-MFG1-03-7): in CURADO until Calidad releases it (MFG1-04).
CREATE TABLE mfg.fg_lot (
  lot_id         uuid        NOT NULL,
  company_id     uuid        NOT NULL,
  summary_id     uuid        NOT NULL,
  run_id         uuid        NOT NULL,
  curing_from    timestamptz NOT NULL,
  releasable_at  timestamptz NOT NULL,
  status         text        NOT NULL,
  version        bigint      NOT NULL,
  CONSTRAINT fg_lot_pk PRIMARY KEY (lot_id),
  CONSTRAINT fg_lot_company_uq UNIQUE (company_id, lot_id),
  CONSTRAINT fg_lot_summary_uq UNIQUE (summary_id),
  CONSTRAINT fg_lot_lot_fk FOREIGN KEY (lot_id) REFERENCES inv.lot (lot_id),
  CONSTRAINT fg_lot_summary_fk FOREIGN KEY (company_id, summary_id) REFERENCES mfg.shift_summary (company_id, summary_id),
  CONSTRAINT fg_lot_run_fk FOREIGN KEY (company_id, run_id) REFERENCES mfg.production_run (company_id, run_id),
  CONSTRAINT fg_lot_window CHECK (releasable_at >= curing_from),
  CONSTRAINT fg_lot_status CHECK (status IN ('CURING', 'RELEASED', 'BLOCKED', 'SCRAPPED', 'VOIDED')),
  CONSTRAINT fg_lot_version_positive CHECK (version >= 1)
);

-- Racks proposed from the good units (E-MFG1-6): ⌈units ÷ units per rack⌉, the last one with the remainder.
CREATE TABLE mfg.rack (
  rack_id     uuid          NOT NULL,
  company_id  uuid          NOT NULL,
  lot_id      uuid          NOT NULL,
  rack_no     integer       NOT NULL,
  units       numeric(18,6) NOT NULL,
  status      text          NOT NULL,
  CONSTRAINT rack_pk PRIMARY KEY (rack_id),
  CONSTRAINT rack_no_uq UNIQUE (lot_id, rack_no),
  CONSTRAINT rack_lot_fk FOREIGN KEY (company_id, lot_id) REFERENCES mfg.fg_lot (company_id, lot_id),
  CONSTRAINT rack_no_positive CHECK (rack_no >= 1),
  CONSTRAINT rack_units CHECK (units > 0),
  CONSTRAINT rack_status CHECK (status IN ('CURING', 'RELEASED', 'BLOCKED', 'SCRAPPED', 'VOIDED'))
);

-- ---------------------------------------------------------------------------------------------
-- Guards: documents are never deleted; consumption lines change only while their summary is DRAFT; posted rows keep their
-- quantities; ADR-027 evidence for every status.
-- ---------------------------------------------------------------------------------------------
CREATE FUNCTION mfg.consumption_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  summary uuid := CASE WHEN TG_OP = 'DELETE' THEN OLD.summary_id ELSE NEW.summary_id END;
BEGIN
  IF NOT EXISTS (SELECT 1 FROM mfg.shift_summary s WHERE s.summary_id = summary AND s.status = 'DRAFT') THEN
    RAISE EXCEPTION 'mfg.material_consumption: lines change only while the shift summary is DRAFT';
  END IF;
  RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END $$;
CREATE TRIGGER material_consumption_guard BEFORE INSERT OR UPDATE OR DELETE ON mfg.material_consumption
  FOR EACH ROW EXECUTE FUNCTION mfg.consumption_guard();

CREATE FUNCTION mfg.summary_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'mfg.shift_summary rows cannot be deleted';
  END IF;
  IF NEW.run_id <> OLD.run_id OR NEW.recorded_by <> OLD.recorded_by OR NEW.version <> OLD.version + 1
     OR NOT ((OLD.status = NEW.status AND OLD.status = 'DRAFT') OR (OLD.status = 'DRAFT' AND NEW.status = 'POSTED') OR (OLD.status = 'POSTED' AND NEW.status = 'REVERSED'))
     OR (OLD.status <> 'DRAFT' AND ROW(NEW.batches, NEW.good_units, NEW.mix_scrap_units, NEW.fresh_scrap_units)
                                   IS DISTINCT FROM ROW(OLD.batches, OLD.good_units, OLD.mix_scrap_units, OLD.fresh_scrap_units)) THEN
    RAISE EXCEPTION 'mfg.shift_summary: % → % or a change of a posted summary is not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER shift_summary_guard BEFORE UPDATE OR DELETE ON mfg.shift_summary FOR EACH ROW EXECUTE FUNCTION mfg.summary_guard();

CREATE FUNCTION mfg.run_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'mfg.production_run rows cannot be deleted';
  END IF;
  IF ROW(NEW.run_no, NEW.plant_id, NEW.machine_id, NEW.shift_id, NEW.business_date, NEW.item_id, NEW.recipe_version_id, NEW.cost_version_id, NEW.collector_id, NEW.started_by)
     IS DISTINCT FROM ROW(OLD.run_no, OLD.plant_id, OLD.machine_id, OLD.shift_id, OLD.business_date, OLD.item_id, OLD.recipe_version_id, OLD.cost_version_id, OLD.collector_id, OLD.started_by)
     OR NEW.version <> OLD.version + 1
     OR NOT ((OLD.status = 'IN_PROGRESS' AND NEW.status IN ('COMPLETED', 'CANCELLED')) OR (OLD.status = 'COMPLETED' AND NEW.status = 'IN_PROGRESS')) THEN
    RAISE EXCEPTION 'mfg.production_run: % → % or a change of the run''s identity is not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER production_run_guard BEFORE UPDATE OR DELETE ON mfg.production_run FOR EACH ROW EXECUTE FUNCTION mfg.run_guard();

CREATE TRIGGER consumption_issue_immutable BEFORE UPDATE OR DELETE ON mfg.consumption_issue FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER fg_lot_no_delete BEFORE DELETE ON mfg.fg_lot FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER rack_no_delete BEFORE DELETE ON mfg.rack FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER cost_collector_no_delete BEFORE DELETE ON mfg.cost_collector FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER production_run_no_truncate BEFORE TRUNCATE ON mfg.production_run FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER shift_summary_no_truncate BEFORE TRUNCATE ON mfg.shift_summary FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

CREATE CONSTRAINT TRIGGER production_run_evidence_on_insert AFTER INSERT ON mfg.production_run
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('ProductionRun', 'run_id');
CREATE CONSTRAINT TRIGGER production_run_evidence_on_change AFTER UPDATE ON mfg.production_run
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('ProductionRun', 'run_id');
CREATE CONSTRAINT TRIGGER shift_summary_evidence_on_insert AFTER INSERT ON mfg.shift_summary
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('ShiftSummary', 'summary_id');
CREATE CONSTRAINT TRIGGER shift_summary_evidence_on_change AFTER UPDATE ON mfg.shift_summary
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('ShiftSummary', 'summary_id');
CREATE CONSTRAINT TRIGGER fg_lot_evidence_on_insert AFTER INSERT ON mfg.fg_lot
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('FgLot', 'lot_id');
CREATE CONSTRAINT TRIGGER fg_lot_evidence_on_change AFTER UPDATE ON mfg.fg_lot
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('FgLot', 'lot_id');
CREATE CONSTRAINT TRIGGER cost_collector_evidence_on_insert AFTER INSERT ON mfg.cost_collector
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('CostCollector', 'collector_id');

-- ---------------------------------------------------------------------------------------------
-- E-MFG1-03-5/10: P-08 MaterialConsumed (Dr WIP [collector] / Cr RAW_MATERIAL at moving average) and P-10 ProductionReceived
-- (Dr FINISHED_GOODS at standard / Cr WIP for the material standard / Cr CONVERSION_ABSORPTION for the rest). DRAFT until the
-- Controller approves them (A-01).
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type) VALUES
  ('0192f001-0000-7000-8000-000000000024', 'P-08', 'MaterialConsumed'),
  ('0192f001-0000-7000-8000-000000000025', 'P-10', 'ProductionReceived');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, also_requires_components, effective_from, status) VALUES
  ('0192f001-0000-7000-8000-000000000024', 1,
   '{"lines": [
      {"code": "P08-DR-WIP", "side": "DEBIT", "account_role": "WIP", "amount": "consumption_value", "dimensions": ["plant", "item"], "subledger": "WIP"},
      {"code": "P08-CR-RM", "side": "CREDIT", "account_role": "RAW_MATERIAL", "amount": "consumption_value", "dimensions": ["plant", "item"], "subledger": "INV"}
    ]}',
   '{"P08-DR-WIP": "Consumo de {material_code} en la corrida {run_no} ({quantity} a costo promedio) cargado a producción en proceso del collector {collector}.",
     "P08-CR-RM": "Salida de {material_code} (lote {lot_code}) por el resumen de turno de la corrida {run_no}."}',
   'INV-MOV', ARRAY[]::text[], DATE '2026-01-01', 'DRAFT'),
  ('0192f001-0000-7000-8000-000000000025', 1,
   '{"lines": [
      {"code": "P10-DR-FG", "side": "DEBIT", "account_role": "FINISHED_GOODS", "amount": "standard_value", "dimensions": ["plant", "item"], "subledger": "INV"},
      {"code": "P10-CR-WIP", "side": "CREDIT", "account_role": "WIP", "amount": "material_standard", "dimensions": ["plant", "item"], "subledger": "WIP"},
      {"code": "P10-CR-CONV", "side": "CREDIT", "account_role": "CONVERSION_ABSORPTION", "amount": "conversion_standard", "dimensions": ["plant"]}
    ]}',
   '{"P10-DR-FG": "Entrada a curado de {good_units} unidades de la corrida {run_no} (lote {lot_code}) a costo estándar {unit_cost}.",
     "P10-CR-WIP": "Materiales a estándar de las unidades buenas ({good_units} × {material_cost}) salen de producción en proceso.",
     "P10-CR-CONV": "Conversión a estándar absorbida por las unidades buenas (costo estándar menos materiales)."}',
   'INV-MOV', ARRAY[]::text[], DATE '2026-01-01', 'DRAFT');

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['mfg.cost_collector', 'mfg.production_run', 'mfg.shift_summary', 'mfg.material_consumption', 'mfg.consumption_issue',
                           'mfg.fg_lot', 'mfg.rack'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON mfg.cost_collector, mfg.production_run, mfg.shift_summary, mfg.material_consumption, mfg.consumption_issue,
  mfg.fg_lot, mfg.rack TO rochell_app;
GRANT DELETE ON mfg.material_consumption TO rochell_app;
GRANT UPDATE (status, settled_by, version) ON mfg.cost_collector TO rochell_app;
GRANT UPDATE (status, version) ON mfg.production_run TO rochell_app;
GRANT UPDATE (batches, good_units, mix_scrap_units, fresh_scrap_units, status, posted_by, material_event_id, receipt_event_id,
  reversal_event_id, reversal_reason, version) ON mfg.shift_summary TO rochell_app;
GRANT UPDATE (status, version) ON mfg.fg_lot TO rochell_app;
GRANT UPDATE (status) ON mfg.rack TO rochell_app;
