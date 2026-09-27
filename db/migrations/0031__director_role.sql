-- E-ADM-1 (b): role DIRECTOR — sees every screen and report, executes nothing. Every READ permission of the matrix, no WRITE or
-- SECURITY permission, so it takes part in no segregation-of-duties pair; operations stay with each responsible role.
INSERT INTO iam.role (role_id, code, name) VALUES (gen_random_uuid(), 'DIRECTOR', 'Director (solo lectura)');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, p.permission_code
FROM iam.role r CROSS JOIN iam.permission p
WHERE r.code = 'DIRECTOR' AND p.access = 'READ';
