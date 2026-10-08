-- MFG3-01 · efficiency and preventive maintenance: schema (approved errata E-MFG3-1…11, E-MFG3-00-1…4, E-MFG3-01-1…5).
--   - What Core read from the portal, kept and never changed (E-MFG3-3, E-MFG3-01-1): each distinct version of a stoppage (its reason and
--     end arrive later), of a maintenance window and of a machine's daily report (broken blocks, good blocks confirmed after curing).
--     Portal times are Dominican local time, kept as `timestamp`, like the readings. The latest version counts.
--   - The ideal cycle per machine and product (E-MFG3-5, E-MFG3-01-2), in seconds, from a date on; set by the plant manager with
--     production_master:manage; never changed — a new row from a later date replaces it.
--   - Preventive maintenance (E-MFG3-8…10, E-MFG3-01-3/5): tasks per machine every N cycles, running hours or days, deactivated
--     instead of deleted; and each time a task was done (from the portal's Mantenimientos, E-MFG3-9), never changed.
--   - maintenance_plan:manage for the plant manager (149 permissions).

CREATE TABLE mfg.portal_stoppage (
  stoppage_row_id   uuid        NOT NULL,
  company_id        uuid        NOT NULL,
  portal_id         bigint      NOT NULL,
  portal_code       text        NOT NULL,
  started_at        timestamp   NOT NULL,
  ended_at          timestamp,
  duration_seconds  integer,
  reason            text,
  detail            text,
  content_sha256    bytea       NOT NULL,
  fetched_at        timestamptz NOT NULL,
  CONSTRAINT portal_stoppage_pk PRIMARY KEY (stoppage_row_id),
  CONSTRAINT portal_stoppage_version_uq UNIQUE (company_id, portal_id, content_sha256),
  CONSTRAINT portal_stoppage_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT portal_stoppage_values CHECK (portal_id > 0 AND (ended_at IS NULL OR ended_at >= started_at) AND (duration_seconds IS NULL OR duration_seconds >= 0)
    AND octet_length(content_sha256) = 32)
);
CREATE INDEX portal_stoppage_latest ON mfg.portal_stoppage (company_id, portal_code, started_at, portal_id, fetched_at DESC);
CREATE TRIGGER portal_stoppage_append_only BEFORE UPDATE OR DELETE ON mfg.portal_stoppage FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

CREATE TABLE mfg.portal_maintenance (
  maintenance_row_id uuid        NOT NULL,
  company_id         uuid        NOT NULL,
  portal_id          bigint      NOT NULL,
  portal_code        text        NOT NULL,
  starts_at          timestamp   NOT NULL,
  ends_at            timestamp,
  reason             text        NOT NULL,
  task_ref           text,
  content_sha256     bytea       NOT NULL,
  fetched_at         timestamptz NOT NULL,
  CONSTRAINT portal_maintenance_pk PRIMARY KEY (maintenance_row_id),
  CONSTRAINT portal_maintenance_version_uq UNIQUE (company_id, portal_id, content_sha256),
  CONSTRAINT portal_maintenance_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT portal_maintenance_values CHECK (portal_id > 0 AND (ends_at IS NULL OR ends_at >= starts_at) AND octet_length(content_sha256) = 32)
);
CREATE INDEX portal_maintenance_latest ON mfg.portal_maintenance (company_id, portal_code, starts_at, portal_id, fetched_at DESC);
CREATE TRIGGER portal_maintenance_append_only BEFORE UPDATE OR DELETE ON mfg.portal_maintenance FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

CREATE TABLE mfg.portal_daily_report (
  report_row_id    uuid        NOT NULL,
  company_id       uuid        NOT NULL,
  portal_code      text        NOT NULL,
  report_date      date        NOT NULL,
  broken_units     integer,
  cured_good       jsonb,
  updated_in_portal text,
  content_sha256   bytea       NOT NULL,
  fetched_at       timestamptz NOT NULL,
  CONSTRAINT portal_daily_report_pk PRIMARY KEY (report_row_id),
  CONSTRAINT portal_daily_report_version_uq UNIQUE (company_id, portal_code, report_date, content_sha256),
  CONSTRAINT portal_daily_report_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT portal_daily_report_values CHECK ((broken_units IS NULL OR broken_units >= 0) AND (cured_good IS NULL OR jsonb_typeof(cured_good) = 'object')
    AND octet_length(content_sha256) = 32)
);
CREATE INDEX portal_daily_report_latest ON mfg.portal_daily_report (company_id, portal_code, report_date, fetched_at DESC);
CREATE TRIGGER portal_daily_report_append_only BEFORE UPDATE OR DELETE ON mfg.portal_daily_report FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

GRANT SELECT, INSERT ON mfg.portal_stoppage, mfg.portal_maintenance, mfg.portal_daily_report TO rochell_app;

CREATE TABLE mfg.ideal_cycle (
  company_id   uuid          NOT NULL,
  machine_id   uuid          NOT NULL,
  item_id      uuid          NOT NULL,
  valid_from   date          NOT NULL,
  seconds      numeric(9,3)  NOT NULL,
  set_by       uuid          NOT NULL,
  set_at       timestamptz   NOT NULL,
  CONSTRAINT ideal_cycle_pk PRIMARY KEY (company_id, machine_id, item_id, valid_from),
  CONSTRAINT ideal_cycle_machine_fk FOREIGN KEY (company_id, machine_id) REFERENCES md.machine (company_id, machine_id),
  CONSTRAINT ideal_cycle_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT ideal_cycle_set_by_fk FOREIGN KEY (set_by) REFERENCES iam.user (user_id),
  CONSTRAINT ideal_cycle_seconds CHECK (seconds > 0 AND seconds <= 3600)
);
CREATE TRIGGER ideal_cycle_append_only BEFORE UPDATE OR DELETE ON mfg.ideal_cycle FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
GRANT SELECT, INSERT ON mfg.ideal_cycle TO rochell_app;

CREATE TABLE mfg.maintenance_task (
  task_id         uuid        NOT NULL,
  company_id      uuid        NOT NULL,
  machine_id      uuid        NOT NULL,
  code            text        NOT NULL,
  name            text        NOT NULL,
  frequency_kind  text        NOT NULL,
  every           integer     NOT NULL,
  instructions    text,
  status          text        NOT NULL,
  created_by      uuid        NOT NULL,
  created_at      timestamptz NOT NULL,
  version         bigint      NOT NULL,
  CONSTRAINT maintenance_task_pk PRIMARY KEY (task_id),
  CONSTRAINT maintenance_task_company_uq UNIQUE (company_id, task_id),
  CONSTRAINT maintenance_task_code_uq UNIQUE (company_id, code),
  CONSTRAINT maintenance_task_machine_fk FOREIGN KEY (company_id, machine_id) REFERENCES md.machine (company_id, machine_id),
  CONSTRAINT maintenance_task_created_by_fk FOREIGN KEY (created_by) REFERENCES iam.user (user_id),
  CONSTRAINT maintenance_task_code CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,29}$'),
  CONSTRAINT maintenance_task_name CHECK (length(btrim(name)) BETWEEN 1 AND 120 AND (instructions IS NULL OR length(btrim(instructions)) BETWEEN 1 AND 2000)),
  CONSTRAINT maintenance_task_frequency CHECK (frequency_kind IN ('CYCLES', 'RUNNING_HOURS', 'DAYS') AND every > 0),
  CONSTRAINT maintenance_task_status CHECK (status IN ('ACTIVE', 'INACTIVE')),
  CONSTRAINT maintenance_task_version CHECK (version >= 1)
);
GRANT SELECT, INSERT ON mfg.maintenance_task TO rochell_app;
GRANT UPDATE (name, frequency_kind, every, instructions, status, version) ON mfg.maintenance_task TO rochell_app;

-- E-MFG3-9: done in the portal (its maintenance window names the task) — or recorded in Core by the plant manager.
CREATE TABLE mfg.maintenance_done (
  done_id             uuid        NOT NULL,
  company_id          uuid        NOT NULL,
  task_id             uuid        NOT NULL,
  done_at             timestamp   NOT NULL,
  source              text        NOT NULL,
  portal_maintenance_id bigint,
  recorded_by         uuid,
  note                text,
  recorded_at         timestamptz NOT NULL,
  CONSTRAINT maintenance_done_pk PRIMARY KEY (done_id),
  CONSTRAINT maintenance_done_task_fk FOREIGN KEY (company_id, task_id) REFERENCES mfg.maintenance_task (company_id, task_id),
  CONSTRAINT maintenance_done_recorded_by_fk FOREIGN KEY (recorded_by) REFERENCES iam.user (user_id),
  CONSTRAINT maintenance_done_portal_uq UNIQUE (company_id, task_id, portal_maintenance_id),
  CONSTRAINT maintenance_done_source CHECK ((source = 'PORTAL' AND portal_maintenance_id IS NOT NULL AND recorded_by IS NULL)
    OR (source = 'CORE' AND portal_maintenance_id IS NULL AND recorded_by IS NOT NULL)),
  CONSTRAINT maintenance_done_note CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 500)
);
CREATE INDEX maintenance_done_latest ON mfg.maintenance_done (company_id, task_id, done_at DESC);
CREATE TRIGGER maintenance_done_append_only BEFORE UPDATE OR DELETE ON mfg.maintenance_done FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
GRANT SELECT, INSERT ON mfg.maintenance_done TO rochell_app;

INSERT INTO iam.permission (permission_code, access) VALUES ('maintenance_plan:manage', 'WRITE');
INSERT INTO iam.role_permission (role_id, permission_code) SELECT role_id, 'maintenance_plan:manage' FROM iam.role WHERE code = 'GERENTE_PLANTA';
