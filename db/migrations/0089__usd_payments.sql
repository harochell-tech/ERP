-- USD1-05a · Payments in USD and USD bank accounts (approved errata E-USD1-05-1…6).
--   - a foreign supplier's bank account: SWIFT/BIC or bank name as its code, an account number or IBAN of letters and digits (E-USD1-05-6);
--   - a payment of USD payables keeps its USD amount and rate: from a USD account at the approved rate of its value date, from a peso
--     account at the bank's rate typed by Tesorería (E-USD1-05-3);
--   - P-41 «Pago en USD» (DRAFT until the Controller approves it): the payables at their carrying pesos with their USD, the bank, and the
--     realized exchange difference (E-USD1-05-4/5).

-- ---------------------------------------------------------------------------------------------
-- Supplier bank accounts (E-USD1-05-6): a local supplier's account number is digits; a foreign one's may carry letters (IBAN).
-- ---------------------------------------------------------------------------------------------
ALTER TABLE md.party_bank_account
  DROP CONSTRAINT party_bank_account_number_format,
  ADD CONSTRAINT party_bank_account_number_format CHECK (account_number ~ '^[A-Z0-9]{5,34}$');

CREATE FUNCTION md.party_bank_account_number_kind() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF (SELECT party_kind FROM md.party WHERE party_id = NEW.party_id) <> 'FOREIGN' AND NEW.account_number !~ '^[0-9]{5,30}$' THEN
    RAISE EXCEPTION 'md.party_bank_account: a local supplier''s account number has 5 to 30 digits';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER party_bank_account_number_kind BEFORE INSERT ON md.party_bank_account FOR EACH ROW EXECUTE FUNCTION md.party_bank_account_number_kind();

-- ---------------------------------------------------------------------------------------------
-- A payment's USD amount and rate change with its plan while PREPARED (E-USD1-05-2/3).
-- ---------------------------------------------------------------------------------------------
GRANT UPDATE (amount_fc, exchange_rate) ON fin.payment TO rochell_app;
GRANT UPDATE (open_amount_fc) ON fin.ap_document TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- P-41 (E-USD1-05-5).
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type) VALUES
  ('0192f001-0000-7000-8000-000000000033', 'P-41', 'ForeignPaymentReleased');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, also_requires_components, effective_from, status) VALUES
  ('0192f001-0000-7000-8000-000000000033', 1,
   '{"lines": [
      {"code": "P41-DR-AP", "side": "DEBIT", "account_role": "AP_FOREIGN", "amount": "applied_amount", "dimensions": ["party"], "subledger": "AP"},
      {"code": "P41-CR-BANK", "side": "CREDIT", "account_role": "BANK", "amount": "payment_amount", "dimensions": ["party"], "subledger": "BANK"},
      {"code": "P41-DR-FXL", "side": "DEBIT", "account_role": "FX_LOSS", "amount": "fx_loss", "dimensions": ["party"]},
      {"code": "P41-CR-FXG", "side": "CREDIT", "account_role": "FX_GAIN", "amount": "fx_gain", "dimensions": ["party"]}
    ]}',
   '{"P41-DR-AP": "Pago {payment_no}: USD {amount_usd} de la factura {number}, que se debía a la tasa de la factura.",
     "P41-CR-BANK": "Pago {payment_no}: salida del banco, USD {amount_usd} a la tasa {rate}.",
     "P41-DR-FXL": "Pago {payment_no}: pérdida cambiaria realizada (tasa del pago {rate} mayor que la de las facturas).",
     "P41-CR-FXG": "Pago {payment_no}: ganancia cambiaria realizada (tasa del pago {rate} menor que la de las facturas)."}',
   'AP-REC', ARRAY['BANK-REC'], DATE '2026-01-01', 'DRAFT');

-- ---------------------------------------------------------------------------------------------
-- A payment of USD payables (E-USD1-05-2/4) allocates and applies USD: its plan and its live applications add up to its USD amount;
-- its pesos also carry the exchange difference. Otherwise as in 0027.
-- ---------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION fin.payment_amount_allocated() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  p fin.payment;
  total numeric;
  expected numeric;
BEGIN
  SELECT * INTO p FROM fin.payment WHERE payment_id = NEW.payment_id;
  IF p.status::text = 'PREPARED' THEN
    SELECT coalesce(sum(amount), 0) INTO total FROM fin.payment_allocation WHERE payment_id = p.payment_id AND payment_version = p.version;
    expected := coalesce(p.amount_fc, p.amount);
  ELSIF p.status::text IN ('RELEASED', 'CLEARED', 'REVERSED') THEN
    SELECT coalesce(sum(CASE WHEN a.reverses_application_id IS NULL THEN coalesce(a.amount_fc, a.amount) ELSE -coalesce(a.amount_fc, a.amount) END), 0) INTO total
    FROM fin.ap_application a WHERE a.payment_id = p.payment_id;
    expected := CASE WHEN p.status::text = 'REVERSED' THEN 0 ELSE coalesce(p.amount_fc, p.amount) END;
  ELSE
    RETURN NULL;
  END IF;
  IF total <> expected THEN
    RAISE EXCEPTION 'fin.payment %: live applications add up to %, expected % for %', p.payment_id, total, expected, p.status;
  END IF;
  RETURN NULL;
END $$;
