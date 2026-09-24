-- PR-07 · Inventory base. Frozen Baseline §8.5, Patch 1 (P-1, P-3, P-5), ADR-005, ADR-016, ADR-017, ADR-023,
-- approved errata E-PR05-8, E-PR07-1…8. Quantities in the item's base UOM (E-PR07-2); values in DOP with 2 decimals (E-PR07-1).

CREATE SCHEMA inv;
REVOKE ALL ON SCHEMA inv FROM PUBLIC;
GRANT USAGE ON SCHEMA inv TO rochell_app;

CREATE TYPE inv.movement_type AS ENUM ('RECEIPT', 'RECEIPT_REVERSAL', 'RECEIPT_CORRECTION', 'ISSUE', 'VALUATION_ADJUSTMENT');

-- Keys that let entries prove "location belongs to plant" and "plant belongs to valuation area" with plain FKs.
ALTER TABLE md.location ADD CONSTRAINT location_plant_location_uq UNIQUE (plant_id, location_id);
ALTER TABLE md.plant ADD CONSTRAINT plant_valuation_area_pair_uq UNIQUE (plant_id, valuation_area_id);

-- ---------------------------------------------------------------------------------------------
-- Lots (E-PR07-3): mandatory for raw materials; one per receipt line. Immutable.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE inv.lot (
  lot_id               uuid NOT NULL,
  company_id           uuid NOT NULL,
  item_id              uuid NOT NULL,
  lot_code             text NOT NULL,
  supplier_party_id    uuid,
  supplier_lot_number  text,
  created_event_id     uuid NOT NULL,
  CONSTRAINT lot_pk PRIMARY KEY (lot_id),
  CONSTRAINT lot_company_uq UNIQUE (company_id, lot_id),
  CONSTRAINT lot_item_uq UNIQUE (lot_id, item_id),
  CONSTRAINT lot_code_uq UNIQUE (company_id, lot_code),
  CONSTRAINT lot_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT lot_supplier_fk FOREIGN KEY (company_id, supplier_party_id) REFERENCES md.party (company_id, party_id),
  CONSTRAINT lot_event_fk FOREIGN KEY (company_id, created_event_id) REFERENCES core.domain_event (company_id, event_id)
);

-- ---------------------------------------------------------------------------------------------
-- Quantity ledger (ADR-016). Five temporal fields (ADR-023). Append-only; row_hash per E-PR07-6.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE inv.inv_quantity_entry (
  quantity_entry_id           uuid              NOT NULL,
  company_id                  uuid              NOT NULL,
  movement_type               inv.movement_type NOT NULL,
  plant_id                    uuid              NOT NULL,
  location_id                 uuid              NOT NULL,
  item_id                     uuid              NOT NULL,
  lot_id                      uuid              NOT NULL,
  quantity                    numeric(18,6)     NOT NULL,
  source_event_id             uuid              NOT NULL,
  source_document_type        text              NOT NULL,
  source_document_id          uuid              NOT NULL,
  source_line_id              uuid,
  reverses_quantity_entry_id  uuid,
  occurred_at                 timestamptz       NOT NULL,
  recorded_at                 timestamptz       NOT NULL,
  business_date               date              NOT NULL,
  posting_date                date              NOT NULL,
  row_hash                    bytea             NOT NULL,
  CONSTRAINT inv_quantity_entry_pk PRIMARY KEY (quantity_entry_id),
  CONSTRAINT inv_quantity_entry_company_uq UNIQUE (company_id, quantity_entry_id),
  CONSTRAINT inv_quantity_entry_single_reversal_uq UNIQUE (reverses_quantity_entry_id),
  CONSTRAINT inv_quantity_entry_plant_fk FOREIGN KEY (company_id, plant_id) REFERENCES md.plant (company_id, plant_id),
  CONSTRAINT inv_quantity_entry_location_fk FOREIGN KEY (company_id, location_id) REFERENCES md.location (company_id, location_id),
  CONSTRAINT inv_quantity_entry_location_plant_fk FOREIGN KEY (plant_id, location_id) REFERENCES md.location (plant_id, location_id),
  CONSTRAINT inv_quantity_entry_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT inv_quantity_entry_lot_fk FOREIGN KEY (company_id, lot_id) REFERENCES inv.lot (company_id, lot_id),
  CONSTRAINT inv_quantity_entry_lot_item_fk FOREIGN KEY (lot_id, item_id) REFERENCES inv.lot (lot_id, item_id),
  CONSTRAINT inv_quantity_entry_event_fk FOREIGN KEY (company_id, source_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT inv_quantity_entry_reverses_fk FOREIGN KEY (company_id, reverses_quantity_entry_id) REFERENCES inv.inv_quantity_entry (company_id, quantity_entry_id),
  CONSTRAINT inv_quantity_entry_nonzero CHECK (quantity <> 0),
  CONSTRAINT inv_quantity_entry_sign CHECK (
    (movement_type = 'RECEIPT' AND quantity > 0) OR
    (movement_type IN ('ISSUE', 'RECEIPT_REVERSAL') AND quantity < 0) OR
    movement_type = 'RECEIPT_CORRECTION'),
  CONSTRAINT inv_quantity_entry_no_value_only CHECK (movement_type <> 'VALUATION_ADJUSTMENT'),
  CONSTRAINT inv_quantity_entry_row_hash_length CHECK (octet_length(row_hash) = 32)
);

-- ---------------------------------------------------------------------------------------------
-- Value ledger (ADR-016): moving average per valuation area × item (ADR-005, E-PR07-7).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE inv.inv_value_entry (
  value_entry_id           uuid              NOT NULL,
  company_id               uuid              NOT NULL,
  movement_type            inv.movement_type NOT NULL,
  valuation_area_id        uuid              NOT NULL,
  plant_id                 uuid              NOT NULL,
  item_id                  uuid              NOT NULL,
  quantity_entry_id        uuid,
  amount                   numeric(19,4)     NOT NULL,
  source_event_id          uuid              NOT NULL,
  reverses_value_entry_id  uuid,
  occurred_at              timestamptz       NOT NULL,
  recorded_at              timestamptz       NOT NULL,
  business_date            date              NOT NULL,
  posting_date             date              NOT NULL,
  row_hash                 bytea             NOT NULL,
  CONSTRAINT inv_value_entry_pk PRIMARY KEY (value_entry_id),
  CONSTRAINT inv_value_entry_company_uq UNIQUE (company_id, value_entry_id),
  CONSTRAINT inv_value_entry_single_reversal_uq UNIQUE (reverses_value_entry_id),
  CONSTRAINT inv_value_entry_area_fk FOREIGN KEY (company_id, valuation_area_id) REFERENCES md.valuation_area (company_id, valuation_area_id),
  CONSTRAINT inv_value_entry_plant_area_fk FOREIGN KEY (plant_id, valuation_area_id) REFERENCES md.plant (plant_id, valuation_area_id),
  CONSTRAINT inv_value_entry_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT inv_value_entry_quantity_fk FOREIGN KEY (company_id, quantity_entry_id) REFERENCES inv.inv_quantity_entry (company_id, quantity_entry_id),
  CONSTRAINT inv_value_entry_event_fk FOREIGN KEY (company_id, source_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT inv_value_entry_reverses_fk FOREIGN KEY (company_id, reverses_value_entry_id) REFERENCES inv.inv_value_entry (company_id, value_entry_id),
  CONSTRAINT inv_value_entry_nonzero CHECK (amount <> 0),
  CONSTRAINT inv_value_entry_two_decimals CHECK (amount = round(amount, 2)),
  CONSTRAINT inv_value_entry_sign CHECK (
    (movement_type = 'RECEIPT' AND amount > 0) OR
    (movement_type IN ('ISSUE', 'RECEIPT_REVERSAL') AND amount < 0) OR
    movement_type IN ('RECEIPT_CORRECTION', 'VALUATION_ADJUSTMENT')),
  CONSTRAINT inv_value_entry_quantity_link CHECK ((movement_type = 'VALUATION_ADJUSTMENT') = (quantity_entry_id IS NULL)),
  CONSTRAINT inv_value_entry_row_hash_length CHECK (octet_length(row_hash) = 32)
);

-- ---------------------------------------------------------------------------------------------
-- Balances (projections maintained in the same transaction; never negative stock, CON-03).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE inv.inv_stock_balance (
  company_id   uuid          NOT NULL,
  plant_id     uuid          NOT NULL,
  location_id  uuid          NOT NULL,
  item_id      uuid          NOT NULL,
  lot_id       uuid          NOT NULL,
  quantity     numeric(18,6) NOT NULL,
  CONSTRAINT inv_stock_balance_pk PRIMARY KEY (location_id, item_id, lot_id),
  CONSTRAINT inv_stock_balance_location_fk FOREIGN KEY (plant_id, location_id) REFERENCES md.location (plant_id, location_id),
  CONSTRAINT inv_stock_balance_company_location_fk FOREIGN KEY (company_id, location_id) REFERENCES md.location (company_id, location_id),
  CONSTRAINT inv_stock_balance_lot_fk FOREIGN KEY (lot_id, item_id) REFERENCES inv.lot (lot_id, item_id),
  CONSTRAINT inv_stock_balance_non_negative CHECK (quantity >= 0)
);

CREATE TABLE inv.inv_valuation_balance (
  company_id         uuid          NOT NULL,
  valuation_area_id  uuid          NOT NULL,
  item_id            uuid          NOT NULL,
  quantity           numeric(18,6) NOT NULL,
  value              numeric(19,4) NOT NULL,
  CONSTRAINT inv_valuation_balance_pk PRIMARY KEY (valuation_area_id, item_id),
  CONSTRAINT inv_valuation_balance_area_fk FOREIGN KEY (company_id, valuation_area_id) REFERENCES md.valuation_area (company_id, valuation_area_id),
  CONSTRAINT inv_valuation_balance_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT inv_valuation_balance_non_negative CHECK (quantity >= 0)
);

-- ---------------------------------------------------------------------------------------------
-- E-PR05-8 / E-PR07-4: the GL ↔ value-entry link is 1:1 in both directions.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE fin.gl_entry
  ADD CONSTRAINT gl_entry_value_entry_fk FOREIGN KEY (company_id, inv_value_entry_id) REFERENCES inv.inv_value_entry (company_id, value_entry_id),
  ADD CONSTRAINT gl_entry_inv_link CHECK (
    (subledger_type IS DISTINCT FROM 'INV' AND inv_value_entry_id IS NULL)
    OR (subledger_type = 'INV' AND inv_value_entry_id IS NOT NULL AND subledger_ref = inv_value_entry_id
        AND plant_id IS NOT NULL AND item_id IS NOT NULL)),
  ADD CONSTRAINT gl_entry_role_subledger CHECK (
    (account_role <> 'RAW_MATERIAL' OR subledger_type = 'INV') AND (account_role <> 'AP_CONTROL' OR subledger_type = 'AP'));

-- P-1: every value entry has exactly one GL line (unique FK above) with the same signed amount, date, plant and item.
CREATE FUNCTION inv.value_entry_gl_link() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  links integer;
  gl_amount numeric;
  mismatched integer;
BEGIN
  SELECT count(*), coalesce(sum(debit - credit), 0),
         count(*) FILTER (WHERE posting_date <> NEW.posting_date OR plant_id <> NEW.plant_id OR item_id <> NEW.item_id)
    INTO links, gl_amount, mismatched
    FROM fin.gl_entry WHERE inv_value_entry_id = NEW.value_entry_id;
  IF links <> 1 OR gl_amount <> NEW.amount OR mismatched <> 0 THEN
    RAISE EXCEPTION 'inv.inv_value_entry %: needs exactly one GL line with the same amount, posting date, plant and item (P-1); found %, amount %', NEW.value_entry_id, links, gl_amount;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER inv_value_entry_gl_link AFTER INSERT ON inv.inv_value_entry
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION inv.value_entry_gl_link();

-- P-3 / E-PR07-8 (+ AT-01, AT-02): for the touched valuation area × item, balances equal their ledgers and the
-- value subledger equals the GL (role RAW_MATERIAL, plants of the area, item).
CREATE FUNCTION inv.check_valuation_position(p_company uuid, p_area uuid, p_item uuid) RETURNS void
  LANGUAGE plpgsql AS $$
DECLARE
  bal_qty numeric; bal_value numeric;
  entries_qty numeric; entries_value numeric; stock_qty numeric; gl_value numeric;
BEGIN
  SELECT coalesce(max(quantity), 0), coalesce(max(value), 0) INTO bal_qty, bal_value
    FROM inv.inv_valuation_balance WHERE valuation_area_id = p_area AND item_id = p_item;
  SELECT coalesce(sum(q.quantity), 0) INTO entries_qty
    FROM inv.inv_quantity_entry q JOIN md.plant p ON p.plant_id = q.plant_id
    WHERE q.company_id = p_company AND p.valuation_area_id = p_area AND q.item_id = p_item;
  SELECT coalesce(sum(s.quantity), 0) INTO stock_qty
    FROM inv.inv_stock_balance s JOIN md.plant p ON p.plant_id = s.plant_id
    WHERE s.company_id = p_company AND p.valuation_area_id = p_area AND s.item_id = p_item;
  SELECT coalesce(sum(amount), 0) INTO entries_value
    FROM inv.inv_value_entry WHERE company_id = p_company AND valuation_area_id = p_area AND item_id = p_item;
  SELECT coalesce(sum(g.debit - g.credit), 0) INTO gl_value
    FROM fin.gl_entry g JOIN md.plant p ON p.plant_id = g.plant_id
    WHERE g.company_id = p_company AND g.account_role = 'RAW_MATERIAL' AND p.valuation_area_id = p_area AND g.item_id = p_item;

  IF bal_qty <> entries_qty OR bal_qty <> stock_qty THEN
    RAISE EXCEPTION 'inventory quantity out of balance for area % item %: valuation %, entries %, stock %', p_area, p_item, bal_qty, entries_qty, stock_qty;
  END IF;
  IF bal_value <> entries_value OR entries_value <> gl_value THEN
    RAISE EXCEPTION 'inventory value out of balance for area % item % (P-3): valuation %, value entries %, GL %', p_area, p_item, bal_value, entries_value, gl_value;
  END IF;
END $$;

CREATE FUNCTION inv.value_entry_position() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  PERFORM inv.check_valuation_position(NEW.company_id, NEW.valuation_area_id, NEW.item_id);
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER inv_value_entry_position AFTER INSERT ON inv.inv_value_entry
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION inv.value_entry_position();

CREATE FUNCTION inv.quantity_entry_position() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  area uuid;
  ledger_qty numeric;
  balance_qty numeric;
BEGIN
  SELECT coalesce(sum(quantity), 0) INTO ledger_qty FROM inv.inv_quantity_entry
    WHERE location_id = NEW.location_id AND item_id = NEW.item_id AND lot_id = NEW.lot_id;
  SELECT coalesce(max(quantity), 0) INTO balance_qty FROM inv.inv_stock_balance
    WHERE location_id = NEW.location_id AND item_id = NEW.item_id AND lot_id = NEW.lot_id;
  IF ledger_qty <> balance_qty THEN
    RAISE EXCEPTION 'stock balance out of sync for location % item % lot %: entries %, balance % (AT-01)', NEW.location_id, NEW.item_id, NEW.lot_id, ledger_qty, balance_qty;
  END IF;
  SELECT valuation_area_id INTO area FROM md.plant WHERE plant_id = NEW.plant_id;
  PERFORM inv.check_valuation_position(NEW.company_id, area, NEW.item_id);
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER inv_quantity_entry_position AFTER INSERT ON inv.inv_quantity_entry
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION inv.quantity_entry_position();

-- Append-only ledgers and lots.
CREATE TRIGGER lot_append_only BEFORE UPDATE OR DELETE ON inv.lot FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER inv_quantity_entry_append_only BEFORE UPDATE OR DELETE ON inv.inv_quantity_entry FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER inv_quantity_entry_no_truncate BEFORE TRUNCATE ON inv.inv_quantity_entry FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER inv_value_entry_append_only BEFORE UPDATE OR DELETE ON inv.inv_value_entry FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER inv_value_entry_no_truncate BEFORE TRUNCATE ON inv.inv_value_entry FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['inv.lot', 'inv.inv_quantity_entry', 'inv.inv_value_entry', 'inv.inv_stock_balance', 'inv.inv_valuation_balance'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON inv.lot, inv.inv_quantity_entry, inv.inv_value_entry TO rochell_app;
GRANT SELECT, INSERT ON inv.inv_stock_balance, inv.inv_valuation_balance TO rochell_app;
GRANT UPDATE (quantity) ON inv.inv_stock_balance TO rochell_app;
GRANT UPDATE (quantity, value) ON inv.inv_valuation_balance TO rochell_app;
