-- USD1-05b · Transfers between own accounts and USD statements (approved errata E-USD1-05b-1…5).
--   - a transfer TRF-YYYY-NNNNNN moves money between two of the company's accounts — buying or selling USD at the bank's rate, or between
--     two accounts of the same currency; prepared by Tesorería, released by someone else, voided while PREPARED, reversed by the Controller
--     (E-USD1-05b-1);
--   - P-42: the destination bank debited and the origin credited with the same pesos; a USD account's line keeps its USD (E-USD1-05b-2);
--   - a statement line of either account is matched to the transfer by that account's amount (E-USD1-05b-3).

CREATE TABLE fin.bank_transfer (
  transfer_id           uuid          NOT NULL,
  company_id            uuid          NOT NULL,
  transfer_no           text          NOT NULL,
  from_bank_account_id  uuid          NOT NULL,
  to_bank_account_id    uuid          NOT NULL,
  value_date            date          NOT NULL,
  from_amount           numeric(19,4) NOT NULL,
  to_amount             numeric(19,4) NOT NULL,
  exchange_rate         numeric(18,4),
  amount_dop            numeric(19,4) NOT NULL,
  bank_reference        text,
  status                text          NOT NULL,
  prepared_by           uuid          NOT NULL,
  released_by           uuid,
  posting_event_id      uuid,
  version               bigint        NOT NULL,
  CONSTRAINT bank_transfer_pk PRIMARY KEY (transfer_id),
  CONSTRAINT bank_transfer_company_uq UNIQUE (company_id, transfer_id),
  CONSTRAINT bank_transfer_no_uq UNIQUE (company_id, transfer_no),
  CONSTRAINT bank_transfer_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT bank_transfer_from_fk FOREIGN KEY (from_bank_account_id) REFERENCES fin.bank_account (bank_account_id),
  CONSTRAINT bank_transfer_to_fk FOREIGN KEY (to_bank_account_id) REFERENCES fin.bank_account (bank_account_id),
  CONSTRAINT bank_transfer_event_fk FOREIGN KEY (company_id, posting_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT bank_transfer_prepared_fk FOREIGN KEY (prepared_by) REFERENCES iam.user (user_id),
  CONSTRAINT bank_transfer_released_fk FOREIGN KEY (released_by) REFERENCES iam.user (user_id),
  CONSTRAINT bank_transfer_no_format CHECK (transfer_no ~ '^TRF-[0-9]{4}-[0-9]{6}$'),
  CONSTRAINT bank_transfer_accounts CHECK (from_bank_account_id <> to_bank_account_id),
  CONSTRAINT bank_transfer_amounts CHECK (from_amount > 0 AND to_amount > 0 AND amount_dop > 0 AND from_amount = round(from_amount, 2)
    AND to_amount = round(to_amount, 2) AND amount_dop = round(amount_dop, 2) AND (exchange_rate IS NULL OR exchange_rate > 0)),
  CONSTRAINT bank_transfer_reference CHECK (bank_reference IS NULL OR length(btrim(bank_reference)) BETWEEN 1 AND 80),
  CONSTRAINT bank_transfer_status CHECK (status IN ('PREPARED', 'RELEASED', 'VOIDED', 'REVERSED')),
  CONSTRAINT bank_transfer_released CHECK ((status IN ('RELEASED', 'REVERSED')) = (released_by IS NOT NULL AND posting_event_id IS NOT NULL)),
  CONSTRAINT bank_transfer_version_positive CHECK (version >= 1)
);

-- The currencies of the two accounts decide the amounts (E-USD1-05b-1/2): pesos ↔ USD at the bank's rate; between two accounts of one
-- currency the same amount, and between USD accounts the pesos at the day's approved rate.
CREATE FUNCTION fin.bank_transfer_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  from_currency char(3);
  to_currency char(3);
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'fin.bank_transfer rows cannot be deleted; void or reverse the transfer';
  END IF;
  SELECT currency INTO from_currency FROM fin.bank_account WHERE bank_account_id = NEW.from_bank_account_id AND company_id = NEW.company_id;
  SELECT currency INTO to_currency FROM fin.bank_account WHERE bank_account_id = NEW.to_bank_account_id AND company_id = NEW.company_id;
  IF from_currency IS NULL OR to_currency IS NULL THEN
    RAISE EXCEPTION 'fin.bank_transfer: both accounts are the company''s';
  END IF;
  IF fin.bank_transfer_amounts_ok(NEW, from_currency, to_currency) IS NOT TRUE THEN
    RAISE EXCEPTION 'fin.bank_transfer: amounts do not agree with the currencies (% → %)', from_currency, to_currency;
  END IF;
  IF TG_OP = 'UPDATE' AND ROW(NEW.transfer_id, NEW.company_id, NEW.transfer_no, NEW.prepared_by) IS DISTINCT FROM ROW(OLD.transfer_id, OLD.company_id, OLD.transfer_no, OLD.prepared_by) THEN
    RAISE EXCEPTION 'fin.bank_transfer: identity columns are immutable';
  END IF;
  IF TG_OP = 'UPDATE' AND OLD.status <> 'PREPARED'
     AND ROW(NEW.from_bank_account_id, NEW.to_bank_account_id, NEW.value_date, NEW.from_amount, NEW.to_amount, NEW.exchange_rate, NEW.amount_dop)
         IS DISTINCT FROM ROW(OLD.from_bank_account_id, OLD.to_bank_account_id, OLD.value_date, OLD.from_amount, OLD.to_amount, OLD.exchange_rate, OLD.amount_dop) THEN
    RAISE EXCEPTION 'fin.bank_transfer: a released transfer never changes';
  END IF;
  IF TG_OP = 'UPDATE' AND NEW.status IS DISTINCT FROM OLD.status AND NOT (
       (OLD.status = 'PREPARED' AND NEW.status IN ('RELEASED', 'VOIDED')) OR (OLD.status = 'RELEASED' AND NEW.status = 'REVERSED')) THEN
    RAISE EXCEPTION 'fin.bank_transfer: % → % is not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;

CREATE FUNCTION fin.bank_transfer_amounts_ok(t fin.bank_transfer, from_currency char(3), to_currency char(3)) RETURNS boolean
  LANGUAGE sql IMMUTABLE AS $$
  SELECT CASE
    WHEN from_currency = to_currency AND from_currency = 'DOP' THEN t.from_amount = t.to_amount AND t.amount_dop = t.from_amount AND t.exchange_rate IS NULL
    WHEN from_currency = to_currency THEN t.from_amount = t.to_amount AND t.exchange_rate IS NOT NULL AND t.amount_dop = round(t.from_amount * t.exchange_rate, 2)
    WHEN from_currency = 'DOP' THEN t.exchange_rate IS NOT NULL AND t.from_amount = round(t.to_amount * t.exchange_rate, 2) AND t.amount_dop = t.from_amount
    ELSE t.exchange_rate IS NOT NULL AND t.to_amount = round(t.from_amount * t.exchange_rate, 2) AND t.amount_dop = t.to_amount
  END
$$;

CREATE TRIGGER bank_transfer_guard BEFORE INSERT OR UPDATE OR DELETE ON fin.bank_transfer FOR EACH ROW EXECUTE FUNCTION fin.bank_transfer_guard();
CREATE TRIGGER bank_transfer_no_truncate BEFORE TRUNCATE ON fin.bank_transfer FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER bank_transfer_four_eyes BEFORE INSERT OR UPDATE ON fin.bank_transfer
  FOR EACH ROW EXECUTE FUNCTION core.four_eyes('released_by', 'prepared_by', 'bank_transfer_four_eyes');
CREATE CONSTRAINT TRIGGER bank_transfer_evidence_on_insert AFTER INSERT ON fin.bank_transfer
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('BankTransfer', 'transfer_id');
CREATE CONSTRAINT TRIGGER bank_transfer_evidence_on_change AFTER UPDATE ON fin.bank_transfer
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('BankTransfer', 'transfer_id');

GRANT SELECT, INSERT ON fin.bank_transfer TO rochell_app;
GRANT UPDATE (from_bank_account_id, to_bank_account_id, value_date, from_amount, to_amount, exchange_rate, amount_dop, bank_reference, status, released_by,
  posting_event_id, version) ON fin.bank_transfer TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- A statement line of either account matches the transfer (E-USD1-05b-3): the origin's DEBIT, the destination's CREDIT.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE fin.bank_statement_line
  ADD COLUMN matched_transfer_id uuid,
  ADD CONSTRAINT bank_statement_line_transfer_fk FOREIGN KEY (company_id, matched_transfer_id) REFERENCES fin.bank_transfer (company_id, transfer_id),
  DROP CONSTRAINT bank_statement_line_matched,
  ADD CONSTRAINT bank_statement_line_matched CHECK (
    (status = 'MATCHED') = (num_nonnulls(matched_payment_id, matched_receipt_id, matched_deposit_id, matched_refund_id, matched_transfer_id) = 1)
    AND (status = 'MATCHED' OR num_nonnulls(matched_payment_id, matched_receipt_id, matched_deposit_id, matched_refund_id, matched_transfer_id) = 0));
CREATE UNIQUE INDEX bank_statement_line_matched_transfer_uq ON fin.bank_statement_line (matched_transfer_id, bank_account_id) WHERE matched_transfer_id IS NOT NULL;
GRANT UPDATE (matched_transfer_id) ON fin.bank_statement_line TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- P-42 (E-USD1-05b-2). Seeded DRAFT; the Controller approves it (A-01).
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type) VALUES
  ('0192f001-0000-7000-8000-000000000034', 'P-42', 'BankTransferReleased');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, effective_from, status) VALUES
  ('0192f001-0000-7000-8000-000000000034', 1,
   '{"lines": [
      {"code": "P42-DR-BANK", "side": "DEBIT", "account_role": "BANK", "amount": "transfer_amount", "dimensions": [], "subledger": "BANK"},
      {"code": "P42-CR-BANK", "side": "CREDIT", "account_role": "BANK", "amount": "transfer_amount", "dimensions": [], "subledger": "BANK"}
    ]}',
   '{"P42-DR-BANK": "Transferencia {transfer_no}: entra a la cuenta de destino{rate_text}.",
     "P42-CR-BANK": "Transferencia {transfer_no}: sale de la cuenta de origen{rate_text}."}',
   'BANK-REC', DATE '2026-01-01', 'DRAFT');
