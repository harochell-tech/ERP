-- PR-13b · Supplier invoice posting (R-04, R-05), AP and reversal (R-07, R-07B). Frozen Baseline §11.4, §13, E-10, E-11,
-- Patch 1 (P-1, P-4), approved errata E-PR13-3…7 and E-PR13b-1…3.

-- E-PR13-7: value-only price adjustments. As in PR-10, CHECKs compare movement_type as text because the new enum value
-- cannot be used as a literal in the transaction that adds it.
ALTER TYPE inv.movement_type ADD VALUE 'PRICE_ADJUSTMENT';

ALTER TABLE inv.inv_value_entry DROP CONSTRAINT inv_value_entry_sign;
ALTER TABLE inv.inv_value_entry ADD CONSTRAINT inv_value_entry_sign CHECK (
  (movement_type::text = 'RECEIPT' AND amount > 0) OR
  (movement_type::text IN ('ISSUE', 'RECEIPT_REVERSAL') AND amount < 0) OR
  movement_type::text IN ('RECEIPT_CORRECTION', 'VALUATION_ADJUSTMENT', 'VALUATION_REALLOCATION', 'PRICE_ADJUSTMENT'));
ALTER TABLE inv.inv_value_entry DROP CONSTRAINT inv_value_entry_quantity_link;
ALTER TABLE inv.inv_value_entry ADD CONSTRAINT inv_value_entry_quantity_link CHECK (
  (movement_type::text IN ('VALUATION_ADJUSTMENT', 'VALUATION_REALLOCATION', 'PRICE_ADJUSTMENT')) = (quantity_entry_id IS NULL));
-- A receipt reversal always points to its receipt; a price adjustment may be the exact reversal of another (R-07).
ALTER TABLE inv.inv_value_entry DROP CONSTRAINT inv_value_entry_reversal_link;
ALTER TABLE inv.inv_value_entry ADD CONSTRAINT inv_value_entry_reversal_link CHECK (
  (movement_type::text <> 'RECEIPT_REVERSAL' OR reverses_value_entry_id IS NOT NULL) AND
  (reverses_value_entry_id IS NULL OR movement_type::text IN ('RECEIPT_REVERSAL', 'PRICE_ADJUSTMENT')));
ALTER TABLE inv.inv_quantity_entry DROP CONSTRAINT inv_quantity_entry_no_value_only;
ALTER TABLE inv.inv_quantity_entry ADD CONSTRAINT inv_quantity_entry_no_value_only CHECK (
  movement_type::text NOT IN ('VALUATION_ADJUSTMENT', 'VALUATION_REALLOCATION', 'PRICE_ADJUSTMENT'));

-- ---------------------------------------------------------------------------------------------
-- Rules (DRAFT from 2026-01-01; the Controller approves them). E-PR13-6: R-04 closes with AP-REC, R-05 with INV-MOV;
-- each journal balances on its own, so R-05 carries the AP share of the price difference D (derived rule).
-- E-PR13b-2 dimensions: GRNI plant + supplier; AP supplier + AP subledger (the AP document); withholding supplier; ITBIS plant.
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type) VALUES
  ('0192f001-0000-7000-8000-000000000005', 'R-04', 'SupplierInvoicePosted'),
  ('0192f001-0000-7000-8000-000000000006', 'R-05', 'SupplierInvoicePosted'),
  ('0192f001-0000-7000-8000-000000000007', 'R-07B', 'SupplierInvoiceReversed');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, effective_from, status) VALUES
  ('0192f001-0000-7000-8000-000000000005', 1,
   '{"lines": [
      {"code": "R04-DR-GRNI", "side": "DEBIT", "account_role": "GRNI", "amount": "received_value", "dimensions": ["plant", "party"]},
      {"code": "R04-DR-ITBIS", "side": "DEBIT", "account_role": "ITBIS_RECOVERABLE", "amount": "recoverable_itbis", "dimensions": ["plant"]},
      {"code": "R04-CR-AP", "side": "CREDIT", "account_role": "AP_CONTROL", "amount": "payable_at_po_price", "dimensions": ["party"], "subledger": "AP"},
      {"code": "R04-CR-WHT", "side": "CREDIT", "account_role": "WITHHOLDING_PAYABLE", "amount": "withholding", "dimensions": ["party"]}
    ]}',
   '{"R04-DR-GRNI": "Factura {ncf}: se liquida lo recibido pendiente de factura (cantidad facturada × precio de la OC).",
     "R04-DR-ITBIS": "Factura {ncf}: ITBIS recuperable según la regla fiscal activa.",
     "R04-CR-AP": "Factura {ncf}: cuenta por pagar al proveedor al precio de la OC, más ITBIS, menos retenciones.",
     "R04-CR-WHT": "Factura {ncf}: retención a pagar a la DGII según la regla fiscal activa."}',
   'AP-REC', DATE '2026-01-01', 'DRAFT'),
  ('0192f001-0000-7000-8000-000000000006', 1,
   '{"lines": [
      {"code": "R05-DR-INV", "side": "DEBIT", "account_role": "RAW_MATERIAL", "amount": "covered_difference", "dimensions": ["plant", "item"], "subledger": "INV"},
      {"code": "R05-DR-PPV", "side": "DEBIT", "account_role": "PURCHASE_PRICE_VARIANCE", "amount": "uncovered_difference", "dimensions": ["plant", "item"]},
      {"code": "R05-CR-AP", "side": "CREDIT", "account_role": "AP_CONTROL", "amount": "price_difference", "dimensions": ["party"], "subledger": "AP"},
      {"code": "R05-CR-INV", "side": "CREDIT", "account_role": "RAW_MATERIAL", "amount": "covered_difference", "dimensions": ["plant", "item"], "subledger": "INV"},
      {"code": "R05-CR-PPV", "side": "CREDIT", "account_role": "PURCHASE_PRICE_VARIANCE", "amount": "uncovered_difference", "dimensions": ["plant", "item"]},
      {"code": "R05-DR-AP", "side": "DEBIT", "account_role": "AP_CONTROL", "amount": "price_difference", "dimensions": ["party"], "subledger": "AP"}
    ]}',
   '{"R05-DR-INV": "Factura {ncf}: parte de la diferencia de precio cubierta por el stock existente (STOCK_COVERAGE) sube el costo del inventario.",
     "R05-DR-PPV": "Factura {ncf}: parte de la diferencia de precio sin stock que la cubra va a variación de precio de compra.",
     "R05-CR-AP": "Factura {ncf}: la cuenta por pagar aumenta por la diferencia entre el precio facturado y el de la OC.",
     "R05-CR-INV": "Factura {ncf}: parte de la rebaja de precio cubierta por el stock existente baja el costo del inventario.",
     "R05-CR-PPV": "Factura {ncf}: parte de la rebaja de precio sin stock que la cubra va a variación de precio de compra.",
     "R05-DR-AP": "Factura {ncf}: la cuenta por pagar disminuye por la rebaja respecto al precio de la OC."}',
   'INV-MOV', DATE '2026-01-01', 'DRAFT'),
  ('0192f001-0000-7000-8000-000000000007', 1,
   '{"lines": [
      {"code": "R07B-DR-INV", "side": "DEBIT", "account_role": "RAW_MATERIAL", "amount": "reallocation", "dimensions": ["plant", "item"], "subledger": "INV"},
      {"code": "R07B-CR-PPV", "side": "CREDIT", "account_role": "PURCHASE_PRICE_VARIANCE", "amount": "reallocation", "dimensions": ["plant", "item"]},
      {"code": "R07B-CR-INV", "side": "CREDIT", "account_role": "RAW_MATERIAL", "amount": "reallocation", "dimensions": ["plant", "item"], "subledger": "INV"},
      {"code": "R07B-DR-PPV", "side": "DEBIT", "account_role": "PURCHASE_PRICE_VARIANCE", "amount": "reallocation", "dimensions": ["plant", "item"]}
    ]}',
   '{"R07B-DR-INV": "Reversa de la factura {ncf}: la parte de la diferencia de precio ya consumida no sale del inventario actual; se lleva a variación.",
     "R07B-CR-PPV": "Reversa de la factura {ncf}: contrapartida en variación de precio de compra.",
     "R07B-CR-INV": "Reversa de la factura {ncf}: ajuste del inventario actual por la parte de la rebaja ya consumida.",
     "R07B-DR-PPV": "Reversa de la factura {ncf}: contrapartida en variación de precio de compra."}',
   'INV-MOV', DATE '2026-01-01', 'DRAFT');
