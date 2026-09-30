-- ADM-2 · The superadministrator (approved errata E-ADM-2-1…7; supersedes E-ADM-1): a role with every permission, held for at most
-- 90 days, whose holder may prepare and approve alone. Each control waived that way is marked in core.command_log and
-- core.state_history and listed by the CONTROLS-WAIVED reconciliation.

-- E-ADM-2-1: every permission except identity:act_as (TEST databases only), including the ones later migrations add.
INSERT INTO iam.role (role_id, code, name) VALUES (gen_random_uuid(), 'SUPERADMIN', 'Superadministrador');
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, p.permission_code FROM iam.role r CROSS JOIN iam.permission p
WHERE r.code = 'SUPERADMIN' AND p.permission_code <> 'identity:act_as';

CREATE FUNCTION iam.superadmin_permission() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.permission_code <> 'identity:act_as' THEN
    INSERT INTO iam.role_permission (role_id, permission_code) SELECT role_id, NEW.permission_code FROM iam.role WHERE code = 'SUPERADMIN';
  END IF;
  RETURN NULL;
END $$;
CREATE TRIGGER permission_superadmin AFTER INSERT ON iam.permission
  FOR EACH ROW EXECUTE FUNCTION iam.superadmin_permission();

-- E-ADM-2-2: a company-wide assignment that ends within 90 days (90 days when no end is given). Renewed with a new request.
CREATE FUNCTION iam.role_assignment_superadmin_term() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = iam, pg_temp AS $$
BEGIN
  IF (SELECT code FROM iam.role WHERE role_id = NEW.role_id) = 'SUPERADMIN' THEN
    IF NEW.plant_id IS NOT NULL THEN
      RAISE EXCEPTION 'iam.role_assignment: SUPERADMIN is assigned for the whole company, not for a plant (E-ADM-2-2)';
    END IF;
    NEW.valid_to := coalesce(NEW.valid_to, NEW.valid_from + interval '90 days');
    IF NEW.valid_to > NEW.valid_from + interval '90 days' THEN
      RAISE EXCEPTION 'iam.role_assignment: SUPERADMIN ends within 90 days of its start (E-ADM-2-2)' USING ERRCODE = 'check_violation';
    END IF;
  END IF;
  RETURN NEW;
END $$;
REVOKE ALL ON FUNCTION iam.role_assignment_superadmin_term() FROM PUBLIC;
CREATE TRIGGER role_assignment_superadmin_term BEFORE INSERT ON iam.role_assignment
  FOR EACH ROW EXECUTE FUNCTION iam.role_assignment_superadmin_term();

-- An assignment with an end date may still be revoked earlier: valid_to is set once, or brought forward while it is in the future.
CREATE OR REPLACE FUNCTION iam.role_assignment_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'UPDATE' THEN
    RAISE EXCEPTION 'iam.role_assignment rows cannot be deleted; revoke by setting valid_to';
  END IF;
  IF ROW(NEW.assignment_id, NEW.company_id, NEW.user_id, NEW.role_id, NEW.plant_id, NEW.valid_from, NEW.sod_exception_id, NEW.granted_by)
     IS DISTINCT FROM ROW(OLD.assignment_id, OLD.company_id, OLD.user_id, OLD.role_id, OLD.plant_id, OLD.valid_from, OLD.sod_exception_id, OLD.granted_by) THEN
    RAISE EXCEPTION 'iam.role_assignment: only valid_to may change';
  END IF;
  IF NEW.valid_to IS NULL OR (OLD.valid_to IS NOT NULL AND (OLD.valid_to <= now() OR NEW.valid_to >= OLD.valid_to)) THEN
    RAISE EXCEPTION 'iam.role_assignment: valid_to can be set only once, or brought forward while it is in the future';
  END IF;
  RETURN NEW;
END $$;

-- E-ADM-2-3: "are this person's controls waived?" — true while the user holds an unexpired SUPERADMIN assignment in the company.
-- Commands pass the application clock (as authorization does); triggers use the transaction time.
CREATE FUNCTION iam.controls_waived(p_company uuid, p_user uuid, p_at timestamptz DEFAULT now()) RETURNS boolean
  LANGUAGE sql STABLE SECURITY DEFINER SET search_path = iam, pg_temp AS $$
  SELECT EXISTS (
    SELECT 1 FROM iam.role_assignment ra JOIN iam.role r ON r.role_id = ra.role_id JOIN iam.user u ON u.user_id = ra.user_id
    WHERE ra.company_id = p_company AND ra.user_id = p_user AND r.code = 'SUPERADMIN' AND u.status = 'ACTIVE'
      AND (ra.valid_to IS NULL OR ra.valid_to > p_at))
$$;
REVOKE ALL ON FUNCTION iam.controls_waived(uuid, uuid, timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION iam.controls_waived(uuid, uuid, timestamptz) TO rochell_app;

-- The segregation-of-duties rule does not apply to a superadministrator (their permissions conflict by design).
CREATE OR REPLACE FUNCTION iam.enforce_sod() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  conflict text;
BEGIN
  -- Serialize SoD checks per user and company so concurrent grants cannot slip past each other.
  PERFORM pg_advisory_xact_lock(hashtextextended('sod:' || NEW.company_id::text || ':' || NEW.user_id::text, 0));

  IF iam.controls_waived(NEW.company_id, NEW.user_id) THEN
    RETURN NULL;
  END IF;

  WITH perms AS (
    SELECT DISTINCT rp.permission_code, p.access, r.code AS role_code
    FROM iam.role_assignment ra
    JOIN iam.role r ON r.role_id = ra.role_id
    JOIN iam.role_permission rp ON rp.role_id = ra.role_id
    JOIN iam.permission p ON p.permission_code = rp.permission_code
    WHERE ra.company_id = NEW.company_id
      AND ra.user_id = NEW.user_id
      AND (ra.valid_to IS NULL OR ra.valid_to > now())
  )
  SELECT c INTO conflict FROM (
    SELECT s.permission_a || ' / ' || s.permission_b AS c
    FROM iam.sod_rule s
    WHERE EXISTS (SELECT 1 FROM perms WHERE permission_code = s.permission_a)
      AND EXISTS (SELECT 1 FROM perms WHERE permission_code = s.permission_b)
    UNION ALL
    SELECT 'AUDITOR / ' || min(permission_code)
    FROM perms
    WHERE access IN ('WRITE', 'SECURITY')
    HAVING count(*) > 0 AND EXISTS (SELECT 1 FROM perms WHERE role_code = 'AUDITOR')
    UNION ALL
    SELECT 'role:assign|role:revoke / ' || min(permission_code)
    FROM perms
    WHERE access = 'WRITE'
    HAVING count(*) > 0 AND EXISTS (SELECT 1 FROM perms WHERE permission_code IN ('role:assign', 'role:revoke'))
  ) conflicts
  LIMIT 1;

  IF conflict IS NOT NULL THEN
    RAISE EXCEPTION 'SOD_CONFLICT: %', conflict;
  END IF;
  RETURN NULL;
END $$;

-- E-ADM-2-4: the "decider ≠ preparer" CHECKs become a trigger that asks iam.controls_waived. Arguments: the decider column, the
-- preparer column and the former constraint's name (kept in the error, SQLSTATE 23514 as before). Checked when the decider is written.
-- A waiver marks the transaction (app.controls_waived) so the command log and the state history record it (E-ADM-2-5).
CREATE FUNCTION core.four_eyes() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  r jsonb := to_jsonb(NEW);
  decider uuid := (r ->> TG_ARGV[0])::uuid;
BEGIN
  IF decider IS NULL OR decider IS DISTINCT FROM (r ->> TG_ARGV[1])::uuid
     OR (TG_OP = 'UPDATE' AND (to_jsonb(OLD) ->> TG_ARGV[0])::uuid IS NOT DISTINCT FROM decider) THEN
    RETURN NEW;
  END IF;
  IF NOT iam.controls_waived((r ->> 'company_id')::uuid, decider) THEN
    RAISE EXCEPTION 'new row for relation "%" violates check constraint "%"', TG_TABLE_NAME, TG_ARGV[2]
      USING ERRCODE = 'check_violation', SCHEMA = TG_TABLE_SCHEMA, TABLE = TG_TABLE_NAME, CONSTRAINT = TG_ARGV[2];
  END IF;
  PERFORM set_config('app.controls_waived', 'true', true);
  RETURN NEW;
END $$;

DO $$
DECLARE
  c record;
BEGIN
  FOR c IN SELECT * FROM (VALUES
    ('fin', 'account_role_map', 'account_role_map_four_eyes', 'approved_by', 'prepared_by'),
    ('acc', 'accounting_policy_version', 'accounting_policy_version_four_eyes', 'approved_by', 'prepared_by'),
    ('pur', 'purchase_order', 'purchase_order_approver_not_creator', 'approved_by', 'created_by'),
    ('pur', 'receipt_correction', 'receipt_correction_approver_not_creator', 'approved_by', 'created_by'),
    ('tax', 'fiscal_rule_version', 'fiscal_rule_version_activator', 'activated_by', 'configured_by'),
    ('pur', 'supplier_invoice', 'supplier_invoice_exception_approver', 'exception_approved_by', 'created_by'),
    ('fin', 'reopen_request', 'reopen_request_second_person', 'second_approved_by', 'requested_by'),
    ('fin', 'reopen_request', 'reopen_request_rejecter', 'rejected_by', 'requested_by'),
    ('md', 'party_bank_account', 'party_bank_account_four_eyes', 'verified_by', 'requested_by'),
    ('md', 'party_bank_account', 'party_bank_account_rejecter', 'rejected_by', 'requested_by'),
    ('fin', 'payment', 'payment_four_eyes', 'released_by', 'prepared_by'),
    ('fin', 'manual_journal', 'manual_journal_four_eyes', 'approved_by', 'prepared_by'),
    ('fin', 'manual_journal', 'manual_journal_rejecter', 'rejected_by', 'prepared_by'),
    ('fin', 'report_structure_version', 'report_structure_version_four_eyes', 'approved_by', 'prepared_by'),
    ('sal', 'customer_terms_version', 'customer_terms_four_eyes', 'approved_by', 'prepared_by'),
    ('md', 'standard_cost_version', 'standard_cost_four_eyes', 'approved_by', 'prepared_by'),
    ('sal', 'price_list_version', 'price_list_four_eyes', 'approved_by', 'prepared_by'),
    ('mig', 'migration_batch', 'migration_batch_four_eyes', 'posted_by', 'prepared_by'),
    ('mfg', 'recipe_version', 'recipe_four_eyes', 'approved_by', 'prepared_by'),
    ('mfg', 'shift_summary', 'shift_summary_four_eyes', 'posted_by', 'recorded_by'),
    ('tax', 'fiscal_authorization', 'fiscal_authorization_four_eyes', 'verified_by', 'registered_by'),
    ('sal', 'quote', 'quote_price_four_eyes', 'price_approved_by', 'created_by')
  ) AS v (sch, tbl, con, decider, preparer)
  LOOP
    EXECUTE format('ALTER TABLE %I.%I DROP CONSTRAINT %I', c.sch, c.tbl, c.con);
    EXECUTE format('CREATE TRIGGER %I BEFORE INSERT OR UPDATE ON %I.%I FOR EACH ROW EXECUTE FUNCTION core.four_eyes(%L, %L, %L)',
                   c.con, c.sch, c.tbl, c.decider, c.preparer, c.con);
  END LOOP;
END $$;

-- Role change requests: the affected user never decides on their own request (a CHECK, as before); the requester ≠ decider part
-- goes through the waiver like the others.
ALTER TABLE iam.role_assignment_request
  DROP CONSTRAINT role_assignment_request_distinct_approver,
  ADD CONSTRAINT role_assignment_request_distinct_approver CHECK (second_approved_by IS NULL OR second_approved_by <> user_id),
  DROP CONSTRAINT role_assignment_request_rejected,
  ADD CONSTRAINT role_assignment_request_rejected CHECK (
    (status = 'REJECTED' AND rejected_by IS NOT NULL AND rejected_at IS NOT NULL AND length(btrim(rejection_reason)) > 0
       AND rejected_by <> user_id)
    OR (status <> 'REJECTED' AND rejected_by IS NULL AND rejected_at IS NULL AND rejection_reason IS NULL));
CREATE TRIGGER role_assignment_request_distinct_approver BEFORE INSERT OR UPDATE ON iam.role_assignment_request
  FOR EACH ROW EXECUTE FUNCTION core.four_eyes('second_approved_by', 'requested_by', 'role_assignment_request_distinct_approver');
CREATE TRIGGER role_assignment_request_rejecter BEFORE INSERT OR UPDATE ON iam.role_assignment_request
  FOR EACH ROW EXECUTE FUNCTION core.four_eyes('rejected_by', 'requested_by', 'role_assignment_request_rejected');

-- E-ADM-2-5: the mark. The command log row takes it when its result is written (the last step of every command); a state
-- history row when it is inserted after the waiver.
ALTER TABLE core.command_log ADD COLUMN controls_waived boolean NOT NULL DEFAULT false;
ALTER TABLE core.state_history ADD COLUMN controls_waived boolean NOT NULL DEFAULT false;

CREATE FUNCTION core.mark_controls_waived() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  NEW.controls_waived := coalesce(current_setting('app.controls_waived', true), '') = 'true';
  RETURN NEW;
END $$;
CREATE TRIGGER command_log_controls_waived BEFORE UPDATE ON core.command_log
  FOR EACH ROW EXECUTE FUNCTION core.mark_controls_waived();
CREATE TRIGGER state_history_controls_waived BEFORE INSERT ON core.state_history
  FOR EACH ROW EXECUTE FUNCTION core.mark_controls_waived();

-- E-ADM-2-5: the month's waived commands, as a warning.
INSERT INTO rec.recon_definition (recon_code, description, severity) VALUES
  ('CONTROLS-WAIVED', 'Comandos del mes en que un superadministrador dispensó un control de cuatro ojos (advertencia, E-ADM-2-5)', 'WARNING');
