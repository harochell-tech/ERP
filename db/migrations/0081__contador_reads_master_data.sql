-- GAS1-07 · E-GAS-07-9: the Contador prepares expense categories (E-GAS-01-4) from Maestros › Categorías de gasto, whose list —
-- like every master data list — is read with master_data:read. The Contador gets that read permission; nothing to write.
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT role_id, 'master_data:read' FROM iam.role WHERE code = 'CONTADOR'
ON CONFLICT DO NOTHING;
