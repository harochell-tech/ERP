-- COST-01 · Standard scrap allowance (E-COST-1…5).
--   - E-COST-1: a standard cost from a recipe gains a third component, the scrap allowance per unit (zero or more):
--     unit cost = material + conversion + scrap allowance. A cost written by hand keeps no breakdown and no allowance.
--   - E-COST-2: account role SCRAP_ALLOWANCE and P-10 version 2 (from 2026-10-01, DRAFT until the Controller activates it; an active
--     version 1 ends there): version 1's lines plus the credit of the allowance absorbed by the good units.
--   - E-COST-3: real scrap is untouched (P-12 for cured product, the usage variance for fresh breakage).

-- ---------------------------------------------------------------------------------------------
-- E-COST-1: the third component.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE md.standard_cost_version
  ADD COLUMN scrap_allowance numeric(19,4) NOT NULL DEFAULT 0,
  DROP CONSTRAINT standard_cost_breakdown,
  ADD CONSTRAINT standard_cost_breakdown CHECK (
    scrap_allowance >= 0
    AND ((material_cost IS NULL AND conversion_cost IS NULL AND scrap_allowance = 0)
      OR (material_cost IS NOT NULL AND conversion_cost IS NOT NULL
          AND material_cost >= 0 AND conversion_cost >= 0 AND material_cost + conversion_cost + scrap_allowance = unit_cost)));

CREATE OR REPLACE FUNCTION md.standard_cost_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'DRAFT' THEN
      RAISE EXCEPTION 'md.standard_cost_version: a version is prepared as DRAFT';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM md.item i WHERE i.item_id = NEW.item_id AND i.item_type = 'FINISHED_GOOD') THEN
      RAISE EXCEPTION 'md.standard_cost_version: item % is not a finished good', NEW.item_id;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM md.valuation_area a WHERE a.valuation_area_id = NEW.valuation_area_id AND a.company_id = NEW.company_id) THEN
      RAISE EXCEPTION 'md.standard_cost_version: valuation area % is not of the company', NEW.valuation_area_id;
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'md.standard_cost_version rows cannot be deleted';
  END IF;
  IF ROW(NEW.cost_version_id, NEW.company_id, NEW.item_id, NEW.valuation_area_id, NEW.version, NEW.prepared_by)
     IS DISTINCT FROM ROW(OLD.cost_version_id, OLD.company_id, OLD.item_id, OLD.valuation_area_id, OLD.version, OLD.prepared_by)
     OR NOT sal.version_transition_allowed(OLD.status, NEW.status)
     OR (OLD.status <> 'DRAFT' AND ROW(NEW.effective_from, NEW.unit_cost, NEW.material_cost, NEW.conversion_cost, NEW.scrap_allowance)
                                   IS DISTINCT FROM ROW(OLD.effective_from, OLD.unit_cost, OLD.material_cost, OLD.conversion_cost, OLD.scrap_allowance)) THEN
    RAISE EXCEPTION 'md.standard_cost_version: % → % or a change of an approved version is not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;

-- ---------------------------------------------------------------------------------------------
-- E-COST-2: the account the allowance is absorbed into, mapped by the Controller (A-01), and P-10 version 2.
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.account_role (role_code, is_control, description, name) VALUES
  ('SCRAP_ALLOWANCE', false, 'Merma estándar absorbida por las unidades buenas; se compara con la merma real (E-COST-2)', 'Merma estándar absorbida');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, also_requires_components, effective_from, status) VALUES
  ('0192f001-0000-7000-8000-000000000025', 2,
   '{"lines": [
      {"code": "P10-DR-FG", "side": "DEBIT", "account_role": "FINISHED_GOODS", "amount": "standard_value", "dimensions": ["plant", "item"], "subledger": "INV"},
      {"code": "P10-CR-WIP", "side": "CREDIT", "account_role": "WIP", "amount": "material_standard", "dimensions": ["plant", "item"], "subledger": "WIP"},
      {"code": "P10-CR-CONV", "side": "CREDIT", "account_role": "CONVERSION_ABSORPTION", "amount": "conversion_standard", "dimensions": ["plant"]},
      {"code": "P10-CR-SCRAP", "side": "CREDIT", "account_role": "SCRAP_ALLOWANCE", "amount": "scrap_standard", "dimensions": ["plant"]}
    ]}',
   '{"P10-DR-FG": "Entrada a curado de {good_units} unidades de la corrida {run_no} (lote {lot_code}) a costo estándar {unit_cost}.",
     "P10-CR-WIP": "Materiales a estándar de las unidades buenas ({good_units} × {material_cost}) salen de producción en proceso.",
     "P10-CR-CONV": "Conversión a estándar absorbida por las unidades buenas (costo estándar menos materiales y merma estándar).",
     "P10-CR-SCRAP": "Merma estándar absorbida por las unidades buenas ({good_units} × {scrap_allowance})."}',
   'INV-MOV', ARRAY[]::text[], DATE '2026-10-01', 'DRAFT');
