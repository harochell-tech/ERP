-- VS3-06 · Commercial credit note (P-22). Frozen Baseline VS#3 §3, §6, §7; v2.1.1 E-1; approved errata E-VS3-12, E-VS3-06-1…10.

-- ---------------------------------------------------------------------------------------------
-- E-VS3-06-4: an invoice fully credited is CREDITED (it keeps its posting and its accepted e-CF).
-- ---------------------------------------------------------------------------------------------
ALTER TABLE sal.invoice DROP CONSTRAINT invoice_statuses,
  ADD CONSTRAINT invoice_statuses CHECK (
    (commercial_status = 'DRAFT' AND accounting_status = 'NOT_POSTED' AND fiscal_status = 'PENDING')
    OR (commercial_status IN ('CONFIRMED', 'PARTIALLY_PAID', 'PAID') AND accounting_status = 'POSTED' AND fiscal_status IN ('PENDING_EXTERNAL', 'ACCEPTED_EXTERNAL'))
    OR (commercial_status = 'CREDITED' AND accounting_status = 'POSTED' AND fiscal_status = 'ACCEPTED_EXTERNAL')
    OR (commercial_status = 'VOIDED' AND accounting_status = 'REVERSED' AND fiscal_status = 'PENDING_EXTERNAL'));

-- ---------------------------------------------------------------------------------------------
-- Credit notes (E-VS3-06-1…5): NC-000001 per company, on a fiscalized invoice; e-CF type 34.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE sal.credit_note (
  credit_note_id     uuid          NOT NULL,
  company_id         uuid          NOT NULL,
  credit_note_no     text          NOT NULL,
  invoice_id         uuid          NOT NULL,
  party_id           uuid          NOT NULL,
  reason_category    text          NOT NULL,
  reason             text          NOT NULL,
  credit_date        date,
  commercial_status  text          NOT NULL,
  accounting_status  text          NOT NULL,
  fiscal_status      text          NOT NULL,
  encf               text,
  net_total          numeric(19,4) NOT NULL,
  tax_total          numeric(19,4) NOT NULL,
  total              numeric(19,4) NOT NULL,
  posting_event_id   uuid,
  created_by         uuid          NOT NULL,
  issued_by          uuid,
  version            bigint        NOT NULL,
  CONSTRAINT credit_note_pk PRIMARY KEY (credit_note_id),
  CONSTRAINT credit_note_company_uq UNIQUE (company_id, credit_note_id),
  CONSTRAINT credit_note_no_uq UNIQUE (company_id, credit_note_no),
  CONSTRAINT credit_note_invoice_fk FOREIGN KEY (company_id, invoice_id) REFERENCES sal.invoice (company_id, invoice_id),
  CONSTRAINT credit_note_party_fk FOREIGN KEY (company_id, party_id) REFERENCES md.party (company_id, party_id),
  CONSTRAINT credit_note_posting_event_fk FOREIGN KEY (company_id, posting_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT credit_note_created_by_fk FOREIGN KEY (created_by) REFERENCES iam.user (user_id),
  CONSTRAINT credit_note_issued_by_fk FOREIGN KEY (issued_by) REFERENCES iam.user (user_id),
  CONSTRAINT credit_note_no_format CHECK (credit_note_no ~ '^NC-[0-9]{6,}$'),
  CONSTRAINT credit_note_reason CHECK (reason_category IN ('DESCUENTO', 'ERROR_DE_PRECIO', 'OTRO') AND length(btrim(reason)) BETWEEN 1 AND 500),
  CONSTRAINT credit_note_statuses CHECK (
    (commercial_status = 'DRAFT' AND accounting_status = 'NOT_POSTED' AND fiscal_status = 'PENDING')
    OR (commercial_status = 'CONFIRMED' AND accounting_status = 'POSTED' AND fiscal_status IN ('PENDING_EXTERNAL', 'ACCEPTED_EXTERNAL'))),
  CONSTRAINT credit_note_issued CHECK ((commercial_status = 'DRAFT') = (issued_by IS NULL AND posting_event_id IS NULL AND credit_date IS NULL)),
  CONSTRAINT credit_note_encf CHECK ((fiscal_status = 'ACCEPTED_EXTERNAL') = (encf IS NOT NULL) AND (encf IS NULL OR encf ~ '^E34[0-9]{10}$')),
  CONSTRAINT credit_note_totals CHECK (net_total > 0 AND net_total = round(net_total, 2) AND tax_total >= 0 AND tax_total = round(tax_total, 2) AND total = net_total + tax_total),
  CONSTRAINT credit_note_version_positive CHECK (version >= 1)
);
CREATE UNIQUE INDEX credit_note_encf_uq ON sal.credit_note (company_id, encf) WHERE encf IS NOT NULL;

CREATE TABLE sal.credit_note_line (
  credit_note_line_id  uuid          NOT NULL,
  company_id           uuid          NOT NULL,
  credit_note_id       uuid          NOT NULL,
  line_no              integer       NOT NULL,
  invoice_line_id      uuid          NOT NULL,
  net_amount           numeric(19,4) NOT NULL,
  rate                 numeric(9,6)  NOT NULL,
  itbis                numeric(19,4) NOT NULL,
  CONSTRAINT credit_note_line_pk PRIMARY KEY (credit_note_line_id),
  CONSTRAINT credit_note_line_no_uq UNIQUE (credit_note_id, line_no),
  CONSTRAINT credit_note_line_invoice_line_uq UNIQUE (credit_note_id, invoice_line_id),
  CONSTRAINT credit_note_line_note_fk FOREIGN KEY (company_id, credit_note_id) REFERENCES sal.credit_note (company_id, credit_note_id),
  CONSTRAINT credit_note_line_invoice_line_fk FOREIGN KEY (company_id, invoice_line_id) REFERENCES sal.invoice_line (company_id, invoice_line_id),
  CONSTRAINT credit_note_line_amounts CHECK (net_amount > 0 AND net_amount = round(net_amount, 2) AND rate >= 0 AND itbis >= 0 AND itbis = round(itbis, 2))
);

CREATE FUNCTION sal.credit_note_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.commercial_status <> 'DRAFT' OR NEW.version <> 1 THEN
      RAISE EXCEPTION 'sal.credit_note: a credit note is created DRAFT with version 1';
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'sal.credit_note rows cannot be deleted';
  END IF;
  IF ROW(NEW.credit_note_id, NEW.company_id, NEW.credit_note_no, NEW.invoice_id, NEW.party_id, NEW.reason_category, NEW.reason, NEW.net_total, NEW.tax_total, NEW.total, NEW.created_by)
     IS DISTINCT FROM ROW(OLD.credit_note_id, OLD.company_id, OLD.credit_note_no, OLD.invoice_id, OLD.party_id, OLD.reason_category, OLD.reason, OLD.net_total, OLD.tax_total, OLD.total, OLD.created_by)
     OR NEW.version <> OLD.version + 1
     OR (OLD.commercial_status <> 'DRAFT' AND ROW(NEW.credit_date, NEW.posting_event_id, NEW.issued_by) IS DISTINCT FROM ROW(OLD.credit_date, OLD.posting_event_id, OLD.issued_by))
     OR (OLD.encf IS NOT NULL AND NEW.encf IS DISTINCT FROM OLD.encf) THEN
    RAISE EXCEPTION 'sal.credit_note: its content, posting and e-NCF never change once set';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER credit_note_guard BEFORE INSERT OR UPDATE OR DELETE ON sal.credit_note FOR EACH ROW EXECUTE FUNCTION sal.credit_note_guard();
CREATE TRIGGER credit_note_no_truncate BEFORE TRUNCATE ON sal.credit_note FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

CREATE FUNCTION sal.require_credit_note_history() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (
       SELECT 1 FROM core.state_history h
       WHERE h.aggregate_type = 'CreditNote' AND h.aggregate_id = NEW.credit_note_id AND h.to_state = NEW.commercial_status AND h.xmin = pg_current_xact_id()::xid) THEN
    RAISE EXCEPTION 'CreditNote %: status % without its state_history row (ADR-027)', NEW.credit_note_id, NEW.commercial_status;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER credit_note_evidence_on_insert AFTER INSERT ON sal.credit_note
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION sal.require_credit_note_history();
CREATE CONSTRAINT TRIGGER credit_note_evidence_on_change AFTER UPDATE ON sal.credit_note
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.commercial_status IS DISTINCT FROM NEW.commercial_status)
  EXECUTE FUNCTION sal.require_credit_note_history();

-- K-25: POSTED ⇔ one live AUTO journal of its posting event.
CREATE FUNCTION sal.credit_note_journal_evidence() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.accounting_status = 'POSTED' AND (
       SELECT count(*) FROM fin.gl_journal j
       WHERE j.company_id = NEW.company_id AND j.source_event_id = NEW.posting_event_id AND j.journal_type = 'AUTO'
         AND NOT EXISTS (SELECT 1 FROM fin.gl_journal r WHERE r.reverses_journal_id = j.journal_id)) <> 1 THEN
    RAISE EXCEPTION 'sal.credit_note %: POSTED without its live journal (K-25)', NEW.credit_note_id;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER credit_note_journal_evidence AFTER UPDATE ON sal.credit_note
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.accounting_status IS DISTINCT FROM NEW.accounting_status)
  EXECUTE FUNCTION sal.credit_note_journal_evidence();

CREATE FUNCTION sal.credit_note_line_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'INSERT' THEN
    RAISE EXCEPTION 'sal.credit_note_line rows cannot be changed or deleted';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM sal.credit_note n WHERE n.credit_note_id = NEW.credit_note_id AND n.commercial_status = 'DRAFT') THEN
    RAISE EXCEPTION 'sal.credit_note_line: lines are written only while the credit note is DRAFT';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM sal.invoice_line il JOIN sal.credit_note n ON n.invoice_id = il.invoice_id
                 WHERE il.invoice_line_id = NEW.invoice_line_id AND n.credit_note_id = NEW.credit_note_id) THEN
    RAISE EXCEPTION 'sal.credit_note_line: the line belongs to another invoice';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER credit_note_line_guard BEFORE INSERT OR UPDATE OR DELETE ON sal.credit_note_line FOR EACH ROW EXECUTE FUNCTION sal.credit_note_line_guard();
CREATE TRIGGER credit_note_line_no_truncate BEFORE TRUNCATE ON sal.credit_note_line FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- E-VS3-06-8: the external fiscal record belongs to exactly one invoice or one credit note.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE tax.external_fiscal_record
  ALTER COLUMN invoice_id DROP NOT NULL,
  ADD COLUMN credit_note_id uuid,
  ADD CONSTRAINT external_fiscal_record_credit_note_uq UNIQUE (credit_note_id),
  ADD CONSTRAINT external_fiscal_record_credit_note_fk FOREIGN KEY (company_id, credit_note_id) REFERENCES sal.credit_note (company_id, credit_note_id),
  ADD CONSTRAINT external_fiscal_record_subject CHECK ((invoice_id IS NULL) <> (credit_note_id IS NULL));

-- ---------------------------------------------------------------------------------------------
-- E-VS3-06-6: P-22, DRAFT until the Controller approves it (A-01). Close component AR-REC.
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type)
VALUES ('0192f001-0000-7000-8000-000000000017', 'P-22', 'CreditNoteIssued');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, also_requires_components, effective_from, status)
VALUES ('0192f001-0000-7000-8000-000000000017', 1,
   '{"lines": [
      {"code": "P22-DR-DISC", "side": "DEBIT", "account_role": "SALES_DISCOUNTS", "amount": "note_net", "dimensions": ["party"]},
      {"code": "P22-DR-ITBIS", "side": "DEBIT", "account_role": "ITBIS_PAYABLE", "amount": "note_itbis", "dimensions": []},
      {"code": "P22-CR-AR", "side": "CREDIT", "account_role": "AR_CONTROL", "amount": "note_total", "dimensions": ["party"], "subledger": "AR"}
    ]}',
   '{"P22-DR-DISC": "Descuento o corrección de precio de la nota de crédito {credit_note_no} sobre la factura {invoice_no} (e-NCF {invoice_encf}).",
     "P22-DR-ITBIS": "ITBIS de ventas que la nota {credit_note_no} devuelve, a la tasa de la factura {invoice_no}.",
     "P22-CR-AR": "La nota {credit_note_no} rebaja la cuenta por cobrar de la factura {invoice_no}."}',
   'AR-REC', ARRAY[]::text[], DATE '2026-01-01', 'DRAFT');

-- ---------------------------------------------------------------------------------------------
-- Permissions (E-VS3-06-7).
-- ---------------------------------------------------------------------------------------------
INSERT INTO iam.permission (permission_code, access) VALUES ('credit_note:create', 'WRITE'), ('credit_note:issue', 'WRITE');
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES ('FACTURACION', 'credit_note:create'), ('FACTURACION', 'credit_note:issue')) AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['sal.credit_note', 'sal.credit_note_line'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON sal.credit_note, sal.credit_note_line TO rochell_app;
GRANT UPDATE (credit_date, commercial_status, accounting_status, fiscal_status, encf, posting_event_id, issued_by, version) ON sal.credit_note TO rochell_app;
