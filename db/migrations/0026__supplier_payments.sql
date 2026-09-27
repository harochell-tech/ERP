-- VS2-03 · Supplier payments: prepare, update, void, release (R-09). Approved errata E-VS2-03-1…9.

-- ---------------------------------------------------------------------------------------------
-- E-VS2-03-5: payment, application and statement amounts carry 2 decimals, like the GL lines they become.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE fin.payment ADD CONSTRAINT payment_two_decimals CHECK (amount = round(amount, 2));
ALTER TABLE fin.ap_application ADD CONSTRAINT ap_application_two_decimals CHECK (amount = round(amount, 2));
ALTER TABLE fin.bank_statement_line ADD CONSTRAINT bank_statement_line_two_decimals CHECK (amount = round(amount, 2));

-- ---------------------------------------------------------------------------------------------
-- E-VS2-03-7: the payment's own number (PAG-000123), typed by the treasurer in the transfer's description or reference so the
-- statement line can be matched to it (VS2-05). Unique per company, immutable.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE fin.payment
  ADD COLUMN payment_no text NOT NULL,
  ADD CONSTRAINT payment_no_uq UNIQUE (company_id, payment_no),
  ADD CONSTRAINT payment_no_format CHECK (payment_no ~ '^PAG-[0-9]{6,}$');

CREATE OR REPLACE FUNCTION fin.payment_guard() RETURNS trigger
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
  IF ROW(NEW.payment_id, NEW.company_id, NEW.direction, NEW.party_id, NEW.method, NEW.currency, NEW.prepared_by, NEW.payment_no)
     IS DISTINCT FROM ROW(OLD.payment_id, OLD.company_id, OLD.direction, OLD.party_id, OLD.method, OLD.currency, OLD.prepared_by, OLD.payment_no) THEN
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

-- ---------------------------------------------------------------------------------------------
-- E-VS2-03-1: what a PREPARED payment plans to apply, one row per invoice per payment version (append-only). Prepare and each
-- update write the whole set for the payment's new version; release turns the current version's set into fin.ap_application.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fin.payment_allocation (
  company_id       uuid          NOT NULL,
  payment_id       uuid          NOT NULL,
  payment_version  bigint        NOT NULL,
  ap_doc_id        uuid          NOT NULL,
  amount           numeric(19,4) NOT NULL,
  CONSTRAINT payment_allocation_pk PRIMARY KEY (payment_id, payment_version, ap_doc_id),
  CONSTRAINT payment_allocation_payment_fk FOREIGN KEY (company_id, payment_id) REFERENCES fin.payment (company_id, payment_id),
  CONSTRAINT payment_allocation_ap_doc_fk FOREIGN KEY (company_id, ap_doc_id) REFERENCES fin.ap_document (company_id, ap_doc_id),
  CONSTRAINT payment_allocation_amount_positive CHECK (amount > 0),
  CONSTRAINT payment_allocation_two_decimals CHECK (amount = round(amount, 2)),
  CONSTRAINT payment_allocation_version_positive CHECK (payment_version >= 1)
);

-- Only for the payment's current version while it is PREPARED, and only invoices of its supplier.
CREATE FUNCTION fin.payment_allocation_before_insert() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  p fin.payment;
BEGIN
  SELECT * INTO p FROM fin.payment WHERE payment_id = NEW.payment_id;
  IF p.status::text <> 'PREPARED' OR p.version <> NEW.payment_version THEN
    RAISE EXCEPTION 'fin.payment_allocation: allocations belong to the current version of a PREPARED payment (E-VS2-03-1)';
  END IF;
  IF (SELECT party_id FROM fin.ap_document WHERE ap_doc_id = NEW.ap_doc_id) IS DISTINCT FROM p.party_id THEN
    RAISE EXCEPTION 'fin.payment_allocation: AP document % does not belong to the payment''s supplier (E-VS2-01-14)', NEW.ap_doc_id;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER payment_allocation_before_insert BEFORE INSERT ON fin.payment_allocation
  FOR EACH ROW EXECUTE FUNCTION fin.payment_allocation_before_insert();
CREATE TRIGGER payment_allocation_append_only BEFORE UPDATE OR DELETE ON fin.payment_allocation FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER payment_allocation_no_truncate BEFORE TRUNCATE ON fin.payment_allocation FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- At COMMIT: a PREPARED payment's current allocations add up to its amount; a released one's live applications too (PAY-APPL).
CREATE FUNCTION fin.payment_amount_allocated() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  p fin.payment;
  total numeric;
BEGIN
  SELECT * INTO p FROM fin.payment WHERE payment_id = NEW.payment_id;
  IF p.status::text = 'PREPARED' THEN
    SELECT coalesce(sum(amount), 0) INTO total FROM fin.payment_allocation WHERE payment_id = p.payment_id AND payment_version = p.version;
  ELSIF p.status::text IN ('RELEASED', 'CLEARED') THEN
    SELECT coalesce(sum(CASE WHEN a.reverses_application_id IS NULL THEN a.amount ELSE -a.amount END), 0) INTO total
    FROM fin.ap_application a WHERE a.payment_id = p.payment_id;
  ELSE
    RETURN NULL;
  END IF;
  IF total <> p.amount THEN
    RAISE EXCEPTION 'fin.payment %: its applications add up to %, not to its amount % (E-VS2-03-1)', p.payment_id, total, p.amount;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER payment_amount_allocated AFTER INSERT OR UPDATE ON fin.payment
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.payment_amount_allocated();

-- K-25 for payments: RELEASED or CLEARED ⇔ an unreversed AUTO journal of its posting event.
CREATE FUNCTION fin.payment_posting_evidence() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.status::text IN ('RELEASED', 'CLEARED') AND NOT EXISTS (
       SELECT 1 FROM fin.gl_journal j
       WHERE j.company_id = NEW.company_id AND j.source_event_id = NEW.posting_event_id AND j.journal_type = 'AUTO'
         AND NOT EXISTS (SELECT 1 FROM fin.gl_journal r WHERE r.reverses_journal_id = j.journal_id)) THEN
    RAISE EXCEPTION 'fin.payment %: % without the journal of its posting event (K-25)', NEW.payment_id, NEW.status;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER payment_posting_evidence AFTER UPDATE ON fin.payment
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.payment_posting_evidence();

-- ---------------------------------------------------------------------------------------------
-- E-VS2-03-3: a rule version may require more close components open than its own (R-09: BANK-REC and AP-REC, E-VS2-7).
-- ---------------------------------------------------------------------------------------------
ALTER TABLE fin.posting_rule_version
  ADD COLUMN also_requires_components text[] NOT NULL DEFAULT '{}',
  ADD CONSTRAINT posting_rule_version_also_requires CHECK (also_requires_components <@ ARRAY['INV-MOV', 'AP-REC', 'BANK-REC']::text[]);

CREATE OR REPLACE FUNCTION fin.posting_rule_version_identity() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF ROW(NEW.posting_rule_id, NEW.version, NEW.definition, NEW.explanation_templates, NEW.close_component, NEW.effective_from, NEW.also_requires_components)
     IS DISTINCT FROM ROW(OLD.posting_rule_id, OLD.version, OLD.definition, OLD.explanation_templates, OLD.close_component, OLD.effective_from, OLD.also_requires_components) THEN
    RAISE EXCEPTION 'fin.posting_rule_version: definition columns are immutable; create a new version';
  END IF;
  RETURN NEW;
END $$;

-- E-VS1-9 extended: no journal into a period where any component its rule version requires is CLOSED.
CREATE OR REPLACE FUNCTION fin.gl_journal_component_open() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
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
-- E-VS2-03-2: R-09 (v2.1 P-06), DRAFT until the Controller approves it. One AP line per application (subledger AP, reference =
-- the AP document); one BANK line for the payment (subledger BANK, reference = the bank account, whose own GL account the engine
-- uses, E-VS2-01-1).
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type)
VALUES ('0192f001-0000-7000-8000-000000000009', 'R-09', 'SupplierPaymentReleased');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, also_requires_components, effective_from, status)
VALUES ('0192f001-0000-7000-8000-000000000009', 1,
   '{"lines": [
      {"code": "R09-DR-AP", "side": "DEBIT", "account_role": "AP_CONTROL", "amount": "applied_amount", "dimensions": ["party"], "subledger": "AP"},
      {"code": "R09-CR-BANK", "side": "CREDIT", "account_role": "BANK", "amount": "payment_amount", "dimensions": ["party"], "subledger": "BANK"}
    ]}',
   '{"R09-DR-AP": "Pago {payment_no}: baja la cuenta por pagar de la factura aplicada por el monto aplicado.",
     "R09-CR-BANK": "Pago {payment_no}: sale del banco por el total transferido al proveedor."}',
   'BANK-REC', ARRAY['AP-REC'], DATE '2026-01-01', 'DRAFT');

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE fin.payment_allocation ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON fin.payment_allocation
  USING (company_id = nullif(current_setting('app.company_id', true), '')::uuid)
  WITH CHECK (company_id = nullif(current_setting('app.company_id', true), '')::uuid);
GRANT SELECT, INSERT ON fin.payment_allocation TO rochell_app;
