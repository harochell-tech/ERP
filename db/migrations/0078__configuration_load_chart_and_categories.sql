-- GAS1-03 · The configuration load also brings the chart of accounts and the expense categories (approved errata E-GAS-03-4,
-- E-GAS-03-7): the service identity «Carga de configuración» creates accounts and prepares categories, through the same commands as
-- the screens. Approving a category stays with a person (the Controller).
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES ('account:manage'), ('expense_category:prepare')) AS v (permission_code)
CROSS JOIN iam.role r WHERE r.code = 'CARGA_CONFIGURACION';

UPDATE iam.role SET description =
  'Identidad de servicio: desde la herramienta de despliegue registra fuentes fiscales y prepara versiones de reglas con sus pruebas, crea las cuentas del catálogo y prepara categorías de gasto. No activa ni aprueba.'
WHERE code = 'CARGA_CONFIGURACION';
