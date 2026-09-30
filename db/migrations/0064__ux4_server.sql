-- UX4-01 · Remaining UX findings, server side (approved errata E-UX4-1…17): sequential purchase-order and goods-receipt numbers,
-- bank account alias, the supplier's printed total, a positive minimum curing time for new recipes, the two sides of each
-- reconciliation in words and the Contador's read access to periods and reconciliations.

-- E-UX4-5: new orders and receipts are numbered OC-YYYY-000123 / RM-YYYY-000045 per company and year (the application assigns
-- the next number under an advisory lock, like PAG-…). The earlier readable, non-sequential numbers (8 hex characters) stay valid.
ALTER TABLE pur.purchase_order DROP CONSTRAINT purchase_order_no_format;
ALTER TABLE pur.purchase_order ADD CONSTRAINT purchase_order_no_format CHECK (po_no ~ '^OC-[0-9]{4}-([0-9A-F]{8}|[0-9]{6,7})$');
ALTER TABLE pur.goods_receipt DROP CONSTRAINT goods_receipt_no_format;
ALTER TABLE pur.goods_receipt ADD CONSTRAINT goods_receipt_no_format CHECK (gr_no ~ '^RM-[0-9]{4}-([0-9A-F]{8}|[0-9]{6,7})$');

-- E-UX4-6: a short name for each bank account, shown as "alias · banco ••••6789". Changed by SetBankAccountAlias (version + 1;
-- the guard already lets any non-identity column change with the version).
ALTER TABLE fin.bank_account
  ADD COLUMN alias text,
  ADD CONSTRAINT bank_account_alias_format CHECK (alias IS NULL OR (alias = btrim(alias) AND char_length(alias) BETWEEN 1 AND 60));
GRANT UPDATE (alias) ON fin.bank_account TO rochell_app;

-- E-UX4-7: the total printed on the supplier's invoice, as typed at registration (optional); compared with the determined gross.
ALTER TABLE pur.supplier_invoice
  ADD COLUMN printed_total numeric(19,2),
  ADD CONSTRAINT supplier_invoice_printed_total_positive CHECK (printed_total IS NULL OR printed_total > 0);

-- E-UX4-9: a recipe cures at least one hour. Only new (DRAFT) versions are held to it, so an existing ACTIVE or SUPERSEDED version
-- with 0 hours can still be superseded; the constraint is validated when no DRAFT version breaks it.
ALTER TABLE mfg.recipe_version ADD CONSTRAINT recipe_min_curing_positive CHECK (min_curing_hours > 0 OR status <> 'DRAFT') NOT VALID;
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM mfg.recipe_version WHERE min_curing_hours <= 0 AND status = 'DRAFT') THEN
    ALTER TABLE mfg.recipe_version VALIDATE CONSTRAINT recipe_min_curing_positive;
  END IF;
END $$;

-- E-UX4-2 (A-22): what side A and side B of each reconciliation with totals are, in Spanish. A migration that adds a reconciliation
-- with totals gives both labels.
ALTER TABLE rec.recon_definition
  ADD COLUMN side_a_label text,
  ADD COLUMN side_b_label text,
  ADD CONSTRAINT recon_definition_side_labels CHECK ((side_a_label IS NULL) = (side_b_label IS NULL));
UPDATE rec.recon_definition d SET side_a_label = v.a, side_b_label = v.b FROM (VALUES
  ('AP-GL', 'Saldo abierto de cuentas por pagar', 'Saldo de la cuenta de control de proveedores'),
  ('AR-GL', 'Saldo abierto de cuentas por cobrar', 'Saldo de la cuenta de control de clientes'),
  ('INV-VALUE-GL', 'Valor del inventario', 'Saldo contable del inventario'),
  ('INV-QTY-BALANCE', 'Existencias', 'Suma de los movimientos de cantidad'),
  ('INV-VALUE-BALANCE', 'Valor del inventario', 'Suma de las entradas de valor'),
  ('VALUE-GL-LINK', 'Suma de las entradas de valor', 'Líneas contables enlazadas a entradas de valor'),
  ('PAY-APPL', 'Aplicado a facturas de proveedor', 'Pagos liberados'),
  ('TB-BALANCED', 'Débitos', 'Créditos'),
  ('CONTRACT-ASSET', 'Entregado no facturado al precio del pedido', 'Saldo de activo de contrato y cuentas por cobrar no facturadas'),
  ('RECEIPT-APPL', 'Cobros aplicados más saldo sin aplicar', 'Cobros registrados'),
  ('WIP-GL', 'Consumo contabilizado menos estándar y variaciones liquidadas', 'Saldo contable de producción en proceso'),
  ('BANK-GL', 'Saldo contable del banco', 'Saldo del extracto más partidas en tránsito')
) AS v (code, a, b) WHERE d.recon_code = v.code;

-- E-UX4-13: the Contador follows the close and the reconciliations (READ only; READ permissions take part in no SoD rule).
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, p.code FROM iam.role r CROSS JOIN (VALUES ('period:read'), ('reconciliation:read')) AS p (code) WHERE r.code = 'CONTADOR';
