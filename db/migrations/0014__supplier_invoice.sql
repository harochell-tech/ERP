-- PR-13a · Supplier invoice, match and AP document schema. Frozen Baseline §9.2, §11.4, E-10, E-11, Patch 1 (P-1, K-25),
-- ADR-027, approved errata E-PR13-0…7. Posting (R-04/R-05), AP and reversal (R-07) arrive in PR-13b.

CREATE TYPE pur.si_status AS ENUM ('DRAFT', 'MATCH_EXCEPTION', 'MATCHED', 'VOIDED', 'REVERSED');

-- ---------------------------------------------------------------------------------------------
-- Header. E-PR13-4: NCF (B + 10 digits) or e-NCF (E + 12 digits), unique per supplier unless voided (SI-04, SI-05).
-- E-PR13-5: due_date >= doc_date. §11.4 invalid combinations are CHECKs.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE pur.supplier_invoice (
  si_id                   uuid                  NOT NULL,
  company_id              uuid                  NOT NULL,
  party_id                uuid                  NOT NULL,
  supplier_fiscal_number  text                  NOT NULL,
  doc_date                date                  NOT NULL,
  due_date                date                  NOT NULL,
  document_status         pur.si_status         NOT NULL,
  accounting_status       fin.accounting_status NOT NULL,
  tax_determination_id    uuid,
  total_amount            numeric(19,4)         NOT NULL,
  created_by              uuid                  NOT NULL,
  exception_approved_by   uuid,
  posting_event_id        uuid,
  version                 bigint                NOT NULL,
  CONSTRAINT supplier_invoice_pk PRIMARY KEY (si_id),
  CONSTRAINT supplier_invoice_company_uq UNIQUE (company_id, si_id),
  CONSTRAINT supplier_invoice_party_fk FOREIGN KEY (company_id, party_id) REFERENCES md.party (company_id, party_id),
  CONSTRAINT supplier_invoice_determination_fk FOREIGN KEY (company_id, tax_determination_id) REFERENCES tax.tax_determination (company_id, determination_id),
  CONSTRAINT supplier_invoice_event_fk FOREIGN KEY (company_id, posting_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT supplier_invoice_created_by_fk FOREIGN KEY (created_by) REFERENCES iam.user (user_id),
  CONSTRAINT supplier_invoice_exception_by_fk FOREIGN KEY (exception_approved_by) REFERENCES iam.user (user_id),
  CONSTRAINT supplier_invoice_ncf_format CHECK (supplier_fiscal_number ~ '^(B[0-9]{10}|E[0-9]{12})$'),
  CONSTRAINT supplier_invoice_dates CHECK (due_date >= doc_date),
  CONSTRAINT supplier_invoice_total CHECK (total_amount > 0),
  CONSTRAINT supplier_invoice_exception_approver CHECK (exception_approved_by IS NULL OR exception_approved_by <> created_by),
  CONSTRAINT supplier_invoice_posted_evidence CHECK (accounting_status <> 'POSTED' OR (posting_event_id IS NOT NULL AND tax_determination_id IS NOT NULL)),
  CONSTRAINT supplier_invoice_voided_not_posted CHECK (document_status <> 'VOIDED' OR accounting_status = 'NOT_POSTED'),
  CONSTRAINT supplier_invoice_unmatched_not_posted CHECK (document_status NOT IN ('DRAFT', 'MATCH_EXCEPTION') OR accounting_status = 'NOT_POSTED'),
  CONSTRAINT supplier_invoice_blocked_only_matched CHECK (accounting_status <> 'POSTING_BLOCKED' OR document_status = 'MATCHED'),
  CONSTRAINT supplier_invoice_reversal_pair CHECK ((document_status = 'REVERSED') = (accounting_status = 'REVERSED')),
  CONSTRAINT supplier_invoice_version_positive CHECK (version >= 1)
);
CREATE UNIQUE INDEX si_fiscal_uq ON pur.supplier_invoice (company_id, party_id, supplier_fiscal_number) WHERE document_status <> 'VOIDED';

-- ---------------------------------------------------------------------------------------------
-- Lines: E-10 only INVENTORY_PO; quantity in the PO line UOM; net = round(qty × price, 2). Written only while DRAFT.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE pur.supplier_invoice_line (
  si_line_id  uuid          NOT NULL,
  company_id  uuid          NOT NULL,
  si_id       uuid          NOT NULL,
  line_no     integer       NOT NULL,
  line_kind   text          NOT NULL,
  po_line_id  uuid          NOT NULL,
  qty         numeric(18,6) NOT NULL,
  unit_price  numeric(19,6) NOT NULL,
  net_amount  numeric(19,4) NOT NULL,
  CONSTRAINT supplier_invoice_line_pk PRIMARY KEY (si_line_id),
  CONSTRAINT supplier_invoice_line_company_uq UNIQUE (company_id, si_line_id),
  CONSTRAINT supplier_invoice_line_po_line_uq UNIQUE (si_id, po_line_id),
  CONSTRAINT supplier_invoice_line_no_uq UNIQUE (si_id, line_no),
  CONSTRAINT supplier_invoice_line_header_fk FOREIGN KEY (company_id, si_id) REFERENCES pur.supplier_invoice (company_id, si_id),
  CONSTRAINT supplier_invoice_line_po_line_fk FOREIGN KEY (company_id, po_line_id) REFERENCES pur.purchase_order_line (company_id, po_line_id),
  CONSTRAINT supplier_invoice_line_kind CHECK (line_kind = 'INVENTORY_PO'),
  CONSTRAINT supplier_invoice_line_qty CHECK (qty > 0),
  CONSTRAINT supplier_invoice_line_price CHECK (unit_price > 0),
  CONSTRAINT supplier_invoice_line_net CHECK (net_amount = round(qty * unit_price, 2)),
  CONSTRAINT supplier_invoice_line_no CHECK (line_no >= 1)
);

-- A line bills a PO line of the invoice's own supplier, and only a DRAFT invoice gets lines.
CREATE FUNCTION pur.supplier_invoice_line_before_insert() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pur.supplier_invoice si
    JOIN pur.purchase_order_line pol ON pol.po_line_id = NEW.po_line_id
    JOIN pur.purchase_order po ON po.po_id = pol.po_id AND po.party_id = si.party_id
    WHERE si.si_id = NEW.si_id AND si.document_status = 'DRAFT') THEN
    RAISE EXCEPTION 'pur.supplier_invoice_line: PO line % is not of the invoice supplier, or the invoice is not DRAFT', NEW.po_line_id;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER supplier_invoice_line_before_insert BEFORE INSERT ON pur.supplier_invoice_line FOR EACH ROW EXECUTE FUNCTION pur.supplier_invoice_line_before_insert();
CREATE TRIGGER supplier_invoice_line_append_only BEFORE UPDATE OR DELETE ON pur.supplier_invoice_line FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER supplier_invoice_line_no_truncate BEFORE TRUNCATE ON pur.supplier_invoice_line FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- Latest match evaluation per line (re-match overwrites it; each evaluation is also an event).
-- E-PR13-1: qty_exceeds (qty > available) is never approvable.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE pur.match_result (
  si_line_id                uuid          NOT NULL,
  company_id                uuid          NOT NULL,
  qty_available_to_invoice  numeric(18,6) NOT NULL,
  qty_diff                  numeric(18,6) NOT NULL,
  price_diff                numeric(19,6) NOT NULL,
  amount_diff               numeric(19,4) NOT NULL,
  qty_exceeds               boolean       NOT NULL,
  within_tolerance          boolean       NOT NULL,
  policy_version_id         uuid          NOT NULL,
  evaluated_at              timestamptz   NOT NULL,
  CONSTRAINT match_result_pk PRIMARY KEY (si_line_id),
  CONSTRAINT match_result_line_fk FOREIGN KEY (company_id, si_line_id) REFERENCES pur.supplier_invoice_line (company_id, si_line_id),
  CONSTRAINT match_result_policy_fk FOREIGN KEY (company_id, policy_version_id) REFERENCES acc.accounting_policy_version (company_id, policy_version_id),
  CONSTRAINT match_result_qty_consistent CHECK (qty_exceeds = (qty_diff > 0)),
  CONSTRAINT match_result_exceeds_outside CHECK (NOT (qty_exceeds AND within_tolerance))
);
CREATE TRIGGER match_result_no_delete BEFORE DELETE ON pur.match_result FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- AP document (§9.2), written by posting in PR-13b.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fin.ap_document (
  ap_doc_id        uuid          NOT NULL,
  company_id       uuid          NOT NULL,
  party_id         uuid          NOT NULL,
  doc_type         text          NOT NULL,
  source_doc_id    uuid          NOT NULL,
  doc_date         date          NOT NULL,
  due_date         date          NOT NULL,
  original_amount  numeric(19,4) NOT NULL,
  open_amount      numeric(19,4) NOT NULL,
  version          bigint        NOT NULL,
  CONSTRAINT ap_document_pk PRIMARY KEY (ap_doc_id),
  CONSTRAINT ap_document_company_uq UNIQUE (company_id, ap_doc_id),
  CONSTRAINT ap_document_source_uq UNIQUE (source_doc_id),
  CONSTRAINT ap_document_party_fk FOREIGN KEY (company_id, party_id) REFERENCES md.party (company_id, party_id),
  CONSTRAINT ap_document_invoice_fk FOREIGN KEY (company_id, source_doc_id) REFERENCES pur.supplier_invoice (company_id, si_id),
  CONSTRAINT ap_document_type CHECK (doc_type = 'SUPPLIER_INVOICE'),
  CONSTRAINT ap_document_original CHECK (original_amount > 0),
  CONSTRAINT ap_document_open CHECK (open_amount >= 0 AND open_amount <= original_amount),
  CONSTRAINT ap_document_dates CHECK (due_date >= doc_date),
  CONSTRAINT ap_document_version_positive CHECK (version >= 1)
);

-- ---------------------------------------------------------------------------------------------
-- Header guard: identity immutable, version +1, §11.4 transitions, approval data set once, never deleted.
-- ---------------------------------------------------------------------------------------------
CREATE FUNCTION pur.supplier_invoice_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'UPDATE' THEN
    RAISE EXCEPTION 'pur.supplier_invoice rows cannot be deleted; void the invoice';
  END IF;
  IF ROW(NEW.si_id, NEW.company_id, NEW.party_id, NEW.supplier_fiscal_number, NEW.doc_date, NEW.due_date, NEW.total_amount, NEW.created_by)
     IS DISTINCT FROM ROW(OLD.si_id, OLD.company_id, OLD.party_id, OLD.supplier_fiscal_number, OLD.doc_date, OLD.due_date, OLD.total_amount, OLD.created_by) THEN
    RAISE EXCEPTION 'pur.supplier_invoice: identity columns are immutable';
  END IF;
  IF NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'pur.supplier_invoice: version must increase by exactly 1';
  END IF;
  IF NEW.document_status IS DISTINCT FROM OLD.document_status AND NOT (
       (OLD.document_status = 'DRAFT' AND NEW.document_status IN ('MATCHED', 'MATCH_EXCEPTION', 'VOIDED')) OR
       (OLD.document_status = 'MATCH_EXCEPTION' AND NEW.document_status IN ('MATCHED', 'VOIDED')) OR
       (OLD.document_status = 'MATCHED' AND NEW.document_status IN ('VOIDED', 'REVERSED'))) THEN
    RAISE EXCEPTION 'pur.supplier_invoice: transition % → % is not allowed (§11.4)', OLD.document_status, NEW.document_status;
  END IF;
  IF OLD.exception_approved_by IS NOT NULL AND NEW.exception_approved_by IS DISTINCT FROM OLD.exception_approved_by THEN
    RAISE EXCEPTION 'pur.supplier_invoice: exception approval is immutable';
  END IF;
  IF OLD.posting_event_id IS NOT NULL AND NEW.posting_event_id IS DISTINCT FROM OLD.posting_event_id THEN
    RAISE EXCEPTION 'pur.supplier_invoice: the posting event is immutable';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER supplier_invoice_guard BEFORE UPDATE OR DELETE ON pur.supplier_invoice FOR EACH ROW EXECUTE FUNCTION pur.supplier_invoice_guard();
CREATE TRIGGER supplier_invoice_no_truncate BEFORE TRUNCATE ON pur.supplier_invoice FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ADR-027 (history row per status) and E-11/K-25 (POSTED ⇔ an unreversed AUTO journal of the posting event).
CREATE FUNCTION pur.supplier_invoice_evidence() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.accounting_status = 'POSTED' AND NOT EXISTS (
    SELECT 1 FROM fin.gl_journal j
    WHERE j.company_id = NEW.company_id AND j.source_event_id = NEW.posting_event_id AND j.journal_type = 'AUTO'
      AND NOT EXISTS (SELECT 1 FROM fin.gl_journal r WHERE r.reverses_journal_id = j.journal_id)) THEN
    RAISE EXCEPTION 'pur.supplier_invoice %: accounting_status POSTED without its journal (K-25)', NEW.si_id;
  END IF;
  IF (TG_OP = 'INSERT' OR NEW.document_status IS DISTINCT FROM OLD.document_status) AND NOT EXISTS (
    SELECT 1 FROM core.state_history
    WHERE aggregate_type = 'SupplierInvoice' AND aggregate_id = NEW.si_id AND to_state = NEW.document_status::text
      AND xmin = pg_current_xact_id()::xid) THEN
    RAISE EXCEPTION 'pur.supplier_invoice %: status % without its state_history row (ADR-027)', NEW.si_id, NEW.document_status;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER supplier_invoice_evidence_on_insert AFTER INSERT ON pur.supplier_invoice
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION pur.supplier_invoice_evidence();
CREATE CONSTRAINT TRIGGER supplier_invoice_evidence_on_change AFTER UPDATE ON pur.supplier_invoice
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
  WHEN (OLD.document_status IS DISTINCT FROM NEW.document_status OR OLD.accounting_status IS DISTINCT FROM NEW.accounting_status)
  EXECUTE FUNCTION pur.supplier_invoice_evidence();

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['pur.supplier_invoice', 'pur.supplier_invoice_line', 'pur.match_result', 'fin.ap_document'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON pur.supplier_invoice, pur.supplier_invoice_line, pur.match_result, fin.ap_document TO rochell_app;
GRANT UPDATE (document_status, accounting_status, tax_determination_id, exception_approved_by, posting_event_id, version) ON pur.supplier_invoice TO rochell_app;
GRANT UPDATE (qty_available_to_invoice, qty_diff, price_diff, amount_diff, qty_exceeds, within_tolerance, policy_version_id, evaluated_at) ON pur.match_result TO rochell_app;
GRANT UPDATE (open_amount, version) ON fin.ap_document TO rochell_app;
