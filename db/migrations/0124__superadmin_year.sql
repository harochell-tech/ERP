-- SEC-01 · Superadministrator for up to a year (E-SEC-4, amends E-ADM-2-2).
--   - A SUPERADMIN assignment ends within 365 days of its start (365 days when none is given); renewed with a new grant.
--   - The SUPERADMIN assignments in force are brought to 365 days from their start. role_assignment_guard only lets valid_to move
--     earlier, so it is switched off for that one statement of this migration and on again.

CREATE OR REPLACE FUNCTION iam.role_assignment_superadmin_term() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = iam, pg_temp AS $$
BEGIN
  IF (SELECT code FROM iam.role WHERE role_id = NEW.role_id) = 'SUPERADMIN' THEN
    IF NEW.plant_id IS NOT NULL THEN
      RAISE EXCEPTION 'iam.role_assignment: SUPERADMIN is assigned for the whole company, not for a plant (E-ADM-2-2)';
    END IF;
    NEW.valid_to := coalesce(NEW.valid_to, NEW.valid_from + interval '365 days');
    IF NEW.valid_to > NEW.valid_from + interval '365 days' THEN
      RAISE EXCEPTION 'iam.role_assignment: SUPERADMIN ends within 365 days of its start (E-SEC-4)' USING ERRCODE = 'check_violation';
    END IF;
  END IF;
  RETURN NEW;
END $$;

ALTER TABLE iam.role_assignment DISABLE TRIGGER role_assignment_guard;
UPDATE iam.role_assignment ra SET valid_to = ra.valid_from + interval '365 days'
FROM iam.role r
WHERE r.role_id = ra.role_id AND r.code = 'SUPERADMIN' AND ra.valid_to > now() AND ra.valid_to < ra.valid_from + interval '365 days';
ALTER TABLE iam.role_assignment ENABLE TRIGGER role_assignment_guard;
