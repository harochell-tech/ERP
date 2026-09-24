-- Test fixture (TST-01): a value-only valuation adjustment, used by tests to build a consistent orphan value (qty 0, value ≠ 0)
-- for R-06 (IV-03). Never present in db/migrations.
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type) VALUES
  ('0192f000-0000-7000-8000-0000000000f3', 'TEST.VALUE', 'TestValueAdjusted');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, effective_from, status) VALUES
  ('0192f000-0000-7000-8000-0000000000f3', 1,
   '{"lines": [
      {"code": "TV-DR-INV", "side": "DEBIT", "account_role": "RAW_MATERIAL", "amount": "adjustment", "dimensions": ["plant", "item"], "subledger": "INV"},
      {"code": "TV-CR", "side": "CREDIT", "account_role": "TEST_INCOME", "amount": "adjustment", "dimensions": []},
      {"code": "TV-CR-INV", "side": "CREDIT", "account_role": "RAW_MATERIAL", "amount": "adjustment", "dimensions": ["plant", "item"], "subledger": "INV"},
      {"code": "TV-DR", "side": "DEBIT", "account_role": "TEST_EXPENSE", "amount": "adjustment", "dimensions": []}
    ]}',
   '{"TV-DR-INV": "Test value up", "TV-CR": "Test value up counterpart", "TV-CR-INV": "Test value down", "TV-DR": "Test value down counterpart"}',
   'INV-MOV', '2020-01-01', 'DRAFT');
