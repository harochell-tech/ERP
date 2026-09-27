-- VS2-06 · BANK-GL, PAY-APPL and the BANK-REC component in the close. Approved errata E-VS2-06-1…9.

-- E-VS2-06-1: a run may have a cutoff date (CloseComponent passes the period end); only BANK-GL uses it.
ALTER TABLE rec.recon_run ADD COLUMN cutoff_date date;

INSERT INTO rec.recon_definition (recon_code, description, severity) VALUES
  ('BANK-GL', 'Saldo GL de cada cuenta bancaria al corte = saldo del extracto ± partidas en tránsito identificadas (E-VS2-06-2…4, E-VS2-06-8)', 'ERROR'),
  ('PAY-APPL', 'Aplicaciones vivas por pago = monto; original − abierto por documento AP = aplicaciones; una línea R-09 AP por aplicación (E-VS2-06-5)', 'ERROR');

-- E-VS2-06-6/7/9: BANK-REC closes only without BANK-GL, PAY-APPL or ACC-EVIDENCE errors; AP-REC also needs PAY-APPL.
INSERT INTO rec.recon_blocking (recon_code, component) VALUES
  ('BANK-GL', 'BANK-REC'), ('PAY-APPL', 'BANK-REC'), ('ACC-EVIDENCE', 'BANK-REC'), ('PAY-APPL', 'AP-REC');
