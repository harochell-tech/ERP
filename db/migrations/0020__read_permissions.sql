-- PR-18a · API read side. Approved errata E-PR18-4: READ permissions for the list and detail queries the UI needs,
-- granted to the roles that work those documents (§14) plus the Controller and the Auditor.
-- READ permissions take part in no segregation-of-duties rule (E-PR03-4 b, c apply to WRITE / SECURITY only).

INSERT INTO iam.permission (permission_code, access) VALUES
  ('purchase_order:read', 'READ'), ('goods_receipt:read', 'READ'), ('supplier_invoice:read', 'READ'),
  ('period:read', 'READ'), ('master_data:read', 'READ');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES
  -- Purchase orders: whoever creates, approves, receives against or bills against them.
  ('COMPRADOR', 'purchase_order:read'), ('APROBADOR_COMPRAS', 'purchase_order:read'), ('ALMACENISTA', 'purchase_order:read'),
  ('CUENTAS_POR_PAGAR', 'purchase_order:read'), ('CONTROLLER', 'purchase_order:read'), ('AUDITOR', 'purchase_order:read'),
  -- Goods receipts and receipt corrections: whoever posts, corrects, matches invoices against or reverses them.
  ('ALMACENISTA', 'goods_receipt:read'), ('CUENTAS_POR_PAGAR', 'goods_receipt:read'), ('CONTROLLER', 'goods_receipt:read'),
  ('AUDITOR', 'goods_receipt:read'),
  -- Supplier invoices: accounts payable, and the Controller who approves exceptions and reverses.
  ('CUENTAS_POR_PAGAR', 'supplier_invoice:read'), ('CONTROLLER', 'supplier_invoice:read'), ('AUDITOR', 'supplier_invoice:read'),
  -- Periods, component states and reopen requests: who closes, who approves a reopening, who audits.
  ('CONTROLLER', 'period:read'), ('SEGUNDO_APROBADOR_CIERRE', 'period:read'), ('AUDITOR', 'period:read'),
  -- Master data (suppliers, items, plants, locations): every role that picks them in a document.
  ('COMPRADOR', 'master_data:read'), ('APROBADOR_COMPRAS', 'master_data:read'), ('ALMACENISTA', 'master_data:read'),
  ('CUENTAS_POR_PAGAR', 'master_data:read'), ('CONTROLLER', 'master_data:read'), ('AUDITOR', 'master_data:read')
) AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;
