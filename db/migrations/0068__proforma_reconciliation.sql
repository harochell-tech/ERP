-- FIS1b-06 · Reconciliation of proformas and their allocations (approved errata E-FIS1b-11, E-FIS1b-01-12). The warning for
-- proformas waiting too long for their e-CF is the existing UNBILLED_AGED of CONTRACT-ASSET (E-FIS1b-01-10): no new parameter.
-- No side labels: it stores no totals (E-UX4-10 gives them to the reconciliations that do).
INSERT INTO rec.recon_definition (recon_code, description, severity, name, guidance) VALUES
  ('PROFORMA-ASIG',
   'Asignado de cada proforma y de cada recibo = suma de sus asignaciones vigentes; neto de cada proforma abierta = entregado sin facturar de su conduce (E-FIS1b-01-12)',
   'ERROR',
   'Proformas y cobros asignados',
   'Comprueba que lo asignado de cada proforma y de cada recibo sea la suma de sus asignaciones vigentes, y que cada proforma abierta corresponda a lo entregado y aún no facturado de su conduce (una proforma facturada ya no debe tener nada sin facturar). Es una verificación interna: repórtela a soporte con la clave. Bloquea AR-REC.');

INSERT INTO rec.recon_blocking (recon_code, component) VALUES ('PROFORMA-ASIG', 'AR-REC');

INSERT INTO rec.recon_classification (classification, name, guidance) VALUES
  ('PROFORMA_ALLOCATION_DIFFERENCE', 'Asignado de la proforma distinto de sus asignaciones',
   'Lo asignado que guarda la proforma no es la suma de las asignaciones vigentes de recibos. Es un error interno: repórtelo a soporte con el número de la proforma.'),
  ('RECEIPT_ALLOCATION_DIFFERENCE', 'Asignado del recibo distinto de sus asignaciones',
   'Lo asignado que guarda el recibo no es la suma de sus asignaciones vigentes a proformas. Es un error interno: repórtelo a soporte con el número del recibo.'),
  ('PROFORMA_UNBILLED_DIFFERENCE', 'Proforma que no corresponde a lo entregado sin facturar',
   'El neto de una proforma abierta no coincide con lo entregado y aún no facturado de su conduce, o una proforma facturada conserva entregas sin facturar. Revise si el conduce se facturó por otra vía; si no encuentra la causa, repórtelo a soporte.');
