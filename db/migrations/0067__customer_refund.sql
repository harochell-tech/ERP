-- FIS1b-05 · Customer refund (approved errata E-FIS1b-8, E-FIS1b-01-9): the credit balance of a receipt — typically the ITBIS the
-- customer advanced on proformas that ended in an exempt e-CF 44 — is paid back. DEV-000001 per company: prepared by one person,
-- released by another (the money leaves the bank: P-36), matched with the bank statement like a payment.

-- ---------------------------------------------------------------------------------------------
-- The refund. PREPARED → RELEASED (posted) → CLEARED (its DEBIT line of the statement is matched; back to RELEASED on unmatch),
-- or PREPARED → VOIDED. It takes its amount out of the receipt's unapplied amount when released.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fin.customer_refund (
  refund_id         uuid          NOT NULL,
  company_id        uuid          NOT NULL,
  refund_no         text          NOT NULL,
  party_id          uuid          NOT NULL,
  receipt_id        uuid          NOT NULL,
  bank_account_id   uuid          NOT NULL,
  method            text          NOT NULL,
  amount            numeric(19,4) NOT NULL,
  reference         text,
  reason            text          NOT NULL,
  refund_date       date,
  status            text          NOT NULL,
  posting_event_id  uuid,
  void_reason       text,
  prepared_by       uuid          NOT NULL,
  released_by       uuid,
  version           bigint        NOT NULL,
  CONSTRAINT customer_refund_pk PRIMARY KEY (refund_id),
  CONSTRAINT customer_refund_company_uq UNIQUE (company_id, refund_id),
  CONSTRAINT customer_refund_no_uq UNIQUE (company_id, refund_no),
  CONSTRAINT customer_refund_party_fk FOREIGN KEY (company_id, party_id) REFERENCES md.party (company_id, party_id),
  CONSTRAINT customer_refund_receipt_fk FOREIGN KEY (company_id, receipt_id) REFERENCES fin.receipt (company_id, receipt_id),
  CONSTRAINT customer_refund_bank_account_fk FOREIGN KEY (company_id, bank_account_id) REFERENCES fin.bank_account (company_id, bank_account_id),
  CONSTRAINT customer_refund_posting_event_fk FOREIGN KEY (company_id, posting_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT customer_refund_prepared_by_fk FOREIGN KEY (prepared_by) REFERENCES iam.user (user_id),
  CONSTRAINT customer_refund_released_by_fk FOREIGN KEY (released_by) REFERENCES iam.user (user_id),
  CONSTRAINT customer_refund_no_format CHECK (refund_no ~ '^DEV-[0-9]{6,}$'),
  CONSTRAINT customer_refund_method CHECK (method IN ('TRANSFER', 'CHEQUE')),
  CONSTRAINT customer_refund_amount CHECK (amount > 0 AND amount = round(amount, 2)),
  CONSTRAINT customer_refund_reference CHECK (reference IS NULL OR length(btrim(reference)) BETWEEN 1 AND 100),
  CONSTRAINT customer_refund_reason CHECK (length(btrim(reason)) BETWEEN 1 AND 500),
  CONSTRAINT customer_refund_status CHECK (status IN ('PREPARED', 'RELEASED', 'CLEARED', 'VOIDED')),
  CONSTRAINT customer_refund_released CHECK ((status IN ('RELEASED', 'CLEARED')) = (released_by IS NOT NULL AND posting_event_id IS NOT NULL AND refund_date IS NOT NULL)),
  CONSTRAINT customer_refund_voided CHECK ((status = 'VOIDED') = coalesce(length(btrim(void_reason)) > 0, false)),
  CONSTRAINT customer_refund_version_positive CHECK (version >= 1)
);
CREATE INDEX customer_refund_receipt ON fin.customer_refund (company_id, receipt_id);

CREATE FUNCTION fin.customer_refund_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'PREPARED' OR NEW.version <> 1 THEN
      RAISE EXCEPTION 'fin.customer_refund: a refund is prepared PREPARED with version 1';
    END IF;
    IF (SELECT party_id FROM fin.receipt WHERE receipt_id = NEW.receipt_id) IS DISTINCT FROM NEW.party_id THEN
      RAISE EXCEPTION 'fin.customer_refund: the receipt belongs to another customer';
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'fin.customer_refund rows cannot be deleted; void the refund';
  END IF;
  IF ROW(NEW.refund_id, NEW.company_id, NEW.refund_no, NEW.party_id, NEW.receipt_id, NEW.bank_account_id, NEW.method, NEW.amount, NEW.reference, NEW.reason, NEW.prepared_by)
     IS DISTINCT FROM ROW(OLD.refund_id, OLD.company_id, OLD.refund_no, OLD.party_id, OLD.receipt_id, OLD.bank_account_id, OLD.method, OLD.amount, OLD.reference, OLD.reason,
                          OLD.prepared_by)
     OR NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'fin.customer_refund: what was prepared is immutable and the version increases by 1';
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
       (OLD.status = 'PREPARED' AND NEW.status IN ('RELEASED', 'VOIDED')) OR (OLD.status = 'RELEASED' AND NEW.status = 'CLEARED')
    OR (OLD.status = 'CLEARED' AND NEW.status = 'RELEASED')) THEN
    RAISE EXCEPTION 'fin.customer_refund: % → % is not allowed', OLD.status, NEW.status;
  END IF;
  IF OLD.status <> 'PREPARED' AND ROW(NEW.released_by, NEW.posting_event_id, NEW.refund_date) IS DISTINCT FROM ROW(OLD.released_by, OLD.posting_event_id, OLD.refund_date) THEN
    RAISE EXCEPTION 'fin.customer_refund: the release is set once';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER customer_refund_guard BEFORE INSERT OR UPDATE OR DELETE ON fin.customer_refund FOR EACH ROW EXECUTE FUNCTION fin.customer_refund_guard();
CREATE TRIGGER customer_refund_no_truncate BEFORE TRUNCATE ON fin.customer_refund FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- Two people (E-FIS1b-8): who releases is not who prepared, unless the releaser's four-eyes controls are waived (E-ADM-2).
CREATE TRIGGER customer_refund_four_eyes BEFORE INSERT OR UPDATE ON fin.customer_refund
  FOR EACH ROW EXECUTE FUNCTION core.four_eyes('released_by', 'prepared_by', 'customer_refund_four_eyes');

-- ADR-027 for the status; K-25: a released refund has the live journal of its event.
CREATE CONSTRAINT TRIGGER customer_refund_evidence_on_insert AFTER INSERT ON fin.customer_refund
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('CustomerRefund', 'refund_id');
CREATE CONSTRAINT TRIGGER customer_refund_evidence_on_change AFTER UPDATE ON fin.customer_refund
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('CustomerRefund', 'refund_id');

CREATE FUNCTION fin.customer_refund_coherent() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.status IN ('RELEASED', 'CLEARED') AND NOT EXISTS (
       SELECT 1 FROM fin.gl_journal j
       WHERE j.company_id = NEW.company_id AND j.source_event_id = NEW.posting_event_id AND j.journal_type = 'AUTO'
         AND NOT EXISTS (SELECT 1 FROM fin.gl_journal r WHERE r.reverses_journal_id = j.journal_id)) THEN
    RAISE EXCEPTION 'fin.customer_refund %: % without the journal of its event (K-25)', NEW.refund_id, NEW.status;
  END IF;
  IF (NEW.status = 'CLEARED') <> EXISTS (SELECT 1 FROM fin.bank_statement_line l WHERE l.matched_refund_id = NEW.refund_id) THEN
    RAISE EXCEPTION 'fin.customer_refund %: status % disagrees with its statement line', NEW.refund_id, NEW.status;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER customer_refund_coherent AFTER UPDATE ON fin.customer_refund
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.customer_refund_coherent();

-- ---------------------------------------------------------------------------------------------
-- E-FIS1b-01-9: a DEBIT line of the statement is matched to a released refund of its bank account and amount.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE fin.bank_statement_line
  ADD COLUMN matched_refund_id uuid,
  ADD CONSTRAINT bank_statement_line_refund_fk FOREIGN KEY (company_id, matched_refund_id) REFERENCES fin.customer_refund (company_id, refund_id),
  DROP CONSTRAINT bank_statement_line_matched,
  ADD CONSTRAINT bank_statement_line_matched CHECK ((status = 'MATCHED') = (num_nonnulls(matched_payment_id, matched_receipt_id, matched_deposit_id, matched_refund_id) = 1)
    AND (status = 'MATCHED' OR num_nonnulls(matched_payment_id, matched_receipt_id, matched_deposit_id, matched_refund_id) = 0));
CREATE UNIQUE INDEX bank_statement_line_matched_refund_uq ON fin.bank_statement_line (matched_refund_id) WHERE matched_refund_id IS NOT NULL;
GRANT UPDATE (matched_refund_id) ON fin.bank_statement_line TO rochell_app;

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
  IF NEW.status = 'MATCHED' AND NEW.matched_payment_id IS NOT NULL THEN
    IF NOT EXISTS (SELECT 1 FROM fin.payment p WHERE p.payment_id = NEW.matched_payment_id AND p.bank_account_id = NEW.bank_account_id) THEN
      RAISE EXCEPTION 'fin.bank_statement_line: a line is matched only to a payment of its bank account';
    END IF;
    IF NEW.direction = 'CREDIT' AND NOT EXISTS (
         SELECT 1 FROM fin.payment p
         JOIN fin.bank_statement_line d ON d.matched_payment_id = p.payment_id AND d.direction = 'DEBIT' AND d.status = 'MATCHED'
         WHERE p.payment_id = NEW.matched_payment_id AND p.status::text = 'REVERSED') THEN
      RAISE EXCEPTION 'fin.bank_statement_line: a CREDIT line is matched only as the return of a reversed payment whose DEBIT line is matched (E-VS2-05-10)';
    END IF;
  END IF;
  IF NEW.status = 'MATCHED' AND NEW.matched_receipt_id IS NOT NULL AND NOT EXISTS (
       SELECT 1 FROM fin.receipt r
       WHERE r.receipt_id = NEW.matched_receipt_id AND r.amount = NEW.amount
         AND ((NEW.direction = 'CREDIT' AND r.method = 'TRANSFER' AND r.bank_account_id = NEW.bank_account_id)
           OR (NEW.direction = 'DEBIT' AND r.status = 'BOUNCED'
               AND EXISTS (SELECT 1 FROM fin.receipt_deposit d WHERE d.deposit_id = r.deposit_id AND d.bank_account_id = NEW.bank_account_id)))) THEN
    RAISE EXCEPTION 'fin.bank_statement_line: a receipt is matched by a CREDIT line of its transfer or a DEBIT line of its bounce, same account and amount (E-VS3-07-10)';
  END IF;
  IF NEW.status = 'MATCHED' AND NEW.matched_deposit_id IS NOT NULL AND NOT EXISTS (
       SELECT 1 FROM fin.receipt_deposit d WHERE d.deposit_id = NEW.matched_deposit_id AND d.bank_account_id = NEW.bank_account_id AND d.total = NEW.amount) THEN
    RAISE EXCEPTION 'fin.bank_statement_line: a deposit is matched by a CREDIT line of its bank account and total (E-VS3-07-10)';
  END IF;
  IF NEW.status = 'MATCHED' AND NEW.matched_refund_id IS NOT NULL AND NOT EXISTS (
       SELECT 1 FROM fin.customer_refund f
       WHERE f.refund_id = NEW.matched_refund_id AND f.bank_account_id = NEW.bank_account_id AND f.amount = NEW.amount AND NEW.direction = 'DEBIT') THEN
    RAISE EXCEPTION 'fin.bank_statement_line: a customer refund is matched by a DEBIT line of its bank account and amount (E-FIS1b-01-9)';
  END IF;
  IF NEW.status = 'CHARGE_RECOGNIZED' AND NEW.direction <> 'DEBIT' THEN
    RAISE EXCEPTION 'fin.bank_statement_line: a bank charge is recognized on a DEBIT line only (E-VS2-05-7)';
  END IF;
  RETURN NEW;
END $$;

CREATE OR REPLACE FUNCTION fin.bank_statement_line_coherent() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.status = 'MATCHED' AND NEW.matched_payment_id IS NOT NULL AND NOT EXISTS (
       SELECT 1 FROM fin.payment p WHERE p.payment_id = NEW.matched_payment_id
         AND p.status::text IN (CASE WHEN NEW.direction = 'DEBIT' THEN 'CLEARED' ELSE 'REVERSED' END, 'REVERSED')) THEN
    RAISE EXCEPTION 'fin.bank_statement_line %: MATCHED to a payment that is not CLEARED (DEBIT) or REVERSED (CREDIT)', NEW.line_id;
  END IF;
  IF OLD.status = 'MATCHED' AND NEW.status = 'UNMATCHED' AND OLD.direction = 'DEBIT' AND OLD.matched_payment_id IS NOT NULL AND EXISTS (
       SELECT 1 FROM fin.payment p WHERE p.payment_id = OLD.matched_payment_id AND p.status::text = 'CLEARED') THEN
    RAISE EXCEPTION 'fin.bank_statement_line %: unmatched while its payment is still CLEARED', NEW.line_id;
  END IF;
  IF NEW.status = 'MATCHED' AND NEW.direction = 'CREDIT' AND NEW.matched_receipt_id IS NOT NULL
     AND NOT EXISTS (SELECT 1 FROM fin.receipt r WHERE r.receipt_id = NEW.matched_receipt_id AND r.bank_status = 'MATCHED') THEN
    RAISE EXCEPTION 'fin.bank_statement_line %: MATCHED to a transfer receipt that is not MATCHED', NEW.line_id;
  END IF;
  IF NEW.status = 'MATCHED' AND NEW.matched_deposit_id IS NOT NULL
     AND NOT EXISTS (SELECT 1 FROM fin.receipt_deposit d WHERE d.deposit_id = NEW.matched_deposit_id AND d.status = 'MATCHED') THEN
    RAISE EXCEPTION 'fin.bank_statement_line %: MATCHED to a deposit that is not MATCHED', NEW.line_id;
  END IF;
  IF NEW.status = 'MATCHED' AND NEW.matched_refund_id IS NOT NULL
     AND NOT EXISTS (SELECT 1 FROM fin.customer_refund f WHERE f.refund_id = NEW.matched_refund_id AND f.status = 'CLEARED') THEN
    RAISE EXCEPTION 'fin.bank_statement_line %: MATCHED to a customer refund that is not CLEARED', NEW.line_id;
  END IF;
  IF NEW.status = 'CHARGE_RECOGNIZED' AND NOT EXISTS (
       SELECT 1 FROM fin.gl_journal j
       WHERE j.company_id = NEW.company_id AND j.source_event_id = NEW.charge_event_id AND j.journal_type = 'AUTO'
         AND NOT EXISTS (SELECT 1 FROM fin.gl_journal r WHERE r.reverses_journal_id = j.journal_id)) THEN
    RAISE EXCEPTION 'fin.bank_statement_line %: CHARGE_RECOGNIZED without the journal of its event (K-25)', NEW.line_id;
  END IF;
  RETURN NULL;
END $$;

-- ---------------------------------------------------------------------------------------------
-- Posting rule P-36, DRAFT until the Controller approves it (A-01). Close component BANK-REC, also AR-REC (as P-24).
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type) VALUES
  ('0192f001-0000-7000-8000-000000000028', 'P-36', 'CustomerRefundReleased');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, also_requires_components, effective_from, status)
VALUES
  ('0192f001-0000-7000-8000-000000000028', 1,
   '{"lines": [
      {"code": "P36-DR-UNAP", "side": "DEBIT", "account_role": "UNAPPLIED_RECEIPTS", "amount": "refund_amount", "dimensions": ["party"], "subledger": "AR"},
      {"code": "P36-CR-BANK", "side": "CREDIT", "account_role": "BANK", "amount": "refund_amount", "dimensions": [], "subledger": "BANK"}
    ]}',
   '{"P36-DR-UNAP": "Devolución {refund_no} al cliente de su saldo a favor en el recibo {receipt_no}.",
     "P36-CR-BANK": "Sale del banco la devolución {refund_no} al cliente."}',
   'BANK-REC', ARRAY['AR-REC']::text[], DATE '2026-01-01', 'DRAFT');

-- ---------------------------------------------------------------------------------------------
-- Permissions and SoD (E-FIS1b-8): prepare ≠ release.
-- ---------------------------------------------------------------------------------------------
INSERT INTO iam.permission (permission_code, access) VALUES ('customer_refund:prepare', 'WRITE'), ('customer_refund:release', 'WRITE');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES ('COBROS', 'customer_refund:prepare'), ('CONTROLLER', 'customer_refund:release')) AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;

INSERT INTO iam.sod_rule (permission_a, permission_b)
SELECT least(a, b), greatest(a, b) FROM (VALUES ('customer_refund:prepare', 'customer_refund:release')) AS v (a, b);

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE fin.customer_refund ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON fin.customer_refund
  USING (company_id = nullif(current_setting('app.company_id', true), '')::uuid)
  WITH CHECK (company_id = nullif(current_setting('app.company_id', true), '')::uuid);
GRANT SELECT, INSERT ON fin.customer_refund TO rochell_app;
GRANT UPDATE (refund_date, status, posting_event_id, void_reason, released_by, version) ON fin.customer_refund TO rochell_app;
