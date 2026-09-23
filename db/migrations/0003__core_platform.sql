-- PR-02 · Platform core. Baseline §17 PR-02, Frozen Baseline Patch 1 (P-3, P-5, §5.2), Patch 1.1 (correction 3).
-- Owner: deployment role (runs migrations). Application role: rochell_app (no DELETE anywhere, UPDATE only where listed).

-- ---------------------------------------------------------------------------------------------
-- Roles and schemas
-- ---------------------------------------------------------------------------------------------
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'rochell_app') THEN
    CREATE ROLE rochell_app NOLOGIN;
  END IF;
END $$;

CREATE SCHEMA core;
CREATE SCHEMA obs;

REVOKE ALL ON SCHEMA core, obs FROM PUBLIC;
GRANT USAGE ON SCHEMA md, core, obs TO rochell_app;
GRANT SELECT ON md.company TO rochell_app;

-- Generic guard for append-only tables (rows and TRUNCATE).
CREATE FUNCTION core.reject_mutation() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION '%.% is append-only (% not allowed)', TG_TABLE_SCHEMA, TG_TABLE_NAME, TG_OP;
END $$;

-- ---------------------------------------------------------------------------------------------
-- Deployment environment (Patch 1.1, correction 3): single row, written only by the deployment role.
-- Populated by `rochell-migrate init-environment TEST|PRODUCTION`; never changed afterwards.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE core.deployment_environment (
  singleton    boolean     NOT NULL DEFAULT true,
  environment  text        NOT NULL,
  set_by       text        NOT NULL,
  set_at       timestamptz NOT NULL,
  CONSTRAINT deployment_environment_pk PRIMARY KEY (singleton),
  CONSTRAINT deployment_environment_singleton CHECK (singleton),
  CONSTRAINT deployment_environment_value CHECK (environment IN ('TEST', 'PRODUCTION'))
);
REVOKE ALL ON core.deployment_environment FROM PUBLIC, rochell_app;
GRANT SELECT ON core.deployment_environment TO rochell_app;
CREATE TRIGGER deployment_environment_immutable
  BEFORE UPDATE OR DELETE ON core.deployment_environment
  FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER deployment_environment_no_truncate
  BEFORE TRUNCATE ON core.deployment_environment
  FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

CREATE FUNCTION core.current_environment() RETURNS text
  LANGUAGE sql STABLE SECURITY DEFINER SET search_path = core, pg_temp
  AS 'SELECT environment FROM core.deployment_environment WHERE singleton';
REVOKE ALL ON FUNCTION core.current_environment() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION core.current_environment() TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- Command log (Patch 1, P-3). Inserted first with result NULL; exactly one UPDATE of the result,
-- by the inserting transaction, right before COMMIT. Immutable afterwards.
-- session_id: FK to iam.session is added in PR-03 (iam does not exist yet; erratum E-PR02-1).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE core.command_log (
  company_id       uuid        NOT NULL,
  command_id       uuid        NOT NULL,
  command_type     text        NOT NULL,
  idempotency_key  text        NOT NULL,
  session_id       uuid        NOT NULL,
  result_ref       uuid        NOT NULL,
  result_payload   jsonb,
  committed_at     timestamptz,
  CONSTRAINT command_log_pk PRIMARY KEY (command_id),
  CONSTRAINT command_log_company_uq UNIQUE (company_id, command_id),
  CONSTRAINT command_log_idempotency_uq UNIQUE (company_id, command_type, idempotency_key),
  CONSTRAINT command_log_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT command_log_key_length CHECK (length(idempotency_key) BETWEEN 1 AND 200),
  CONSTRAINT command_log_result_complete CHECK ((result_payload IS NULL) = (committed_at IS NULL))
);

CREATE FUNCTION core.command_log_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  row_xmin xid;
BEGIN
  IF TG_OP <> 'UPDATE' THEN
    RAISE EXCEPTION 'core.command_log is immutable (% not allowed)', TG_OP;
  END IF;
  IF OLD.result_payload IS NOT NULL OR OLD.committed_at IS NOT NULL THEN
    RAISE EXCEPTION 'core.command_log result of command % is immutable once written', OLD.command_id;
  END IF;
  SELECT xmin INTO row_xmin FROM core.command_log WHERE command_id = OLD.command_id;
  IF row_xmin IS DISTINCT FROM pg_current_xact_id()::xid THEN
    RAISE EXCEPTION 'core.command_log result of command % can only be written by the inserting transaction', OLD.command_id;
  END IF;
  IF ROW(NEW.company_id, NEW.command_id, NEW.command_type, NEW.idempotency_key, NEW.session_id, NEW.result_ref)
     IS DISTINCT FROM ROW(OLD.company_id, OLD.command_id, OLD.command_type, OLD.idempotency_key, OLD.session_id, OLD.result_ref) THEN
    RAISE EXCEPTION 'core.command_log: only result_payload and committed_at may be written';
  END IF;
  IF NEW.result_payload IS NULL OR NEW.committed_at IS NULL THEN
    RAISE EXCEPTION 'core.command_log: result_payload and committed_at must be written together';
  END IF;
  RETURN NEW;
END $$;

CREATE TRIGGER command_log_guard
  BEFORE UPDATE OR DELETE ON core.command_log
  FOR EACH ROW EXECUTE FUNCTION core.command_log_guard();
CREATE TRIGGER command_log_no_truncate
  BEFORE TRUNCATE ON core.command_log
  FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

CREATE FUNCTION core.command_log_require_result() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM core.command_log
                 WHERE command_id = NEW.command_id AND result_payload IS NOT NULL AND committed_at IS NOT NULL) THEN
    RAISE EXCEPTION 'command % reached COMMIT without its result (Patch 1 P-3, step 15)', NEW.command_id;
  END IF;
  RETURN NULL;
END $$;

CREATE CONSTRAINT TRIGGER command_log_result_written
  AFTER INSERT ON core.command_log
  DEFERRABLE INITIALLY DEFERRED
  FOR EACH ROW EXECUTE FUNCTION core.command_log_require_result();

-- ---------------------------------------------------------------------------------------------
-- Domain events (Errata E-2): several events per aggregate version via event_sequence.
-- row_hash = SHA-256 canonical hash v1 computed by the application (Rochell.Platform.Hashing).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE core.domain_event (
  event_id             uuid        NOT NULL,
  company_id           uuid        NOT NULL,
  command_id           uuid        NOT NULL,
  command_event_index  integer     NOT NULL,
  event_type           text        NOT NULL,
  schema_version       integer     NOT NULL,
  aggregate_type       text        NOT NULL,
  aggregate_id         uuid        NOT NULL,
  aggregate_version    bigint      NOT NULL,
  event_sequence       smallint    NOT NULL,
  occurred_at          timestamptz NOT NULL,
  recorded_at          timestamptz NOT NULL DEFAULT now(),
  business_date        date        NOT NULL,
  session_id           uuid        NOT NULL,
  correlation_id       uuid        NOT NULL,
  causation_id         uuid,
  payload              jsonb       NOT NULL,
  row_hash             bytea       NOT NULL,
  CONSTRAINT domain_event_pk PRIMARY KEY (event_id),
  CONSTRAINT domain_event_company_uq UNIQUE (company_id, event_id),
  CONSTRAINT domain_event_aggregate_uq UNIQUE (aggregate_type, aggregate_id, aggregate_version, event_sequence),
  CONSTRAINT domain_event_command_uq UNIQUE (command_id, command_event_index),
  CONSTRAINT domain_event_command_fk FOREIGN KEY (company_id, command_id) REFERENCES core.command_log (company_id, command_id),
  CONSTRAINT domain_event_command_event_index_positive CHECK (command_event_index >= 1),
  CONSTRAINT domain_event_event_sequence_positive CHECK (event_sequence >= 1),
  CONSTRAINT domain_event_schema_version_positive CHECK (schema_version >= 1),
  CONSTRAINT domain_event_aggregate_version_positive CHECK (aggregate_version >= 1),
  CONSTRAINT domain_event_row_hash_length CHECK (octet_length(row_hash) = 32)
);
CREATE TRIGGER domain_event_append_only BEFORE UPDATE OR DELETE ON core.domain_event
  FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER domain_event_no_truncate BEFORE TRUNCATE ON core.domain_event
  FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- Outbox / inbox (ADR-003, ADR-021). At-least-once delivery; consumers deduplicate with inbox.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE core.outbox (
  outbox_id      bigserial   NOT NULL,
  company_id     uuid        NOT NULL,
  event_id       uuid        NOT NULL,
  available_at   timestamptz NOT NULL DEFAULT now(),
  dispatched_at  timestamptz,
  attempts       integer     NOT NULL DEFAULT 0,
  CONSTRAINT outbox_pk PRIMARY KEY (outbox_id),
  CONSTRAINT outbox_event_uq UNIQUE (event_id),
  CONSTRAINT outbox_event_fk FOREIGN KEY (company_id, event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT outbox_attempts_non_negative CHECK (attempts >= 0)
);
CREATE INDEX outbox_pending ON core.outbox (outbox_id) WHERE dispatched_at IS NULL;

CREATE TABLE core.inbox (
  consumer      text        NOT NULL,
  company_id    uuid        NOT NULL,
  event_id      uuid        NOT NULL,
  processed_at  timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT inbox_pk PRIMARY KEY (consumer, event_id),
  CONSTRAINT inbox_event_fk FOREIGN KEY (company_id, event_id) REFERENCES core.domain_event (company_id, event_id)
);
CREATE TRIGGER inbox_append_only BEFORE UPDATE OR DELETE ON core.inbox
  FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER inbox_no_truncate BEFORE TRUNCATE ON core.inbox
  FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- State history (ADR-027) and document graph (v2.1 §9.3; VS#1 link types, Errata §9.5).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE core.state_history (
  state_history_id  uuid NOT NULL,
  company_id        uuid NOT NULL,
  aggregate_type    text NOT NULL,
  aggregate_id      uuid NOT NULL,
  status_kind       text NOT NULL,
  from_state        text,
  to_state          text NOT NULL,
  command           text NOT NULL,
  event_id          uuid NOT NULL,
  reason            text,
  CONSTRAINT state_history_pk PRIMARY KEY (state_history_id),
  CONSTRAINT state_history_company_uq UNIQUE (company_id, state_history_id),
  CONSTRAINT state_history_event_fk FOREIGN KEY (company_id, event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT state_history_status_kind CHECK (status_kind IN ('DOCUMENT', 'ACCOUNTING'))
);
CREATE TRIGGER state_history_append_only BEFORE UPDATE OR DELETE ON core.state_history
  FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER state_history_no_truncate BEFORE TRUNCATE ON core.state_history
  FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

CREATE TYPE core.link_type AS ENUM ('RECEIVES', 'BILLS', 'REVERSES', 'CORRECTS');

CREATE TABLE core.document_link (
  link_id       uuid NOT NULL,
  company_id    uuid NOT NULL,
  from_type     text NOT NULL,
  from_id       uuid NOT NULL,
  from_line_id  uuid,
  to_type       text NOT NULL,
  to_id         uuid NOT NULL,
  to_line_id    uuid,
  link_type     core.link_type NOT NULL,
  qty           numeric(18,6),
  amount        numeric(19,4),
  event_id      uuid NOT NULL,
  CONSTRAINT document_link_pk PRIMARY KEY (link_id),
  CONSTRAINT document_link_company_uq UNIQUE (company_id, link_id),
  CONSTRAINT document_link_event_fk FOREIGN KEY (company_id, event_id) REFERENCES core.domain_event (company_id, event_id)
);
CREATE UNIQUE INDEX document_link_uq ON core.document_link
  (from_id, COALESCE(from_line_id, '00000000-0000-0000-0000-000000000000'::uuid),
   to_id,   COALESCE(to_line_id,   '00000000-0000-0000-0000-000000000000'::uuid), link_type);
CREATE TRIGGER document_link_append_only BEFORE UPDATE OR DELETE ON core.document_link
  FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER document_link_no_truncate BEFORE TRUNCATE ON core.document_link
  FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- Request log (Errata E-3): observability of every attempt, written outside the command TX. No FKs.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE obs.request_log (
  request_id       uuid        NOT NULL,
  company_id       uuid,
  command_type     text,
  idempotency_key  text,
  session_id       uuid,
  correlation_id   uuid,
  outcome          text        NOT NULL,
  error_code       text,
  error_message    text,
  duration_ms      integer,
  received_at      timestamptz NOT NULL,
  CONSTRAINT request_log_pk PRIMARY KEY (request_id),
  CONSTRAINT request_log_outcome CHECK (outcome IN
    ('SUCCEEDED', 'DUPLICATE_RETURNED', 'REJECTED_DOMAIN', 'CONFLICT_RETRYABLE', 'FAILED_TECHNICAL'))
);

-- ---------------------------------------------------------------------------------------------
-- Application role privileges: no DELETE, no TRUNCATE; UPDATE only on the listed columns.
-- ---------------------------------------------------------------------------------------------
GRANT SELECT, INSERT ON core.command_log TO rochell_app;
GRANT UPDATE (result_payload, committed_at) ON core.command_log TO rochell_app;
GRANT SELECT, INSERT ON core.domain_event, core.inbox, core.state_history, core.document_link TO rochell_app;
GRANT SELECT, INSERT ON core.outbox TO rochell_app;
GRANT UPDATE (available_at, dispatched_at, attempts) ON core.outbox TO rochell_app;
GRANT USAGE ON SEQUENCE core.outbox_outbox_id_seq TO rochell_app;
GRANT SELECT, INSERT ON obs.request_log TO rochell_app;
