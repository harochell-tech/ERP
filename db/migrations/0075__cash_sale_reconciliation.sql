-- CF1-04 · Reconciliation of cash sales (approved errata E-CF1-10, E-CF1-11, E-CF1-02-2): a cash order never has delivered more
-- than what is collected with money that counts, what an order says is assigned equals its live assignments, and cash or cheques
-- kept without depositing are warned about. The first two block the AR close.

-- E-CF1-11: how long cash or a cheque may wait for its deposit before the warning; its value is the Controller's (A-01).
INSERT INTO acc.policy_parameter_definition (param_code, policy_code, value_type, min_value, max_value, allowed_values, description, label, unit, example, affects) VALUES
  ('cash_deposit_alert_days', 'REVENUE_ACCOUNTING', 'INTEGER', 1, 365, NULL, 'Días que un cobro en efectivo o cheque puede estar sin depositar antes de avisar (E-CF1-11)',
   'Aviso de efectivo o cheques sin depositar', 'DAYS', '2 días',
   'Días desde el cobro en efectivo o cheque sin depósito a partir de los cuales la conciliación CASH-SALE avisa.');

INSERT INTO rec.recon_definition (recon_code, description, severity, name, guidance) VALUES
  ('CASH-SALE',
   'Entregado de cada venta de contado ≤ cobrado con dinero que cuenta; asignado del pedido = suma de sus asignaciones vigentes; efectivo y cheques sin depositar (E-CF1-10, E-CF1-11)',
   'ERROR',
   'Ventas de contado',
   'Comprueba que ninguna venta de contado tenga entregado más de lo cobrado con dinero que cuente (un cheque cuenta cuando su depósito está conciliado con el banco) y que lo asignado de cada pedido sea la suma de sus asignaciones vigentes. Avisa, además, del efectivo y los cheques que llevan días sin depositarse. Bloquea AR-REC.');

INSERT INTO rec.recon_blocking (recon_code, component) VALUES ('CASH-SALE', 'AR-REC');

INSERT INTO rec.recon_classification (classification, name, guidance) VALUES
  ('CASH_SALE_UNPAID', 'Venta de contado entregada sin cobro completo',
   'Lo entregado de una venta de contado vale más que lo cobrado con dinero que cuente: un cheque devuelto o un recibo anulado después de despachar. Cobre la diferencia y asígnela al pedido, o acredite la entrega con una nota de crédito.'),
  ('ORDER_ALLOCATION_DIFFERENCE', 'Asignado del pedido distinto de sus asignaciones',
   'Lo asignado que guarda el pedido de contado no es la suma de las asignaciones vigentes de recibos. Es un error interno: repórtelo a soporte con el número del pedido.'),
  ('CASH_UNDEPOSITED', 'Efectivo o cheque sin depositar',
   'Un cobro en efectivo o en cheque lleva más días de los permitidos sin depositarse. Deposítelo en Cobros › Depósitos; si el dinero no está, avise al Controller.');
