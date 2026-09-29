-- FIS1-04 · Fiscal authorization reconciliations and the expiry alert threshold (Frozen Baseline FIS-1 §7; approved errata
-- E-FIS1-14, E-FIS1-04-1…6).
INSERT INTO rec.recon_definition (recon_code, description, severity) VALUES
  ('AUTH-CONSUMPTION', 'Consumo de cada línea de autorización = consumos − devoluciones; consumo de cada línea e-CF 44 = neto − notas de crédito (E-FIS1-04-1)', 'ERROR'),
  ('EXEMPT-WITHOUT-AUTH', 'Facturas emitidas sin ITBIS que no son e-CF 44 y llevan artículos gravados por la regla aplicada (E-FIS1-04-2)', 'ERROR'),
  ('AUTH-EXPIRY', 'Autorizaciones que vencen dentro de authorization_expiry_alert_days o cuyo proyecto pasó su plazo (advertencia, E-FIS1-04-3)', 'WARNING');

INSERT INTO rec.recon_blocking (recon_code, component) VALUES ('AUTH-CONSUMPTION', 'AR-REC'), ('EXEMPT-WITHOUT-AUTH', 'AR-REC');

-- E-FIS1-04-4: the alert threshold is a REVENUE_ACCOUNTING parameter (value approved by the Controller, A-01).
INSERT INTO acc.policy_parameter_definition (param_code, policy_code, value_type, min_value, max_value, allowed_values, description) VALUES
  ('authorization_expiry_alert_days', 'REVENUE_ACCOUNTING', 'INTEGER', 1, 365, NULL, 'Días antes del vencimiento de una autorización fiscal en que se avisa (E-FIS1-04-4)');

-- E-FIS1-04-7: the daily process. A service identity of its own (not the deployment identity), with one system role that grants
-- only fiscal_authorization:suspend, and SERVICE sessions that only the API opens internally (never through Google or a cookie).
INSERT INTO iam.user (user_id, kind, status) VALUES ('00000000-0000-7000-8000-00000000d002', 'SERVICE', 'ACTIVE');
INSERT INTO iam.role (role_id, code, name) VALUES (gen_random_uuid(), 'PROCESO_DIARIO', 'Proceso diario');
INSERT INTO iam.role_permission (role_id, permission_code) SELECT role_id, 'fiscal_authorization:suspend' FROM iam.role WHERE code = 'PROCESO_DIARIO';

ALTER TABLE iam.session
  DROP CONSTRAINT session_auth_method,
  ADD CONSTRAINT session_auth_method CHECK (auth_method IN ('OIDC_GOOGLE', 'ACT_AS', 'SERVICE'));

-- A SERVICE session is of a service identity, and a service identity has no other kind of session.
CREATE FUNCTION iam.session_service_guard() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = iam, pg_temp AS $$
BEGIN
  IF (NEW.auth_method = 'SERVICE') IS DISTINCT FROM ((SELECT kind FROM iam.user WHERE user_id = NEW.user_id) = 'SERVICE') THEN
    RAISE EXCEPTION 'iam.session: SERVICE sessions are only of service identities, and service identities only have SERVICE sessions (E-FIS1-04-7)';
  END IF;
  RETURN NEW;
END $$;
REVOKE ALL ON FUNCTION iam.session_service_guard() FROM PUBLIC;
CREATE TRIGGER session_service_guard BEFORE INSERT ON iam.session
  FOR EACH ROW EXECUTE FUNCTION iam.session_service_guard();

-- PROCESO_DIARIO is held only by service identities, and service identities hold nothing else.
CREATE FUNCTION iam.role_assignment_service_guard() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = iam, pg_temp AS $$
BEGIN
  IF ((SELECT kind FROM iam.user WHERE user_id = NEW.user_id) = 'SERVICE') IS DISTINCT FROM ((SELECT code FROM iam.role WHERE role_id = NEW.role_id) = 'PROCESO_DIARIO') THEN
    RAISE EXCEPTION 'iam.role_assignment: PROCESO_DIARIO is only for service identities, which hold no other role (E-FIS1-04-7)';
  END IF;
  RETURN NEW;
END $$;
REVOKE ALL ON FUNCTION iam.role_assignment_service_guard() FROM PUBLIC;
CREATE TRIGGER role_assignment_service_guard BEFORE INSERT ON iam.role_assignment
  FOR EACH ROW EXECUTE FUNCTION iam.role_assignment_service_guard();

-- Existing companies; `rochell-migrate create-company` assigns it to new ones.
INSERT INTO iam.role_assignment (assignment_id, company_id, user_id, role_id, plant_id, valid_from, granted_by)
SELECT gen_random_uuid(), c.company_id, '00000000-0000-7000-8000-00000000d002', r.role_id, NULL, now(), '00000000-0000-7000-8000-00000000d001'
FROM md.company c CROSS JOIN iam.role r WHERE r.code = 'PROCESO_DIARIO';
