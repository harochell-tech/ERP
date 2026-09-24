-- PR-10 · Goods receipt reversal. Frozen Baseline §9.2, §11.2–11.3, Errata E-8 §5.2 (guards), Patch 1 P-4 (R-02 A/B),
-- approved errata E-PR10-1…4.

-- E-PR10-3: value-only reallocation entries (R-02B). A new enum value cannot be used as a literal in the transaction
-- that adds it, so the updated CHECKs compare movement_type as text.
ALTER TYPE inv.movement_type ADD VALUE 'VALUATION_REALLOCATION';

ALTER TABLE inv.inv_value_entry DROP CONSTRAINT inv_value_entry_sign;
ALTER TABLE inv.inv_value_entry ADD CONSTRAINT inv_value_entry_sign CHECK (
  (movement_type::text = 'RECEIPT' AND amount > 0) OR
  (movement_type::text IN ('ISSUE', 'RECEIPT_REVERSAL') AND amount < 0) OR
  movement_type::text IN ('RECEIPT_CORRECTION', 'VALUATION_ADJUSTMENT', 'VALUATION_REALLOCATION'));
ALTER TABLE inv.inv_value_entry DROP CONSTRAINT inv_value_entry_quantity_link;
ALTER TABLE inv.inv_value_entry ADD CONSTRAINT inv_value_entry_quantity_link CHECK (
  (movement_type::text IN ('VALUATION_ADJUSTMENT', 'VALUATION_REALLOCATION')) = (quantity_entry_id IS NULL));
ALTER TABLE inv.inv_value_entry ADD CONSTRAINT inv_value_entry_reversal_link CHECK (
  (movement_type::text = 'RECEIPT_REVERSAL') = (reverses_value_entry_id IS NOT NULL));
ALTER TABLE inv.inv_quantity_entry DROP CONSTRAINT inv_quantity_entry_no_value_only;
ALTER TABLE inv.inv_quantity_entry ADD CONSTRAINT inv_quantity_entry_no_value_only CHECK (
  movement_type::text NOT IN ('VALUATION_ADJUSTMENT', 'VALUATION_REALLOCATION'));
ALTER TABLE inv.inv_quantity_entry ADD CONSTRAINT inv_quantity_entry_reversal_link CHECK (
  (movement_type::text = 'RECEIPT_REVERSAL') = (reverses_quantity_entry_id IS NOT NULL));

-- ---------------------------------------------------------------------------------------------
-- Reversal document: one per receipt (UNIQUE), always POSTED (P-1), append-only.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE pur.goods_receipt_reversal (
  grr_id             uuid                  NOT NULL,
  company_id         uuid                  NOT NULL,
  reversed_gr_id     uuid                  NOT NULL,
  reason             text                  NOT NULL,
  accounting_status  fin.accounting_status NOT NULL,
  posting_event_id   uuid                  NOT NULL,
  version            bigint                NOT NULL,
  CONSTRAINT goods_receipt_reversal_pk PRIMARY KEY (grr_id),
  CONSTRAINT goods_receipt_reversal_company_uq UNIQUE (company_id, grr_id),
  CONSTRAINT goods_receipt_reversal_once UNIQUE (reversed_gr_id),
  CONSTRAINT goods_receipt_reversal_gr_fk FOREIGN KEY (company_id, reversed_gr_id) REFERENCES pur.goods_receipt (company_id, gr_id),
  CONSTRAINT goods_receipt_reversal_event_fk FOREIGN KEY (company_id, posting_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT goods_receipt_reversal_posted CHECK (accounting_status = 'POSTED'),
  CONSTRAINT goods_receipt_reversal_reason CHECK (length(btrim(reason)) > 0),
  CONSTRAINT goods_receipt_reversal_version CHECK (version = 1)
);
CREATE TRIGGER goods_receipt_reversal_append_only BEFORE UPDATE OR DELETE ON pur.goods_receipt_reversal FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER goods_receipt_reversal_no_truncate BEFORE TRUNCATE ON pur.goods_receipt_reversal FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- K-25 + ADR-027: the reversal has its REVERSAL journal (source = its event) and its history row; the receipt it reverses
-- is REVERSED in the same transaction.
CREATE FUNCTION pur.goods_receipt_reversal_evidence() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM fin.gl_journal WHERE company_id = NEW.company_id AND source_event_id = NEW.posting_event_id AND journal_type = 'REVERSAL') THEN
    RAISE EXCEPTION 'pur.goods_receipt_reversal %: POSTED without its reversal journal (K-25)', NEW.grr_id;
  END IF;
  IF NOT EXISTS (
    SELECT 1 FROM core.state_history
    WHERE aggregate_type = 'GoodsReceiptReversal' AND aggregate_id = NEW.grr_id AND to_state = 'POSTED' AND xmin = pg_current_xact_id()::xid) THEN
    RAISE EXCEPTION 'pur.goods_receipt_reversal %: without its state_history row (ADR-027)', NEW.grr_id;
  END IF;
  IF NOT EXISTS (
    SELECT 1 FROM pur.goods_receipt WHERE gr_id = NEW.reversed_gr_id AND document_status = 'REVERSED' AND accounting_status = 'REVERSED') THEN
    RAISE EXCEPTION 'pur.goods_receipt_reversal %: the receipt must be REVERSED in the same transaction', NEW.grr_id;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER goods_receipt_reversal_evidence AFTER INSERT ON pur.goods_receipt_reversal
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION pur.goods_receipt_reversal_evidence();

-- A receipt only becomes REVERSED together with its reversal document.
CREATE FUNCTION pur.goods_receipt_reversed_has_document() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.document_status = 'REVERSED' AND NOT EXISTS (
    SELECT 1 FROM pur.goods_receipt_reversal WHERE reversed_gr_id = NEW.gr_id) THEN
    RAISE EXCEPTION 'pur.goods_receipt %: REVERSED without a goods_receipt_reversal', NEW.gr_id;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER goods_receipt_reversed_has_document AFTER UPDATE ON pur.goods_receipt
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.document_status = 'REVERSED')
  EXECUTE FUNCTION pur.goods_receipt_reversed_has_document();

-- ---------------------------------------------------------------------------------------------
-- Rule R-02B (E-PR10-2, E-PR10-4): valuation reallocation after an exact reversal leaves an orphan or
-- non-positive value. Both side variants; the handler uses one pair. DRAFT until the Controller approves it.
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type)
VALUES ('0192f001-0000-7000-8000-000000000002', 'R-02B', 'GoodsReceiptReversed');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, effective_from, status)
VALUES ('0192f001-0000-7000-8000-000000000002', 1,
  '{"lines": [
     {"code": "R02B-DR-INV", "side": "DEBIT", "account_role": "RAW_MATERIAL", "amount": "reallocation", "dimensions": ["plant", "item"], "subledger": "INV"},
     {"code": "R02B-CR-PPV", "side": "CREDIT", "account_role": "PURCHASE_PRICE_VARIANCE", "amount": "reallocation", "dimensions": ["plant", "item"]},
     {"code": "R02B-CR-INV", "side": "CREDIT", "account_role": "RAW_MATERIAL", "amount": "reallocation", "dimensions": ["plant", "item"], "subledger": "INV"},
     {"code": "R02B-DR-PPV", "side": "DEBIT", "account_role": "PURCHASE_PRICE_VARIANCE", "amount": "reallocation", "dimensions": ["plant", "item"]}
   ]}',
  '{"R02B-DR-INV": "Reasignación de valuación tras la reversa {grr_id}: el inventario remanente vuelve a su costo promedio previo.",
    "R02B-CR-PPV": "Contrapartida en variación de precio de compra de la reasignación de la reversa {grr_id}.",
    "R02B-CR-INV": "Reasignación de valuación tras la reversa {grr_id}: se elimina valor huérfano del inventario.",
    "R02B-DR-PPV": "Contrapartida en variación de precio de compra de la reasignación de la reversa {grr_id}."}',
  'INV-MOV', DATE '2026-01-01', 'DRAFT');

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE pur.goods_receipt_reversal ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON pur.goods_receipt_reversal
  USING (company_id = nullif(current_setting('app.company_id', true), '')::uuid)
  WITH CHECK (company_id = nullif(current_setting('app.company_id', true), '')::uuid);
GRANT SELECT, INSERT ON pur.goods_receipt_reversal TO rochell_app;
