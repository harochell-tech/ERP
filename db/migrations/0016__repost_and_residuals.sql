-- PR-14 · Repost (R-REP, T-11) and valuation residual adjustment (R-06, T-12). Frozen Baseline §11.5, §13, E-5,
-- Patch 1 (P-1, P-3), approved errata E-PR14-1…5.

-- E-PR14-3 / E-PR14-5: value-only movements for reposts and residual adjustments (text comparisons: see 0011).
ALTER TYPE inv.movement_type ADD VALUE 'REPOST';
ALTER TYPE inv.movement_type ADD VALUE 'RESIDUAL_ADJUSTMENT';

ALTER TABLE inv.inv_value_entry DROP CONSTRAINT inv_value_entry_sign;
ALTER TABLE inv.inv_value_entry ADD CONSTRAINT inv_value_entry_sign CHECK (
  (movement_type::text = 'RECEIPT' AND amount > 0) OR
  (movement_type::text IN ('ISSUE', 'RECEIPT_REVERSAL') AND amount < 0) OR
  movement_type::text IN ('RECEIPT_CORRECTION', 'VALUATION_ADJUSTMENT', 'VALUATION_REALLOCATION', 'PRICE_ADJUSTMENT', 'REPOST', 'RESIDUAL_ADJUSTMENT'));
ALTER TABLE inv.inv_value_entry DROP CONSTRAINT inv_value_entry_quantity_link;
ALTER TABLE inv.inv_value_entry ADD CONSTRAINT inv_value_entry_quantity_link CHECK (
  (movement_type::text IN ('VALUATION_ADJUSTMENT', 'VALUATION_REALLOCATION', 'PRICE_ADJUSTMENT', 'REPOST', 'RESIDUAL_ADJUSTMENT')) = (quantity_entry_id IS NULL));
ALTER TABLE inv.inv_value_entry DROP CONSTRAINT inv_value_entry_reversal_link;
ALTER TABLE inv.inv_value_entry ADD CONSTRAINT inv_value_entry_reversal_link CHECK (
  (movement_type::text <> 'RECEIPT_REVERSAL' OR reverses_value_entry_id IS NOT NULL) AND
  (reverses_value_entry_id IS NULL OR movement_type::text IN ('RECEIPT_REVERSAL', 'PRICE_ADJUSTMENT', 'REPOST')));
ALTER TABLE inv.inv_quantity_entry DROP CONSTRAINT inv_quantity_entry_no_value_only;
ALTER TABLE inv.inv_quantity_entry ADD CONSTRAINT inv_quantity_entry_no_value_only CHECK (
  movement_type::text NOT IN ('VALUATION_ADJUSTMENT', 'VALUATION_REALLOCATION', 'PRICE_ADJUSTMENT', 'REPOST', 'RESIDUAL_ADJUSTMENT'));

-- E-PR14-5: the counter-account of a residual adjustment is an accounting-policy decision (Controller, A-01).
INSERT INTO acc.policy_parameter_definition (param_code, policy_code, value_type, min_value, max_value, allowed_values, description) VALUES
  ('valuation_residual_account_role', 'INVENTORY', 'ENUM', NULL, NULL, ARRAY['PURCHASE_PRICE_VARIANCE', 'INVENTORY_ADJUSTMENT'],
   'Cuenta contrapartida del ajuste de valor huérfano de inventario (R-06)');

-- R-06 (E-PR14-4/5): orphan value (quantity 0, value ≠ 0) to zero, against PPV or INVENTORY_ADJUSTMENT per policy.
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type)
VALUES ('0192f001-0000-7000-8000-000000000008', 'R-06', 'ValuationResidualAdjusted');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, effective_from, status)
VALUES ('0192f001-0000-7000-8000-000000000008', 1,
  '{"lines": [
     {"code": "R06-CR-INV", "side": "CREDIT", "account_role": "RAW_MATERIAL", "amount": "residual", "dimensions": ["plant", "item"], "subledger": "INV"},
     {"code": "R06-DR-INV", "side": "DEBIT", "account_role": "RAW_MATERIAL", "amount": "residual", "dimensions": ["plant", "item"], "subledger": "INV"},
     {"code": "R06-DR-PPV", "side": "DEBIT", "account_role": "PURCHASE_PRICE_VARIANCE", "amount": "residual", "dimensions": ["plant", "item"]},
     {"code": "R06-CR-PPV", "side": "CREDIT", "account_role": "PURCHASE_PRICE_VARIANCE", "amount": "residual", "dimensions": ["plant", "item"]},
     {"code": "R06-DR-ADJ", "side": "DEBIT", "account_role": "INVENTORY_ADJUSTMENT", "amount": "residual", "dimensions": ["plant", "item"]},
     {"code": "R06-CR-ADJ", "side": "CREDIT", "account_role": "INVENTORY_ADJUSTMENT", "amount": "residual", "dimensions": ["plant", "item"]}
   ]}',
  '{"R06-CR-INV": "Ajuste de residuo de valuación: se elimina el valor que quedó sin cantidad en inventario.",
    "R06-DR-INV": "Ajuste de residuo de valuación: se elimina el valor negativo que quedó sin cantidad en inventario.",
    "R06-DR-PPV": "Contrapartida del residuo en variación de precio de compra, según la política INVENTORY.",
    "R06-CR-PPV": "Contrapartida del residuo en variación de precio de compra, según la política INVENTORY.",
    "R06-DR-ADJ": "Contrapartida del residuo en ajuste de inventario, según la política INVENTORY.",
    "R06-CR-ADJ": "Contrapartida del residuo en ajuste de inventario, según la política INVENTORY."}',
  'INV-MOV', DATE '2026-01-01', 'DRAFT');
