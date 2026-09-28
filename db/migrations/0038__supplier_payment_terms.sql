-- VS3-02 · Master commands of VS#3. E-VS3-02-1…11.
-- E-VS3-17 (b), E-VS3-02-10: a supplier's payment term in days (0–365, optional) proposes the due date of its invoices on screen.
-- It changes at any time (md.party_guard of 0037 freezes only RNC, legal name and status once the party is ACTIVE).
ALTER TABLE md.party
  ADD COLUMN supplier_payment_terms_days integer,
  ADD CONSTRAINT party_supplier_payment_terms CHECK (supplier_payment_terms_days IS NULL
    OR (is_supplier AND supplier_payment_terms_days BETWEEN 0 AND 365));

GRANT UPDATE (supplier_payment_terms_days) ON md.party TO rochell_app;
