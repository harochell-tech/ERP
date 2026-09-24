-- PR-03 · Identity. Frozen Baseline v2.1.1 §8.2 + Patch 1 (P-5, P-8) + approved errata E-PR02-1, E-PR03-1…8.

CREATE SCHEMA iam;
REVOKE ALL ON SCHEMA iam FROM PUBLIC;
GRANT USAGE ON SCHEMA iam TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- Users (global). Humans always have an employee and an OIDC subject (SC-04). Provisioned by the
-- deployment role with `rochell-migrate create-user` in VS#1 (E-PR03-5).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE iam.user (
  user_id       uuid NOT NULL,
  kind          text NOT NULL,
  employee_id   uuid,
  email         text,
  oidc_subject  text,
  status        text NOT NULL,
  CONSTRAINT user_pk PRIMARY KEY (user_id),
  CONSTRAINT user_employee_uq UNIQUE (employee_id),
  CONSTRAINT user_email_uq UNIQUE (email),
  CONSTRAINT user_oidc_subject_uq UNIQUE (oidc_subject),
  CONSTRAINT user_kind CHECK (kind IN ('HUMAN', 'SERVICE')),
  CONSTRAINT user_status CHECK (status IN ('ACTIVE', 'DISABLED')),
  CONSTRAINT user_human_identity CHECK (kind = 'SERVICE' OR (employee_id IS NOT NULL AND oidc_subject IS NOT NULL)),
  CONSTRAINT user_email_lowercase CHECK (email IS NULL OR email = lower(email))
);

-- Service identity used as granted_by for bootstrap grants made by the deployment role (E-PR03-5).
INSERT INTO iam.user (user_id, kind, status) VALUES ('00000000-0000-7000-8000-00000000d001', 'SERVICE', 'ACTIVE');

-- ---------------------------------------------------------------------------------------------
-- Permissions (global). access: READ / WRITE / SECURITY — used by the pattern SoD rules (E-PR03-4 b, c).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE iam.permission (
  permission_code  text NOT NULL,
  access           text NOT NULL,
  CONSTRAINT permission_pk PRIMARY KEY (permission_code),
  CONSTRAINT permission_code_format CHECK (permission_code ~ '^[a-z_]+:[a-z_]+$'),
  CONSTRAINT permission_access CHECK (access IN ('READ', 'WRITE', 'SECURITY'))
);

INSERT INTO iam.permission (permission_code, access) VALUES
  ('supplier:create', 'WRITE'), ('supplier:update', 'WRITE'), ('supplier:activate', 'WRITE'),
  ('item:create', 'WRITE'), ('item:activate', 'WRITE'),
  ('purchase_order:create', 'WRITE'), ('purchase_order:submit', 'WRITE'), ('purchase_order:cancel', 'WRITE'),
  ('purchase_order:approve', 'WRITE'), ('purchase_order:approve_over_receipt', 'WRITE'),
  ('goods_receipt:post', 'WRITE'), ('goods_receipt:reverse', 'WRITE'),
  ('receipt_correction:create', 'WRITE'), ('receipt_correction:approve', 'WRITE'),
  ('supplier_invoice:register', 'WRITE'), ('supplier_invoice:match', 'WRITE'), ('supplier_invoice:void', 'WRITE'),
  ('match_exception:approve', 'WRITE'), ('supplier_invoice:post', 'WRITE'), ('supplier_invoice:reverse', 'WRITE'),
  ('journal:repost', 'WRITE'), ('valuation_residual:approve', 'WRITE'),
  ('account_role_map:approve', 'WRITE'), ('posting_rule:approve', 'WRITE'),
  ('accounting_policy:prepare', 'WRITE'), ('accounting_policy:approve', 'WRITE'),
  ('fiscal_rule:configure', 'WRITE'), ('fiscal_rule_source:register', 'WRITE'), ('fiscal_rule:activate', 'WRITE'),
  ('period_component:close', 'WRITE'), ('period_component:reopen', 'WRITE'), ('period_component:second_approve', 'WRITE'),
  ('reconciliation:run', 'WRITE'), ('reconciliation:read', 'READ'),
  ('audit:read', 'READ'), ('hash:verify', 'READ'),
  ('role:assign', 'SECURITY'), ('role:revoke', 'SECURITY'), ('role:second_approve', 'SECURITY');

-- ---------------------------------------------------------------------------------------------
-- Roles of VS#1 (Errata §14 + Patch 1 P-8) and their permissions.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE iam.role (
  role_id  uuid NOT NULL,
  code     text NOT NULL,
  name     text NOT NULL,
  CONSTRAINT role_pk PRIMARY KEY (role_id),
  CONSTRAINT role_code_uq UNIQUE (code),
  CONSTRAINT role_code_format CHECK (code ~ '^[A-Z][A-Z0-9_]*$')
);

CREATE TABLE iam.role_permission (
  role_id          uuid NOT NULL,
  permission_code  text NOT NULL,
  CONSTRAINT role_permission_pk PRIMARY KEY (role_id, permission_code),
  CONSTRAINT role_permission_role_fk FOREIGN KEY (role_id) REFERENCES iam.role (role_id),
  CONSTRAINT role_permission_permission_fk FOREIGN KEY (permission_code) REFERENCES iam.permission (permission_code)
);

INSERT INTO iam.role (role_id, code, name) VALUES
  (gen_random_uuid(), 'COMPRADOR', 'Comprador'),
  (gen_random_uuid(), 'APROBADOR_COMPRAS', 'Aprobador de compras'),
  (gen_random_uuid(), 'ALMACENISTA', 'Almacenista'),
  (gen_random_uuid(), 'CUENTAS_POR_PAGAR', 'Cuentas por pagar'),
  (gen_random_uuid(), 'CONTROLLER', 'Controller'),
  (gen_random_uuid(), 'ESPECIALISTA_FISCAL', 'Especialista fiscal'),
  (gen_random_uuid(), 'ANALISTA_FISCAL', 'Analista fiscal'),
  (gen_random_uuid(), 'ADMIN_SEGURIDAD', 'Administrador de seguridad'),
  (gen_random_uuid(), 'AUDITOR', 'Auditor'),
  (gen_random_uuid(), 'SEGUNDO_APROBADOR_CIERRE', 'Segundo aprobador de cierre'),
  (gen_random_uuid(), 'SEGUNDO_APROBADOR_SEGURIDAD', 'Segundo aprobador de seguridad');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES
  ('COMPRADOR', 'supplier:create'), ('COMPRADOR', 'supplier:update'), ('COMPRADOR', 'purchase_order:create'),
  ('COMPRADOR', 'purchase_order:submit'), ('COMPRADOR', 'purchase_order:cancel'),
  ('APROBADOR_COMPRAS', 'purchase_order:approve'), ('APROBADOR_COMPRAS', 'purchase_order:approve_over_receipt'),
  ('ALMACENISTA', 'item:create'), ('ALMACENISTA', 'goods_receipt:post'), ('ALMACENISTA', 'receipt_correction:create'),
  ('CUENTAS_POR_PAGAR', 'supplier_invoice:register'), ('CUENTAS_POR_PAGAR', 'supplier_invoice:match'),
  ('CUENTAS_POR_PAGAR', 'supplier_invoice:void'), ('CUENTAS_POR_PAGAR', 'supplier_invoice:post'),
  ('CONTROLLER', 'supplier:activate'), ('CONTROLLER', 'item:activate'), ('CONTROLLER', 'purchase_order:approve'),
  ('CONTROLLER', 'goods_receipt:reverse'), ('CONTROLLER', 'receipt_correction:approve'), ('CONTROLLER', 'match_exception:approve'),
  ('CONTROLLER', 'supplier_invoice:reverse'), ('CONTROLLER', 'journal:repost'), ('CONTROLLER', 'valuation_residual:approve'),
  ('CONTROLLER', 'account_role_map:approve'), ('CONTROLLER', 'posting_rule:approve'),
  ('CONTROLLER', 'accounting_policy:prepare'), ('CONTROLLER', 'accounting_policy:approve'),
  ('CONTROLLER', 'period_component:close'), ('CONTROLLER', 'period_component:reopen'),
  ('CONTROLLER', 'reconciliation:run'), ('CONTROLLER', 'reconciliation:read'), ('CONTROLLER', 'audit:read'), ('CONTROLLER', 'hash:verify'),
  ('ESPECIALISTA_FISCAL', 'fiscal_rule:activate'),
  ('ANALISTA_FISCAL', 'fiscal_rule:configure'), ('ANALISTA_FISCAL', 'fiscal_rule_source:register'),
  ('ADMIN_SEGURIDAD', 'role:assign'), ('ADMIN_SEGURIDAD', 'role:revoke'),
  ('AUDITOR', 'audit:read'), ('AUDITOR', 'hash:verify'), ('AUDITOR', 'reconciliation:read'),
  ('SEGUNDO_APROBADOR_CIERRE', 'period_component:second_approve'),
  ('SEGUNDO_APROBADOR_SEGURIDAD', 'role:second_approve')
) AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;

-- ---------------------------------------------------------------------------------------------
-- Segregation of duties. Explicit pairs from §14 / P-8 (document-level rules are enforced by CHECKs in
-- document tables, E-PR03-4 a). Pattern rules (E-PR03-4 b, c) are enforced in iam.enforce_sod().
-- ---------------------------------------------------------------------------------------------
CREATE TABLE iam.sod_rule (
  permission_a  text NOT NULL,
  permission_b  text NOT NULL,
  CONSTRAINT sod_rule_pk PRIMARY KEY (permission_a, permission_b),
  CONSTRAINT sod_rule_a_fk FOREIGN KEY (permission_a) REFERENCES iam.permission (permission_code),
  CONSTRAINT sod_rule_b_fk FOREIGN KEY (permission_b) REFERENCES iam.permission (permission_code),
  CONSTRAINT sod_rule_ordered CHECK (permission_a < permission_b)
);

INSERT INTO iam.sod_rule (permission_a, permission_b)
SELECT least(a, b), greatest(a, b) FROM (VALUES
  ('supplier:create', 'supplier_invoice:post'),
  ('supplier:create', 'match_exception:approve'),
  ('supplier:update', 'supplier_invoice:post'),
  ('supplier:update', 'match_exception:approve'),
  ('supplier:activate', 'supplier:create'),
  ('purchase_order:approve_over_receipt', 'goods_receipt:post'),
  ('goods_receipt:post', 'supplier_invoice:register'),
  ('goods_receipt:post', 'supplier_invoice:match'),
  ('goods_receipt:post', 'supplier_invoice:void'),
  ('goods_receipt:post', 'supplier_invoice:post'),
  ('goods_receipt:reverse', 'goods_receipt:post'),
  ('receipt_correction:create', 'receipt_correction:approve'),
  ('supplier_invoice:reverse', 'supplier_invoice:post'),
  ('period_component:reopen', 'period_component:second_approve'),
  ('role:assign', 'role:second_approve'),
  ('role:revoke', 'role:second_approve')
) AS v (a, b);

-- ---------------------------------------------------------------------------------------------
-- Sessions (office, OIDC). last_activity_at supports the idle timeout of E-PR03-6.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE iam.session (
  session_id        uuid        NOT NULL,
  user_id           uuid        NOT NULL,
  auth_method       text        NOT NULL,
  ip                inet,
  user_agent        text,
  login_at          timestamptz NOT NULL,
  last_step_up_at   timestamptz,
  last_activity_at  timestamptz NOT NULL,
  logout_at         timestamptz,
  CONSTRAINT session_pk PRIMARY KEY (session_id),
  CONSTRAINT session_user_fk FOREIGN KEY (user_id) REFERENCES iam.user (user_id),
  CONSTRAINT session_auth_method CHECK (auth_method IN ('OIDC_GOOGLE')),
  CONSTRAINT session_times CHECK (last_activity_at >= login_at AND (logout_at IS NULL OR logout_at >= login_at))
);

CREATE FUNCTION iam.session_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'UPDATE' THEN
    RAISE EXCEPTION 'iam.session rows cannot be deleted';
  END IF;
  IF ROW(NEW.session_id, NEW.user_id, NEW.auth_method, NEW.ip, NEW.user_agent, NEW.login_at)
     IS DISTINCT FROM ROW(OLD.session_id, OLD.user_id, OLD.auth_method, OLD.ip, OLD.user_agent, OLD.login_at) THEN
    RAISE EXCEPTION 'iam.session identity columns are immutable';
  END IF;
  IF OLD.logout_at IS NOT NULL THEN
    RAISE EXCEPTION 'iam.session % is closed', OLD.session_id;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER session_guard BEFORE UPDATE OR DELETE ON iam.session
  FOR EACH ROW EXECUTE FUNCTION iam.session_guard();
CREATE TRIGGER session_no_truncate BEFORE TRUNCATE ON iam.session
  FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- E-PR02-1: the command log references a real session.
ALTER TABLE core.command_log
  ADD CONSTRAINT command_log_session_fk FOREIGN KEY (session_id) REFERENCES iam.session (session_id);

-- ---------------------------------------------------------------------------------------------
-- Role assignments (company-scoped). plant_id FK to md.plant is added in PR-04 (E-PR03-1).
-- SoD exceptions are out of VS#1 (E-PR03-7). Revocation = setting valid_to once.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE iam.role_assignment (
  assignment_id     uuid        NOT NULL,
  company_id        uuid        NOT NULL,
  user_id           uuid        NOT NULL,
  role_id           uuid        NOT NULL,
  plant_id          uuid,
  valid_from        timestamptz NOT NULL,
  valid_to          timestamptz,
  sod_exception_id  uuid,
  granted_by        uuid        NOT NULL,
  CONSTRAINT role_assignment_pk PRIMARY KEY (assignment_id),
  CONSTRAINT role_assignment_company_uq UNIQUE (company_id, assignment_id),
  CONSTRAINT role_assignment_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT role_assignment_user_fk FOREIGN KEY (user_id) REFERENCES iam.user (user_id),
  CONSTRAINT role_assignment_role_fk FOREIGN KEY (role_id) REFERENCES iam.role (role_id),
  CONSTRAINT role_assignment_granted_by_fk FOREIGN KEY (granted_by) REFERENCES iam.user (user_id),
  CONSTRAINT role_assignment_not_self_granted CHECK (granted_by <> user_id),
  CONSTRAINT role_assignment_no_sod_exception CHECK (sod_exception_id IS NULL),
  CONSTRAINT role_assignment_validity CHECK (valid_to IS NULL OR valid_to > valid_from)
);
CREATE UNIQUE INDEX role_assignment_active_uq ON iam.role_assignment
  (company_id, user_id, role_id, COALESCE(plant_id, '00000000-0000-0000-0000-000000000000'::uuid))
  WHERE valid_to IS NULL;

CREATE FUNCTION iam.role_assignment_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'UPDATE' THEN
    RAISE EXCEPTION 'iam.role_assignment rows cannot be deleted; revoke by setting valid_to';
  END IF;
  IF ROW(NEW.assignment_id, NEW.company_id, NEW.user_id, NEW.role_id, NEW.plant_id, NEW.valid_from, NEW.sod_exception_id, NEW.granted_by)
     IS DISTINCT FROM ROW(OLD.assignment_id, OLD.company_id, OLD.user_id, OLD.role_id, OLD.plant_id, OLD.valid_from, OLD.sod_exception_id, OLD.granted_by) THEN
    RAISE EXCEPTION 'iam.role_assignment: only valid_to may change';
  END IF;
  IF OLD.valid_to IS NOT NULL OR NEW.valid_to IS NULL THEN
    RAISE EXCEPTION 'iam.role_assignment: valid_to can be set only once';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER role_assignment_guard BEFORE UPDATE OR DELETE ON iam.role_assignment
  FOR EACH ROW EXECUTE FUNCTION iam.role_assignment_guard();
CREATE TRIGGER role_assignment_no_truncate BEFORE TRUNCATE ON iam.role_assignment
  FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

CREATE FUNCTION iam.enforce_sod() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  conflict text;
BEGIN
  -- Serialize SoD checks per user and company so concurrent grants cannot slip past each other.
  PERFORM pg_advisory_xact_lock(hashtextextended('sod:' || NEW.company_id::text || ':' || NEW.user_id::text, 0));

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
CREATE TRIGGER role_assignment_sod AFTER INSERT ON iam.role_assignment
  FOR EACH ROW EXECUTE FUNCTION iam.enforce_sod();

-- ---------------------------------------------------------------------------------------------
-- Role change requests with second approval (Patch 1 P-8, T-15). plant FK in PR-04 (E-PR03-1).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE iam.role_assignment_request (
  company_id          uuid NOT NULL,
  request_id          uuid NOT NULL,
  user_id             uuid NOT NULL,
  role_id             uuid NOT NULL,
  plant_id            uuid,
  action              text NOT NULL,
  requested_by        uuid NOT NULL,
  second_approved_by  uuid,
  status              text NOT NULL,
  CONSTRAINT role_assignment_request_pk PRIMARY KEY (request_id),
  CONSTRAINT role_assignment_request_company_uq UNIQUE (company_id, request_id),
  CONSTRAINT role_assignment_request_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT role_assignment_request_user_fk FOREIGN KEY (user_id) REFERENCES iam.user (user_id),
  CONSTRAINT role_assignment_request_role_fk FOREIGN KEY (role_id) REFERENCES iam.role (role_id),
  CONSTRAINT role_assignment_request_requested_by_fk FOREIGN KEY (requested_by) REFERENCES iam.user (user_id),
  CONSTRAINT role_assignment_request_approver_fk FOREIGN KEY (second_approved_by) REFERENCES iam.user (user_id),
  CONSTRAINT role_assignment_request_action CHECK (action IN ('ASSIGN', 'REVOKE')),
  CONSTRAINT role_assignment_request_status CHECK (status IN ('REQUESTED', 'APPROVED')),
  CONSTRAINT role_assignment_request_not_for_self CHECK (requested_by <> user_id),
  CONSTRAINT role_assignment_request_distinct_approver CHECK
    (second_approved_by IS NULL OR (second_approved_by <> requested_by AND second_approved_by <> user_id)),
  CONSTRAINT role_assignment_request_approved CHECK ((status = 'APPROVED') = (second_approved_by IS NOT NULL))
);

CREATE FUNCTION iam.role_assignment_request_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'UPDATE' THEN
    RAISE EXCEPTION 'iam.role_assignment_request rows cannot be deleted';
  END IF;
  IF ROW(NEW.company_id, NEW.request_id, NEW.user_id, NEW.role_id, NEW.plant_id, NEW.action, NEW.requested_by)
     IS DISTINCT FROM ROW(OLD.company_id, OLD.request_id, OLD.user_id, OLD.role_id, OLD.plant_id, OLD.action, OLD.requested_by) THEN
    RAISE EXCEPTION 'iam.role_assignment_request: only status and second_approved_by may change';
  END IF;
  IF OLD.status <> 'REQUESTED' THEN
    RAISE EXCEPTION 'iam.role_assignment_request % is already %', OLD.request_id, OLD.status;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER role_assignment_request_guard BEFORE UPDATE OR DELETE ON iam.role_assignment_request
  FOR EACH ROW EXECUTE FUNCTION iam.role_assignment_request_guard();
CREATE TRIGGER role_assignment_request_no_truncate BEFORE TRUNCATE ON iam.role_assignment_request
  FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- Row-level security by company (ADR-013). The owner (deployment role) is not subject to RLS.
-- The tenant comes from the transaction-local setting app.company_id written by the command pipeline.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['core.command_log', 'core.domain_event', 'core.outbox', 'core.inbox', 'core.state_history',
                           'core.document_link', 'iam.role_assignment', 'iam.role_assignment_request'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

-- ---------------------------------------------------------------------------------------------
-- Application role privileges.
-- ---------------------------------------------------------------------------------------------
GRANT SELECT ON iam.user, iam.permission, iam.role, iam.role_permission, iam.sod_rule TO rochell_app;
GRANT SELECT, INSERT ON iam.session TO rochell_app;
GRANT UPDATE (last_step_up_at, last_activity_at, logout_at) ON iam.session TO rochell_app;
GRANT SELECT, INSERT ON iam.role_assignment TO rochell_app;
GRANT UPDATE (valid_to) ON iam.role_assignment TO rochell_app;
GRANT SELECT, INSERT ON iam.role_assignment_request TO rochell_app;
GRANT UPDATE (status, second_approved_by) ON iam.role_assignment_request TO rochell_app;
