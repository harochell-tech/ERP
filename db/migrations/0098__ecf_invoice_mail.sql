-- VS4-05 · invoices by e-mail with their e-CF (approved errata E-VS4-05-1…6).
--   - core.mail_message gains one extra attachment (the signed XML of an e-CF), written once with the message: the application
--     may insert it, never change it;
--   - invoice:email for Facturación and Cobros (E-VS4-05-2).

ALTER TABLE core.mail_message
  DROP CONSTRAINT mail_message_document_type_known,
  ADD CONSTRAINT mail_message_document_type_known CHECK (document_type IN ('QUOTE', 'PROFORMA', 'DELIVERY', 'STATEMENT', 'AR_AGING', 'INVOICE')),
  ADD COLUMN attachment_name  text,
  ADD COLUMN attachment       bytea,
  ADD COLUMN attachment_type  text,
  ADD CONSTRAINT mail_message_attachment CHECK (
    (attachment IS NULL) = (attachment_name IS NULL) AND (attachment IS NULL) = (attachment_type IS NULL)
    AND (attachment_name IS NULL OR attachment_name ~ '^[A-Za-z0-9._-]{1,120}$')
    AND (attachment IS NULL OR octet_length(attachment) BETWEEN 1 AND 10485760)
    AND (attachment_type IS NULL OR attachment_type IN ('application/xml', 'application/pdf')));

INSERT INTO iam.permission (permission_code, access) VALUES ('invoice:email', 'WRITE');
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, 'invoice:email' FROM iam.role r WHERE r.code IN ('FACTURACION', 'COBROS');
