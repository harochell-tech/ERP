-- PR-12 · Fiscal gate and purchase Tax Engine. Frozen Baseline §9.1 (tax.*), §30, §14 (permissions), approved errata
-- E-PR12-1…7. No normative value (rate, exemption, withholding) lives in code or in this migration: every one is a
-- fiscal_rule_version activated through the gate.

CREATE SCHEMA tax;
REVOKE ALL ON SCHEMA tax FROM PUBLIC;
GRANT USAGE ON SCHEMA tax TO rochell_app;

-- §14: configuring a fiscal rule and activating it are incompatible (gap in the PR-03 seed).
INSERT INTO iam.sod_rule (permission_a, permission_b) VALUES ('fiscal_rule:activate', 'fiscal_rule:configure');

-- ---------------------------------------------------------------------------------------------
-- Official sources (E-PR12-1 company scoped; E-PR12-2 text reference + SHA-256 of the document until WORM storage).
-- Registered by the fiscal analyst, append-only.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE tax.fiscal_rule_source (
  source_id          uuid        NOT NULL,
  company_id         uuid        NOT NULL,
  official_source    text        NOT NULL,
  document_title     text        NOT NULL,
  document_version   text        NOT NULL,
  publication_date   date        NOT NULL,
  consulted_at       timestamptz NOT NULL,
  effective_from     date        NOT NULL,
  effective_to       date,
  url_or_reference   text        NOT NULL,
  file_object_key    text        NOT NULL,
  file_hash          bytea       NOT NULL,
  approved_by        uuid        NOT NULL,
  approved_at        timestamptz NOT NULL,
  CONSTRAINT fiscal_rule_source_pk PRIMARY KEY (source_id),
  CONSTRAINT fiscal_rule_source_company_uq UNIQUE (company_id, source_id),
  CONSTRAINT fiscal_rule_source_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT fiscal_rule_source_approved_by_fk FOREIGN KEY (approved_by) REFERENCES iam.user (user_id),
  CONSTRAINT fiscal_rule_source_texts CHECK (
    length(btrim(official_source)) > 0 AND length(btrim(document_title)) > 0 AND length(btrim(document_version)) > 0
    AND length(btrim(url_or_reference)) > 0 AND length(btrim(file_object_key)) > 0),
  CONSTRAINT fiscal_rule_source_hash CHECK (length(file_hash) = 32),
  CONSTRAINT fiscal_rule_source_validity CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT fiscal_rule_source_publication CHECK (publication_date <= effective_from OR publication_date <= consulted_at::date)
);
CREATE TRIGGER fiscal_rule_source_append_only BEFORE UPDATE OR DELETE ON tax.fiscal_rule_source FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER fiscal_rule_source_no_truncate BEFORE TRUNCATE ON tax.fiscal_rule_source FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- Rules and versions. Definition is immutable (a change is a new version, E-PR12-4).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE tax.fiscal_rule (
  rule_id     uuid NOT NULL,
  company_id  uuid NOT NULL,
  code        text NOT NULL,
  rule_kind   text NOT NULL,
  CONSTRAINT fiscal_rule_pk PRIMARY KEY (rule_id),
  CONSTRAINT fiscal_rule_company_uq UNIQUE (company_id, rule_id),
  CONSTRAINT fiscal_rule_code_uq UNIQUE (company_id, code),
  CONSTRAINT fiscal_rule_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT fiscal_rule_code_format CHECK (code ~ '^[A-Z][A-Z0-9_.-]*$'),
  CONSTRAINT fiscal_rule_kind CHECK (rule_kind IN ('PURCHASE_ITBIS', 'PURCHASE_WITHHOLDING'))
);
CREATE TRIGGER fiscal_rule_append_only BEFORE UPDATE OR DELETE ON tax.fiscal_rule FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

CREATE TABLE tax.fiscal_rule_version (
  rule_version_id  uuid        NOT NULL,
  company_id       uuid        NOT NULL,
  rule_id          uuid        NOT NULL,
  version          integer     NOT NULL,
  definition       jsonb       NOT NULL,
  effective_from   date        NOT NULL,
  effective_to     date,
  status           text        NOT NULL,
  configured_by    uuid        NOT NULL,
  configured_at    timestamptz NOT NULL,
  activated_by     uuid,
  activated_at     timestamptz,
  row_version      bigint      NOT NULL,
  CONSTRAINT fiscal_rule_version_pk PRIMARY KEY (rule_version_id),
  CONSTRAINT fiscal_rule_version_company_uq UNIQUE (company_id, rule_version_id),
  CONSTRAINT fiscal_rule_version_uq UNIQUE (rule_id, version),
  CONSTRAINT fiscal_rule_version_rule_fk FOREIGN KEY (company_id, rule_id) REFERENCES tax.fiscal_rule (company_id, rule_id),
  CONSTRAINT fiscal_rule_version_configured_by_fk FOREIGN KEY (configured_by) REFERENCES iam.user (user_id),
  CONSTRAINT fiscal_rule_version_activated_by_fk FOREIGN KEY (activated_by) REFERENCES iam.user (user_id),
  CONSTRAINT fiscal_rule_version_status CHECK (status IN ('DRAFT', 'BLOCKED_PENDING_SOURCE', 'READY', 'ACTIVE', 'RETIRED')),
  CONSTRAINT fiscal_rule_version_activator CHECK (activated_by IS NULL OR activated_by <> configured_by),
  CONSTRAINT fiscal_rule_version_active_data CHECK (status NOT IN ('ACTIVE', 'RETIRED') OR activated_by IS NOT NULL OR status = 'RETIRED'),
  CONSTRAINT fiscal_rule_version_validity CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT fiscal_rule_version_number CHECK (version >= 1),
  CONSTRAINT fiscal_rule_version_row_version CHECK (row_version >= 1),
  CONSTRAINT fiscal_rule_version_one_active_at_a_time EXCLUDE USING gist (
    rule_id WITH =, daterange(effective_from, effective_to, '[)') WITH &&) WHERE (status = 'ACTIVE')
);

CREATE TABLE tax.fiscal_rule_version_source (
  company_id       uuid        NOT NULL,
  rule_version_id  uuid        NOT NULL,
  source_id        uuid        NOT NULL,
  linked_by        uuid        NOT NULL,
  linked_at        timestamptz NOT NULL,
  CONSTRAINT fiscal_rule_version_source_pk PRIMARY KEY (rule_version_id, source_id),
  CONSTRAINT fiscal_rule_version_source_version_fk FOREIGN KEY (company_id, rule_version_id) REFERENCES tax.fiscal_rule_version (company_id, rule_version_id),
  CONSTRAINT fiscal_rule_version_source_source_fk FOREIGN KEY (company_id, source_id) REFERENCES tax.fiscal_rule_source (company_id, source_id),
  CONSTRAINT fiscal_rule_version_source_linked_by_fk FOREIGN KEY (linked_by) REFERENCES iam.user (user_id)
);
CREATE TRIGGER fiscal_rule_version_source_append_only BEFORE UPDATE OR DELETE ON tax.fiscal_rule_version_source FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

CREATE TABLE tax.fiscal_rule_test_run (
  test_run_id      uuid        NOT NULL,
  company_id       uuid        NOT NULL,
  rule_version_id  uuid        NOT NULL,
  environment      text        NOT NULL,
  passed           boolean     NOT NULL,
  cases            integer     NOT NULL,
  result_hash      bytea       NOT NULL,
  executed_by      uuid        NOT NULL,
  executed_at      timestamptz NOT NULL,
  CONSTRAINT fiscal_rule_test_run_pk PRIMARY KEY (test_run_id),
  CONSTRAINT fiscal_rule_test_run_version_fk FOREIGN KEY (company_id, rule_version_id) REFERENCES tax.fiscal_rule_version (company_id, rule_version_id),
  CONSTRAINT fiscal_rule_test_run_executed_by_fk FOREIGN KEY (executed_by) REFERENCES iam.user (user_id),
  CONSTRAINT fiscal_rule_test_run_cases CHECK (cases >= 1),
  CONSTRAINT fiscal_rule_test_run_hash CHECK (length(result_hash) = 32)
);
CREATE TRIGGER fiscal_rule_test_run_append_only BEFORE UPDATE OR DELETE ON tax.fiscal_rule_test_run FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- The gate, in the database: identity immutable; transitions of E-PR12-4; READY and ACTIVE need ≥ 1 linked source and the
-- latest test run passed, executed in this deployment's environment (E-PR12-5) after the version was configured.
-- ---------------------------------------------------------------------------------------------
CREATE FUNCTION tax.fiscal_rule_version_gate() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  latest_passed boolean;
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'tax.fiscal_rule_version rows cannot be deleted; retire the version';
  END IF;
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'BLOCKED_PENDING_SOURCE' OR NEW.activated_by IS NOT NULL OR NEW.row_version <> 1 THEN
      RAISE EXCEPTION 'tax.fiscal_rule_version: a new version starts BLOCKED_PENDING_SOURCE (E-PR12-4)';
    END IF;
    RETURN NEW;
  END IF;

  IF ROW(NEW.rule_version_id, NEW.company_id, NEW.rule_id, NEW.version, NEW.definition, NEW.effective_from, NEW.configured_by, NEW.configured_at)
     IS DISTINCT FROM ROW(OLD.rule_version_id, OLD.company_id, OLD.rule_id, OLD.version, OLD.definition, OLD.effective_from, OLD.configured_by, OLD.configured_at) THEN
    RAISE EXCEPTION 'tax.fiscal_rule_version: identity and definition are immutable; configure a new version';
  END IF;
  IF NEW.row_version <> OLD.row_version + 1 THEN
    RAISE EXCEPTION 'tax.fiscal_rule_version: row_version must increase by exactly 1';
  END IF;
  IF NEW.effective_to IS DISTINCT FROM OLD.effective_to AND NOT (OLD.status = 'ACTIVE' AND OLD.effective_to IS NULL) THEN
    RAISE EXCEPTION 'tax.fiscal_rule_version: only an open ACTIVE version can be closed by its successor';
  END IF;
  IF OLD.activated_by IS NOT NULL AND ROW(NEW.activated_by, NEW.activated_at) IS DISTINCT FROM ROW(OLD.activated_by, OLD.activated_at) THEN
    RAISE EXCEPTION 'tax.fiscal_rule_version: activation data is immutable';
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
       (OLD.status = 'BLOCKED_PENDING_SOURCE' AND NEW.status IN ('READY', 'RETIRED')) OR
       (OLD.status = 'READY' AND NEW.status IN ('BLOCKED_PENDING_SOURCE', 'ACTIVE', 'RETIRED')) OR
       (OLD.status = 'ACTIVE' AND NEW.status = 'RETIRED')) THEN
    RAISE EXCEPTION 'tax.fiscal_rule_version: transition % → % is not allowed', OLD.status, NEW.status;
  END IF;

  IF NEW.status IN ('READY', 'ACTIVE') AND NEW.status IS DISTINCT FROM OLD.status THEN
    IF NOT EXISTS (SELECT 1 FROM tax.fiscal_rule_version_source WHERE rule_version_id = NEW.rule_version_id) THEN
      RAISE EXCEPTION 'tax.fiscal_rule_version %: % requires at least one official source', NEW.rule_version_id, NEW.status;
    END IF;
    SELECT r.passed INTO latest_passed
    FROM tax.fiscal_rule_test_run r
    WHERE r.rule_version_id = NEW.rule_version_id
      AND r.executed_at >= NEW.configured_at
      AND r.environment = (SELECT environment FROM core.deployment_environment)
    ORDER BY r.executed_at DESC, r.test_run_id DESC
    LIMIT 1;
    IF latest_passed IS NOT TRUE THEN
      RAISE EXCEPTION 'tax.fiscal_rule_version %: % requires the latest test run in this environment to pass', NEW.rule_version_id, NEW.status;
    END IF;
  END IF;
  IF NEW.status = 'ACTIVE' AND NEW.status IS DISTINCT FROM OLD.status AND (NEW.activated_by IS NULL OR NEW.activated_at IS NULL) THEN
    RAISE EXCEPTION 'tax.fiscal_rule_version %: activation needs the activating specialist', NEW.rule_version_id;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER fiscal_rule_version_gate BEFORE INSERT OR UPDATE OR DELETE ON tax.fiscal_rule_version FOR EACH ROW EXECUTE FUNCTION tax.fiscal_rule_version_gate();
CREATE TRIGGER fiscal_rule_version_no_truncate BEFORE TRUNCATE ON tax.fiscal_rule_version FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- Determinations: append-only evidence of what was computed, with which versions (§30). E-PR12-7: 2 decimals half-up.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE tax.tax_determination (
  determination_id  uuid        NOT NULL,
  company_id        uuid        NOT NULL,
  subject_type      text        NOT NULL,
  subject_id        uuid        NOT NULL,
  determination_date date       NOT NULL,
  rule_version_ids  uuid[]      NOT NULL,
  inputs            jsonb       NOT NULL,
  determined_at     timestamptz NOT NULL,
  CONSTRAINT tax_determination_pk PRIMARY KEY (determination_id),
  CONSTRAINT tax_determination_company_uq UNIQUE (company_id, determination_id),
  CONSTRAINT tax_determination_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT tax_determination_subject CHECK (length(btrim(subject_type)) > 0)
);
CREATE TRIGGER tax_determination_append_only BEFORE UPDATE OR DELETE ON tax.tax_determination FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

CREATE TABLE tax.tax_determination_line (
  determination_id  uuid          NOT NULL,
  line_no           integer       NOT NULL,
  company_id        uuid          NOT NULL,
  subject_line_id   uuid          NOT NULL,
  rule_version_id   uuid          NOT NULL,
  tax_code          text          NOT NULL,
  base              numeric(19,4) NOT NULL,
  rate              numeric(12,6) NOT NULL,
  amount            numeric(19,4) NOT NULL,
  effect            text          NOT NULL,
  CONSTRAINT tax_determination_line_pk PRIMARY KEY (determination_id, line_no),
  CONSTRAINT tax_determination_line_header_fk FOREIGN KEY (company_id, determination_id) REFERENCES tax.tax_determination (company_id, determination_id),
  CONSTRAINT tax_determination_line_version_fk FOREIGN KEY (company_id, rule_version_id) REFERENCES tax.fiscal_rule_version (company_id, rule_version_id),
  CONSTRAINT tax_determination_line_effect CHECK (effect IN ('RECOVERABLE_INPUT', 'NON_RECOVERABLE_INPUT', 'WITHHOLDING')),
  CONSTRAINT tax_determination_line_rate CHECK (rate > 0 AND rate <= 1),
  CONSTRAINT tax_determination_line_base CHECK (base > 0),
  CONSTRAINT tax_determination_line_amount CHECK (amount = round(base * rate, 2)),
  CONSTRAINT tax_determination_line_no CHECK (line_no >= 1)
);
CREATE TRIGGER tax_determination_line_append_only BEFORE UPDATE OR DELETE ON tax.tax_determination_line FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['tax.fiscal_rule_source', 'tax.fiscal_rule', 'tax.fiscal_rule_version', 'tax.fiscal_rule_version_source',
                           'tax.fiscal_rule_test_run', 'tax.tax_determination', 'tax.tax_determination_line'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON ALL TABLES IN SCHEMA tax TO rochell_app;
GRANT UPDATE (status, effective_to, activated_by, activated_at, row_version) ON tax.fiscal_rule_version TO rochell_app;
