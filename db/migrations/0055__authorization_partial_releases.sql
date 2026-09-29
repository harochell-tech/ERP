-- FIS1-03 · Partial releases of fiscal authorization consumption (E-FIS1-03-7, E-FIS1-03-10): a commercial credit note returns only
-- the net it credits (it has no quantity) and an invoice line may be credited more than once, so a consumption row can have several
-- release rows and a release may carry quantity zero. Σ releases ≤ the consumption is enforced by the commands.
ALTER TABLE tax.fiscal_authorization_consumption
  DROP CONSTRAINT fiscal_authorization_consumption_reverses_uq,
  DROP CONSTRAINT fiscal_authorization_consumption_amounts,
  ADD CONSTRAINT fiscal_authorization_consumption_amounts CHECK (net > 0 AND qty >= 0 AND (qty > 0 OR reverses_consumption_id IS NOT NULL));
CREATE INDEX fiscal_authorization_consumption_reverses ON tax.fiscal_authorization_consumption (reverses_consumption_id) WHERE reverses_consumption_id IS NOT NULL;
