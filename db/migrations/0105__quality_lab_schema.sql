-- LAB1-01 · quality lab: schema, the lot's field code and the machine's short code (approved errata E-LAB1-1…10, E-LAB1-01-1…15).
--   - Schema `qa` (E-LAB1-01-1): per-item requirements in versions (E-LAB1-01-2), the lab's parameters with their history (E-LAB1-01-3),
--     failure types (E-LAB1-01-11), compression tests one row per specimen (E-LAB1-01-6…10) and absorption tests one row per block
--     (E-LAB1-01-12). A test is never edited: RECORDED → VOIDED with a reason (E-LAB1-01-9).
--   - Parameters and failure types start from values every company shares (the validated Excel's); a company's own value replaces them.
--   - `mfg.fg_lot.field_code` (E-LAB1-1, E-LAB1-01-4/5), unique among live lots, set once; `md.machine.short_code` (E-LAB1-3).
--   - lab_test:record, fg_lot:final_release, lab_spec:manage, lab:read and the role LABORATORIO (E-LAB1-9, E-LAB1-01-13; 153 permissions).
--   The evaluation, FINAL_RELEASED, certificates and the history before Core arrive with their PRs (E-LAB1-01-15).

CREATE SCHEMA qa;
GRANT USAGE ON SCHEMA qa TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- E-LAB1-3: the machine's short code (P1, P2, P3), unique per company.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE md.machine
  ADD COLUMN short_code text,
  ADD CONSTRAINT machine_short_code_format CHECK (short_code ~ '^[A-Z0-9]{1,6}$'),
  ADD CONSTRAINT machine_short_code_uq UNIQUE (company_id, short_code);
GRANT UPDATE (short_code) ON md.machine TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- E-LAB1-1, E-LAB1-01-4/5: <item's lot prefix><DDMMYY><machine short code>[-<shift>]. A lot may wait for it; once set it never changes.
-- A VOIDED lot keeps its code and the lot of the run redone after the reversal reuses it.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE mfg.fg_lot
  ADD COLUMN field_code text,
  ADD CONSTRAINT fg_lot_field_code_format CHECK (field_code ~ '^[A-Z0-9]{1,4}[0-9]{6}[A-Z0-9]{1,6}(-[A-Z0-9_-]{1,20})?$');
CREATE UNIQUE INDEX fg_lot_field_code_live ON mfg.fg_lot (company_id, field_code) WHERE status <> 'VOIDED';
GRANT UPDATE (field_code) ON mfg.fg_lot TO rochell_app;

CREATE OR REPLACE FUNCTION mfg.fg_lot_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF ROW(NEW.lot_id, NEW.company_id, NEW.summary_id, NEW.run_id, NEW.curing_from, NEW.releasable_at)
     IS DISTINCT FROM ROW(OLD.lot_id, OLD.company_id, OLD.summary_id, OLD.run_id, OLD.curing_from, OLD.releasable_at)
     OR NEW.version <> OLD.version + 1
     OR (OLD.field_code IS NOT NULL AND NEW.field_code IS DISTINCT FROM OLD.field_code)
     OR NOT ((OLD.status = 'CURING' AND NEW.status IN ('RELEASED', 'BLOCKED', 'SCRAPPED', 'VOIDED'))
             OR (OLD.status = 'BLOCKED' AND NEW.status IN ('CURING', 'SCRAPPED'))
             OR (OLD.status = 'RELEASED' AND NEW.status = 'SCRAPPED')
             OR (OLD.status = NEW.status AND OLD.field_code IS NULL AND NEW.field_code IS NOT NULL)) THEN
    RAISE EXCEPTION 'mfg.fg_lot: % → % or a change of the lot''s identity is not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;

-- ---------------------------------------------------------------------------------------------
-- E-LAB1-01-2: requirements per item. Every save is a new version; the highest one counts.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE qa.item_spec (
  company_id          uuid          NOT NULL,
  item_id             uuid          NOT NULL,
  version             integer       NOT NULL,
  lot_prefix          text          NOT NULL,
  nominal_width_cm    numeric(18,6) NOT NULL,
  nominal_height_cm   numeric(18,6) NOT NULL,
  nominal_length_cm   numeric(18,6) NOT NULL,
  net_area_fraction   numeric(7,6),
  min_avg_28d         numeric(18,6),
  min_individual_28d  numeric(18,6),
  set_by              uuid          NOT NULL,
  set_at              timestamptz   NOT NULL,
  CONSTRAINT item_spec_pk PRIMARY KEY (company_id, item_id, version),
  CONSTRAINT item_spec_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT item_spec_set_by_fk FOREIGN KEY (set_by) REFERENCES iam.user (user_id),
  CONSTRAINT item_spec_version CHECK (version >= 1),
  CONSTRAINT item_spec_prefix CHECK (lot_prefix ~ '^[A-Z0-9]{1,4}$'),
  CONSTRAINT item_spec_nominal CHECK (nominal_width_cm > 0 AND nominal_height_cm > 0 AND nominal_length_cm > 0),
  CONSTRAINT item_spec_net_area CHECK (net_area_fraction IS NULL OR (net_area_fraction > 0 AND net_area_fraction <= 1)),
  CONSTRAINT item_spec_minimums CHECK ((min_avg_28d IS NULL OR min_avg_28d > 0) AND (min_individual_28d IS NULL OR min_individual_28d > 0)
    AND (min_individual_28d IS NULL OR min_avg_28d IS NOT NULL))
);
CREATE TRIGGER item_spec_append_only BEFORE UPDATE OR DELETE ON qa.item_spec FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- E-LAB1-01-3: the lab's parameters. The starting values are the validated Excel's (CONFIG and CURVA EDAD) and are shared; a company's
-- own value is appended (never changed) and the latest one counts.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE qa.parameter_default (
  code          text          NOT NULL,
  name          text          NOT NULL,
  kind          text          NOT NULL,
  number_value  numeric(18,8),
  text_value    text,
  min_value     numeric(18,8),
  max_value     numeric(18,8),
  whole         boolean       NOT NULL DEFAULT false,
  sort_order    integer       NOT NULL,
  CONSTRAINT parameter_default_pk PRIMARY KEY (code),
  CONSTRAINT parameter_default_code CHECK (code ~ '^[A-Z0-9_]{1,40}$'),
  CONSTRAINT parameter_default_kind CHECK ((kind = 'NUMBER' AND number_value IS NOT NULL AND text_value IS NULL AND min_value IS NOT NULL AND max_value IS NOT NULL)
    OR (kind = 'TEXT' AND text_value IS NOT NULL AND number_value IS NULL))
);
CREATE TRIGGER parameter_default_fixed BEFORE UPDATE OR DELETE ON qa.parameter_default FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

INSERT INTO qa.parameter_default (code, name, kind, number_value, text_value, min_value, max_value, whole, sort_order) VALUES
  ('MAX_CV', 'CV máximo del lote (fracción)', 'NUMBER', 0.15, NULL, 0.01, 1, false, 10),
  ('MIN_SPECIMENS', 'Mínimo de probetas por lote', 'NUMBER', 3, NULL, 1, 100, true, 20),
  ('AGE_28D_MIN_DAYS', 'Edad mínima que cuenta como 28 días', 'NUMBER', 26, NULL, 1, 28, true, 30),
  ('OWN_FACTOR_MIN_LOTS', 'Lotes necesarios para usar el factor de edad propio', 'NUMBER', 2, NULL, 1, 100, true, 40),
  ('KGCM2_TO_MPA', 'Conversión de kg/cm² a MPa', 'NUMBER', 0.0980665, NULL, 0.09, 0.1, false, 50),
  ('DENSITY_MEDIUM_FROM', 'Densidad desde la que el bloque es de peso mediano (kg/m³)', 'NUMBER', 1680, NULL, 500, 4000, false, 60),
  ('DENSITY_NORMAL_FROM', 'Densidad desde la que el bloque es de peso normal (kg/m³)', 'NUMBER', 2000, NULL, 500, 4000, false, 70),
  ('ABSORPTION_MAX_LIGHT', 'Absorción máxima, peso liviano (kg/m³)', 'NUMBER', 288, NULL, 1, 1000, false, 80),
  ('ABSORPTION_MAX_MEDIUM', 'Absorción máxima, peso mediano (kg/m³)', 'NUMBER', 240, NULL, 1, 1000, false, 90),
  ('ABSORPTION_MAX_NORMAL', 'Absorción máxima, peso normal (kg/m³)', 'NUMBER', 208, NULL, 1, 1000, false, 100),
  ('EQUIPMENT_BRAND', 'Prensa: marca', 'TEXT', NULL, 'TEST MARK', NULL, NULL, false, 110),
  ('EQUIPMENT_MODEL', 'Prensa: modelo', 'TEXT', NULL, 'CM-2500-iD', NULL, NULL, false, 120),
  ('EQUIPMENT_SERIAL', 'Prensa: serie', 'TEXT', NULL, '220808', NULL, NULL, false, 130);

-- Initial age factors, 1 to 28 days (baseline §4.3: the Excel's table, already interpolated).
INSERT INTO qa.parameter_default (code, name, kind, number_value, text_value, min_value, max_value, whole, sort_order)
SELECT 'AGE_FACTOR_' || to_char(d, 'FM00'), 'Factor de edad inicial, ' || d || CASE WHEN d = 1 THEN ' día' ELSE ' días' END, 'NUMBER', f, NULL, 0.01, 1, false, 200 + d
FROM unnest(ARRAY[0.70, 0.77, 0.84, 0.87, 0.89, 0.90, 0.91, 0.917, 0.924, 0.931, 0.939, 0.946, 0.953, 0.96,
                  0.963, 0.966, 0.969, 0.971, 0.974, 0.977, 0.98, 0.983, 0.986, 0.989, 0.991, 0.994, 0.997, 1.00]) WITH ORDINALITY AS t (f, d);

CREATE TABLE qa.parameter_value (
  value_id      uuid          NOT NULL,
  company_id    uuid          NOT NULL,
  code          text          NOT NULL,
  number_value  numeric(18,8),
  text_value    text,
  set_by        uuid          NOT NULL,
  set_at        timestamptz   NOT NULL,
  CONSTRAINT parameter_value_pk PRIMARY KEY (value_id),
  CONSTRAINT parameter_value_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT parameter_value_code_fk FOREIGN KEY (code) REFERENCES qa.parameter_default (code),
  CONSTRAINT parameter_value_set_by_fk FOREIGN KEY (set_by) REFERENCES iam.user (user_id),
  CONSTRAINT parameter_value_one CHECK ((number_value IS NULL) <> (text_value IS NULL) AND (text_value IS NULL OR length(btrim(text_value)) BETWEEN 1 AND 120))
);
CREATE INDEX parameter_value_latest ON qa.parameter_value (company_id, code, set_at DESC, value_id DESC);
CREATE TRIGGER parameter_value_append_only BEFORE UPDATE OR DELETE ON qa.parameter_value FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

-- The value in force for a company: its latest own value, else the shared one.
CREATE FUNCTION qa.parameter_number(company uuid, parameter text) RETURNS numeric
  LANGUAGE sql STABLE AS $$
  SELECT coalesce(
    (SELECT v.number_value FROM qa.parameter_value v WHERE v.company_id = company AND v.code = parameter ORDER BY v.set_at DESC, v.value_id DESC LIMIT 1),
    (SELECT d.number_value FROM qa.parameter_default d WHERE d.code = parameter))
$$;
CREATE FUNCTION qa.parameter_text(company uuid, parameter text) RETURNS text
  LANGUAGE sql STABLE AS $$
  SELECT coalesce(
    (SELECT v.text_value FROM qa.parameter_value v WHERE v.company_id = company AND v.code = parameter ORDER BY v.set_at DESC, v.value_id DESC LIMIT 1),
    (SELECT d.text_value FROM qa.parameter_default d WHERE d.code = parameter))
$$;

-- ---------------------------------------------------------------------------------------------
-- E-LAB1-01-11: failure types. The Excel's seven are shared; a company adds its own, renames or deactivates any of them.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE qa.failure_type_default (
  code        text    NOT NULL,
  name        text    NOT NULL,
  sort_order  integer NOT NULL,
  CONSTRAINT failure_type_default_pk PRIMARY KEY (code)
);
CREATE TRIGGER failure_type_default_fixed BEFORE UPDATE OR DELETE ON qa.failure_type_default FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
INSERT INTO qa.failure_type_default (code, name, sort_order) VALUES
  ('CONICA', 'Cónica', 1), ('CONO_CORTE', 'Cono y corte', 2), ('CORTE_DIAGONAL', 'Corte / diagonal', 3), ('COLUMNAR', 'Columnar / vertical', 4),
  ('DESPRENDIMIENTO_CARA', 'Desprendimiento de cara', 5), ('APLASTAMIENTO_LOCAL', 'Aplastamiento local', 6), ('OTRA', 'Otra', 7);

CREATE TABLE qa.failure_type (
  company_id  uuid   NOT NULL,
  code        text   NOT NULL,
  name        text   NOT NULL,
  status      text   NOT NULL,
  version     bigint NOT NULL,
  CONSTRAINT failure_type_pk PRIMARY KEY (company_id, code),
  CONSTRAINT failure_type_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT failure_type_code CHECK (code ~ '^[A-Z0-9][A-Z0-9_]{0,29}$'),
  CONSTRAINT failure_type_name CHECK (length(btrim(name)) BETWEEN 1 AND 80),
  CONSTRAINT failure_type_status CHECK (status IN ('ACTIVE', 'INACTIVE')),
  CONSTRAINT failure_type_version CHECK (version >= 1)
);
CREATE TRIGGER failure_type_no_delete BEFORE DELETE ON qa.failure_type FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- E-LAB1-01-6…10: compression tests, one row per specimen. Measures are kept as entered (NULL = the item's nominal one, from
-- `spec_version`); gross area and gross strength are the ones computed when recorded.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE qa.compression_test (
  test_id          uuid          NOT NULL,
  company_id       uuid          NOT NULL,
  lot_id           uuid          NOT NULL,
  batch_id         uuid          NOT NULL,
  line_no          integer       NOT NULL,
  break_date       date          NOT NULL,
  age_days         integer       NOT NULL,
  width_cm         numeric(18,6),
  height_cm        numeric(18,6),
  length_cm        numeric(18,6),
  nominal_used     boolean       NOT NULL,
  spec_version     integer,
  weight_kg        numeric(18,6),
  load_kg          numeric(18,6) NOT NULL,
  gross_area_cm2   numeric(18,6) NOT NULL,
  strength_kgcm2   numeric(18,6) NOT NULL,
  block_condition  text,
  failure_type     text,
  notes            text,
  tested_by        uuid          NOT NULL,
  recorded_at      timestamptz   NOT NULL,
  status           text          NOT NULL,
  void_reason      text,
  voided_by        uuid,
  voided_at        timestamptz,
  CONSTRAINT compression_test_pk PRIMARY KEY (test_id),
  CONSTRAINT compression_test_line_uq UNIQUE (batch_id, line_no),
  CONSTRAINT compression_test_lot_fk FOREIGN KEY (company_id, lot_id) REFERENCES mfg.fg_lot (company_id, lot_id),
  CONSTRAINT compression_test_tested_by_fk FOREIGN KEY (tested_by) REFERENCES iam.user (user_id),
  CONSTRAINT compression_test_voided_by_fk FOREIGN KEY (voided_by) REFERENCES iam.user (user_id),
  CONSTRAINT compression_test_age CHECK (age_days >= 0 AND line_no >= 1),
  CONSTRAINT compression_test_measures CHECK ((width_cm IS NULL OR width_cm > 0) AND (height_cm IS NULL OR height_cm > 0) AND (length_cm IS NULL OR length_cm > 0)
    AND (weight_kg IS NULL OR weight_kg > 0) AND load_kg > 0 AND gross_area_cm2 > 0 AND strength_kgcm2 > 0),
  CONSTRAINT compression_test_nominal CHECK (nominal_used = (width_cm IS NULL OR height_cm IS NULL OR length_cm IS NULL) AND (NOT nominal_used OR spec_version IS NOT NULL)),
  CONSTRAINT compression_test_condition CHECK (block_condition IN ('SECO_AL_AIRE', 'HUMEDO', 'SATURADO')),
  CONSTRAINT compression_test_notes CHECK (notes IS NULL OR length(btrim(notes)) BETWEEN 1 AND 500),
  CONSTRAINT compression_test_status CHECK (status IN ('RECORDED', 'VOIDED')),
  CONSTRAINT compression_test_void CHECK ((status = 'VOIDED') = (void_reason IS NOT NULL) AND (void_reason IS NULL) = (voided_by IS NULL) AND (void_reason IS NULL) = (voided_at IS NULL)
    AND (void_reason IS NULL OR length(btrim(void_reason)) BETWEEN 1 AND 500))
);
CREATE INDEX compression_test_lot ON qa.compression_test (company_id, lot_id, break_date, recorded_at, line_no);

-- ---------------------------------------------------------------------------------------------
-- E-LAB1-01-12: absorption tests, one row per block (ASTM C140). Ws saturated, Wi immersed, Wd oven-dry, in kg.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE qa.absorption_test (
  test_id          uuid          NOT NULL,
  company_id       uuid          NOT NULL,
  lot_id           uuid          NOT NULL,
  batch_id         uuid          NOT NULL,
  line_no          integer       NOT NULL,
  test_date        date          NOT NULL,
  ws_kg            numeric(18,6) NOT NULL,
  wi_kg            numeric(18,6) NOT NULL,
  wd_kg            numeric(18,6) NOT NULL,
  absorption_kgm3  numeric(18,6) GENERATED ALWAYS AS (round((ws_kg - wd_kg) / (ws_kg - wi_kg) * 1000, 6)) STORED,
  absorption_fraction numeric(18,6) GENERATED ALWAYS AS (round((ws_kg - wd_kg) / wd_kg, 6)) STORED,
  density_kgm3     numeric(18,6) GENERATED ALWAYS AS (round(wd_kg / (ws_kg - wi_kg) * 1000, 6)) STORED,
  notes            text,
  tested_by        uuid          NOT NULL,
  recorded_at      timestamptz   NOT NULL,
  status           text          NOT NULL,
  void_reason      text,
  voided_by        uuid,
  voided_at        timestamptz,
  CONSTRAINT absorption_test_pk PRIMARY KEY (test_id),
  CONSTRAINT absorption_test_line_uq UNIQUE (batch_id, line_no),
  CONSTRAINT absorption_test_lot_fk FOREIGN KEY (company_id, lot_id) REFERENCES mfg.fg_lot (company_id, lot_id),
  CONSTRAINT absorption_test_tested_by_fk FOREIGN KEY (tested_by) REFERENCES iam.user (user_id),
  CONSTRAINT absorption_test_voided_by_fk FOREIGN KEY (voided_by) REFERENCES iam.user (user_id),
  CONSTRAINT absorption_test_weights CHECK (ws_kg > wi_kg AND ws_kg >= wd_kg AND wd_kg > 0 AND wi_kg >= 0 AND line_no >= 1),
  CONSTRAINT absorption_test_notes CHECK (notes IS NULL OR length(btrim(notes)) BETWEEN 1 AND 500),
  CONSTRAINT absorption_test_status CHECK (status IN ('RECORDED', 'VOIDED')),
  CONSTRAINT absorption_test_void CHECK ((status = 'VOIDED') = (void_reason IS NOT NULL) AND (void_reason IS NULL) = (voided_by IS NULL) AND (void_reason IS NULL) = (voided_at IS NULL)
    AND (void_reason IS NULL OR length(btrim(void_reason)) BETWEEN 1 AND 500))
);
CREATE INDEX absorption_test_lot ON qa.absorption_test (company_id, lot_id, test_date, recorded_at, line_no);

-- E-LAB1-01-9: a test is registered RECORDED and only becomes VOIDED, once; nothing else of it changes; it is never deleted.
CREATE FUNCTION qa.test_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  -- What voiding writes, and the generated columns (not computed yet in a BEFORE trigger; their inputs are compared).
  changing CONSTANT text[] := ARRAY['status', 'void_reason', 'voided_by', 'voided_at', 'absorption_kgm3', 'absorption_fraction', 'density_kgm3'];
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION '%.% rows cannot be deleted; void the test', TG_TABLE_SCHEMA, TG_TABLE_NAME;
  END IF;
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'RECORDED' THEN
      RAISE EXCEPTION '%.%: a test is registered RECORDED', TG_TABLE_SCHEMA, TG_TABLE_NAME;
    END IF;
    RETURN NEW;
  END IF;
  IF OLD.status <> 'RECORDED' OR NEW.status <> 'VOIDED'
     OR (to_jsonb(NEW) - changing) IS DISTINCT FROM (to_jsonb(OLD) - changing) THEN
    RAISE EXCEPTION '%.%: a test only goes from RECORDED to VOIDED', TG_TABLE_SCHEMA, TG_TABLE_NAME;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER compression_test_guard BEFORE INSERT OR UPDATE OR DELETE ON qa.compression_test FOR EACH ROW EXECUTE FUNCTION qa.test_guard();
CREATE TRIGGER absorption_test_guard BEFORE INSERT OR UPDATE OR DELETE ON qa.absorption_test FOR EACH ROW EXECUTE FUNCTION qa.test_guard();

CREATE CONSTRAINT TRIGGER compression_test_evidence_on_insert AFTER INSERT ON qa.compression_test
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('CompressionTest', 'test_id');
CREATE CONSTRAINT TRIGGER compression_test_evidence_on_change AFTER UPDATE ON qa.compression_test
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('CompressionTest', 'test_id');
CREATE CONSTRAINT TRIGGER absorption_test_evidence_on_insert AFTER INSERT ON qa.absorption_test
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('AbsorptionTest', 'test_id');
CREATE CONSTRAINT TRIGGER absorption_test_evidence_on_change AFTER UPDATE ON qa.absorption_test
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('AbsorptionTest', 'test_id');

-- ---------------------------------------------------------------------------------------------
-- E-LAB1-9, E-LAB1-01-13: permissions, the role LABORATORIO and segregation of duties (production ≠ final release).
-- ---------------------------------------------------------------------------------------------
INSERT INTO iam.permission (permission_code, access) VALUES
  ('lab_test:record', 'WRITE'), ('fg_lot:final_release', 'WRITE'), ('lab_spec:manage', 'WRITE'), ('lab:read', 'READ');

INSERT INTO iam.role (role_id, code, name, description)
VALUES (gen_random_uuid(), 'LABORATORIO', 'Laboratorio',
        'Técnico de laboratorio: registra y anula los ensayos de compresión y de absorción de los lotes. No libera lotes ni cambia requisitos.');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES
  ('LABORATORIO', 'lab_test:record'), ('LABORATORIO', 'lab:read'),
  ('CALIDAD', 'lab_test:record'), ('CALIDAD', 'fg_lot:final_release'), ('CALIDAD', 'lab_spec:manage'), ('CALIDAD', 'lab:read'),
  ('GERENTE_PLANTA', 'lab:read'), ('SUPERVISOR_PRODUCCION', 'lab:read'), ('DIRECTOR', 'lab:read'), ('AUDITOR', 'lab:read')
) AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;

INSERT INTO iam.sod_rule (permission_a, permission_b) VALUES ('fg_lot:final_release', 'shift_summary:record');

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['qa.item_spec', 'qa.parameter_value', 'qa.failure_type', 'qa.compression_test', 'qa.absorption_test'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT ON qa.parameter_default, qa.failure_type_default TO rochell_app;
GRANT SELECT, INSERT ON qa.item_spec, qa.parameter_value, qa.failure_type, qa.compression_test, qa.absorption_test TO rochell_app;
GRANT UPDATE (name, status, version) ON qa.failure_type TO rochell_app;
GRANT UPDATE (status, void_reason, voided_by, voided_at) ON qa.compression_test, qa.absorption_test TO rochell_app;
GRANT EXECUTE ON FUNCTION qa.parameter_number(uuid, text), qa.parameter_text(uuid, text) TO rochell_app;
