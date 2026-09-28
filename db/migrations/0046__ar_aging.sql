-- VS3-09 · AR aging buckets. Frozen Baseline VS#3 §11 (VS3-09); approved errata E-VS3-09-1, E-VS3-09-10.

-- E-VS3-09-1: the buckets are thresholds, so they are parameters of the CREDIT policy approved by the Controller (A-01), as the AP
-- ones are of TREASURY (E-VS2-07-2): invoices overdue 1…bucket 1 days, bucket 1 + 1…bucket 2, bucket 2 + 1…bucket 3, and more.
INSERT INTO acc.policy_parameter_definition (param_code, policy_code, value_type, min_value, max_value, allowed_values, description) VALUES
  ('ar_aging_bucket_1_days', 'CREDIT', 'INTEGER', 1, 3650, NULL, 'Antigüedad de CxC: fin del primer tramo de vencido (días)'),
  ('ar_aging_bucket_2_days', 'CREDIT', 'INTEGER', 1, 3650, NULL, 'Antigüedad de CxC: fin del segundo tramo de vencido (días)'),
  ('ar_aging_bucket_3_days', 'CREDIT', 'INTEGER', 1, 3650, NULL, 'Antigüedad de CxC: fin del tercer tramo de vencido (días); lo que pasa de aquí es el último tramo');
