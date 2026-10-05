-- USD1-02 · E-USD1-02-8: who reads the exchange rates — the ones who prepare and approve them, and the ones whose documents take them.
INSERT INTO iam.permission (permission_code, access) VALUES ('exchange_rate:read', 'READ');
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, 'exchange_rate:read' FROM iam.role r WHERE r.code IN ('TESORERO', 'CONTADOR', 'CONTROLLER', 'CUENTAS_POR_PAGAR', 'COMPRADOR', 'AUDITOR');
