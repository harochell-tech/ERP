-- FIX · Locations coded TRANSITO / CURADO created with `rochell-migrate create-location` were left without their flag (E-PLT-2: they
-- are the plant's transit and curing locations). A site delivery's gate-out then tried to create the plant's TRANSITO and hit
-- location_code_uq (23505, «Error inesperado del sistema» on Despachar, 2026-10-15). Each such location gets its flag, unless the plant
-- already has a flagged one. The guard on md.location (E-PLT-2/3) is lifted for this statement only.

ALTER TABLE md.location DISABLE TRIGGER location_guard;

UPDATE md.location l SET is_transit = true
WHERE l.code = 'TRANSITO' AND NOT l.is_transit AND NOT l.is_curing
  AND NOT EXISTS (SELECT 1 FROM md.location o WHERE o.plant_id = l.plant_id AND o.is_transit);

UPDATE md.location l SET is_curing = true
WHERE l.code = 'CURADO' AND NOT l.is_curing AND NOT l.is_transit
  AND NOT EXISTS (SELECT 1 FROM md.location o WHERE o.plant_id = l.plant_id AND o.is_curing);

ALTER TABLE md.location ENABLE TRIGGER location_guard;
