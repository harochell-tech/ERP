-- PR-16 · Reconciliations and component close. Frozen Baseline §9.4, §11.7, T-13; Patch 1 P-2 / P-8 (T-14);
-- Patch 1.1 (close gate dates); approved errata E-PR16-1…9.

CREATE SCHEMA rec;

-- Definitions. Blocking is per component (E-PR16-2): a separate table instead of the single blocks_component column,
-- because ACC-EVIDENCE blocks both components (each for its own documents).
CREATE TABLE rec.recon_definition (
  recon_code  text NOT NULL,
  description text NOT NULL,
  severity    text NOT NULL,
  CONSTRAINT recon_definition_pk PRIMARY KEY (recon_code),
  CONSTRAINT recon_definition_severity CHECK (severity IN ('ERROR', 'WARNING'))
);
CREATE TABLE rec.recon_blocking (
  recon_code text NOT NULL REFERENCES rec.recon_definition,
  component  text NOT NULL,
  CONSTRAINT recon_blocking_pk PRIMARY KEY (recon_code, component),
  CONSTRAINT recon_blocking_component CHECK (component IN ('INV-MOV', 'AP-REC'))
);

INSERT INTO rec.recon_definition (recon_code, description, severity) VALUES
  ('AP-GL', 'Documentos AP abiertos por proveedor = saldo de AP_CONTROL en el GL por proveedor', 'ERROR'),
  ('INV-VALUE-GL', 'Valuación por área × ítem = saldo de RAW_MATERIAL en el GL (plantas del área, ítem)', 'ERROR'),
  ('INV-QTY-BALANCE', 'Saldos de stock = Σ quantity entries; cantidad valuada = Σ stock de las plantas del área', 'ERROR'),
  ('INV-VALUE-BALANCE', 'Valuación por área × ítem = Σ value entries', 'ERROR'),
  ('VAL-RESIDUAL', 'Valor huérfano (cantidad 0, valor ≠ 0) = ERROR; cantidad > 0 con valor ≤ 0 = advertencia (E-PR16-3)', 'ERROR'),
  ('ACC-EVIDENCE', 'Estado contable de cada documento respaldado por sus journals (E-11, E-PR16-4)', 'ERROR'),
  ('VALUE-GL-LINK', 'Cada value entry con exactamente una línea de GL por el mismo importe (P-1)', 'ERROR'),
  ('GRNI-AGING', 'Recepciones sin facturar más antiguas que grni_aging_alert_days (advertencia)', 'WARNING');

INSERT INTO rec.recon_blocking (recon_code, component) VALUES
  ('INV-VALUE-GL', 'INV-MOV'), ('INV-QTY-BALANCE', 'INV-MOV'), ('INV-VALUE-BALANCE', 'INV-MOV'), ('VAL-RESIDUAL', 'INV-MOV'),
  ('VALUE-GL-LINK', 'INV-MOV'), ('ACC-EVIDENCE', 'INV-MOV'), ('ACC-EVIDENCE', 'AP-REC'), ('AP-GL', 'AP-REC');

CREATE TABLE rec.recon_run (
  run_id      uuid          NOT NULL,
  company_id  uuid          NOT NULL REFERENCES md.company,
  recon_code  text          NOT NULL REFERENCES rec.recon_definition,
  as_of       timestamptz   NOT NULL,
  total_a     numeric(19,4),
  total_b     numeric(19,4),
  difference  numeric(19,4),
  status      text          NOT NULL,
  command_id  uuid          NOT NULL,
  CONSTRAINT recon_run_pk PRIMARY KEY (run_id),
  CONSTRAINT recon_run_company_uq UNIQUE (company_id, run_id),
  CONSTRAINT recon_run_status CHECK (status IN ('MATCHED', 'MATCHED_WITH_TOLERANCE', 'EXCEPTIONS', 'FAILED'))
);
CREATE INDEX recon_run_latest ON rec.recon_run (company_id, recon_code, as_of DESC);

-- E-PR16-9: findings are OPEN; a later run that no longer finds them is the resolution (no workflow in VS#1).
CREATE TABLE rec.recon_exception (
  exception_id   uuid          NOT NULL,
  company_id     uuid          NOT NULL,
  run_id         uuid          NOT NULL,
  match_key      text          NOT NULL,
  value_a        numeric(19,4),
  value_b        numeric(19,4),
  classification text          NOT NULL,
  severity       text          NOT NULL,
  component      text,
  status         text          NOT NULL,
  resolution     text,
  CONSTRAINT recon_exception_pk PRIMARY KEY (exception_id),
  CONSTRAINT recon_exception_run_fk FOREIGN KEY (company_id, run_id) REFERENCES rec.recon_run (company_id, run_id),
  CONSTRAINT recon_exception_severity CHECK (severity IN ('ERROR', 'WARNING')),
  CONSTRAINT recon_exception_component CHECK (component IS NULL OR component IN ('INV-MOV', 'AP-REC')),
  CONSTRAINT recon_exception_status CHECK (status = 'OPEN')
);

-- E-PR16-6: what the component looked like when it closed; its SHA-256 is close_component_state.snapshot_hash.
CREATE TABLE fin.close_snapshot (
  snapshot_id  uuid        NOT NULL,
  company_id   uuid        NOT NULL,
  period_id    uuid        NOT NULL,
  component    text        NOT NULL,
  closed_by    uuid        NOT NULL REFERENCES iam.user (user_id),
  closed_at    timestamptz NOT NULL,
  content      jsonb       NOT NULL,
  content_hash bytea       NOT NULL,
  CONSTRAINT close_snapshot_pk PRIMARY KEY (snapshot_id),
  CONSTRAINT close_snapshot_period_fk FOREIGN KEY (company_id, period_id) REFERENCES fin.period (company_id, period_id),
  CONSTRAINT close_snapshot_component CHECK (component IN ('INV-MOV', 'AP-REC')),
  CONSTRAINT close_snapshot_hash CHECK (octet_length(content_hash) = 32)
);

-- Patch 1 P-8 / E-PR16-7: reopening needs a second approver.
CREATE TABLE fin.reopen_request (
  request_id         uuid        NOT NULL,
  company_id         uuid        NOT NULL,
  period_id          uuid        NOT NULL,
  component          text        NOT NULL,
  reason             text        NOT NULL,
  requested_by       uuid        NOT NULL REFERENCES iam.user (user_id),
  requested_at       timestamptz NOT NULL,
  second_approved_by uuid        REFERENCES iam.user (user_id),
  second_approved_at timestamptz,
  rejected_by        uuid        REFERENCES iam.user (user_id),
  status             text        NOT NULL,
  CONSTRAINT reopen_request_pk PRIMARY KEY (request_id),
  CONSTRAINT reopen_request_period_fk FOREIGN KEY (company_id, period_id) REFERENCES fin.period (company_id, period_id),
  CONSTRAINT reopen_request_component CHECK (component IN ('INV-MOV', 'AP-REC')),
  CONSTRAINT reopen_request_status CHECK (status IN ('REQUESTED', 'APPROVED', 'REJECTED')),
  CONSTRAINT reopen_request_second_person CHECK (second_approved_by IS NULL OR second_approved_by <> requested_by),
  CONSTRAINT reopen_request_rejecter CHECK (rejected_by IS NULL OR rejected_by <> requested_by),
  CONSTRAINT reopen_request_approved CHECK ((status = 'APPROVED') = (second_approved_by IS NOT NULL)),
  CONSTRAINT reopen_request_rejected CHECK ((status = 'REJECTED') = (rejected_by IS NOT NULL))
);
CREATE UNIQUE INDEX reopen_request_one_open ON fin.reopen_request (period_id, component) WHERE status = 'REQUESTED';

CREATE TRIGGER recon_run_append_only BEFORE UPDATE OR DELETE ON rec.recon_run FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER recon_exception_append_only BEFORE UPDATE OR DELETE ON rec.recon_exception FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER close_snapshot_append_only BEFORE UPDATE OR DELETE ON fin.close_snapshot FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

-- Only a REQUESTED reopen request changes, once.
CREATE FUNCTION fin.reopen_request_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' OR OLD.status <> 'REQUESTED'
     OR ROW(NEW.request_id, NEW.company_id, NEW.period_id, NEW.component, NEW.reason, NEW.requested_by, NEW.requested_at)
        IS DISTINCT FROM ROW(OLD.request_id, OLD.company_id, OLD.period_id, OLD.component, OLD.reason, OLD.requested_by, OLD.requested_at) THEN
    RAISE EXCEPTION 'fin.reopen_request: only a REQUESTED request can be approved or rejected, once';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER reopen_request_guard BEFORE UPDATE OR DELETE ON fin.reopen_request FOR EACH ROW EXECUTE FUNCTION fin.reopen_request_guard();

-- Component states move only OPEN/REOPENED → CLOSED (with its snapshot) and CLOSED → REOPENED.
CREATE FUNCTION fin.close_component_state_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'fin.close_component_state rows cannot be deleted';
  END IF;
  IF ROW(NEW.company_id, NEW.period_id, NEW.component) IS DISTINCT FROM ROW(OLD.company_id, OLD.period_id, OLD.component)
     OR NEW.version <> OLD.version + 1
     OR NOT ((OLD.status IN ('OPEN', 'REOPENED') AND NEW.status = 'CLOSED' AND NEW.snapshot_hash IS NOT NULL AND NEW.closed_by IS NOT NULL)
          OR (OLD.status = 'CLOSED' AND NEW.status = 'REOPENED')) THEN
    RAISE EXCEPTION 'fin.close_component_state: % → % is not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER close_component_state_guard BEFORE UPDATE OR DELETE ON fin.close_component_state
  FOR EACH ROW EXECUTE FUNCTION fin.close_component_state_guard();

-- Patch 1.1 / E-PR16-8: which date places a group in a period, per ledger.
ALTER TABLE audit.integrity_state ADD CONSTRAINT integrity_state_dates CHECK (
  (ledger = 'DOMAIN_EVENT' AND business_date IS NOT NULL AND posting_date IS NULL) OR
  (ledger <> 'DOMAIN_EVENT' AND posting_date IS NOT NULL));
CREATE INDEX integrity_pending_by_business_date ON audit.integrity_state (company_id, business_date)
  WHERE integrity_status <> 'SEALED' AND ledger = 'DOMAIN_EVENT';

-- Row-level security and privileges.
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['rec.recon_run', 'rec.recon_exception', 'fin.close_snapshot', 'fin.reopen_request'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT USAGE ON SCHEMA rec TO rochell_app;
GRANT SELECT ON rec.recon_definition, rec.recon_blocking TO rochell_app;
GRANT SELECT, INSERT ON rec.recon_run, rec.recon_exception, fin.close_snapshot, fin.reopen_request TO rochell_app;
GRANT UPDATE (status, second_approved_by, second_approved_at, rejected_by) ON fin.reopen_request TO rochell_app;
GRANT UPDATE (status, closed_by, closed_at, snapshot_hash, version) ON fin.close_component_state TO rochell_app;
