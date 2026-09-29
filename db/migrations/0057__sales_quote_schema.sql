-- QUO1-01 · Sales quotations: schema, permissions and SoD (Frozen Baseline QUO-1 §2; approved errata E-QUO1-1…14,
-- E-QUO1-01-1…12). A quote posts nothing, moves no stock and issues no e-CF (E-QUO1-13).

-- ---------------------------------------------------------------------------------------------
-- The quote (E-QUO1-01-1…10). Lines are rewritten as a new lines_version while DRAFT, like the sales order.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE sal.quote (
  quote_id                uuid          NOT NULL,
  company_id              uuid          NOT NULL,
  quote_no                text          NOT NULL,
  party_id                uuid          NOT NULL,
  plant_id                uuid          NOT NULL,
  quote_date              date          NOT NULL,
  valid_until             date          NOT NULL,
  delivery_term_code      text          NOT NULL,
  site_address            text,
  customer_ref            text,
  notes                   text,
  price_list_version_id   uuid          NOT NULL,
  status                  text          NOT NULL,
  total_net               numeric(19,4) NOT NULL,
  lines_version           integer       NOT NULL,
  created_by              uuid          NOT NULL,
  copied_from_quote_id    uuid,
  price_approved_by       uuid,
  price_approved_at       timestamptz,
  approved_lines_version  integer,
  sales_order_id          uuid,
  closing_reason          text,
  version                 bigint        NOT NULL,
  CONSTRAINT quote_pk PRIMARY KEY (quote_id),
  CONSTRAINT quote_company_uq UNIQUE (company_id, quote_id),
  CONSTRAINT quote_no_uq UNIQUE (company_id, quote_no),
  CONSTRAINT quote_party_fk FOREIGN KEY (company_id, party_id) REFERENCES md.party (company_id, party_id),
  CONSTRAINT quote_plant_fk FOREIGN KEY (company_id, plant_id) REFERENCES md.plant (company_id, plant_id),
  CONSTRAINT quote_price_list_fk FOREIGN KEY (company_id, price_list_version_id) REFERENCES sal.price_list_version (company_id, price_list_version_id),
  CONSTRAINT quote_created_by_fk FOREIGN KEY (created_by) REFERENCES iam.user (user_id),
  CONSTRAINT quote_copied_from_fk FOREIGN KEY (company_id, copied_from_quote_id) REFERENCES sal.quote (company_id, quote_id),
  CONSTRAINT quote_price_approved_by_fk FOREIGN KEY (price_approved_by) REFERENCES iam.user (user_id),
  CONSTRAINT quote_order_fk FOREIGN KEY (company_id, sales_order_id) REFERENCES sal.sales_order (company_id, sales_order_id),
  CONSTRAINT quote_no_format CHECK (quote_no ~ '^COT-[0-9]{6,}$'),
  CONSTRAINT quote_validity CHECK (valid_until >= quote_date),
  CONSTRAINT quote_term CHECK (delivery_term_code IN ('PICKUP_AT_PLANT', 'DELIVERED_OWN_TRANSPORT')),
  CONSTRAINT quote_site CHECK (delivery_term_code <> 'DELIVERED_OWN_TRANSPORT' OR length(btrim(site_address)) > 0),
  CONSTRAINT quote_site_length CHECK (site_address IS NULL OR length(site_address) <= 300),
  CONSTRAINT quote_customer_ref CHECK (customer_ref IS NULL OR length(btrim(customer_ref)) BETWEEN 1 AND 60),
  CONSTRAINT quote_notes CHECK (notes IS NULL OR length(btrim(notes)) BETWEEN 1 AND 1000),
  CONSTRAINT quote_status CHECK (status IN ('DRAFT', 'PENDING_APPROVAL', 'SENT', 'CONVERTED', 'LOST', 'CANCELLED')),
  CONSTRAINT quote_total CHECK (total_net >= 0 AND total_net = round(total_net, 2)),
  CONSTRAINT quote_lines_version_positive CHECK (lines_version >= 1),
  CONSTRAINT quote_version_positive CHECK (version >= 1),
  -- E-QUO1-01-2: an approval names who, when and which lines; never the quote's own author (E-QUO1-11).
  CONSTRAINT quote_price_approval CHECK ((price_approved_by IS NULL) = (price_approved_at IS NULL) AND (price_approved_by IS NULL) = (approved_lines_version IS NULL)),
  CONSTRAINT quote_price_four_eyes CHECK (price_approved_by IS NULL OR price_approved_by <> created_by),
  -- E-QUO1-01-9 / E-QUO1-01-4: the order exactly when CONVERTED; a reason exactly when LOST or CANCELLED.
  CONSTRAINT quote_converted CHECK ((status = 'CONVERTED') = (sales_order_id IS NOT NULL)),
  CONSTRAINT quote_closing_reason CHECK ((status IN ('LOST', 'CANCELLED')) = coalesce(length(btrim(closing_reason)) BETWEEN 1 AND 500, false))
);

CREATE TABLE sal.quote_line (
  line_id        uuid          NOT NULL,
  company_id     uuid          NOT NULL,
  quote_id       uuid          NOT NULL,
  lines_version  integer       NOT NULL,
  line_no        integer       NOT NULL,
  item_id        uuid          NOT NULL,
  uom            text          NOT NULL,
  quantity       numeric(18,6) NOT NULL,
  list_price     numeric(19,4) NOT NULL,
  unit_price     numeric(19,4) NOT NULL,
  net_amount     numeric(19,4) NOT NULL,
  CONSTRAINT quote_line_pk PRIMARY KEY (line_id),
  CONSTRAINT quote_line_company_uq UNIQUE (company_id, line_id),
  CONSTRAINT quote_line_no_uq UNIQUE (quote_id, lines_version, line_no),
  CONSTRAINT quote_line_item_uq UNIQUE (quote_id, lines_version, item_id, uom),
  CONSTRAINT quote_line_quote_fk FOREIGN KEY (company_id, quote_id) REFERENCES sal.quote (company_id, quote_id),
  CONSTRAINT quote_line_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT quote_line_uom_fk FOREIGN KEY (uom) REFERENCES md.uom (uom_code),
  CONSTRAINT quote_line_no_positive CHECK (line_no >= 1),
  CONSTRAINT quote_line_qty CHECK (quantity > 0),
  CONSTRAINT quote_line_prices CHECK (list_price > 0 AND unit_price > 0),
  CONSTRAINT quote_line_amount CHECK (net_amount > 0 AND net_amount = round(net_amount, 2))
);

-- ---------------------------------------------------------------------------------------------
-- Guards (E-QUO1-01-2/3) and state history evidence (ADR-027).
-- ---------------------------------------------------------------------------------------------
CREATE FUNCTION sal.quote_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'DRAFT' OR NEW.version <> 1 OR NEW.lines_version <> 1 OR NEW.price_approved_by IS NOT NULL THEN
      RAISE EXCEPTION 'sal.quote: a quote is created DRAFT with version 1 and no price approval';
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'sal.quote rows cannot be deleted; cancel the quote';
  END IF;
  IF ROW(NEW.quote_id, NEW.company_id, NEW.quote_no, NEW.party_id, NEW.created_by, NEW.copied_from_quote_id)
     IS DISTINCT FROM ROW(OLD.quote_id, OLD.company_id, OLD.quote_no, OLD.party_id, OLD.created_by, OLD.copied_from_quote_id)
     OR NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'sal.quote: identity columns are immutable and the version increases by 1';
  END IF;
  IF OLD.status <> 'DRAFT' AND ROW(NEW.plant_id, NEW.quote_date, NEW.valid_until, NEW.delivery_term_code, NEW.site_address, NEW.customer_ref, NEW.notes,
                                   NEW.price_list_version_id, NEW.total_net, NEW.lines_version)
                        IS DISTINCT FROM ROW(OLD.plant_id, OLD.quote_date, OLD.valid_until, OLD.delivery_term_code, OLD.site_address, OLD.customer_ref, OLD.notes,
                                   OLD.price_list_version_id, OLD.total_net, OLD.lines_version) THEN
    RAISE EXCEPTION 'sal.quote: only a DRAFT quote changes (a sent quote is copied, E-QUO1-8)';
  END IF;
  IF ROW(NEW.price_approved_by, NEW.price_approved_at, NEW.approved_lines_version) IS DISTINCT FROM ROW(OLD.price_approved_by, OLD.price_approved_at, OLD.approved_lines_version)
     AND NOT (OLD.status = 'PENDING_APPROVAL' AND NEW.status = 'DRAFT' AND NEW.approved_lines_version = OLD.lines_version) THEN
    RAISE EXCEPTION 'sal.quote: prices are approved only when a pending quote returns approved, for its current lines';
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
       (OLD.status = 'DRAFT' AND NEW.status IN ('PENDING_APPROVAL', 'SENT', 'CANCELLED'))
    OR (OLD.status = 'PENDING_APPROVAL' AND NEW.status = 'DRAFT')
    OR (OLD.status = 'SENT' AND NEW.status IN ('CONVERTED', 'LOST', 'CANCELLED'))) THEN
    RAISE EXCEPTION 'sal.quote: % → % is not allowed', OLD.status, NEW.status;
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status AND NEW.status = 'SENT'
     AND NEW.approved_lines_version IS DISTINCT FROM NEW.lines_version
     AND EXISTS (SELECT 1 FROM sal.quote_line l WHERE l.quote_id = NEW.quote_id AND l.lines_version = NEW.lines_version AND l.unit_price < l.list_price) THEN
    RAISE EXCEPTION 'sal.quote: a price below the list needs approval of the current lines before sending (E-QUO1-3)';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER quote_guard BEFORE INSERT OR UPDATE OR DELETE ON sal.quote FOR EACH ROW EXECUTE FUNCTION sal.quote_guard();
CREATE TRIGGER quote_no_truncate BEFORE TRUNCATE ON sal.quote FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
CREATE CONSTRAINT TRIGGER quote_evidence_on_insert AFTER INSERT ON sal.quote
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('Quote', 'quote_id');
CREATE CONSTRAINT TRIGGER quote_evidence_on_change AFTER UPDATE ON sal.quote
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('Quote', 'quote_id');

-- Lines: written only while the quote is DRAFT, finished goods only (E-VS3-2), never changed afterwards.
CREATE FUNCTION sal.quote_line_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NOT EXISTS (SELECT 1 FROM sal.quote q WHERE q.quote_id = NEW.quote_id AND q.status = 'DRAFT') THEN
      RAISE EXCEPTION 'sal.quote_line: lines are written only while the quote is DRAFT';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM md.item i WHERE i.item_id = NEW.item_id AND i.item_type = 'FINISHED_GOOD') THEN
      RAISE EXCEPTION 'sal.quote_line: item % is not a finished good (E-VS3-2)', NEW.item_id;
    END IF;
    RETURN NEW;
  END IF;
  RAISE EXCEPTION 'sal.quote_line rows cannot be changed or deleted; write a new lines version';
END $$;
CREATE TRIGGER quote_line_guard BEFORE INSERT OR UPDATE OR DELETE ON sal.quote_line FOR EACH ROW EXECUTE FUNCTION sal.quote_line_guard();
CREATE TRIGGER quote_line_no_truncate BEFORE TRUNCATE ON sal.quote_line FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- E-QUO1-01-9: the order born from a quote (QUOTED_AS), at most one per quote; quote_id joins the order's immutable identity.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE sal.sales_order
  ADD COLUMN quote_id uuid,
  ADD CONSTRAINT sales_order_quote_fk FOREIGN KEY (company_id, quote_id) REFERENCES sal.quote (company_id, quote_id),
  ADD CONSTRAINT sales_order_quote_uq UNIQUE (quote_id);

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
  IF ROW(NEW.sales_order_id, NEW.company_id, NEW.order_no, NEW.party_id, NEW.created_by, NEW.quote_id)
     IS DISTINCT FROM ROW(OLD.sales_order_id, OLD.company_id, OLD.order_no, OLD.party_id, OLD.created_by, OLD.quote_id)
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

-- ---------------------------------------------------------------------------------------------
-- Permissions and SoD (E-QUO1-11, E-QUO1-01-11).
-- ---------------------------------------------------------------------------------------------
INSERT INTO iam.permission (permission_code, access) VALUES ('quote:manage', 'WRITE'), ('quote:approve_price', 'WRITE');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES ('VENDEDOR', 'quote:manage'), ('APROBADOR_POLITICAS', 'quote:approve_price')) AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;

INSERT INTO iam.sod_rule (permission_a, permission_b) VALUES ('quote:approve_price', 'quote:manage');

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['sal.quote', 'sal.quote_line'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON sal.quote, sal.quote_line TO rochell_app;
GRANT UPDATE (plant_id, quote_date, valid_until, delivery_term_code, site_address, customer_ref, notes, price_list_version_id, status, total_net,
  lines_version, price_approved_by, price_approved_at, approved_lines_version, sales_order_id, closing_reason, version) ON sal.quote TO rochell_app;
