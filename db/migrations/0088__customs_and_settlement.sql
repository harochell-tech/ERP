-- USD1-04 · The DUA and the import settlement (approved errata E-USD1-04-1…9).
--   - the DUA names its plant and is registered and posted in one step (P-39): duties and other charges to «Importaciones por liquidar»,
--     its ITBIS recoverable, the total owed to the DGA as a payable paid like any other (E-USD1-04-1/2);
--   - fin.ap_source: the document behind each payable (supplier invoice or DUA) with its number, for Treasury's payment screens;
--   - a settlement gathers posted documents (the foreign goods invoices whose lines receive the cost, expense invoices and DUAs that bring it)
--     and is posted with P-40 (E-USD1-04-3…6); once reversed its documents are free for another one (E-USD1-04-7).

-- ---------------------------------------------------------------------------------------------
-- The DUA's plant (E-USD1-04-1).
-- ---------------------------------------------------------------------------------------------
ALTER TABLE pur.customs_declaration
  ADD COLUMN plant_id uuid NOT NULL,
  ADD CONSTRAINT customs_declaration_plant_fk FOREIGN KEY (company_id, plant_id) REFERENCES md.plant (company_id, plant_id),
  ADD CONSTRAINT customs_declaration_posted CHECK ((status = 'DRAFT') = (accounting_status = 'NOT_POSTED')
    AND (status <> 'POSTED' OR (accounting_status = 'POSTED' AND posting_event_id IS NOT NULL))
    AND (status <> 'REVERSED' OR accounting_status = 'REVERSED'));

-- ---------------------------------------------------------------------------------------------
-- The source of each payable, with the number shown on payment screens: the supplier's fiscal number, or «DUA <number>».
-- ---------------------------------------------------------------------------------------------
CREATE VIEW fin.ap_source AS
SELECT d.ap_doc_id, d.doc_type, i.si_id AS source_id, i.supplier_fiscal_number AS doc_number, i.accounting_status::text AS accounting_status
FROM fin.ap_document d JOIN pur.supplier_invoice i ON i.si_id = d.source_doc_id
WHERE d.doc_type = 'SUPPLIER_INVOICE'
UNION ALL
SELECT d.ap_doc_id, d.doc_type, c.dua_id, 'DUA ' || c.dua_no, c.accounting_status::text
FROM fin.ap_document d JOIN pur.customs_declaration c ON c.dua_id = d.source_doc_id
WHERE d.doc_type = 'CUSTOMS_DECLARATION';
GRANT SELECT ON fin.ap_source TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- A document (and so its lines) is in one live settlement — DRAFT or POSTED — at a time; a REVERSED or CANCELLED one releases it
-- (E-USD1-04-7). A cancelled draft deletes its rows; a reversed settlement keeps them as its record.
-- ---------------------------------------------------------------------------------------------
DROP INDEX pur.import_settlement_document_once;
DROP INDEX pur.import_settlement_allocation_once;

CREATE FUNCTION pur.import_settlement_document_live_once() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF EXISTS (SELECT 1 FROM pur.import_settlement_document d JOIN pur.import_settlement s ON s.settlement_id = d.settlement_id
             WHERE d.company_id = NEW.company_id AND d.document_kind = NEW.document_kind AND d.document_id = NEW.document_id
               AND d.settlement_id <> NEW.settlement_id AND s.status IN ('DRAFT', 'POSTED')) THEN
    RAISE EXCEPTION 'pur.import_settlement_document: % % is already in another settlement', NEW.document_kind, NEW.document_id
      USING ERRCODE = 'unique_violation';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER import_settlement_document_live_once BEFORE INSERT ON pur.import_settlement_document
  FOR EACH ROW EXECUTE FUNCTION pur.import_settlement_document_live_once();

-- E-USD1-04-8: until imported raw material is settled, the cost goes to the expense lines of foreign invoices only.
ALTER TABLE pur.import_settlement_allocation
  DROP CONSTRAINT import_settlement_allocation_kind,
  ADD CONSTRAINT import_settlement_allocation_kind CHECK (target_kind = 'EXPENSE_LINE');

ALTER TABLE pur.import_settlement
  ADD CONSTRAINT import_settlement_posted CHECK ((status = 'POSTED') = (accounting_status = 'POSTED' AND posting_event_id IS NOT NULL)
    AND (status <> 'REVERSED' OR accounting_status = 'REVERSED')
    AND (status NOT IN ('DRAFT', 'CANCELLED') OR accounting_status = 'NOT_POSTED'));

-- ---------------------------------------------------------------------------------------------
-- «Importaciones por liquidar» is a control account: its subledger is the DUA (subledger IMPORT, ref = dua_id), so what each DUA still
-- holds is its balance.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE fin.gl_entry
  DROP CONSTRAINT gl_entry_subledger_type,
  ADD CONSTRAINT gl_entry_subledger_type CHECK (subledger_type IS NULL OR subledger_type IN ('AP', 'INV', 'BANK', 'AR', 'WIP', 'IMPORT')),
  DROP CONSTRAINT gl_entry_role_subledger,
  ADD CONSTRAINT gl_entry_role_subledger CHECK (
    (account_role NOT IN ('RAW_MATERIAL', 'FINISHED_GOODS', 'FINISHED_GOODS_IN_TRANSIT') OR subledger_type = 'INV')
    AND (account_role NOT IN ('AP_CONTROL', 'AP_FOREIGN') OR subledger_type = 'AP')
    AND (account_role NOT IN ('AR_CONTROL', 'CONTRACT_ASSET', 'UNBILLED_RECEIVABLE', 'UNAPPLIED_RECEIPTS', 'CASH_IN_TRANSIT') OR subledger_type = 'AR')
    AND ((account_role = 'WIP') = (subledger_type IS NOT DISTINCT FROM 'WIP'))
    AND ((account_role = 'BANK') = (subledger_type IS NOT DISTINCT FROM 'BANK'))
    AND ((account_role = 'IMPORT_CLEARING') = (subledger_type IS NOT DISTINCT FROM 'IMPORT')));

-- ---------------------------------------------------------------------------------------------
-- P-39 (E-USD1-04-2): the DUA. P-40 (E-USD1-04-5): the settlement. Seeded DRAFT; the Controller approves them with the IMPORT_CLEARING
-- map (A-01). Reversals are the exact inverse of their journals.
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type) VALUES
  ('0192f001-0000-7000-8000-000000000031', 'P-39', 'CustomsDeclarationPosted'),
  ('0192f001-0000-7000-8000-000000000032', 'P-40', 'ImportSettlementPosted');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, effective_from, status) VALUES
  ('0192f001-0000-7000-8000-000000000031', 1,
   '{"lines": [
      {"code": "P39-DR-CLR", "side": "DEBIT", "account_role": "IMPORT_CLEARING", "amount": "import_cost", "dimensions": ["plant", "party"], "subledger": "IMPORT"},
      {"code": "P39-DR-ITBIS", "side": "DEBIT", "account_role": "ITBIS_RECOVERABLE", "amount": "itbis", "dimensions": ["plant"]},
      {"code": "P39-CR-AP", "side": "CREDIT", "account_role": "AP_CONTROL", "amount": "payable", "dimensions": ["party"], "subledger": "AP"}
    ]}',
   '{"P39-DR-CLR": "DUA {dua}: aranceles y otros cargos de aduana, por liquidar en el costo del embarque.",
     "P39-DR-ITBIS": "DUA {dua}: ITBIS pagado en aduana, adelantado.",
     "P39-CR-AP": "DUA {dua}: cuenta por pagar a la DGA."}',
   'AP-REC', DATE '2026-01-01', 'DRAFT'),
  ('0192f001-0000-7000-8000-000000000032', 1,
   '{"lines": [
      {"code": "P40-DR-COST", "side": "DEBIT", "account_role": "PURCHASE_EXPENSE", "amount": "added_cost", "dimensions": ["plant", "party"]},
      {"code": "P40-CR-CLR", "side": "CREDIT", "account_role": "IMPORT_CLEARING", "amount": "dua_cost", "dimensions": ["plant", "party"], "subledger": "IMPORT"},
      {"code": "P40-CR-EXP", "side": "CREDIT", "account_role": "PURCHASE_EXPENSE", "amount": "expense_cost", "dimensions": ["plant", "party"]}
    ]}',
   '{"P40-DR-COST": "Liquidación {settlement}: costo del embarque que corresponde a {description} ({category}).",
     "P40-CR-CLR": "Liquidación {settlement}: aranceles y otros cargos del DUA {dua} pasan al costo del embarque.",
     "P40-CR-EXP": "Liquidación {settlement}: {description} ({category}) de la factura {number} pasa al costo del embarque."}',
   'AP-REC', DATE '2026-01-01', 'DRAFT');
