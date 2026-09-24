-- PR-19 · Slice conformance. Approved errata E-PR19-9 (P-7 source environment and gate condition 2, Patch 1.1
-- core.current_environment()), E-PR19-13 (tenant FKs of the stock balance), E-PR19-14 (UNIQUE (company_id, pk)).

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
