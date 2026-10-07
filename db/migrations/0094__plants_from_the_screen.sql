-- PLT-01 · Plants and locations managed from the screen (approved errata E-PLT-1…5).
--   - a plant is created with its valuation area and four locations (RECEPCION, PATIO, CURADO, TRANSITO); more locations are added
--     to any plant (E-PLT-1/2);
--   - plants and locations are never deleted: they are deactivated (and reactivated) when nothing is left in them (E-PLT-3);
--   - a location has a readable name.

ALTER TABLE md.plant ADD COLUMN status text NOT NULL DEFAULT 'ACTIVE',
  ADD CONSTRAINT plant_status CHECK (status IN ('ACTIVE', 'INACTIVE'));

CREATE OR REPLACE FUNCTION md.plant_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'md.plant rows cannot be deleted; a plant is deactivated (E-PLT-3)';
  END IF;
  IF ROW(NEW.plant_id, NEW.company_id, NEW.code, NEW.valuation_area_id) IS DISTINCT FROM ROW(OLD.plant_id, OLD.company_id, OLD.code, OLD.valuation_area_id) THEN
    RAISE EXCEPTION 'md.plant: only the name and the status change (E-UX1-01-4, E-PLT-3)';
  END IF;
  RETURN NEW;
END $$;

ALTER TABLE md.location ADD COLUMN name text,
  ADD COLUMN status text NOT NULL DEFAULT 'ACTIVE',
  ADD CONSTRAINT location_name CHECK (name IS NULL OR length(btrim(name)) BETWEEN 1 AND 100),
  ADD CONSTRAINT location_status CHECK (status IN ('ACTIVE', 'INACTIVE'));

-- md.location was append-only (0005 location_immutable): now its name and status change; it is never deleted.
DROP TRIGGER location_immutable ON md.location;
CREATE FUNCTION md.location_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'md.location rows cannot be deleted; a location is deactivated (E-PLT-3)';
  END IF;
  IF ROW(NEW.location_id, NEW.company_id, NEW.plant_id, NEW.code, NEW.is_transit, NEW.is_curing)
     IS DISTINCT FROM ROW(OLD.location_id, OLD.company_id, OLD.plant_id, OLD.code, OLD.is_transit, OLD.is_curing) THEN
    RAISE EXCEPTION 'md.location: only the name and the status change (E-PLT-2/3)';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER location_guard BEFORE UPDATE OR DELETE ON md.location FOR EACH ROW EXECUTE FUNCTION md.location_guard();

GRANT INSERT (company_id, valuation_area_id, code) ON md.valuation_area TO rochell_app;
GRANT INSERT (plant_id, company_id, code, valuation_area_id, name) ON md.plant TO rochell_app;
GRANT UPDATE (status) ON md.plant TO rochell_app;
GRANT INSERT (name) ON md.location TO rochell_app;
GRANT UPDATE (name, status) ON md.location TO rochell_app;
