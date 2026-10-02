-- CFG-01 · The configuration load (approved errata E-CFG-1…6): a service identity that only prepares fiscal configuration — it
-- registers official sources, configures rule versions, links sources and runs their tests — through the same commands as the
-- screens, run by the deployment CLI (`rochell-migrate load-fiscal-rules`). It never activates: activation stays with a person.
INSERT INTO iam.user (user_id, kind, status, display_name) VALUES ('00000000-0000-7000-8000-00000000d003', 'SERVICE', 'ACTIVE', 'Carga de configuración');
INSERT INTO iam.role (role_id, code, name, description)
VALUES (gen_random_uuid(), 'CARGA_CONFIGURACION', 'Carga de configuración',
        'Identidad de servicio: registra fuentes fiscales y prepara versiones de reglas con sus pruebas desde la herramienta de despliegue. No activa.');
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES ('fiscal_rule_source:register'), ('fiscal_rule:configure')) AS v (permission_code)
CROSS JOIN iam.role r WHERE r.code = 'CARGA_CONFIGURACION';

-- Service roles are held only by service identities, each by its own, and service identities hold nothing else.
CREATE OR REPLACE FUNCTION iam.role_assignment_service_guard() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = iam, pg_temp AS $$
DECLARE
  role_code text := (SELECT code FROM iam.role WHERE role_id = NEW.role_id);
  expected text := CASE NEW.user_id
    WHEN '00000000-0000-7000-8000-00000000d002' THEN 'PROCESO_DIARIO'
    WHEN '00000000-0000-7000-8000-00000000d003' THEN 'CARGA_CONFIGURACION' END;
BEGIN
  IF (SELECT kind FROM iam.user WHERE user_id = NEW.user_id) = 'SERVICE' THEN
    IF role_code IS DISTINCT FROM expected THEN
      RAISE EXCEPTION 'iam.role_assignment: a service identity holds only its own service role (E-FIS1-04-7, E-CFG-1)';
    END IF;
  ELSIF role_code IN ('PROCESO_DIARIO', 'CARGA_CONFIGURACION') THEN
    RAISE EXCEPTION 'iam.role_assignment: % is only for its service identity (E-FIS1-04-7, E-CFG-1)', role_code;
  END IF;
  RETURN NEW;
END $$;

-- Existing companies; `rochell-migrate create-company` assigns it to new ones.
INSERT INTO iam.role_assignment (assignment_id, company_id, user_id, role_id, plant_id, valid_from, granted_by)
SELECT gen_random_uuid(), c.company_id, '00000000-0000-7000-8000-00000000d003', r.role_id, NULL, now(), '00000000-0000-7000-8000-00000000d001'
FROM md.company c CROSS JOIN iam.role r WHERE r.code = 'CARGA_CONFIGURACION';
