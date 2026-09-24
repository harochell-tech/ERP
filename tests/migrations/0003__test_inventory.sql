-- R-T1 test fixture (TST-01): stock receipt and issue rules used only by tests. Never present in db/migrations.
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type) VALUES
  ('0192f000-0000-7000-8000-0000000000f1', 'TEST.RECEIPT', 'TestStockReceived'),
  ('0192f000-0000-7000-8000-0000000000f2', 'TEST.ISSUE', 'TestStockIssued');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, effective_from, status) VALUES
  ('0192f000-0000-7000-8000-0000000000f1', 1,
   '{"lines": [
      {"code": "TR-DR-INV", "side": "DEBIT", "account_role": "RAW_MATERIAL", "amount": "receipt_value", "dimensions": ["plant", "item"], "subledger": "INV"},
      {"code": "TR-CR", "side": "CREDIT", "account_role": "TEST_INCOME", "amount": "receipt_value", "dimensions": []}
    ]}',
   '{"TR-DR-INV": "Test receipt into inventory", "TR-CR": "Test receipt counterpart"}',
   'INV-MOV', '2020-01-01', 'DRAFT'),
  ('0192f000-0000-7000-8000-0000000000f2', 1,
   '{"lines": [
      {"code": "TI-DR", "side": "DEBIT", "account_role": "TEST_EXPENSE", "amount": "issue_value", "dimensions": ["plant"]},
      {"code": "TI-CR-INV", "side": "CREDIT", "account_role": "RAW_MATERIAL", "amount": "issue_value", "dimensions": ["plant", "item"], "subledger": "INV"}
    ]}',
   '{"TI-DR": "Test consumption", "TI-CR-INV": "Test issue from inventory"}',
   'INV-MOV', '2020-01-01', 'DRAFT');
