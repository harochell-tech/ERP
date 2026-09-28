-- VS3-03 · Sales order and credit check. Frozen Baseline VS#3 §2–§5, §9 (SAL-01, SAL-02); approved errata E-VS3-03-1…10.

-- ---------------------------------------------------------------------------------------------
-- Sales orders (E-VS3-03-5…7): PV-000001 per company; DRAFT → PENDING_CREDIT / CONFIRMED; PENDING_CREDIT → CONFIRMED or back to
-- DRAFT; DRAFT, PENDING_CREDIT or CONFIRMED (without deliveries) → CANCELLED. The delivery statuses arrive with VS3-04.
-- Amounts are net of ITBIS (E-VS3-03-2).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE sal.sales_order (
  sales_order_id         uuid          NOT NULL,
  company_id             uuid          NOT NULL,
  order_no               text          NOT NULL,
  party_id               uuid          NOT NULL,
  plant_id               uuid          NOT NULL,
  order_date             date          NOT NULL,
  delivery_term_code     text          NOT NULL,
  site_address           text,
  requested_date         date,
  customer_po_ref        text,
  price_list_version_id  uuid          NOT NULL,
  status                 text          NOT NULL,
  total_net              numeric(19,4) NOT NULL,
  lines_version          integer       NOT NULL,
  created_by             uuid          NOT NULL,
  cancel_reason          text,
  version                bigint        NOT NULL,
  CONSTRAINT sales_order_pk PRIMARY KEY (sales_order_id),
  CONSTRAINT sales_order_company_uq UNIQUE (company_id, sales_order_id),
  CONSTRAINT sales_order_no_uq UNIQUE (company_id, order_no),
  CONSTRAINT sales_order_party_fk FOREIGN KEY (company_id, party_id) REFERENCES md.party (company_id, party_id),
  CONSTRAINT sales_order_plant_fk FOREIGN KEY (company_id, plant_id) REFERENCES md.plant (company_id, plant_id),
  CONSTRAINT sales_order_price_list_fk FOREIGN KEY (company_id, price_list_version_id) REFERENCES sal.price_list_version (company_id, price_list_version_id),
  CONSTRAINT sales_order_created_by_fk FOREIGN KEY (created_by) REFERENCES iam.user (user_id),
  CONSTRAINT sales_order_no_format CHECK (order_no ~ '^PV-[0-9]{6,}$'),
  CONSTRAINT sales_order_term CHECK (delivery_term_code IN ('PICKUP_AT_PLANT', 'DELIVERED_OWN_TRANSPORT')),
  CONSTRAINT sales_order_site CHECK (delivery_term_code <> 'DELIVERED_OWN_TRANSPORT' OR length(btrim(site_address)) > 0),
  CONSTRAINT sales_order_site_length CHECK (site_address IS NULL OR length(site_address) <= 300),
  CONSTRAINT sales_order_po_ref CHECK (customer_po_ref IS NULL OR length(btrim(customer_po_ref)) BETWEEN 1 AND 60),
  CONSTRAINT sales_order_status CHECK (status IN ('DRAFT', 'PENDING_CREDIT', 'CONFIRMED', 'PARTIALLY_DELIVERED', 'DELIVERED', 'CLOSED', 'CANCELLED')),
  CONSTRAINT sales_order_total CHECK (total_net > 0 AND total_net = round(total_net, 2)),
  CONSTRAINT sales_order_cancelled CHECK ((status = 'CANCELLED') = (length(btrim(cancel_reason)) > 0)),
  CONSTRAINT sales_order_version_positive CHECK (version >= 1 AND lines_version >= 1)
);

CREATE TABLE sal.sales_order_line (
  line_id          uuid          NOT NULL,
  company_id       uuid          NOT NULL,
  sales_order_id   uuid          NOT NULL,
  lines_version    integer       NOT NULL,
  line_no          integer       NOT NULL,
  item_id          uuid          NOT NULL,
  uom              text          NOT NULL,
  qty_ordered      numeric(18,6) NOT NULL,
  unit_price       numeric(19,4) NOT NULL,
  net_amount       numeric(19,4) NOT NULL,
  qty_delivered    numeric(18,6) NOT NULL DEFAULT 0,
  qty_invoiced     numeric(18,6) NOT NULL DEFAULT 0,
  CONSTRAINT sales_order_line_pk PRIMARY KEY (line_id),
  CONSTRAINT sales_order_line_company_uq UNIQUE (company_id, line_id),
  CONSTRAINT sales_order_line_no_uq UNIQUE (sales_order_id, lines_version, line_no),
  CONSTRAINT sales_order_line_item_uq UNIQUE (sales_order_id, lines_version, item_id, uom),
  CONSTRAINT sales_order_line_order_fk FOREIGN KEY (company_id, sales_order_id) REFERENCES sal.sales_order (company_id, sales_order_id),
  CONSTRAINT sales_order_line_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT sales_order_line_uom_fk FOREIGN KEY (uom) REFERENCES md.uom (uom_code),
  CONSTRAINT sales_order_line_qty CHECK (qty_ordered > 0),
  CONSTRAINT sales_order_line_price CHECK (unit_price > 0),
  CONSTRAINT sales_order_line_amount CHECK (net_amount > 0 AND net_amount = round(net_amount, 2)),
  CONSTRAINT sales_order_line_progress CHECK (qty_delivered >= 0 AND qty_invoiced >= 0 AND qty_invoiced <= qty_delivered)
);

-- E-VS3-03-4: every evaluation is kept; an AUTO_APPROVED one needs no decision, a NEEDS_APPROVAL one gets APPROVED / REJECTED once.
CREATE TABLE sal.credit_check (
  credit_check_id      uuid          NOT NULL,
  company_id           uuid          NOT NULL,
  sales_order_id       uuid          NOT NULL,
  lines_version        integer       NOT NULL,
  checked_at           timestamptz   NOT NULL,
  order_amount         numeric(19,4) NOT NULL,
  exposure_ar          numeric(19,4) NOT NULL,
  exposure_orders      numeric(19,4) NOT NULL,
  exposure_uninvoiced  numeric(19,4) NOT NULL,
  credit_limit         numeric(19,4) NOT NULL,
  credit_hold          boolean       NOT NULL,
  overdue_days         integer       NOT NULL,
  overdue_days_block   integer       NOT NULL,
  decision             text          NOT NULL,
  outcome              text,
  decided_by           uuid,
  decided_at           timestamptz,
  reason               text,
  CONSTRAINT credit_check_pk PRIMARY KEY (credit_check_id),
  CONSTRAINT credit_check_company_uq UNIQUE (company_id, credit_check_id),
  CONSTRAINT credit_check_order_fk FOREIGN KEY (company_id, sales_order_id) REFERENCES sal.sales_order (company_id, sales_order_id),
  CONSTRAINT credit_check_decided_by_fk FOREIGN KEY (decided_by) REFERENCES iam.user (user_id),
  CONSTRAINT credit_check_decision CHECK (decision IN ('AUTO_APPROVED', 'NEEDS_APPROVAL')),
  CONSTRAINT credit_check_outcome CHECK (outcome IS NULL OR (decision = 'NEEDS_APPROVAL' AND outcome IN ('APPROVED', 'REJECTED'))),
  CONSTRAINT credit_check_decided CHECK ((outcome IS NULL) = (decided_by IS NULL AND decided_at IS NULL)),
  CONSTRAINT credit_check_rejection_reason CHECK (outcome IS DISTINCT FROM 'REJECTED' OR length(btrim(reason)) > 0),
  CONSTRAINT credit_check_amounts CHECK (order_amount > 0 AND credit_limit >= 0 AND overdue_days >= 0 AND overdue_days_block >= 0)
);
CREATE UNIQUE INDEX credit_check_one_open ON sal.credit_check (sales_order_id) WHERE decision = 'NEEDS_APPROVAL' AND outcome IS NULL;

CREATE FUNCTION sal.sales_order_guard() RETURNS trigger
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
        AND NOT EXISTS (SELECT 1 FROM sal.sales_order_line l WHERE l.sales_order_id = OLD.sales_order_id AND l.qty_delivered > 0))) THEN
    RAISE EXCEPTION 'sal.sales_order: % → % is not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER sales_order_guard BEFORE INSERT OR UPDATE OR DELETE ON sal.sales_order FOR EACH ROW EXECUTE FUNCTION sal.sales_order_guard();
CREATE TRIGGER sales_order_no_truncate BEFORE TRUNCATE ON sal.sales_order FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
CREATE CONSTRAINT TRIGGER sales_order_evidence_on_insert AFTER INSERT ON sal.sales_order
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('SalesOrder', 'sales_order_id');
CREATE CONSTRAINT TRIGGER sales_order_evidence_on_change AFTER UPDATE ON sal.sales_order
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('SalesOrder', 'sales_order_id');

-- Lines: a new version of every line while the order is DRAFT; afterwards only the delivered and invoiced quantities move.
CREATE FUNCTION sal.sales_order_line_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NOT EXISTS (SELECT 1 FROM sal.sales_order o WHERE o.sales_order_id = NEW.sales_order_id AND o.status = 'DRAFT') THEN
      RAISE EXCEPTION 'sal.sales_order_line: lines are written only while the order is DRAFT';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM md.item i WHERE i.item_id = NEW.item_id AND i.item_type = 'FINISHED_GOOD') THEN
      RAISE EXCEPTION 'sal.sales_order_line: item % is not a finished good (E-VS3-2)', NEW.item_id;
    END IF;
    IF NEW.qty_delivered <> 0 OR NEW.qty_invoiced <> 0 THEN
      RAISE EXCEPTION 'sal.sales_order_line: a new line has nothing delivered or invoiced';
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'sal.sales_order_line rows cannot be deleted';
  END IF;
  IF ROW(NEW.line_id, NEW.company_id, NEW.sales_order_id, NEW.lines_version, NEW.line_no, NEW.item_id, NEW.uom, NEW.qty_ordered, NEW.unit_price, NEW.net_amount)
     IS DISTINCT FROM ROW(OLD.line_id, OLD.company_id, OLD.sales_order_id, OLD.lines_version, OLD.line_no, OLD.item_id, OLD.uom, OLD.qty_ordered, OLD.unit_price, OLD.net_amount) THEN
    RAISE EXCEPTION 'sal.sales_order_line: only delivered and invoiced quantities change';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER sales_order_line_guard BEFORE INSERT OR UPDATE OR DELETE ON sal.sales_order_line FOR EACH ROW EXECUTE FUNCTION sal.sales_order_line_guard();
CREATE TRIGGER sales_order_line_no_truncate BEFORE TRUNCATE ON sal.sales_order_line FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- A credit check is decided once; its evaluation never changes.
CREATE FUNCTION sal.credit_check_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'sal.credit_check rows cannot be deleted';
  END IF;
  IF OLD.outcome IS NOT NULL
     OR ROW(NEW.credit_check_id, NEW.company_id, NEW.sales_order_id, NEW.lines_version, NEW.checked_at, NEW.order_amount, NEW.exposure_ar, NEW.exposure_orders,
            NEW.exposure_uninvoiced, NEW.credit_limit, NEW.credit_hold, NEW.overdue_days, NEW.overdue_days_block, NEW.decision)
        IS DISTINCT FROM ROW(OLD.credit_check_id, OLD.company_id, OLD.sales_order_id, OLD.lines_version, OLD.checked_at, OLD.order_amount, OLD.exposure_ar, OLD.exposure_orders,
            OLD.exposure_uninvoiced, OLD.credit_limit, OLD.credit_hold, OLD.overdue_days, OLD.overdue_days_block, OLD.decision) THEN
    RAISE EXCEPTION 'sal.credit_check: an evaluation is decided once and never changes';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER credit_check_guard BEFORE UPDATE OR DELETE ON sal.credit_check FOR EACH ROW EXECUTE FUNCTION sal.credit_check_guard();
CREATE TRIGGER credit_check_no_truncate BEFORE TRUNCATE ON sal.credit_check FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- E-VS3-03-3: accounting policy CREDIT (A-01), approved by the Aprobador de políticas as every policy.
-- ---------------------------------------------------------------------------------------------
INSERT INTO acc.accounting_policy (policy_code, owner_role, description) VALUES
  ('CREDIT', 'CONTROLLER', 'Evaluación de crédito de clientes');

INSERT INTO acc.policy_parameter_definition (param_code, policy_code, value_type, min_value, max_value, allowed_values, description) VALUES
  ('overdue_days_block', 'CREDIT', 'INTEGER', 0, 3650, NULL, 'Días de atraso de la CxC del cliente a partir de los cuales un pedido ya no se aprueba automáticamente');

-- ---------------------------------------------------------------------------------------------
-- Permissions and SoD (E-VS3-03-8).
-- ---------------------------------------------------------------------------------------------
INSERT INTO iam.permission (permission_code, access) VALUES
  ('sales_order:create', 'WRITE'), ('sales_order:cancel', 'WRITE'), ('credit:approve', 'WRITE');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES ('VENDEDOR', 'sales_order:create'), ('VENDEDOR', 'sales_order:cancel'), ('CREDITO', 'credit:approve')) AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;

INSERT INTO iam.sod_rule (permission_a, permission_b) VALUES ('credit:approve', 'sales_order:create');

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['sal.sales_order', 'sal.sales_order_line', 'sal.credit_check'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON sal.sales_order, sal.sales_order_line, sal.credit_check TO rochell_app;
GRANT UPDATE (plant_id, order_date, delivery_term_code, site_address, requested_date, customer_po_ref, price_list_version_id, status, total_net,
  lines_version, cancel_reason, version) ON sal.sales_order TO rochell_app;
GRANT UPDATE (qty_delivered, qty_invoiced) ON sal.sales_order_line TO rochell_app;
GRANT UPDATE (outcome, decided_by, decided_at, reason) ON sal.credit_check TO rochell_app;
