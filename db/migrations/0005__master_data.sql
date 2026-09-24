-- PR-04 · Master data. Frozen Baseline v2.1.1 §8.3 + Patch 1 (P-5 §4.1, P-6) + approved errata E-PR03-1, E-PR04-1…9.

CREATE TYPE md.master_status AS ENUM ('DRAFT', 'REVIEW', 'APPROVED', 'ACTIVE', 'OBSOLETE');

-- ---------------------------------------------------------------------------------------------
-- Valuation areas, plants, locations. Created by the deployment role (`rochell-migrate create-plant`,
-- `create-location`, E-PR04-2); immutable afterwards. One valuation area per plant (Errata §8.3).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE md.valuation_area (
  company_id         uuid NOT NULL,
  valuation_area_id  uuid NOT NULL,
  code               text NOT NULL,
  CONSTRAINT valuation_area_pk PRIMARY KEY (valuation_area_id),
  CONSTRAINT valuation_area_company_uq UNIQUE (company_id, valuation_area_id),
  CONSTRAINT valuation_area_code_uq UNIQUE (company_id, code),
  CONSTRAINT valuation_area_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT valuation_area_code_format CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,19}$')
);

CREATE TABLE md.plant (
  plant_id           uuid NOT NULL,
  company_id         uuid NOT NULL,
  code               text NOT NULL,
  valuation_area_id  uuid NOT NULL,
  CONSTRAINT plant_pk PRIMARY KEY (plant_id),
  CONSTRAINT plant_company_uq UNIQUE (company_id, plant_id),
  CONSTRAINT plant_code_uq UNIQUE (company_id, code),
  CONSTRAINT plant_valuation_area_uq UNIQUE (valuation_area_id),
  CONSTRAINT plant_valuation_area_fk FOREIGN KEY (company_id, valuation_area_id) REFERENCES md.valuation_area (company_id, valuation_area_id),
  CONSTRAINT plant_code_format CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,19}$')
);

CREATE TABLE md.location (
  location_id  uuid NOT NULL,
  company_id   uuid NOT NULL,
  plant_id     uuid NOT NULL,
  code         text NOT NULL,
  CONSTRAINT location_pk PRIMARY KEY (location_id),
  CONSTRAINT location_company_uq UNIQUE (company_id, location_id),
  CONSTRAINT location_code_uq UNIQUE (plant_id, code),
  CONSTRAINT location_plant_fk FOREIGN KEY (company_id, plant_id) REFERENCES md.plant (company_id, plant_id),
  CONSTRAINT location_code_format CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,29}$')
);

CREATE TRIGGER valuation_area_immutable BEFORE UPDATE OR DELETE ON md.valuation_area FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER plant_immutable BEFORE UPDATE OR DELETE ON md.plant FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER location_immutable BEFORE UPDATE OR DELETE ON md.location FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

-- E-PR03-1: plant-scoped role assignments and requests reference a real plant of the same company.
ALTER TABLE iam.role_assignment
  ADD CONSTRAINT role_assignment_plant_fk FOREIGN KEY (company_id, plant_id) REFERENCES md.plant (company_id, plant_id);
ALTER TABLE iam.role_assignment_request
  ADD CONSTRAINT role_assignment_request_plant_fk FOREIGN KEY (company_id, plant_id) REFERENCES md.plant (company_id, plant_id);

-- ---------------------------------------------------------------------------------------------
-- Parties (suppliers in VS#1). RNC: 9 digits (RNC) or 11 digits (cédula), stored without separators;
-- format only in VS#1 (E-PR04-5). The command accepts only LOCAL suppliers (E-PR04-6).
-- Lifecycle in VS#1: DRAFT → ACTIVE (E-PR04-3); only DRAFT rows can be modified (E-PR04-4).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE md.party (
  party_id          uuid             NOT NULL,
  company_id        uuid             NOT NULL,
  party_kind        text             NOT NULL,
  rnc               text,
  legal_name        text             NOT NULL,
  is_supplier       boolean          NOT NULL,
  status            md.master_status NOT NULL,
  rnc_validated_at  timestamptz,
  version           bigint           NOT NULL,
  CONSTRAINT party_pk PRIMARY KEY (party_id),
  CONSTRAINT party_company_uq UNIQUE (company_id, party_id),
  CONSTRAINT party_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT party_kind_value CHECK (party_kind IN ('LOCAL', 'FOREIGN')),
  CONSTRAINT party_rnc_required CHECK (rnc IS NOT NULL OR party_kind = 'FOREIGN'),
  CONSTRAINT party_rnc_format CHECK (rnc IS NULL OR rnc ~ '^([0-9]{9}|[0-9]{11})$'),
  CONSTRAINT party_legal_name_present CHECK (length(btrim(legal_name)) > 0),
  CONSTRAINT party_version_positive CHECK (version >= 1)
);
CREATE UNIQUE INDEX party_rnc_uq ON md.party (company_id, rnc) WHERE rnc IS NOT NULL;

-- ---------------------------------------------------------------------------------------------
-- Units of measure (global) and raw-material items.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE md.uom (
  uom_code   text NOT NULL,
  dimension  text NOT NULL,
  CONSTRAINT uom_pk PRIMARY KEY (uom_code),
  CONSTRAINT uom_dimension CHECK (dimension IN ('MASS', 'VOLUME', 'COUNT'))
);
INSERT INTO md.uom (uom_code, dimension) VALUES ('kg', 'MASS'), ('t', 'MASS'), ('m3', 'VOLUME'), ('l', 'VOLUME'), ('un', 'COUNT');
CREATE TRIGGER uom_immutable BEFORE UPDATE OR DELETE ON md.uom FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

CREATE TABLE md.item (
  item_id        uuid             NOT NULL,
  company_id     uuid             NOT NULL,
  code           text             NOT NULL,
  description    text             NOT NULL,
  item_type      text             NOT NULL,
  base_uom       text             NOT NULL,
  item_category  text             NOT NULL,
  status         md.master_status NOT NULL,
  version        bigint           NOT NULL,
  CONSTRAINT item_pk PRIMARY KEY (item_id),
  CONSTRAINT item_company_uq UNIQUE (company_id, item_id),
  CONSTRAINT item_code_uq UNIQUE (company_id, code),
  CONSTRAINT item_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT item_base_uom_fk FOREIGN KEY (base_uom) REFERENCES md.uom (uom_code),
  CONSTRAINT item_type_vs1 CHECK (item_type = 'RAW_MATERIAL'),
  CONSTRAINT item_category_value CHECK (item_category IN ('CEMENTO', 'AGREGADO', 'ADITIVO', 'OTRA_MATERIA_PRIMA')),
  CONSTRAINT item_code_format CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{1,39}$'),
  CONSTRAINT item_description_present CHECK (length(btrim(description)) > 0),
  CONSTRAINT item_version_positive CHECK (version >= 1)
);

-- Guard shared by party and item: no deletes, identity immutable, version +1 per change,
-- only DRAFT rows change, and the only status transition in VS#1 is DRAFT → ACTIVE.
CREATE FUNCTION md.master_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'UPDATE' THEN
    RAISE EXCEPTION 'md.% rows cannot be deleted', TG_TABLE_NAME;
  END IF;
  IF NEW.company_id <> OLD.company_id THEN
    RAISE EXCEPTION 'md.%: company cannot change', TG_TABLE_NAME;
  END IF;
  IF NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'md.%: version must increase by exactly 1 (expected %, got %)', TG_TABLE_NAME, OLD.version + 1, NEW.version;
  END IF;
  IF OLD.status <> 'DRAFT' THEN
    RAISE EXCEPTION 'md.%: % rows cannot be modified in VS#1', TG_TABLE_NAME, OLD.status;
  END IF;
  IF NEW.status NOT IN ('DRAFT', 'ACTIVE') THEN
    RAISE EXCEPTION 'md.%: status % is not used in VS#1', TG_TABLE_NAME, NEW.status;
  END IF;
  RETURN NEW;
END $$;

CREATE FUNCTION md.party_identity_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.party_id <> OLD.party_id OR NEW.party_kind <> OLD.party_kind THEN
    RAISE EXCEPTION 'md.party: party_id and party_kind are immutable';
  END IF;
  RETURN NEW;
END $$;

CREATE FUNCTION md.item_identity_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.item_id <> OLD.item_id OR NEW.item_type <> OLD.item_type OR NEW.base_uom <> OLD.base_uom THEN
    RAISE EXCEPTION 'md.item: item_id, item_type and base_uom are immutable';
  END IF;
  RETURN NEW;
END $$;

CREATE TRIGGER party_guard BEFORE UPDATE OR DELETE ON md.party FOR EACH ROW EXECUTE FUNCTION md.master_guard();
CREATE TRIGGER party_identity_guard BEFORE UPDATE ON md.party FOR EACH ROW EXECUTE FUNCTION md.party_identity_guard();
CREATE TRIGGER item_guard BEFORE UPDATE OR DELETE ON md.item FOR EACH ROW EXECUTE FUNCTION md.master_guard();
CREATE TRIGGER item_identity_guard BEFORE UPDATE ON md.item FOR EACH ROW EXECUTE FUNCTION md.item_identity_guard();

-- ---------------------------------------------------------------------------------------------
-- UOM conversions (E-PR04-8): always towards the item's base UOM; effective ranges never overlap (P-6);
-- a new conversion closes the open one; no retroactive start (enforced by the command, which knows the business date).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE md.uom_conversion (
  company_id      uuid          NOT NULL,
  item_id         uuid          NOT NULL,
  from_uom        text          NOT NULL,
  to_uom          text          NOT NULL,
  factor          numeric(18,8) NOT NULL,
  effective_from  date          NOT NULL,
  effective_to    date,
  CONSTRAINT uom_conversion_pk PRIMARY KEY (item_id, from_uom, to_uom, effective_from),
  CONSTRAINT uom_conversion_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT uom_conversion_from_fk FOREIGN KEY (from_uom) REFERENCES md.uom (uom_code),
  CONSTRAINT uom_conversion_to_fk FOREIGN KEY (to_uom) REFERENCES md.uom (uom_code),
  CONSTRAINT uom_conversion_factor_positive CHECK (factor > 0),
  CONSTRAINT uom_conversion_distinct_uoms CHECK (from_uom <> to_uom),
  CONSTRAINT uom_conversion_range CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT uom_conversion_no_overlap EXCLUDE USING gist (
    company_id WITH =, item_id WITH =, from_uom WITH =, to_uom WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
);

CREATE FUNCTION md.uom_conversion_to_base() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM md.item WHERE item_id = NEW.item_id AND base_uom = NEW.to_uom) THEN
    RAISE EXCEPTION 'md.uom_conversion: to_uom must be the base UOM of the item (E-PR04-8)';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER uom_conversion_to_base BEFORE INSERT ON md.uom_conversion FOR EACH ROW EXECUTE FUNCTION md.uom_conversion_to_base();

CREATE FUNCTION md.uom_conversion_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'UPDATE' THEN
    RAISE EXCEPTION 'md.uom_conversion rows cannot be deleted';
  END IF;
  IF ROW(NEW.company_id, NEW.item_id, NEW.from_uom, NEW.to_uom, NEW.factor, NEW.effective_from)
     IS DISTINCT FROM ROW(OLD.company_id, OLD.item_id, OLD.from_uom, OLD.to_uom, OLD.factor, OLD.effective_from) THEN
    RAISE EXCEPTION 'md.uom_conversion: only effective_to may change';
  END IF;
  IF OLD.effective_to IS NOT NULL OR NEW.effective_to IS NULL THEN
    RAISE EXCEPTION 'md.uom_conversion: effective_to can be set only once';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER uom_conversion_guard BEFORE UPDATE OR DELETE ON md.uom_conversion FOR EACH ROW EXECUTE FUNCTION md.uom_conversion_guard();

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['md.valuation_area', 'md.plant', 'md.location', 'md.party', 'md.item', 'md.uom_conversion'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT ON md.valuation_area, md.plant, md.location, md.uom TO rochell_app;
GRANT SELECT, INSERT ON md.party TO rochell_app;
GRANT UPDATE (rnc, legal_name, status, rnc_validated_at, version) ON md.party TO rochell_app;
GRANT SELECT, INSERT ON md.item TO rochell_app;
GRANT UPDATE (status, version) ON md.item TO rochell_app;
GRANT SELECT, INSERT ON md.uom_conversion TO rochell_app;
GRANT UPDATE (effective_to) ON md.uom_conversion TO rochell_app;
