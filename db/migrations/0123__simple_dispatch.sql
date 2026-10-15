-- DSP-01 · Simple dispatch (E-DSP-1…5).
--   - E-DSP-1: a plant says whether it has a truck scale (`md.plant.has_truck_scale`, false unless set). Without one the gate-out
--     records no weighing; with one the weighing is required as before (E-VS3-04-7). The weighing is all four values or none.
--   - E-DSP-5: a lot chosen for a delivery line may carry the base quantity to take from it (`log.delivery_line_scan.base_quantity`);
--     without it the lot is taken up to its stock, as a scanned rack.

ALTER TABLE md.plant ADD COLUMN has_truck_scale boolean NOT NULL DEFAULT false;
GRANT UPDATE (has_truck_scale) ON md.plant TO rochell_app;

ALTER TABLE log.delivery DROP CONSTRAINT delivery_weighing,
  ADD CONSTRAINT delivery_weighing CHECK (
    (gross_kg IS NULL) = (tare_kg IS NULL) AND (gross_kg IS NULL) = (weigh_ticket_ref IS NULL) AND (gross_kg IS NULL) = (weigh_ticket_sha256 IS NULL)
    AND (gate_out_at IS NOT NULL OR gross_kg IS NULL)
    AND (gross_kg IS NULL OR coalesce(gross_kg > tare_kg AND tare_kg >= 0 AND length(btrim(weigh_ticket_ref)) > 0 AND octet_length(weigh_ticket_sha256) = 32, false)));

ALTER TABLE log.delivery_line_scan ADD COLUMN base_quantity numeric(18,6),
  ADD CONSTRAINT delivery_line_scan_quantity CHECK (base_quantity IS NULL OR base_quantity > 0);
