-- UX1-01a · People and plants by name (approved errata E-UX1-01-3, E-UX1-01-4).

-- E-UX1-01-3: the name Google gives at sign-in ("name" claim, profile scope), refreshed on every sign-in; screens show it where
-- they showed the e-mail, which stays the identity.
ALTER TABLE iam.user
  ADD COLUMN display_name text,
  ADD CONSTRAINT user_display_name CHECK (display_name IS NULL OR length(btrim(display_name)) BETWEEN 1 AND 200);
GRANT UPDATE (display_name) ON iam.user TO rochell_app;

-- E-UX1-01-4: a plant's readable name beside its code (set with rochell-migrate create-plant / set-plant-name).
ALTER TABLE md.plant
  ADD COLUMN name text,
  ADD CONSTRAINT plant_name CHECK (name IS NULL OR length(btrim(name)) BETWEEN 1 AND 100);

-- md.plant was append-only (0005 plant_immutable); now only its name may change, and only the deployment role can (no grant).
DROP TRIGGER plant_immutable ON md.plant;
CREATE FUNCTION md.plant_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'md.plant rows cannot be deleted';
  END IF;
  IF ROW(NEW.plant_id, NEW.company_id, NEW.code, NEW.valuation_area_id) IS DISTINCT FROM ROW(OLD.plant_id, OLD.company_id, OLD.code, OLD.valuation_area_id) THEN
    RAISE EXCEPTION 'md.plant: only the name changes (E-UX1-01-4)';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER plant_guard BEFORE UPDATE OR DELETE ON md.plant FOR EACH ROW EXECUTE FUNCTION md.plant_guard();
