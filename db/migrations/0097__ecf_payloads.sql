-- VS4-03 · e-CF payloads from invoices and credit notes (approved errata E-VS4-03-1…11).
--   - md.company gains the e-CF issuer's address (required by Alanube, DireccionEmisor ≤ 100), trade name, phone and e-mail,
--     edited on Configuración › Empresa (E-VS4-03-2). Without an address the gateway refuses to issue.

ALTER TABLE md.company
  ADD COLUMN address     text,
  ADD COLUMN trade_name  text,
  ADD COLUMN phone       text,
  ADD COLUMN email       text,
  ADD CONSTRAINT company_address CHECK (address IS NULL OR length(btrim(address)) BETWEEN 1 AND 100),
  ADD CONSTRAINT company_trade_name CHECK (trade_name IS NULL OR length(btrim(trade_name)) BETWEEN 1 AND 150),
  ADD CONSTRAINT company_phone CHECK (phone IS NULL OR phone ~ '^[0-9]{3}-[0-9]{3}-[0-9]{4}$'),
  ADD CONSTRAINT company_email CHECK (email IS NULL OR (length(email) <= 80 AND email ~ '^[^@\s]+@[^@\s]+\.[^@\s]+$'));
GRANT UPDATE (address, trade_name, phone, email) ON md.company TO rochell_app;
