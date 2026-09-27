-- Test-only statement formats (VS2-05, E-VS2-05-1). The real banks' formats arrive in db/migrations from the owner's samples.
-- TEST_BANK: comma-separated, one header row, debit and credit columns, period and balances given with the import.
-- TEST_SIGNED: semicolon-separated windows-1252 with a decimal comma, a signed amount (negative = debit), a direction-free
-- layout, period and balances in the header cells and one footer row.
INSERT INTO fin.bank_statement_format (format_id, bank_code, version, definition, description) VALUES
  ('0192f0b5-0000-7000-8000-000000000001', 'TEST_BANK', 1,
   '{"encoding": "UTF-8", "delimiter": ",", "skip_rows": 1, "date_format": "dd/MM/yyyy", "decimal_separator": ".", "thousands_separator": ",",
     "columns": {"value_date": 0, "reference": 1, "description": 2, "debit": 3, "credit": 4}}',
   'Test format: debit and credit columns'),
  ('0192f0b5-0000-7000-8000-000000000002', 'TEST_SIGNED', 1,
   '{"encoding": "windows-1252", "delimiter": ";", "skip_rows": 4, "skip_trailing_rows": 1, "date_format": "yyyy-MM-dd",
     "decimal_separator": ",", "thousands_separator": ".",
     "columns": {"value_date": 0, "description": 1, "reference": 2, "amount": 3},
     "cells": {"period_from": {"row": 0, "column": 1}, "period_to": {"row": 0, "column": 2},
               "opening_balance": {"row": 1, "column": 1}, "closing_balance": {"row": 2, "column": 1}}}',
   'Test format: signed amount, header cells, footer');
