-- X1-04a · A fiscal rule version may start before its predecessor's set end (E-X1-04-1): activation brings that end forward, as long
-- as no tax was determined under the old version on the days that change hands (checked by ActivateFiscalRuleVersion). The activation
-- gate (last defined in 0073) lets an ACTIVE version's end move earlier, never later.
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
  -- E-X1-04-1: a successor closes an open ACTIVE version or brings a closed one's end forward, never later.
  IF NEW.effective_to IS DISTINCT FROM OLD.effective_to AND NOT (
       OLD.status = 'ACTIVE' AND NEW.effective_to IS NOT NULL AND (OLD.effective_to IS NULL OR NEW.effective_to < OLD.effective_to)) THEN
    RAISE EXCEPTION 'tax.fiscal_rule_version: only an ACTIVE version can be closed, or its end brought forward, by its successor';
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
    -- E-FIS2-01-9, E-CF1-01-6: a 606 classification and the consumer identification amount compute no tax; their source alone
    -- makes them READY.
    IF latest_passed IS NOT TRUE
       AND (SELECT f.rule_kind FROM tax.fiscal_rule f WHERE f.rule_id = NEW.rule_id) NOT IN ('REPORT_606_CLASSIFICATION', 'CONSUMER_ID_THRESHOLD') THEN
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
