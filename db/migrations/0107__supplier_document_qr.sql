-- OCR1-03 · the printed e-CF's QR on captured supplier documents (approved errata E-OCR1-03-6/7, E-OCR1-02-8).
--   - qr_url: the DGII stamp link read from the QR («Verificar en la DGII»), only the DGII's hosts;
--   - qr_total_amount: the total the QR states, kept to compare with the XML's once it arrives.

ALTER TABLE pur.supplier_document
  ADD COLUMN qr_url text,
  ADD COLUMN qr_total_amount numeric(19,4),
  ADD CONSTRAINT supplier_document_qr CHECK (
    (qr_url IS NULL OR (length(qr_url) <= 1000 AND qr_url ~ '^https://(ecf|fc)\.dgii\.gov\.do/'))
    AND (qr_total_amount IS NULL OR qr_total_amount >= 0)
    AND ((qr_url IS NULL) = (qr_scanned_at IS NULL)));

GRANT UPDATE (qr_url, qr_total_amount) ON pur.supplier_document TO rochell_app;
