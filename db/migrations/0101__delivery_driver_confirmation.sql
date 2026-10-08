-- ENT1-01 · the driver confirms the delivery from the delivery note's QR (approved errata E-ENT-1…8, E-ENT1-01-1…10).
--   - Driver PIN (E-ENT-2, E-ENT1-01-3): four digits set by Dispatch, kept only as a slow salted hash; the link counts failed tries.
--   - One link per own-transport delivery (E-ENT-1, E-ENT1-01-2/4): created at gate out, valid 7 days; its token is an HMAC of the
--     delivery and the generation under a server key, so the database holds no token. Reopening starts a new generation.
--   - Every PIN try is kept (E-ENT1-01-3: 5 per link, 30 per hour per client address); the driver's confirmation is kept once and
--     never changed (E-ENT-3, E-ENT1-01-6/7/8): receiver, full / differences, both times, location, evidence in the private bucket.
--   - A full receipt becomes the POD under the service identity «Confirmación de entrega» (E-ENT-4, E-ENT1-01-5), which holds only
--     delivery:driver_confirm; the POD remembers the confirmation it came from (Dispatch completes the ones with differences from it).

CREATE TABLE log.driver_pin (
  company_id  uuid        NOT NULL,
  driver_id   uuid        NOT NULL,
  pin_hash    bytea       NOT NULL,
  pin_salt    bytea       NOT NULL,
  iterations  integer     NOT NULL,
  set_by      uuid        NOT NULL,
  set_at      timestamptz NOT NULL,
  version     bigint      NOT NULL,
  CONSTRAINT driver_pin_pk PRIMARY KEY (company_id, driver_id),
  CONSTRAINT driver_pin_driver_fk FOREIGN KEY (company_id, driver_id) REFERENCES log.driver (company_id, driver_id),
  CONSTRAINT driver_pin_set_by_fk FOREIGN KEY (set_by) REFERENCES iam.user (user_id),
  CONSTRAINT driver_pin_hash CHECK (octet_length(pin_hash) = 32 AND octet_length(pin_salt) = 16 AND iterations >= 100000),
  CONSTRAINT driver_pin_version CHECK (version >= 1)
);
GRANT SELECT, INSERT ON log.driver_pin TO rochell_app;
GRANT UPDATE (pin_hash, pin_salt, iterations, set_by, set_at, version) ON log.driver_pin TO rochell_app;

CREATE TABLE log.delivery_link (
  company_id       uuid        NOT NULL,
  delivery_id      uuid        NOT NULL,
  generation       integer     NOT NULL,
  status           text        NOT NULL,
  expires_at       timestamptz NOT NULL,
  failed_attempts  integer     NOT NULL DEFAULT 0,
  created_at       timestamptz NOT NULL,
  version          bigint      NOT NULL,
  CONSTRAINT delivery_link_pk PRIMARY KEY (company_id, delivery_id),
  CONSTRAINT delivery_link_delivery_fk FOREIGN KEY (company_id, delivery_id) REFERENCES log.delivery (company_id, delivery_id),
  -- ACTIVE: the driver may confirm; LOCKED: 5 failed PINs, Dispatch reopens; CONFIRMED: used; ANNULLED: the delivery was cancelled,
  -- returned or its POD recorded by Dispatch (E-ENT1-01-9). Expiry is read from expires_at.
  CONSTRAINT delivery_link_status CHECK (status IN ('ACTIVE', 'LOCKED', 'CONFIRMED', 'ANNULLED')),
  CONSTRAINT delivery_link_generation CHECK (generation >= 1),
  CONSTRAINT delivery_link_attempts CHECK (failed_attempts BETWEEN 0 AND 5 AND (status <> 'LOCKED' OR failed_attempts = 5)),
  CONSTRAINT delivery_link_expiry CHECK (expires_at > created_at),
  CONSTRAINT delivery_link_version CHECK (version >= 1)
);
GRANT SELECT, INSERT ON log.delivery_link TO rochell_app;
GRANT UPDATE (generation, status, expires_at, failed_attempts, version) ON log.delivery_link TO rochell_app;

CREATE TABLE log.delivery_link_attempt (
  attempt_id      uuid        NOT NULL,
  company_id      uuid        NOT NULL,
  delivery_id     uuid        NOT NULL,
  generation      integer     NOT NULL,
  attempted_at    timestamptz NOT NULL,
  client_address  inet        NOT NULL,
  pin_ok          boolean     NOT NULL,
  CONSTRAINT delivery_link_attempt_pk PRIMARY KEY (attempt_id),
  CONSTRAINT delivery_link_attempt_link_fk FOREIGN KEY (company_id, delivery_id) REFERENCES log.delivery_link (company_id, delivery_id)
);
CREATE INDEX delivery_link_attempt_address_ix ON log.delivery_link_attempt (client_address, attempted_at);
CREATE TRIGGER delivery_link_attempt_append_only BEFORE UPDATE OR DELETE ON log.delivery_link_attempt FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
GRANT SELECT, INSERT ON log.delivery_link_attempt TO rochell_app;

CREATE TABLE log.driver_confirmation (
  confirmation_id       uuid          NOT NULL,
  company_id            uuid          NOT NULL,
  delivery_id           uuid          NOT NULL,
  generation            integer       NOT NULL,
  driver_id             uuid          NOT NULL,
  receiver_name         text          NOT NULL,
  receiver_national_id  text,
  outcome               text          NOT NULL,
  note                  text,
  phone_at              timestamptz,
  server_at             timestamptz   NOT NULL,
  confirmed_at          timestamptz   NOT NULL,
  latitude              numeric(9,6),
  longitude             numeric(9,6),
  accuracy_m            numeric(9,2),
  evidence_kind         text          NOT NULL,
  evidence_ref          text          NOT NULL,
  evidence_sha256       bytea         NOT NULL,
  client_address        inet          NOT NULL,
  event_id              uuid          NOT NULL,
  CONSTRAINT driver_confirmation_pk PRIMARY KEY (confirmation_id),
  CONSTRAINT driver_confirmation_company_uq UNIQUE (company_id, confirmation_id),
  CONSTRAINT driver_confirmation_once UNIQUE (company_id, delivery_id, generation),
  CONSTRAINT driver_confirmation_link_fk FOREIGN KEY (company_id, delivery_id) REFERENCES log.delivery_link (company_id, delivery_id),
  CONSTRAINT driver_confirmation_driver_fk FOREIGN KEY (company_id, driver_id) REFERENCES log.driver (company_id, driver_id),
  CONSTRAINT driver_confirmation_event_fk FOREIGN KEY (company_id, event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT driver_confirmation_receiver CHECK (length(btrim(receiver_name)) BETWEEN 1 AND 200),
  CONSTRAINT driver_confirmation_national_id CHECK (receiver_national_id IS NULL OR receiver_national_id ~ '^[0-9]{11}$'),
  CONSTRAINT driver_confirmation_outcome CHECK (outcome IN ('FULL', 'DIFFERENCES')),
  -- E-ENT-3: differences always say what happened.
  CONSTRAINT driver_confirmation_note CHECK ((note IS NULL OR length(btrim(note)) BETWEEN 1 AND 1000)
    AND (outcome = 'FULL' OR note IS NOT NULL)),
  -- E-ENT1-01-6: the phone's time only when it lies before the server's time + 5 minutes (the lower bound, gate out, is checked by
  -- the command); otherwise the server's.
  CONSTRAINT driver_confirmation_time CHECK (confirmed_at = server_at OR (phone_at IS NOT NULL AND confirmed_at = phone_at AND phone_at <= server_at + interval '5 minutes')),
  CONSTRAINT driver_confirmation_location CHECK ((latitude IS NULL) = (longitude IS NULL)
    AND (latitude IS NULL OR (latitude BETWEEN -90 AND 90 AND longitude BETWEEN -180 AND 180))
    AND (accuracy_m IS NULL OR (latitude IS NOT NULL AND accuracy_m >= 0))),
  CONSTRAINT driver_confirmation_evidence CHECK (evidence_kind IN ('PHOTO', 'SIGNATURE') AND length(btrim(evidence_ref)) BETWEEN 1 AND 200
    AND octet_length(evidence_sha256) = 32)
);
CREATE TRIGGER driver_confirmation_append_only BEFORE UPDATE OR DELETE ON log.driver_confirmation FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
GRANT SELECT, INSERT ON log.driver_confirmation TO rochell_app;

-- The POD made from (or completed from) a driver's confirmation points to it.
ALTER TABLE log.pod
  ADD COLUMN driver_confirmation_id uuid,
  ADD CONSTRAINT pod_driver_confirmation_fk FOREIGN KEY (company_id, driver_confirmation_id) REFERENCES log.driver_confirmation (company_id, confirmation_id),
  ADD CONSTRAINT pod_driver_confirmation_uq UNIQUE (driver_confirmation_id);

-- E-ENT1-01-1 / 5: Dispatch sets PINs and reopens links; the service identity only confirms full receipts.
INSERT INTO iam.permission (permission_code, access) VALUES ('driver_pin:manage', 'WRITE'), ('delivery_link:reopen', 'WRITE'), ('delivery:driver_confirm', 'WRITE');
INSERT INTO iam.user (user_id, kind, status, display_name) VALUES ('00000000-0000-7000-8000-00000000d004', 'SERVICE', 'ACTIVE', 'Confirmación de entrega');
INSERT INTO iam.role (role_id, code, name, description)
VALUES (gen_random_uuid(), 'CONFIRMACION_ENTREGA', 'Confirmación de entrega',
        'Identidad de servicio: registra la entrega en obra cuando el chofer confirma desde el QR del conduce que todo fue recibido. No hace nada más.');
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES ('DESPACHO', 'driver_pin:manage'), ('DESPACHO', 'delivery_link:reopen'), ('CONFIRMACION_ENTREGA', 'delivery:driver_confirm')) AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;

-- Service roles are held only by service identities, each by its own, and service identities hold nothing else.
CREATE OR REPLACE FUNCTION iam.role_assignment_service_guard() RETURNS trigger
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = iam, pg_temp AS $$
DECLARE
  role_code text := (SELECT code FROM iam.role WHERE role_id = NEW.role_id);
  expected text := CASE NEW.user_id
    WHEN '00000000-0000-7000-8000-00000000d002' THEN 'PROCESO_DIARIO'
    WHEN '00000000-0000-7000-8000-00000000d003' THEN 'CARGA_CONFIGURACION'
    WHEN '00000000-0000-7000-8000-00000000d004' THEN 'CONFIRMACION_ENTREGA' END;
BEGIN
  IF (SELECT kind FROM iam.user WHERE user_id = NEW.user_id) = 'SERVICE' THEN
    IF role_code IS DISTINCT FROM expected THEN
      RAISE EXCEPTION 'iam.role_assignment: a service identity holds only its own service role (E-FIS1-04-7, E-CFG-1, E-ENT1-01-5)';
    END IF;
  ELSIF role_code IN ('PROCESO_DIARIO', 'CARGA_CONFIGURACION', 'CONFIRMACION_ENTREGA') THEN
    RAISE EXCEPTION 'iam.role_assignment: % is only for its service identity (E-FIS1-04-7, E-CFG-1, E-ENT1-01-5)', role_code;
  END IF;
  RETURN NEW;
END $$;

-- Existing companies; `rochell-migrate create-company` assigns it to new ones.
INSERT INTO iam.role_assignment (assignment_id, company_id, user_id, role_id, plant_id, valid_from, granted_by)
SELECT gen_random_uuid(), c.company_id, '00000000-0000-7000-8000-00000000d004', r.role_id, NULL, now(), '00000000-0000-7000-8000-00000000d001'
FROM md.company c CROSS JOIN iam.role r WHERE r.code = 'CONFIRMACION_ENTREGA';
