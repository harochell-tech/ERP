-- UI-01 · Audit and security screens. Approved errata E-UI01-1…8.

-- E-UI01-4: reading users, their roles and the role change requests. READ: part of no segregation-of-duties pair; the Auditor
-- and security pattern rules allow it.
INSERT INTO iam.permission (permission_code, access) VALUES ('iam:read', 'READ');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, 'iam:read' FROM iam.role r WHERE r.code IN ('ADMIN_SEGURIDAD', 'SEGUNDO_APROBADOR_SEGURIDAD', 'AUDITOR', 'DIRECTOR');

-- E-UI01-6 (a): the second approver may reject a role change request, with a reason. REQUESTED → APPROVED | REJECTED, once.
ALTER TABLE iam.role_assignment_request
  ADD COLUMN rejected_by uuid REFERENCES iam.user (user_id),
  ADD COLUMN rejected_at timestamptz,
  ADD COLUMN rejection_reason text;

ALTER TABLE iam.role_assignment_request
  DROP CONSTRAINT role_assignment_request_status,
  ADD CONSTRAINT role_assignment_request_status CHECK (status IN ('REQUESTED', 'APPROVED', 'REJECTED')),
  ADD CONSTRAINT role_assignment_request_rejected CHECK (
    (status = 'REJECTED' AND rejected_by IS NOT NULL AND rejected_at IS NOT NULL AND length(btrim(rejection_reason)) > 0
       AND rejected_by <> requested_by AND rejected_by <> user_id)
    OR (status <> 'REJECTED' AND rejected_by IS NULL AND rejected_at IS NULL AND rejection_reason IS NULL));

CREATE OR REPLACE FUNCTION iam.role_assignment_request_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'UPDATE' THEN
    RAISE EXCEPTION 'iam.role_assignment_request rows cannot be deleted';
  END IF;
  IF ROW(NEW.company_id, NEW.request_id, NEW.user_id, NEW.role_id, NEW.plant_id, NEW.action, NEW.requested_by)
     IS DISTINCT FROM ROW(OLD.company_id, OLD.request_id, OLD.user_id, OLD.role_id, OLD.plant_id, OLD.action, OLD.requested_by) THEN
    RAISE EXCEPTION 'iam.role_assignment_request: only the decision columns may change';
  END IF;
  IF OLD.status <> 'REQUESTED' THEN
    RAISE EXCEPTION 'iam.role_assignment_request % is already %', OLD.request_id, OLD.status;
  END IF;
  RETURN NEW;
END $$;

GRANT UPDATE (rejected_by, rejected_at, rejection_reason) ON iam.role_assignment_request TO rochell_app;
