-- SEC-02 · Invitations and user deactivation (E-SEC-3, E-SEC-02-1…8).
--   - iam.user kind GUEST: someone outside the company signing in with their own Google account; bound to a Google subject, no
--     employee. HUMAN keeps its employee id (an invited company user gets a fresh one).
--   - iam.invitation: an e-mail, a name and roles, requested by one person (role:assign) and approved by another
--     (role:second_approve; the superadministrator's waiver applies); valid 7 days from its approval; a guest's roles end on
--     roles_valid_to. Accepted at the first Google sign-in with that exact verified e-mail.
--   - iam.user_status_request: disabling or enabling a user, with a second approver.

ALTER TABLE iam.user
  DROP CONSTRAINT user_kind,
  ADD CONSTRAINT user_kind CHECK (kind IN ('HUMAN', 'SERVICE', 'SYNTHETIC', 'GUEST')),
  ADD CONSTRAINT user_guest_identity CHECK (kind <> 'GUEST' OR (oidc_subject IS NOT NULL AND employee_id IS NULL AND email IS NOT NULL));

-- The application still never writes iam.user directly (identity master data): the two functions at the end do it, each checking
-- its own condition — an approved, unexpired invitation for that e-mail, or an approved status request.

ALTER TABLE core.mail_message
  DROP CONSTRAINT mail_message_document_type_known,
  ADD CONSTRAINT mail_message_document_type_known CHECK (document_type IN ('QUOTE', 'PROFORMA', 'DELIVERY', 'STATEMENT', 'AR_AGING', 'INVOICE', 'INVITATION'));

CREATE TABLE iam.invitation (
  company_id          uuid        NOT NULL,
  invitation_id       uuid        NOT NULL,
  email               text        NOT NULL,
  display_name        text        NOT NULL,
  guest               boolean     NOT NULL,
  roles_valid_to      timestamptz,
  status              text        NOT NULL,
  requested_by        uuid        NOT NULL,
  requested_at        timestamptz NOT NULL,
  second_approved_by  uuid,
  approved_at         timestamptz,
  expires_at          timestamptz,
  rejected_by         uuid,
  cancelled_by        uuid,
  decided_at          timestamptz,
  decision_reason     text,
  accepted_user_id    uuid,
  accepted_at         timestamptz,
  version             bigint      NOT NULL,
  CONSTRAINT invitation_pk PRIMARY KEY (invitation_id),
  CONSTRAINT invitation_company_uq UNIQUE (company_id, invitation_id),
  CONSTRAINT invitation_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT invitation_requested_by_fk FOREIGN KEY (requested_by) REFERENCES iam.user (user_id),
  CONSTRAINT invitation_approved_by_fk FOREIGN KEY (second_approved_by) REFERENCES iam.user (user_id),
  CONSTRAINT invitation_rejected_by_fk FOREIGN KEY (rejected_by) REFERENCES iam.user (user_id),
  CONSTRAINT invitation_cancelled_by_fk FOREIGN KEY (cancelled_by) REFERENCES iam.user (user_id),
  CONSTRAINT invitation_accepted_user_fk FOREIGN KEY (accepted_user_id) REFERENCES iam.user (user_id),
  CONSTRAINT invitation_email CHECK (email = lower(btrim(email)) AND email ~ '^[^@\s]+@[^@\s]+\.[^@\s]+$' AND length(email) <= 254),
  CONSTRAINT invitation_name CHECK (length(btrim(display_name)) BETWEEN 1 AND 200),
  CONSTRAINT invitation_status CHECK (status IN ('REQUESTED', 'APPROVED', 'ACCEPTED', 'REJECTED', 'CANCELLED')),
  CONSTRAINT invitation_guest_roles_end CHECK (guest = (roles_valid_to IS NOT NULL)),
  CONSTRAINT invitation_approved CHECK (
    (status NOT IN ('APPROVED', 'ACCEPTED') OR (second_approved_by IS NOT NULL AND approved_at IS NOT NULL AND expires_at IS NOT NULL))
    AND (status NOT IN ('REQUESTED', 'REJECTED') OR second_approved_by IS NULL)),
  CONSTRAINT invitation_accepted CHECK ((status = 'ACCEPTED') = (accepted_user_id IS NOT NULL AND accepted_at IS NOT NULL)),
  CONSTRAINT invitation_rejected CHECK ((status = 'REJECTED') = (rejected_by IS NOT NULL)),
  CONSTRAINT invitation_cancelled CHECK ((status = 'CANCELLED') = (cancelled_by IS NOT NULL)),
  CONSTRAINT invitation_version_positive CHECK (version >= 1)
);

-- One invitation in progress per e-mail and company.
CREATE UNIQUE INDEX invitation_one_open ON iam.invitation (company_id, email) WHERE status IN ('REQUESTED', 'APPROVED');

CREATE TABLE iam.invitation_role (
  company_id     uuid NOT NULL,
  invitation_id  uuid NOT NULL,
  role_id        uuid NOT NULL,
  plant_id       uuid,
  CONSTRAINT invitation_role_invitation_fk FOREIGN KEY (company_id, invitation_id) REFERENCES iam.invitation (company_id, invitation_id),
  CONSTRAINT invitation_role_role_fk FOREIGN KEY (role_id) REFERENCES iam.role (role_id),
  CONSTRAINT invitation_role_plant_fk FOREIGN KEY (company_id, plant_id) REFERENCES md.plant (company_id, plant_id)
);
CREATE UNIQUE INDEX invitation_role_uq ON iam.invitation_role (invitation_id, role_id, coalesce(plant_id, '00000000-0000-0000-0000-000000000000'::uuid));

CREATE FUNCTION iam.invitation_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'UPDATE' THEN
    RAISE EXCEPTION 'iam.invitation rows cannot be deleted; an invitation is cancelled';
  END IF;
  IF ROW(NEW.company_id, NEW.invitation_id, NEW.email, NEW.display_name, NEW.guest, NEW.roles_valid_to, NEW.requested_by, NEW.requested_at)
     IS DISTINCT FROM ROW(OLD.company_id, OLD.invitation_id, OLD.email, OLD.display_name, OLD.guest, OLD.roles_valid_to, OLD.requested_by, OLD.requested_at)
     OR NEW.version <> OLD.version + 1
     OR NOT ((OLD.status = 'REQUESTED' AND NEW.status IN ('APPROVED', 'REJECTED', 'CANCELLED'))
             OR (OLD.status = 'APPROVED' AND NEW.status IN ('ACCEPTED', 'CANCELLED'))) THEN
    RAISE EXCEPTION 'iam.invitation: % → % or a change of what was invited is not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER invitation_guard BEFORE UPDATE OR DELETE ON iam.invitation FOR EACH ROW EXECUTE FUNCTION iam.invitation_guard();
CREATE TRIGGER invitation_role_immutable BEFORE UPDATE OR DELETE ON iam.invitation_role FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER invitation_four_eyes BEFORE INSERT OR UPDATE ON iam.invitation
  FOR EACH ROW EXECUTE FUNCTION core.four_eyes('second_approved_by', 'requested_by', 'invitation_distinct_approver');

CREATE TABLE iam.user_status_request (
  company_id          uuid        NOT NULL,
  request_id          uuid        NOT NULL,
  user_id             uuid        NOT NULL,
  action              text        NOT NULL,
  reason              text        NOT NULL,
  status              text        NOT NULL,
  requested_by        uuid        NOT NULL,
  requested_at        timestamptz NOT NULL,
  second_approved_by  uuid,
  rejected_by         uuid,
  decided_at          timestamptz,
  version             bigint      NOT NULL,
  CONSTRAINT user_status_request_pk PRIMARY KEY (request_id),
  CONSTRAINT user_status_request_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT user_status_request_user_fk FOREIGN KEY (user_id) REFERENCES iam.user (user_id),
  CONSTRAINT user_status_request_requested_by_fk FOREIGN KEY (requested_by) REFERENCES iam.user (user_id),
  CONSTRAINT user_status_request_approved_by_fk FOREIGN KEY (second_approved_by) REFERENCES iam.user (user_id),
  CONSTRAINT user_status_request_rejected_by_fk FOREIGN KEY (rejected_by) REFERENCES iam.user (user_id),
  CONSTRAINT user_status_request_action CHECK (action IN ('DISABLE', 'ENABLE')),
  CONSTRAINT user_status_request_reason CHECK (length(btrim(reason)) BETWEEN 1 AND 500),
  CONSTRAINT user_status_request_status CHECK (status IN ('REQUESTED', 'APPROVED', 'REJECTED')),
  CONSTRAINT user_status_request_not_for_self CHECK (requested_by <> user_id),
  CONSTRAINT user_status_request_decided CHECK ((status = 'APPROVED') = (second_approved_by IS NOT NULL) AND (status = 'REJECTED') = (rejected_by IS NOT NULL)
    AND (status = 'REQUESTED') = (decided_at IS NULL)),
  CONSTRAINT user_status_request_version_positive CHECK (version >= 1)
);
CREATE UNIQUE INDEX user_status_request_one_open ON iam.user_status_request (company_id, user_id) WHERE status = 'REQUESTED';

CREATE FUNCTION iam.user_status_request_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'UPDATE' THEN
    RAISE EXCEPTION 'iam.user_status_request rows cannot be deleted';
  END IF;
  IF ROW(NEW.company_id, NEW.request_id, NEW.user_id, NEW.action, NEW.reason, NEW.requested_by, NEW.requested_at)
     IS DISTINCT FROM ROW(OLD.company_id, OLD.request_id, OLD.user_id, OLD.action, OLD.reason, OLD.requested_by, OLD.requested_at)
     OR OLD.status <> 'REQUESTED' OR NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'iam.user_status_request: only a REQUESTED request is decided, once';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER user_status_request_guard BEFORE UPDATE OR DELETE ON iam.user_status_request FOR EACH ROW EXECUTE FUNCTION iam.user_status_request_guard();
CREATE TRIGGER user_status_request_four_eyes BEFORE INSERT OR UPDATE ON iam.user_status_request
  FOR EACH ROW EXECUTE FUNCTION core.four_eyes('second_approved_by', 'requested_by', 'user_status_request_distinct_approver');

DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['iam.invitation', 'iam.invitation_role', 'iam.user_status_request'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON iam.invitation, iam.invitation_role, iam.user_status_request TO rochell_app;
GRANT UPDATE (status, second_approved_by, approved_at, expires_at, rejected_by, cancelled_by, decided_at, decision_reason, version)
  ON iam.invitation TO rochell_app;
GRANT UPDATE (status, second_approved_by, rejected_by, decided_at, version) ON iam.user_status_request TO rochell_app;

-- The first sign-in does not yet know the company: it finds the approved, unexpired invitation of a verified e-mail through this
-- function (it bypasses row-level security for that lookup only), then works under the invitation's company.
CREATE FUNCTION iam.open_invitation_for(p_email text, p_now timestamptz)
  RETURNS TABLE (company_id uuid, invitation_id uuid)
  LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, iam AS $$
  SELECT i.company_id, i.invitation_id FROM iam.invitation i
  WHERE i.email = lower(btrim(p_email)) AND i.status = 'APPROVED' AND i.expires_at > p_now
  ORDER BY i.approved_at DESC
  LIMIT 1
$$;
REVOKE ALL ON FUNCTION iam.open_invitation_for(text, timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION iam.open_invitation_for(text, timestamptz) TO rochell_app;

-- E-SEC-02-2: accepts an invitation at the first sign-in — the invitation is APPROVED, unexpired and for exactly this e-mail; the
-- user is created HUMAN (with a fresh employee id) or GUEST bound to the Google subject, with the invited roles granted by the
-- invitation's approver (a guest's until roles_valid_to), and the invitation becomes ACCEPTED. Returns the new user's id.
CREATE FUNCTION iam.accept_invitation(p_invitation uuid, p_email text, p_subject text, p_name text, p_user uuid, p_employee uuid, p_now timestamptz)
  RETURNS uuid
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, iam AS $$
DECLARE
  inv iam.invitation%ROWTYPE;
BEGIN
  SELECT * INTO inv FROM iam.invitation WHERE invitation_id = p_invitation FOR UPDATE;
  IF NOT FOUND OR inv.status <> 'APPROVED' OR inv.expires_at <= p_now OR inv.email <> lower(btrim(p_email)) THEN
    RAISE EXCEPTION 'iam.accept_invitation: no approved, unexpired invitation for this e-mail';
  END IF;
  INSERT INTO iam.user (user_id, kind, employee_id, email, oidc_subject, status, display_name)
  VALUES (p_user, CASE WHEN inv.guest THEN 'GUEST' ELSE 'HUMAN' END, CASE WHEN inv.guest THEN NULL ELSE p_employee END, inv.email, p_subject, 'ACTIVE',
          coalesce(nullif(btrim(p_name), ''), inv.display_name));
  INSERT INTO iam.role_assignment (assignment_id, company_id, user_id, role_id, plant_id, valid_from, valid_to, granted_by)
  SELECT gen_random_uuid(), ir.company_id, p_user, ir.role_id, ir.plant_id, p_now, inv.roles_valid_to, inv.second_approved_by
  FROM iam.invitation_role ir WHERE ir.invitation_id = p_invitation;
  UPDATE iam.invitation SET status = 'ACCEPTED', accepted_user_id = p_user, accepted_at = p_now, version = version + 1 WHERE invitation_id = p_invitation;
  RETURN p_user;
END $$;
REVOKE ALL ON FUNCTION iam.accept_invitation(uuid, text, text, text, uuid, uuid, timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION iam.accept_invitation(uuid, text, text, text, uuid, uuid, timestamptz) TO rochell_app;

-- E-SEC-02-7: applies an APPROVED status request to its user (a person, never a service identity).
CREATE FUNCTION iam.apply_user_status_request(p_request uuid)
  RETURNS text
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, iam AS $$
DECLARE
  req iam.user_status_request%ROWTYPE;
  target text;
BEGIN
  SELECT * INTO req FROM iam.user_status_request WHERE request_id = p_request;
  IF NOT FOUND OR req.status <> 'APPROVED' THEN
    RAISE EXCEPTION 'iam.apply_user_status_request: the request is not approved';
  END IF;
  target := CASE req.action WHEN 'DISABLE' THEN 'DISABLED' ELSE 'ACTIVE' END;
  UPDATE iam.user SET status = target WHERE user_id = req.user_id AND kind IN ('HUMAN', 'GUEST');
  IF NOT FOUND THEN
    RAISE EXCEPTION 'iam.apply_user_status_request: the user is not a person';
  END IF;
  RETURN target;
END $$;
REVOKE ALL ON FUNCTION iam.apply_user_status_request(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION iam.apply_user_status_request(uuid) TO rochell_app;
