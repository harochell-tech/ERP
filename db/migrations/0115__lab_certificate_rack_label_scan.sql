-- LAB1-03 · the lab certificate, the rack label and the scan at loading (approved errata E-LAB1-03-1…14).
--   - `qa.certificate` (E-LAB1-03-2…6): one per lot and break date (`CR-<field code>-<DDMMYY>`, then -2, -3…), a snapshot of what it
--     certifies, its public code for the QR, ISSUED → VOIDED (by Calidad, or by itself when one of its specimens is voided).
--   - `qa.certificate_test`: the specimens a certificate shows, so a voided specimen finds its certificates.
--   - The signer printed on the certificate is a lab parameter (E-LAB1-03-6).
--   - Two printable documents with editable formats: LAB_CERTIFICATE and RACK_LABEL (E-LAB1-03-1, 9).
--   - `log.delivery_line_scan` (E-LAB1-03-11/12): the lots scanned on each line at «Confirmar carga», in the order scanned.

-- ---------------------------------------------------------------------------------------------
-- E-LAB1-03-6: the signer, a parameter of the lab (name and title).
-- ---------------------------------------------------------------------------------------------
INSERT INTO qa.parameter_default (code, name, kind, number_value, text_value, min_value, max_value, whole, sort_order) VALUES
  ('CERT_SIGNER_NAME', 'Certificado: firmante', 'TEXT', NULL, 'Ing. Alexander Rochell', NULL, NULL, false, 140),
  ('CERT_SIGNER_TITLE', 'Certificado: cargo del firmante', 'TEXT', NULL, 'Director de Operaciones', NULL, NULL, false, 150);

-- ---------------------------------------------------------------------------------------------
-- E-LAB1-03-2…5: the certificate.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE qa.certificate (
  certificate_id   uuid          NOT NULL,
  company_id       uuid          NOT NULL,
  lot_id           uuid          NOT NULL,
  break_date       date          NOT NULL,
  seq              integer       NOT NULL,
  certificate_no   text          NOT NULL,
  delivery_id      uuid,
  public_code      text          NOT NULL,
  snapshot         jsonb         NOT NULL,
  status           text          NOT NULL,
  issued_by        uuid          NOT NULL,
  issued_at        timestamptz   NOT NULL,
  void_cause       text,
  void_reason      text,
  voided_by        uuid,
  voided_at        timestamptz,
  version          integer       NOT NULL,
  CONSTRAINT certificate_pk PRIMARY KEY (certificate_id),
  CONSTRAINT certificate_company_uq UNIQUE (company_id, certificate_id),
  CONSTRAINT certificate_no_uq UNIQUE (company_id, certificate_no),
  CONSTRAINT certificate_seq_uq UNIQUE (lot_id, break_date, seq),
  CONSTRAINT certificate_public_code_uq UNIQUE (public_code),
  CONSTRAINT certificate_lot_fk FOREIGN KEY (company_id, lot_id) REFERENCES mfg.fg_lot (company_id, lot_id),
  CONSTRAINT certificate_delivery_fk FOREIGN KEY (company_id, delivery_id) REFERENCES log.delivery (company_id, delivery_id),
  CONSTRAINT certificate_issued_by_fk FOREIGN KEY (issued_by) REFERENCES iam.user (user_id),
  CONSTRAINT certificate_voided_by_fk FOREIGN KEY (voided_by) REFERENCES iam.user (user_id),
  CONSTRAINT certificate_seq CHECK (seq >= 1 AND version >= 1),
  CONSTRAINT certificate_public_code CHECK (public_code ~ '^[a-z0-9]{24}$'),
  CONSTRAINT certificate_status CHECK (status IN ('ISSUED', 'VOIDED')),
  CONSTRAINT certificate_void CHECK ((status = 'VOIDED') = (void_cause IS NOT NULL) AND (void_cause IS NULL) = (void_reason IS NULL)
    AND (void_cause IS NULL) = (voided_at IS NULL) AND (void_cause IS NULL OR void_cause IN ('MANUAL', 'SPECIMEN_VOIDED'))
    AND (void_cause IS DISTINCT FROM 'MANUAL' OR voided_by IS NOT NULL) AND length(btrim(coalesce(void_reason, 'x'))) BETWEEN 1 AND 500)
);
CREATE INDEX certificate_lot ON qa.certificate (company_id, lot_id, break_date);

-- A certificate is a snapshot: only ISSUED → VOIDED, once.
CREATE FUNCTION qa.certificate_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'qa.certificate: a certificate is never deleted';
  END IF;
  IF ROW(NEW.certificate_id, NEW.company_id, NEW.lot_id, NEW.break_date, NEW.seq, NEW.certificate_no, NEW.delivery_id, NEW.public_code, NEW.snapshot,
         NEW.issued_by, NEW.issued_at)
     IS DISTINCT FROM ROW(OLD.certificate_id, OLD.company_id, OLD.lot_id, OLD.break_date, OLD.seq, OLD.certificate_no, OLD.delivery_id, OLD.public_code, OLD.snapshot,
         OLD.issued_by, OLD.issued_at)
     OR NOT (OLD.status = 'ISSUED' AND NEW.status = 'VOIDED') OR NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'qa.certificate: only ISSUED → VOIDED, nothing else changes';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER certificate_guard BEFORE UPDATE OR DELETE ON qa.certificate FOR EACH ROW EXECUTE FUNCTION qa.certificate_guard();

CREATE CONSTRAINT TRIGGER certificate_evidence_on_insert AFTER INSERT ON qa.certificate
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('LabCertificate', 'certificate_id');
CREATE CONSTRAINT TRIGGER certificate_evidence_on_change AFTER UPDATE ON qa.certificate
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('LabCertificate', 'certificate_id');

CREATE TABLE qa.certificate_test (
  certificate_id  uuid  NOT NULL,
  company_id      uuid  NOT NULL,
  test_id         uuid  NOT NULL,
  CONSTRAINT certificate_test_pk PRIMARY KEY (certificate_id, test_id),
  CONSTRAINT certificate_test_certificate_fk FOREIGN KEY (company_id, certificate_id) REFERENCES qa.certificate (company_id, certificate_id),
  CONSTRAINT certificate_test_test_fk FOREIGN KEY (test_id) REFERENCES qa.compression_test (test_id)
);
CREATE INDEX certificate_test_test ON qa.certificate_test (test_id);
CREATE TRIGGER certificate_test_append_only BEFORE UPDATE OR DELETE ON qa.certificate_test FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- E-LAB1-03-1, 9: the certificate and the rack label print with editable formats like the other documents.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE md.print_format
  DROP CONSTRAINT print_format_type,
  ADD CONSTRAINT print_format_type CHECK (document_type IN ('DELIVERY_NOTE', 'INVOICE', 'CREDIT_NOTE', 'QUOTE', 'PROFORMA', 'ORDER_PROFORMA', 'STATEMENT', 'AR_AGING',
                                                             'RECEIPT', 'CUSTOMER_REFUND', 'PURCHASE_ORDER', 'LAB_CERTIFICATE', 'RACK_LABEL'));

-- ---------------------------------------------------------------------------------------------
-- E-LAB1-03-11/12: the lots scanned on a delivery line at «Confirmar carga»; the gate-out takes them first, in this order.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE log.delivery_line_scan (
  delivery_line_id  uuid     NOT NULL,
  company_id        uuid     NOT NULL,
  seq               integer  NOT NULL,
  lot_id            uuid     NOT NULL,
  rack_no           integer,
  CONSTRAINT delivery_line_scan_pk PRIMARY KEY (delivery_line_id, seq),
  CONSTRAINT delivery_line_scan_lot_uq UNIQUE (delivery_line_id, lot_id),
  CONSTRAINT delivery_line_scan_line_fk FOREIGN KEY (company_id, delivery_line_id) REFERENCES log.delivery_line (company_id, delivery_line_id),
  CONSTRAINT delivery_line_scan_lot_fk FOREIGN KEY (company_id, lot_id) REFERENCES inv.lot (company_id, lot_id),
  CONSTRAINT delivery_line_scan_seq CHECK (seq >= 1 AND (rack_no IS NULL OR rack_no >= 1))
);
CREATE TRIGGER delivery_line_scan_append_only BEFORE UPDATE OR DELETE ON log.delivery_line_scan FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['qa.certificate', 'qa.certificate_test', 'log.delivery_line_scan'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON qa.certificate, qa.certificate_test, log.delivery_line_scan TO rochell_app;
GRANT UPDATE (status, void_cause, void_reason, voided_by, voided_at, version) ON qa.certificate TO rochell_app;
