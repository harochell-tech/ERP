-- MFG1-02 · Standard cost revaluation (E-MFG1-10, E-MFG1-02-6…8, E-MFG1-02-10): posting rule REVAL, DRAFT until the Controller
-- approves it (A-01). Approving a new standard with stock brings the area's value to quantity × new standard: a VALUATION_ADJUSTMENT
-- value entry and REVAL. Amounts are never negative, so a revaluation up uses the UP pair and one down the DOWN pair.

INSERT INTO fin.posting_rule (posting_rule_id, code, event_type)
VALUES ('0192f001-0000-7000-8000-000000000023', 'REVAL', 'StandardCostRevalued');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, also_requires_components, effective_from, status)
VALUES ('0192f001-0000-7000-8000-000000000023', 1,
   '{"lines": [
      {"code": "REVAL-UP-DR-FG", "side": "DEBIT", "account_role": "FINISHED_GOODS", "amount": "revaluation", "dimensions": ["plant", "item"], "subledger": "INV"},
      {"code": "REVAL-UP-CR-REV", "side": "CREDIT", "account_role": "STANDARD_REVALUATION", "amount": "revaluation", "dimensions": ["plant"]},
      {"code": "REVAL-DN-DR-REV", "side": "DEBIT", "account_role": "STANDARD_REVALUATION", "amount": "revaluation", "dimensions": ["plant"]},
      {"code": "REVAL-DN-CR-FG", "side": "CREDIT", "account_role": "FINISHED_GOODS", "amount": "revaluation", "dimensions": ["plant", "item"], "subledger": "INV"}
    ]}',
   '{"REVAL-UP-DR-FG": "Revaluación de existencias al aprobar el costo estándar {new_unit_cost} (antes {previous_unit_cost}): {quantity} × estándar nuevo − valor anterior {previous_value}.",
     "REVAL-UP-CR-REV": "Contrapartida de la revaluación por aumento del costo estándar.",
     "REVAL-DN-DR-REV": "Contrapartida de la revaluación por baja del costo estándar.",
     "REVAL-DN-CR-FG": "Revaluación de existencias al aprobar el costo estándar {new_unit_cost} (antes {previous_unit_cost}): {quantity} × estándar nuevo − valor anterior {previous_value}."}',
   'INV-MOV', ARRAY[]::text[], DATE '2026-01-01', 'DRAFT');
