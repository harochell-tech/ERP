-- LAB1-03c · the public verification of a lab certificate (approved errata E-LAB1-03-8, 15).
--   The QR on a certificate opens a page without sign-in. The API answers it as a service identity of its own, «Verificación pública»
--   (role VERIFICACION_PUBLICA), whose only permission reads a certificate by its public code (`lab_certificate:verify`) — like
--   «Confirmación de entrega» does for the drivers' page (E-ENT1-01-5).

INSERT INTO iam.permission (permission_code, access) VALUES ('lab_certificate:verify', 'READ');
INSERT INTO iam.user (user_id, kind, status, display_name) VALUES ('00000000-0000-7000-8000-00000000d005', 'SERVICE', 'ACTIVE', 'Verificación pública');
INSERT INTO iam.role (role_id, code, name, description)
VALUES (gen_random_uuid(), 'VERIFICACION_PUBLICA', 'Verificación pública',
        'Identidad de servicio: muestra, sin iniciar sesión, si un certificado de laboratorio es auténtico y está vigente, leyendo el código de su QR. No hace nada más.');
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT role_id, 'lab_certificate:verify' FROM iam.role WHERE code = 'VERIFICACION_PUBLICA';

-- Service roles are held only by service identities, each by its own, and service identities hold nothing else.
CREATE OR REPLACE FUNCTION iam.role_assignment_service_guard() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = iam, pg_temp AS $$
DECLARE
  role_code text := (SELECT code FROM iam.role WHERE role_id = NEW.role_id);
  expected text := CASE NEW.user_id
    WHEN '00000000-0000-7000-8000-00000000d002' THEN 'PROCESO_DIARIO'
    WHEN '00000000-0000-7000-8000-00000000d003' THEN 'CARGA_CONFIGURACION'
    WHEN '00000000-0000-7000-8000-00000000d004' THEN 'CONFIRMACION_ENTREGA'
    WHEN '00000000-0000-7000-8000-00000000d005' THEN 'VERIFICACION_PUBLICA' END;
BEGIN
  IF (SELECT kind FROM iam.user WHERE user_id = NEW.user_id) = 'SERVICE' THEN
    IF role_code IS DISTINCT FROM expected THEN
      RAISE EXCEPTION 'iam.role_assignment: a service identity holds only its own service role (E-FIS1-04-7, E-CFG-1, E-ENT1-01-5, E-LAB1-03-15)';
    END IF;
  ELSIF role_code IN ('PROCESO_DIARIO', 'CARGA_CONFIGURACION', 'CONFIRMACION_ENTREGA', 'VERIFICACION_PUBLICA') THEN
    RAISE EXCEPTION 'iam.role_assignment: % is only for its service identity (E-FIS1-04-7, E-CFG-1, E-ENT1-01-5, E-LAB1-03-15)', role_code;
  END IF;
  RETURN NEW;
END $$;

-- Existing companies; `rochell-migrate create-company` assigns it to new ones.
INSERT INTO iam.role_assignment (assignment_id, company_id, user_id, role_id, plant_id, valid_from, granted_by)
SELECT gen_random_uuid(), c.company_id, '00000000-0000-7000-8000-00000000d005', r.role_id, NULL, now(), '00000000-0000-7000-8000-00000000d001'
FROM md.company c CROSS JOIN iam.role r WHERE r.code = 'VERIFICACION_PUBLICA';
