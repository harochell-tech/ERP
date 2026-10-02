-- MAIL-02 · The documents sent by e-mail (approved errata E-MAIL-3, E-MAIL-6, E-MAIL-01-2, 7, 8, 10): quote, proforma, delivery
-- note, statement of account and the customer's open invoices by age. Invoices wait for the e-CF's QR (VS#4).
ALTER TABLE core.mail_message
  ADD CONSTRAINT mail_message_document_type_known CHECK (document_type IN ('QUOTE', 'PROFORMA', 'DELIVERY', 'STATEMENT', 'AR_AGING'));

-- E-MAIL-01-8: one permission per document, held by who works that document; the statement and the aging are Cobros's. A FAILED
-- message is retried by any of them (E-MAIL-01-10): the retry sends again exactly what was queued, to the same recipients.
-- SUPERADMIN receives every new permission through its trigger (E-ADM-2).
INSERT INTO iam.permission (permission_code, access) VALUES
  ('quote:email', 'WRITE'), ('proforma:email', 'WRITE'), ('delivery:email', 'WRITE'), ('statement:email', 'WRITE'), ('mail:retry', 'WRITE');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES ('VENDEDOR', 'quote:email'), ('FACTURACION', 'proforma:email'), ('FACTURACION', 'delivery:email'), ('DESPACHO', 'delivery:email'), ('COBROS', 'statement:email'),
             ('VENDEDOR', 'mail:retry'), ('FACTURACION', 'mail:retry'), ('DESPACHO', 'mail:retry'), ('COBROS', 'mail:retry')) AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;
