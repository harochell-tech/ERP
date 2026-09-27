-- VS2-05 · Bank statements: import, match, bank charges (R-10). Approved errata E-VS2-05-1…9.

-- ---------------------------------------------------------------------------------------------
-- E-VS2-05-1: statement formats, global and versioned per bank code (declarative JSON read by the importer). Seeded by
-- migrations from the owner's samples; no UI in VS#2. The importer uses the highest version of the bank account's bank code.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fin.bank_statement_format (
  format_id    uuid        NOT NULL,
  bank_code    text        NOT NULL,
  version      integer     NOT NULL,
  definition   jsonb       NOT NULL,
  description  text        NOT NULL,
  created_at   timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT bank_statement_format_pk PRIMARY KEY (format_id),
  CONSTRAINT bank_statement_format_version_uq UNIQUE (bank_code, version),
  CONSTRAINT bank_statement_format_bank_code CHECK (bank_code = upper(btrim(bank_code)) AND char_length(bank_code) BETWEEN 2 AND 20),
  CONSTRAINT bank_statement_format_version_positive CHECK (version >= 1),
  CONSTRAINT bank_statement_format_definition_object CHECK (jsonb_typeof(definition) = 'object'),
  CONSTRAINT bank_statement_format_description_present CHECK (length(btrim(description)) > 0)
);
CREATE TRIGGER bank_statement_format_append_only BEFORE UPDATE OR DELETE ON fin.bank_statement_format FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER bank_statement_format_no_truncate BEFORE TRUNCATE ON fin.bank_statement_format FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- E-VS2-05-2: the imported file's bytes (≤ 5 MB), append-only, one per statement; its SHA-256 is the statement's file_sha256.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fin.bank_statement_file (
  statement_id     uuid  NOT NULL,
  company_id       uuid  NOT NULL,
  bank_account_id  uuid  NOT NULL,
  format_id        uuid  NOT NULL,
  file_name        text  NOT NULL,
  content          bytea NOT NULL,
  CONSTRAINT bank_statement_file_pk PRIMARY KEY (statement_id),
  CONSTRAINT bank_statement_file_statement_fk FOREIGN KEY (company_id, statement_id, bank_account_id)
    REFERENCES fin.bank_statement (company_id, statement_id, bank_account_id),
  CONSTRAINT bank_statement_file_format_fk FOREIGN KEY (format_id) REFERENCES fin.bank_statement_format (format_id),
  CONSTRAINT bank_statement_file_name_present CHECK (length(btrim(file_name)) BETWEEN 1 AND 255),
  CONSTRAINT bank_statement_file_size CHECK (octet_length(content) BETWEEN 1 AND 5242880)
);
CREATE TRIGGER bank_statement_file_append_only BEFORE UPDATE OR DELETE ON fin.bank_statement_file FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER bank_statement_file_no_truncate BEFORE TRUNCATE ON fin.bank_statement_file FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

CREATE FUNCTION fin.bank_statement_file_hash() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM fin.bank_statement s WHERE s.statement_id = NEW.statement_id AND s.file_sha256 = sha256(NEW.content)) THEN
    RAISE EXCEPTION 'fin.bank_statement_file: the content''s SHA-256 differs from its statement''s file_sha256';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER bank_statement_file_hash BEFORE INSERT ON fin.bank_statement_file
  FOR EACH ROW EXECUTE FUNCTION fin.bank_statement_file_hash();

-- Every statement keeps its file (checked at COMMIT: the statement row is written first).
CREATE FUNCTION fin.bank_statement_has_file() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM fin.bank_statement_file f WHERE f.statement_id = NEW.statement_id) THEN
    RAISE EXCEPTION 'fin.bank_statement %: imported without its file (E-VS2-05-2)', NEW.statement_id;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER bank_statement_has_file AFTER INSERT ON fin.bank_statement
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.bank_statement_has_file();

-- ---------------------------------------------------------------------------------------------
-- E-VS2-05-6: a payment is matched to one statement line at most.
-- ---------------------------------------------------------------------------------------------
DROP INDEX fin.bank_statement_line_payment;
CREATE UNIQUE INDEX bank_statement_line_matched_payment_uq ON fin.bank_statement_line (matched_payment_id) WHERE matched_payment_id IS NOT NULL;

-- ---------------------------------------------------------------------------------------------
-- Line transitions (VS#2 §4), now also: a charge is recognized on a DEBIT line only (E-VS2-05-7).
-- ---------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION fin.bank_statement_line_guard() RETURNS trigger
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
  IF NEW.status = 'CHARGE_RECOGNIZED' AND NEW.direction <> 'DEBIT' THEN
    RAISE EXCEPTION 'fin.bank_statement_line: a bank charge is recognized on a DEBIT line only (E-VS2-05-7)';
  END IF;
  RETURN NEW;
END $$;

-- ---------------------------------------------------------------------------------------------
-- Match coherence, checked at COMMIT (E-VS2-05-8, E-VS2-01-11, E-VS2-04-1): a CLEARED payment has its MATCHED line; a RELEASED
-- one has none; a MATCHED line's payment is CLEARED, or REVERSED after being cleared (its line stays matched).
-- ---------------------------------------------------------------------------------------------
CREATE FUNCTION fin.payment_cleared_line() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  matched integer;
BEGIN
  SELECT count(*) INTO matched FROM fin.bank_statement_line l WHERE l.matched_payment_id = NEW.payment_id;
  IF NEW.status::text = 'CLEARED' AND matched <> 1 THEN
    RAISE EXCEPTION 'fin.payment %: CLEARED without its matched statement line', NEW.payment_id;
  END IF;
  IF NEW.status::text = 'RELEASED' AND matched <> 0 THEN
    RAISE EXCEPTION 'fin.payment %: RELEASED while a statement line is matched to it', NEW.payment_id;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER payment_cleared_line AFTER UPDATE ON fin.payment
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.payment_cleared_line();

CREATE FUNCTION fin.bank_statement_line_coherent() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.status = 'MATCHED' AND NOT EXISTS (
       SELECT 1 FROM fin.payment p WHERE p.payment_id = NEW.matched_payment_id AND p.status::text IN ('CLEARED', 'REVERSED')) THEN
    RAISE EXCEPTION 'fin.bank_statement_line %: MATCHED to a payment that is not CLEARED', NEW.line_id;
  END IF;
  IF OLD.status = 'MATCHED' AND NEW.status = 'UNMATCHED' AND EXISTS (
       SELECT 1 FROM fin.payment p WHERE p.payment_id = OLD.matched_payment_id AND p.status::text = 'CLEARED') THEN
    RAISE EXCEPTION 'fin.bank_statement_line %: unmatched while its payment is still CLEARED', NEW.line_id;
  END IF;
  -- K-25 for charges: a recognized charge has the unreversed AUTO journal of its BankChargeRecognized event (no un-recognize in VS#2).
  IF NEW.status = 'CHARGE_RECOGNIZED' AND NOT EXISTS (
       SELECT 1 FROM fin.gl_journal j
       WHERE j.company_id = NEW.company_id AND j.source_event_id = NEW.charge_event_id AND j.journal_type = 'AUTO'
         AND NOT EXISTS (SELECT 1 FROM fin.gl_journal r WHERE r.reverses_journal_id = j.journal_id)) THEN
    RAISE EXCEPTION 'fin.bank_statement_line %: CHARGE_RECOGNIZED without the journal of its event (K-25)', NEW.line_id;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER bank_statement_line_coherent AFTER UPDATE ON fin.bank_statement_line
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.bank_statement_line_coherent();

-- ---------------------------------------------------------------------------------------------
-- E-VS2-05-7: R-10 (v2.1 P-28), DRAFT until the Controller approves it. The whole DEBIT line goes to BANK_CHARGES (the 0.15 %
-- DGII tax included in VS#2); the BANK line references the bank account, whose own GL account the engine uses (E-VS2-01-1).
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type)
VALUES ('0192f001-0000-7000-8000-000000000010', 'R-10', 'BankChargeRecognized');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, also_requires_components, effective_from, status)
VALUES ('0192f001-0000-7000-8000-000000000010', 1,
   '{"lines": [
      {"code": "R10-DR-CHG", "side": "DEBIT", "account_role": "BANK_CHARGES", "amount": "charge_amount", "dimensions": []},
      {"code": "R10-CR-BANK", "side": "CREDIT", "account_role": "BANK", "amount": "charge_amount", "dimensions": [], "subledger": "BANK"}
    ]}',
   '{"R10-DR-CHG": "Cargo bancario del {value_date} ({description}): gasto por el monto debitado por el banco.",
     "R10-CR-BANK": "Cargo bancario del {value_date} ({description}): sale del banco por el monto del extracto."}',
   'BANK-REC', ARRAY[]::text[], DATE '2026-01-01', 'DRAFT');

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges. Formats are global reference data: read-only for the application.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE fin.bank_statement_file ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON fin.bank_statement_file
  USING (company_id = nullif(current_setting('app.company_id', true), '')::uuid)
  WITH CHECK (company_id = nullif(current_setting('app.company_id', true), '')::uuid);
GRANT SELECT, INSERT ON fin.bank_statement_file TO rochell_app;
GRANT SELECT ON fin.bank_statement_format TO rochell_app;
