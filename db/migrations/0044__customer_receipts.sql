-- VS3-07 · Customer receipts: record, deposit, apply / unapply, customer withholding, bounced cheque, reversal and matching with the
-- bank statement. Frozen Baseline VS#3 §3–§8; approved errata E-VS3-7, E-VS3-11, E-VS3-07-1…15.

-- ---------------------------------------------------------------------------------------------
-- E-VS3-07-3: deposit slips DEP-000001 per company; a slip takes one or more cheque or cash receipts to one bank account (P-29).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fin.receipt_deposit (
  deposit_id        uuid          NOT NULL,
  company_id        uuid          NOT NULL,
  deposit_no        text          NOT NULL,
  bank_account_id   uuid          NOT NULL,
  deposit_date      date          NOT NULL,
  total             numeric(19,4) NOT NULL,
  status            text          NOT NULL,
  posting_event_id  uuid          NOT NULL,
  deposited_by      uuid          NOT NULL,
  version           bigint        NOT NULL,
  CONSTRAINT receipt_deposit_pk PRIMARY KEY (deposit_id),
  CONSTRAINT receipt_deposit_company_uq UNIQUE (company_id, deposit_id),
  CONSTRAINT receipt_deposit_no_uq UNIQUE (company_id, deposit_no),
  CONSTRAINT receipt_deposit_bank_account_fk FOREIGN KEY (company_id, bank_account_id) REFERENCES fin.bank_account (company_id, bank_account_id),
  CONSTRAINT receipt_deposit_event_fk FOREIGN KEY (company_id, posting_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT receipt_deposit_by_fk FOREIGN KEY (deposited_by) REFERENCES iam.user (user_id),
  CONSTRAINT receipt_deposit_no_format CHECK (deposit_no ~ '^DEP-[0-9]{6,}$'),
  CONSTRAINT receipt_deposit_total CHECK (total > 0 AND total = round(total, 2)),
  CONSTRAINT receipt_deposit_status CHECK (status IN ('DEPOSITED', 'MATCHED')),
  CONSTRAINT receipt_deposit_version_positive CHECK (version >= 1)
);

-- ---------------------------------------------------------------------------------------------
-- E-VS3-07-1/2/4: receipts REC-000001 per company, three status columns (E-VS3-07-4).
--   status              RECORDED → BOUNCED (deposited cheque) | REVERSED (nothing applied, not deposited or matched)
--   application_status  UNAPPLIED / PARTIALLY_APPLIED / APPLIED, from the unapplied amount
--   bank_status         IN_TRANSIT (cheque or cash not deposited) → DEPOSITED → MATCHED; a transfer is born DEPOSITED
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fin.receipt (
  receipt_id          uuid          NOT NULL,
  company_id          uuid          NOT NULL,
  receipt_no          text          NOT NULL,
  party_id            uuid          NOT NULL,
  method              text          NOT NULL,
  amount              numeric(19,4) NOT NULL,
  receipt_date        date          NOT NULL,
  value_date          date          NOT NULL,
  bank_account_id     uuid,
  reference           text,
  cheque_bank         text,
  cheque_no           text,
  cheque_date         date,
  status              text          NOT NULL,
  application_status  text          NOT NULL,
  bank_status         text          NOT NULL,
  unapplied_amount    numeric(19,4) NOT NULL,
  deposit_id          uuid,
  posting_event_id    uuid          NOT NULL,
  closing_event_id    uuid,
  closing_reason      text,
  recorded_by         uuid          NOT NULL,
  version             bigint        NOT NULL,
  CONSTRAINT receipt_pk PRIMARY KEY (receipt_id),
  CONSTRAINT receipt_company_uq UNIQUE (company_id, receipt_id),
  CONSTRAINT receipt_no_uq UNIQUE (company_id, receipt_no),
  CONSTRAINT receipt_party_fk FOREIGN KEY (company_id, party_id) REFERENCES md.party (company_id, party_id),
  CONSTRAINT receipt_bank_account_fk FOREIGN KEY (company_id, bank_account_id) REFERENCES fin.bank_account (company_id, bank_account_id),
  CONSTRAINT receipt_deposit_fk FOREIGN KEY (company_id, deposit_id) REFERENCES fin.receipt_deposit (company_id, deposit_id),
  CONSTRAINT receipt_posting_event_fk FOREIGN KEY (company_id, posting_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT receipt_closing_event_fk FOREIGN KEY (company_id, closing_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT receipt_recorded_by_fk FOREIGN KEY (recorded_by) REFERENCES iam.user (user_id),
  CONSTRAINT receipt_no_format CHECK (receipt_no ~ '^REC-[0-9]{6,}$'),
  CONSTRAINT receipt_method CHECK (method IN ('TRANSFER', 'CHEQUE', 'CASH')),
  CONSTRAINT receipt_amount CHECK (amount > 0 AND amount = round(amount, 2)),
  CONSTRAINT receipt_value_date CHECK (value_date <= receipt_date AND (method = 'TRANSFER' OR value_date = receipt_date)),
  CONSTRAINT receipt_transfer_account CHECK ((method = 'TRANSFER') = (bank_account_id IS NOT NULL)),
  CONSTRAINT receipt_cheque CHECK ((method = 'CHEQUE') = (cheque_bank IS NOT NULL AND cheque_no IS NOT NULL AND cheque_date IS NOT NULL)
    AND (cheque_date IS NULL OR cheque_date <= receipt_date)
    AND (cheque_bank IS NULL OR length(btrim(cheque_bank)) BETWEEN 1 AND 60) AND (cheque_no IS NULL OR length(btrim(cheque_no)) BETWEEN 1 AND 30)),
  CONSTRAINT receipt_reference CHECK (reference IS NULL OR length(btrim(reference)) BETWEEN 1 AND 100),
  CONSTRAINT receipt_status CHECK (status IN ('RECORDED', 'BOUNCED', 'REVERSED')),
  CONSTRAINT receipt_application_status CHECK (application_status IN ('UNAPPLIED', 'PARTIALLY_APPLIED', 'APPLIED')),
  CONSTRAINT receipt_bank_status CHECK (bank_status IN ('IN_TRANSIT', 'DEPOSITED', 'MATCHED')),
  CONSTRAINT receipt_unapplied CHECK (unapplied_amount >= 0 AND unapplied_amount <= amount
    AND (application_status = 'UNAPPLIED') = (unapplied_amount = amount) AND (application_status = 'APPLIED') = (unapplied_amount = 0)),
  CONSTRAINT receipt_bank CHECK ((method = 'TRANSFER' OR deposit_id IS NOT NULL) = (bank_status <> 'IN_TRANSIT') AND (method <> 'TRANSFER' OR deposit_id IS NULL)),
  CONSTRAINT receipt_closed CHECK ((status <> 'RECORDED') = (closing_event_id IS NOT NULL AND coalesce(length(btrim(closing_reason)) > 0, false))
    AND (status = 'RECORDED' OR application_status = 'UNAPPLIED')),
  CONSTRAINT receipt_bounced CHECK (status <> 'BOUNCED' OR (method = 'CHEQUE' AND deposit_id IS NOT NULL)),
  CONSTRAINT receipt_version_positive CHECK (version >= 1)
);
CREATE INDEX receipt_party ON fin.receipt (company_id, party_id);
CREATE INDEX receipt_deposit_slip ON fin.receipt (deposit_id) WHERE deposit_id IS NOT NULL;

CREATE FUNCTION fin.receipt_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'RECORDED' OR NEW.application_status <> 'UNAPPLIED' OR NEW.deposit_id IS NOT NULL OR NEW.version <> 1 THEN
      RAISE EXCEPTION 'fin.receipt: a receipt is recorded RECORDED, UNAPPLIED, not deposited, with version 1';
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'fin.receipt rows cannot be deleted; reverse the receipt';
  END IF;
  IF ROW(NEW.receipt_id, NEW.company_id, NEW.receipt_no, NEW.party_id, NEW.method, NEW.amount, NEW.receipt_date, NEW.value_date, NEW.bank_account_id,
         NEW.reference, NEW.cheque_bank, NEW.cheque_no, NEW.cheque_date, NEW.posting_event_id, NEW.recorded_by)
     IS DISTINCT FROM ROW(OLD.receipt_id, OLD.company_id, OLD.receipt_no, OLD.party_id, OLD.method, OLD.amount, OLD.receipt_date, OLD.value_date,
         OLD.bank_account_id, OLD.reference, OLD.cheque_bank, OLD.cheque_no, OLD.cheque_date, OLD.posting_event_id, OLD.recorded_by)
     OR NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'fin.receipt: what was received is immutable and the version increases by 1';
  END IF;
  IF (OLD.deposit_id IS NOT NULL AND NEW.deposit_id IS DISTINCT FROM OLD.deposit_id)
     OR (OLD.closing_event_id IS NOT NULL AND ROW(NEW.closing_event_id, NEW.closing_reason) IS DISTINCT FROM ROW(OLD.closing_event_id, OLD.closing_reason)) THEN
    RAISE EXCEPTION 'fin.receipt: the deposit and the bounce or reversal are set once';
  END IF;
  IF OLD.status <> NEW.status AND NOT (OLD.status = 'RECORDED' AND NEW.status IN ('BOUNCED', 'REVERSED')) THEN
    RAISE EXCEPTION 'fin.receipt: transition % → % is not allowed', OLD.status, NEW.status;
  END IF;
  IF OLD.status <> 'RECORDED' AND (NEW.unapplied_amount <> OLD.unapplied_amount OR (OLD.deposit_id IS NULL AND NEW.deposit_id IS NOT NULL)) THEN
    RAISE EXCEPTION 'fin.receipt: a % receipt is neither applied nor deposited', OLD.status;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER receipt_guard BEFORE INSERT OR UPDATE OR DELETE ON fin.receipt FOR EACH ROW EXECUTE FUNCTION fin.receipt_guard();
CREATE TRIGGER receipt_no_truncate BEFORE TRUNCATE ON fin.receipt FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ADR-027 for the life-cycle status.
CREATE CONSTRAINT TRIGGER receipt_evidence_on_insert AFTER INSERT ON fin.receipt
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('Receipt', 'receipt_id');
CREATE CONSTRAINT TRIGGER receipt_evidence_on_change AFTER UPDATE ON fin.receipt
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('Receipt', 'receipt_id');

-- K-25: RECORDED or BOUNCED ⇔ the unreversed AUTO journal of ReceiptRecorded (P-23); REVERSED ⇔ that journal reversed by the
-- closing event. A BOUNCED receipt also has the unreversed AUTO journal of its ReceiptBounced event (P-24).
CREATE FUNCTION fin.receipt_journal_evidence() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  live integer;
  reversed integer;
  bounced integer;
BEGIN
  SELECT count(*) FILTER (WHERE NOT EXISTS (SELECT 1 FROM fin.gl_journal r WHERE r.reverses_journal_id = j.journal_id)),
         count(*) FILTER (WHERE EXISTS (SELECT 1 FROM fin.gl_journal r WHERE r.reverses_journal_id = j.journal_id AND r.source_event_id = NEW.closing_event_id))
    INTO live, reversed
    FROM fin.gl_journal j WHERE j.company_id = NEW.company_id AND j.source_event_id = NEW.posting_event_id AND j.journal_type = 'AUTO';
  SELECT count(*) INTO bounced FROM fin.gl_journal j
    WHERE j.company_id = NEW.company_id AND j.source_event_id = NEW.closing_event_id AND j.journal_type = 'AUTO'
      AND NOT EXISTS (SELECT 1 FROM fin.gl_journal r WHERE r.reverses_journal_id = j.journal_id);
  IF (NEW.status = 'RECORDED' AND live <> 1) OR (NEW.status = 'BOUNCED' AND (live <> 1 OR bounced <> 1))
     OR (NEW.status = 'REVERSED' AND (live <> 0 OR reversed <> 1)) THEN
    RAISE EXCEPTION 'fin.receipt %: % without its journals (K-25): live %, reversed %, bounce %', NEW.receipt_id, NEW.status, live, reversed, bounced;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER receipt_journal_on_insert AFTER INSERT ON fin.receipt
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.receipt_journal_evidence();
CREATE CONSTRAINT TRIGGER receipt_journal_on_change AFTER UPDATE ON fin.receipt
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.receipt_journal_evidence();

CREATE FUNCTION fin.receipt_deposit_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'DEPOSITED' OR NEW.version <> 1 THEN
      RAISE EXCEPTION 'fin.receipt_deposit: a deposit is created DEPOSITED with version 1';
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'fin.receipt_deposit rows cannot be deleted';
  END IF;
  IF ROW(NEW.deposit_id, NEW.company_id, NEW.deposit_no, NEW.bank_account_id, NEW.deposit_date, NEW.total, NEW.posting_event_id, NEW.deposited_by)
     IS DISTINCT FROM ROW(OLD.deposit_id, OLD.company_id, OLD.deposit_no, OLD.bank_account_id, OLD.deposit_date, OLD.total, OLD.posting_event_id, OLD.deposited_by)
     OR NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'fin.receipt_deposit: only the status changes, with version + 1';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER receipt_deposit_guard BEFORE INSERT OR UPDATE OR DELETE ON fin.receipt_deposit FOR EACH ROW EXECUTE FUNCTION fin.receipt_deposit_guard();
CREATE TRIGGER receipt_deposit_no_truncate BEFORE TRUNCATE ON fin.receipt_deposit FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- A deposit's total is the sum of its receipts, all of its bank account's slip; P-29 is its unreversed AUTO journal (K-25).
CREATE FUNCTION fin.receipt_deposit_coherent() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.total <> (SELECT coalesce(sum(r.amount), 0) FROM fin.receipt r WHERE r.deposit_id = NEW.deposit_id) THEN
    RAISE EXCEPTION 'fin.receipt_deposit %: its total is not the sum of its receipts', NEW.deposit_id;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM fin.gl_journal j WHERE j.company_id = NEW.company_id AND j.source_event_id = NEW.posting_event_id AND j.journal_type = 'AUTO'
                   AND NOT EXISTS (SELECT 1 FROM fin.gl_journal r WHERE r.reverses_journal_id = j.journal_id)) THEN
    RAISE EXCEPTION 'fin.receipt_deposit %: without the journal of its posting event (K-25)', NEW.deposit_id;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER receipt_deposit_coherent AFTER INSERT ON fin.receipt_deposit
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.receipt_deposit_coherent();

-- ---------------------------------------------------------------------------------------------
-- E-VS3-07-5/6: applications of a receipt to AR documents of its customer; an unapply adds the mirror row (as fin.ap_application).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fin.ar_application (
  application_id           uuid          NOT NULL,
  company_id               uuid          NOT NULL,
  receipt_id               uuid          NOT NULL,
  ar_doc_id                uuid          NOT NULL,
  amount                   numeric(19,4) NOT NULL,
  event_id                 uuid          NOT NULL,
  reverses_application_id  uuid,
  CONSTRAINT ar_application_pk PRIMARY KEY (application_id),
  CONSTRAINT ar_application_company_uq UNIQUE (company_id, application_id),
  CONSTRAINT ar_application_reverses_uq UNIQUE (reverses_application_id),
  CONSTRAINT ar_application_receipt_fk FOREIGN KEY (company_id, receipt_id) REFERENCES fin.receipt (company_id, receipt_id),
  CONSTRAINT ar_application_ar_doc_fk FOREIGN KEY (company_id, ar_doc_id) REFERENCES fin.ar_document (company_id, ar_doc_id),
  CONSTRAINT ar_application_event_fk FOREIGN KEY (company_id, event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT ar_application_reverses_fk FOREIGN KEY (company_id, reverses_application_id) REFERENCES fin.ar_application (company_id, application_id),
  CONSTRAINT ar_application_amount CHECK (amount > 0 AND amount = round(amount, 2)),
  CONSTRAINT ar_application_not_self CHECK (reverses_application_id IS DISTINCT FROM application_id)
);
CREATE INDEX ar_application_ar_doc ON fin.ar_application (company_id, ar_doc_id);
CREATE INDEX ar_application_receipt ON fin.ar_application (company_id, receipt_id);

CREATE FUNCTION fin.ar_application_before_insert() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  original fin.ar_application;
BEGIN
  IF (SELECT party_id FROM fin.ar_document WHERE ar_doc_id = NEW.ar_doc_id) IS DISTINCT FROM (SELECT party_id FROM fin.receipt WHERE receipt_id = NEW.receipt_id) THEN
    RAISE EXCEPTION 'fin.ar_application: AR document % does not belong to the receipt''s customer (E-VS3-07-5)', NEW.ar_doc_id;
  END IF;
  IF NEW.reverses_application_id IS NOT NULL THEN
    SELECT * INTO original FROM fin.ar_application WHERE application_id = NEW.reverses_application_id;
    IF original.reverses_application_id IS NOT NULL
       OR ROW(original.receipt_id, original.ar_doc_id, original.amount) IS DISTINCT FROM ROW(NEW.receipt_id, NEW.ar_doc_id, NEW.amount) THEN
      RAISE EXCEPTION 'fin.ar_application: an unapply mirrors one original application (same receipt, document and amount)';
    END IF;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER ar_application_before_insert BEFORE INSERT ON fin.ar_application FOR EACH ROW EXECUTE FUNCTION fin.ar_application_before_insert();
CREATE TRIGGER ar_application_append_only BEFORE UPDATE OR DELETE ON fin.ar_application FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER ar_application_no_truncate BEFORE TRUNCATE ON fin.ar_application FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- E-VS3-07-7: withholdings made by the customer, with their certificate (P-27); reversed by the Controller.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fin.customer_withholding (
  withholding_id     uuid          NOT NULL,
  company_id         uuid          NOT NULL,
  party_id           uuid          NOT NULL,
  invoice_id         uuid          NOT NULL,
  ar_doc_id          uuid          NOT NULL,
  kind               text          NOT NULL,
  amount             numeric(19,4) NOT NULL,
  withholding_date   date          NOT NULL,
  certificate_no     text          NOT NULL,
  evidence_ref       text          NOT NULL,
  evidence_sha256    bytea         NOT NULL,
  status             text          NOT NULL,
  posting_event_id   uuid          NOT NULL,
  reversal_event_id  uuid,
  reversal_reason    text,
  recorded_by        uuid          NOT NULL,
  version            bigint        NOT NULL,
  CONSTRAINT customer_withholding_pk PRIMARY KEY (withholding_id),
  CONSTRAINT customer_withholding_company_uq UNIQUE (company_id, withholding_id),
  CONSTRAINT customer_withholding_certificate_uq UNIQUE (company_id, party_id, kind, certificate_no),
  CONSTRAINT customer_withholding_party_fk FOREIGN KEY (company_id, party_id) REFERENCES md.party (company_id, party_id),
  CONSTRAINT customer_withholding_invoice_fk FOREIGN KEY (company_id, invoice_id) REFERENCES sal.invoice (company_id, invoice_id),
  CONSTRAINT customer_withholding_ar_doc_fk FOREIGN KEY (company_id, ar_doc_id) REFERENCES fin.ar_document (company_id, ar_doc_id),
  CONSTRAINT customer_withholding_event_fk FOREIGN KEY (company_id, posting_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT customer_withholding_reversal_fk FOREIGN KEY (company_id, reversal_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT customer_withholding_by_fk FOREIGN KEY (recorded_by) REFERENCES iam.user (user_id),
  CONSTRAINT customer_withholding_kind CHECK (kind IN ('ITBIS', 'ISR')),
  CONSTRAINT customer_withholding_amount CHECK (amount > 0 AND amount = round(amount, 2)),
  CONSTRAINT customer_withholding_texts CHECK (length(btrim(certificate_no)) BETWEEN 1 AND 60 AND length(btrim(evidence_ref)) BETWEEN 1 AND 200
    AND octet_length(evidence_sha256) = 32),
  CONSTRAINT customer_withholding_status CHECK (status IN ('ACTIVE', 'REVERSED')),
  CONSTRAINT customer_withholding_reversed CHECK ((status = 'REVERSED') = (reversal_event_id IS NOT NULL AND coalesce(length(btrim(reversal_reason)) > 0, false))),
  CONSTRAINT customer_withholding_version_positive CHECK (version >= 1)
);
CREATE INDEX customer_withholding_ar_doc ON fin.customer_withholding (company_id, ar_doc_id);

CREATE FUNCTION fin.customer_withholding_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'ACTIVE' OR NEW.version <> 1 THEN
      RAISE EXCEPTION 'fin.customer_withholding: a withholding is recorded ACTIVE with version 1';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM sal.invoice i WHERE i.invoice_id = NEW.invoice_id AND i.ar_doc_id = NEW.ar_doc_id AND i.party_id = NEW.party_id) THEN
      RAISE EXCEPTION 'fin.customer_withholding: the AR document and customer are the invoice''s';
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'fin.customer_withholding rows cannot be deleted; reverse the withholding';
  END IF;
  IF ROW(NEW.withholding_id, NEW.company_id, NEW.party_id, NEW.invoice_id, NEW.ar_doc_id, NEW.kind, NEW.amount, NEW.withholding_date, NEW.certificate_no,
         NEW.evidence_ref, NEW.evidence_sha256, NEW.posting_event_id, NEW.recorded_by)
     IS DISTINCT FROM ROW(OLD.withholding_id, OLD.company_id, OLD.party_id, OLD.invoice_id, OLD.ar_doc_id, OLD.kind, OLD.amount, OLD.withholding_date,
         OLD.certificate_no, OLD.evidence_ref, OLD.evidence_sha256, OLD.posting_event_id, OLD.recorded_by)
     OR NEW.version <> OLD.version + 1 OR OLD.status <> 'ACTIVE' THEN
    RAISE EXCEPTION 'fin.customer_withholding: only an ACTIVE withholding is reversed, once, with version + 1';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER customer_withholding_guard BEFORE INSERT OR UPDATE OR DELETE ON fin.customer_withholding FOR EACH ROW EXECUTE FUNCTION fin.customer_withholding_guard();
CREATE TRIGGER customer_withholding_no_truncate BEFORE TRUNCATE ON fin.customer_withholding FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

CREATE CONSTRAINT TRIGGER customer_withholding_evidence_on_insert AFTER INSERT ON fin.customer_withholding
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('CustomerWithholding', 'withholding_id');
CREATE CONSTRAINT TRIGGER customer_withholding_evidence_on_change AFTER UPDATE ON fin.customer_withholding
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('CustomerWithholding', 'withholding_id');

-- K-25: ACTIVE ⇔ the unreversed AUTO journal of its WithholdingByCustomer event; REVERSED ⇔ reversed by the reversal event.
CREATE FUNCTION fin.customer_withholding_journal_evidence() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  live integer;
  reversed integer;
BEGIN
  SELECT count(*) FILTER (WHERE NOT EXISTS (SELECT 1 FROM fin.gl_journal r WHERE r.reverses_journal_id = j.journal_id)),
         count(*) FILTER (WHERE EXISTS (SELECT 1 FROM fin.gl_journal r WHERE r.reverses_journal_id = j.journal_id AND r.source_event_id = NEW.reversal_event_id))
    INTO live, reversed
    FROM fin.gl_journal j WHERE j.company_id = NEW.company_id AND j.source_event_id = NEW.posting_event_id AND j.journal_type = 'AUTO';
  IF (NEW.status = 'ACTIVE' AND live <> 1) OR (NEW.status = 'REVERSED' AND (live <> 0 OR reversed <> 1)) THEN
    RAISE EXCEPTION 'fin.customer_withholding %: % without its journal (K-25)', NEW.withholding_id, NEW.status;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER customer_withholding_journal_on_insert AFTER INSERT ON fin.customer_withholding
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.customer_withholding_journal_evidence();
CREATE CONSTRAINT TRIGGER customer_withholding_journal_on_change AFTER UPDATE ON fin.customer_withholding
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.customer_withholding_journal_evidence();

-- ---------------------------------------------------------------------------------------------
-- E-VS3-07-10: a statement line is matched to a payment (VS#2), a receipt or a deposit.
--   CREDIT ↔ a TRANSFER receipt (money in) or a deposit slip; DEBIT ↔ a BOUNCED cheque (the bank takes it back).
-- ---------------------------------------------------------------------------------------------
ALTER TABLE fin.bank_statement_line
  ADD COLUMN matched_receipt_id uuid,
  ADD COLUMN matched_deposit_id uuid,
  ADD CONSTRAINT bank_statement_line_receipt_fk FOREIGN KEY (company_id, matched_receipt_id) REFERENCES fin.receipt (company_id, receipt_id),
  ADD CONSTRAINT bank_statement_line_deposit_fk FOREIGN KEY (company_id, matched_deposit_id) REFERENCES fin.receipt_deposit (company_id, deposit_id),
  DROP CONSTRAINT bank_statement_line_matched,
  ADD CONSTRAINT bank_statement_line_matched CHECK ((status = 'MATCHED') = (num_nonnulls(matched_payment_id, matched_receipt_id, matched_deposit_id) = 1)
    AND (status = 'MATCHED' OR num_nonnulls(matched_payment_id, matched_receipt_id, matched_deposit_id) = 0)),
  ADD CONSTRAINT bank_statement_line_deposit_credit CHECK (matched_deposit_id IS NULL OR direction = 'CREDIT');
CREATE UNIQUE INDEX bank_statement_line_matched_receipt_uq ON fin.bank_statement_line (matched_receipt_id, direction) WHERE matched_receipt_id IS NOT NULL;
CREATE UNIQUE INDEX bank_statement_line_matched_deposit_uq ON fin.bank_statement_line (matched_deposit_id) WHERE matched_deposit_id IS NOT NULL;

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
  IF NEW.status = 'CHARGE_RECOGNIZED' AND NEW.direction <> 'DEBIT' THEN
    RAISE EXCEPTION 'fin.bank_statement_line: a bank charge is recognized on a DEBIT line only (E-VS2-05-7)';
  END IF;
  RETURN NEW;
END $$;

-- Match coherence at COMMIT: the VS#2 rules for payments; a transfer receipt or deposit matched ⇔ bank_status / status MATCHED.
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
  IF NEW.status = 'CHARGE_RECOGNIZED' AND NOT EXISTS (
       SELECT 1 FROM fin.gl_journal j
       WHERE j.company_id = NEW.company_id AND j.source_event_id = NEW.charge_event_id AND j.journal_type = 'AUTO'
         AND NOT EXISTS (SELECT 1 FROM fin.gl_journal r WHERE r.reverses_journal_id = j.journal_id)) THEN
    RAISE EXCEPTION 'fin.bank_statement_line %: CHARGE_RECOGNIZED without the journal of its event (K-25)', NEW.line_id;
  END IF;
  RETURN NULL;
END $$;

-- The other side: a MATCHED transfer receipt or deposit has its CREDIT line; a deposited cheque's bank status follows its slip.
CREATE FUNCTION fin.receipt_bank_coherent() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.method = 'TRANSFER' AND (NEW.bank_status = 'MATCHED') <> EXISTS (
       SELECT 1 FROM fin.bank_statement_line l WHERE l.matched_receipt_id = NEW.receipt_id AND l.direction = 'CREDIT') THEN
    RAISE EXCEPTION 'fin.receipt %: bank status % disagrees with its statement line', NEW.receipt_id, NEW.bank_status;
  END IF;
  IF NEW.deposit_id IS NOT NULL AND NEW.bank_status <> (SELECT d.status FROM fin.receipt_deposit d WHERE d.deposit_id = NEW.deposit_id) THEN
    RAISE EXCEPTION 'fin.receipt %: bank status % disagrees with its deposit', NEW.receipt_id, NEW.bank_status;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER receipt_bank_coherent AFTER UPDATE ON fin.receipt
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.bank_status IS DISTINCT FROM NEW.bank_status OR OLD.deposit_id IS DISTINCT FROM NEW.deposit_id)
  EXECUTE FUNCTION fin.receipt_bank_coherent();

CREATE FUNCTION fin.receipt_deposit_matched() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF (NEW.status = 'MATCHED') <> EXISTS (SELECT 1 FROM fin.bank_statement_line l WHERE l.matched_deposit_id = NEW.deposit_id) THEN
    RAISE EXCEPTION 'fin.receipt_deposit %: status % disagrees with its statement line', NEW.deposit_id, NEW.status;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER receipt_deposit_matched AFTER UPDATE ON fin.receipt_deposit
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.receipt_deposit_matched();

-- ---------------------------------------------------------------------------------------------
-- Posting rules (VS#3 §6, E-VS3-07-2/3/5/7/8/12), DRAFT until the Controller approves them (A-01).
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type) VALUES
  ('0192f001-0000-7000-8000-000000000018', 'P-23', 'ReceiptRecorded'),
  ('0192f001-0000-7000-8000-000000000019', 'P-24', 'ReceiptBounced'),
  ('0192f001-0000-7000-8000-000000000020', 'P-25', 'ReceiptApplied'),
  ('0192f001-0000-7000-8000-000000000021', 'P-27', 'WithholdingByCustomer'),
  ('0192f001-0000-7000-8000-000000000022', 'P-29', 'CashInTransitDeposited');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, also_requires_components, effective_from, status)
VALUES
  ('0192f001-0000-7000-8000-000000000018', 1,
   '{"lines": [
      {"code": "P23-DR-BANK", "side": "DEBIT", "account_role": "BANK", "amount": "receipt_amount", "dimensions": [], "subledger": "BANK"},
      {"code": "P23-DR-CIT", "side": "DEBIT", "account_role": "CASH_IN_TRANSIT", "amount": "receipt_amount", "dimensions": ["party"], "subledger": "AR"},
      {"code": "P23-CR-UNAP", "side": "CREDIT", "account_role": "UNAPPLIED_RECEIPTS", "amount": "receipt_amount", "dimensions": ["party"], "subledger": "AR"}
    ]}',
   '{"P23-DR-BANK": "Transferencia del cliente recibida en el banco: recibo {receipt_no}.",
     "P23-DR-CIT": "Cheque o efectivo del recibo {receipt_no} en tránsito hasta depositarlo.",
     "P23-CR-UNAP": "Cobro {receipt_no} pendiente de aplicar a facturas del cliente."}',
   'BANK-REC', ARRAY['AR-REC']::text[], DATE '2026-01-01', 'DRAFT'),
  ('0192f001-0000-7000-8000-000000000019', 1,
   '{"lines": [
      {"code": "P24-DR-UNAP", "side": "DEBIT", "account_role": "UNAPPLIED_RECEIPTS", "amount": "receipt_amount", "dimensions": ["party"], "subledger": "AR"},
      {"code": "P24-CR-BANK", "side": "CREDIT", "account_role": "BANK", "amount": "receipt_amount", "dimensions": [], "subledger": "BANK"}
    ]}',
   '{"P24-DR-UNAP": "El cheque del recibo {receipt_no} fue devuelto: el cobro deja de existir.",
     "P24-CR-BANK": "El banco debita el cheque devuelto del recibo {receipt_no} (depósito {deposit_no})."}',
   'BANK-REC', ARRAY['AR-REC']::text[], DATE '2026-01-01', 'DRAFT'),
  ('0192f001-0000-7000-8000-000000000020', 1,
   '{"lines": [
      {"code": "P25-DR-UNAP", "side": "DEBIT", "account_role": "UNAPPLIED_RECEIPTS", "amount": "applied_amount", "dimensions": ["party"], "subledger": "AR"},
      {"code": "P25-CR-AR", "side": "CREDIT", "account_role": "AR_CONTROL", "amount": "applied_amount", "dimensions": ["party"], "subledger": "AR"}
    ]}',
   '{"P25-DR-UNAP": "Se aplica el cobro {receipt_no} a la factura {invoice_no}.",
     "P25-CR-AR": "La factura {invoice_no} baja por el cobro {receipt_no}."}',
   'AR-REC', ARRAY[]::text[], DATE '2026-01-01', 'DRAFT'),
  ('0192f001-0000-7000-8000-000000000021', 1,
   '{"lines": [
      {"code": "P27-DR-WH", "side": "DEBIT", "account_role": "WITHHOLDING_RECEIVABLE", "amount": "withheld_amount", "dimensions": ["party"]},
      {"code": "P27-CR-AR", "side": "CREDIT", "account_role": "AR_CONTROL", "amount": "withheld_amount", "dimensions": ["party"], "subledger": "AR"}
    ]}',
   '{"P27-DR-WH": "Retención de {kind} hecha por el cliente, certificado {certificate_no}.",
     "P27-CR-AR": "La factura {invoice_no} baja por la retención de {kind} (certificado {certificate_no})."}',
   'AR-REC', ARRAY[]::text[], DATE '2026-01-01', 'DRAFT'),
  ('0192f001-0000-7000-8000-000000000022', 1,
   '{"lines": [
      {"code": "P29-DR-BANK", "side": "DEBIT", "account_role": "BANK", "amount": "deposit_total", "dimensions": [], "subledger": "BANK"},
      {"code": "P29-CR-CIT", "side": "CREDIT", "account_role": "CASH_IN_TRANSIT", "amount": "receipt_amount", "dimensions": ["party"], "subledger": "AR"}
    ]}',
   '{"P29-DR-BANK": "Depósito {deposit_no} de cheques y efectivo en el banco.",
     "P29-CR-CIT": "El recibo {receipt_no} sale de tránsito con el depósito {deposit_no}."}',
   'BANK-REC', ARRAY['AR-REC']::text[], DATE '2026-01-01', 'DRAFT');

-- ---------------------------------------------------------------------------------------------
-- Permissions and SoD (E-VS3-07-13).
-- ---------------------------------------------------------------------------------------------
INSERT INTO iam.permission (permission_code, access) VALUES
  ('receipt:record', 'WRITE'), ('receipt:apply', 'WRITE'), ('receipt:deposit', 'WRITE'), ('customer_withholding:record', 'WRITE'),
  ('receipt:bounce', 'WRITE'), ('receipt:reverse', 'WRITE'), ('customer_withholding:reverse', 'WRITE');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES ('COBROS', 'receipt:record'), ('COBROS', 'receipt:apply'), ('COBROS', 'receipt:deposit'), ('COBROS', 'customer_withholding:record'),
             ('TESORERO', 'receipt:bounce'), ('CONTROLLER', 'receipt:reverse'), ('CONTROLLER', 'customer_withholding:reverse')) AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;

INSERT INTO iam.sod_rule (permission_a, permission_b)
SELECT least(a, b), greatest(a, b) FROM (VALUES ('invoice:issue', 'receipt:record'), ('bank_line:match', 'receipt:record')) AS v (a, b);

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['fin.receipt_deposit', 'fin.receipt', 'fin.ar_application', 'fin.customer_withholding'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON fin.receipt_deposit, fin.receipt, fin.ar_application, fin.customer_withholding TO rochell_app;
GRANT UPDATE (status, version) ON fin.receipt_deposit TO rochell_app;
GRANT UPDATE (status, application_status, bank_status, unapplied_amount, deposit_id, closing_event_id, closing_reason, version) ON fin.receipt TO rochell_app;
GRANT UPDATE (status, reversal_event_id, reversal_reason, version) ON fin.customer_withholding TO rochell_app;
GRANT UPDATE (matched_receipt_id, matched_deposit_id) ON fin.bank_statement_line TO rochell_app;
