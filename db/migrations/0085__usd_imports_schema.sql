-- USD1-01 · Imports and foreign suppliers in USD: schema only. Baseline docs/architecture/usd1/frozen-baseline-usd1.md; approved errata
-- E-USD-1…9 and E-USD1-01-1…7. The books stay in pesos: every amount column that exists today keeps its peso value; USD documents add
-- their currency, rate and USD amounts (E-USD-1, E-USD1-01-2).

-- ---------------------------------------------------------------------------------------------
-- Exchange rates (E-USD-2, E-USD1-01-1): one per currency and day, 4 decimals (DOP per USD, Banco Central selling rate), prepared and
-- approved by someone else; an approved rate never changes — a correction is a new rate of the same day that supersedes it.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fin.exchange_rate (
  rate_id      uuid          NOT NULL,
  company_id   uuid          NOT NULL,
  currency     char(3)       NOT NULL,
  rate_date    date          NOT NULL,
  rate         numeric(18,4) NOT NULL,
  source       text          NOT NULL,
  status       text          NOT NULL,
  prepared_by  uuid          NOT NULL,
  approved_by  uuid,
  approved_at  timestamptz,
  version      bigint        NOT NULL,
  CONSTRAINT exchange_rate_pk PRIMARY KEY (rate_id),
  CONSTRAINT exchange_rate_company_uq UNIQUE (company_id, rate_id),
  CONSTRAINT exchange_rate_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT exchange_rate_prepared_fk FOREIGN KEY (prepared_by) REFERENCES iam.user (user_id),
  CONSTRAINT exchange_rate_approved_fk FOREIGN KEY (approved_by) REFERENCES iam.user (user_id),
  CONSTRAINT exchange_rate_currency CHECK (currency = 'USD'),
  CONSTRAINT exchange_rate_positive CHECK (rate > 0),
  CONSTRAINT exchange_rate_source_length CHECK (length(btrim(source)) BETWEEN 1 AND 200),
  CONSTRAINT exchange_rate_status CHECK (status IN ('DRAFT', 'ACTIVE', 'SUPERSEDED', 'DISCARDED')),
  CONSTRAINT exchange_rate_approved CHECK ((status IN ('ACTIVE', 'SUPERSEDED')) = (approved_by IS NOT NULL AND approved_at IS NOT NULL)),
  CONSTRAINT exchange_rate_version_positive CHECK (version >= 1)
);
CREATE UNIQUE INDEX exchange_rate_one_active ON fin.exchange_rate (company_id, currency, rate_date) WHERE status = 'ACTIVE';

CREATE FUNCTION fin.exchange_rate_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'fin.exchange_rate rows cannot be deleted';
  END IF;
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'DRAFT' OR NEW.version <> 1 THEN
      RAISE EXCEPTION 'fin.exchange_rate: a rate is prepared as DRAFT with version 1';
    END IF;
    RETURN NEW;
  END IF;
  IF ROW(NEW.rate_id, NEW.company_id, NEW.currency, NEW.rate_date, NEW.prepared_by)
     IS DISTINCT FROM ROW(OLD.rate_id, OLD.company_id, OLD.currency, OLD.rate_date, OLD.prepared_by)
     OR NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'fin.exchange_rate: identity columns are immutable and the version increases by 1';
  END IF;
  IF OLD.status <> 'DRAFT' AND ROW(NEW.rate, NEW.source) IS DISTINCT FROM ROW(OLD.rate, OLD.source) THEN
    RAISE EXCEPTION 'fin.exchange_rate: an approved rate never changes; prepare a new rate of the same day';
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
       (OLD.status = 'DRAFT' AND NEW.status IN ('ACTIVE', 'DISCARDED'))
    OR (OLD.status = 'ACTIVE' AND NEW.status = 'SUPERSEDED')) THEN
    RAISE EXCEPTION 'fin.exchange_rate: % → % is not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER exchange_rate_guard BEFORE INSERT OR UPDATE OR DELETE ON fin.exchange_rate FOR EACH ROW EXECUTE FUNCTION fin.exchange_rate_guard();
CREATE TRIGGER exchange_rate_no_truncate BEFORE TRUNCATE ON fin.exchange_rate FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER exchange_rate_four_eyes BEFORE INSERT OR UPDATE ON fin.exchange_rate
  FOR EACH ROW EXECUTE FUNCTION core.four_eyes('approved_by', 'prepared_by', 'exchange_rate_four_eyes');
CREATE CONSTRAINT TRIGGER exchange_rate_evidence_on_insert AFTER INSERT ON fin.exchange_rate
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('ExchangeRate', 'rate_id');
CREATE CONSTRAINT TRIGGER exchange_rate_evidence_on_change AFTER UPDATE ON fin.exchange_rate
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('ExchangeRate', 'rate_id');

-- ---------------------------------------------------------------------------------------------
-- Purchase orders and supplier invoices in USD (E-USD-3): a foreign supplier deals in USD, a local one in pesos. An order's prices are
-- in its currency; an invoice keeps its peso columns (net, total) at its rate and adds the USD amounts; its number is the supplier's
-- own invoice number (no NCF), its ITBIS and withholdings none.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE pur.purchase_order
  ADD COLUMN currency char(3) NOT NULL DEFAULT 'DOP',
  ADD CONSTRAINT purchase_order_currency CHECK (currency IN ('DOP', 'USD'));

ALTER TABLE pur.supplier_invoice
  ADD COLUMN currency char(3) NOT NULL DEFAULT 'DOP',
  ADD COLUMN exchange_rate numeric(18,4),
  ADD COLUMN total_amount_fc numeric(19,4),
  ADD CONSTRAINT supplier_invoice_currency CHECK (currency IN ('DOP', 'USD')),
  ADD CONSTRAINT supplier_invoice_fc CHECK ((currency = 'DOP' AND exchange_rate IS NULL AND total_amount_fc IS NULL)
    OR (currency = 'USD' AND exchange_rate IS NOT NULL AND total_amount_fc IS NOT NULL AND exchange_rate > 0 AND total_amount_fc >= 0)),
  DROP CONSTRAINT supplier_invoice_ncf_format,
  ADD CONSTRAINT supplier_invoice_ncf_format CHECK (
    (currency = 'DOP' AND supplier_fiscal_number ~ '^(B[0-9]{10}|E[0-9]{12})$')
    OR (currency = 'USD' AND length(btrim(supplier_fiscal_number)) BETWEEN 1 AND 40 AND supplier_fiscal_number = btrim(supplier_fiscal_number)));

ALTER TABLE pur.supplier_invoice_line
  ADD COLUMN unit_price_fc numeric(19,6),
  ADD COLUMN net_amount_fc numeric(19,4),
  ADD CONSTRAINT supplier_invoice_line_fc CHECK ((unit_price_fc IS NULL) = (net_amount_fc IS NULL) AND (net_amount_fc IS NULL OR net_amount_fc > 0));

-- The document's currency follows its supplier: FOREIGN ⇔ USD (E-USD-3).
CREATE FUNCTION pur.document_currency_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF (NEW.currency = 'USD') IS DISTINCT FROM ((SELECT party_kind FROM md.party WHERE party_id = NEW.party_id) = 'FOREIGN') THEN
    RAISE EXCEPTION '%.%: a foreign supplier deals in USD and a local one in pesos', TG_TABLE_SCHEMA, TG_TABLE_NAME;
  END IF;
  IF TG_OP = 'UPDATE' AND NEW.currency IS DISTINCT FROM OLD.currency THEN
    RAISE EXCEPTION '%.%: the currency never changes', TG_TABLE_SCHEMA, TG_TABLE_NAME;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER purchase_order_currency_guard BEFORE INSERT OR UPDATE ON pur.purchase_order FOR EACH ROW EXECUTE FUNCTION pur.document_currency_guard();
CREATE TRIGGER supplier_invoice_currency_guard BEFORE INSERT OR UPDATE ON pur.supplier_invoice FOR EACH ROW EXECUTE FUNCTION pur.document_currency_guard();

-- ---------------------------------------------------------------------------------------------
-- The DUA (E-USD-6, E-USD1-01-4): the customs declaration of a shipment — CIF value, duties, ITBIS, other charges in pesos — owed to
-- the DGA (a local supplier) as a payable paid like any other.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE pur.customs_declaration (
  dua_id             uuid                  NOT NULL,
  company_id         uuid                  NOT NULL,
  dua_no             text                  NOT NULL,
  dua_date           date                  NOT NULL,
  party_id           uuid                  NOT NULL,
  cif_amount         numeric(19,4)         NOT NULL,
  duties_amount      numeric(19,4)         NOT NULL,
  itbis_amount       numeric(19,4)         NOT NULL,
  other_amount       numeric(19,4)         NOT NULL,
  due_date           date                  NOT NULL,
  status             text                  NOT NULL,
  accounting_status  fin.accounting_status NOT NULL,
  posting_event_id   uuid,
  created_by         uuid                  NOT NULL,
  version            bigint                NOT NULL,
  CONSTRAINT customs_declaration_pk PRIMARY KEY (dua_id),
  CONSTRAINT customs_declaration_company_uq UNIQUE (company_id, dua_id),
  CONSTRAINT customs_declaration_party_fk FOREIGN KEY (company_id, party_id) REFERENCES md.party (company_id, party_id),
  CONSTRAINT customs_declaration_event_fk FOREIGN KEY (company_id, posting_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT customs_declaration_created_fk FOREIGN KEY (created_by) REFERENCES iam.user (user_id),
  CONSTRAINT customs_declaration_no_length CHECK (length(btrim(dua_no)) BETWEEN 1 AND 40 AND dua_no = btrim(dua_no)),
  CONSTRAINT customs_declaration_amounts CHECK (cif_amount > 0 AND duties_amount >= 0 AND itbis_amount >= 0 AND other_amount >= 0
    AND duties_amount + itbis_amount + other_amount > 0
    AND cif_amount = round(cif_amount, 2) AND duties_amount = round(duties_amount, 2) AND itbis_amount = round(itbis_amount, 2) AND other_amount = round(other_amount, 2)),
  CONSTRAINT customs_declaration_dates CHECK (due_date >= dua_date),
  CONSTRAINT customs_declaration_status CHECK (status IN ('DRAFT', 'POSTED', 'REVERSED', 'CANCELLED')),
  CONSTRAINT customs_declaration_version_positive CHECK (version >= 1)
);
CREATE UNIQUE INDEX customs_declaration_no_uq ON pur.customs_declaration (company_id, dua_no) WHERE status <> 'CANCELLED';

-- ---------------------------------------------------------------------------------------------
-- The import settlement (E-USD-5, E-USD1-01-5): per shipment, the documents whose cost it gathers and its allocation over the receipt
-- lines by value; prepared by Cuentas por pagar, approved (and posted) by the Controller.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE pur.import_settlement (
  settlement_id      uuid                  NOT NULL,
  company_id         uuid                  NOT NULL,
  settlement_no      text                  NOT NULL,
  plant_id           uuid                  NOT NULL,
  settlement_date    date                  NOT NULL,
  reference          text,
  status             text                  NOT NULL,
  accounting_status  fin.accounting_status NOT NULL,
  posting_event_id   uuid,
  prepared_by        uuid                  NOT NULL,
  approved_by        uuid,
  approved_at        timestamptz,
  version            bigint                NOT NULL,
  CONSTRAINT import_settlement_pk PRIMARY KEY (settlement_id),
  CONSTRAINT import_settlement_company_uq UNIQUE (company_id, settlement_id),
  CONSTRAINT import_settlement_no_uq UNIQUE (company_id, settlement_no),
  CONSTRAINT import_settlement_plant_fk FOREIGN KEY (company_id, plant_id) REFERENCES md.plant (company_id, plant_id),
  CONSTRAINT import_settlement_event_fk FOREIGN KEY (company_id, posting_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT import_settlement_prepared_fk FOREIGN KEY (prepared_by) REFERENCES iam.user (user_id),
  CONSTRAINT import_settlement_approved_fk FOREIGN KEY (approved_by) REFERENCES iam.user (user_id),
  CONSTRAINT import_settlement_no_format CHECK (settlement_no ~ '^LI-[0-9]{4}-[0-9]{6}$'),
  CONSTRAINT import_settlement_reference_length CHECK (reference IS NULL OR length(btrim(reference)) BETWEEN 1 AND 80),
  CONSTRAINT import_settlement_status CHECK (status IN ('DRAFT', 'POSTED', 'REVERSED', 'CANCELLED')),
  CONSTRAINT import_settlement_approved CHECK ((status IN ('POSTED', 'REVERSED')) = (approved_by IS NOT NULL AND approved_at IS NOT NULL)),
  CONSTRAINT import_settlement_version_positive CHECK (version >= 1)
);
CREATE TRIGGER import_settlement_four_eyes BEFORE INSERT OR UPDATE ON pur.import_settlement
  FOR EACH ROW EXECUTE FUNCTION core.four_eyes('approved_by', 'prepared_by', 'import_settlement_four_eyes');
CREATE CONSTRAINT TRIGGER import_settlement_evidence_on_insert AFTER INSERT ON pur.import_settlement
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('ImportSettlement', 'settlement_id');
CREATE CONSTRAINT TRIGGER import_settlement_evidence_on_change AFTER UPDATE ON pur.import_settlement
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('ImportSettlement', 'settlement_id');
CREATE CONSTRAINT TRIGGER customs_declaration_evidence_on_insert AFTER INSERT ON pur.customs_declaration
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('CustomsDeclaration', 'dua_id');
CREATE CONSTRAINT TRIGGER customs_declaration_evidence_on_change AFTER UPDATE ON pur.customs_declaration
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('CustomsDeclaration', 'dua_id');

-- The documents of a shipment: the foreign supplier's invoice (its goods, already in inventory at the receipt rate), expense invoices
-- (freight, insurance, agent, local transport — GAS-1) and the DUA (duties and other charges; its ITBIS is recoverable, never cost).
CREATE TABLE pur.import_settlement_document (
  settlement_id   uuid          NOT NULL,
  company_id      uuid          NOT NULL,
  document_kind   text          NOT NULL,
  document_id     uuid          NOT NULL,
  cost_amount     numeric(19,4) NOT NULL,
  CONSTRAINT import_settlement_document_pk PRIMARY KEY (settlement_id, document_kind, document_id),
  CONSTRAINT import_settlement_document_header_fk FOREIGN KEY (company_id, settlement_id) REFERENCES pur.import_settlement (company_id, settlement_id),
  CONSTRAINT import_settlement_document_kind CHECK (document_kind IN ('SUPPLIER_INVOICE', 'EXPENSE_INVOICE', 'CUSTOMS_DECLARATION')),
  CONSTRAINT import_settlement_document_amount CHECK (cost_amount >= 0 AND cost_amount = round(cost_amount, 2))
);
-- A document is settled in one settlement only (a draft that is cancelled first deletes its documents).
CREATE UNIQUE INDEX import_settlement_document_once ON pur.import_settlement_document (company_id, document_kind, document_id);

-- The allocation over the received lines: their receipt value and the cost added (by value; the cent left goes to the largest line).
CREATE TABLE pur.import_settlement_allocation (
  settlement_id          uuid          NOT NULL,
  company_id             uuid          NOT NULL,
  goods_receipt_line_id  uuid          NOT NULL,
  item_id                uuid          NOT NULL,
  receipt_value          numeric(19,4) NOT NULL,
  added_cost             numeric(19,4) NOT NULL,
  CONSTRAINT import_settlement_allocation_pk PRIMARY KEY (settlement_id, goods_receipt_line_id),
  CONSTRAINT import_settlement_allocation_header_fk FOREIGN KEY (company_id, settlement_id) REFERENCES pur.import_settlement (company_id, settlement_id),
  CONSTRAINT import_settlement_allocation_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT import_settlement_allocation_amounts CHECK (receipt_value > 0 AND added_cost >= 0 AND added_cost = round(added_cost, 2))
);
CREATE UNIQUE INDEX import_settlement_allocation_once ON pur.import_settlement_allocation (company_id, goods_receipt_line_id);

-- Documents and allocations are written while the settlement is DRAFT, never afterwards.
CREATE FUNCTION pur.import_settlement_child_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  v_settlement uuid := CASE WHEN TG_OP = 'DELETE' THEN OLD.settlement_id ELSE NEW.settlement_id END;
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pur.import_settlement s WHERE s.settlement_id = v_settlement AND s.status = 'DRAFT') THEN
    RAISE EXCEPTION '%.%: written only while the settlement is DRAFT', TG_TABLE_SCHEMA, TG_TABLE_NAME;
  END IF;
  RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END $$;
CREATE TRIGGER import_settlement_document_guard BEFORE INSERT OR UPDATE OR DELETE ON pur.import_settlement_document
  FOR EACH ROW EXECUTE FUNCTION pur.import_settlement_child_guard();
CREATE TRIGGER import_settlement_allocation_guard BEFORE INSERT OR UPDATE OR DELETE ON pur.import_settlement_allocation
  FOR EACH ROW EXECUTE FUNCTION pur.import_settlement_child_guard();

-- ---------------------------------------------------------------------------------------------
-- Payables in USD and the DGA's payable for a DUA (E-USD-3, E-USD-6): the peso columns stay; USD documents add their USD amounts.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE fin.ap_document
  ADD COLUMN currency char(3) NOT NULL DEFAULT 'DOP',
  ADD COLUMN original_amount_fc numeric(19,4),
  ADD COLUMN open_amount_fc numeric(19,4),
  DROP CONSTRAINT ap_document_type,
  ADD CONSTRAINT ap_document_type CHECK (doc_type IN ('SUPPLIER_INVOICE', 'CUSTOMS_DECLARATION')),
  DROP CONSTRAINT ap_document_invoice_fk,
  ADD CONSTRAINT ap_document_currency CHECK (currency IN ('DOP', 'USD')),
  ADD CONSTRAINT ap_document_fc CHECK ((currency = 'DOP' AND original_amount_fc IS NULL AND open_amount_fc IS NULL)
    OR (currency = 'USD' AND original_amount_fc IS NOT NULL AND open_amount_fc IS NOT NULL AND original_amount_fc > 0 AND open_amount_fc >= 0
        AND open_amount_fc <= original_amount_fc));

-- The source of a payable is its invoice or its DUA (the foreign key the single column cannot express).
CREATE FUNCTION fin.ap_document_source_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.doc_type = 'SUPPLIER_INVOICE' AND NOT EXISTS (
       SELECT 1 FROM pur.supplier_invoice si WHERE si.company_id = NEW.company_id AND si.si_id = NEW.source_doc_id AND si.currency = NEW.currency) THEN
    RAISE EXCEPTION 'fin.ap_document: the source invoice does not exist or is in another currency';
  END IF;
  IF NEW.doc_type = 'CUSTOMS_DECLARATION' AND (NEW.currency <> 'DOP' OR NOT EXISTS (
       SELECT 1 FROM pur.customs_declaration d WHERE d.company_id = NEW.company_id AND d.dua_id = NEW.source_doc_id)) THEN
    RAISE EXCEPTION 'fin.ap_document: the source DUA does not exist (a DUA is owed in pesos)';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER ap_document_source_guard BEFORE INSERT ON fin.ap_document FOR EACH ROW EXECUTE FUNCTION fin.ap_document_source_guard();

-- ---------------------------------------------------------------------------------------------
-- Bank accounts and payments in USD (E-USD-7, E-USD-9): a USD account; a payment keeps its peso amount and, in USD, its USD amount and
-- the bank's rate; an application to a USD payable keeps the USD it settles.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE fin.bank_account
  DROP CONSTRAINT bank_account_currency_vs2,
  ADD CONSTRAINT bank_account_currency CHECK (currency IN ('DOP', 'USD'));

ALTER TABLE fin.payment
  ADD COLUMN amount_fc numeric(19,4),
  ADD COLUMN exchange_rate numeric(18,4),
  DROP CONSTRAINT payment_currency_vs2,
  ADD CONSTRAINT payment_currency CHECK (currency IN ('DOP', 'USD')),
  ADD CONSTRAINT payment_fc CHECK ((amount_fc IS NULL) = (exchange_rate IS NULL) AND (amount_fc IS NULL OR (amount_fc > 0 AND exchange_rate > 0))
    AND (currency = 'DOP' OR amount_fc IS NOT NULL));

ALTER TABLE fin.ap_application
  ADD COLUMN amount_fc numeric(19,4),
  ADD CONSTRAINT ap_application_fc CHECK (amount_fc IS NULL OR amount_fc > 0);

-- ---------------------------------------------------------------------------------------------
-- The ledger stays in pesos (E-USD1-01-3): a line of a USD control (foreign payables, USD banks) also keeps its USD amount, to reconcile
-- and revalue in both currencies.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE fin.gl_entry
  ADD COLUMN amount_fc numeric(19,4),
  DROP CONSTRAINT gl_entry_currency_vs1,
  ADD CONSTRAINT gl_entry_currency CHECK ((currency = 'DOP' AND amount_fc IS NULL)
    OR (currency = 'USD' AND amount_fc IS NOT NULL AND amount_fc > 0 AND amount_fc = round(amount_fc, 2) AND account_role IN ('AP_FOREIGN', 'BANK')));

-- ---------------------------------------------------------------------------------------------
-- Account roles (E-USD1-01-6), seeded unmapped; the Controller maps them (A-01).
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.account_role (role_code, is_control, description, name) VALUES
  ('AP_FOREIGN', true, 'Cuentas por pagar a proveedores del exterior, en pesos con su saldo en USD (subledger AP)', 'Proveedores del exterior'),
  ('IMPORT_CLEARING', true, 'Mercancía importada recibida cuyo costo espera la liquidación de importación', 'Importaciones por liquidar'),
  ('FX_GAIN', false, 'Ganancia cambiaria realizada al pagar o cobrar en dólares', 'Ganancia cambiaria'),
  ('FX_LOSS', false, 'Pérdida cambiaria realizada al pagar o cobrar en dólares', 'Pérdida cambiaria'),
  ('FX_UNREALIZED', false, 'Diferencia cambiaria no realizada de la revaluación de cierre de mes (se reversa el día siguiente)', 'Diferencia cambiaria no realizada');

ALTER TABLE fin.gl_entry
  DROP CONSTRAINT gl_entry_role_subledger,
  ADD CONSTRAINT gl_entry_role_subledger CHECK (
    (account_role NOT IN ('RAW_MATERIAL', 'FINISHED_GOODS', 'FINISHED_GOODS_IN_TRANSIT') OR subledger_type = 'INV')
    AND (account_role NOT IN ('AP_CONTROL', 'AP_FOREIGN') OR subledger_type = 'AP')
    AND (account_role NOT IN ('AR_CONTROL', 'CONTRACT_ASSET', 'UNBILLED_RECEIVABLE', 'UNAPPLIED_RECEIPTS', 'CASH_IN_TRANSIT') OR subledger_type = 'AR')
    AND ((account_role = 'WIP') = (subledger_type IS NOT DISTINCT FROM 'WIP'))
    AND ((account_role = 'BANK') = (subledger_type IS NOT DISTINCT FROM 'BANK')));

-- ---------------------------------------------------------------------------------------------
-- Permissions and segregation of duties (E-USD1-01-7).
-- ---------------------------------------------------------------------------------------------
INSERT INTO iam.permission (permission_code, access) VALUES
  ('exchange_rate:prepare', 'WRITE'), ('exchange_rate:approve', 'WRITE'), ('import_settlement:prepare', 'WRITE'), ('import_settlement:approve', 'WRITE');
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES ('TESORERO', 'exchange_rate:prepare'), ('CONTADOR', 'exchange_rate:prepare'), ('CONTROLLER', 'exchange_rate:approve'),
             ('CUENTAS_POR_PAGAR', 'import_settlement:prepare'), ('CONTROLLER', 'import_settlement:approve')) AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;
INSERT INTO iam.sod_rule (permission_a, permission_b)
SELECT least(a, b), greatest(a, b) FROM (VALUES ('exchange_rate:prepare', 'exchange_rate:approve'), ('import_settlement:prepare', 'import_settlement:approve')) AS v (a, b);

GRANT SELECT, INSERT ON fin.exchange_rate, pur.customs_declaration, pur.import_settlement, pur.import_settlement_document, pur.import_settlement_allocation
  TO rochell_app;
GRANT UPDATE (status, approved_by, approved_at, version) ON fin.exchange_rate TO rochell_app;
GRANT UPDATE (status, accounting_status, posting_event_id, version) ON pur.customs_declaration TO rochell_app;
GRANT UPDATE (status, accounting_status, posting_event_id, approved_by, approved_at, reference, settlement_date, version) ON pur.import_settlement TO rochell_app;
GRANT DELETE ON pur.import_settlement_document, pur.import_settlement_allocation TO rochell_app;
GRANT UPDATE (open_amount_fc) ON fin.ap_document TO rochell_app;
