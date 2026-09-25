-- PR-19 · Slice conformance. Approved errata E-PR19-9 (P-7 source environment and gate condition 2, Patch 1.1
-- core.current_environment()), E-PR19-10 (subledger = GL by transaction deltas, Patch 1 precision 1), E-PR19-13 (tenant FKs of
-- the stock balance), E-PR19-14 (UNIQUE (company_id, pk)).

-- ---------------------------------------------------------------------------------------------
-- E-PR19-9 / P-7: every fiscal source states whether it is a TEST or a PRODUCTION source. Sources are append-only, so the
-- column is added with a default (rows that already exist, only in test databases, become TEST) and the default is dropped:
-- from now on the registering command states it.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE tax.fiscal_rule_source ADD COLUMN environment text NOT NULL DEFAULT 'TEST';
ALTER TABLE tax.fiscal_rule_source ALTER COLUMN environment DROP DEFAULT;
ALTER TABLE tax.fiscal_rule_source ADD CONSTRAINT fiscal_rule_source_environment CHECK (environment IN ('TEST', 'PRODUCTION'));

-- The gate (0013) with the environment read through core.current_environment() and P-7 condition 2 at activation.
CREATE OR REPLACE FUNCTION tax.fiscal_rule_version_gate() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  latest_passed boolean;
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'tax.fiscal_rule_version rows cannot be deleted; retire the version';
  END IF;
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'BLOCKED_PENDING_SOURCE' OR NEW.activated_by IS NOT NULL OR NEW.row_version <> 1 THEN
      RAISE EXCEPTION 'tax.fiscal_rule_version: a new version starts BLOCKED_PENDING_SOURCE (E-PR12-4)';
    END IF;
    RETURN NEW;
  END IF;

  IF ROW(NEW.rule_version_id, NEW.company_id, NEW.rule_id, NEW.version, NEW.definition, NEW.effective_from, NEW.configured_by, NEW.configured_at)
     IS DISTINCT FROM ROW(OLD.rule_version_id, OLD.company_id, OLD.rule_id, OLD.version, OLD.definition, OLD.effective_from, OLD.configured_by, OLD.configured_at) THEN
    RAISE EXCEPTION 'tax.fiscal_rule_version: identity and definition are immutable; configure a new version';
  END IF;
  IF NEW.row_version <> OLD.row_version + 1 THEN
    RAISE EXCEPTION 'tax.fiscal_rule_version: row_version must increase by exactly 1';
  END IF;
  IF NEW.effective_to IS DISTINCT FROM OLD.effective_to AND NOT (OLD.status = 'ACTIVE' AND OLD.effective_to IS NULL) THEN
    RAISE EXCEPTION 'tax.fiscal_rule_version: only an open ACTIVE version can be closed by its successor';
  END IF;
  IF OLD.activated_by IS NOT NULL AND ROW(NEW.activated_by, NEW.activated_at) IS DISTINCT FROM ROW(OLD.activated_by, OLD.activated_at) THEN
    RAISE EXCEPTION 'tax.fiscal_rule_version: activation data is immutable';
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
       (OLD.status = 'BLOCKED_PENDING_SOURCE' AND NEW.status IN ('READY', 'RETIRED')) OR
       (OLD.status = 'READY' AND NEW.status IN ('BLOCKED_PENDING_SOURCE', 'ACTIVE', 'RETIRED')) OR
       (OLD.status = 'ACTIVE' AND NEW.status = 'RETIRED')) THEN
    RAISE EXCEPTION 'tax.fiscal_rule_version: transition % → % is not allowed', OLD.status, NEW.status;
  END IF;

  IF NEW.status IN ('READY', 'ACTIVE') AND NEW.status IS DISTINCT FROM OLD.status THEN
    IF NOT EXISTS (SELECT 1 FROM tax.fiscal_rule_version_source WHERE rule_version_id = NEW.rule_version_id) THEN
      RAISE EXCEPTION 'tax.fiscal_rule_version %: % requires at least one official source', NEW.rule_version_id, NEW.status;
    END IF;
    SELECT r.passed INTO latest_passed
    FROM tax.fiscal_rule_test_run r
    WHERE r.rule_version_id = NEW.rule_version_id
      AND r.executed_at >= NEW.configured_at
      AND r.environment = core.current_environment()
    ORDER BY r.executed_at DESC, r.test_run_id DESC
    LIMIT 1;
    IF latest_passed IS NOT TRUE THEN
      RAISE EXCEPTION 'tax.fiscal_rule_version %: % requires the latest test run in this environment to pass', NEW.rule_version_id, NEW.status;
    END IF;
  END IF;
  -- P-7 gate condition 2 (E-PR19-9): in production, at least one linked source must be a PRODUCTION source.
  IF NEW.status = 'ACTIVE' AND NEW.status IS DISTINCT FROM OLD.status AND core.current_environment() = 'PRODUCTION'
     AND NOT EXISTS (
       SELECT 1 FROM tax.fiscal_rule_version_source vs
       JOIN tax.fiscal_rule_source s ON s.source_id = vs.source_id
       WHERE vs.rule_version_id = NEW.rule_version_id AND s.environment = 'PRODUCTION') THEN
    RAISE EXCEPTION 'tax.fiscal_rule_version %: activation in PRODUCTION requires a PRODUCTION source (P-7)', NEW.rule_version_id;
  END IF;
  IF NEW.status = 'ACTIVE' AND NEW.status IS DISTINCT FROM OLD.status AND (NEW.activated_by IS NULL OR NEW.activated_at IS NULL) THEN
    RAISE EXCEPTION 'tax.fiscal_rule_version %: activation needs the activating specialist', NEW.rule_version_id;
  END IF;
  RETURN NEW;
END $$;

-- ---------------------------------------------------------------------------------------------
-- E-PR19-13: the stock balance belongs to the company of its item and its lot.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE inv.inv_stock_balance
  ADD CONSTRAINT inv_stock_balance_company_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  ADD CONSTRAINT inv_stock_balance_company_lot_fk FOREIGN KEY (company_id, lot_id) REFERENCES inv.lot (company_id, lot_id);

-- ---------------------------------------------------------------------------------------------
-- E-PR19-14: UNIQUE (company_id, <pk>) on the company-scoped tables that lacked it. Not on the three tables written by
-- INSERT … ON CONFLICT (pk) DO UPDATE (stock and valuation balances, match results): a second unique index is not the
-- upsert's arbiter, so concurrent first inserts fail with 23505 instead of updating (found by PF-01). No composite FK
-- references them, which is the constraint's only purpose.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE tax.tax_determination_line ADD CONSTRAINT tax_determination_line_company_uq UNIQUE (company_id, determination_id, line_no);
ALTER TABLE tax.fiscal_rule_test_run ADD CONSTRAINT fiscal_rule_test_run_company_uq UNIQUE (company_id, test_run_id);
ALTER TABLE fin.reopen_request ADD CONSTRAINT reopen_request_company_uq UNIQUE (company_id, request_id);
ALTER TABLE fin.close_snapshot ADD CONSTRAINT close_snapshot_company_uq UNIQUE (company_id, snapshot_id);
ALTER TABLE md.uom_conversion ADD CONSTRAINT uom_conversion_company_uq UNIQUE (company_id, item_id, from_uom, to_uom, effective_from);

-- ---------------------------------------------------------------------------------------------
-- E-PR19-10 / Patch 1 precision 1: the valuation position check no longer re-sums the whole history of a position at every
-- COMMIT (its cost grew with the history; PF-01 missed p95 < 500 ms on the reference runner). Each ledger insert adds its own
-- delta to three control totals on the position's valuation row — quantity entries, value entries, RAW_MATERIAL GL lines —
-- through SECURITY DEFINER triggers the application role cannot bypass or write. The deferred check compares that one row:
-- valuation quantity = Σ quantity entries, valuation value = Σ value entries = Σ GL. Since every total starts from the full
-- history (backfilled below) and changes only by the deltas of committed inserts, this is the equality of each transaction's
-- deltas, inductively the total equality. Stock by lot keeps its own per-lot check; the reconciliations re-sum everything.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE inv.inv_valuation_balance
  ADD COLUMN ledger_quantity numeric(18,6) NOT NULL DEFAULT 0,
  ADD COLUMN ledger_value    numeric(19,4) NOT NULL DEFAULT 0,
  ADD COLUMN gl_value        numeric(19,4) NOT NULL DEFAULT 0;

UPDATE inv.inv_valuation_balance b SET
  ledger_quantity = (SELECT coalesce(sum(q.quantity), 0) FROM inv.inv_quantity_entry q JOIN md.plant p ON p.plant_id = q.plant_id
                     WHERE q.company_id = b.company_id AND p.valuation_area_id = b.valuation_area_id AND q.item_id = b.item_id),
  ledger_value    = (SELECT coalesce(sum(v.amount), 0) FROM inv.inv_value_entry v
                     WHERE v.company_id = b.company_id AND v.valuation_area_id = b.valuation_area_id AND v.item_id = b.item_id),
  gl_value        = (SELECT coalesce(sum(g.debit - g.credit), 0) FROM fin.gl_entry g JOIN md.plant p ON p.plant_id = g.plant_id
                     WHERE g.company_id = b.company_id AND g.account_role = 'RAW_MATERIAL' AND p.valuation_area_id = b.valuation_area_id
                       AND g.item_id = b.item_id);

-- The application writes only the balance itself; the control totals are written by the triggers below.
REVOKE INSERT ON inv.inv_valuation_balance FROM rochell_app;
GRANT INSERT (company_id, valuation_area_id, item_id, quantity, value) ON inv.inv_valuation_balance TO rochell_app;

CREATE FUNCTION inv.accumulate_position(p_company uuid, p_area uuid, p_item uuid, p_quantity numeric, p_value numeric, p_gl numeric)
  RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path = inv, pg_temp AS $$
BEGIN
  INSERT INTO inv.inv_valuation_balance (company_id, valuation_area_id, item_id, quantity, value, ledger_quantity, ledger_value, gl_value)
  VALUES (p_company, p_area, p_item, 0, 0, p_quantity, p_value, p_gl)
  ON CONFLICT (valuation_area_id, item_id) DO UPDATE SET
    ledger_quantity = inv.inv_valuation_balance.ledger_quantity + EXCLUDED.ledger_quantity,
    ledger_value    = inv.inv_valuation_balance.ledger_value + EXCLUDED.ledger_value,
    gl_value        = inv.inv_valuation_balance.gl_value + EXCLUDED.gl_value;
END $$;
REVOKE ALL ON FUNCTION inv.accumulate_position(uuid, uuid, uuid, numeric, numeric, numeric) FROM PUBLIC;

CREATE FUNCTION inv.accumulate_quantity_entry() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = inv, pg_temp AS $$
BEGIN
  PERFORM inv.accumulate_position(NEW.company_id, (SELECT valuation_area_id FROM md.plant WHERE plant_id = NEW.plant_id), NEW.item_id, NEW.quantity, 0, 0);
  RETURN NULL;
END $$;
CREATE TRIGGER inv_quantity_entry_accumulate AFTER INSERT ON inv.inv_quantity_entry
  FOR EACH ROW EXECUTE FUNCTION inv.accumulate_quantity_entry();

CREATE FUNCTION inv.accumulate_value_entry() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = inv, pg_temp AS $$
BEGIN
  PERFORM inv.accumulate_position(NEW.company_id, NEW.valuation_area_id, NEW.item_id, 0, NEW.amount, 0);
  RETURN NULL;
END $$;
CREATE TRIGGER inv_value_entry_accumulate AFTER INSERT ON inv.inv_value_entry
  FOR EACH ROW EXECUTE FUNCTION inv.accumulate_value_entry();

CREATE FUNCTION inv.accumulate_raw_material_line() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = inv, pg_temp AS $$
BEGIN
  PERFORM inv.accumulate_position(NEW.company_id, (SELECT valuation_area_id FROM md.plant WHERE plant_id = NEW.plant_id), NEW.item_id, 0, 0, NEW.debit - NEW.credit);
  RETURN NULL;
END $$;
CREATE TRIGGER gl_entry_raw_material_accumulate AFTER INSERT ON fin.gl_entry
  FOR EACH ROW WHEN (NEW.account_role = 'RAW_MATERIAL') EXECUTE FUNCTION inv.accumulate_raw_material_line();

-- Same name and callers (the deferred triggers of 0008); one row instead of four sums over the history.
CREATE OR REPLACE FUNCTION inv.check_valuation_position(p_company uuid, p_area uuid, p_item uuid) RETURNS void
  LANGUAGE plpgsql AS $$
DECLARE
  position record;
BEGIN
  SELECT quantity, value, ledger_quantity, ledger_value, gl_value INTO position
    FROM inv.inv_valuation_balance WHERE valuation_area_id = p_area AND item_id = p_item;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'inventory quantity out of balance for area % item %: no valuation position', p_area, p_item;
  END IF;
  IF position.quantity <> position.ledger_quantity THEN
    RAISE EXCEPTION 'inventory quantity out of balance for area % item %: valuation %, entries %', p_area, p_item, position.quantity, position.ledger_quantity;
  END IF;
  IF position.value <> position.ledger_value OR position.ledger_value <> position.gl_value THEN
    RAISE EXCEPTION 'inventory value out of balance for area % item % (P-3): valuation %, value entries %, GL %', p_area, p_item, position.value, position.ledger_value, position.gl_value;
  END IF;
END $$;
