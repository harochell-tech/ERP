-- PR-15 · Hash chain: integrity state, seals, daily digests. v2.1 §8 / ADR-037, Frozen Baseline §9.4, Patch 1 P-2,
-- approved errata E-PR15-1…8.

CREATE SCHEMA audit;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'rochell_sealer') THEN
    CREATE ROLE rochell_sealer NOLOGIN;   -- E-PR15-3: the only writer of seals and digests
  END IF;
END $$;

-- P-2 / E-PR15-1: one row per ledger group; PENDING_SEAL is written in the command's transaction (by the triggers below),
-- SEALED / SEAL_ERROR only by the sealer.
CREATE TABLE audit.integrity_state (
  company_id       uuid        NOT NULL REFERENCES md.company,
  ledger           text        NOT NULL,
  group_ref        uuid        NOT NULL,
  posting_date     date,
  business_date    date,
  integrity_status text        NOT NULL,
  ledger_sequence  bigint,
  error_detail     text,
  updated_at       timestamptz NOT NULL,
  CONSTRAINT integrity_state_pk PRIMARY KEY (company_id, ledger, group_ref),
  CONSTRAINT integrity_state_ledger CHECK (ledger IN ('GL', 'INV_QTY', 'INV_VALUE', 'DOMAIN_EVENT')),
  CONSTRAINT integrity_state_status CHECK (integrity_status IN ('PENDING_SEAL', 'SEALED', 'SEAL_ERROR')),
  CONSTRAINT integrity_state_sequence CHECK ((integrity_status = 'SEALED') = (ledger_sequence IS NOT NULL)),
  CONSTRAINT integrity_state_error CHECK ((integrity_status = 'SEAL_ERROR') = (error_detail IS NOT NULL))
);
CREATE INDEX integrity_pending ON audit.integrity_state (company_id, posting_date) WHERE integrity_status <> 'SEALED';
CREATE INDEX integrity_to_seal ON audit.integrity_state (company_id, ledger, updated_at, group_ref) WHERE integrity_status = 'PENDING_SEAL';

CREATE TABLE audit.ledger_seal (
  company_id      uuid        NOT NULL REFERENCES md.company,
  ledger          text        NOT NULL,
  ledger_sequence bigint      NOT NULL,
  group_ref       uuid        NOT NULL,
  group_hash      bytea       NOT NULL,
  prev_hash       bytea       NOT NULL,
  chain_hash      bytea       NOT NULL,
  sealed_at       timestamptz NOT NULL,
  CONSTRAINT ledger_seal_pk PRIMARY KEY (company_id, ledger, ledger_sequence),
  CONSTRAINT ledger_seal_group_uq UNIQUE (company_id, ledger, group_ref),
  CONSTRAINT ledger_seal_sequence CHECK (ledger_sequence >= 1),
  CONSTRAINT ledger_seal_hashes CHECK (octet_length(group_hash) = 32 AND octet_length(prev_hash) = 32 AND octet_length(chain_hash) = 32)
);

CREATE TABLE audit.ledger_digest (
  company_id       uuid   NOT NULL REFERENCES md.company,
  ledger           text   NOT NULL,
  digest_date      date   NOT NULL,
  first_seq        bigint NOT NULL,
  last_seq         bigint NOT NULL,
  item_count       int    NOT NULL,
  merkle_root      bytea  NOT NULL,
  last_chain_hash  bytea  NOT NULL,
  prev_digest_hash bytea,
  digest_hash      bytea  NOT NULL,
  worm_object_key  text   NOT NULL,
  CONSTRAINT ledger_digest_pk PRIMARY KEY (company_id, ledger, digest_date),
  CONSTRAINT ledger_digest_range CHECK (first_seq >= 1 AND last_seq >= first_seq AND item_count = last_seq - first_seq + 1)
);

CREATE TRIGGER ledger_seal_append_only BEFORE UPDATE OR DELETE ON audit.ledger_seal
  FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER ledger_digest_append_only BEFORE UPDATE OR DELETE ON audit.ledger_digest
  FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

-- Only the status columns of integrity_state ever change, and only forward from PENDING_SEAL.
CREATE FUNCTION audit.integrity_state_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'audit.integrity_state rows cannot be deleted';
  END IF;
  IF ROW(NEW.company_id, NEW.ledger, NEW.group_ref, NEW.posting_date, NEW.business_date)
     IS DISTINCT FROM ROW(OLD.company_id, OLD.ledger, OLD.group_ref, OLD.posting_date, OLD.business_date)
     OR OLD.integrity_status <> 'PENDING_SEAL' THEN
    RAISE EXCEPTION 'audit.integrity_state: only PENDING_SEAL → SEALED / SEAL_ERROR is allowed';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER integrity_state_guard BEFORE UPDATE OR DELETE ON audit.integrity_state
  FOR EACH ROW EXECUTE FUNCTION audit.integrity_state_guard();

-- E-PR15-1 / E-PR15-2: registers the group of a new ledger row as PENDING_SEAL; a sealed (or failed) group cannot grow.
CREATE FUNCTION audit.register_group(p_company uuid, p_ledger text, p_group uuid, p_posting date, p_business date) RETURNS void
  LANGUAGE plpgsql AS $$
DECLARE
  current_status text;
BEGIN
  INSERT INTO audit.integrity_state (company_id, ledger, group_ref, posting_date, business_date, integrity_status, updated_at)
  VALUES (p_company, p_ledger, p_group, p_posting, p_business, 'PENDING_SEAL', now())
  ON CONFLICT (company_id, ledger, group_ref) DO NOTHING;
  SELECT integrity_status INTO current_status FROM audit.integrity_state
   WHERE company_id = p_company AND ledger = p_ledger AND group_ref = p_group;
  IF current_status <> 'PENDING_SEAL' THEN
    RAISE EXCEPTION 'audit: % group % is already %; a sealed group cannot receive rows (E-PR15-1)', p_ledger, p_group, current_status;
  END IF;
END $$;

CREATE FUNCTION audit.register_gl_journal() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN PERFORM audit.register_group(NEW.company_id, 'GL', NEW.journal_id, NEW.posting_date, NULL); RETURN NULL; END $$;
CREATE FUNCTION audit.register_gl_entry() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN PERFORM audit.register_group(NEW.company_id, 'GL', NEW.journal_id, NEW.posting_date, NULL); RETURN NULL; END $$;
CREATE FUNCTION audit.register_inv_quantity() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN PERFORM audit.register_group(NEW.company_id, 'INV_QTY', NEW.source_event_id, NEW.posting_date, NEW.business_date); RETURN NULL; END $$;
CREATE FUNCTION audit.register_inv_value() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN PERFORM audit.register_group(NEW.company_id, 'INV_VALUE', NEW.source_event_id, NEW.posting_date, NEW.business_date); RETURN NULL; END $$;
CREATE FUNCTION audit.register_domain_event() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN PERFORM audit.register_group(NEW.company_id, 'DOMAIN_EVENT', NEW.command_id, NULL, NEW.business_date); RETURN NULL; END $$;

CREATE TRIGGER gl_journal_integrity AFTER INSERT ON fin.gl_journal FOR EACH ROW EXECUTE FUNCTION audit.register_gl_journal();
CREATE TRIGGER gl_entry_integrity AFTER INSERT ON fin.gl_entry FOR EACH ROW EXECUTE FUNCTION audit.register_gl_entry();
CREATE TRIGGER inv_quantity_entry_integrity AFTER INSERT ON inv.inv_quantity_entry FOR EACH ROW EXECUTE FUNCTION audit.register_inv_quantity();
CREATE TRIGGER inv_value_entry_integrity AFTER INSERT ON inv.inv_value_entry FOR EACH ROW EXECUTE FUNCTION audit.register_inv_value();
CREATE TRIGGER domain_event_integrity AFTER INSERT ON core.domain_event FOR EACH ROW EXECUTE FUNCTION audit.register_domain_event();

-- Groups that existed before this migration start as PENDING_SEAL, so the first sealing covers the whole history.
INSERT INTO audit.integrity_state (company_id, ledger, group_ref, posting_date, business_date, integrity_status, updated_at)
SELECT company_id, 'GL', journal_id, posting_date, NULL, 'PENDING_SEAL', recorded FROM (
  SELECT j.company_id, j.journal_id, j.posting_date, j.occurred_at AS recorded FROM fin.gl_journal j) g
UNION ALL
SELECT company_id, 'INV_QTY', source_event_id, min(posting_date), min(business_date), 'PENDING_SEAL', min(recorded_at)
  FROM inv.inv_quantity_entry GROUP BY company_id, source_event_id
UNION ALL
SELECT company_id, 'INV_VALUE', source_event_id, min(posting_date), min(business_date), 'PENDING_SEAL', min(recorded_at)
  FROM inv.inv_value_entry GROUP BY company_id, source_event_id
UNION ALL
SELECT company_id, 'DOMAIN_EVENT', command_id, NULL, min(business_date), 'PENDING_SEAL', min(recorded_at)
  FROM core.domain_event GROUP BY company_id, command_id;

-- Row-level security and privileges.
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['audit.integrity_state', 'audit.ledger_seal', 'audit.ledger_digest'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT USAGE ON SCHEMA audit TO rochell_app;
GRANT SELECT, INSERT ON audit.integrity_state TO rochell_app;          -- INSERT through the triggers, in the command's TX
GRANT SELECT ON audit.ledger_seal, audit.ledger_digest TO rochell_app;  -- verification (hash:verify)

GRANT USAGE ON SCHEMA md, core, fin, inv, audit TO rochell_sealer;
GRANT SELECT ON md.company, core.domain_event, fin.gl_journal, fin.gl_entry, inv.inv_quantity_entry, inv.inv_value_entry TO rochell_sealer;
GRANT SELECT ON core.deployment_environment TO rochell_sealer;
GRANT SELECT ON audit.integrity_state, audit.ledger_seal, audit.ledger_digest TO rochell_sealer;
GRANT UPDATE (integrity_status, ledger_sequence, error_detail, updated_at) ON audit.integrity_state TO rochell_sealer;
GRANT INSERT ON audit.ledger_seal, audit.ledger_digest TO rochell_sealer;
