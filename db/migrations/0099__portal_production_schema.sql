-- MFG2-01 · production from the machines' portal: schema (approved errata E-MFG2-1…14, E-MFG2-01-1…8).
--   - Pairings, set by the plant manager on Producción › Portal (E-MFG2-3, E-MFG2-01-3/4/7): portal machine (planta1, planta2…) →
--     Core machine and its batch plant (NULL: the batch plant is offline, consumption typed, E-MFG2-11); mould (4, 6, 8) → product;
--     portal shift number → Core shift; batch-plant material code → item, unit and the location it is issued from.
--   - What Core read, kept and never changed (E-MFG2-01-6): every changed reading of a machine's shift and every batch-plant post.
--   - The shift summary knows where it came from (PORTAL / MANUAL), whether a person changed it (then the portal no longer
--     replaces it, E-MFG2-01-2) and where its consumption came from: BATCH_PLANT, MANUAL (typed, with a reason when the batch plant
--     is online) or PENDING (the recipe's theoretical as a placeholder, never posted, E-MFG2-8, E-MFG2-01-8).
--   - portal:manage for the plant manager; the daily process may record summaries and open runs (E-MFG2-6).

CREATE TABLE mfg.portal_machine (
  company_id     uuid   NOT NULL,
  portal_code    text   NOT NULL,
  machine_id     uuid   NOT NULL,
  batch_plant    text,
  version        bigint NOT NULL,
  CONSTRAINT portal_machine_pk PRIMARY KEY (company_id, portal_code),
  CONSTRAINT portal_machine_machine_uq UNIQUE (company_id, machine_id),
  CONSTRAINT portal_machine_machine_fk FOREIGN KEY (company_id, machine_id) REFERENCES md.machine (company_id, machine_id),
  CONSTRAINT portal_machine_code CHECK (portal_code ~ '^[a-z0-9_-]{1,40}$'),
  CONSTRAINT portal_machine_batch_plant CHECK (batch_plant IS NULL OR batch_plant ~ '^[a-z0-9_-]{1,40}$'),
  CONSTRAINT portal_machine_version CHECK (version >= 1)
);

CREATE TABLE mfg.portal_mould (
  company_id  uuid   NOT NULL,
  mould       text   NOT NULL,
  item_id     uuid   NOT NULL,
  version     bigint NOT NULL,
  CONSTRAINT portal_mould_pk PRIMARY KEY (company_id, mould),
  CONSTRAINT portal_mould_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT portal_mould_code CHECK (mould ~ '^[A-Za-z0-9]{1,10}$'),
  CONSTRAINT portal_mould_version CHECK (version >= 1)
);

CREATE TABLE mfg.portal_shift (
  company_id  uuid     NOT NULL,
  shift_no    smallint NOT NULL,
  shift_id    uuid     NOT NULL,
  version     bigint   NOT NULL,
  CONSTRAINT portal_shift_pk PRIMARY KEY (company_id, shift_no),
  CONSTRAINT portal_shift_shift_fk FOREIGN KEY (company_id, shift_id) REFERENCES mfg.shift (company_id, shift_id),
  CONSTRAINT portal_shift_no CHECK (shift_no BETWEEN 1 AND 9),
  CONSTRAINT portal_shift_version CHECK (version >= 1)
);

CREATE TABLE mfg.portal_material (
  company_id     uuid   NOT NULL,
  batch_plant    text   NOT NULL,
  material_code  text   NOT NULL,
  item_id        uuid   NOT NULL,
  uom            text   NOT NULL,
  location_id    uuid   NOT NULL,
  version        bigint NOT NULL,
  CONSTRAINT portal_material_pk PRIMARY KEY (company_id, batch_plant, material_code),
  CONSTRAINT portal_material_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT portal_material_uom_fk FOREIGN KEY (uom) REFERENCES md.uom (uom_code),
  CONSTRAINT portal_material_location_fk FOREIGN KEY (company_id, location_id) REFERENCES md.location (company_id, location_id),
  CONSTRAINT portal_material_codes CHECK (batch_plant ~ '^[a-z0-9_-]{1,40}$' AND material_code ~ '^[A-Z0-9_.-]{1,40}$'),
  CONSTRAINT portal_material_version CHECK (version >= 1)
);

GRANT SELECT, INSERT ON mfg.portal_machine, mfg.portal_mould, mfg.portal_shift, mfg.portal_material TO rochell_app;
GRANT UPDATE (machine_id, batch_plant, version) ON mfg.portal_machine TO rochell_app;
GRANT UPDATE (item_id, version) ON mfg.portal_mould TO rochell_app;
GRANT UPDATE (shift_id, version) ON mfg.portal_shift TO rochell_app;
GRANT UPDATE (item_id, uom, location_id, version) ON mfg.portal_material TO rochell_app;
GRANT DELETE ON mfg.portal_machine, mfg.portal_mould, mfg.portal_shift, mfg.portal_material TO rochell_app;

-- A machine's shift as the portal reported it; a new row only when something changed (E-MFG2-01-6).
CREATE TABLE mfg.portal_reading (
  reading_id          uuid          NOT NULL,
  company_id          uuid          NOT NULL,
  portal_code         text          NOT NULL,
  shift_date          date          NOT NULL,
  shift_no            smallint      NOT NULL,
  window_from         timestamp     NOT NULL,
  window_to           timestamp     NOT NULL,
  closed              boolean       NOT NULL,
  cycles              integer       NOT NULL,
  cycles_without_mould integer      NOT NULL,
  maintenance_cycles  integer       NOT NULL,
  dead_minutes        integer       NOT NULL,
  first_cycle         timestamp,
  last_cycle          timestamp,
  moulds              jsonb         NOT NULL,
  content_sha256      bytea         NOT NULL,
  fetched_at          timestamptz   NOT NULL,
  CONSTRAINT portal_reading_pk PRIMARY KEY (reading_id),
  CONSTRAINT portal_reading_company_uq UNIQUE (company_id, reading_id),
  CONSTRAINT portal_reading_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT portal_reading_counts CHECK (cycles >= 0 AND cycles_without_mould >= 0 AND maintenance_cycles >= 0 AND dead_minutes >= 0 AND shift_no BETWEEN 1 AND 9
    AND window_to > window_from AND length(content_sha256) = 32 AND jsonb_typeof(moulds) = 'array')
);
CREATE INDEX portal_reading_shift ON mfg.portal_reading (company_id, portal_code, shift_date, shift_no, fetched_at DESC);
CREATE TRIGGER portal_reading_append_only BEFORE UPDATE OR DELETE ON mfg.portal_reading FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

-- A batch plant's post for a shift, as the portal kept it (its id); the highest portal id of a shift counts (E-MFG2-01-5/6).
CREATE TABLE mfg.portal_consumption (
  consumption_id  uuid        NOT NULL,
  company_id      uuid        NOT NULL,
  portal_id       bigint      NOT NULL,
  batch_plant     text        NOT NULL,
  shift_date      date        NOT NULL,
  shift_no        smallint    NOT NULL,
  batches         integer,
  materials       jsonb       NOT NULL,
  received_at     timestamp   NOT NULL,
  fetched_at      timestamptz NOT NULL,
  CONSTRAINT portal_consumption_pk PRIMARY KEY (consumption_id),
  CONSTRAINT portal_consumption_company_uq UNIQUE (company_id, consumption_id),
  CONSTRAINT portal_consumption_portal_uq UNIQUE (company_id, portal_id),
  CONSTRAINT portal_consumption_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT portal_consumption_values CHECK (portal_id > 0 AND (batches IS NULL OR batches >= 0) AND shift_no BETWEEN 1 AND 9 AND jsonb_typeof(materials) = 'array')
);
CREATE INDEX portal_consumption_shift ON mfg.portal_consumption (company_id, batch_plant, shift_date, shift_no, portal_id DESC);
CREATE TRIGGER portal_consumption_append_only BEFORE UPDATE OR DELETE ON mfg.portal_consumption FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

GRANT SELECT, INSERT ON mfg.portal_reading, mfg.portal_consumption TO rochell_app;

-- Where a shift summary came from and the state of its consumption.
ALTER TABLE mfg.shift_summary
  ADD COLUMN source               text NOT NULL DEFAULT 'MANUAL',
  ADD COLUMN portal_reading_id    uuid,
  ADD COLUMN edited_by            uuid,
  ADD COLUMN consumption_source   text NOT NULL DEFAULT 'MANUAL',
  ADD COLUMN consumption_reason   text,
  ADD COLUMN portal_consumption_id uuid,
  ADD CONSTRAINT shift_summary_source CHECK (source IN ('MANUAL', 'PORTAL') AND (source = 'PORTAL') = (portal_reading_id IS NOT NULL)),
  ADD CONSTRAINT shift_summary_reading_fk FOREIGN KEY (company_id, portal_reading_id) REFERENCES mfg.portal_reading (company_id, reading_id),
  ADD CONSTRAINT shift_summary_edited_fk FOREIGN KEY (edited_by) REFERENCES iam.user (user_id),
  ADD CONSTRAINT shift_summary_consumption_source CHECK (consumption_source IN ('MANUAL', 'BATCH_PLANT', 'PENDING')
    AND (consumption_source = 'BATCH_PLANT') = (portal_consumption_id IS NOT NULL)
    AND (consumption_reason IS NULL OR length(btrim(consumption_reason)) BETWEEN 10 AND 300)
    AND (status = 'DRAFT' OR consumption_source <> 'PENDING')),
  ADD CONSTRAINT shift_summary_consumption_fk FOREIGN KEY (company_id, portal_consumption_id) REFERENCES mfg.portal_consumption (company_id, consumption_id);
GRANT UPDATE (portal_reading_id, edited_by, consumption_source, consumption_reason, portal_consumption_id) ON mfg.shift_summary TO rochell_app;

-- E-MFG2-01-7: the plant manager pairs the portal; E-MFG2-6: the daily process prepares drafts (never posts).
INSERT INTO iam.permission (permission_code, access) VALUES ('portal:manage', 'WRITE');
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES ('GERENTE_PLANTA', 'portal:manage'), ('PROCESO_DIARIO', 'shift_summary:record'), ('PROCESO_DIARIO', 'production_run:manage')) AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;
