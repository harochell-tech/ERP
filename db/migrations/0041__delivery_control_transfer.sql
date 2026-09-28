-- VS3-04 · Deliveries (conduce), weighing, gate out, POD, control transfer and revenue (P-15, P-15R, P-16, P-30).
-- Frozen Baseline VS#3 §2–§6, v2.1 §2 (ADR-031), §14 C-08…C-10, §18 (Delivery); approved errata E-VS3-04-1…15.

-- ---------------------------------------------------------------------------------------------
-- E-VS3-04-2: movement type TRANSFER (a pair out / in between locations, signed). Text comparisons as in 0011/0016/0039.
-- ---------------------------------------------------------------------------------------------
ALTER TYPE inv.movement_type ADD VALUE 'TRANSFER';

ALTER TABLE inv.inv_quantity_entry DROP CONSTRAINT inv_quantity_entry_sign;
ALTER TABLE inv.inv_quantity_entry ADD CONSTRAINT inv_quantity_entry_sign CHECK (
  (movement_type::text IN ('RECEIPT', 'OPENING') AND quantity > 0) OR
  (movement_type::text IN ('ISSUE', 'RECEIPT_REVERSAL') AND quantity < 0) OR
  movement_type::text IN ('RECEIPT_CORRECTION', 'TRANSFER'));
ALTER TABLE inv.inv_value_entry DROP CONSTRAINT inv_value_entry_sign;
ALTER TABLE inv.inv_value_entry ADD CONSTRAINT inv_value_entry_sign CHECK (
  (movement_type::text IN ('RECEIPT', 'OPENING') AND amount > 0) OR
  (movement_type::text IN ('ISSUE', 'RECEIPT_REVERSAL') AND amount < 0) OR
  movement_type::text IN ('RECEIPT_CORRECTION', 'VALUATION_ADJUSTMENT', 'VALUATION_REALLOCATION', 'PRICE_ADJUSTMENT', 'REPOST', 'RESIDUAL_ADJUSTMENT', 'TRANSFER'));

-- ---------------------------------------------------------------------------------------------
-- E-VS3-04-1: one system location TRANSITO per plant, created by the first dispatch.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE md.location ADD COLUMN is_transit boolean NOT NULL DEFAULT false;
ALTER TABLE md.location ADD CONSTRAINT location_transit_code CHECK (NOT is_transit OR code = 'TRANSITO');
CREATE UNIQUE INDEX location_one_transit_per_plant ON md.location (plant_id) WHERE is_transit;
GRANT INSERT (location_id, company_id, plant_id, code, is_transit) ON md.location TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- E-VS3-04-10: UNBILLED_RECEIVABLE (control, subledger AR) and the REVENUE_ACCOUNTING policy (E-9).
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.account_role (role_code, is_control, description) VALUES
  ('UNBILLED_RECEIVABLE', true, 'Cuenta por cobrar no facturada: entregado no facturado (subledger AR)');

ALTER TABLE fin.gl_entry
  DROP CONSTRAINT gl_entry_role_subledger,
  ADD CONSTRAINT gl_entry_role_subledger CHECK (
    (account_role NOT IN ('RAW_MATERIAL', 'FINISHED_GOODS', 'FINISHED_GOODS_IN_TRANSIT') OR subledger_type = 'INV')
    AND (account_role <> 'AP_CONTROL' OR subledger_type = 'AP')
    AND (account_role NOT IN ('AR_CONTROL', 'CONTRACT_ASSET', 'UNBILLED_RECEIVABLE', 'UNAPPLIED_RECEIPTS', 'CASH_IN_TRANSIT') OR subledger_type = 'AR')
    AND ((account_role = 'BANK') = (subledger_type IS NOT DISTINCT FROM 'BANK')));

INSERT INTO acc.accounting_policy (policy_code, owner_role, description) VALUES
  ('REVENUE_ACCOUNTING', 'CONTROLLER', 'Presentación de ingresos: entregado no facturado (E-9)');
INSERT INTO acc.policy_parameter_definition (param_code, policy_code, value_type, min_value, max_value, allowed_values, description) VALUES
  ('unbilled_delivery_presentation', 'REVENUE_ACCOUNTING', 'ENUM', NULL, NULL, ARRAY['CONTRACT_ASSET', 'UNBILLED_RECEIVABLE'],
   'Rol del entregado no facturado cuando el control se transfiere sin factura (P-16)');

-- ---------------------------------------------------------------------------------------------
-- E-VS3-04-9: where each delivery term transfers control. Global reference data, changed only by errata and migration.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE log.delivery_term_policy (
  term_code             text    NOT NULL,
  version               integer NOT NULL,
  control_transfers_at  text    NOT NULL,
  description           text    NOT NULL,
  CONSTRAINT delivery_term_policy_pk PRIMARY KEY (term_code, version),
  CONSTRAINT delivery_term_policy_point CHECK (control_transfers_at IN ('GATE_OUT', 'POD'))
);
INSERT INTO log.delivery_term_policy VALUES
  ('PICKUP_AT_PLANT', 1, 'GATE_OUT', 'Retira en planta: el control pasa al cliente al salir por el portón'),
  ('DELIVERED_OWN_TRANSPORT', 1, 'POD', 'Entregado en obra con camión propio: el control pasa con la prueba de entrega');
CREATE TRIGGER delivery_term_policy_immutable BEFORE UPDATE OR DELETE ON log.delivery_term_policy FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
GRANT SELECT ON log.delivery_term_policy TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- Deliveries (E-VS3-04-5…8, 13, 14). CD-000001 per company; one sales order per delivery.
-- PLANNED → LOADING → LOADED → IN_TRANSIT (site) / DELIVERED (pickup) → DELIVERED / DELIVERED_WITH_EXCEPTIONS / RETURNED;
-- CANCELLED before the gate.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE log.delivery (
  delivery_id            uuid          NOT NULL,
  company_id             uuid          NOT NULL,
  delivery_no            text          NOT NULL,
  sales_order_id         uuid          NOT NULL,
  plant_id               uuid          NOT NULL,
  delivery_term_code     text          NOT NULL,
  term_version           integer       NOT NULL,
  control_transfers_at   text          NOT NULL,
  vehicle_id             uuid,
  driver_id              uuid,
  customer_vehicle_plate text,
  customer_driver_name   text,
  gross_kg               numeric(18,6),
  tare_kg                numeric(18,6),
  weigh_ticket_ref       text,
  weigh_ticket_sha256    bytea,
  gate_out_at            timestamptz,
  status                 text          NOT NULL,
  exception_reason       text,
  cancel_reason          text,
  created_by             uuid          NOT NULL,
  version                bigint        NOT NULL,
  CONSTRAINT delivery_pk PRIMARY KEY (delivery_id),
  CONSTRAINT delivery_company_uq UNIQUE (company_id, delivery_id),
  CONSTRAINT delivery_no_uq UNIQUE (company_id, delivery_no),
  CONSTRAINT delivery_order_fk FOREIGN KEY (company_id, sales_order_id) REFERENCES sal.sales_order (company_id, sales_order_id),
  CONSTRAINT delivery_plant_fk FOREIGN KEY (company_id, plant_id) REFERENCES md.plant (company_id, plant_id),
  CONSTRAINT delivery_term_fk FOREIGN KEY (delivery_term_code, term_version) REFERENCES log.delivery_term_policy (term_code, version),
  CONSTRAINT delivery_vehicle_fk FOREIGN KEY (company_id, vehicle_id) REFERENCES log.vehicle (company_id, vehicle_id),
  CONSTRAINT delivery_driver_fk FOREIGN KEY (company_id, driver_id) REFERENCES log.driver (company_id, driver_id),
  CONSTRAINT delivery_created_by_fk FOREIGN KEY (created_by) REFERENCES iam.user (user_id),
  CONSTRAINT delivery_no_format CHECK (delivery_no ~ '^CD-[0-9]{6,}$'),
  CONSTRAINT delivery_status CHECK (status IN ('PLANNED', 'LOADING', 'LOADED', 'IN_TRANSIT', 'DELIVERED', 'DELIVERED_WITH_EXCEPTIONS', 'RETURNED', 'CANCELLED')),
  -- E-VS3-04-6: own vehicle and driver for site delivery, the customer's plate and driver for a pickup, from LOADING on.
  CONSTRAINT delivery_transport CHECK (status IN ('PLANNED', 'CANCELLED')
    OR (delivery_term_code = 'DELIVERED_OWN_TRANSPORT' AND vehicle_id IS NOT NULL AND driver_id IS NOT NULL)
    OR (delivery_term_code = 'PICKUP_AT_PLANT' AND coalesce(length(btrim(customer_vehicle_plate)) > 0 AND length(btrim(customer_driver_name)) > 0, false))),
  -- E-VS3-04-7: the weighing and the gate out together.
  CONSTRAINT delivery_weighing CHECK ((gate_out_at IS NULL) = (gross_kg IS NULL AND tare_kg IS NULL AND weigh_ticket_ref IS NULL AND weigh_ticket_sha256 IS NULL)
    AND (gate_out_at IS NULL OR coalesce(gross_kg > tare_kg AND tare_kg >= 0 AND length(btrim(weigh_ticket_ref)) > 0 AND octet_length(weigh_ticket_sha256) = 32, false))),
  CONSTRAINT delivery_gate_out CHECK ((gate_out_at IS NOT NULL) = (status IN ('IN_TRANSIT', 'DELIVERED', 'DELIVERED_WITH_EXCEPTIONS', 'RETURNED'))),
  CONSTRAINT delivery_exception CHECK ((status IN ('DELIVERED_WITH_EXCEPTIONS', 'RETURNED')) = coalesce(length(btrim(exception_reason)) > 0, false)),
  CONSTRAINT delivery_cancelled CHECK ((status = 'CANCELLED') = coalesce(length(btrim(cancel_reason)) > 0, false)),
  CONSTRAINT delivery_version_positive CHECK (version >= 1)
);

CREATE TABLE log.delivery_line (
  delivery_line_id     uuid          NOT NULL,
  company_id           uuid          NOT NULL,
  delivery_id          uuid          NOT NULL,
  line_no              integer       NOT NULL,
  sales_order_line_id  uuid          NOT NULL,
  item_id              uuid          NOT NULL,
  uom                  text          NOT NULL,
  qty_planned          numeric(18,6) NOT NULL,
  base_factor          numeric(18,8),
  source_location_id   uuid,
  qty_issued           numeric(18,6) NOT NULL DEFAULT 0,
  qty_delivered        numeric(18,6) NOT NULL DEFAULT 0,
  qty_returned         numeric(18,6) NOT NULL DEFAULT 0,
  qty_lost             numeric(18,6) NOT NULL DEFAULT 0,
  qty_invoiced         numeric(18,6) NOT NULL DEFAULT 0,
  CONSTRAINT delivery_line_pk PRIMARY KEY (delivery_line_id),
  CONSTRAINT delivery_line_company_uq UNIQUE (company_id, delivery_line_id),
  CONSTRAINT delivery_line_no_uq UNIQUE (delivery_id, line_no),
  CONSTRAINT delivery_line_order_line_uq UNIQUE (delivery_id, sales_order_line_id),
  CONSTRAINT delivery_line_delivery_fk FOREIGN KEY (company_id, delivery_id) REFERENCES log.delivery (company_id, delivery_id),
  CONSTRAINT delivery_line_order_line_fk FOREIGN KEY (company_id, sales_order_line_id) REFERENCES sal.sales_order_line (company_id, line_id),
  CONSTRAINT delivery_line_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT delivery_line_location_fk FOREIGN KEY (company_id, source_location_id) REFERENCES md.location (company_id, location_id),
  CONSTRAINT delivery_line_planned CHECK (qty_planned > 0),
  CONSTRAINT delivery_line_factor CHECK (base_factor IS NULL OR base_factor > 0),
  CONSTRAINT delivery_line_issued CHECK (qty_issued IN (0, qty_planned)),
  CONSTRAINT delivery_line_outcome CHECK (qty_delivered >= 0 AND qty_returned >= 0 AND qty_lost >= 0 AND qty_delivered + qty_returned + qty_lost <= qty_issued),
  CONSTRAINT delivery_line_invoiced CHECK (qty_invoiced >= 0 AND qty_invoiced <= qty_delivered)
);

-- The lots of a line: which lot left which location, in base units (E-VS3-04-5).
CREATE TABLE log.delivery_line_lot (
  delivery_line_id    uuid          NOT NULL,
  company_id          uuid          NOT NULL,
  lot_id              uuid          NOT NULL,
  source_location_id  uuid          NOT NULL,
  base_quantity       numeric(18,6) NOT NULL,
  CONSTRAINT delivery_line_lot_pk PRIMARY KEY (delivery_line_id, lot_id),
  CONSTRAINT delivery_line_lot_line_fk FOREIGN KEY (company_id, delivery_line_id) REFERENCES log.delivery_line (company_id, delivery_line_id),
  CONSTRAINT delivery_line_lot_lot_fk FOREIGN KEY (company_id, lot_id) REFERENCES inv.lot (company_id, lot_id),
  CONSTRAINT delivery_line_lot_location_fk FOREIGN KEY (company_id, source_location_id) REFERENCES md.location (company_id, location_id),
  CONSTRAINT delivery_line_lot_quantity CHECK (base_quantity > 0)
);

-- E-VS3-04-8: one POD per delivery (E-VS3-6 evidence).
CREATE TABLE log.pod (
  pod_id            uuid        NOT NULL,
  company_id        uuid        NOT NULL,
  delivery_id       uuid        NOT NULL,
  received_by_name  text        NOT NULL,
  received_at       timestamptz NOT NULL,
  evidence_ref      text        NOT NULL,
  evidence_sha256   bytea       NOT NULL,
  recorded_event_id uuid        NOT NULL,
  CONSTRAINT pod_pk PRIMARY KEY (pod_id),
  CONSTRAINT pod_delivery_uq UNIQUE (delivery_id),
  CONSTRAINT pod_delivery_fk FOREIGN KEY (company_id, delivery_id) REFERENCES log.delivery (company_id, delivery_id),
  CONSTRAINT pod_event_fk FOREIGN KEY (company_id, recorded_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT pod_received_by CHECK (length(btrim(received_by_name)) BETWEEN 1 AND 200),
  CONSTRAINT pod_evidence CHECK (length(btrim(evidence_ref)) BETWEEN 1 AND 200 AND octet_length(evidence_sha256) = 32)
);

-- v2.1 §2 / C-10: the Policy Engine's persisted decision, one per triggering event.
CREATE TABLE inv.control_assessment (
  assessment_id         uuid        NOT NULL,
  company_id            uuid        NOT NULL,
  delivery_id           uuid        NOT NULL,
  trigger_point         text        NOT NULL,
  trigger_event_id      uuid        NOT NULL,
  term_code             text        NOT NULL,
  term_version          integer     NOT NULL,
  control_transfers_at  text        NOT NULL,
  policy_version_id     uuid,
  result                text        NOT NULL,
  inputs                jsonb       NOT NULL,
  assessed_at           timestamptz NOT NULL,
  CONSTRAINT control_assessment_pk PRIMARY KEY (assessment_id),
  CONSTRAINT control_assessment_trigger_uq UNIQUE (trigger_event_id),
  CONSTRAINT control_assessment_delivery_fk FOREIGN KEY (company_id, delivery_id) REFERENCES log.delivery (company_id, delivery_id),
  CONSTRAINT control_assessment_event_fk FOREIGN KEY (company_id, trigger_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT control_assessment_term_fk FOREIGN KEY (term_code, term_version) REFERENCES log.delivery_term_policy (term_code, version),
  CONSTRAINT control_assessment_trigger CHECK (trigger_point IN ('GATE_OUT', 'POD')),
  CONSTRAINT control_assessment_result CHECK (result IN ('TRANSFERRED', 'RETAINED')),
  CONSTRAINT control_assessment_policy CHECK ((result = 'TRANSFERRED') = (policy_version_id IS NOT NULL))
);
CREATE TRIGGER control_assessment_append_only BEFORE UPDATE OR DELETE ON inv.control_assessment FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER pod_append_only BEFORE UPDATE OR DELETE ON log.pod FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER delivery_line_lot_append_only BEFORE UPDATE OR DELETE ON log.delivery_line_lot FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

CREATE FUNCTION log.delivery_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'PLANNED' OR NEW.version <> 1 THEN
      RAISE EXCEPTION 'log.delivery: a delivery is planned with version 1';
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'log.delivery rows cannot be deleted; cancel the delivery';
  END IF;
  IF ROW(NEW.delivery_id, NEW.company_id, NEW.delivery_no, NEW.sales_order_id, NEW.plant_id, NEW.delivery_term_code, NEW.term_version, NEW.control_transfers_at, NEW.created_by)
     IS DISTINCT FROM ROW(OLD.delivery_id, OLD.company_id, OLD.delivery_no, OLD.sales_order_id, OLD.plant_id, OLD.delivery_term_code, OLD.term_version, OLD.control_transfers_at, OLD.created_by)
     OR NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'log.delivery: identity columns are immutable and the version increases by 1';
  END IF;
  IF OLD.gate_out_at IS NOT NULL AND ROW(NEW.gross_kg, NEW.tare_kg, NEW.weigh_ticket_ref, NEW.weigh_ticket_sha256, NEW.gate_out_at, NEW.vehicle_id, NEW.driver_id)
     IS DISTINCT FROM ROW(OLD.gross_kg, OLD.tare_kg, OLD.weigh_ticket_ref, OLD.weigh_ticket_sha256, OLD.gate_out_at, OLD.vehicle_id, OLD.driver_id) THEN
    RAISE EXCEPTION 'log.delivery: the weighing, gate out and transport do not change after the gate';
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
       (OLD.status = 'PLANNED' AND NEW.status IN ('LOADING', 'CANCELLED'))
    OR (OLD.status = 'LOADING' AND NEW.status IN ('LOADED', 'CANCELLED'))
    OR (OLD.status = 'LOADED' AND NEW.status IN ('IN_TRANSIT', 'DELIVERED', 'CANCELLED'))
    OR (OLD.status = 'IN_TRANSIT' AND NEW.status IN ('DELIVERED', 'DELIVERED_WITH_EXCEPTIONS', 'RETURNED'))) THEN
    RAISE EXCEPTION 'log.delivery: % → % is not allowed', OLD.status, NEW.status;
  END IF;
  IF NEW.status = 'IN_TRANSIT' AND NEW.control_transfers_at <> 'POD' OR NEW.status = 'DELIVERED' AND OLD.status = 'LOADED' AND NEW.control_transfers_at <> 'GATE_OUT' THEN
    RAISE EXCEPTION 'log.delivery: the delivery term decides whether the gate out ends the delivery (E-VS3-04-9)';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER delivery_guard BEFORE INSERT OR UPDATE OR DELETE ON log.delivery FOR EACH ROW EXECUTE FUNCTION log.delivery_guard();
CREATE TRIGGER delivery_no_truncate BEFORE TRUNCATE ON log.delivery FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
CREATE CONSTRAINT TRIGGER delivery_evidence_on_insert AFTER INSERT ON log.delivery
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('Delivery', 'delivery_id');
CREATE CONSTRAINT TRIGGER delivery_evidence_on_change AFTER UPDATE ON log.delivery
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('Delivery', 'delivery_id');

-- Lines: planned with the delivery; the source location and factor are set while loading, the quantities move forward only.
CREATE FUNCTION log.delivery_line_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NOT EXISTS (SELECT 1 FROM log.delivery d WHERE d.delivery_id = NEW.delivery_id AND d.status = 'PLANNED') THEN
      RAISE EXCEPTION 'log.delivery_line: lines are planned with the delivery';
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'log.delivery_line rows cannot be deleted';
  END IF;
  IF ROW(NEW.delivery_line_id, NEW.company_id, NEW.delivery_id, NEW.line_no, NEW.sales_order_line_id, NEW.item_id, NEW.uom, NEW.qty_planned)
     IS DISTINCT FROM ROW(OLD.delivery_line_id, OLD.company_id, OLD.delivery_id, OLD.line_no, OLD.sales_order_line_id, OLD.item_id, OLD.uom, OLD.qty_planned)
     OR NEW.qty_issued < OLD.qty_issued OR NEW.qty_delivered < OLD.qty_delivered OR NEW.qty_returned < OLD.qty_returned
     OR NEW.qty_lost < OLD.qty_lost OR NEW.qty_invoiced < OLD.qty_invoiced
     OR (OLD.qty_issued > 0 AND ROW(NEW.source_location_id, NEW.base_factor) IS DISTINCT FROM ROW(OLD.source_location_id, OLD.base_factor)) THEN
    RAISE EXCEPTION 'log.delivery_line: the plan is immutable and quantities only move forward';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER delivery_line_guard BEFORE INSERT OR UPDATE OR DELETE ON log.delivery_line FOR EACH ROW EXECUTE FUNCTION log.delivery_line_guard();
CREATE TRIGGER delivery_line_no_truncate BEFORE TRUNCATE ON log.delivery_line FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- E-VS3-04-13: the order follows its deliveries; CLOSED ends a partially delivered order (close short).
-- ---------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION sal.sales_order_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'DRAFT' OR NEW.version <> 1 OR NEW.lines_version <> 1 THEN
      RAISE EXCEPTION 'sal.sales_order: an order is created DRAFT with version 1';
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'sal.sales_order rows cannot be deleted; cancel the order';
  END IF;
  IF ROW(NEW.sales_order_id, NEW.company_id, NEW.order_no, NEW.party_id, NEW.created_by)
     IS DISTINCT FROM ROW(OLD.sales_order_id, OLD.company_id, OLD.order_no, OLD.party_id, OLD.created_by)
     OR NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'sal.sales_order: identity columns are immutable and the version increases by 1';
  END IF;
  IF OLD.status <> 'DRAFT' AND ROW(NEW.plant_id, NEW.order_date, NEW.delivery_term_code, NEW.site_address, NEW.requested_date, NEW.customer_po_ref,
                                   NEW.price_list_version_id, NEW.total_net, NEW.lines_version)
                        IS DISTINCT FROM ROW(OLD.plant_id, OLD.order_date, OLD.delivery_term_code, OLD.site_address, OLD.requested_date, OLD.customer_po_ref,
                                   OLD.price_list_version_id, OLD.total_net, OLD.lines_version) THEN
    RAISE EXCEPTION 'sal.sales_order: only a DRAFT order changes';
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
       (OLD.status = 'DRAFT' AND NEW.status IN ('PENDING_CREDIT', 'CONFIRMED', 'CANCELLED'))
    OR (OLD.status = 'PENDING_CREDIT' AND NEW.status IN ('CONFIRMED', 'DRAFT', 'CANCELLED'))
    OR (OLD.status = 'CONFIRMED' AND NEW.status = 'CANCELLED'
        AND NOT EXISTS (SELECT 1 FROM sal.sales_order_line l WHERE l.sales_order_id = OLD.sales_order_id AND l.qty_delivered > 0))
    OR (OLD.status IN ('CONFIRMED', 'PARTIALLY_DELIVERED') AND NEW.status IN ('PARTIALLY_DELIVERED', 'DELIVERED'))
    OR (OLD.status = 'PARTIALLY_DELIVERED' AND NEW.status = 'CLOSED')) THEN
    RAISE EXCEPTION 'sal.sales_order: % → % is not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;

-- A reason is present exactly on CLOSED (and on CANCELLED: 0040 compared a NULL reason as unknown, which a CHECK accepts).
ALTER TABLE sal.sales_order
  ADD COLUMN close_reason text,
  ADD CONSTRAINT sales_order_closed CHECK ((status = 'CLOSED') = coalesce(length(btrim(close_reason)) > 0, false)),
  DROP CONSTRAINT sales_order_cancelled,
  ADD CONSTRAINT sales_order_cancelled CHECK ((status = 'CANCELLED') = coalesce(length(btrim(cancel_reason)) > 0, false));
ALTER TABLE sal.sales_order_line ADD CONSTRAINT sales_order_line_not_over_delivered CHECK (qty_delivered <= qty_ordered);

-- ---------------------------------------------------------------------------------------------
-- Posting rules (VS#3 §6), DRAFT until the Controller approves them (A-01). Close component INV-MOV (E-VS3-04-12).
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type) VALUES
  ('0192f001-0000-7000-8000-000000000012', 'P-15', 'GoodsIssued'),
  ('0192f001-0000-7000-8000-000000000013', 'P-15R', 'GoodsReturnedFromTransit'),
  ('0192f001-0000-7000-8000-000000000014', 'P-16', 'ControlTransferred'),
  ('0192f001-0000-7000-8000-000000000015', 'P-30', 'TransitLossRecognized');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, also_requires_components, effective_from, status) VALUES
  ('0192f001-0000-7000-8000-000000000012', 1,
   '{"lines": [
      {"code": "P15-DR-TRANSIT", "side": "DEBIT", "account_role": "FINISHED_GOODS_IN_TRANSIT", "amount": "transfer_value", "dimensions": ["plant", "item"], "subledger": "INV"},
      {"code": "P15-CR-FG", "side": "CREDIT", "account_role": "FINISHED_GOODS", "amount": "transfer_value", "dimensions": ["plant", "item"], "subledger": "INV"}
    ]}',
   '{"P15-DR-TRANSIT": "Producto terminado que sale por el portón en camión propio (conduce {delivery_no}); sigue en el balance hasta la entrega.",
     "P15-CR-FG": "Sale del patio de la planta hacia tránsito (conduce {delivery_no})."}',
   'INV-MOV', ARRAY[]::text[], DATE '2026-01-01', 'DRAFT'),
  ('0192f001-0000-7000-8000-000000000013', 1,
   '{"lines": [
      {"code": "P15R-DR-FG", "side": "DEBIT", "account_role": "FINISHED_GOODS", "amount": "transfer_value", "dimensions": ["plant", "item"], "subledger": "INV"},
      {"code": "P15R-CR-TRANSIT", "side": "CREDIT", "account_role": "FINISHED_GOODS_IN_TRANSIT", "amount": "transfer_value", "dimensions": ["plant", "item"], "subledger": "INV"}
    ]}',
   '{"P15R-DR-FG": "Producto devuelto por el cliente regresa a la planta (conduce {delivery_no}).",
     "P15R-CR-TRANSIT": "Sale de tránsito el producto devuelto (conduce {delivery_no})."}',
   'INV-MOV', ARRAY[]::text[], DATE '2026-01-01', 'DRAFT'),
  ('0192f001-0000-7000-8000-000000000014', 1,
   '{"lines": [
      {"code": "P16-DR-COGS", "side": "DEBIT", "account_role": "COGS", "amount": "cost_value", "dimensions": ["plant", "item"]},
      {"code": "P16-CR-FG", "side": "CREDIT", "account_role": "FINISHED_GOODS", "amount": "cost_value", "dimensions": ["plant", "item"], "subledger": "INV"},
      {"code": "P16-CR-TRANSIT", "side": "CREDIT", "account_role": "FINISHED_GOODS_IN_TRANSIT", "amount": "cost_value", "dimensions": ["plant", "item"], "subledger": "INV"},
      {"code": "P16-DR-CA", "side": "DEBIT", "account_role": "CONTRACT_ASSET", "amount": "revenue", "dimensions": ["party"], "subledger": "AR"},
      {"code": "P16-DR-UR", "side": "DEBIT", "account_role": "UNBILLED_RECEIVABLE", "amount": "revenue", "dimensions": ["party"], "subledger": "AR"},
      {"code": "P16-CR-REV", "side": "CREDIT", "account_role": "REVENUE_PRODUCT", "amount": "revenue", "dimensions": ["plant", "item", "party"]}
    ]}',
   '{"P16-DR-COGS": "Costo de lo entregado en el conduce {delivery_no}: cantidad transferida al costo de valuación.",
     "P16-CR-FG": "Sale del inventario al pasar el control al cliente en el portón (conduce {delivery_no}).",
     "P16-CR-TRANSIT": "Sale de tránsito al pasar el control al cliente con la prueba de entrega (conduce {delivery_no}).",
     "P16-DR-CA": "Entregado no facturado (activo de contrato) del conduce {delivery_no}: cantidad × precio del pedido {order_no}.",
     "P16-DR-UR": "Entregado no facturado (cuenta por cobrar no facturada) del conduce {delivery_no}: cantidad × precio del pedido {order_no}.",
     "P16-CR-REV": "Ingreso por la entrega del conduce {delivery_no} (pedido {order_no}), reconocido al transferirse el control."}',
   'INV-MOV', ARRAY[]::text[], DATE '2026-01-01', 'DRAFT'),
  ('0192f001-0000-7000-8000-000000000015', 1,
   '{"lines": [
      {"code": "P30-DR-LOSS", "side": "DEBIT", "account_role": "TRANSIT_LOSS", "amount": "loss_value", "dimensions": ["plant", "item"]},
      {"code": "P30-CR-TRANSIT", "side": "CREDIT", "account_role": "FINISHED_GOODS_IN_TRANSIT", "amount": "loss_value", "dimensions": ["plant", "item"], "subledger": "INV"}
    ]}',
   '{"P30-DR-LOSS": "Pérdida en tránsito del conduce {delivery_no}: lo enviado que no se recibió ni regresó.",
     "P30-CR-TRANSIT": "Baja de tránsito por la pérdida del conduce {delivery_no}."}',
   'INV-MOV', ARRAY[]::text[], DATE '2026-01-01', 'DRAFT');

-- ---------------------------------------------------------------------------------------------
-- Permissions (E-VS3-04-15).
-- ---------------------------------------------------------------------------------------------
INSERT INTO iam.permission (permission_code, access) VALUES ('delivery:manage', 'WRITE'), ('sales_order:close', 'WRITE');
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES ('DESPACHO', 'delivery:manage'), ('CREDITO', 'sales_order:close')) AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;
INSERT INTO iam.sod_rule (permission_a, permission_b) VALUES ('sales_order:close', 'sales_order:create');

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['log.delivery', 'log.delivery_line', 'log.delivery_line_lot', 'log.pod', 'inv.control_assessment'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON log.delivery, log.delivery_line, log.delivery_line_lot, log.pod, inv.control_assessment TO rochell_app;
GRANT UPDATE (vehicle_id, driver_id, customer_vehicle_plate, customer_driver_name, gross_kg, tare_kg, weigh_ticket_ref, weigh_ticket_sha256, gate_out_at,
  status, exception_reason, cancel_reason, version) ON log.delivery TO rochell_app;
GRANT UPDATE (base_factor, source_location_id, qty_issued, qty_delivered, qty_returned, qty_lost, qty_invoiced) ON log.delivery_line TO rochell_app;
GRANT UPDATE (close_reason) ON sal.sales_order TO rochell_app;
