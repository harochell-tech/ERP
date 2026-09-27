-- FIN1-01 · Schema of the adjustment journal, the account classes and the report structures. Frozen Baseline FIN-1 with errata
-- E-FIN1-1…10 and E-FIN1-01-1…7.

-- ---------------------------------------------------------------------------------------------
-- Accounts (E-FIN1-3/4, E-FIN1-01-1): a class for the statements (nullable for existing accounts) and a status; an account with
-- movements is never deleted, only made INACTIVE. The Controller creates and edits accounts (FIN1-02).
-- ---------------------------------------------------------------------------------------------
ALTER TABLE fin.account
  ADD COLUMN account_class text,
  ADD COLUMN status text NOT NULL DEFAULT 'ACTIVE',
  ADD CONSTRAINT account_class_values CHECK (account_class IS NULL OR account_class IN ('ASSET', 'LIABILITY', 'EQUITY', 'REVENUE', 'COST', 'EXPENSE')),
  ADD CONSTRAINT account_status_values CHECK (status IN ('ACTIVE', 'INACTIVE'));

DROP TRIGGER account_immutable ON fin.account;
CREATE FUNCTION fin.account_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'fin.account rows cannot be deleted; make the account INACTIVE';
  END IF;
  IF ROW(NEW.account_id, NEW.company_id, NEW.code, NEW.is_control) IS DISTINCT FROM ROW(OLD.account_id, OLD.company_id, OLD.code, OLD.is_control) THEN
    RAISE EXCEPTION 'fin.account: code and control flag are immutable';
  END IF;
  IF NEW.status = 'INACTIVE' AND OLD.status = 'ACTIVE' AND EXISTS (
       SELECT 1 FROM fin.gl_entry e WHERE e.account_id = NEW.account_id GROUP BY e.account_id HAVING sum(e.debit - e.credit) <> 0) THEN
    RAISE EXCEPTION 'fin.account %: an account with a balance cannot be made INACTIVE (E-FIN1-7)', NEW.code;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER account_guard BEFORE UPDATE OR DELETE ON fin.account FOR EACH ROW EXECUTE FUNCTION fin.account_guard();
CREATE TRIGGER account_no_truncate BEFORE TRUNCATE ON fin.account FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

GRANT INSERT ON fin.account TO rochell_app;
GRANT UPDATE (name, account_class, status) ON fin.account TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- E-FIN1-01-2: manual lines carry the technical role MANUAL_ADJUSTMENT (never mapped) and rule line P-34; a manual journal has no
-- posting rule, and neither has its reversal.
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.account_role (role_code, is_control, description) VALUES
  ('MANUAL_ADJUSTMENT', false, 'Línea de un asiento de ajuste manual (P-34); rol técnico, nunca se mapea');

CREATE FUNCTION fin.account_role_map_not_manual() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.account_role = 'MANUAL_ADJUSTMENT' THEN
    RAISE EXCEPTION 'fin.account_role_map: MANUAL_ADJUSTMENT is a technical role and is never mapped (E-FIN1-01-2)';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER account_role_map_not_manual BEFORE INSERT ON fin.account_role_map
  FOR EACH ROW EXECUTE FUNCTION fin.account_role_map_not_manual();

ALTER TABLE fin.gl_journal
  ALTER COLUMN posting_rule_id DROP NOT NULL,
  ALTER COLUMN posting_rule_version DROP NOT NULL,
  DROP CONSTRAINT gl_journal_type,
  ADD CONSTRAINT gl_journal_type CHECK (journal_type IN ('AUTO', 'REVERSAL', 'VALUATION_REALLOCATION', 'MANUAL_ADJUSTMENT')),
  ADD CONSTRAINT gl_journal_rule_presence CHECK (
    (posting_rule_id IS NULL) = (posting_rule_version IS NULL)
    AND (journal_type <> 'MANUAL_ADJUSTMENT' OR posting_rule_id IS NULL)
    AND (posting_rule_id IS NOT NULL OR journal_type IN ('MANUAL_ADJUSTMENT', 'REVERSAL')));

-- A journal without a rule is a manual adjustment or the reversal of one; its lines are MANUAL_ADJUSTMENT lines, and a
-- MANUAL_ADJUSTMENT line belongs only to such a journal (so a control account is never adjusted by hand: the role is not control).
CREATE FUNCTION fin.gl_entry_manual_line() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  manual boolean;
BEGIN
  SELECT j.posting_rule_id IS NULL INTO manual FROM fin.gl_journal j WHERE j.journal_id = NEW.journal_id;
  IF manual <> (NEW.account_role = 'MANUAL_ADJUSTMENT') THEN
    RAISE EXCEPTION 'fin.gl_entry: MANUAL_ADJUSTMENT lines go only in manual journals and their reversals (E-FIN1-01-2)';
  END IF;
  IF manual AND NEW.rule_line_code <> 'P-34' THEN
    RAISE EXCEPTION 'fin.gl_entry: a manual line has rule line code P-34';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER gl_entry_manual_line BEFORE INSERT ON fin.gl_entry
  FOR EACH ROW EXECUTE FUNCTION fin.gl_entry_manual_line();

-- ---------------------------------------------------------------------------------------------
-- E-FIN1-01-3: close components ACR-NTX and ACR-TAX, accepted everywhere a component is named, OPEN in every existing period.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE fin.posting_rule_version DROP CONSTRAINT posting_rule_version_component,
  ADD CONSTRAINT posting_rule_version_component CHECK (close_component IN ('INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX'));
ALTER TABLE fin.posting_rule_version DROP CONSTRAINT posting_rule_version_also_requires,
  ADD CONSTRAINT posting_rule_version_also_requires CHECK (also_requires_components <@ ARRAY['INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX']::text[]);
ALTER TABLE fin.close_component_state DROP CONSTRAINT close_component_state_component,
  ADD CONSTRAINT close_component_state_component CHECK (component IN ('INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX'));
ALTER TABLE fin.close_snapshot DROP CONSTRAINT close_snapshot_component,
  ADD CONSTRAINT close_snapshot_component CHECK (component IN ('INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX'));
ALTER TABLE fin.reopen_request DROP CONSTRAINT reopen_request_component,
  ADD CONSTRAINT reopen_request_component CHECK (component IN ('INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX'));
ALTER TABLE rec.recon_blocking DROP CONSTRAINT recon_blocking_component,
  ADD CONSTRAINT recon_blocking_component CHECK (component IN ('INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX'));
ALTER TABLE rec.recon_exception DROP CONSTRAINT recon_exception_component,
  ADD CONSTRAINT recon_exception_component CHECK (component IS NULL OR component IN ('INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX'));

INSERT INTO fin.close_component_state (company_id, period_id, component, status, version)
SELECT p.company_id, p.period_id, c.component, 'OPEN', 1 FROM fin.period p CROSS JOIN (VALUES ('ACR-NTX'), ('ACR-TAX')) AS c (component)
ON CONFLICT (period_id, component) DO NOTHING;

-- ---------------------------------------------------------------------------------------------
-- The adjustment journal (E-FIN1-1/2/6/7/8, E-FIN1-01-4/6/7). Prepared by the Contador, approved by the Controller (another
-- person); on approval its journal is posted (P-34) and, if auto-reversing, its reversal dated the first day of the next month.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fin.manual_journal (
  manual_journal_id  uuid        NOT NULL,
  company_id         uuid        NOT NULL,
  journal_no         text        NOT NULL,
  posting_date       date        NOT NULL,
  description        text        NOT NULL,
  support_ref        text        NOT NULL,
  support_sha256     bytea       NOT NULL,
  close_component    text        NOT NULL,
  auto_reverse       boolean     NOT NULL,
  status             text        NOT NULL,
  prepared_by        uuid        NOT NULL,
  approved_by        uuid,
  rejected_by        uuid,
  rejection_reason   text,
  posting_event_id   uuid,
  reversal_event_id  uuid,
  version            bigint      NOT NULL,
  CONSTRAINT manual_journal_pk PRIMARY KEY (manual_journal_id),
  CONSTRAINT manual_journal_company_uq UNIQUE (company_id, manual_journal_id),
  CONSTRAINT manual_journal_no_uq UNIQUE (company_id, journal_no),
  CONSTRAINT manual_journal_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT manual_journal_prepared_by_fk FOREIGN KEY (prepared_by) REFERENCES iam.user (user_id),
  CONSTRAINT manual_journal_approved_by_fk FOREIGN KEY (approved_by) REFERENCES iam.user (user_id),
  CONSTRAINT manual_journal_rejected_by_fk FOREIGN KEY (rejected_by) REFERENCES iam.user (user_id),
  CONSTRAINT manual_journal_posting_event_fk FOREIGN KEY (company_id, posting_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT manual_journal_reversal_event_fk FOREIGN KEY (company_id, reversal_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT manual_journal_no_format CHECK (journal_no ~ '^AJ-[0-9]{6,}$'),
  CONSTRAINT manual_journal_texts CHECK (length(btrim(description)) > 0 AND length(btrim(support_ref)) > 0),
  CONSTRAINT manual_journal_support_hash CHECK (octet_length(support_sha256) = 32),
  CONSTRAINT manual_journal_component CHECK (close_component IN ('ACR-NTX', 'ACR-TAX')),
  CONSTRAINT manual_journal_status CHECK (status IN ('DRAFT', 'PENDING_APPROVAL', 'POSTED', 'REJECTED', 'REVERSED')),
  CONSTRAINT manual_journal_four_eyes CHECK (approved_by IS NULL OR approved_by <> prepared_by),
  CONSTRAINT manual_journal_rejecter CHECK (rejected_by IS NULL OR rejected_by <> prepared_by),
  CONSTRAINT manual_journal_approved CHECK ((status IN ('POSTED', 'REVERSED')) = (approved_by IS NOT NULL AND posting_event_id IS NOT NULL)),
  CONSTRAINT manual_journal_rejected CHECK ((status = 'REJECTED') = (rejected_by IS NOT NULL AND length(btrim(rejection_reason)) > 0)),
  CONSTRAINT manual_journal_reversed CHECK ((status <> 'REVERSED' OR reversal_event_id IS NOT NULL)
    AND (reversal_event_id IS NULL OR status = 'REVERSED' OR (status = 'POSTED' AND auto_reverse))),
  CONSTRAINT manual_journal_version_positive CHECK (version >= 1)
);

CREATE TABLE fin.manual_journal_line (
  company_id         uuid          NOT NULL,
  manual_journal_id  uuid          NOT NULL,
  line_no            integer       NOT NULL,
  account_id         uuid          NOT NULL,
  debit              numeric(19,4) NOT NULL,
  credit             numeric(19,4) NOT NULL,
  plant_id           uuid,
  party_id           uuid,
  memo               text,
  journal_version    bigint        NOT NULL,
  CONSTRAINT manual_journal_line_pk PRIMARY KEY (manual_journal_id, journal_version, line_no),
  CONSTRAINT manual_journal_line_journal_fk FOREIGN KEY (company_id, manual_journal_id) REFERENCES fin.manual_journal (company_id, manual_journal_id),
  CONSTRAINT manual_journal_line_account_fk FOREIGN KEY (company_id, account_id) REFERENCES fin.account (company_id, account_id),
  CONSTRAINT manual_journal_line_plant_fk FOREIGN KEY (company_id, plant_id) REFERENCES md.plant (company_id, plant_id),
  CONSTRAINT manual_journal_line_party_fk FOREIGN KEY (company_id, party_id) REFERENCES md.party (company_id, party_id),
  CONSTRAINT manual_journal_line_sides CHECK (debit >= 0 AND credit >= 0 AND (debit = 0) <> (credit = 0)),
  CONSTRAINT manual_journal_line_two_decimals CHECK (debit = round(debit, 2) AND credit = round(credit, 2)),
  CONSTRAINT manual_journal_line_no_positive CHECK (line_no >= 1)
);

-- Lines are written once per version of a DRAFT journal (like payment allocations, E-VS2-03-1); only to active, non-control
-- accounts (E-FIN1-2).
CREATE FUNCTION fin.manual_journal_line_before_insert() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM fin.manual_journal j WHERE j.manual_journal_id = NEW.manual_journal_id AND j.status = 'DRAFT' AND j.version = NEW.journal_version) THEN
    RAISE EXCEPTION 'fin.manual_journal_line: lines are written for the current version of a DRAFT adjustment';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM fin.account a WHERE a.account_id = NEW.account_id AND NOT a.is_control AND a.status = 'ACTIVE') THEN
    RAISE EXCEPTION 'fin.manual_journal_line: account % is a control account or inactive (E-FIN1-2)', NEW.account_id;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER manual_journal_line_before_insert BEFORE INSERT ON fin.manual_journal_line
  FOR EACH ROW EXECUTE FUNCTION fin.manual_journal_line_before_insert();
CREATE TRIGGER manual_journal_line_append_only BEFORE UPDATE OR DELETE ON fin.manual_journal_line FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER manual_journal_line_no_truncate BEFORE TRUNCATE ON fin.manual_journal_line FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- Transitions: DRAFT → DRAFT (update, version + 1) | PENDING_APPROVAL; PENDING_APPROVAL → POSTED | REJECTED | DRAFT (back to the
-- preparer); POSTED → REVERSED (manual reversal) or POSTED with its automatic reversal recorded at approval.
CREATE FUNCTION fin.manual_journal_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'fin.manual_journal rows cannot be deleted';
  END IF;
  IF ROW(NEW.manual_journal_id, NEW.company_id, NEW.journal_no, NEW.prepared_by) IS DISTINCT FROM ROW(OLD.manual_journal_id, OLD.company_id, OLD.journal_no, OLD.prepared_by) THEN
    RAISE EXCEPTION 'fin.manual_journal: identity columns are immutable';
  END IF;
  IF OLD.status <> 'DRAFT' AND ROW(NEW.posting_date, NEW.description, NEW.support_ref, NEW.support_sha256, NEW.close_component, NEW.auto_reverse)
     IS DISTINCT FROM ROW(OLD.posting_date, OLD.description, OLD.support_ref, OLD.support_sha256, OLD.close_component, OLD.auto_reverse) THEN
    RAISE EXCEPTION 'fin.manual_journal: only a DRAFT adjustment can be changed';
  END IF;
  IF NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'fin.manual_journal: version must increase by exactly 1';
  END IF;
  IF NEW.status <> OLD.status AND NOT (
       (OLD.status = 'DRAFT' AND NEW.status = 'PENDING_APPROVAL') OR
       (OLD.status = 'PENDING_APPROVAL' AND NEW.status IN ('POSTED', 'REJECTED', 'DRAFT')) OR
       (OLD.status = 'POSTED' AND NEW.status = 'REVERSED')) THEN
    RAISE EXCEPTION 'fin.manual_journal: transition % → % is not allowed', OLD.status, NEW.status;
  END IF;
  IF NEW.status = OLD.status AND OLD.status <> 'DRAFT' THEN
    RAISE EXCEPTION 'fin.manual_journal: a % adjustment changes only by a transition', OLD.status;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER manual_journal_guard BEFORE UPDATE OR DELETE ON fin.manual_journal
  FOR EACH ROW EXECUTE FUNCTION fin.manual_journal_guard();
CREATE TRIGGER manual_journal_no_truncate BEFORE TRUNCATE ON fin.manual_journal FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
CREATE CONSTRAINT TRIGGER manual_journal_evidence_on_insert AFTER INSERT ON fin.manual_journal
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('ManualJournal', 'manual_journal_id');
CREATE CONSTRAINT TRIGGER manual_journal_evidence_on_change AFTER UPDATE ON fin.manual_journal
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('ManualJournal', 'manual_journal_id');

-- K-25 for adjustments, at COMMIT: POSTED ⇔ a live MANUAL_ADJUSTMENT journal of its posting event; REVERSED ⇔ that journal has its
-- reversal. A DRAFT sent for approval has balanced lines (at least two) for its current version.
CREATE FUNCTION fin.manual_journal_evidence() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  live boolean;
  reversed boolean;
  lines integer;
  debit numeric;
  credit numeric;
BEGIN
  SELECT EXISTS (SELECT 1 FROM fin.gl_journal j WHERE j.company_id = NEW.company_id AND j.source_event_id = NEW.posting_event_id AND j.journal_type = 'MANUAL_ADJUSTMENT'),
         EXISTS (SELECT 1 FROM fin.gl_journal j JOIN fin.gl_journal r ON r.reverses_journal_id = j.journal_id
                 WHERE j.company_id = NEW.company_id AND j.source_event_id = NEW.posting_event_id AND j.journal_type = 'MANUAL_ADJUSTMENT')
    INTO live, reversed;
  IF NEW.status IN ('POSTED', 'REVERSED') AND NOT live THEN
    RAISE EXCEPTION 'fin.manual_journal %: % without its MANUAL_ADJUSTMENT journal (K-25)', NEW.journal_no, NEW.status;
  END IF;
  IF (NEW.status = 'REVERSED' OR (NEW.status = 'POSTED' AND NEW.auto_reverse)) AND NOT reversed THEN
    RAISE EXCEPTION 'fin.manual_journal %: its reversal journal is missing', NEW.journal_no;
  END IF;
  IF NEW.status = 'POSTED' AND NOT NEW.auto_reverse AND reversed THEN
    RAISE EXCEPTION 'fin.manual_journal %: POSTED while its journal is reversed', NEW.journal_no;
  END IF;
  IF NEW.status = 'PENDING_APPROVAL' THEN
    SELECT count(*), coalesce(sum(l.debit), 0), coalesce(sum(l.credit), 0) INTO lines, debit, credit
    FROM fin.manual_journal_line l WHERE l.manual_journal_id = NEW.manual_journal_id AND l.journal_version = (
      SELECT max(x.journal_version) FROM fin.manual_journal_line x WHERE x.manual_journal_id = NEW.manual_journal_id);
    IF lines < 2 OR debit <> credit THEN
      RAISE EXCEPTION 'fin.manual_journal %: sent for approval unbalanced or with fewer than two lines', NEW.journal_no;
    END IF;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER manual_journal_evidence AFTER INSERT OR UPDATE ON fin.manual_journal
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.manual_journal_evidence();

-- A journal without a rule takes its close component from its adjustment (E-FIN1-6): no posting into a period whose ACR component
-- is CLOSED. Rule-based journals keep the check of 0026.
CREATE OR REPLACE FUNCTION fin.gl_journal_component_open() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  component text;
BEGIN
  IF NEW.posting_rule_id IS NULL THEN
    SELECT m.close_component INTO component FROM fin.manual_journal m
    WHERE m.company_id = NEW.company_id AND m.posting_event_id = coalesce(
      (SELECT o.source_event_id FROM fin.gl_journal o WHERE o.journal_id = NEW.reverses_journal_id), NEW.source_event_id);
    IF component IS NULL THEN
      RAISE EXCEPTION 'fin.gl_journal %: a journal without a posting rule belongs to an adjustment', NEW.journal_id;
    END IF;
    IF EXISTS (SELECT 1 FROM fin.close_component_state s WHERE s.period_id = NEW.period_id AND s.component = component AND s.status = 'CLOSED') THEN
      RAISE EXCEPTION 'fin.gl_journal %: % is CLOSED in its period', NEW.journal_id, component;
    END IF;
    RETURN NEW;
  END IF;
  IF EXISTS (
       SELECT 1
       FROM fin.posting_rule_version v
       JOIN fin.close_component_state s ON s.period_id = NEW.period_id
         AND (s.component = v.close_component OR s.component = ANY (v.also_requires_components))
       WHERE v.posting_rule_id = NEW.posting_rule_id AND v.version = NEW.posting_rule_version AND s.status = 'CLOSED') THEN
    RAISE EXCEPTION 'fin.gl_journal %: its period and a close component it requires are CLOSED (E-VS1-9, E-VS2-03-3)', NEW.journal_id;
  END IF;
  RETURN NEW;
END $$;

-- ---------------------------------------------------------------------------------------------
-- Report structures (E-FIN1-5): versioned lines of the balance sheet and the income statement, prepared by the Controller and
-- approved by the Aprobador de políticas; each account in at most one line per structure version.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fin.report_structure_version (
  structure_version_id  uuid        NOT NULL,
  company_id            uuid        NOT NULL,
  report                text        NOT NULL,
  version               integer     NOT NULL,
  effective_from        date        NOT NULL,
  status                text        NOT NULL,
  prepared_by           uuid        NOT NULL,
  approved_by           uuid,
  CONSTRAINT report_structure_version_pk PRIMARY KEY (structure_version_id),
  CONSTRAINT report_structure_version_company_uq UNIQUE (company_id, structure_version_id),
  CONSTRAINT report_structure_version_no_uq UNIQUE (company_id, report, version),
  CONSTRAINT report_structure_version_prepared_fk FOREIGN KEY (prepared_by) REFERENCES iam.user (user_id),
  CONSTRAINT report_structure_version_approved_fk FOREIGN KEY (approved_by) REFERENCES iam.user (user_id),
  CONSTRAINT report_structure_version_report CHECK (report IN ('BALANCE_SHEET', 'INCOME_STATEMENT')),
  CONSTRAINT report_structure_version_status CHECK (status IN ('DRAFT', 'ACTIVE', 'SUPERSEDED')),
  CONSTRAINT report_structure_version_four_eyes CHECK (approved_by IS NULL OR approved_by <> prepared_by),
  CONSTRAINT report_structure_version_approved CHECK ((status IN ('ACTIVE', 'SUPERSEDED')) = (approved_by IS NOT NULL))
);
CREATE UNIQUE INDEX report_structure_one_active ON fin.report_structure_version (company_id, report) WHERE status = 'ACTIVE';

CREATE TABLE fin.report_line (
  report_line_id        uuid    NOT NULL,
  company_id            uuid    NOT NULL,
  structure_version_id  uuid    NOT NULL,
  line_code             text    NOT NULL,
  caption               text    NOT NULL,
  parent_line_code      text,
  sign                  integer NOT NULL,
  order_no              integer NOT NULL,
  CONSTRAINT report_line_pk PRIMARY KEY (report_line_id),
  CONSTRAINT report_line_code_uq UNIQUE (structure_version_id, line_code),
  CONSTRAINT report_line_structure_fk FOREIGN KEY (company_id, structure_version_id) REFERENCES fin.report_structure_version (company_id, structure_version_id),
  CONSTRAINT report_line_parent_fk FOREIGN KEY (structure_version_id, parent_line_code) REFERENCES fin.report_line (structure_version_id, line_code),
  CONSTRAINT report_line_sign CHECK (sign IN (-1, 1)),
  CONSTRAINT report_line_caption_present CHECK (length(btrim(caption)) > 0)
);

CREATE TABLE fin.report_line_account (
  company_id            uuid NOT NULL,
  structure_version_id  uuid NOT NULL,
  report_line_id        uuid NOT NULL,
  account_id            uuid NOT NULL,
  CONSTRAINT report_line_account_pk PRIMARY KEY (report_line_id, account_id),
  CONSTRAINT report_line_account_once UNIQUE (structure_version_id, account_id),
  CONSTRAINT report_line_account_line_fk FOREIGN KEY (report_line_id) REFERENCES fin.report_line (report_line_id),
  CONSTRAINT report_line_account_account_fk FOREIGN KEY (company_id, account_id) REFERENCES fin.account (company_id, account_id)
);

-- A structure is edited only while DRAFT; approval only moves DRAFT → ACTIVE (the previous ACTIVE → SUPERSEDED).
CREATE FUNCTION fin.report_structure_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'fin.report_structure_version rows cannot be deleted';
  END IF;
  IF ROW(NEW.structure_version_id, NEW.company_id, NEW.report, NEW.version, NEW.effective_from, NEW.prepared_by)
     IS DISTINCT FROM ROW(OLD.structure_version_id, OLD.company_id, OLD.report, OLD.version, OLD.effective_from, OLD.prepared_by)
     OR NOT ((OLD.status = 'DRAFT' AND NEW.status = 'ACTIVE') OR (OLD.status = 'ACTIVE' AND NEW.status = 'SUPERSEDED')) THEN
    RAISE EXCEPTION 'fin.report_structure_version: % → % is not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER report_structure_guard BEFORE UPDATE OR DELETE ON fin.report_structure_version
  FOR EACH ROW EXECUTE FUNCTION fin.report_structure_guard();

CREATE FUNCTION fin.report_line_draft_only() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM fin.report_structure_version v WHERE v.structure_version_id = NEW.structure_version_id AND v.status = 'DRAFT') THEN
    RAISE EXCEPTION 'report lines are added only to a DRAFT structure';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER report_line_draft_only BEFORE INSERT ON fin.report_line FOR EACH ROW EXECUTE FUNCTION fin.report_line_draft_only();
CREATE TRIGGER report_line_account_draft_only BEFORE INSERT ON fin.report_line_account FOR EACH ROW EXECUTE FUNCTION fin.report_line_draft_only();
CREATE TRIGGER report_line_append_only BEFORE UPDATE OR DELETE ON fin.report_line FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER report_line_account_append_only BEFORE UPDATE OR DELETE ON fin.report_line_account FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- Role CONTADOR, permissions and segregation of duties (E-FIN1-01-5, E-FIN1-1/9). READ permissions take part in no SoD rule.
-- ---------------------------------------------------------------------------------------------
INSERT INTO iam.permission (permission_code, access) VALUES
  ('account:manage', 'WRITE'), ('manual_journal:prepare', 'WRITE'), ('manual_journal:approve', 'WRITE'),
  ('report_structure:approve', 'WRITE'), ('ledger:read', 'READ');

INSERT INTO iam.role (role_id, code, name) VALUES (gen_random_uuid(), 'CONTADOR', 'Contador');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES
  ('CONTADOR', 'manual_journal:prepare'), ('CONTADOR', 'ledger:read'),
  ('CONTROLLER', 'account:manage'), ('CONTROLLER', 'manual_journal:approve'), ('CONTROLLER', 'ledger:read'),
  ('APROBADOR_POLITICAS', 'report_structure:approve'),
  ('AUDITOR', 'ledger:read'), ('DIRECTOR', 'ledger:read')
) AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;

INSERT INTO iam.sod_rule (permission_a, permission_b) VALUES ('manual_journal:approve', 'manual_journal:prepare');

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['fin.manual_journal', 'fin.manual_journal_line', 'fin.report_structure_version', 'fin.report_line', 'fin.report_line_account'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON fin.manual_journal, fin.manual_journal_line, fin.report_structure_version, fin.report_line, fin.report_line_account TO rochell_app;
GRANT UPDATE (posting_date, description, support_ref, support_sha256, close_component, auto_reverse, status, approved_by, rejected_by,
  rejection_reason, posting_event_id, reversal_event_id, version) ON fin.manual_journal TO rochell_app;
GRANT UPDATE (status, approved_by) ON fin.report_structure_version TO rochell_app;
