-- VS#1 interim ledger review (E-VS1-3) · database guards for findings #26, #27 and #28.
-- Approved errata E-VS1-8 (component transitions), E-VS1-9 (no journal into a CLOSED component) and E-VS1-10 (balances equal
-- their ledgers at COMMIT, whoever writes them).

-- ---------------------------------------------------------------------------------------------
-- E-VS1-8 (#26): a component reaches CLOSED only with the close snapshot of the same hash written in the same transaction, and
-- CLOSED → REOPENED only with a reopen request approved in the same transaction; either way its state_history row must exist.
-- Deferred because ApproveReopen changes the component before it marks the request APPROVED.
-- ---------------------------------------------------------------------------------------------
CREATE FUNCTION fin.close_component_state_evidence() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  this_transaction xid := pg_current_xact_id()::xid;
BEGIN
  IF NEW.status = OLD.status THEN
    RETURN NULL;
  END IF;
  IF NEW.status = 'CLOSED' AND NOT EXISTS (
       SELECT 1 FROM fin.close_snapshot s
       WHERE s.period_id = NEW.period_id AND s.component = NEW.component AND s.content_hash = NEW.snapshot_hash AND s.xmin = this_transaction) THEN
    RAISE EXCEPTION 'fin.close_component_state %/%: CLOSED needs its close snapshot written in the same transaction (E-VS1-8)', NEW.period_id, NEW.component;
  END IF;
  IF NEW.status = 'REOPENED' AND NOT EXISTS (
       SELECT 1 FROM fin.reopen_request r
       WHERE r.period_id = NEW.period_id AND r.component = NEW.component AND r.status = 'APPROVED' AND r.second_approved_by IS NOT NULL
         AND r.xmin = this_transaction) THEN
    RAISE EXCEPTION 'fin.close_component_state %/%: REOPENED needs a reopen request approved in the same transaction (E-VS1-8, P-8)', NEW.period_id, NEW.component;
  END IF;
  IF NOT EXISTS (
       SELECT 1 FROM core.state_history h
       WHERE h.aggregate_type = 'PeriodComponent:' || NEW.component AND h.aggregate_id = NEW.period_id AND h.to_state = NEW.status
         AND h.xmin = this_transaction) THEN
    RAISE EXCEPTION 'fin.close_component_state %/%: % without its state_history row (ADR-027)', NEW.period_id, NEW.component, NEW.status;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER close_component_state_evidence AFTER UPDATE ON fin.close_component_state
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.close_component_state_evidence();

-- ---------------------------------------------------------------------------------------------
-- E-VS1-9 (#27): "a closed component accepts nothing" in the database too. Postings hold the period lock in shared mode and the
-- close takes it exclusively, so the status read here is the one the lock protocol guarantees.
-- ---------------------------------------------------------------------------------------------
CREATE FUNCTION fin.gl_journal_component_open() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF EXISTS (
       SELECT 1
       FROM fin.posting_rule_version v
       JOIN fin.close_component_state s ON s.period_id = NEW.period_id AND s.component = v.close_component
       WHERE v.posting_rule_id = NEW.posting_rule_id AND v.version = NEW.posting_rule_version AND s.status = 'CLOSED') THEN
    RAISE EXCEPTION 'fin.gl_journal %: its period and close component are CLOSED (E-VS1-9)', NEW.journal_id;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER gl_journal_component_open BEFORE INSERT ON fin.gl_journal
  FOR EACH ROW EXECUTE FUNCTION fin.gl_journal_component_open();

-- ---------------------------------------------------------------------------------------------
-- E-VS1-10 (#28): a balance row written by anyone (a command, a direct statement) must equal its ledgers at COMMIT — the
-- valuation row its control totals (0021), a stock row the sum of its lot's quantity entries.
-- ---------------------------------------------------------------------------------------------
CREATE FUNCTION inv.valuation_balance_position() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  PERFORM inv.check_valuation_position(NEW.company_id, NEW.valuation_area_id, NEW.item_id);
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER inv_valuation_balance_position AFTER INSERT OR UPDATE ON inv.inv_valuation_balance
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION inv.valuation_balance_position();

CREATE FUNCTION inv.stock_balance_position() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  ledger_qty numeric;
  balance_qty numeric;
BEGIN
  SELECT coalesce(sum(quantity), 0) INTO ledger_qty FROM inv.inv_quantity_entry
    WHERE location_id = NEW.location_id AND item_id = NEW.item_id AND lot_id = NEW.lot_id;
  SELECT coalesce(max(quantity), 0) INTO balance_qty FROM inv.inv_stock_balance
    WHERE location_id = NEW.location_id AND item_id = NEW.item_id AND lot_id = NEW.lot_id;
  IF ledger_qty <> balance_qty THEN
    RAISE EXCEPTION 'stock balance out of sync for location % item % lot %: entries %, balance % (E-VS1-10)', NEW.location_id, NEW.item_id, NEW.lot_id, ledger_qty, balance_qty;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER inv_stock_balance_position AFTER INSERT OR UPDATE ON inv.inv_stock_balance
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION inv.stock_balance_position();
