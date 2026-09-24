-- Test-only account roles and posting rule used by the posting engine tests (never present in production).
INSERT INTO fin.account_role (role_code, is_control, description) VALUES
  ('TEST_EXPENSE', false, 'Test expense'),
  ('TEST_INCOME', false, 'Test income'),
  ('TEST_CONTROL', true, 'Test control account (subledger AP)');

INSERT INTO fin.posting_rule (posting_rule_id, code, event_type) VALUES
  ('0192f000-0000-7000-8000-0000000000e1', 'TEST.POSTING', 'TestPosted');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, effective_from, status)
VALUES ('0192f000-0000-7000-8000-0000000000e1', 1,
  '{"lines": [
     {"code": "T-DR", "side": "DEBIT", "account_role": "TEST_EXPENSE", "amount": "amount", "dimensions": ["plant"]},
     {"code": "T-CTL", "side": "DEBIT", "account_role": "TEST_CONTROL", "amount": "amount", "dimensions": [], "subledger": "AP"},
     {"code": "T-CR", "side": "CREDIT", "account_role": "TEST_INCOME", "amount": "credit_amount", "dimensions": []}
   ]}',
  '{"T-DR": "Test debit", "T-CTL": "Test control debit", "T-CR": "Test credit"}',
  'INV-MOV', '2020-01-01', 'DRAFT');
