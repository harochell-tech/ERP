-- PR-05 · Finance core (Posting Engine). Frozen Baseline v2.1.1 §8.4, Patch 1 (P-1, P-4, P-5, P-6, K-20),
-- Patch 1.1, approved errata E-PR05-1…8.

CREATE SCHEMA fin;
REVOKE ALL ON SCHEMA fin FROM PUBLIC;
GRANT USAGE ON SCHEMA fin TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- Account roles (global, E-PR05-5). Control roles can only map to control accounts and vice versa.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fin.account_role (
  role_code    text    NOT NULL,
  is_control   boolean NOT NULL,
  description  text    NOT NULL,
  CONSTRAINT account_role_pk PRIMARY KEY (role_code),
  CONSTRAINT account_role_code_format CHECK (role_code ~ '^[A-Z][A-Z0-9_]*$')
);
INSERT INTO fin.account_role (role_code, is_control, description) VALUES
  ('RAW_MATERIAL', true, 'Inventario de materia prima (subledger INV)'),
  ('AP_CONTROL', true, 'Cuentas por pagar (subledger AP)'),
  ('GRNI', false, 'Recibido no facturado'),
  ('ITBIS_RECOVERABLE', false, 'ITBIS adelantado'),
  ('WITHHOLDING_PAYABLE', false, 'Retenciones por pagar'),
  ('PURCHASE_PRICE_VARIANCE', false, 'Variación de precio de compra'),
  ('MATERIAL_USAGE_VARIANCE', false, 'Variación de uso de material'),
  ('INVENTORY_ADJUSTMENT', false, 'Ajuste de inventario'),
  ('ROUNDING_DIFFERENCE', false, 'Diferencia de redondeo');
CREATE TRIGGER account_role_immutable BEFORE UPDATE OR DELETE ON fin.account_role FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- Chart of accounts (company-scoped). Loaded with `rochell-migrate import-accounts` (E-PR05-1); immutable in VS#1.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fin.account (
  account_id  uuid    NOT NULL,
  company_id  uuid    NOT NULL,
  code        text    NOT NULL,
  name        text    NOT NULL,
  is_control  boolean NOT NULL,
  CONSTRAINT account_pk PRIMARY KEY (account_id),
  CONSTRAINT account_company_uq UNIQUE (company_id, account_id),
  CONSTRAINT account_code_uq UNIQUE (company_id, code),
  CONSTRAINT account_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT account_code_format CHECK (code ~ '^[0-9A-Z][0-9A-Z.-]{0,29}$'),
  CONSTRAINT account_name_present CHECK (length(btrim(name)) > 0)
);
CREATE TRIGGER account_immutable BEFORE UPDATE OR DELETE ON fin.account FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- Account role map (versioned). DRAFT rows are loaded by the deployment role; the Controller approves them
-- (ApproveAccountRoleMap). prepared_by ≠ approved_by. ACTIVE ranges never overlap (P-6).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fin.account_role_map (
  map_id          uuid NOT NULL,
  company_id      uuid NOT NULL,
  account_role    text NOT NULL,
  item_category   text,
  account_id      uuid NOT NULL,
  effective_from  date NOT NULL,
  effective_to    date,
  prepared_by     uuid NOT NULL,
  approved_by     uuid,
  status          text NOT NULL,
  CONSTRAINT account_role_map_pk PRIMARY KEY (map_id),
  CONSTRAINT account_role_map_company_uq UNIQUE (company_id, map_id),
  CONSTRAINT account_role_map_account_fk FOREIGN KEY (company_id, account_id) REFERENCES fin.account (company_id, account_id),
  CONSTRAINT account_role_map_role_fk FOREIGN KEY (account_role) REFERENCES fin.account_role (role_code),
  CONSTRAINT account_role_map_prepared_by_fk FOREIGN KEY (prepared_by) REFERENCES iam.user (user_id),
  CONSTRAINT account_role_map_approved_by_fk FOREIGN KEY (approved_by) REFERENCES iam.user (user_id),
  CONSTRAINT account_role_map_status CHECK (status IN ('DRAFT', 'ACTIVE')),
  CONSTRAINT account_role_map_approval CHECK ((status = 'ACTIVE') = (approved_by IS NOT NULL)),
  CONSTRAINT account_role_map_four_eyes CHECK (approved_by IS NULL OR approved_by <> prepared_by),
  CONSTRAINT account_role_map_category CHECK (item_category IS NULL OR item_category IN ('CEMENTO', 'AGREGADO', 'ADITIVO', 'OTRA_MATERIA_PRIMA')),
  CONSTRAINT account_role_map_range CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT account_role_map_no_overlap EXCLUDE USING gist (
    company_id WITH =, account_role WITH =, (COALESCE(item_category, '*')) WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&) WHERE (status = 'ACTIVE')
);

CREATE FUNCTION fin.account_role_map_control_match() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF (SELECT is_control FROM fin.account WHERE account_id = NEW.account_id)
     IS DISTINCT FROM (SELECT is_control FROM fin.account_role WHERE role_code = NEW.account_role) THEN
    RAISE EXCEPTION 'fin.account_role_map: control roles map only to control accounts and vice versa (E-PR05-5)';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER account_role_map_control_match BEFORE INSERT ON fin.account_role_map
  FOR EACH ROW EXECUTE FUNCTION fin.account_role_map_control_match();

CREATE FUNCTION fin.versioned_config_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  -- Shared by account_role_map and posting_rule_version: DRAFT → ACTIVE once (with approver);
  -- afterwards only effective_to may be set, once. Nothing is deleted.
  IF TG_OP <> 'UPDATE' THEN
    RAISE EXCEPTION 'fin.% rows cannot be deleted', TG_TABLE_NAME;
  END IF;
  IF OLD.status = 'DRAFT' AND NEW.status = 'ACTIVE' AND OLD.effective_to IS NOT DISTINCT FROM NEW.effective_to THEN
    RETURN NEW;
  END IF;
  IF OLD.status = 'ACTIVE' AND NEW.status = 'ACTIVE' AND OLD.effective_to IS NULL AND NEW.effective_to IS NOT NULL
     AND OLD.approved_by = NEW.approved_by THEN
    RETURN NEW;
  END IF;
  RAISE EXCEPTION 'fin.%: only DRAFT → ACTIVE, or closing an ACTIVE range once, is allowed', TG_TABLE_NAME;
END $$;

CREATE FUNCTION fin.account_role_map_identity() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF ROW(NEW.map_id, NEW.company_id, NEW.account_role, NEW.item_category, NEW.account_id, NEW.effective_from, NEW.prepared_by)
     IS DISTINCT FROM ROW(OLD.map_id, OLD.company_id, OLD.account_role, OLD.item_category, OLD.account_id, OLD.effective_from, OLD.prepared_by) THEN
    RAISE EXCEPTION 'fin.account_role_map: mapping columns are immutable';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER account_role_map_guard BEFORE UPDATE OR DELETE ON fin.account_role_map FOR EACH ROW EXECUTE FUNCTION fin.versioned_config_guard();
CREATE TRIGGER account_role_map_identity BEFORE UPDATE ON fin.account_role_map FOR EACH ROW EXECUTE FUNCTION fin.account_role_map_identity();

-- ---------------------------------------------------------------------------------------------
-- Posting rules (global, versioned; E-PR05-2 declarative definition; E-PR05-4 close component).
-- Production rules arrive as DRAFT rows in later PRs and are approved by the Controller.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fin.posting_rule (
  posting_rule_id  uuid NOT NULL,
  code             text NOT NULL,
  event_type       text NOT NULL,
  CONSTRAINT posting_rule_pk PRIMARY KEY (posting_rule_id),
  CONSTRAINT posting_rule_code_uq UNIQUE (code),
  CONSTRAINT posting_rule_code_format CHECK (code ~ '^[A-Z][A-Z0-9_.-]*$')
);
CREATE TRIGGER posting_rule_immutable BEFORE UPDATE OR DELETE ON fin.posting_rule FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

CREATE TABLE fin.posting_rule_version (
  posting_rule_id        uuid    NOT NULL,
  version                integer NOT NULL,
  definition             jsonb   NOT NULL,
  explanation_templates  jsonb   NOT NULL,
  close_component        text    NOT NULL,
  effective_from         date    NOT NULL,
  effective_to           date,
  status                 text    NOT NULL,
  approved_by            uuid,
  CONSTRAINT posting_rule_version_pk PRIMARY KEY (posting_rule_id, version),
  CONSTRAINT posting_rule_version_rule_fk FOREIGN KEY (posting_rule_id) REFERENCES fin.posting_rule (posting_rule_id),
  CONSTRAINT posting_rule_version_approved_by_fk FOREIGN KEY (approved_by) REFERENCES iam.user (user_id),
  CONSTRAINT posting_rule_version_positive CHECK (version >= 1),
  CONSTRAINT posting_rule_version_status CHECK (status IN ('DRAFT', 'ACTIVE')),
  CONSTRAINT posting_rule_version_approval CHECK ((status = 'ACTIVE') = (approved_by IS NOT NULL)),
  CONSTRAINT posting_rule_version_component CHECK (close_component IN ('INV-MOV', 'AP-REC')),
  CONSTRAINT posting_rule_version_definition_shape CHECK (jsonb_typeof(definition -> 'lines') = 'array'),
  CONSTRAINT posting_rule_version_range CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT posting_rule_version_no_overlap EXCLUDE USING gist (
    posting_rule_id WITH =, daterange(effective_from, effective_to, '[)') WITH &&) WHERE (status = 'ACTIVE')
);

CREATE FUNCTION fin.posting_rule_version_identity() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF ROW(NEW.posting_rule_id, NEW.version, NEW.definition, NEW.explanation_templates, NEW.close_component, NEW.effective_from)
     IS DISTINCT FROM ROW(OLD.posting_rule_id, OLD.version, OLD.definition, OLD.explanation_templates, OLD.close_component, OLD.effective_from) THEN
    RAISE EXCEPTION 'fin.posting_rule_version: definition columns are immutable; create a new version';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER posting_rule_version_guard BEFORE UPDATE OR DELETE ON fin.posting_rule_version FOR EACH ROW EXECUTE FUNCTION fin.versioned_config_guard();
CREATE TRIGGER posting_rule_version_identity BEFORE UPDATE ON fin.posting_rule_version FOR EACH ROW EXECUTE FUNCTION fin.posting_rule_version_identity();

-- ---------------------------------------------------------------------------------------------
-- Periods (E-PR05-3): calendar months, created with `rochell-migrate open-periods <rnc> <year>`.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fin.period (
  period_id   uuid NOT NULL,
  company_id  uuid NOT NULL,
  starts_on   date NOT NULL,
  ends_on     date NOT NULL,
  CONSTRAINT period_pk PRIMARY KEY (period_id),
  CONSTRAINT period_company_uq UNIQUE (company_id, period_id),
  CONSTRAINT period_start_uq UNIQUE (company_id, starts_on),
  CONSTRAINT period_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT period_is_calendar_month CHECK (
    starts_on = date_trunc('month', starts_on)::date
    AND ends_on = (date_trunc('month', starts_on) + interval '1 month' - interval '1 day')::date)
);
CREATE TRIGGER period_immutable BEFORE UPDATE OR DELETE ON fin.period FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

CREATE TABLE fin.close_component_state (
  company_id     uuid        NOT NULL,
  period_id      uuid        NOT NULL,
  component      text        NOT NULL,
  status         text        NOT NULL,
  closed_by      uuid,
  closed_at      timestamptz,
  snapshot_hash  bytea,
  version        bigint      NOT NULL,
  CONSTRAINT close_component_state_pk PRIMARY KEY (period_id, component),
  CONSTRAINT close_component_state_period_fk FOREIGN KEY (company_id, period_id) REFERENCES fin.period (company_id, period_id),
  CONSTRAINT close_component_state_closed_by_fk FOREIGN KEY (closed_by) REFERENCES iam.user (user_id),
  CONSTRAINT close_component_state_component CHECK (component IN ('INV-MOV', 'AP-REC')),
  CONSTRAINT close_component_state_status CHECK (status IN ('OPEN', 'CLOSED', 'REOPENED')),
  CONSTRAINT close_component_state_version_positive CHECK (version >= 1)
);

-- ---------------------------------------------------------------------------------------------
-- Journals and entries (append-only). Balance and ≥ 2 lines checked at COMMIT; entries only in the
-- journal's own transaction. Amounts in DOP with 2 decimals (E-PR05-6, E-PR05-7).
-- gl_entry.inv_value_entry_id gets its FK in PR-07 (E-PR05-8).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fin.gl_journal (
  journal_id            uuid        NOT NULL,
  company_id            uuid        NOT NULL,
  posting_date          date        NOT NULL,
  period_id             uuid        NOT NULL,
  source_event_id       uuid        NOT NULL,
  posting_rule_id       uuid        NOT NULL,
  posting_rule_version  integer     NOT NULL,
  posting_generation    integer     NOT NULL DEFAULT 1,
  journal_type          text        NOT NULL,
  reverses_journal_id   uuid,
  late_entry            boolean     NOT NULL DEFAULT false,
  occurred_at           timestamptz NOT NULL,
  row_hash              bytea       NOT NULL,
  CONSTRAINT gl_journal_pk PRIMARY KEY (journal_id),
  CONSTRAINT gl_journal_company_uq UNIQUE (company_id, journal_id),
  CONSTRAINT gl_journal_generation_uq UNIQUE (source_event_id, posting_rule_id, posting_generation),
  CONSTRAINT gl_journal_single_reversal_uq UNIQUE (reverses_journal_id),
  CONSTRAINT gl_journal_period_fk FOREIGN KEY (company_id, period_id) REFERENCES fin.period (company_id, period_id),
  CONSTRAINT gl_journal_event_fk FOREIGN KEY (company_id, source_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT gl_journal_rule_fk FOREIGN KEY (posting_rule_id, posting_rule_version) REFERENCES fin.posting_rule_version (posting_rule_id, version),
  CONSTRAINT gl_journal_reverses_fk FOREIGN KEY (company_id, reverses_journal_id) REFERENCES fin.gl_journal (company_id, journal_id),
  CONSTRAINT gl_journal_type CHECK (journal_type IN ('AUTO', 'REVERSAL', 'VALUATION_REALLOCATION')),
  CONSTRAINT gl_journal_reversal_link CHECK ((journal_type = 'REVERSAL') = (reverses_journal_id IS NOT NULL)),
  CONSTRAINT gl_journal_generation_positive CHECK (posting_generation >= 1),
  CONSTRAINT gl_journal_row_hash_length CHECK (octet_length(row_hash) = 32)
);

CREATE TABLE fin.gl_entry (
  gl_entry_id           uuid          NOT NULL,
  journal_id            uuid          NOT NULL,
  line_no               integer       NOT NULL,
  company_id            uuid          NOT NULL,
  posting_date          date          NOT NULL,
  account_id            uuid          NOT NULL,
  account_role          text          NOT NULL,
  debit                 numeric(19,4) NOT NULL DEFAULT 0,
  credit                numeric(19,4) NOT NULL DEFAULT 0,
  currency              char(3)       NOT NULL DEFAULT 'DOP',
  plant_id              uuid,
  item_id               uuid,
  party_id              uuid,
  subledger_type        text,
  subledger_ref         uuid,
  inv_value_entry_id    uuid,
  source_event_id       uuid          NOT NULL,
  rule_line_code        text          NOT NULL,
  determination_inputs  jsonb         NOT NULL,
  row_hash              bytea         NOT NULL,
  CONSTRAINT gl_entry_pk PRIMARY KEY (gl_entry_id),
  CONSTRAINT gl_entry_company_uq UNIQUE (company_id, gl_entry_id),
  CONSTRAINT gl_entry_line_uq UNIQUE (journal_id, line_no),
  CONSTRAINT gl_entry_value_entry_uq UNIQUE (inv_value_entry_id),
  CONSTRAINT gl_entry_journal_fk FOREIGN KEY (company_id, journal_id) REFERENCES fin.gl_journal (company_id, journal_id),
  CONSTRAINT gl_entry_account_fk FOREIGN KEY (company_id, account_id) REFERENCES fin.account (company_id, account_id),
  CONSTRAINT gl_entry_role_fk FOREIGN KEY (account_role) REFERENCES fin.account_role (role_code),
  CONSTRAINT gl_entry_plant_fk FOREIGN KEY (company_id, plant_id) REFERENCES md.plant (company_id, plant_id),
  CONSTRAINT gl_entry_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT gl_entry_party_fk FOREIGN KEY (company_id, party_id) REFERENCES md.party (company_id, party_id),
  CONSTRAINT gl_entry_event_fk FOREIGN KEY (company_id, source_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT gl_entry_sides CHECK (debit >= 0 AND credit >= 0 AND (debit = 0) <> (credit = 0)),
  CONSTRAINT gl_entry_two_decimals CHECK (debit = round(debit, 2) AND credit = round(credit, 2)),
  CONSTRAINT gl_entry_currency_vs1 CHECK (currency = 'DOP'),
  CONSTRAINT gl_entry_subledger_type CHECK (subledger_type IS NULL OR subledger_type IN ('AP', 'INV')),
  CONSTRAINT gl_entry_subledger_pair CHECK ((subledger_type IS NULL) = (subledger_ref IS NULL)),
  CONSTRAINT gl_entry_row_hash_length CHECK (octet_length(row_hash) = 32)
);
CREATE INDEX gl_entry_account_date ON fin.gl_entry (company_id, account_id, posting_date);
CREATE INDEX gl_entry_subledger ON fin.gl_entry (subledger_type, subledger_ref) WHERE subledger_type IS NOT NULL;

-- Entries belong to a journal created in the same transaction, share its posting date, and respect control accounts.
CREATE FUNCTION fin.gl_entry_before_insert() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  journal_xmin xid;
  journal_date date;
  account_is_control boolean;
  role_is_control boolean;
BEGIN
  SELECT xmin, posting_date INTO journal_xmin, journal_date FROM fin.gl_journal WHERE journal_id = NEW.journal_id;
  IF journal_xmin IS DISTINCT FROM pg_current_xact_id()::xid THEN
    RAISE EXCEPTION 'fin.gl_entry: lines can only be added in the transaction that created journal %', NEW.journal_id;
  END IF;
  IF NEW.posting_date <> journal_date THEN
    RAISE EXCEPTION 'fin.gl_entry: posting_date must equal the journal posting_date';
  END IF;
  SELECT is_control INTO account_is_control FROM fin.account WHERE account_id = NEW.account_id;
  SELECT is_control INTO role_is_control FROM fin.account_role WHERE role_code = NEW.account_role;
  IF account_is_control IS DISTINCT FROM role_is_control THEN
    RAISE EXCEPTION 'fin.gl_entry: account and account role disagree on control status';
  END IF;
  IF account_is_control AND NEW.subledger_type IS NULL THEN
    RAISE EXCEPTION 'fin.gl_entry: control accounts are posted only from their subledger';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER gl_entry_before_insert BEFORE INSERT ON fin.gl_entry FOR EACH ROW EXECUTE FUNCTION fin.gl_entry_before_insert();

CREATE FUNCTION fin.gl_journal_balanced() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  lines integer;
  total_debit numeric;
  total_credit numeric;
BEGIN
  SELECT count(*), coalesce(sum(debit), 0), coalesce(sum(credit), 0)
    INTO lines, total_debit, total_credit
    FROM fin.gl_entry WHERE journal_id = NEW.journal_id;
  IF lines < 2 OR total_debit <> total_credit THEN
    RAISE EXCEPTION 'fin.gl_journal %: unbalanced or incomplete (lines %, debit %, credit %)', NEW.journal_id, lines, total_debit, total_credit;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER gl_journal_balanced AFTER INSERT ON fin.gl_journal
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.gl_journal_balanced();

-- Posting date must fall inside the journal's period.
CREATE FUNCTION fin.gl_journal_before_insert() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM fin.period WHERE period_id = NEW.period_id AND NEW.posting_date BETWEEN starts_on AND ends_on) THEN
    RAISE EXCEPTION 'fin.gl_journal: posting_date % is outside period %', NEW.posting_date, NEW.period_id;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER gl_journal_before_insert BEFORE INSERT ON fin.gl_journal FOR EACH ROW EXECUTE FUNCTION fin.gl_journal_before_insert();

CREATE TRIGGER gl_journal_append_only BEFORE UPDATE OR DELETE ON fin.gl_journal FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER gl_journal_no_truncate BEFORE TRUNCATE ON fin.gl_journal FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER gl_entry_append_only BEFORE UPDATE OR DELETE ON fin.gl_entry FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER gl_entry_no_truncate BEFORE TRUNCATE ON fin.gl_entry FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- Period balances: projection maintained by the Posting Engine in the same transaction.
CREATE TABLE fin.gl_period_balance (
  company_id  uuid          NOT NULL,
  period_id   uuid          NOT NULL,
  account_id  uuid          NOT NULL,
  plant_id    uuid,
  party_id    uuid,
  debit       numeric(19,4) NOT NULL,
  credit      numeric(19,4) NOT NULL,
  CONSTRAINT gl_period_balance_uq UNIQUE NULLS NOT DISTINCT (company_id, period_id, account_id, plant_id, party_id),
  CONSTRAINT gl_period_balance_period_fk FOREIGN KEY (company_id, period_id) REFERENCES fin.period (company_id, period_id),
  CONSTRAINT gl_period_balance_account_fk FOREIGN KEY (company_id, account_id) REFERENCES fin.account (company_id, account_id),
  CONSTRAINT gl_period_balance_non_negative CHECK (debit >= 0 AND credit >= 0)
);

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['fin.account', 'fin.account_role_map', 'fin.period', 'fin.close_component_state',
                           'fin.gl_journal', 'fin.gl_entry', 'fin.gl_period_balance'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT ON fin.account_role, fin.account, fin.posting_rule, fin.period, fin.close_component_state TO rochell_app;
GRANT SELECT ON fin.account_role_map, fin.posting_rule_version TO rochell_app;
GRANT UPDATE (status, approved_by, effective_to) ON fin.account_role_map TO rochell_app;
GRANT UPDATE (status, approved_by, effective_to) ON fin.posting_rule_version TO rochell_app;
GRANT SELECT, INSERT ON fin.gl_journal, fin.gl_entry TO rochell_app;
GRANT SELECT, INSERT ON fin.gl_period_balance TO rochell_app;
GRANT UPDATE (debit, credit) ON fin.gl_period_balance TO rochell_app;
