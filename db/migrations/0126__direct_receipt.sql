-- REC-02 · Direct receipt without a purchase order (E-REC-6…8, E-REC-02-1…7).
--   - `pur.purchase_order.origin`: DIRECT_RECEIPT marks the order Core creates behind a direct receipt (E-REC-02-7).
--   - The service identity «Recepción directa» approves those orders when the price is within the policy (E-REC-02-1); it never
--     signs in and holds no role.
--   - `pur.direct_receipt`: what the receiver entered (location, arrival, the supplier's note, our truck and driver) and the price
--     check per line, kept while the order waits for approval (E-REC-02-4). `closed_at` is set when the receipt is posted or the
--     order cancelled; while it is open the supplier's note is held (unique per supplier).
--   - PURCHASING parameter `direct_receipt_price_deviation_pct` (E-REC-02-3) and permission `goods_receipt:direct` for ALMACENISTA
--     and COMPRADOR (E-REC-02-6).

ALTER TABLE pur.purchase_order
  ADD COLUMN origin text NOT NULL DEFAULT 'ORDER',
  ADD CONSTRAINT purchase_order_origin CHECK (origin IN ('ORDER', 'DIRECT_RECEIPT'));

INSERT INTO iam.user (user_id, kind, status, display_name) VALUES ('00000000-0000-7000-8000-00000000d006', 'SERVICE', 'ACTIVE', 'Recepción directa');

CREATE TABLE pur.direct_receipt (
  po_id                  uuid        NOT NULL,
  company_id             uuid        NOT NULL,
  location_id            uuid        NOT NULL,
  occurred_at            timestamptz NOT NULL,
  vehicle_id             uuid        NOT NULL,
  driver_id              uuid        NOT NULL,
  supplier_party_id      uuid        NOT NULL,
  supplier_delivery_note text        NOT NULL,
  weigh_ticket_ref       text,
  received_by            uuid        NOT NULL,
  requires_approval      boolean     NOT NULL,
  price_check            jsonb       NOT NULL,
  gr_id                  uuid,
  closed_at              timestamptz,
  version                bigint      NOT NULL,
  CONSTRAINT direct_receipt_pk PRIMARY KEY (po_id),
  CONSTRAINT direct_receipt_po_fk FOREIGN KEY (company_id, po_id) REFERENCES pur.purchase_order (company_id, po_id),
  CONSTRAINT direct_receipt_location_fk FOREIGN KEY (company_id, location_id) REFERENCES md.location (company_id, location_id),
  CONSTRAINT direct_receipt_vehicle_fk FOREIGN KEY (company_id, vehicle_id) REFERENCES log.vehicle (company_id, vehicle_id),
  CONSTRAINT direct_receipt_driver_fk FOREIGN KEY (company_id, driver_id) REFERENCES log.driver (company_id, driver_id),
  CONSTRAINT direct_receipt_supplier_fk FOREIGN KEY (company_id, supplier_party_id) REFERENCES md.party (company_id, party_id),
  CONSTRAINT direct_receipt_received_by_fk FOREIGN KEY (received_by) REFERENCES iam.user (user_id),
  CONSTRAINT direct_receipt_gr_fk FOREIGN KEY (company_id, gr_id) REFERENCES pur.goods_receipt (company_id, gr_id),
  CONSTRAINT direct_receipt_note CHECK (length(btrim(supplier_delivery_note)) BETWEEN 1 AND 60),
  CONSTRAINT direct_receipt_ticket_present CHECK (weigh_ticket_ref IS NULL OR length(btrim(weigh_ticket_ref)) > 0),
  CONSTRAINT direct_receipt_price_check_array CHECK (jsonb_typeof(price_check) = 'array'),
  CONSTRAINT direct_receipt_posted_closed CHECK (gr_id IS NULL OR closed_at IS NOT NULL),
  CONSTRAINT direct_receipt_version_positive CHECK (version >= 1)
);

CREATE UNIQUE INDEX direct_receipt_note_uq ON pur.direct_receipt (company_id, supplier_party_id, upper(supplier_delivery_note)) WHERE closed_at IS NULL;

CREATE FUNCTION pur.direct_receipt_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'UPDATE' THEN
    RAISE EXCEPTION 'pur.direct_receipt rows cannot be deleted';
  END IF;
  IF ROW(NEW.po_id, NEW.company_id, NEW.location_id, NEW.occurred_at, NEW.vehicle_id, NEW.driver_id, NEW.supplier_party_id, NEW.supplier_delivery_note,
         NEW.weigh_ticket_ref, NEW.received_by, NEW.requires_approval, NEW.price_check)
     IS DISTINCT FROM ROW(OLD.po_id, OLD.company_id, OLD.location_id, OLD.occurred_at, OLD.vehicle_id, OLD.driver_id, OLD.supplier_party_id, OLD.supplier_delivery_note,
         OLD.weigh_ticket_ref, OLD.received_by, OLD.requires_approval, OLD.price_check) THEN
    RAISE EXCEPTION 'pur.direct_receipt: what was received is immutable';
  END IF;
  IF OLD.closed_at IS NOT NULL THEN
    RAISE EXCEPTION 'pur.direct_receipt: a closed direct receipt does not change';
  END IF;
  IF NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'pur.direct_receipt: version must increase by exactly 1';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER direct_receipt_guard BEFORE UPDATE OR DELETE ON pur.direct_receipt FOR EACH ROW EXECUTE FUNCTION pur.direct_receipt_guard();
CREATE TRIGGER direct_receipt_no_truncate BEFORE TRUNCATE ON pur.direct_receipt FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

ALTER TABLE pur.direct_receipt ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON pur.direct_receipt
  USING (company_id = nullif(current_setting('app.company_id', true), '')::uuid)
  WITH CHECK (company_id = nullif(current_setting('app.company_id', true), '')::uuid);
GRANT SELECT, INSERT ON pur.direct_receipt TO rochell_app;
GRANT UPDATE (gr_id, closed_at, version) ON pur.direct_receipt TO rochell_app;

INSERT INTO iam.permission (permission_code, access) VALUES ('goods_receipt:direct', 'WRITE');
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT role_id, 'goods_receipt:direct' FROM iam.role WHERE code IN ('ALMACENISTA', 'COMPRADOR');

INSERT INTO acc.policy_parameter_definition (param_code, policy_code, value_type, min_value, max_value, allowed_values, description, label, unit, example, affects) VALUES
  ('direct_receipt_price_deviation_pct', 'PURCHASING', 'DECIMAL_PERCENT', 0, 1, NULL,
   'Desviación de precio permitida en una recepción directa frente al precio de referencia (fracción, E-REC-02-3)',
   'Desviación de precio en recepción directa', 'PERCENT', '10 %',
   'Una recepción sin orden de compra cuyo precio se aparta del último precio del proveedor (o del estándar) más que este porcentaje espera aprobación antes de entrar al inventario. Sin este valor, toda recepción directa espera aprobación.');
