-- Test-only permissions and role used by Ping commands (never present in production databases).
INSERT INTO iam.permission (permission_code, access) VALUES
  ('test:ping', 'WRITE'),
  ('test:ping_step_up', 'WRITE');

INSERT INTO iam.role (role_id, code, name) VALUES (gen_random_uuid(), 'TEST_PINGER', 'Test pinger');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT role_id, p FROM iam.role, (VALUES ('test:ping'), ('test:ping_step_up')) AS v (p) WHERE code = 'TEST_PINGER';
