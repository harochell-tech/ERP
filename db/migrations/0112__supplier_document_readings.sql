-- OCR1-04 · photos and scans of supplier invoices read by AI (approved errata E-OCR-5/6/7, E-OCR1-01-10, E-OCR1-04-1…4).
--   - pur.supplier_document_reading: every reading asked of the AI — the file's SHA-256 (the same file is never read twice), outcome,
--     what it read, the model and its tokens, who and when; append-only. The monthly limit counts them.
--   - PURCHASING `ocr_monthly_readings`: readings allowed per calendar month (E-OCR1-04-3).

CREATE TABLE pur.supplier_document_reading (
  reading_id     uuid        NOT NULL,
  company_id     uuid        NOT NULL,
  file_sha256    bytea       NOT NULL,
  content_type   text        NOT NULL,
  size_bytes     integer     NOT NULL,
  outcome        text        NOT NULL,
  result         jsonb,
  message        text,
  model          text        NOT NULL,
  input_tokens   integer,
  output_tokens  integer,
  read_by        uuid        NOT NULL,
  read_at        timestamptz NOT NULL,
  CONSTRAINT supplier_document_reading_pk PRIMARY KEY (reading_id),
  CONSTRAINT supplier_document_reading_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT supplier_document_reading_by_fk FOREIGN KEY (read_by) REFERENCES iam.user (user_id),
  CONSTRAINT supplier_document_reading_outcome CHECK (outcome IN ('READ', 'UNREADABLE')),
  CONSTRAINT supplier_document_reading_result CHECK ((outcome = 'READ') = (result IS NOT NULL)),
  CONSTRAINT supplier_document_reading_type CHECK (content_type IN ('image/jpeg', 'image/png', 'application/pdf')),
  CONSTRAINT supplier_document_reading_size CHECK (size_bytes BETWEEN 1 AND 10485760 AND length(file_sha256) = 32),
  CONSTRAINT supplier_document_reading_texts CHECK (length(model) BETWEEN 1 AND 100 AND (message IS NULL OR length(message) <= 2000))
);
CREATE INDEX supplier_document_reading_file ON pur.supplier_document_reading (company_id, file_sha256);
CREATE INDEX supplier_document_reading_month ON pur.supplier_document_reading (company_id, read_at);
CREATE TRIGGER supplier_document_reading_append_only BEFORE UPDATE OR DELETE ON pur.supplier_document_reading FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

-- A count (readings) is a unit of its own on the policy screens.
ALTER TABLE acc.policy_parameter_definition DROP CONSTRAINT policy_parameter_definition_unit,
  ADD CONSTRAINT policy_parameter_definition_unit CHECK (unit IS NULL OR unit IN ('PERCENT', 'AMOUNT', 'DAYS', 'HOURS', 'MINUTES', 'OPTION', 'COUNT'));

INSERT INTO acc.policy_parameter_definition (param_code, policy_code, value_type, min_value, max_value, allowed_values, description, label, unit, example, affects) VALUES
  ('ocr_monthly_readings', 'PURCHASING', 'INTEGER', 0, 100000, NULL,
   'Lecturas de fotos o PDF de facturas por IA permitidas por mes calendario (E-OCR1-04-3)',
   'Lecturas por IA al mes', 'COUNT', '500 lecturas',
   'Cuántas fotos o PDF de facturas de proveedor se pueden leer con IA en un mes. Al llegar al tope, se capturan a mano.');

ALTER TABLE pur.supplier_document_reading ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON pur.supplier_document_reading
  USING (company_id = nullif(current_setting('app.company_id', true), '')::uuid)
  WITH CHECK (company_id = nullif(current_setting('app.company_id', true), '')::uuid);

GRANT SELECT, INSERT ON pur.supplier_document_reading TO rochell_app;
