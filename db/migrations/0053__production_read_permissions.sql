-- MFG1-07 · Read permissions the production screens need (approved errata E-MFG1-07-9…11): the production roles read plants,
-- locations and items; the Aprobador de políticas reads the sales masters to approve standard costs from the UI.
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES
  ('SUPERVISOR_PRODUCCION', 'master_data:read'), ('GERENTE_PLANTA', 'master_data:read'), ('CALIDAD', 'master_data:read'),
  ('APROBADOR_POLITICAS', 'sales:read')
) AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;
