-- FIN1-04 · E-FIN1-04-2: the Contador reads the chart of accounts (and the other configuration lists) to prepare adjustments.
-- READ only; READ permissions take part in no segregation-of-duties rule.
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, 'configuration:read' FROM iam.role r WHERE r.code = 'CONTADOR';
