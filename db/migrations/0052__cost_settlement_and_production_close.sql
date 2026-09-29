-- MFG1-05 · Cost collector settlement (P-13), production reconciliations, close components OP-DAY and COST-SET, policy PRODUCTION.
-- Frozen Baseline MFG-1 §6 and §8; approved errata E-MFG1-11, E-MFG1-12, E-MFG1-16 and E-MFG1-05-1…8.

-- ---------------------------------------------------------------------------------------------
-- E-MFG1-05-1/2: the settlement's figures on the collector.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE mfg.cost_collector
  ADD COLUMN usage_variance numeric(19,4),
  ADD COLUMN price_variance numeric(19,4),
  ADD COLUMN settlement_event_id uuid,
  ADD COLUMN settled_at timestamptz,
  ADD CONSTRAINT cost_collector_settlement CHECK ((status = 'SETTLED') = (usage_variance IS NOT NULL) AND (status = 'SETTLED') = (price_variance IS NOT NULL)
    AND (status = 'SETTLED') = (settlement_event_id IS NOT NULL) AND (status = 'SETTLED') = (settled_at IS NOT NULL));

CREATE FUNCTION mfg.cost_collector_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF ROW(NEW.collector_id, NEW.company_id, NEW.plant_id, NEW.item_id, NEW.period_month) IS DISTINCT FROM ROW(OLD.collector_id, OLD.company_id, OLD.plant_id, OLD.item_id, OLD.period_month)
     OR NEW.version <> OLD.version + 1 OR NOT (OLD.status = 'OPEN' AND NEW.status = 'SETTLED') THEN
    RAISE EXCEPTION 'mfg.cost_collector: % → % or a change of the collector''s identity is not allowed (a settled collector stays settled)', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER cost_collector_guard BEFORE UPDATE ON mfg.cost_collector FOR EACH ROW EXECUTE FUNCTION mfg.cost_collector_guard();
CREATE CONSTRAINT TRIGGER cost_collector_evidence_on_change AFTER UPDATE ON mfg.cost_collector
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('CostCollector', 'collector_id');
GRANT UPDATE (usage_variance, price_variance, settlement_event_id, settled_at) ON mfg.cost_collector TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- E-MFG1-16, E-MFG1-05-5: close components OP-DAY and COST-SET, accepted everywhere a component is named, OPEN in every
-- existing period.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE fin.posting_rule_version DROP CONSTRAINT posting_rule_version_component,
  ADD CONSTRAINT posting_rule_version_component CHECK (close_component IN ('INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX', 'AR-REC', 'OP-DAY', 'COST-SET'));
ALTER TABLE fin.posting_rule_version DROP CONSTRAINT posting_rule_version_also_requires,
  ADD CONSTRAINT posting_rule_version_also_requires CHECK (also_requires_components <@ ARRAY['INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX', 'AR-REC', 'OP-DAY', 'COST-SET']::text[]);
ALTER TABLE fin.close_component_state DROP CONSTRAINT close_component_state_component,
  ADD CONSTRAINT close_component_state_component CHECK (component IN ('INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX', 'AR-REC', 'OP-DAY', 'COST-SET'));
ALTER TABLE fin.close_snapshot DROP CONSTRAINT close_snapshot_component,
  ADD CONSTRAINT close_snapshot_component CHECK (component IN ('INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX', 'AR-REC', 'OP-DAY', 'COST-SET'));
ALTER TABLE fin.reopen_request DROP CONSTRAINT reopen_request_component,
  ADD CONSTRAINT reopen_request_component CHECK (component IN ('INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX', 'AR-REC', 'OP-DAY', 'COST-SET'));
ALTER TABLE rec.recon_blocking DROP CONSTRAINT recon_blocking_component,
  ADD CONSTRAINT recon_blocking_component CHECK (component IN ('INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX', 'AR-REC', 'OP-DAY', 'COST-SET'));
ALTER TABLE rec.recon_exception DROP CONSTRAINT recon_exception_component,
  ADD CONSTRAINT recon_exception_component CHECK (component IS NULL OR component IN ('INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX', 'AR-REC', 'OP-DAY', 'COST-SET'));

INSERT INTO fin.close_component_state (company_id, period_id, component, status, version)
SELECT company_id, period_id, comp, 'OPEN', 1 FROM fin.period CROSS JOIN (VALUES ('OP-DAY'), ('COST-SET')) AS v (comp)
ON CONFLICT (period_id, component) DO NOTHING;

-- ---------------------------------------------------------------------------------------------
-- E-MFG1-12, E-MFG1-05-3: P-13 CostCollectorSettled — usage and price variances against WIP [collector]; favourable variances use
-- the reversed pairs (amounts are never negative). Posted on the collector's last day; close component COST-SET. DRAFT until A-01.
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type)
VALUES ('0192f001-0000-7000-8000-000000000027', 'P-13', 'CostCollectorSettled');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, also_requires_components, effective_from, status)
VALUES ('0192f001-0000-7000-8000-000000000027', 1,
   '{"lines": [
      {"code": "P13-DR-USAGE", "side": "DEBIT", "account_role": "MATERIAL_USAGE_VARIANCE", "amount": "usage_variance", "dimensions": ["plant", "item"]},
      {"code": "P13-CR-WIP-USAGE", "side": "CREDIT", "account_role": "WIP", "amount": "usage_variance", "dimensions": ["plant", "item"], "subledger": "WIP"},
      {"code": "P13-DR-WIP-USAGE", "side": "DEBIT", "account_role": "WIP", "amount": "usage_variance", "dimensions": ["plant", "item"], "subledger": "WIP"},
      {"code": "P13-CR-USAGE", "side": "CREDIT", "account_role": "MATERIAL_USAGE_VARIANCE", "amount": "usage_variance", "dimensions": ["plant", "item"]},
      {"code": "P13-DR-PRICE", "side": "DEBIT", "account_role": "MATERIAL_PRICE_VARIANCE", "amount": "price_variance", "dimensions": ["plant", "item"]},
      {"code": "P13-CR-WIP-PRICE", "side": "CREDIT", "account_role": "WIP", "amount": "price_variance", "dimensions": ["plant", "item"], "subledger": "WIP"},
      {"code": "P13-DR-WIP-PRICE", "side": "DEBIT", "account_role": "WIP", "amount": "price_variance", "dimensions": ["plant", "item"], "subledger": "WIP"},
      {"code": "P13-CR-PRICE", "side": "CREDIT", "account_role": "MATERIAL_PRICE_VARIANCE", "amount": "price_variance", "dimensions": ["plant", "item"]}
    ]}',
   '{"P13-DR-USAGE": "Variación de uso desfavorable del collector {collector} ({month}): consumo real sobre el estándar de las unidades buenas, a precio estándar.",
     "P13-CR-WIP-USAGE": "La variación de uso sale de producción en proceso del collector {collector}.",
     "P13-DR-WIP-USAGE": "La variación de uso favorable vuelve a producción en proceso del collector {collector}.",
     "P13-CR-USAGE": "Variación de uso favorable del collector {collector} ({month}).",
     "P13-DR-PRICE": "Variación de precio desfavorable del collector {collector} ({month}): costo promedio real sobre el precio estándar.",
     "P13-CR-WIP-PRICE": "La variación de precio sale de producción en proceso del collector {collector}.",
     "P13-DR-WIP-PRICE": "La variación de precio favorable vuelve a producción en proceso del collector {collector}.",
     "P13-CR-PRICE": "Variación de precio favorable del collector {collector} ({month})."}',
   'COST-SET', ARRAY[]::text[], DATE '2026-01-01', 'DRAFT');

-- ---------------------------------------------------------------------------------------------
-- E-MFG1-05-4: production reconciliations and what they block; E-MFG1-05-5: the close order OP-DAY → COST-SET → INV-MOV for a
-- period with production.
-- ---------------------------------------------------------------------------------------------
INSERT INTO rec.recon_definition (recon_code, description, severity) VALUES
  ('WIP-GL', 'Saldo WIP del GL por collector = consumo − estándar de materiales − variaciones liquidadas (E-MFG1-05-4)', 'ERROR'),
  ('WIP-OPEN', 'Collectors de meses terminados sin liquidar (E-MFG1-05-4)', 'ERROR'),
  ('SHIFT-OPEN', 'Corridas de fechas pasadas en IN_PROGRESS (E-MFG1-05-4)', 'ERROR'),
  ('USAGE-TOLERANCE', 'Consumo real fuera de la tolerancia usage_tolerance_pct del teórico (advertencia, E-MFG1-05-4)', 'WARNING'),
  ('CURING-OVERDUE', 'Lotes en curado más allá de las horas máximas de su receta (advertencia, E-MFG1-05-4)', 'WARNING'),
  ('PRODUCTION-CLOSE-ORDER', 'Con producción en el período: OP-DAY cerrado antes que COST-SET y COST-SET antes que INV-MOV (E-MFG1-05-5)', 'ERROR');

INSERT INTO rec.recon_blocking (recon_code, component) VALUES
  ('SHIFT-OPEN', 'OP-DAY'), ('SHIFT-OPEN', 'COST-SET'), ('WIP-GL', 'COST-SET'), ('WIP-OPEN', 'COST-SET'),
  ('PRODUCTION-CLOSE-ORDER', 'COST-SET'), ('PRODUCTION-CLOSE-ORDER', 'INV-MOV');

-- ---------------------------------------------------------------------------------------------
-- E-MFG1-05-6: policy PRODUCTION (values approved by the Controller, A-01). A fraction, as the other tolerances.
-- ---------------------------------------------------------------------------------------------
INSERT INTO acc.accounting_policy (policy_code, owner_role, description) VALUES
  ('PRODUCTION', 'CONTROLLER', 'Producción: tolerancia de consumo real contra el teórico de la receta');
INSERT INTO acc.policy_parameter_definition (param_code, policy_code, value_type, min_value, max_value, allowed_values, description) VALUES
  ('usage_tolerance_pct', 'PRODUCTION', 'DECIMAL_PERCENT', 0, 1, NULL, 'Diferencia máxima del consumo real contra el teórico antes de avisar (fracción)');
