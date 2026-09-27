-- VS2-07 · Treasury queries. Approved errata E-VS2-07-1…7.

-- E-VS2-07-2: the AP aging buckets are thresholds, so they come from an accounting policy approved by the Controller (A-01):
-- documents overdue 1…bucket 1 days, bucket 1 + 1…bucket 2, bucket 2 + 1…bucket 3, and more than bucket 3.
INSERT INTO acc.accounting_policy (policy_code, owner_role, description) VALUES
  ('TREASURY', 'CONTROLLER', 'Tramos de antigüedad de cuentas por pagar');

INSERT INTO acc.policy_parameter_definition (param_code, policy_code, value_type, min_value, max_value, allowed_values, description) VALUES
  ('ap_aging_bucket_1_days', 'TREASURY', 'INTEGER', 1, 3650, NULL, 'Antigüedad de CxP: fin del primer tramo de vencido (días)'),
  ('ap_aging_bucket_2_days', 'TREASURY', 'INTEGER', 1, 3650, NULL, 'Antigüedad de CxP: fin del segundo tramo de vencido (días)'),
  ('ap_aging_bucket_3_days', 'TREASURY', 'INTEGER', 1, 3650, NULL, 'Antigüedad de CxP: fin del tercer tramo de vencido (días); lo que pasa de aquí es el último tramo');

-- E-VS2-07-3: bank account numbers leave the server masked to the last 4 digits unless the reader holds this READ permission.
INSERT INTO iam.permission (permission_code, access) VALUES ('bank_account_number:read', 'READ');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, 'bank_account_number:read' FROM iam.role r WHERE r.code IN ('CONTROLLER', 'AUDITOR');
