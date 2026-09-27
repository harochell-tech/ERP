-- B03-3 · Test identities for TEST databases (approved errata E-B03-14 (a)).
-- One signed-in person may act as synthetic users, each a distinct user with its own roles, so segregation of duties and the
-- four-eyes CHECKs hold exactly as in production. The acting session records the signed-in session it came from. In any
-- database that is not TEST (core.current_environment(), Patch 1.1) synthetic users, acting sessions and the act-as role are
-- refused by the database itself, as TEST fiscal sources are (P-7).

-- ---------------------------------------------------------------------------------------------
-- Synthetic users: no employee, no Google subject; an e-mail as their label (e.g. comprador@staging.invalid).
-- ---------------------------------------------------------------------------------------------
ALTER TABLE iam.user
  DROP CONSTRAINT user_kind,
  ADD CONSTRAINT user_kind CHECK (kind IN ('HUMAN', 'SERVICE', 'SYNTHETIC')),
  DROP CONSTRAINT user_human_identity,
  ADD CONSTRAINT user_human_identity CHECK (kind <> 'HUMAN' OR (employee_id IS NOT NULL AND oidc_subject IS NOT NULL)),
  ADD CONSTRAINT user_synthetic_identity CHECK (kind <> 'SYNTHETIC' OR (employee_id IS NULL AND oidc_subject IS NULL AND email IS NOT NULL));

CREATE FUNCTION iam.user_synthetic_test_only() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.kind = 'SYNTHETIC' AND core.current_environment() IS DISTINCT FROM 'TEST' THEN
    RAISE EXCEPTION 'iam.user: synthetic users exist only in TEST databases (E-B03-14; this database is %)', coalesce(core.current_environment(), 'not initialized');
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER user_synthetic_test_only BEFORE INSERT OR UPDATE ON iam.user
  FOR EACH ROW EXECUTE FUNCTION iam.user_synthetic_test_only();

-- ---------------------------------------------------------------------------------------------
-- Permission and role (SECURITY: it changes who acts; a READ or WRITE pattern rule never pairs with it by accident).
-- ---------------------------------------------------------------------------------------------
INSERT INTO iam.permission (permission_code, access) VALUES ('identity:act_as', 'SECURITY');
INSERT INTO iam.role (role_id, code, name) VALUES (gen_random_uuid(), 'PROBADOR', 'Probador (solo bases TEST)');
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT role_id, 'identity:act_as' FROM iam.role WHERE code = 'PROBADOR';

CREATE FUNCTION iam.role_assignment_act_as_test_only() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF EXISTS (SELECT 1 FROM iam.role_permission WHERE role_id = NEW.role_id AND permission_code = 'identity:act_as')
     AND core.current_environment() IS DISTINCT FROM 'TEST' THEN
    RAISE EXCEPTION 'iam.role_assignment: identity:act_as is granted only in TEST databases (E-B03-14)';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER role_assignment_act_as_test_only BEFORE INSERT ON iam.role_assignment
  FOR EACH ROW EXECUTE FUNCTION iam.role_assignment_act_as_test_only();

-- ---------------------------------------------------------------------------------------------
-- Acting sessions: auth_method ACT_AS, the synthetic user as user_id, the signed-in session in authenticated_session_id.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE iam.session
  ADD COLUMN authenticated_session_id uuid,
  ADD CONSTRAINT session_authenticated_fk FOREIGN KEY (authenticated_session_id) REFERENCES iam.session (session_id),
  DROP CONSTRAINT session_auth_method,
  ADD CONSTRAINT session_auth_method CHECK (auth_method IN ('OIDC_GOOGLE', 'ACT_AS')),
  ADD CONSTRAINT session_act_as_link CHECK ((auth_method = 'ACT_AS') = (authenticated_session_id IS NOT NULL));

-- Runs with the definer's rights: the acting check reads role assignments of the tenant set by the caller (app.company_id),
-- the same rows row-level security would show the caller.
CREATE FUNCTION iam.session_act_as_guard() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = iam, core, pg_temp AS $$
DECLARE
  user_kind text;
  company uuid := nullif(current_setting('app.company_id', true), '')::uuid;
  parent iam.session;
  parent_kind text;
BEGIN
  SELECT kind INTO user_kind FROM iam.user WHERE user_id = NEW.user_id;
  IF NEW.auth_method <> 'ACT_AS' THEN
    IF user_kind = 'SYNTHETIC' THEN
      RAISE EXCEPTION 'iam.session: a synthetic user has only acting sessions (E-B03-14)';
    END IF;
    RETURN NEW;
  END IF;

  IF core.current_environment() IS DISTINCT FROM 'TEST' THEN
    RAISE EXCEPTION 'iam.session: acting as another user exists only in TEST databases (E-B03-14)';
  END IF;
  IF user_kind IS DISTINCT FROM 'SYNTHETIC' THEN
    RAISE EXCEPTION 'iam.session: only synthetic users can be acted as (E-B03-14)';
  END IF;
  SELECT * INTO parent FROM iam.session WHERE session_id = NEW.authenticated_session_id;
  SELECT kind INTO parent_kind FROM iam.user WHERE user_id = parent.user_id;
  IF parent.session_id IS NULL OR parent.logout_at IS NOT NULL OR parent.auth_method <> 'OIDC_GOOGLE' OR parent_kind <> 'HUMAN' THEN
    RAISE EXCEPTION 'iam.session: an acting session starts from an open Google session of a person (E-B03-14)';
  END IF;
  IF company IS NULL
     OR NOT EXISTS (
       SELECT 1 FROM iam.role_assignment ra JOIN iam.role_permission rp ON rp.role_id = ra.role_id
       WHERE ra.company_id = company AND ra.user_id = parent.user_id AND rp.permission_code = 'identity:act_as'
         AND ra.valid_from <= now() AND (ra.valid_to IS NULL OR ra.valid_to > now()))
     OR NOT EXISTS (
       SELECT 1 FROM iam.role_assignment ra
       WHERE ra.company_id = company AND ra.user_id = NEW.user_id AND ra.valid_from <= now() AND (ra.valid_to IS NULL OR ra.valid_to > now())) THEN
    RAISE EXCEPTION 'iam.session: acting needs identity:act_as and a synthetic user with roles in the same company (E-B03-14)';
  END IF;
  RETURN NEW;
END $$;
REVOKE ALL ON FUNCTION iam.session_act_as_guard() FROM PUBLIC;
CREATE TRIGGER session_act_as_guard BEFORE INSERT ON iam.session
  FOR EACH ROW EXECUTE FUNCTION iam.session_act_as_guard();

-- The link to the signed-in session is part of the session's identity.
CREATE OR REPLACE FUNCTION iam.session_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'UPDATE' THEN
    RAISE EXCEPTION 'iam.session rows cannot be deleted';
  END IF;
  IF ROW(NEW.session_id, NEW.user_id, NEW.auth_method, NEW.ip, NEW.user_agent, NEW.login_at, NEW.authenticated_session_id)
     IS DISTINCT FROM ROW(OLD.session_id, OLD.user_id, OLD.auth_method, OLD.ip, OLD.user_agent, OLD.login_at, OLD.authenticated_session_id) THEN
    RAISE EXCEPTION 'iam.session identity columns are immutable';
  END IF;
  IF OLD.logout_at IS NOT NULL THEN
    RAISE EXCEPTION 'iam.session % is closed', OLD.session_id;
  END IF;
  RETURN NEW;
END $$;
