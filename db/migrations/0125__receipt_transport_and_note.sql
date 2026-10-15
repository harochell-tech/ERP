-- REC-01 · A raw material receipt names our truck and driver and the supplier's delivery note (E-REC-1…3).
--   - E-REC-1: `vehicle_id` / `driver_id` from our own fleet (log.vehicle, log.driver).
--   - E-REC-2: `supplier_delivery_note` (1–60 characters), never repeated for one supplier while the receipt stands; the supplier is
--     kept on the receipt (`supplier_party_id`, the order's party) for that rule and the report.
-- Receipts posted before stay without them.

ALTER TABLE pur.goods_receipt
  ADD COLUMN vehicle_id uuid,
  ADD COLUMN driver_id uuid,
  ADD COLUMN supplier_party_id uuid,
  ADD COLUMN supplier_delivery_note text,
  ADD CONSTRAINT goods_receipt_vehicle_fk FOREIGN KEY (company_id, vehicle_id) REFERENCES log.vehicle (company_id, vehicle_id),
  ADD CONSTRAINT goods_receipt_driver_fk FOREIGN KEY (company_id, driver_id) REFERENCES log.driver (company_id, driver_id),
  ADD CONSTRAINT goods_receipt_supplier_note CHECK (supplier_delivery_note IS NULL OR length(btrim(supplier_delivery_note)) BETWEEN 1 AND 60);

CREATE UNIQUE INDEX gr_supplier_note_uq ON pur.goods_receipt (company_id, supplier_party_id, upper(supplier_delivery_note))
  WHERE supplier_delivery_note IS NOT NULL AND document_status <> 'REVERSED';
