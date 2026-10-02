-- FLT-01 · Fleet details (approved errata E-FLT-1…5): each vehicle carries its «ficha» — the code the plant knows it by, such as
-- "BR 09" or "HR 114", printed on the delivery note beside the driver — and its insurance policy number; each driver the date the
-- licence expires. An expired licence only warns (E-FLT-4): nothing here blocks a delivery. Vehicles and drivers already registered
-- stay without these data until someone completes them (E-FLT-5).
ALTER TABLE log.vehicle
  ADD COLUMN fleet_code text,
  ADD COLUMN insurance_policy_no text,
  ADD CONSTRAINT vehicle_fleet_code_format CHECK (fleet_code IS NULL OR (fleet_code ~ '^[A-Z0-9]+( [A-Z0-9]+)*$' AND length(fleet_code) BETWEEN 2 AND 12)),
  ADD CONSTRAINT vehicle_insurance_policy CHECK (insurance_policy_no IS NULL OR (length(btrim(insurance_policy_no)) BETWEEN 1 AND 40 AND insurance_policy_no = btrim(insurance_policy_no)));
CREATE UNIQUE INDEX vehicle_fleet_code_uq ON log.vehicle (company_id, fleet_code) WHERE fleet_code IS NOT NULL;
GRANT UPDATE (fleet_code, insurance_policy_no) ON log.vehicle TO rochell_app;

ALTER TABLE log.driver ADD COLUMN license_expires_on date;
GRANT UPDATE (license_expires_on) ON log.driver TO rochell_app;
