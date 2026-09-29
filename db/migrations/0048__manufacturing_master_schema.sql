-- MFG1-01 · Manufacturing master data: machines, shifts, recipes (with the product × machine configuration), the standard cost
-- breakdown, the CURADO location, production movement types, account roles, roles and permissions (no commands yet).
-- Frozen Baseline MFG-1 §2 and §7; approved errata E-MFG1-1…18 and E-MFG1-01-1…13. The documents of the slice (runs, shift
-- summaries, racks, lots, cost collectors) arrive with their PRs (E-MFG1-01-1).

CREATE SCHEMA mfg;
GRANT USAGE ON SCHEMA mfg TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- Machines (E-MFG1-01-3): one production line per plant today (E-MFG1-4); ACTIVE ⇄ INACTIVE; never deleted.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE md.machine (
  machine_id  uuid   NOT NULL,
  company_id  uuid   NOT NULL,
  plant_id    uuid   NOT NULL,
  code        text   NOT NULL,
  name        text   NOT NULL,
  status      text   NOT NULL,
  version     bigint NOT NULL,
  CONSTRAINT machine_pk PRIMARY KEY (machine_id),
  CONSTRAINT machine_company_uq UNIQUE (company_id, machine_id),
  CONSTRAINT machine_code_uq UNIQUE (company_id, code),
  CONSTRAINT machine_plant_fk FOREIGN KEY (company_id, plant_id) REFERENCES md.plant (company_id, plant_id),
  CONSTRAINT machine_code_format CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,29}$'),
  CONSTRAINT machine_name_present CHECK (length(btrim(name)) BETWEEN 1 AND 200),
  CONSTRAINT machine_status CHECK (status IN ('ACTIVE', 'INACTIVE')),
  CONSTRAINT machine_version_positive CHECK (version >= 1)
);

-- ---------------------------------------------------------------------------------------------
-- Shifts (E-MFG1-01-4): the business date of a shift is the date it starts (E-MFG1-15); a night shift ends the next day.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE mfg.shift (
  shift_id    uuid   NOT NULL,
  company_id  uuid   NOT NULL,
  plant_id    uuid   NOT NULL,
  code        text   NOT NULL,
  starts_at   time   NOT NULL,
  ends_at     time   NOT NULL,
  status      text   NOT NULL,
  version     bigint NOT NULL,
  CONSTRAINT shift_pk PRIMARY KEY (shift_id),
  CONSTRAINT shift_company_uq UNIQUE (company_id, shift_id),
  CONSTRAINT shift_code_uq UNIQUE (plant_id, code),
  CONSTRAINT shift_plant_fk FOREIGN KEY (company_id, plant_id) REFERENCES md.plant (company_id, plant_id),
  CONSTRAINT shift_code_format CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,19}$'),
  CONSTRAINT shift_times CHECK (starts_at <> ends_at),
  CONSTRAINT shift_status CHECK (status IN ('ACTIVE', 'INACTIVE')),
  CONSTRAINT shift_version_positive CHECK (version >= 1)
);

-- Shared guard: registered ACTIVE with version 1; plant and code never change; version +1 per change.
CREATE FUNCTION mfg.master_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'ACTIVE' OR NEW.version <> 1 THEN
      RAISE EXCEPTION '%.%: registered ACTIVE with version 1', TG_TABLE_SCHEMA, TG_TABLE_NAME;
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION '%.% rows cannot be deleted; make them INACTIVE', TG_TABLE_SCHEMA, TG_TABLE_NAME;
  END IF;
  IF ROW(to_jsonb(NEW) ->> 'company_id', to_jsonb(NEW) ->> 'plant_id', to_jsonb(NEW) ->> 'code')
     IS DISTINCT FROM ROW(to_jsonb(OLD) ->> 'company_id', to_jsonb(OLD) ->> 'plant_id', to_jsonb(OLD) ->> 'code') THEN
    RAISE EXCEPTION '%.%: company, plant and code cannot change', TG_TABLE_SCHEMA, TG_TABLE_NAME;
  END IF;
  IF NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION '%.%: version must increase by exactly 1', TG_TABLE_SCHEMA, TG_TABLE_NAME;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER machine_guard BEFORE INSERT OR UPDATE OR DELETE ON md.machine FOR EACH ROW EXECUTE FUNCTION mfg.master_guard();
CREATE TRIGGER machine_no_truncate BEFORE TRUNCATE ON md.machine FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER shift_guard BEFORE INSERT OR UPDATE OR DELETE ON mfg.shift FOR EACH ROW EXECUTE FUNCTION mfg.master_guard();
CREATE TRIGGER shift_no_truncate BEFORE TRUNCATE ON mfg.shift FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
CREATE CONSTRAINT TRIGGER machine_evidence_on_insert AFTER INSERT ON md.machine
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('Machine', 'machine_id');
CREATE CONSTRAINT TRIGGER machine_evidence_on_change AFTER UPDATE ON md.machine
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('Machine', 'machine_id');
CREATE CONSTRAINT TRIGGER shift_evidence_on_insert AFTER INSERT ON mfg.shift
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('Shift', 'shift_id');
CREATE CONSTRAINT TRIGGER shift_evidence_on_change AFTER UPDATE ON mfg.shift
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('Shift', 'shift_id');

-- ---------------------------------------------------------------------------------------------
-- Recipes (E-MFG1-01-2, E-MFG1-01-5): per finished good × machine, versioned, prepared by the supervisor and approved by the
-- plant manager; the version carries the product × machine configuration (units per batch, per cycle, per rack) and the curing
-- window. One ACTIVE version per product × machine; content changes only while DRAFT.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE mfg.recipe_version (
  recipe_version_id  uuid          NOT NULL,
  company_id         uuid          NOT NULL,
  item_id            uuid          NOT NULL,
  machine_id         uuid          NOT NULL,
  version            integer       NOT NULL,
  effective_from     date          NOT NULL,
  units_per_batch    numeric(18,6) NOT NULL,
  units_per_cycle    numeric(18,6) NOT NULL,
  units_per_rack     numeric(18,6) NOT NULL,
  min_curing_hours   integer       NOT NULL,
  max_curing_hours   integer       NOT NULL,
  status             text          NOT NULL,
  prepared_by        uuid          NOT NULL,
  approved_by        uuid,
  CONSTRAINT recipe_version_pk PRIMARY KEY (recipe_version_id),
  CONSTRAINT recipe_version_company_uq UNIQUE (company_id, recipe_version_id),
  CONSTRAINT recipe_version_no_uq UNIQUE (company_id, item_id, machine_id, version),
  CONSTRAINT recipe_version_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT recipe_version_machine_fk FOREIGN KEY (company_id, machine_id) REFERENCES md.machine (company_id, machine_id),
  CONSTRAINT recipe_version_prepared_fk FOREIGN KEY (prepared_by) REFERENCES iam.user (user_id),
  CONSTRAINT recipe_version_approved_fk FOREIGN KEY (approved_by) REFERENCES iam.user (user_id),
  CONSTRAINT recipe_version_positive CHECK (version >= 1),
  CONSTRAINT recipe_units CHECK (units_per_batch > 0 AND units_per_cycle > 0 AND units_per_rack > 0),
  CONSTRAINT recipe_curing CHECK (min_curing_hours >= 0 AND max_curing_hours > min_curing_hours),
  CONSTRAINT recipe_status CHECK (status IN ('DRAFT', 'ACTIVE', 'SUPERSEDED')),
  CONSTRAINT recipe_four_eyes CHECK (approved_by IS NULL OR approved_by <> prepared_by),
  CONSTRAINT recipe_approved CHECK ((status IN ('ACTIVE', 'SUPERSEDED')) = (approved_by IS NOT NULL))
);
CREATE UNIQUE INDEX recipe_one_active ON mfg.recipe_version (company_id, item_id, machine_id) WHERE status = 'ACTIVE';

CREATE TABLE mfg.recipe_line (
  recipe_version_id  uuid          NOT NULL,
  company_id         uuid          NOT NULL,
  material_item_id   uuid          NOT NULL,
  qty_per_batch      numeric(18,6) NOT NULL,
  CONSTRAINT recipe_line_pk PRIMARY KEY (recipe_version_id, material_item_id),
  CONSTRAINT recipe_line_version_fk FOREIGN KEY (company_id, recipe_version_id) REFERENCES mfg.recipe_version (company_id, recipe_version_id),
  CONSTRAINT recipe_line_item_fk FOREIGN KEY (company_id, material_item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT recipe_line_qty CHECK (qty_per_batch > 0)
);

CREATE FUNCTION mfg.recipe_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'DRAFT' THEN
      RAISE EXCEPTION 'mfg.recipe_version: a version is prepared as DRAFT';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM md.item i WHERE i.item_id = NEW.item_id AND i.item_type = 'FINISHED_GOOD') THEN
      RAISE EXCEPTION 'mfg.recipe_version: item % is not a finished good', NEW.item_id;
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'mfg.recipe_version rows cannot be deleted';
  END IF;
  IF ROW(NEW.recipe_version_id, NEW.company_id, NEW.item_id, NEW.machine_id, NEW.version, NEW.prepared_by)
     IS DISTINCT FROM ROW(OLD.recipe_version_id, OLD.company_id, OLD.item_id, OLD.machine_id, OLD.version, OLD.prepared_by)
     OR NOT sal.version_transition_allowed(OLD.status, NEW.status)
     OR (OLD.status <> 'DRAFT' AND ROW(NEW.effective_from, NEW.units_per_batch, NEW.units_per_cycle, NEW.units_per_rack, NEW.min_curing_hours, NEW.max_curing_hours)
                                   IS DISTINCT FROM ROW(OLD.effective_from, OLD.units_per_batch, OLD.units_per_cycle, OLD.units_per_rack, OLD.min_curing_hours, OLD.max_curing_hours)) THEN
    RAISE EXCEPTION 'mfg.recipe_version: % → % or a change of an approved version is not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER recipe_guard BEFORE INSERT OR UPDATE OR DELETE ON mfg.recipe_version FOR EACH ROW EXECUTE FUNCTION mfg.recipe_guard();
CREATE TRIGGER recipe_no_truncate BEFORE TRUNCATE ON mfg.recipe_version FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
CREATE CONSTRAINT TRIGGER recipe_evidence_on_insert AFTER INSERT ON mfg.recipe_version
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('Recipe', 'recipe_version_id');
CREATE CONSTRAINT TRIGGER recipe_evidence_on_change AFTER UPDATE ON mfg.recipe_version
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('Recipe', 'recipe_version_id');

-- Lines of a DRAFT recipe only, of raw materials only, never changed or deleted (a correction is a new version).
CREATE FUNCTION mfg.recipe_line_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'INSERT' THEN
    RAISE EXCEPTION 'mfg.recipe_line rows cannot be changed or deleted; prepare a new version';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM mfg.recipe_version v WHERE v.recipe_version_id = NEW.recipe_version_id AND v.status = 'DRAFT') THEN
    RAISE EXCEPTION 'mfg.recipe_line: lines are added only to a DRAFT version';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM md.item i WHERE i.item_id = NEW.material_item_id AND i.item_type = 'RAW_MATERIAL') THEN
    RAISE EXCEPTION 'mfg.recipe_line: item % is not a raw material', NEW.material_item_id;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER recipe_line_guard BEFORE INSERT OR UPDATE OR DELETE ON mfg.recipe_line FOR EACH ROW EXECUTE FUNCTION mfg.recipe_line_guard();
CREATE TRIGGER recipe_line_no_truncate BEFORE TRUNCATE ON mfg.recipe_line FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- Standard cost breakdown (E-MFG1-9, E-MFG1-01-6): material + conversion = unit cost, or both NULL (the VS#3 versions).
-- The material lines are filled while DRAFT, from the recipe, at the Controller's standard prices.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE md.standard_cost_version
  ADD COLUMN material_cost numeric(19,4),
  ADD COLUMN conversion_cost numeric(19,4),
  ADD CONSTRAINT standard_cost_breakdown CHECK (
    (material_cost IS NULL AND conversion_cost IS NULL)
    OR (material_cost IS NOT NULL AND conversion_cost IS NOT NULL
        AND material_cost >= 0 AND conversion_cost >= 0 AND material_cost + conversion_cost = unit_cost));

CREATE OR REPLACE FUNCTION md.standard_cost_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'DRAFT' THEN
      RAISE EXCEPTION 'md.standard_cost_version: a version is prepared as DRAFT';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM md.item i WHERE i.item_id = NEW.item_id AND i.item_type = 'FINISHED_GOOD') THEN
      RAISE EXCEPTION 'md.standard_cost_version: item % is not a finished good', NEW.item_id;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM md.valuation_area a WHERE a.valuation_area_id = NEW.valuation_area_id AND a.company_id = NEW.company_id) THEN
      RAISE EXCEPTION 'md.standard_cost_version: valuation area % is not of the company', NEW.valuation_area_id;
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'md.standard_cost_version rows cannot be deleted';
  END IF;
  IF ROW(NEW.cost_version_id, NEW.company_id, NEW.item_id, NEW.valuation_area_id, NEW.version, NEW.prepared_by)
     IS DISTINCT FROM ROW(OLD.cost_version_id, OLD.company_id, OLD.item_id, OLD.valuation_area_id, OLD.version, OLD.prepared_by)
     OR NOT sal.version_transition_allowed(OLD.status, NEW.status)
     OR (OLD.status <> 'DRAFT' AND ROW(NEW.effective_from, NEW.unit_cost, NEW.material_cost, NEW.conversion_cost)
                                   IS DISTINCT FROM ROW(OLD.effective_from, OLD.unit_cost, OLD.material_cost, OLD.conversion_cost)) THEN
    RAISE EXCEPTION 'md.standard_cost_version: % → % or a change of an approved version is not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;

CREATE TABLE md.standard_cost_material (
  cost_version_id    uuid          NOT NULL,
  company_id         uuid          NOT NULL,
  material_item_id   uuid          NOT NULL,
  std_qty_per_unit   numeric(18,6) NOT NULL,
  std_price          numeric(19,4) NOT NULL,
  CONSTRAINT standard_cost_material_pk PRIMARY KEY (cost_version_id, material_item_id),
  CONSTRAINT standard_cost_material_version_fk FOREIGN KEY (company_id, cost_version_id) REFERENCES md.standard_cost_version (company_id, cost_version_id),
  CONSTRAINT standard_cost_material_item_fk FOREIGN KEY (company_id, material_item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT standard_cost_material_qty CHECK (std_qty_per_unit > 0),
  CONSTRAINT standard_cost_material_price CHECK (std_price > 0)
);

CREATE FUNCTION md.standard_cost_material_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'INSERT' THEN
    RAISE EXCEPTION 'md.standard_cost_material rows cannot be changed or deleted; prepare a new version';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM md.standard_cost_version v WHERE v.cost_version_id = NEW.cost_version_id AND v.status = 'DRAFT') THEN
    RAISE EXCEPTION 'md.standard_cost_material: lines are added only to a DRAFT version';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM md.item i WHERE i.item_id = NEW.material_item_id AND i.item_type = 'RAW_MATERIAL') THEN
    RAISE EXCEPTION 'md.standard_cost_material: item % is not a raw material', NEW.material_item_id;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER standard_cost_material_guard BEFORE INSERT OR UPDATE OR DELETE ON md.standard_cost_material
  FOR EACH ROW EXECUTE FUNCTION md.standard_cost_material_guard();
CREATE TRIGGER standard_cost_material_no_truncate BEFORE TRUNCATE ON md.standard_cost_material FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- E-MFG1-01-7: one system location CURADO per plant, created by the first production; never a dispatch source (MFG1-04).
-- ---------------------------------------------------------------------------------------------
ALTER TABLE md.location ADD COLUMN is_curing boolean NOT NULL DEFAULT false;
ALTER TABLE md.location ADD CONSTRAINT location_curing_code CHECK (NOT is_curing OR (code = 'CURADO' AND NOT is_transit));
CREATE UNIQUE INDEX location_one_curing_per_plant ON md.location (plant_id) WHERE is_curing;
GRANT INSERT (is_curing) ON md.location TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- E-MFG1-01-8: production movement types. Text comparisons in the CHECKs (the new values cannot be used as literals here).
-- ---------------------------------------------------------------------------------------------
ALTER TYPE inv.movement_type ADD VALUE 'PRODUCTION_ISSUE';
ALTER TYPE inv.movement_type ADD VALUE 'PRODUCTION_RECEIPT';

ALTER TABLE inv.inv_quantity_entry DROP CONSTRAINT inv_quantity_entry_sign;
ALTER TABLE inv.inv_quantity_entry ADD CONSTRAINT inv_quantity_entry_sign CHECK (
  (movement_type::text IN ('RECEIPT', 'OPENING', 'PRODUCTION_RECEIPT') AND quantity > 0) OR
  (movement_type::text IN ('ISSUE', 'RECEIPT_REVERSAL', 'PRODUCTION_ISSUE') AND quantity < 0) OR
  movement_type::text IN ('RECEIPT_CORRECTION', 'TRANSFER'));
ALTER TABLE inv.inv_value_entry DROP CONSTRAINT inv_value_entry_sign;
ALTER TABLE inv.inv_value_entry ADD CONSTRAINT inv_value_entry_sign CHECK (
  (movement_type::text IN ('RECEIPT', 'OPENING', 'PRODUCTION_RECEIPT') AND amount > 0) OR
  (movement_type::text IN ('ISSUE', 'RECEIPT_REVERSAL', 'PRODUCTION_ISSUE') AND amount < 0) OR
  movement_type::text IN ('RECEIPT_CORRECTION', 'VALUATION_ADJUSTMENT', 'VALUATION_REALLOCATION', 'PRICE_ADJUSTMENT', 'REPOST', 'RESIDUAL_ADJUSTMENT', 'TRANSFER'));

-- ---------------------------------------------------------------------------------------------
-- E-MFG1-01-9: account roles of manufacturing, seeded unmapped. WIP is a control account of the new subledger WIP (one per
-- cost collector).
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.account_role (role_code, is_control, description) VALUES
  ('WIP', true, 'Producción en proceso (subledger WIP, por cost collector)'),
  ('CONVERSION_ABSORPTION', false, 'Absorción de costos de conversión a estándar'),
  ('MATERIAL_PRICE_VARIANCE', false, 'Variación de precio de materiales en producción'),
  ('PRODUCTION_SCRAP', false, 'Scrap de producto terminado en curado o patio'),
  ('STANDARD_REVALUATION', false, 'Revaluación de existencias al cambiar el costo estándar');

ALTER TABLE fin.gl_entry
  DROP CONSTRAINT gl_entry_subledger_type,
  ADD CONSTRAINT gl_entry_subledger_type CHECK (subledger_type IS NULL OR subledger_type IN ('AP', 'INV', 'BANK', 'AR', 'WIP')),
  DROP CONSTRAINT gl_entry_role_subledger,
  ADD CONSTRAINT gl_entry_role_subledger CHECK (
    (account_role NOT IN ('RAW_MATERIAL', 'FINISHED_GOODS', 'FINISHED_GOODS_IN_TRANSIT') OR subledger_type = 'INV')
    AND (account_role <> 'AP_CONTROL' OR subledger_type = 'AP')
    AND (account_role NOT IN ('AR_CONTROL', 'CONTRACT_ASSET', 'UNBILLED_RECEIVABLE', 'UNAPPLIED_RECEIPTS', 'CASH_IN_TRANSIT') OR subledger_type = 'AR')
    AND ((account_role = 'WIP') = (subledger_type IS NOT DISTINCT FROM 'WIP'))
    AND ((account_role = 'BANK') = (subledger_type IS NOT DISTINCT FROM 'BANK')));

-- ---------------------------------------------------------------------------------------------
-- Roles, permissions and segregation of duties (E-MFG1-14, E-MFG1-01-10, E-MFG1-01-11).
-- ---------------------------------------------------------------------------------------------
INSERT INTO iam.permission (permission_code, access) VALUES
  ('production_master:manage', 'WRITE'), ('recipe:prepare', 'WRITE'), ('recipe:approve', 'WRITE'),
  ('production_run:manage', 'WRITE'), ('shift_summary:record', 'WRITE'), ('shift_summary:post', 'WRITE'),
  ('fg_lot:release', 'WRITE'), ('fg_lot:scrap', 'WRITE'), ('cost_collector:settle', 'WRITE'),
  ('production:read', 'READ');

INSERT INTO iam.role (role_id, code, name) VALUES
  (gen_random_uuid(), 'SUPERVISOR_PRODUCCION', 'Supervisor de producción'),
  (gen_random_uuid(), 'GERENTE_PLANTA', 'Gerente de planta'),
  (gen_random_uuid(), 'CALIDAD', 'Calidad');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES
  ('GERENTE_PLANTA', 'production_master:manage'), ('GERENTE_PLANTA', 'recipe:approve'),
  ('GERENTE_PLANTA', 'shift_summary:post'), ('GERENTE_PLANTA', 'fg_lot:scrap'),
  ('SUPERVISOR_PRODUCCION', 'recipe:prepare'), ('SUPERVISOR_PRODUCCION', 'production_run:manage'),
  ('SUPERVISOR_PRODUCCION', 'shift_summary:record'),
  ('CALIDAD', 'fg_lot:release'),
  ('CONTROLLER', 'cost_collector:settle'),
  ('SUPERVISOR_PRODUCCION', 'production:read'), ('GERENTE_PLANTA', 'production:read'), ('CALIDAD', 'production:read'),
  ('CONTROLLER', 'production:read'), ('AUDITOR', 'production:read'), ('DIRECTOR', 'production:read')
) AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;

INSERT INTO iam.sod_rule (permission_a, permission_b)
SELECT least(a, b), greatest(a, b) FROM (VALUES
  ('recipe:prepare', 'recipe:approve'),
  ('shift_summary:record', 'shift_summary:post'),
  ('shift_summary:record', 'fg_lot:release'),
  ('cost_collector:settle', 'standard_cost:approve')
) AS v (a, b);

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['md.machine', 'mfg.shift', 'mfg.recipe_version', 'mfg.recipe_line', 'md.standard_cost_material'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON md.machine, mfg.shift, mfg.recipe_version, mfg.recipe_line, md.standard_cost_material TO rochell_app;
GRANT UPDATE (name, status, version) ON md.machine TO rochell_app;
GRANT UPDATE (starts_at, ends_at, status, version) ON mfg.shift TO rochell_app;
GRANT UPDATE (effective_from, units_per_batch, units_per_cycle, units_per_rack, min_curing_hours, max_curing_hours, status, approved_by)
  ON mfg.recipe_version TO rochell_app;
GRANT UPDATE (material_cost, conversion_cost) ON md.standard_cost_version TO rochell_app;
