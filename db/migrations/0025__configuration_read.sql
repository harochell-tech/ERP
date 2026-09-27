-- B03-4 · Configuration screens. Approved errata E-B03-15-1: READ permission for the lists behind the approval screens
-- (accounts, account role maps, posting rule versions, accounting policies, fiscal sources and rules), granted to the roles that
-- prepare, approve or audit that configuration. READ permissions take part in no segregation-of-duties rule.

INSERT INTO iam.permission (permission_code, access) VALUES ('configuration:read', 'READ');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, 'configuration:read'
FROM iam.role r
WHERE r.code IN ('CONTROLLER', 'APROBADOR_POLITICAS', 'ANALISTA_FISCAL', 'ESPECIALISTA_FISCAL', 'AUDITOR');
