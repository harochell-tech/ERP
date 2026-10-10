-- OCR1-01 · supplier documents captured from a received e-CF, the printed e-CF's QR or a photo read by AI (approved errata E-OCR-1…8,
-- E-OCR1-01-1…10).
--   - pur.supplier_document (E-OCR1-01-1/5): one captured document per issuer RNC and fiscal number while live; the sources add to it.
--     It is not a supplier invoice: «pasar a factura» registers one through the usual commands and links it here.
--   - pur.supplier_document_line: the lines read from the XML or by AI, insert-only; the XML's lines win over the AI's.
--   - pur.supplier_document_file (E-OCR1-01-7): the XML in the database, the photo / scan / PDF in the evidence store (B2), append-only.
--   - The commercial response to the DGII (E-OCR-3, E-OCR1-01-2/3): NOT_DECLARED → ACCEPTED / REJECTED once, never changed.
--   - pur.received_document_sync (E-OCR1-01-9): where the hourly reading of Alanube's received documents is.
--   - tax.ecf_call records the reception calls to Alanube too.
--   - supplier_document:capture and supplier_document:respond (E-OCR1-01-8; 155 permissions).

-- ---------------------------------------------------------------------------------------------
-- The captured document. The fiscal number is an NCF (B + 10 digits) or an e-NCF (E + 12 digits), as on supplier invoices; its type
-- is the two digits after the letter. Header fields stay editable while it waits to be registered: the XML corrects what the AI read.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE pur.supplier_document (
  supplier_document_id  uuid          NOT NULL,
  company_id            uuid          NOT NULL,
  issuer_rnc            text          NOT NULL,
  issuer_name           text,
  buyer_rnc             text,
  fiscal_number         text          NOT NULL,
  doc_date              date,
  total_amount          numeric(19,4),
  itbis_amount          numeric(19,4),
  security_code         text,
  signature_at          timestamptz,
  party_id              uuid,
  status                text          NOT NULL,
  si_id                 uuid,
  discard_reason        text,
  provider_id           text,
  received_status       text,
  commercial_response   text          NOT NULL DEFAULT 'NOT_DECLARED',
  response_reason       text,
  responded_by          uuid,
  responded_at          timestamptz,
  response_sent_at      timestamptz,
  ai_fields             text[]        NOT NULL DEFAULT '{}',
  qr_scanned_at         timestamptz,
  created_by            uuid          NOT NULL,
  created_at            timestamptz   NOT NULL,
  version               bigint        NOT NULL,
  CONSTRAINT supplier_document_pk PRIMARY KEY (supplier_document_id),
  CONSTRAINT supplier_document_company_uq UNIQUE (company_id, supplier_document_id),
  CONSTRAINT supplier_document_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT supplier_document_party_fk FOREIGN KEY (company_id, party_id) REFERENCES md.party (company_id, party_id),
  CONSTRAINT supplier_document_invoice_fk FOREIGN KEY (company_id, si_id) REFERENCES pur.supplier_invoice (company_id, si_id),
  CONSTRAINT supplier_document_created_by_fk FOREIGN KEY (created_by) REFERENCES iam.user (user_id),
  CONSTRAINT supplier_document_responded_by_fk FOREIGN KEY (responded_by) REFERENCES iam.user (user_id),
  CONSTRAINT supplier_document_issuer_rnc CHECK (issuer_rnc ~ '^([0-9]{9}|[0-9]{11})$'),
  CONSTRAINT supplier_document_buyer_rnc CHECK (buyer_rnc IS NULL OR buyer_rnc ~ '^([0-9]{9}|[0-9]{11})$'),
  CONSTRAINT supplier_document_fiscal_number CHECK (fiscal_number ~ '^(B[0-9]{10}|E[0-9]{12})$'),
  CONSTRAINT supplier_document_names CHECK ((issuer_name IS NULL OR length(btrim(issuer_name)) BETWEEN 1 AND 250)
    AND (security_code IS NULL OR length(security_code) BETWEEN 1 AND 20) AND (provider_id IS NULL OR length(provider_id) BETWEEN 1 AND 100)),
  CONSTRAINT supplier_document_amounts CHECK ((total_amount IS NULL OR total_amount >= 0) AND (itbis_amount IS NULL OR itbis_amount >= 0)),
  CONSTRAINT supplier_document_status CHECK (status IN ('CAPTURED', 'REGISTERED', 'DISCARDED')),
  CONSTRAINT supplier_document_registered CHECK ((status = 'REGISTERED') = (si_id IS NOT NULL)),
  CONSTRAINT supplier_document_discarded CHECK ((status = 'DISCARDED') = (discard_reason IS NOT NULL AND length(btrim(discard_reason)) >= 3)),
  CONSTRAINT supplier_document_received_status CHECK (received_status IS NULL OR received_status IN ('RECEIVED', 'NOT_RECEIVED')),
  -- Only an e-CF that reached Alanube can be answered to the DGII; a rejection carries its reason.
  CONSTRAINT supplier_document_response CHECK (commercial_response IN ('NOT_DECLARED', 'ACCEPTED', 'REJECTED')
    AND (commercial_response = 'NOT_DECLARED') = (responded_by IS NULL AND responded_at IS NULL)
    AND (commercial_response = 'NOT_DECLARED' OR provider_id IS NOT NULL)
    AND (commercial_response <> 'REJECTED' OR (response_reason IS NOT NULL AND length(btrim(response_reason)) BETWEEN 3 AND 250))
    AND (response_sent_at IS NULL OR commercial_response <> 'NOT_DECLARED')),
  CONSTRAINT supplier_document_ai_fields CHECK (ai_fields <@ ARRAY['issuer_rnc', 'issuer_name', 'buyer_rnc', 'fiscal_number', 'doc_date', 'total_amount',
    'itbis_amount', 'lines']::text[]),
  CONSTRAINT supplier_document_version_positive CHECK (version >= 1)
);
-- E-OCR1-01-5: one live document per issuer and fiscal number; Alanube's id once per company.
CREATE UNIQUE INDEX supplier_document_one_live ON pur.supplier_document (company_id, issuer_rnc, fiscal_number) WHERE status <> 'DISCARDED';
CREATE UNIQUE INDEX supplier_document_provider_uq ON pur.supplier_document (company_id, provider_id) WHERE provider_id IS NOT NULL;
-- E-OCR1-01-1: an invoice comes from one document at most.
CREATE UNIQUE INDEX supplier_document_invoice_uq ON pur.supplier_document (company_id, si_id) WHERE si_id IS NOT NULL;
CREATE INDEX supplier_document_inbox ON pur.supplier_document (company_id, status, created_at DESC);

CREATE FUNCTION pur.supplier_document_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'pur.supplier_document rows cannot be deleted';
  END IF;
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'CAPTURED' OR NEW.version <> 1 THEN
      RAISE EXCEPTION 'pur.supplier_document: a document is captured CAPTURED, version 1';
    END IF;
    RETURN NEW;
  END IF;
  IF ROW(NEW.supplier_document_id, NEW.company_id, NEW.issuer_rnc, NEW.fiscal_number, NEW.created_by, NEW.created_at)
     IS DISTINCT FROM ROW(OLD.supplier_document_id, OLD.company_id, OLD.issuer_rnc, OLD.fiscal_number, OLD.created_by, OLD.created_at) THEN
    RAISE EXCEPTION 'pur.supplier_document: the issuer and the fiscal number never change; a wrong one is discarded and captured again';
  END IF;
  IF OLD.provider_id IS NOT NULL AND NEW.provider_id IS DISTINCT FROM OLD.provider_id THEN
    RAISE EXCEPTION 'pur.supplier_document: Alanube''s id never changes once known';
  END IF;
  -- CAPTURED → REGISTERED / DISCARDED; REGISTERED → CAPTURED when its invoice is voided; DISCARDED is final.
  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
       (OLD.status = 'CAPTURED' AND NEW.status IN ('REGISTERED', 'DISCARDED')) OR
       (OLD.status = 'REGISTERED' AND NEW.status = 'CAPTURED')) THEN
    RAISE EXCEPTION 'pur.supplier_document: transition % → % is not allowed', OLD.status, NEW.status;
  END IF;
  IF OLD.status <> 'CAPTURED' AND ROW(NEW.issuer_name, NEW.buyer_rnc, NEW.doc_date, NEW.total_amount, NEW.itbis_amount, NEW.party_id)
     IS DISTINCT FROM ROW(OLD.issuer_name, OLD.buyer_rnc, OLD.doc_date, OLD.total_amount, OLD.itbis_amount, OLD.party_id) THEN
    RAISE EXCEPTION 'pur.supplier_document: only a document waiting to be registered changes its header';
  END IF;
  IF OLD.commercial_response <> 'NOT_DECLARED' AND ROW(NEW.commercial_response, NEW.response_reason, NEW.responded_by, NEW.responded_at)
     IS DISTINCT FROM ROW(OLD.commercial_response, OLD.response_reason, OLD.responded_by, OLD.responded_at) THEN
    RAISE EXCEPTION 'pur.supplier_document: the commercial response to the DGII is given once (E-OCR-3)';
  END IF;
  IF OLD.response_sent_at IS NOT NULL AND NEW.response_sent_at IS DISTINCT FROM OLD.response_sent_at THEN
    RAISE EXCEPTION 'pur.supplier_document: the commercial response was already sent';
  END IF;
  IF NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'pur.supplier_document: version must increase by exactly 1';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER supplier_document_guard BEFORE INSERT OR UPDATE OR DELETE ON pur.supplier_document FOR EACH ROW EXECUTE FUNCTION pur.supplier_document_guard();
CREATE CONSTRAINT TRIGGER supplier_document_evidence_on_insert AFTER INSERT ON pur.supplier_document
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('SupplierDocument', 'supplier_document_id');
CREATE CONSTRAINT TRIGGER supplier_document_evidence_on_change AFTER UPDATE ON pur.supplier_document
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('SupplierDocument', 'supplier_document_id');

-- ---------------------------------------------------------------------------------------------
-- Lines, insert-only. Each set is tagged with its source; the XML's set wins over the AI's (E-OCR-1). Amounts as read: Core
-- recomputes nothing here, the checks of E-OCR-6 compare them when shown.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE pur.supplier_document_line (
  supplier_document_id  uuid          NOT NULL,
  company_id            uuid          NOT NULL,
  source                text          NOT NULL,
  line_no               integer       NOT NULL,
  item_code             text,
  description           text          NOT NULL,
  quantity              numeric(18,6) NOT NULL,
  unit_code             text,
  unit_price            numeric(19,4) NOT NULL,
  itbis_amount          numeric(19,4),
  amount                numeric(19,4) NOT NULL,
  billing_indicator     integer,
  added_at              timestamptz   NOT NULL,
  CONSTRAINT supplier_document_line_pk PRIMARY KEY (supplier_document_id, source, line_no),
  CONSTRAINT supplier_document_line_document_fk FOREIGN KEY (company_id, supplier_document_id)
    REFERENCES pur.supplier_document (company_id, supplier_document_id),
  CONSTRAINT supplier_document_line_source CHECK (source IN ('XML', 'AI')),
  CONSTRAINT supplier_document_line_texts CHECK (line_no >= 1 AND length(btrim(description)) BETWEEN 1 AND 500
    AND (item_code IS NULL OR length(item_code) <= 50) AND (unit_code IS NULL OR length(unit_code) <= 20)),
  CONSTRAINT supplier_document_line_amounts CHECK (quantity > 0 AND unit_price >= 0 AND amount >= 0 AND (itbis_amount IS NULL OR itbis_amount >= 0)),
  CONSTRAINT supplier_document_line_indicator CHECK (billing_indicator IS NULL OR billing_indicator BETWEEN 0 AND 4)
);
CREATE TRIGGER supplier_document_line_append_only BEFORE UPDATE OR DELETE ON pur.supplier_document_line FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- E-OCR1-01-7: files. The XML lives in the database (and its daily backups); a photo, scan or PDF in the evidence store, ≤ 10 MB.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE pur.supplier_document_file (
  file_id               uuid          NOT NULL,
  company_id            uuid          NOT NULL,
  supplier_document_id  uuid          NOT NULL,
  kind                  text          NOT NULL,
  content_type          text          NOT NULL,
  content               bytea,
  storage_key           text,
  size_bytes            integer       NOT NULL,
  sha256                bytea         NOT NULL,
  added_by              uuid          NOT NULL,
  added_at              timestamptz   NOT NULL,
  CONSTRAINT supplier_document_file_pk PRIMARY KEY (file_id),
  CONSTRAINT supplier_document_file_document_fk FOREIGN KEY (company_id, supplier_document_id)
    REFERENCES pur.supplier_document (company_id, supplier_document_id),
  CONSTRAINT supplier_document_file_added_by_fk FOREIGN KEY (added_by) REFERENCES iam.user (user_id),
  CONSTRAINT supplier_document_file_kind CHECK (kind IN ('XML', 'IMAGE', 'PDF')),
  CONSTRAINT supplier_document_file_type CHECK ((kind = 'XML' AND content_type IN ('application/xml', 'text/xml'))
    OR (kind = 'IMAGE' AND content_type IN ('image/jpeg', 'image/png')) OR (kind = 'PDF' AND content_type = 'application/pdf')),
  CONSTRAINT supplier_document_file_where CHECK ((kind = 'XML') = (content IS NOT NULL AND storage_key IS NULL)
    AND (kind = 'XML' OR (storage_key IS NOT NULL AND length(storage_key) BETWEEN 1 AND 500))),
  CONSTRAINT supplier_document_file_size CHECK (size_bytes BETWEEN 1 AND 10485760 AND (content IS NULL OR octet_length(content) = size_bytes)
    AND length(sha256) = 32)
);
CREATE UNIQUE INDEX supplier_document_file_one_xml ON pur.supplier_document_file (supplier_document_id) WHERE kind = 'XML';
CREATE TRIGGER supplier_document_file_append_only BEFORE UPDATE OR DELETE ON pur.supplier_document_file FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER supplier_document_file_no_truncate BEFORE TRUNCATE ON pur.supplier_document_file FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- E-OCR1-01-9: the hourly reading of received documents: the first one goes back 30 days, the next ones from the last good one less a day.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE pur.received_document_sync (
  company_id        uuid        NOT NULL,
  last_success_at   timestamptz,
  last_attempt_at   timestamptz NOT NULL,
  last_error        text,
  CONSTRAINT received_document_sync_pk PRIMARY KEY (company_id),
  CONSTRAINT received_document_sync_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT received_document_sync_error CHECK (last_error IS NULL OR length(last_error) <= 2000)
);

-- Reception calls to Alanube are kept like the issuing ones (never the token).
ALTER TABLE tax.ecf_call DROP CONSTRAINT ecf_call_operation,
  ADD CONSTRAINT ecf_call_operation CHECK (operation IN ('SUBMIT', 'QUERY', 'CANCEL', 'WEBHOOK', 'DOWNLOAD', 'RECEIVED_LIST', 'RECEIVED_GET', 'COMMERCIAL_RESPONSE'));

-- ---------------------------------------------------------------------------------------------
-- E-OCR1-01-8: capture for Cuentas por pagar; the commercial response for Cuentas por pagar and the Contador, who also reads the inbox.
-- ---------------------------------------------------------------------------------------------
INSERT INTO iam.permission (permission_code, access) VALUES ('supplier_document:capture', 'WRITE'), ('supplier_document:respond', 'WRITE');
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES
  ('CUENTAS_POR_PAGAR', 'supplier_document:capture'), ('CUENTAS_POR_PAGAR', 'supplier_document:respond'),
  ('CONTADOR', 'supplier_document:respond'), ('CONTADOR', 'supplier_invoice:read')
) AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['pur.supplier_document', 'pur.supplier_document_line', 'pur.supplier_document_file', 'pur.received_document_sync'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON pur.supplier_document, pur.supplier_document_line, pur.supplier_document_file, pur.received_document_sync TO rochell_app;
GRANT UPDATE (issuer_name, buyer_rnc, doc_date, total_amount, itbis_amount, security_code, signature_at, party_id, status, si_id, discard_reason, provider_id,
  received_status, commercial_response, response_reason, responded_by, responded_at, response_sent_at, ai_fields, qr_scanned_at, version)
  ON pur.supplier_document TO rochell_app;
GRANT UPDATE (last_success_at, last_attempt_at, last_error) ON pur.received_document_sync TO rochell_app;
