-- VS3-05 · Sales invoice from deliveries, sales ITBIS, external e-CF record and void (P-18). Frozen Baseline VS#3 §2–§6,
-- v2.1.1 E-1 (three statuses), v2.1 §4.1 (external fiscal channel); approved errata E-VS3-05-1…14.

-- Document links of the invoice: INVOICES (invoice line → delivery line) and FISCALIZES (external e-CF → invoice, v2.1 §4.1).
-- Used by the commands only, never as literals in this migration (see 0011).
ALTER TYPE core.link_type ADD VALUE 'INVOICES';
ALTER TYPE core.link_type ADD VALUE 'FISCALIZES';

-- ---------------------------------------------------------------------------------------------
-- E-VS3-05-1: SALES_ITBIS rules and the OUTPUT effect.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE tax.fiscal_rule DROP CONSTRAINT fiscal_rule_kind,
  ADD CONSTRAINT fiscal_rule_kind CHECK (rule_kind IN ('PURCHASE_ITBIS', 'PURCHASE_WITHHOLDING', 'SALES_ITBIS'));
ALTER TABLE tax.tax_determination_line DROP CONSTRAINT tax_determination_line_effect,
  ADD CONSTRAINT tax_determination_line_effect CHECK (effect IN ('RECOVERABLE_INPUT', 'NON_RECOVERABLE_INPUT', 'WITHHOLDING', 'OUTPUT'));

-- ---------------------------------------------------------------------------------------------
-- E-VS3-05-7: close component AR-REC, accepted everywhere a component is named, OPEN in every existing period.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE fin.posting_rule_version DROP CONSTRAINT posting_rule_version_component,
  ADD CONSTRAINT posting_rule_version_component CHECK (close_component IN ('INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX', 'AR-REC'));
ALTER TABLE fin.posting_rule_version DROP CONSTRAINT posting_rule_version_also_requires,
  ADD CONSTRAINT posting_rule_version_also_requires CHECK (also_requires_components <@ ARRAY['INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX', 'AR-REC']::text[]);
ALTER TABLE fin.close_component_state DROP CONSTRAINT close_component_state_component,
  ADD CONSTRAINT close_component_state_component CHECK (component IN ('INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX', 'AR-REC'));
ALTER TABLE fin.close_snapshot DROP CONSTRAINT close_snapshot_component,
  ADD CONSTRAINT close_snapshot_component CHECK (component IN ('INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX', 'AR-REC'));
ALTER TABLE fin.reopen_request DROP CONSTRAINT reopen_request_component,
  ADD CONSTRAINT reopen_request_component CHECK (component IN ('INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX', 'AR-REC'));
ALTER TABLE rec.recon_blocking DROP CONSTRAINT recon_blocking_component,
  ADD CONSTRAINT recon_blocking_component CHECK (component IN ('INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX', 'AR-REC'));
ALTER TABLE rec.recon_exception DROP CONSTRAINT recon_exception_component,
  ADD CONSTRAINT recon_exception_component CHECK (component IS NULL OR component IN ('INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX', 'AR-REC'));

INSERT INTO fin.close_component_state (company_id, period_id, component, status, version)
SELECT company_id, period_id, 'AR-REC', 'OPEN', 1 FROM fin.period
ON CONFLICT (period_id, component) DO NOTHING;

-- ---------------------------------------------------------------------------------------------
-- E-VS3-05-6: accounts receivable documents (receipts arrive with VS3-07).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fin.ar_document (
  ar_doc_id        uuid          NOT NULL,
  company_id       uuid          NOT NULL,
  party_id         uuid          NOT NULL,
  doc_type         text          NOT NULL,
  source_doc_id    uuid          NOT NULL,
  doc_no           text          NOT NULL,
  doc_date         date          NOT NULL,
  due_date         date          NOT NULL,
  original_amount  numeric(19,4) NOT NULL,
  open_amount      numeric(19,4) NOT NULL,
  version          bigint        NOT NULL,
  CONSTRAINT ar_document_pk PRIMARY KEY (ar_doc_id),
  CONSTRAINT ar_document_company_uq UNIQUE (company_id, ar_doc_id),
  CONSTRAINT ar_document_source_uq UNIQUE (company_id, source_doc_id),
  CONSTRAINT ar_document_party_fk FOREIGN KEY (company_id, party_id) REFERENCES md.party (company_id, party_id),
  CONSTRAINT ar_document_type CHECK (doc_type = 'INVOICE'),
  CONSTRAINT ar_document_dates CHECK (due_date >= doc_date),
  CONSTRAINT ar_document_amounts CHECK (original_amount > 0 AND original_amount = round(original_amount, 2) AND open_amount >= 0 AND open_amount <= original_amount),
  CONSTRAINT ar_document_version_positive CHECK (version >= 1)
);

CREATE FUNCTION fin.ar_document_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'fin.ar_document rows cannot be deleted';
  END IF;
  IF ROW(NEW.ar_doc_id, NEW.company_id, NEW.party_id, NEW.doc_type, NEW.source_doc_id, NEW.doc_no, NEW.doc_date, NEW.due_date, NEW.original_amount)
     IS DISTINCT FROM ROW(OLD.ar_doc_id, OLD.company_id, OLD.party_id, OLD.doc_type, OLD.source_doc_id, OLD.doc_no, OLD.doc_date, OLD.due_date, OLD.original_amount)
     OR NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'fin.ar_document: only the open amount changes, with version + 1';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER ar_document_guard BEFORE UPDATE OR DELETE ON fin.ar_document FOR EACH ROW EXECUTE FUNCTION fin.ar_document_guard();
CREATE TRIGGER ar_document_no_truncate BEFORE TRUNCATE ON fin.ar_document FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- Invoices (E-1, E-VS3-05-3…5, 8, 11): FA-000001 per company; three statuses with the valid combinations of VS#3.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE sal.invoice (
  invoice_id            uuid          NOT NULL,
  company_id            uuid          NOT NULL,
  invoice_no            text          NOT NULL,
  party_id              uuid          NOT NULL,
  invoice_date          date,
  due_date              date,
  ecf_type              text          NOT NULL,
  commercial_status     text          NOT NULL,
  accounting_status     text          NOT NULL,
  fiscal_status         text          NOT NULL,
  encf                  text,
  tax_determination_id  uuid,
  net_total             numeric(19,4) NOT NULL,
  tax_total             numeric(19,4),
  total                 numeric(19,4),
  ar_doc_id             uuid,
  posting_event_id      uuid,
  void_event_id         uuid,
  void_reason           text,
  created_by            uuid          NOT NULL,
  issued_by             uuid,
  version               bigint        NOT NULL,
  CONSTRAINT invoice_pk PRIMARY KEY (invoice_id),
  CONSTRAINT invoice_company_uq UNIQUE (company_id, invoice_id),
  CONSTRAINT invoice_no_uq UNIQUE (company_id, invoice_no),
  CONSTRAINT invoice_party_fk FOREIGN KEY (company_id, party_id) REFERENCES md.party (company_id, party_id),
  CONSTRAINT invoice_determination_fk FOREIGN KEY (tax_determination_id) REFERENCES tax.tax_determination (determination_id),
  CONSTRAINT invoice_ar_fk FOREIGN KEY (company_id, ar_doc_id) REFERENCES fin.ar_document (company_id, ar_doc_id),
  CONSTRAINT invoice_posting_event_fk FOREIGN KEY (company_id, posting_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT invoice_void_event_fk FOREIGN KEY (company_id, void_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT invoice_created_by_fk FOREIGN KEY (created_by) REFERENCES iam.user (user_id),
  CONSTRAINT invoice_issued_by_fk FOREIGN KEY (issued_by) REFERENCES iam.user (user_id),
  CONSTRAINT invoice_no_format CHECK (invoice_no ~ '^FA-[0-9]{6,}$'),
  CONSTRAINT invoice_ecf_type CHECK (ecf_type IN ('31', '32')),
  CONSTRAINT invoice_encf_format CHECK (encf IS NULL OR encf ~ ('^E' || ecf_type || '[0-9]{10}$')),
  CONSTRAINT invoice_statuses CHECK (
    (commercial_status = 'DRAFT' AND accounting_status = 'NOT_POSTED' AND fiscal_status = 'PENDING')
    OR (commercial_status IN ('CONFIRMED', 'PARTIALLY_PAID', 'PAID') AND accounting_status = 'POSTED' AND fiscal_status IN ('PENDING_EXTERNAL', 'ACCEPTED_EXTERNAL'))
    OR (commercial_status = 'VOIDED' AND accounting_status = 'REVERSED' AND fiscal_status = 'PENDING_EXTERNAL')),
  CONSTRAINT invoice_issued CHECK ((commercial_status = 'DRAFT') = (issued_by IS NULL AND posting_event_id IS NULL AND ar_doc_id IS NULL AND tax_determination_id IS NULL
                                                                   AND invoice_date IS NULL AND due_date IS NULL AND tax_total IS NULL AND total IS NULL)),
  CONSTRAINT invoice_encf_present CHECK ((fiscal_status = 'ACCEPTED_EXTERNAL') = (encf IS NOT NULL)),
  CONSTRAINT invoice_voided CHECK ((commercial_status = 'VOIDED') = (void_event_id IS NOT NULL AND coalesce(length(btrim(void_reason)) > 0, false))),
  CONSTRAINT invoice_totals CHECK (net_total > 0 AND net_total = round(net_total, 2)
    AND (total IS NULL OR (tax_total >= 0 AND tax_total = round(tax_total, 2) AND total = net_total + tax_total))),
  CONSTRAINT invoice_due CHECK (due_date IS NULL OR due_date >= invoice_date),
  CONSTRAINT invoice_version_positive CHECK (version >= 1)
);
CREATE UNIQUE INDEX invoice_encf_uq ON sal.invoice (company_id, encf) WHERE encf IS NOT NULL;

CREATE TABLE sal.invoice_line (
  invoice_line_id   uuid          NOT NULL,
  company_id        uuid          NOT NULL,
  invoice_id        uuid          NOT NULL,
  line_no           integer       NOT NULL,
  delivery_line_id  uuid          NOT NULL,
  item_id           uuid          NOT NULL,
  uom               text          NOT NULL,
  quantity          numeric(18,6) NOT NULL,
  unit_price        numeric(19,4) NOT NULL,
  net_amount        numeric(19,4) NOT NULL,
  CONSTRAINT invoice_line_pk PRIMARY KEY (invoice_line_id),
  CONSTRAINT invoice_line_company_uq UNIQUE (company_id, invoice_line_id),
  CONSTRAINT invoice_line_no_uq UNIQUE (invoice_id, line_no),
  CONSTRAINT invoice_line_delivery_uq UNIQUE (invoice_id, delivery_line_id),
  CONSTRAINT invoice_line_invoice_fk FOREIGN KEY (company_id, invoice_id) REFERENCES sal.invoice (company_id, invoice_id),
  CONSTRAINT invoice_line_delivery_fk FOREIGN KEY (company_id, delivery_line_id) REFERENCES log.delivery_line (company_id, delivery_line_id),
  CONSTRAINT invoice_line_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT invoice_line_amounts CHECK (quantity > 0 AND unit_price > 0 AND net_amount > 0 AND net_amount = round(net_amount, 2))
);

CREATE FUNCTION sal.invoice_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.commercial_status <> 'DRAFT' OR NEW.version <> 1 THEN
      RAISE EXCEPTION 'sal.invoice: an invoice is created DRAFT with version 1';
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'sal.invoice rows cannot be deleted; void the invoice';
  END IF;
  IF ROW(NEW.invoice_id, NEW.company_id, NEW.invoice_no, NEW.party_id, NEW.net_total, NEW.created_by)
     IS DISTINCT FROM ROW(OLD.invoice_id, OLD.company_id, OLD.invoice_no, OLD.party_id, OLD.net_total, OLD.created_by)
     OR NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'sal.invoice: identity and net total are immutable and the version increases by 1';
  END IF;
  IF OLD.commercial_status <> 'DRAFT' AND ROW(NEW.invoice_date, NEW.due_date, NEW.ecf_type, NEW.tax_determination_id, NEW.tax_total, NEW.total, NEW.ar_doc_id, NEW.posting_event_id, NEW.issued_by)
     IS DISTINCT FROM ROW(OLD.invoice_date, OLD.due_date, OLD.ecf_type, OLD.tax_determination_id, OLD.tax_total, OLD.total, OLD.ar_doc_id, OLD.posting_event_id, OLD.issued_by) THEN
    RAISE EXCEPTION 'sal.invoice: an issued invoice keeps its date, type, taxes, totals and posting';
  END IF;
  IF OLD.encf IS NOT NULL AND NEW.encf IS DISTINCT FROM OLD.encf THEN
    RAISE EXCEPTION 'sal.invoice: the e-NCF never changes once recorded';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER invoice_guard BEFORE INSERT OR UPDATE OR DELETE ON sal.invoice FOR EACH ROW EXECUTE FUNCTION sal.invoice_guard();
CREATE TRIGGER invoice_no_truncate BEFORE TRUNCATE ON sal.invoice FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ADR-027 for the commercial status (the one the users act on) and K-25 for the accounting status.
CREATE FUNCTION sal.require_invoice_history() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (
       SELECT 1 FROM core.state_history h
       WHERE h.aggregate_type = 'Invoice' AND h.aggregate_id = NEW.invoice_id AND h.to_state = NEW.commercial_status AND h.xmin = pg_current_xact_id()::xid) THEN
    RAISE EXCEPTION 'Invoice %: status % without its state_history row (ADR-027)', NEW.invoice_id, NEW.commercial_status;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER invoice_evidence_on_insert AFTER INSERT ON sal.invoice
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION sal.require_invoice_history();
CREATE CONSTRAINT TRIGGER invoice_evidence_on_change AFTER UPDATE ON sal.invoice
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.commercial_status IS DISTINCT FROM NEW.commercial_status)
  EXECUTE FUNCTION sal.require_invoice_history();

CREATE FUNCTION sal.invoice_journal_evidence() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  live integer;
  reversed integer;
BEGIN
  SELECT count(*) FILTER (WHERE NOT EXISTS (SELECT 1 FROM fin.gl_journal r WHERE r.reverses_journal_id = j.journal_id)),
         count(*) FILTER (WHERE EXISTS (SELECT 1 FROM fin.gl_journal r WHERE r.reverses_journal_id = j.journal_id AND r.source_event_id = NEW.void_event_id))
    INTO live, reversed
    FROM fin.gl_journal j WHERE j.company_id = NEW.company_id AND j.source_event_id = NEW.posting_event_id AND j.journal_type = 'AUTO';
  IF (NEW.accounting_status = 'POSTED' AND live <> 1) OR (NEW.accounting_status = 'REVERSED' AND (live <> 0 OR reversed <> 1)) THEN
    RAISE EXCEPTION 'sal.invoice %: accounting status % without its journal (K-25): live %, reversed %', NEW.invoice_id, NEW.accounting_status, live, reversed;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER invoice_journal_evidence AFTER UPDATE ON sal.invoice
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.accounting_status IS DISTINCT FROM NEW.accounting_status)
  EXECUTE FUNCTION sal.invoice_journal_evidence();

CREATE FUNCTION sal.invoice_line_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'INSERT' THEN
    RAISE EXCEPTION 'sal.invoice_line rows cannot be changed or deleted';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM sal.invoice i WHERE i.invoice_id = NEW.invoice_id AND i.commercial_status = 'DRAFT') THEN
    RAISE EXCEPTION 'sal.invoice_line: lines are written only while the invoice is DRAFT';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER invoice_line_guard BEFORE INSERT OR UPDATE OR DELETE ON sal.invoice_line FOR EACH ROW EXECUTE FUNCTION sal.invoice_line_guard();
CREATE TRIGGER invoice_line_no_truncate BEFORE TRUNCATE ON sal.invoice_line FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- E-VS3-05-11: voiding an unfiscalized invoice gives its quantities back to the delivery lines, so the invoiced quantity may go
-- down (never below zero, CHECK of 0041); the plan and the other quantities keep moving forward only.
CREATE OR REPLACE FUNCTION log.delivery_line_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NOT EXISTS (SELECT 1 FROM log.delivery d WHERE d.delivery_id = NEW.delivery_id AND d.status = 'PLANNED') THEN
      RAISE EXCEPTION 'log.delivery_line: lines are planned with the delivery';
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'log.delivery_line rows cannot be deleted';
  END IF;
  IF ROW(NEW.delivery_line_id, NEW.company_id, NEW.delivery_id, NEW.line_no, NEW.sales_order_line_id, NEW.item_id, NEW.uom, NEW.qty_planned)
     IS DISTINCT FROM ROW(OLD.delivery_line_id, OLD.company_id, OLD.delivery_id, OLD.line_no, OLD.sales_order_line_id, OLD.item_id, OLD.uom, OLD.qty_planned)
     OR NEW.qty_issued < OLD.qty_issued OR NEW.qty_delivered < OLD.qty_delivered OR NEW.qty_returned < OLD.qty_returned
     OR NEW.qty_lost < OLD.qty_lost
     OR (OLD.qty_issued > 0 AND ROW(NEW.source_location_id, NEW.base_factor) IS DISTINCT FROM ROW(OLD.source_location_id, OLD.base_factor)) THEN
    RAISE EXCEPTION 'log.delivery_line: the plan is immutable and quantities only move forward (the invoiced one returns on a void)';
  END IF;
  RETURN NEW;
END $$;

-- ---------------------------------------------------------------------------------------------
-- E-VS3-05-10: the fiscal document issued through the provider's channel, recorded once per invoice.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE tax.external_fiscal_record (
  record_id        uuid          NOT NULL,
  company_id       uuid          NOT NULL,
  invoice_id       uuid          NOT NULL,
  encf             text          NOT NULL,
  issued_at        timestamptz   NOT NULL,
  security_code    text          NOT NULL,
  evidence_ref     text          NOT NULL,
  evidence_sha256  bytea         NOT NULL,
  receiver_rnc     text          NOT NULL,
  net_total        numeric(19,4) NOT NULL,
  tax_total        numeric(19,4) NOT NULL,
  total            numeric(19,4) NOT NULL,
  recorded_by      uuid          NOT NULL,
  event_id         uuid          NOT NULL,
  CONSTRAINT external_fiscal_record_pk PRIMARY KEY (record_id),
  CONSTRAINT external_fiscal_record_invoice_uq UNIQUE (invoice_id),
  CONSTRAINT external_fiscal_record_encf_uq UNIQUE (company_id, encf),
  CONSTRAINT external_fiscal_record_invoice_fk FOREIGN KEY (company_id, invoice_id) REFERENCES sal.invoice (company_id, invoice_id),
  CONSTRAINT external_fiscal_record_by_fk FOREIGN KEY (recorded_by) REFERENCES iam.user (user_id),
  CONSTRAINT external_fiscal_record_event_fk FOREIGN KEY (company_id, event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT external_fiscal_record_texts CHECK (length(btrim(security_code)) BETWEEN 1 AND 60 AND length(btrim(evidence_ref)) BETWEEN 1 AND 200
    AND octet_length(evidence_sha256) = 32 AND receiver_rnc ~ '^([0-9]{9}|[0-9]{11})$')
);
CREATE TRIGGER external_fiscal_record_append_only BEFORE UPDATE OR DELETE ON tax.external_fiscal_record FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- E-VS3-05-5: P-18, DRAFT until the Controller approves it (A-01). Close component AR-REC.
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type)
VALUES ('0192f001-0000-7000-8000-000000000016', 'P-18', 'InvoiceIssued');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, also_requires_components, effective_from, status)
VALUES ('0192f001-0000-7000-8000-000000000016', 1,
   '{"lines": [
      {"code": "P18-DR-AR", "side": "DEBIT", "account_role": "AR_CONTROL", "amount": "invoice_total", "dimensions": ["party"], "subledger": "AR"},
      {"code": "P18-CR-CA", "side": "CREDIT", "account_role": "CONTRACT_ASSET", "amount": "line_net", "dimensions": ["party"], "subledger": "AR"},
      {"code": "P18-CR-UR", "side": "CREDIT", "account_role": "UNBILLED_RECEIVABLE", "amount": "line_net", "dimensions": ["party"], "subledger": "AR"},
      {"code": "P18-CR-ITBIS", "side": "CREDIT", "account_role": "ITBIS_PAYABLE", "amount": "itbis", "dimensions": []}
    ]}',
   '{"P18-DR-AR": "Cuenta por cobrar de la factura {invoice_no}: neto + ITBIS.",
     "P18-CR-CA": "La factura {invoice_no} cancela el activo de contrato de la entrega {delivery_no}.",
     "P18-CR-UR": "La factura {invoice_no} cancela la cuenta por cobrar no facturada de la entrega {delivery_no}.",
     "P18-CR-ITBIS": "ITBIS de ventas de la factura {invoice_no} según la regla fiscal activa a la fecha de factura."}',
   'AR-REC', ARRAY[]::text[], DATE '2026-01-01', 'DRAFT');

-- ---------------------------------------------------------------------------------------------
-- Permissions and SoD (E-VS3-05-12).
-- ---------------------------------------------------------------------------------------------
INSERT INTO iam.permission (permission_code, access) VALUES
  ('invoice:create', 'WRITE'), ('invoice:issue', 'WRITE'), ('fiscal_document:record', 'WRITE'), ('invoice:void', 'WRITE');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES ('FACTURACION', 'invoice:create'), ('FACTURACION', 'invoice:issue'), ('FACTURACION', 'fiscal_document:record'), ('CONTROLLER', 'invoice:void')) AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;

INSERT INTO iam.sod_rule (permission_a, permission_b)
SELECT least(a, b), greatest(a, b) FROM (VALUES ('invoice:issue', 'fiscal_rule:configure'), ('invoice:void', 'invoice:issue')) AS v (a, b);

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['fin.ar_document', 'sal.invoice', 'sal.invoice_line', 'tax.external_fiscal_record'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON fin.ar_document, sal.invoice, sal.invoice_line, tax.external_fiscal_record TO rochell_app;
GRANT UPDATE (open_amount, version) ON fin.ar_document TO rochell_app;
GRANT UPDATE (invoice_date, due_date, ecf_type, commercial_status, accounting_status, fiscal_status, encf, tax_determination_id, tax_total, total, ar_doc_id,
  posting_event_id, void_event_id, void_reason, issued_by, version) ON sal.invoice TO rochell_app;
