-- VS2-01 · Supplier payments and banks: schema, seeds, row-level security and grants (no commands yet).
-- Frozen Baseline VS#2 §2 and §7; approved errata E-VS2-1…10 and E-VS2-01-1…14.

CREATE TYPE fin.payment_status AS ENUM ('PREPARED', 'RELEASED', 'CLEARED', 'VOIDED', 'REVERSED');

-- ---------------------------------------------------------------------------------------------
-- Account roles and the BANK subledger (E-VS2-01-1, E-VS2-01-2, E-VS2-01-13).
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.account_role (role_code, is_control, description) VALUES
  ('BANK', true, 'Bancos (subledger BANK; una cuenta contable por cuenta bancaria)'),
  ('BANK_CHARGES', false, 'Cargos y comisiones bancarias');

ALTER TABLE fin.gl_entry
  DROP CONSTRAINT gl_entry_subledger_type,
  ADD CONSTRAINT gl_entry_subledger_type CHECK (subledger_type IS NULL OR subledger_type IN ('AP', 'INV', 'BANK')),
  DROP CONSTRAINT gl_entry_role_subledger,
  ADD CONSTRAINT gl_entry_role_subledger CHECK (
    (account_role <> 'RAW_MATERIAL' OR subledger_type = 'INV') AND (account_role <> 'AP_CONTROL' OR subledger_type = 'AP')
    AND ((account_role = 'BANK') = (subledger_type IS NOT DISTINCT FROM 'BANK')));

-- The BANK role is resolved from the payment's bank account, never from the role map (E-VS2-01-1).
ALTER TABLE fin.account_role_map ADD CONSTRAINT account_role_map_not_bank CHECK (account_role <> 'BANK');

-- ---------------------------------------------------------------------------------------------
-- Close component BANK-REC (E-VS2-01-6): accepted everywhere a component is named, OPEN in every existing period.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE fin.posting_rule_version DROP CONSTRAINT posting_rule_version_component,
  ADD CONSTRAINT posting_rule_version_component CHECK (close_component IN ('INV-MOV', 'AP-REC', 'BANK-REC'));
ALTER TABLE fin.close_component_state DROP CONSTRAINT close_component_state_component,
  ADD CONSTRAINT close_component_state_component CHECK (component IN ('INV-MOV', 'AP-REC', 'BANK-REC'));
ALTER TABLE fin.close_snapshot DROP CONSTRAINT close_snapshot_component,
  ADD CONSTRAINT close_snapshot_component CHECK (component IN ('INV-MOV', 'AP-REC', 'BANK-REC'));
ALTER TABLE fin.reopen_request DROP CONSTRAINT reopen_request_component,
  ADD CONSTRAINT reopen_request_component CHECK (component IN ('INV-MOV', 'AP-REC', 'BANK-REC'));
ALTER TABLE rec.recon_blocking DROP CONSTRAINT recon_blocking_component,
  ADD CONSTRAINT recon_blocking_component CHECK (component IN ('INV-MOV', 'AP-REC', 'BANK-REC'));
ALTER TABLE rec.recon_exception DROP CONSTRAINT recon_exception_component,
  ADD CONSTRAINT recon_exception_component CHECK (component IS NULL OR component IN ('INV-MOV', 'AP-REC', 'BANK-REC'));

INSERT INTO fin.close_component_state (company_id, period_id, component, status, version)
SELECT company_id, period_id, 'BANK-REC', 'OPEN', 1 FROM fin.period
ON CONFLICT (period_id, component) DO NOTHING;

-- ---------------------------------------------------------------------------------------------
-- Status evidence shared by the new documents (ADR-027): a status set on INSERT or changed by UPDATE has its
-- core.state_history row, written in the same transaction. TG_ARGV[0] is the aggregate type, TG_ARGV[1] the id column.
-- ---------------------------------------------------------------------------------------------
CREATE FUNCTION fin.require_state_history() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  row_data jsonb := to_jsonb(NEW);
BEGIN
  IF NOT EXISTS (
       SELECT 1 FROM core.state_history h
       WHERE h.aggregate_type = TG_ARGV[0] AND h.aggregate_id = (row_data ->> TG_ARGV[1])::uuid
         AND h.to_state = row_data ->> 'status' AND h.xmin = pg_current_xact_id()::xid) THEN
    RAISE EXCEPTION '% %: status % without its state_history row (ADR-027)', TG_ARGV[0], row_data ->> TG_ARGV[1], row_data ->> 'status';
  END IF;
  RETURN NULL;
END $$;

-- ---------------------------------------------------------------------------------------------
-- Company bank accounts (VS#2 §2, E-VS2-1). One control GL account per bank account, not shared, not in the role map.
-- Formats (E-VS2-01-3): bank code upper case, 2–20 characters; account number digits only, 5–30.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fin.bank_account (
  bank_account_id  uuid    NOT NULL,
  company_id       uuid    NOT NULL,
  bank_code        text    NOT NULL,
  account_number   text    NOT NULL,
  currency         char(3) NOT NULL,
  gl_account_id    uuid    NOT NULL,
  status           text    NOT NULL,
  version          bigint  NOT NULL,
  CONSTRAINT bank_account_pk PRIMARY KEY (bank_account_id),
  CONSTRAINT bank_account_company_uq UNIQUE (company_id, bank_account_id),
  CONSTRAINT bank_account_number_uq UNIQUE (company_id, bank_code, account_number),
  CONSTRAINT bank_account_gl_account_uq UNIQUE (gl_account_id),
  CONSTRAINT bank_account_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT bank_account_gl_account_fk FOREIGN KEY (company_id, gl_account_id) REFERENCES fin.account (company_id, account_id),
  CONSTRAINT bank_account_bank_code_format CHECK (bank_code = upper(btrim(bank_code)) AND char_length(bank_code) BETWEEN 2 AND 20),
  CONSTRAINT bank_account_number_format CHECK (account_number ~ '^[0-9]{5,30}$'),
  CONSTRAINT bank_account_currency_vs2 CHECK (currency = 'DOP'),
  CONSTRAINT bank_account_status CHECK (status IN ('ACTIVE', 'CLOSED')),
  CONSTRAINT bank_account_version_positive CHECK (version >= 1)
);

CREATE FUNCTION fin.bank_account_before_insert() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.status <> 'ACTIVE' OR NEW.version <> 1 THEN
    RAISE EXCEPTION 'fin.bank_account: a bank account is registered ACTIVE with version 1';
  END IF;
  IF NOT (SELECT is_control FROM fin.account WHERE account_id = NEW.gl_account_id) THEN
    RAISE EXCEPTION 'fin.bank_account: its GL account must be a control account (E-VS2-01-1)';
  END IF;
  IF EXISTS (SELECT 1 FROM fin.account_role_map WHERE account_id = NEW.gl_account_id) THEN
    RAISE EXCEPTION 'fin.bank_account: GL account % is already mapped to an account role (E-VS2-01-1)', NEW.gl_account_id;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER bank_account_before_insert BEFORE INSERT ON fin.bank_account FOR EACH ROW EXECUTE FUNCTION fin.bank_account_before_insert();

CREATE FUNCTION fin.bank_account_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'UPDATE' THEN
    RAISE EXCEPTION 'fin.bank_account rows cannot be deleted; close the account';
  END IF;
  IF ROW(NEW.bank_account_id, NEW.company_id, NEW.bank_code, NEW.account_number, NEW.currency, NEW.gl_account_id)
     IS DISTINCT FROM ROW(OLD.bank_account_id, OLD.company_id, OLD.bank_code, OLD.account_number, OLD.currency, OLD.gl_account_id) THEN
    RAISE EXCEPTION 'fin.bank_account: identity columns are immutable';
  END IF;
  IF NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'fin.bank_account: version must increase by exactly 1';
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (OLD.status = 'ACTIVE' AND NEW.status = 'CLOSED') THEN
    RAISE EXCEPTION 'fin.bank_account: transition % → % is not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER bank_account_guard BEFORE UPDATE OR DELETE ON fin.bank_account FOR EACH ROW EXECUTE FUNCTION fin.bank_account_guard();
CREATE TRIGGER bank_account_no_truncate BEFORE TRUNCATE ON fin.bank_account FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
CREATE CONSTRAINT TRIGGER bank_account_evidence_on_insert AFTER INSERT ON fin.bank_account
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('BankAccount', 'bank_account_id');
CREATE CONSTRAINT TRIGGER bank_account_evidence_on_change AFTER UPDATE ON fin.bank_account
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('BankAccount', 'bank_account_id');

-- The other direction of "not in the role map": a bank's GL account cannot be mapped afterwards.
CREATE FUNCTION fin.account_role_map_not_bank_account() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF EXISTS (SELECT 1 FROM fin.bank_account WHERE gl_account_id = NEW.account_id) THEN
    RAISE EXCEPTION 'fin.account_role_map: account % belongs to a bank account (E-VS2-01-1)', NEW.account_id;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER account_role_map_not_bank_account BEFORE INSERT ON fin.account_role_map
  FOR EACH ROW EXECUTE FUNCTION fin.account_role_map_not_bank_account();

-- A BANK line posts to its bank account's own GL account (E-VS2-01-1, E-VS2-01-13).
CREATE FUNCTION fin.gl_entry_bank_line() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (
       SELECT 1 FROM fin.bank_account b
       WHERE b.company_id = NEW.company_id AND b.bank_account_id = NEW.subledger_ref AND b.gl_account_id = NEW.account_id) THEN
    RAISE EXCEPTION 'fin.gl_entry: a BANK line must reference a bank account and post to its GL account (E-VS2-01-13)';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER gl_entry_bank_line BEFORE INSERT ON fin.gl_entry
  FOR EACH ROW WHEN (NEW.subledger_type = 'BANK') EXECUTE FUNCTION fin.gl_entry_bank_line();

-- ---------------------------------------------------------------------------------------------
-- Supplier bank accounts, versioned (v2 §4, VS#2 §4). E-VS2-01-4 (evidence ≥ 20 characters, no automatic holder match),
-- E-VS2-01-8 (verification data kept in SUPERSEDED), E-VS2-01-9 (one REVIEW and one VERIFIED per supplier),
-- E-VS2-01-10 (rejection data), E-VS2-8 (72 calendar hours from the verification), PAY-10 (verifier ≠ requester).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE md.party_bank_account (
  party_bank_account_id  uuid        NOT NULL,
  company_id             uuid        NOT NULL,
  party_id               uuid        NOT NULL,
  version                integer     NOT NULL,
  bank_code              text        NOT NULL,
  account_number         text        NOT NULL,
  account_holder         text        NOT NULL,
  status                 text        NOT NULL,
  requested_by           uuid        NOT NULL,
  requested_at           timestamptz NOT NULL,
  verified_by            uuid,
  verified_at            timestamptz,
  verification_evidence  text,
  payable_from           timestamptz,
  rejected_by            uuid,
  rejected_at            timestamptz,
  rejection_reason       text,
  CONSTRAINT party_bank_account_pk PRIMARY KEY (party_bank_account_id),
  CONSTRAINT party_bank_account_company_uq UNIQUE (company_id, party_bank_account_id),
  CONSTRAINT party_bank_account_party_uq UNIQUE (company_id, party_id, party_bank_account_id),
  CONSTRAINT party_bank_account_version_uq UNIQUE (company_id, party_id, version),
  CONSTRAINT party_bank_account_party_fk FOREIGN KEY (company_id, party_id) REFERENCES md.party (company_id, party_id),
  CONSTRAINT party_bank_account_requested_by_fk FOREIGN KEY (requested_by) REFERENCES iam.user (user_id),
  CONSTRAINT party_bank_account_verified_by_fk FOREIGN KEY (verified_by) REFERENCES iam.user (user_id),
  CONSTRAINT party_bank_account_rejected_by_fk FOREIGN KEY (rejected_by) REFERENCES iam.user (user_id),
  CONSTRAINT party_bank_account_version_positive CHECK (version >= 1),
  CONSTRAINT party_bank_account_bank_code_format CHECK (bank_code = upper(btrim(bank_code)) AND char_length(bank_code) BETWEEN 2 AND 20),
  CONSTRAINT party_bank_account_number_format CHECK (account_number ~ '^[0-9]{5,30}$'),
  CONSTRAINT party_bank_account_holder_present CHECK (length(btrim(account_holder)) > 0),
  CONSTRAINT party_bank_account_status CHECK (status IN ('REVIEW', 'VERIFIED', 'REJECTED', 'SUPERSEDED')),
  CONSTRAINT party_bank_account_four_eyes CHECK (verified_by IS NULL OR verified_by <> requested_by),
  CONSTRAINT party_bank_account_rejecter CHECK (rejected_by IS NULL OR rejected_by <> requested_by),
  CONSTRAINT party_bank_account_verification CHECK (
    (status IN ('VERIFIED', 'SUPERSEDED') AND verified_by IS NOT NULL AND verified_at IS NOT NULL AND verified_at >= requested_at
       AND char_length(btrim(verification_evidence)) >= 20 AND payable_from = verified_at + interval '72 hours')
    OR (status IN ('REVIEW', 'REJECTED') AND verified_by IS NULL AND verified_at IS NULL AND verification_evidence IS NULL AND payable_from IS NULL)),
  CONSTRAINT party_bank_account_rejection CHECK (
    (status = 'REJECTED' AND rejected_by IS NOT NULL AND rejected_at IS NOT NULL AND rejected_at >= requested_at
       AND length(btrim(rejection_reason)) > 0)
    OR (status <> 'REJECTED' AND rejected_by IS NULL AND rejected_at IS NULL AND rejection_reason IS NULL))
);
CREATE UNIQUE INDEX party_bank_account_one_review ON md.party_bank_account (company_id, party_id) WHERE status = 'REVIEW';
CREATE UNIQUE INDEX party_bank_account_one_verified ON md.party_bank_account (company_id, party_id) WHERE status = 'VERIFIED';

CREATE FUNCTION md.party_bank_account_before_insert() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.status <> 'REVIEW' THEN
    RAISE EXCEPTION 'md.party_bank_account: a new account version starts in REVIEW';
  END IF;
  IF NOT (SELECT is_supplier FROM md.party WHERE party_id = NEW.party_id) THEN
    RAISE EXCEPTION 'md.party_bank_account: party % is not a supplier', NEW.party_id;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER party_bank_account_before_insert BEFORE INSERT ON md.party_bank_account
  FOR EACH ROW EXECUTE FUNCTION md.party_bank_account_before_insert();

CREATE FUNCTION md.party_bank_account_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'UPDATE' THEN
    RAISE EXCEPTION 'md.party_bank_account rows cannot be deleted';
  END IF;
  IF ROW(NEW.party_bank_account_id, NEW.company_id, NEW.party_id, NEW.version, NEW.bank_code, NEW.account_number, NEW.account_holder,
         NEW.requested_by, NEW.requested_at)
     IS DISTINCT FROM ROW(OLD.party_bank_account_id, OLD.company_id, OLD.party_id, OLD.version, OLD.bank_code, OLD.account_number,
         OLD.account_holder, OLD.requested_by, OLD.requested_at) THEN
    RAISE EXCEPTION 'md.party_bank_account: the account data is immutable; request a new version';
  END IF;
  IF NOT ((OLD.status = 'REVIEW' AND NEW.status IN ('VERIFIED', 'REJECTED')) OR (OLD.status = 'VERIFIED' AND NEW.status = 'SUPERSEDED')) THEN
    RAISE EXCEPTION 'md.party_bank_account: transition % → % is not allowed', OLD.status, NEW.status;
  END IF;
  IF OLD.status = 'VERIFIED' AND ROW(NEW.verified_by, NEW.verified_at, NEW.verification_evidence, NEW.payable_from)
     IS DISTINCT FROM ROW(OLD.verified_by, OLD.verified_at, OLD.verification_evidence, OLD.payable_from) THEN
    RAISE EXCEPTION 'md.party_bank_account: the verification is immutable (E-VS2-01-8)';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER party_bank_account_guard BEFORE UPDATE OR DELETE ON md.party_bank_account
  FOR EACH ROW EXECUTE FUNCTION md.party_bank_account_guard();
CREATE TRIGGER party_bank_account_no_truncate BEFORE TRUNCATE ON md.party_bank_account FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- A version is SUPERSEDED only by a newer version of the same supplier verified in the same transaction (VS#2 §4).
CREATE FUNCTION md.party_bank_account_superseded_by_newer() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (
       SELECT 1 FROM md.party_bank_account n
       WHERE n.company_id = NEW.company_id AND n.party_id = NEW.party_id AND n.version > NEW.version AND n.status = 'VERIFIED'
         AND n.xmin = pg_current_xact_id()::xid) THEN
    RAISE EXCEPTION 'md.party_bank_account %: SUPERSEDED needs a newer version verified in the same transaction', NEW.party_bank_account_id;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER party_bank_account_superseded AFTER UPDATE ON md.party_bank_account
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.status = 'SUPERSEDED' AND OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION md.party_bank_account_superseded_by_newer();
CREATE CONSTRAINT TRIGGER party_bank_account_evidence_on_insert AFTER INSERT ON md.party_bank_account
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('PartyBankAccount', 'party_bank_account_id');
CREATE CONSTRAINT TRIGGER party_bank_account_evidence_on_change AFTER UPDATE ON md.party_bank_account
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('PartyBankAccount', 'party_bank_account_id');

-- ---------------------------------------------------------------------------------------------
-- Payments (disbursements in VS#2). E-VS2-2 / E-VS2-01-14: transfer only, to an account of the same supplier.
-- Transitions (VS#2 §4, E-VS2-01-11): PREPARED → RELEASED | VOIDED; RELEASED → CLEARED | REVERSED; CLEARED → REVERSED | RELEASED.
-- Only a PREPARED payment can be edited (UpdatePreparedPayment); later updates change only status and version.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fin.payment (
  payment_id             uuid               NOT NULL,
  company_id             uuid               NOT NULL,
  direction              text               NOT NULL,
  party_id               uuid               NOT NULL,
  bank_account_id        uuid               NOT NULL,
  party_bank_account_id  uuid               NOT NULL,
  method                 text               NOT NULL,
  amount                 numeric(19,4)      NOT NULL,
  currency               char(3)            NOT NULL,
  value_date             date               NOT NULL,
  bank_reference         text,
  status                 fin.payment_status NOT NULL,
  prepared_by            uuid               NOT NULL,
  released_by            uuid,
  posting_event_id       uuid,
  version                bigint             NOT NULL,
  CONSTRAINT payment_pk PRIMARY KEY (payment_id),
  CONSTRAINT payment_company_uq UNIQUE (company_id, payment_id),
  CONSTRAINT payment_bank_reference_uq UNIQUE (company_id, bank_account_id, direction, bank_reference, amount, value_date),
  CONSTRAINT payment_party_fk FOREIGN KEY (company_id, party_id) REFERENCES md.party (company_id, party_id),
  CONSTRAINT payment_bank_account_fk FOREIGN KEY (company_id, bank_account_id) REFERENCES fin.bank_account (company_id, bank_account_id),
  CONSTRAINT payment_party_bank_account_fk FOREIGN KEY (company_id, party_id, party_bank_account_id)
    REFERENCES md.party_bank_account (company_id, party_id, party_bank_account_id),
  CONSTRAINT payment_prepared_by_fk FOREIGN KEY (prepared_by) REFERENCES iam.user (user_id),
  CONSTRAINT payment_released_by_fk FOREIGN KEY (released_by) REFERENCES iam.user (user_id),
  CONSTRAINT payment_posting_event_fk FOREIGN KEY (company_id, posting_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT payment_direction_vs2 CHECK (direction = 'DISBURSEMENT'),
  CONSTRAINT payment_method_vs2 CHECK (method = 'TRANSFER'),
  CONSTRAINT payment_amount_positive CHECK (amount > 0),
  CONSTRAINT payment_currency_vs2 CHECK (currency = 'DOP'),
  CONSTRAINT payment_bank_reference_present CHECK (bank_reference IS NULL OR length(btrim(bank_reference)) > 0),
  CONSTRAINT payment_four_eyes CHECK (released_by IS NULL OR released_by <> prepared_by),
  CONSTRAINT payment_release_data CHECK (
    (status::text IN ('RELEASED', 'CLEARED', 'REVERSED')) = (released_by IS NOT NULL AND posting_event_id IS NOT NULL)),
  CONSTRAINT payment_version_positive CHECK (version >= 1)
);

CREATE FUNCTION fin.payment_before_insert() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.status::text <> 'PREPARED' OR NEW.version <> 1 THEN
    RAISE EXCEPTION 'fin.payment: a payment is prepared with status PREPARED and version 1';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER payment_before_insert BEFORE INSERT ON fin.payment FOR EACH ROW EXECUTE FUNCTION fin.payment_before_insert();

CREATE FUNCTION fin.payment_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  from_state text;
  to_state text;
BEGIN
  IF TG_OP <> 'UPDATE' THEN
    RAISE EXCEPTION 'fin.payment rows cannot be deleted; void or reverse the payment';
  END IF;
  from_state := OLD.status::text;
  to_state := NEW.status::text;
  IF ROW(NEW.payment_id, NEW.company_id, NEW.direction, NEW.party_id, NEW.method, NEW.currency, NEW.prepared_by)
     IS DISTINCT FROM ROW(OLD.payment_id, OLD.company_id, OLD.direction, OLD.party_id, OLD.method, OLD.currency, OLD.prepared_by) THEN
    RAISE EXCEPTION 'fin.payment: identity columns are immutable';
  END IF;
  IF from_state <> 'PREPARED' AND ROW(NEW.bank_account_id, NEW.party_bank_account_id, NEW.amount, NEW.value_date, NEW.bank_reference,
         NEW.released_by, NEW.posting_event_id)
     IS DISTINCT FROM ROW(OLD.bank_account_id, OLD.party_bank_account_id, OLD.amount, OLD.value_date, OLD.bank_reference,
         OLD.released_by, OLD.posting_event_id) THEN
    RAISE EXCEPTION 'fin.payment: only a PREPARED payment can be changed';
  END IF;
  IF NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'fin.payment: version must increase by exactly 1';
  END IF;
  IF to_state <> from_state AND NOT (
       (from_state = 'PREPARED' AND to_state IN ('RELEASED', 'VOIDED')) OR
       (from_state = 'RELEASED' AND to_state IN ('CLEARED', 'REVERSED')) OR
       (from_state = 'CLEARED' AND to_state IN ('REVERSED', 'RELEASED'))) THEN
    RAISE EXCEPTION 'fin.payment: transition % → % is not allowed (VS#2 §4)', from_state, to_state;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER payment_guard BEFORE UPDATE OR DELETE ON fin.payment FOR EACH ROW EXECUTE FUNCTION fin.payment_guard();
CREATE TRIGGER payment_no_truncate BEFORE TRUNCATE ON fin.payment FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
CREATE CONSTRAINT TRIGGER payment_evidence_on_insert AFTER INSERT ON fin.payment
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('Payment', 'payment_id');
CREATE CONSTRAINT TRIGGER payment_evidence_on_change AFTER UPDATE ON fin.payment
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('Payment', 'payment_id');

-- ---------------------------------------------------------------------------------------------
-- Applications of a payment to AP documents (append-only; unapplying = a reversal row). E-VS2-9 / E-VS2-01-14: every
-- applied document belongs to the payment's supplier; a reversal mirrors exactly one live application.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fin.ap_application (
  application_id           uuid          NOT NULL,
  company_id               uuid          NOT NULL,
  payment_id               uuid          NOT NULL,
  ap_doc_id                uuid          NOT NULL,
  amount                   numeric(19,4) NOT NULL,
  event_id                 uuid          NOT NULL,
  reverses_application_id  uuid,
  CONSTRAINT ap_application_pk PRIMARY KEY (application_id),
  CONSTRAINT ap_application_company_uq UNIQUE (company_id, application_id),
  CONSTRAINT ap_application_reverses_uq UNIQUE (reverses_application_id),
  CONSTRAINT ap_application_payment_fk FOREIGN KEY (company_id, payment_id) REFERENCES fin.payment (company_id, payment_id),
  CONSTRAINT ap_application_ap_doc_fk FOREIGN KEY (company_id, ap_doc_id) REFERENCES fin.ap_document (company_id, ap_doc_id),
  CONSTRAINT ap_application_event_fk FOREIGN KEY (company_id, event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT ap_application_reverses_fk FOREIGN KEY (company_id, reverses_application_id)
    REFERENCES fin.ap_application (company_id, application_id),
  CONSTRAINT ap_application_amount_positive CHECK (amount > 0),
  CONSTRAINT ap_application_not_self CHECK (reverses_application_id IS DISTINCT FROM application_id)
);
CREATE INDEX ap_application_ap_doc ON fin.ap_application (company_id, ap_doc_id);
CREATE INDEX ap_application_payment ON fin.ap_application (company_id, payment_id);

CREATE FUNCTION fin.ap_application_before_insert() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  original fin.ap_application;
BEGIN
  IF (SELECT party_id FROM fin.ap_document WHERE ap_doc_id = NEW.ap_doc_id)
     IS DISTINCT FROM (SELECT party_id FROM fin.payment WHERE payment_id = NEW.payment_id) THEN
    RAISE EXCEPTION 'fin.ap_application: AP document % does not belong to the payment''s supplier (E-VS2-01-14)', NEW.ap_doc_id;
  END IF;
  IF NEW.reverses_application_id IS NOT NULL THEN
    SELECT * INTO original FROM fin.ap_application WHERE application_id = NEW.reverses_application_id;
    IF original.reverses_application_id IS NOT NULL
       OR ROW(original.payment_id, original.ap_doc_id, original.amount) IS DISTINCT FROM ROW(NEW.payment_id, NEW.ap_doc_id, NEW.amount) THEN
      RAISE EXCEPTION 'fin.ap_application: a reversal mirrors one original application (same payment, document and amount)';
    END IF;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER ap_application_before_insert BEFORE INSERT ON fin.ap_application
  FOR EACH ROW EXECUTE FUNCTION fin.ap_application_before_insert();
CREATE TRIGGER ap_application_append_only BEFORE UPDATE OR DELETE ON fin.ap_application FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER ap_application_no_truncate BEFORE TRUNCATE ON fin.ap_application FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- Bank statements (append-only evidence) and their lines. IDM-04 / E-VS2-01-12: a line is identified by account,
-- direction, reference (NULL included), amount, value date and its occurrence among identical lines of one file.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fin.bank_statement (
  statement_id     uuid          NOT NULL,
  company_id       uuid          NOT NULL,
  bank_account_id  uuid          NOT NULL,
  period_from      date          NOT NULL,
  period_to        date          NOT NULL,
  opening_balance  numeric(19,4) NOT NULL,
  closing_balance  numeric(19,4) NOT NULL,
  file_sha256      bytea         NOT NULL,
  imported_by      uuid          NOT NULL,
  imported_at      timestamptz   NOT NULL,
  CONSTRAINT bank_statement_pk PRIMARY KEY (statement_id),
  CONSTRAINT bank_statement_company_uq UNIQUE (company_id, statement_id),
  CONSTRAINT bank_statement_account_uq UNIQUE (company_id, statement_id, bank_account_id),
  CONSTRAINT bank_statement_file_uq UNIQUE (company_id, bank_account_id, file_sha256),
  CONSTRAINT bank_statement_bank_account_fk FOREIGN KEY (company_id, bank_account_id) REFERENCES fin.bank_account (company_id, bank_account_id),
  CONSTRAINT bank_statement_imported_by_fk FOREIGN KEY (imported_by) REFERENCES iam.user (user_id),
  CONSTRAINT bank_statement_range CHECK (period_to >= period_from),
  CONSTRAINT bank_statement_file_hash CHECK (octet_length(file_sha256) = 32)
);
CREATE TRIGGER bank_statement_append_only BEFORE UPDATE OR DELETE ON fin.bank_statement FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER bank_statement_no_truncate BEFORE TRUNCATE ON fin.bank_statement FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

CREATE TABLE fin.bank_statement_line (
  line_id             uuid          NOT NULL,
  company_id          uuid          NOT NULL,
  statement_id        uuid          NOT NULL,
  bank_account_id     uuid          NOT NULL,
  value_date          date          NOT NULL,
  direction           text          NOT NULL,
  amount              numeric(19,4) NOT NULL,
  bank_reference      text,
  description         text          NOT NULL,
  occurrence          integer       NOT NULL,
  status              text          NOT NULL,
  matched_payment_id  uuid,
  charge_event_id     uuid,
  version             bigint        NOT NULL,
  CONSTRAINT bank_statement_line_pk PRIMARY KEY (line_id),
  CONSTRAINT bank_statement_line_company_uq UNIQUE (company_id, line_id),
  CONSTRAINT bank_statement_line_idm04_uq UNIQUE NULLS NOT DISTINCT
    (company_id, bank_account_id, direction, bank_reference, amount, value_date, occurrence),
  CONSTRAINT bank_statement_line_statement_fk FOREIGN KEY (company_id, statement_id, bank_account_id)
    REFERENCES fin.bank_statement (company_id, statement_id, bank_account_id),
  CONSTRAINT bank_statement_line_payment_fk FOREIGN KEY (company_id, matched_payment_id) REFERENCES fin.payment (company_id, payment_id),
  CONSTRAINT bank_statement_line_charge_event_fk FOREIGN KEY (company_id, charge_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT bank_statement_line_direction CHECK (direction IN ('DEBIT', 'CREDIT')),
  CONSTRAINT bank_statement_line_amount_positive CHECK (amount > 0),
  CONSTRAINT bank_statement_line_reference_present CHECK (bank_reference IS NULL OR length(btrim(bank_reference)) > 0),
  CONSTRAINT bank_statement_line_description_present CHECK (length(btrim(description)) > 0),
  CONSTRAINT bank_statement_line_occurrence_positive CHECK (occurrence >= 1),
  CONSTRAINT bank_statement_line_status CHECK (status IN ('UNMATCHED', 'MATCHED', 'CHARGE_RECOGNIZED')),
  CONSTRAINT bank_statement_line_matched CHECK ((status = 'MATCHED') = (matched_payment_id IS NOT NULL)),
  CONSTRAINT bank_statement_line_charge CHECK ((status = 'CHARGE_RECOGNIZED') = (charge_event_id IS NOT NULL)),
  CONSTRAINT bank_statement_line_version_positive CHECK (version >= 1)
);
CREATE INDEX bank_statement_line_statement ON fin.bank_statement_line (company_id, statement_id);
CREATE INDEX bank_statement_line_payment ON fin.bank_statement_line (matched_payment_id) WHERE matched_payment_id IS NOT NULL;

CREATE FUNCTION fin.bank_statement_line_before_insert() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.status <> 'UNMATCHED' OR NEW.version <> 1 THEN
    RAISE EXCEPTION 'fin.bank_statement_line: a line is imported UNMATCHED with version 1';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM fin.bank_statement s WHERE s.statement_id = NEW.statement_id AND NEW.value_date BETWEEN s.period_from AND s.period_to) THEN
    RAISE EXCEPTION 'fin.bank_statement_line: value date % is outside its statement''s period', NEW.value_date;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER bank_statement_line_before_insert BEFORE INSERT ON fin.bank_statement_line
  FOR EACH ROW EXECUTE FUNCTION fin.bank_statement_line_before_insert();

-- Transitions (VS#2 §4): UNMATCHED → MATCHED | CHARGE_RECOGNIZED; MATCHED → UNMATCHED. A match is a DEBIT line of the payment's bank account.
CREATE FUNCTION fin.bank_statement_line_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'UPDATE' THEN
    RAISE EXCEPTION 'fin.bank_statement_line rows cannot be deleted';
  END IF;
  IF ROW(NEW.line_id, NEW.company_id, NEW.statement_id, NEW.bank_account_id, NEW.value_date, NEW.direction, NEW.amount, NEW.bank_reference,
         NEW.description, NEW.occurrence)
     IS DISTINCT FROM ROW(OLD.line_id, OLD.company_id, OLD.statement_id, OLD.bank_account_id, OLD.value_date, OLD.direction, OLD.amount,
         OLD.bank_reference, OLD.description, OLD.occurrence) THEN
    RAISE EXCEPTION 'fin.bank_statement_line: imported columns are immutable';
  END IF;
  IF NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'fin.bank_statement_line: version must increase by exactly 1';
  END IF;
  IF NOT ((OLD.status = 'UNMATCHED' AND NEW.status IN ('MATCHED', 'CHARGE_RECOGNIZED')) OR (OLD.status = 'MATCHED' AND NEW.status = 'UNMATCHED')) THEN
    RAISE EXCEPTION 'fin.bank_statement_line: transition % → % is not allowed (VS#2 §4)', OLD.status, NEW.status;
  END IF;
  IF NEW.status = 'MATCHED' AND (NEW.direction <> 'DEBIT' OR NOT EXISTS (
       SELECT 1 FROM fin.payment p WHERE p.payment_id = NEW.matched_payment_id AND p.bank_account_id = NEW.bank_account_id)) THEN
    RAISE EXCEPTION 'fin.bank_statement_line: only a DEBIT line of the payment''s bank account can be matched to it';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER bank_statement_line_guard BEFORE UPDATE OR DELETE ON fin.bank_statement_line
  FOR EACH ROW EXECUTE FUNCTION fin.bank_statement_line_guard();
CREATE TRIGGER bank_statement_line_no_truncate BEFORE TRUNCATE ON fin.bank_statement_line FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
CREATE CONSTRAINT TRIGGER bank_statement_line_evidence_on_change AFTER UPDATE ON fin.bank_statement_line
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('BankStatementLine', 'line_id');

-- ---------------------------------------------------------------------------------------------
-- Role TESORERO, permissions and segregation of duties (VS#2 §7, E-VS2-5, E-VS2-01-5). READ permissions take part in no SoD rule.
-- ---------------------------------------------------------------------------------------------
INSERT INTO iam.permission (permission_code, access) VALUES
  ('bank_account:manage', 'WRITE'), ('party_bank_account:request', 'WRITE'), ('party_bank_account:verify', 'WRITE'),
  ('payment:prepare', 'WRITE'), ('payment:void', 'WRITE'), ('payment:release', 'WRITE'), ('payment:reverse', 'WRITE'),
  ('bank_statement:import', 'WRITE'), ('bank_line:match', 'WRITE'), ('bank_line:unmatch', 'WRITE'), ('bank_charge:recognize', 'WRITE'),
  ('payment:read', 'READ'), ('bank:read', 'READ');

INSERT INTO iam.role (role_id, code, name) VALUES (gen_random_uuid(), 'TESORERO', 'Tesorero');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES
  ('TESORERO', 'party_bank_account:request'), ('TESORERO', 'payment:prepare'), ('TESORERO', 'payment:void'),
  ('TESORERO', 'bank_statement:import'), ('TESORERO', 'bank_line:match'),
  ('CONTROLLER', 'bank_account:manage'), ('CONTROLLER', 'party_bank_account:verify'), ('CONTROLLER', 'payment:release'),
  ('CONTROLLER', 'payment:reverse'), ('CONTROLLER', 'bank_line:unmatch'), ('CONTROLLER', 'bank_charge:recognize'),
  ('TESORERO', 'payment:read'), ('CUENTAS_POR_PAGAR', 'payment:read'), ('CONTROLLER', 'payment:read'), ('AUDITOR', 'payment:read'),
  ('TESORERO', 'bank:read'), ('CUENTAS_POR_PAGAR', 'bank:read'), ('CONTROLLER', 'bank:read'), ('AUDITOR', 'bank:read')
) AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;

INSERT INTO iam.sod_rule (permission_a, permission_b)
SELECT least(a, b), greatest(a, b) FROM (VALUES
  ('bank_account:manage', 'payment:prepare'),
  ('party_bank_account:request', 'party_bank_account:verify'),
  ('payment:prepare', 'payment:release'),
  ('payment:void', 'payment:release'),
  ('payment:reverse', 'payment:prepare'),
  ('payment:release', 'supplier:create'),
  ('payment:release', 'supplier_invoice:register'),
  ('payment:release', 'supplier_invoice:post')
) AS v (a, b);

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['fin.bank_account', 'md.party_bank_account', 'fin.payment', 'fin.ap_application', 'fin.bank_statement',
                           'fin.bank_statement_line'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON fin.bank_account, md.party_bank_account, fin.payment, fin.ap_application, fin.bank_statement,
  fin.bank_statement_line TO rochell_app;
GRANT UPDATE (status, version) ON fin.bank_account TO rochell_app;
GRANT UPDATE (status, verified_by, verified_at, verification_evidence, payable_from, rejected_by, rejected_at, rejection_reason)
  ON md.party_bank_account TO rochell_app;
GRANT UPDATE (bank_account_id, party_bank_account_id, amount, value_date, bank_reference, status, released_by, posting_event_id, version)
  ON fin.payment TO rochell_app;
GRANT UPDATE (status, matched_payment_id, charge_event_id, version) ON fin.bank_statement_line TO rochell_app;
