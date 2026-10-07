-- VS4-01 · e-CF gateway with Alanube: schema (approved errata E-VS4-1…14, E-VS4-01-1…10).
--   - tax.ecf_series: the DGII-authorized e-NCF ranges per e-CF type that Core numbers from (CORE_MANAGED, ADR-034, E-VS4-1);
--   - tax.ecf_document: one e-CF of an invoice or credit note per attempt, its e-NCF, payload, Alanube ids and the DGII answer;
--   - tax.ecf_call: every call to Alanube (never the token), append-only; tax.ecf_file: the signed XML and PDF, append-only;
--   - invoice / credit note fiscal statuses for the gateway; DGII unit codes; range alert parameters; permissions.

-- ---------------------------------------------------------------------------------------------
-- e-NCF ranges (E-VS4-01-1/4): one ACTIVE per type; prepared by the Especialista fiscal, approved by the Controller.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE tax.ecf_series (
  series_id            uuid        NOT NULL,
  company_id           uuid        NOT NULL,
  ecf_type             text        NOT NULL,
  dgii_authorization   text,
  range_from           bigint      NOT NULL,
  range_to             bigint      NOT NULL,
  next_number          bigint      NOT NULL,
  valid_until          date        NOT NULL,
  status               text        NOT NULL,
  prepared_by          uuid        NOT NULL,
  approved_by          uuid,
  approved_at          timestamptz,
  cancelled_from       bigint,
  cancelled_to         bigint,
  version              bigint      NOT NULL,
  CONSTRAINT ecf_series_pk PRIMARY KEY (series_id),
  CONSTRAINT ecf_series_company_uq UNIQUE (company_id, series_id),
  CONSTRAINT ecf_series_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT ecf_series_prepared_fk FOREIGN KEY (prepared_by) REFERENCES iam.user (user_id),
  CONSTRAINT ecf_series_approved_fk FOREIGN KEY (approved_by) REFERENCES iam.user (user_id),
  CONSTRAINT ecf_series_type CHECK (ecf_type IN ('31', '32', '34', '44')),
  CONSTRAINT ecf_series_authorization CHECK (dgii_authorization IS NULL OR length(btrim(dgii_authorization)) BETWEEN 1 AND 60),
  CONSTRAINT ecf_series_range CHECK (range_from >= 1 AND range_to >= range_from AND range_to <= 9999999999),
  CONSTRAINT ecf_series_next CHECK (next_number BETWEEN range_from AND range_to + 1),
  CONSTRAINT ecf_series_status CHECK (status IN ('DRAFT', 'ACTIVE', 'CLOSED', 'CANCELLED', 'DISCARDED')),
  CONSTRAINT ecf_series_approved CHECK ((status IN ('ACTIVE', 'CLOSED', 'CANCELLED')) = (approved_by IS NOT NULL AND approved_at IS NOT NULL)),
  -- E-VS4-12: the unused tail of a cancelled range, voided through Alanube.
  CONSTRAINT ecf_series_cancelled CHECK ((status = 'CANCELLED') = (cancelled_from IS NOT NULL AND cancelled_to IS NOT NULL)
    AND (cancelled_from IS NULL OR (cancelled_from = next_number AND cancelled_to = range_to AND cancelled_from <= cancelled_to))),
  CONSTRAINT ecf_series_version_positive CHECK (version >= 1)
);
CREATE UNIQUE INDEX ecf_series_one_active ON tax.ecf_series (company_id, ecf_type) WHERE status = 'ACTIVE';

-- No two live ranges of a type overlap; numbers only move forward; the range itself never changes once approved.
CREATE FUNCTION tax.ecf_series_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'tax.ecf_series rows cannot be deleted';
  END IF;
  IF EXISTS (SELECT 1 FROM tax.ecf_series s
             WHERE s.company_id = NEW.company_id AND s.ecf_type = NEW.ecf_type AND s.series_id <> NEW.series_id AND s.status <> 'DISCARDED'
               AND s.range_from <= NEW.range_to AND NEW.range_from <= s.range_to) AND NEW.status <> 'DISCARDED' THEN
    RAISE EXCEPTION 'tax.ecf_series: the range % – % overlaps another range of type %', NEW.range_from, NEW.range_to, NEW.ecf_type;
  END IF;
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'DRAFT' OR NEW.version <> 1 OR NEW.next_number <> NEW.range_from THEN
      RAISE EXCEPTION 'tax.ecf_series: a range is prepared DRAFT, version 1, numbering from its first number';
    END IF;
    RETURN NEW;
  END IF;
  IF ROW(NEW.series_id, NEW.company_id, NEW.ecf_type, NEW.prepared_by) IS DISTINCT FROM ROW(OLD.series_id, OLD.company_id, OLD.ecf_type, OLD.prepared_by) THEN
    RAISE EXCEPTION 'tax.ecf_series: identity columns are immutable';
  END IF;
  IF OLD.status <> 'DRAFT' AND ROW(NEW.range_from, NEW.range_to, NEW.valid_until, NEW.dgii_authorization)
     IS DISTINCT FROM ROW(OLD.range_from, OLD.range_to, OLD.valid_until, OLD.dgii_authorization) THEN
    RAISE EXCEPTION 'tax.ecf_series: an approved range never changes';
  END IF;
  IF NEW.next_number < OLD.next_number THEN
    RAISE EXCEPTION 'tax.ecf_series: an e-NCF is never given back (E-VS4-1)';
  END IF;
  IF OLD.status IN ('CLOSED', 'CANCELLED', 'DISCARDED') AND NEW.status IS DISTINCT FROM OLD.status AND NOT (OLD.status = 'CLOSED' AND NEW.status = 'CANCELLED') THEN
    RAISE EXCEPTION 'tax.ecf_series: % → % is not allowed', OLD.status, NEW.status;
  END IF;
  IF OLD.status <> 'ACTIVE' AND NEW.next_number <> OLD.next_number THEN
    RAISE EXCEPTION 'tax.ecf_series: only an ACTIVE range gives numbers';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER ecf_series_guard BEFORE INSERT OR UPDATE OR DELETE ON tax.ecf_series FOR EACH ROW EXECUTE FUNCTION tax.ecf_series_guard();
CREATE TRIGGER ecf_series_four_eyes BEFORE INSERT OR UPDATE ON tax.ecf_series
  FOR EACH ROW EXECUTE FUNCTION core.four_eyes('approved_by', 'prepared_by', 'ecf_series_four_eyes');
CREATE CONSTRAINT TRIGGER ecf_series_evidence_on_insert AFTER INSERT ON tax.ecf_series
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('EcfSeries', 'series_id');
CREATE CONSTRAINT TRIGGER ecf_series_evidence_on_change AFTER UPDATE ON tax.ecf_series
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('EcfSeries', 'series_id');

-- ---------------------------------------------------------------------------------------------
-- e-CF documents (E-VS4-3…6, E-VS4-01-5): one per attempt; one live attempt per invoice or credit note.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE tax.ecf_document (
  document_id          uuid          NOT NULL,
  company_id           uuid          NOT NULL,
  source_kind          text          NOT NULL,
  source_id            uuid          NOT NULL,
  attempt_no           integer       NOT NULL,
  ecf_type             text          NOT NULL,
  encf                 text          NOT NULL,
  series_id            uuid          NOT NULL,
  status               text          NOT NULL,
  payload              jsonb         NOT NULL,
  payload_sha256       bytea         NOT NULL,
  provider_id          text,
  track_id             text,
  security_code        text,
  signature_date       timestamptz,
  stamp_url            text,
  government_response  jsonb,
  reason               text,
  polls                integer       NOT NULL DEFAULT 0,
  next_poll_at         timestamptz,
  created_at           timestamptz   NOT NULL,
  finished_at          timestamptz,
  version              bigint        NOT NULL,
  CONSTRAINT ecf_document_pk PRIMARY KEY (document_id),
  CONSTRAINT ecf_document_company_uq UNIQUE (company_id, document_id),
  CONSTRAINT ecf_document_encf_uq UNIQUE (company_id, encf),
  CONSTRAINT ecf_document_attempt_uq UNIQUE (source_kind, source_id, attempt_no),
  CONSTRAINT ecf_document_series_fk FOREIGN KEY (company_id, series_id) REFERENCES tax.ecf_series (company_id, series_id),
  CONSTRAINT ecf_document_source CHECK (source_kind IN ('INVOICE', 'CREDIT_NOTE')),
  CONSTRAINT ecf_document_type CHECK (ecf_type IN ('31', '32', '34', '44') AND (source_kind = 'CREDIT_NOTE') = (ecf_type = '34')),
  CONSTRAINT ecf_document_encf_format CHECK (encf ~ ('^E' || ecf_type || '[0-9]{10}$')),
  CONSTRAINT ecf_document_status CHECK (status IN ('PENDING', 'SUBMITTED', 'UNKNOWN_OUTCOME', 'ACCEPTED', 'ACCEPTED_CONDITIONAL', 'REJECTED', 'REQUIRES_ACTION',
    'CONTINGENCY')),
  CONSTRAINT ecf_document_accepted CHECK ((status IN ('ACCEPTED', 'ACCEPTED_CONDITIONAL')) <= (security_code IS NOT NULL AND stamp_url IS NOT NULL AND signature_date IS NOT NULL)),
  CONSTRAINT ecf_document_finished CHECK ((status IN ('ACCEPTED', 'ACCEPTED_CONDITIONAL', 'REJECTED')) = (finished_at IS NOT NULL)),
  CONSTRAINT ecf_document_hash CHECK (length(payload_sha256) = 32),
  CONSTRAINT ecf_document_attempt CHECK (attempt_no >= 1 AND polls >= 0 AND version >= 1)
);
-- A rejected attempt is history; any other is the source's live e-CF (never two at once, E-VS4-4).
CREATE UNIQUE INDEX ecf_document_one_live ON tax.ecf_document (source_kind, source_id) WHERE status <> 'REJECTED';
CREATE INDEX ecf_document_to_poll ON tax.ecf_document (next_poll_at) WHERE status IN ('PENDING', 'SUBMITTED', 'UNKNOWN_OUTCOME');

CREATE FUNCTION tax.ecf_document_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'tax.ecf_document rows cannot be deleted';
  END IF;
  IF ROW(NEW.document_id, NEW.company_id, NEW.source_kind, NEW.source_id, NEW.attempt_no, NEW.ecf_type, NEW.encf, NEW.series_id, NEW.payload, NEW.payload_sha256, NEW.created_at)
     IS DISTINCT FROM ROW(OLD.document_id, OLD.company_id, OLD.source_kind, OLD.source_id, OLD.attempt_no, OLD.ecf_type, OLD.encf, OLD.series_id, OLD.payload, OLD.payload_sha256,
       OLD.created_at) THEN
    RAISE EXCEPTION 'tax.ecf_document: what was sent never changes; a correction is a new attempt';
  END IF;
  IF OLD.status IN ('ACCEPTED', 'ACCEPTED_CONDITIONAL', 'REJECTED') AND NEW.status IS DISTINCT FROM OLD.status THEN
    RAISE EXCEPTION 'tax.ecf_document: a final answer (%) never changes', OLD.status;
  END IF;
  IF NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'tax.ecf_document: version must increase by exactly 1';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER ecf_document_guard BEFORE UPDATE OR DELETE ON tax.ecf_document FOR EACH ROW EXECUTE FUNCTION tax.ecf_document_guard();
CREATE CONSTRAINT TRIGGER ecf_document_evidence_on_insert AFTER INSERT ON tax.ecf_document
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('EcfDocument', 'document_id');
CREATE CONSTRAINT TRIGGER ecf_document_evidence_on_change AFTER UPDATE ON tax.ecf_document
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('EcfDocument', 'document_id');

-- Every call to Alanube (E-VS4-01-5): what was asked and what came back — never the token.
CREATE TABLE tax.ecf_call (
  call_id        uuid        NOT NULL,
  company_id     uuid        NOT NULL,
  document_id    uuid,
  series_id      uuid,
  operation      text        NOT NULL,
  mode           text        NOT NULL,
  http_status    integer,
  outcome        text        NOT NULL,
  provider_code  text,
  message        text,
  called_at      timestamptz NOT NULL,
  duration_ms    integer     NOT NULL,
  CONSTRAINT ecf_call_pk PRIMARY KEY (call_id),
  CONSTRAINT ecf_call_document_fk FOREIGN KEY (company_id, document_id) REFERENCES tax.ecf_document (company_id, document_id),
  CONSTRAINT ecf_call_series_fk FOREIGN KEY (company_id, series_id) REFERENCES tax.ecf_series (company_id, series_id),
  CONSTRAINT ecf_call_operation CHECK (operation IN ('SUBMIT', 'QUERY', 'CANCEL', 'WEBHOOK', 'DOWNLOAD')),
  CONSTRAINT ecf_call_mode CHECK (mode IN ('SANDBOX', 'PRODUCTION', 'SIMULATED')),
  CONSTRAINT ecf_call_outcome CHECK (outcome IN ('OK', 'REJECTED', 'TIMEOUT', 'ERROR')),
  CONSTRAINT ecf_call_texts CHECK ((provider_code IS NULL OR length(provider_code) <= 40) AND (message IS NULL OR length(message) <= 2000) AND duration_ms >= 0)
);
CREATE INDEX ecf_call_document ON tax.ecf_call (document_id, called_at);
CREATE TRIGGER ecf_call_append_only BEFORE UPDATE OR DELETE ON tax.ecf_call FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER ecf_call_no_truncate BEFORE TRUNCATE ON tax.ecf_call FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- The signed XML and PDF of an accepted e-CF (E-VS4-5, E-VS4-01-6): kept in the database, in the daily backups, never removed.
CREATE TABLE tax.ecf_file (
  file_id      uuid        NOT NULL,
  company_id   uuid        NOT NULL,
  document_id  uuid        NOT NULL,
  kind         text        NOT NULL,
  content      bytea       NOT NULL,
  sha256       bytea       NOT NULL,
  fetched_at   timestamptz NOT NULL,
  CONSTRAINT ecf_file_pk PRIMARY KEY (file_id),
  CONSTRAINT ecf_file_kind_uq UNIQUE (document_id, kind),
  CONSTRAINT ecf_file_document_fk FOREIGN KEY (company_id, document_id) REFERENCES tax.ecf_document (company_id, document_id),
  CONSTRAINT ecf_file_kind CHECK (kind IN ('XML', 'PDF')),
  CONSTRAINT ecf_file_content CHECK (octet_length(content) BETWEEN 1 AND 20971520 AND length(sha256) = 32)
);
CREATE TRIGGER ecf_file_append_only BEFORE UPDATE OR DELETE ON tax.ecf_file FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER ecf_file_no_truncate BEFORE TRUNCATE ON tax.ecf_file FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- Fiscal statuses of invoices and credit notes (E-VS4-01-2/3): ECF_SENDING → ECF_ACCEPTED, or ECF_REJECTED (corrected and sent again with
-- another e-NCF, or voided), or ECF_ACTION; the manual channel (PENDING_EXTERNAL → ACCEPTED_EXTERNAL) stays as contingency.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE sal.invoice DROP CONSTRAINT invoice_statuses,
  ADD CONSTRAINT invoice_statuses CHECK (
    (commercial_status = 'DRAFT' AND accounting_status = 'NOT_POSTED' AND fiscal_status = 'PENDING')
    OR (commercial_status IN ('CONFIRMED', 'PARTIALLY_PAID', 'PAID') AND accounting_status = 'POSTED'
        AND fiscal_status IN ('PENDING_EXTERNAL', 'ACCEPTED_EXTERNAL', 'ECF_SENDING', 'ECF_ACCEPTED', 'ECF_REJECTED', 'ECF_ACTION'))
    OR (commercial_status = 'CREDITED' AND accounting_status = 'POSTED' AND fiscal_status IN ('ACCEPTED_EXTERNAL', 'ECF_ACCEPTED'))
    OR (commercial_status = 'VOIDED' AND accounting_status = 'REVERSED' AND fiscal_status IN ('PENDING_EXTERNAL', 'ECF_REJECTED'))),
  DROP CONSTRAINT invoice_encf_present,
  ADD CONSTRAINT invoice_encf_present CHECK ((fiscal_status IN ('ACCEPTED_EXTERNAL', 'ECF_ACCEPTED')) = (encf IS NOT NULL));

ALTER TABLE sal.credit_note DROP CONSTRAINT credit_note_statuses,
  ADD CONSTRAINT credit_note_statuses CHECK (
    (commercial_status = 'DRAFT' AND accounting_status = 'NOT_POSTED' AND fiscal_status = 'PENDING')
    OR (commercial_status = 'CONFIRMED' AND accounting_status = 'POSTED'
        AND fiscal_status IN ('PENDING_EXTERNAL', 'ACCEPTED_EXTERNAL', 'ECF_SENDING', 'ECF_ACCEPTED', 'ECF_REJECTED', 'ECF_ACTION'))),
  DROP CONSTRAINT credit_note_encf,
  ADD CONSTRAINT credit_note_encf CHECK ((fiscal_status IN ('ACCEPTED_EXTERNAL', 'ECF_ACCEPTED')) = (encf IS NOT NULL) AND (encf IS NULL OR encf ~ '^E34[0-9]{10}$'));

-- ---------------------------------------------------------------------------------------------
-- DGII unit codes (E-VS4-01-9): an e-CF line needs its unit's code.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE md.uom ADD COLUMN dgii_code integer, ADD CONSTRAINT uom_dgii_code CHECK (dgii_code IS NULL OR dgii_code BETWEEN 1 AND 99);
DROP TRIGGER uom_immutable ON md.uom;
UPDATE md.uom SET dgii_code = v.code FROM (VALUES ('kg', 21), ('t', 39), ('m3', 28), ('l', 24), ('un', 43)) AS v (uom, code) WHERE md.uom.uom_code = v.uom;
CREATE TRIGGER uom_immutable BEFORE UPDATE OR DELETE ON md.uom FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- Range alerts (E-VS4-01-4) in REVENUE_ACCOUNTING.
-- ---------------------------------------------------------------------------------------------
INSERT INTO acc.policy_parameter_definition (param_code, policy_code, value_type, min_value, max_value, allowed_values, description, label, unit, example, affects) VALUES
  ('ecf_range_alert_pct', 'REVENUE_ACCOUNTING', 'DECIMAL_PERCENT', 0.01, 0.5, NULL, 'Fracción del rango de e-NCF restante desde la cual Inicio avisa (E-VS4-01-4)',
   'Aviso de rango de e-NCF por agotarse', 'PERCENT', '10 %',
   'Cuando a un rango vigente de e-NCF le queda este porcentaje o menos, Inicio avisa para pedir otro rango a la DGII a tiempo.'),
  ('ecf_range_alert_days', 'REVENUE_ACCOUNTING', 'INTEGER', 1, 365, NULL, 'Días antes del vencimiento de un rango de e-NCF desde los cuales Inicio avisa (E-VS4-01-4)',
   'Aviso de rango de e-NCF por vencer', 'DAYS', '30 días',
   'Días antes de la fecha de vencimiento de un rango vigente de e-NCF a partir de los cuales Inicio avisa.');

-- ---------------------------------------------------------------------------------------------
-- Permissions (E-VS4-01-8): ranges prepared by the Especialista fiscal and approved by the Controller (never the same person); the
-- fiscal inbox resolved by the Especialista fiscal and Facturación.
-- ---------------------------------------------------------------------------------------------
INSERT INTO iam.permission (permission_code, access) VALUES ('ecf_series:prepare', 'WRITE'), ('ecf_series:approve', 'WRITE'), ('ecf:resolve', 'WRITE');
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES ('ESPECIALISTA_FISCAL', 'ecf_series:prepare'), ('CONTROLLER', 'ecf_series:approve'), ('ESPECIALISTA_FISCAL', 'ecf:resolve'), ('FACTURACION', 'ecf:resolve'))
  AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;
INSERT INTO iam.sod_rule (permission_a, permission_b) VALUES ('ecf_series:approve', 'ecf_series:prepare');

GRANT SELECT, INSERT ON tax.ecf_series, tax.ecf_document, tax.ecf_call, tax.ecf_file TO rochell_app;
GRANT UPDATE (next_number, status, approved_by, approved_at, cancelled_from, cancelled_to, version, range_from, range_to, valid_until, dgii_authorization)
  ON tax.ecf_series TO rochell_app;
GRANT UPDATE (status, provider_id, track_id, security_code, signature_date, stamp_url, government_response, reason, polls, next_poll_at, finished_at, version)
  ON tax.ecf_document TO rochell_app;
