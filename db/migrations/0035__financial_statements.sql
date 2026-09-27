-- FIN1-03 · Trial balance, account ledger and statements. E-FIN1-03-1…11.
-- The statements read fin.gl_entry, fin.account and the report structures of 0033; the only new object is the reconciliation
-- STRUCT-COVERAGE (E-FIN1-03-9): a WARNING that blocks no close component.
INSERT INTO rec.recon_definition (recon_code, description, severity) VALUES
  ('STRUCT-COVERAGE', 'Toda cuenta activa tiene clase y está en una línea de la estructura vigente; el balance cuadra (E-FIN1-03-9)', 'WARNING');
