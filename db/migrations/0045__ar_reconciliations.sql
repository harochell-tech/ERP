-- VS3-08 · AR reconciliations and the AR-REC close: AR-GL, CONTRACT-ASSET, RECEIPT-APPL, FISC-DOC, DELIVERY-OPEN; ACC-EVIDENCE for
-- the VS#3 documents. Frozen Baseline VS#3 §8; approved errata E-VS3-10, E-VS3-15, E-VS3-04-12, E-VS3-08-1…12.

-- ---------------------------------------------------------------------------------------------
-- E-VS3-08-1…7: the new reconciliations.
-- ---------------------------------------------------------------------------------------------
INSERT INTO rec.recon_definition (recon_code, description, severity) VALUES
  ('AR-GL', 'Documentos AR abiertos por cliente = saldo de AR_CONTROL en el GL por cliente (E-VS3-08-1)', 'ERROR'),
  ('CONTRACT-ASSET', 'Entregado no facturado valorizado por línea de conduce = saldo de CONTRACT_ASSET + UNBILLED_RECEIVABLE; aviso de antigüedad (E-VS3-08-2/3)', 'ERROR'),
  ('RECEIPT-APPL', 'Aplicaciones vivas + no aplicado = monto de recibos vivos; original − abierto por factura = aplicaciones + retenciones + notas; una línea P-25 por aplicación (E-VS3-08-4)', 'ERROR'),
  ('FISC-DOC', 'Facturas y notas de crédito con e-CF no final a la fecha de corte (E-VS3-08-6)', 'ERROR'),
  ('DELIVERY-OPEN', 'Conduces en tránsito más tiempo que delivery_open_alert_hours (advertencia, E-VS3-08-7)', 'WARNING');

-- E-VS3-08-8: what blocks each close.
INSERT INTO rec.recon_blocking (recon_code, component) VALUES
  ('AR-GL', 'AR-REC'), ('CONTRACT-ASSET', 'AR-REC'), ('RECEIPT-APPL', 'AR-REC'), ('RECEIPT-APPL', 'BANK-REC'), ('FISC-DOC', 'AR-REC'), ('ACC-EVIDENCE', 'AR-REC');

-- ---------------------------------------------------------------------------------------------
-- E-VS3-08-3/7: the alert thresholds are parameters of REVENUE_ACCOUNTING (values approved by the Controller, A-01).
-- ---------------------------------------------------------------------------------------------
INSERT INTO acc.policy_parameter_definition (param_code, policy_code, value_type, min_value, max_value, allowed_values, description) VALUES
  ('unbilled_aging_alert_days', 'REVENUE_ACCOUNTING', 'INTEGER', 1, 3650, NULL, 'Días de entregado sin facturar que generan aviso (E-VS3-10)'),
  ('delivery_open_alert_hours', 'REVENUE_ACCOUNTING', 'INTEGER', 1, 720, NULL, 'Horas de un conduce en tránsito que generan aviso (E-VS3-15)');

-- ---------------------------------------------------------------------------------------------
-- E-VS3-04-12 / E-VS3-08-9: P-15, P-15R, P-16 and P-30 also require AR-REC. Only a DRAFT version may be corrected here; an ACTIVE
-- one needs a new version, so the migration stops instead of changing an approved rule.
-- ---------------------------------------------------------------------------------------------
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM fin.posting_rule_version v JOIN fin.posting_rule r USING (posting_rule_id)
             WHERE r.code IN ('P-15', 'P-15R', 'P-16', 'P-30') AND v.status <> 'DRAFT') THEN
    RAISE EXCEPTION 'P-15, P-15R, P-16 or P-30 is already approved: add AR-REC through a new rule version (E-VS3-08-9)';
  END IF;
END $$;

ALTER TABLE fin.posting_rule_version DISABLE TRIGGER posting_rule_version_guard;
ALTER TABLE fin.posting_rule_version DISABLE TRIGGER posting_rule_version_identity;
UPDATE fin.posting_rule_version v SET also_requires_components = ARRAY['AR-REC']::text[]
FROM fin.posting_rule r WHERE r.posting_rule_id = v.posting_rule_id AND r.code IN ('P-15', 'P-15R', 'P-16', 'P-30') AND v.version = 1;
ALTER TABLE fin.posting_rule_version ENABLE TRIGGER posting_rule_version_identity;
ALTER TABLE fin.posting_rule_version ENABLE TRIGGER posting_rule_version_guard;
