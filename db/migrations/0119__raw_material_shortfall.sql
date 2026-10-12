-- VS3-02c · Raw material consumed without stock (approved errata E-VS3-02b-11…14, E-VS3-02c-1…5).
--   - E-VS3-02c-1: the stock never goes below zero; what production consumes beyond it is a shortfall per material and location
--     (inv.material_shortfall), Dr WIP / Cr MATERIAL_SHORTFALL at the material's standard price (P-47).
--   - E-VS3-02c-2: the next receipt or opening of the material in that location settles the oldest shortfall first
--     (inv.material_shortfall_settlement): Dr MATERIAL_SHORTFALL at standard / Cr RAW_MATERIAL at the real cost, the difference to
--     MATERIAL_PRICE_VARIANCE (P-48).
--   - E-VS3-02c-3: policy PRODUCTION parameter raw_material_negative_until (a date as YYYYMMDD; 0 or none = off).
--   - E-VS3-02c-4: reconciliation MP-NEGATIVO; an open shortfall not justified blocks INV-MOV of its month.
--   - E-VS3-02b-11, E-VS3-02c-5: OPEN-INV v2 (from 2026-10-01; an active version 1 ends there when v2 is activated) also takes raw
--     material (Dr RAW_MATERIAL) at the standard price of the latest approved standard cost that uses it.

CREATE TABLE inv.material_shortfall (
  shortfall_id     uuid          NOT NULL,
  company_id       uuid          NOT NULL,
  plant_id         uuid          NOT NULL,
  location_id      uuid          NOT NULL,
  item_id          uuid          NOT NULL,
  source_type      text          NOT NULL,
  source_id        uuid          NOT NULL,
  run_id           uuid,
  business_date    date          NOT NULL,
  qty              numeric(18,6) NOT NULL,
  std_price        numeric(19,4) NOT NULL,
  value            numeric(19,4) NOT NULL,
  event_id         uuid          NOT NULL,
  settled_qty      numeric(18,6) NOT NULL DEFAULT 0,
  settled_value    numeric(19,4) NOT NULL DEFAULT 0,
  status           text          NOT NULL,
  justification    text,
  justified_by     uuid,
  justified_at     timestamptz,
  created_at       timestamptz   NOT NULL,
  version          bigint        NOT NULL,
  CONSTRAINT material_shortfall_pk PRIMARY KEY (shortfall_id),
  CONSTRAINT material_shortfall_company_uq UNIQUE (company_id, shortfall_id),
  CONSTRAINT material_shortfall_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT material_shortfall_location_fk FOREIGN KEY (location_id) REFERENCES md.location (location_id),
  CONSTRAINT material_shortfall_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT material_shortfall_justified_by_fk FOREIGN KEY (justified_by) REFERENCES iam.user (user_id),
  CONSTRAINT material_shortfall_values CHECK (qty > 0 AND std_price > 0 AND value > 0 AND value = round(value, 2) AND settled_qty >= 0 AND settled_qty <= qty
    AND source_type IN ('SHIFT_SUMMARY') AND status IN ('OPEN', 'SETTLED', 'CANCELLED') AND (status = 'SETTLED') = (settled_qty = qty)
    AND (justification IS NULL) = (justified_by IS NULL) AND (justification IS NULL) = (justified_at IS NULL) AND version >= 1)
);
CREATE INDEX material_shortfall_open ON inv.material_shortfall (company_id, location_id, item_id, created_at) WHERE status = 'OPEN';
CREATE INDEX material_shortfall_source ON inv.material_shortfall (source_id);

CREATE TABLE inv.material_shortfall_settlement (
  settlement_id    uuid          NOT NULL,
  company_id       uuid          NOT NULL,
  shortfall_id     uuid          NOT NULL,
  qty              numeric(18,6) NOT NULL,
  std_value        numeric(19,4) NOT NULL,
  actual_value     numeric(19,4) NOT NULL,
  lot_id           uuid          NOT NULL,
  value_entry_id   uuid          NOT NULL,
  source_type      text          NOT NULL,
  source_id        uuid          NOT NULL,
  event_id         uuid          NOT NULL,
  created_at       timestamptz   NOT NULL,
  CONSTRAINT material_shortfall_settlement_pk PRIMARY KEY (settlement_id),
  CONSTRAINT material_shortfall_settlement_shortfall_fk FOREIGN KEY (company_id, shortfall_id) REFERENCES inv.material_shortfall (company_id, shortfall_id),
  CONSTRAINT material_shortfall_settlement_values CHECK (qty > 0 AND std_value >= 0 AND actual_value >= 0 AND source_type IN ('GOODS_RECEIPT', 'OPENING_INVENTORY'))
);
CREATE TRIGGER material_shortfall_settlement_append_only BEFORE UPDATE OR DELETE ON inv.material_shortfall_settlement FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['inv.material_shortfall', 'inv.material_shortfall_settlement'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON inv.material_shortfall, inv.material_shortfall_settlement TO rochell_app;
GRANT UPDATE (settled_qty, settled_value, status, justification, justified_by, justified_at, version) ON inv.material_shortfall TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- E-VS3-02c-1: the bridge account, mapped by the Controller (A-01).
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.account_role (role_code, is_control, description, name) VALUES
  ('MATERIAL_SHORTFALL', false, 'Materia prima consumida por producción sin existencia, hasta que entre (pasivo transitorio, E-VS3-02c-1)',
   'Materia prima consumida por regularizar');

-- ---------------------------------------------------------------------------------------------
-- E-VS3-02c-1/2: P-47 and P-48, DRAFT until the Controller activates them; OPEN-INV v2 with raw material (E-VS3-02b-11).
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type) VALUES
  ('0192f001-0000-7000-8000-00000000003a', 'P-47', 'MaterialShortfallRecorded'),
  ('0192f001-0000-7000-8000-00000000003b', 'P-48', 'MaterialShortfallSettled');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, also_requires_components, effective_from, status) VALUES
  ('0192f001-0000-7000-8000-00000000003a', 1,
   '{"lines": [
      {"code": "P47-DR-WIP", "side": "DEBIT", "account_role": "WIP", "amount": "shortfall_value", "dimensions": ["plant", "item"], "subledger": "WIP"},
      {"code": "P47-CR-SHORT", "side": "CREDIT", "account_role": "MATERIAL_SHORTFALL", "amount": "shortfall_value", "dimensions": ["plant", "item"]}
    ]}',
   '{"P47-DR-WIP": "Consumo de {material_code} sin existencia en la corrida {run_no} ({quantity} × precio estándar {std_price}) cargado a producción en proceso.",
     "P47-CR-SHORT": "{quantity} de {material_code} consumidos sin existencia: quedan por regularizar hasta que entre el material."}',
   'INV-MOV', ARRAY[]::text[], DATE '2026-01-01', 'DRAFT'),
  ('0192f001-0000-7000-8000-00000000003b', 1,
   '{"lines": [
      {"code": "P48-DR-SHORT", "side": "DEBIT", "account_role": "MATERIAL_SHORTFALL", "amount": "std_value", "dimensions": ["plant", "item"]},
      {"code": "P48-CR-RM", "side": "CREDIT", "account_role": "RAW_MATERIAL", "amount": "actual_value", "dimensions": ["plant", "item"], "subledger": "INV"},
      {"code": "P48-DR-PRICE", "side": "DEBIT", "account_role": "MATERIAL_PRICE_VARIANCE", "amount": "price_variance", "dimensions": ["plant", "item"]},
      {"code": "P48-CR-PRICE", "side": "CREDIT", "account_role": "MATERIAL_PRICE_VARIANCE", "amount": "price_variance", "dimensions": ["plant", "item"]}
    ]}',
   '{"P48-DR-SHORT": "Se regulariza {quantity} de {material_code} consumido sin existencia, al precio estándar con que se cargó a producción.",
     "P48-CR-RM": "Salida de {material_code} (lote {lot_code}) que tapa el faltante, a su costo real.",
     "P48-DR-PRICE": "El material que tapó el faltante costó más que su precio estándar.",
     "P48-CR-PRICE": "El material que tapó el faltante costó menos que su precio estándar."}',
   'INV-MOV', ARRAY[]::text[], DATE '2026-01-01', 'DRAFT'),
  ('0192f001-0000-7000-8000-000000000011', 2,
   '{"lines": [
      {"code": "OPENINV-DR-FG", "side": "DEBIT", "account_role": "FINISHED_GOODS", "amount": "opening_value", "dimensions": ["plant", "item"], "subledger": "INV"},
      {"code": "OPENINV-DR-RM", "side": "DEBIT", "account_role": "RAW_MATERIAL", "amount": "opening_value", "dimensions": ["plant", "item"], "subledger": "INV"},
      {"code": "OPENINV-CR-MIG", "side": "CREDIT", "account_role": "MIGRATION_CLEARING", "amount": "opening_value", "dimensions": ["plant"]}
    ]}',
   '{"OPENINV-DR-FG": "Saldo inicial de producto terminado (documento {source_document_number}, corte {cutover_date}): cantidad × costo estándar aprobado.",
     "OPENINV-DR-RM": "Saldo inicial de materia prima (documento {source_document_number}, corte {cutover_date}): cantidad × precio estándar del material.",
     "OPENINV-CR-MIG": "Contrapartida de migración del saldo inicial (documento {source_document_number}); debe quedar en cero al completar la migración."}',
   'INV-MOV', ARRAY[]::text[], DATE '2026-10-01', 'DRAFT');

-- ---------------------------------------------------------------------------------------------
-- E-VS3-02c-3: the switch, a date in the PRODUCTION policy (YYYYMMDD; 0 = off). The unit DATE shows it as a date.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE acc.policy_parameter_definition DROP CONSTRAINT policy_parameter_definition_unit,
  ADD CONSTRAINT policy_parameter_definition_unit CHECK (unit IS NULL OR unit IN ('PERCENT', 'AMOUNT', 'DAYS', 'HOURS', 'MINUTES', 'OPTION', 'COUNT', 'DATE'));
INSERT INTO acc.policy_parameter_definition (param_code, policy_code, value_type, min_value, max_value, allowed_values, description, label, unit, example, affects) VALUES
  ('raw_material_negative_until', 'PRODUCTION', 'INTEGER', 0, 20991231, NULL,
   'Hasta qué fecha (AAAAMMDD) la producción puede consumir materia prima sin existencia; 0 = no (E-VS3-02c-3)',
   'Materia prima en negativo hasta', 'DATE', '30/11/2026',
   'Hasta esa fecha, un resumen de turno se registra aunque falte materia prima: lo que falta queda por regularizar y lo tapa la próxima compra o la apertura. Después, o con 0, falta existencia y no se registra.');

-- ---------------------------------------------------------------------------------------------
-- E-VS3-02c-4: reconciliation MP-NEGATIVO; open shortfalls not justified block INV-MOV of their month.
-- ---------------------------------------------------------------------------------------------
INSERT INTO rec.recon_definition (recon_code, description, severity, name, guidance) VALUES
  ('MP-NEGATIVO',
   'Materia prima consumida sin existencia y todavía no regularizada; el saldo de MATERIAL_SHORTFALL = lo abierto a precio estándar (E-VS3-02c-4)',
   'ERROR',
   'Materia prima en negativo',
   'Lista la materia prima que producción consumió sin existencia y que ninguna compra ni apertura ha tapado todavía. Bloquea el cierre de inventario del mes de cada faltante hasta que se regularice o se justifique; comprueba además que la cuenta puente tenga lo que falta por regularizar.');
UPDATE rec.recon_definition SET side_a_label = 'Por regularizar (precio estándar)', side_b_label = 'Cuenta puente en el mayor' WHERE recon_code = 'MP-NEGATIVO';
INSERT INTO rec.recon_blocking (recon_code, component) VALUES ('MP-NEGATIVO', 'INV-MOV');
INSERT INTO rec.recon_classification (classification, name, guidance) VALUES
  ('MATERIAL_SHORTFALL_OPEN', 'Materia prima en negativo sin regularizar',
   'Producción consumió este material sin existencia y no ha entrado material que lo tape. Registre la recepción de la compra o la apertura de materia prima en esa ubicación; si el faltante es correcto y se regulariza el mes siguiente, justifíquelo en Producción › Materia prima en negativo.'),
  ('MATERIAL_SHORTFALL_JUSTIFIED', 'Materia prima en negativo justificada',
   'El faltante sigue abierto pero tiene justificación: no bloquea el cierre. Se tapa con la próxima entrada del material.'),
  ('MATERIAL_SHORTFALL_GL_DIFFERENCE', 'Cuenta puente distinta de los faltantes',
   'El saldo de «Materia prima consumida por regularizar» no es lo abierto de los faltantes a precio estándar. Es un error interno: repórtelo a soporte.');

-- ---------------------------------------------------------------------------------------------
-- E-VS3-02b-11: an opening line is a finished good or a raw material.
-- ---------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION mig.opening_inventory_line_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NOT EXISTS (SELECT 1 FROM mig.migration_batch b WHERE b.batch_id = NEW.batch_id AND b.status = 'DRAFT') THEN
      RAISE EXCEPTION 'mig.opening_inventory_line: lines are added only to a DRAFT batch';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM md.item i WHERE i.item_id = NEW.item_id AND i.item_type IN ('FINISHED_GOOD', 'RAW_MATERIAL')) THEN
      RAISE EXCEPTION 'mig.opening_inventory_line: item % is not a finished good or a raw material (E-VS3-02b-3, E-VS3-02b-11)', NEW.item_id;
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
