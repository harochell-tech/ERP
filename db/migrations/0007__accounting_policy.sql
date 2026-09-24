-- PR-06 · Accounting policies. Architecture v2.1 Correction 9 (ADR-039), Frozen Baseline §8.4, Patch 1 (P-6),
-- Patch 1.1 correction 4, approved errata E-PR06-1…7. No accounting threshold lives in code.

CREATE SCHEMA acc;
REVOKE ALL ON SCHEMA acc FROM PUBLIC;
GRANT USAGE ON SCHEMA acc TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- Policies and parameter definitions (global; E-PR06-1, E-PR06-3).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE acc.accounting_policy (
  policy_code  text NOT NULL,
  owner_role   text NOT NULL,
  description  text NOT NULL,
  CONSTRAINT accounting_policy_pk PRIMARY KEY (policy_code),
  CONSTRAINT accounting_policy_code_format CHECK (policy_code ~ '^[A-Z][A-Z0-9_]*$')
);
INSERT INTO acc.accounting_policy (policy_code, owner_role, description) VALUES
  ('PURCHASING', 'CONTROLLER', 'Tolerancias de recepción y de 3-Way Match'),
  ('INVENTORY', 'CONTROLLER', 'Materialidad de ajustes, antigüedad de GRNI, asignación de diferencias de precio'),
  ('POSTING', 'CONTROLLER', 'Tolerancia de redondeo y registro tardío');

CREATE TABLE acc.policy_parameter_definition (
  param_code      text    NOT NULL,
  policy_code     text    NOT NULL,
  value_type      text    NOT NULL,
  min_value       numeric,
  max_value       numeric,
  allowed_values  text[],
  description     text    NOT NULL,
  CONSTRAINT policy_parameter_definition_pk PRIMARY KEY (param_code),
  CONSTRAINT policy_parameter_definition_policy_fk FOREIGN KEY (policy_code) REFERENCES acc.accounting_policy (policy_code),
  CONSTRAINT policy_parameter_definition_type CHECK (value_type IN ('DECIMAL_PERCENT', 'AMOUNT', 'INTEGER', 'ENUM', 'BOOLEAN')),
  CONSTRAINT policy_parameter_definition_enum CHECK ((value_type = 'ENUM') = (allowed_values IS NOT NULL)),
  CONSTRAINT policy_parameter_definition_code_format CHECK (param_code ~ '^[a-z][a-z0-9_]*$')
);
-- Bounds are type limits (percent 0–1, amounts within numeric(19,4), sane integer ranges), not accounting thresholds.
INSERT INTO acc.policy_parameter_definition (param_code, policy_code, value_type, min_value, max_value, allowed_values, description) VALUES
  ('receipt_tolerance_pct', 'PURCHASING', 'DECIMAL_PERCENT', 0, 1, NULL, 'Sobre-recepción permitida sobre lo pedido (fracción)'),
  ('match_qty_tolerance_pct', 'PURCHASING', 'DECIMAL_PERCENT', 0, 1, NULL, '3-Way Match: tolerancia de cantidad (fracción)'),
  ('match_price_tolerance_pct', 'PURCHASING', 'DECIMAL_PERCENT', 0, 1, NULL, '3-Way Match: tolerancia de precio (fracción)'),
  ('match_amount_tolerance_abs', 'PURCHASING', 'AMOUNT', 0, 999999999999999.9999, NULL, '3-Way Match: tolerancia de importe (DOP)'),
  ('inventory_adjustment_materiality', 'INVENTORY', 'AMOUNT', 0, 999999999999999.9999, NULL, 'Importe a partir del cual un ajuste requiere Controller (DOP)'),
  ('grni_aging_alert_days', 'INVENTORY', 'INTEGER', 1, 3650, NULL, 'Días de antigüedad de GRNI que generan alerta'),
  ('invoice_price_variance_allocation_method', 'INVENTORY', 'ENUM', NULL, NULL, ARRAY['STOCK_COVERAGE'], 'Método de asignación de diferencia de precio (Patch 1.1)'),
  ('rounding_difference_tolerance', 'POSTING', 'AMOUNT', 0, 999999999999999.9999, NULL, 'Diferencia máxima que va a ROUNDING_DIFFERENCE (DOP)'),
  ('late_entry_hours', 'POSTING', 'INTEGER', 1, 8760, NULL, 'Horas entre occurred_at y recorded_at a partir de las que un registro es tardío');

CREATE TRIGGER accounting_policy_immutable BEFORE UPDATE OR DELETE ON acc.accounting_policy FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER policy_parameter_definition_immutable BEFORE UPDATE OR DELETE ON acc.policy_parameter_definition FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- Versions (company-scoped). DRAFT → ACTIVE on approval (E-PR06-2); prepared_by ≠ approved_by;
-- ACTIVE ranges never overlap (P-6); approved versions are immutable except closing their range once.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE acc.accounting_policy_version (
  policy_version_id  uuid        NOT NULL,
  policy_code        text        NOT NULL,
  company_id         uuid        NOT NULL,
  version            integer     NOT NULL,
  status             text        NOT NULL,
  effective_from     date        NOT NULL,
  effective_to       date,
  prepared_by        uuid        NOT NULL,
  approved_by        uuid,
  approved_at        timestamptz,
  justification      text        NOT NULL,
  CONSTRAINT accounting_policy_version_pk PRIMARY KEY (policy_version_id),
  CONSTRAINT accounting_policy_version_company_uq UNIQUE (company_id, policy_version_id),
  CONSTRAINT accounting_policy_version_number_uq UNIQUE (policy_code, company_id, version),
  CONSTRAINT accounting_policy_version_policy_fk FOREIGN KEY (policy_code) REFERENCES acc.accounting_policy (policy_code),
  CONSTRAINT accounting_policy_version_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT accounting_policy_version_prepared_by_fk FOREIGN KEY (prepared_by) REFERENCES iam.user (user_id),
  CONSTRAINT accounting_policy_version_approved_by_fk FOREIGN KEY (approved_by) REFERENCES iam.user (user_id),
  CONSTRAINT accounting_policy_version_positive CHECK (version >= 1),
  CONSTRAINT accounting_policy_version_status CHECK (status IN ('DRAFT', 'ACTIVE')),
  CONSTRAINT accounting_policy_version_approval CHECK ((status = 'ACTIVE') = (approved_by IS NOT NULL AND approved_at IS NOT NULL)),
  CONSTRAINT accounting_policy_version_four_eyes CHECK (approved_by IS NULL OR approved_by <> prepared_by),
  CONSTRAINT accounting_policy_version_justification CHECK (length(btrim(justification)) > 0),
  CONSTRAINT accounting_policy_version_range CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT accounting_policy_version_no_overlap EXCLUDE USING gist (
    policy_code WITH =, company_id WITH =, daterange(effective_from, effective_to, '[)') WITH &&) WHERE (status = 'ACTIVE')
);

CREATE FUNCTION acc.policy_version_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'UPDATE' THEN
    RAISE EXCEPTION 'acc.accounting_policy_version rows cannot be deleted';
  END IF;
  IF ROW(NEW.policy_version_id, NEW.policy_code, NEW.company_id, NEW.version, NEW.effective_from, NEW.prepared_by, NEW.justification)
     IS DISTINCT FROM ROW(OLD.policy_version_id, OLD.policy_code, OLD.company_id, OLD.version, OLD.effective_from, OLD.prepared_by, OLD.justification) THEN
    RAISE EXCEPTION 'acc.accounting_policy_version: identity columns are immutable; prepare a new version';
  END IF;
  IF OLD.status = 'DRAFT' AND NEW.status = 'ACTIVE' AND NEW.effective_to IS NOT DISTINCT FROM OLD.effective_to THEN
    RETURN NEW;
  END IF;
  IF OLD.status = 'ACTIVE' AND NEW.status = 'ACTIVE' AND OLD.effective_to IS NULL AND NEW.effective_to IS NOT NULL
     AND OLD.approved_by = NEW.approved_by AND OLD.approved_at = NEW.approved_at THEN
    RETURN NEW;
  END IF;
  RAISE EXCEPTION 'acc.accounting_policy_version: only DRAFT → ACTIVE, or closing an ACTIVE range once, is allowed';
END $$;
CREATE TRIGGER accounting_policy_version_guard BEFORE UPDATE OR DELETE ON acc.accounting_policy_version
  FOR EACH ROW EXECUTE FUNCTION acc.policy_version_guard();

-- ---------------------------------------------------------------------------------------------
-- Parameters: JSON string values (E-PR06-5), written only while the version is DRAFT, never changed.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE acc.accounting_policy_parameter (
  company_id         uuid  NOT NULL,
  policy_version_id  uuid  NOT NULL,
  param_code         text  NOT NULL,
  value              jsonb NOT NULL,
  CONSTRAINT accounting_policy_parameter_pk PRIMARY KEY (policy_version_id, param_code),
  CONSTRAINT accounting_policy_parameter_version_fk FOREIGN KEY (company_id, policy_version_id) REFERENCES acc.accounting_policy_version (company_id, policy_version_id),
  CONSTRAINT accounting_policy_parameter_definition_fk FOREIGN KEY (param_code) REFERENCES acc.policy_parameter_definition (param_code),
  CONSTRAINT accounting_policy_parameter_string_value CHECK (jsonb_typeof(value) = 'string')
);

CREATE FUNCTION acc.policy_parameter_insert_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF (SELECT status FROM acc.accounting_policy_version WHERE policy_version_id = NEW.policy_version_id) <> 'DRAFT' THEN
    RAISE EXCEPTION 'acc.accounting_policy_parameter: parameters can only be added while the version is DRAFT';
  END IF;
  IF NOT EXISTS (
    SELECT 1 FROM acc.policy_parameter_definition d JOIN acc.accounting_policy_version v ON v.policy_code = d.policy_code
    WHERE d.param_code = NEW.param_code AND v.policy_version_id = NEW.policy_version_id) THEN
    RAISE EXCEPTION 'acc.accounting_policy_parameter: % does not belong to the version''s policy', NEW.param_code;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER accounting_policy_parameter_insert_guard BEFORE INSERT ON acc.accounting_policy_parameter
  FOR EACH ROW EXECUTE FUNCTION acc.policy_parameter_insert_guard();
CREATE TRIGGER accounting_policy_parameter_immutable BEFORE UPDATE OR DELETE ON acc.accounting_policy_parameter
  FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- E-PR06-4: dedicated approver role, so the Controller prepares and another person approves.
-- ---------------------------------------------------------------------------------------------
INSERT INTO iam.role (role_id, code, name) VALUES (gen_random_uuid(), 'APROBADOR_POLITICAS', 'Aprobador de políticas contables');
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT role_id, 'accounting_policy:approve' FROM iam.role WHERE code = 'APROBADOR_POLITICAS';

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['acc.accounting_policy_version', 'acc.accounting_policy_parameter'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT ON acc.accounting_policy, acc.policy_parameter_definition TO rochell_app;
GRANT SELECT, INSERT ON acc.accounting_policy_version, acc.accounting_policy_parameter TO rochell_app;
GRANT UPDATE (status, approved_by, approved_at, effective_to) ON acc.accounting_policy_version TO rochell_app;
