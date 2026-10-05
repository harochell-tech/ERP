-- PRS-01 · Price lists per customer (PRC-1) and delivery freight (SRV-1): schema only. Baseline
-- docs/architecture/prs1/frozen-baseline-prs1.md; approved errata E-PRC1-1…11, E-SRV1-1…19, E-PRS-01-1…8.

-- ---------------------------------------------------------------------------------------------
-- Named price lists (E-PRC1-1, E-PRC1-5). GENERAL is fixed: never deactivated (E-PRS-01-4). A list goes INACTIVE only when no
-- customer terms in force or pending name it (E-PRS-01-5).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE sal.price_list (
  price_list_id  uuid    NOT NULL,
  company_id     uuid    NOT NULL,
  code           text    NOT NULL,
  name           text    NOT NULL,
  status         text    NOT NULL,
  created_by     uuid,
  version        bigint  NOT NULL,
  CONSTRAINT price_list_pk PRIMARY KEY (price_list_id),
  CONSTRAINT price_list_company_uq UNIQUE (company_id, price_list_id),
  CONSTRAINT price_list_code_uq UNIQUE (company_id, code),
  CONSTRAINT price_list_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT price_list_created_fk FOREIGN KEY (created_by) REFERENCES iam.user (user_id),
  CONSTRAINT price_list_code_format CHECK (code ~ '^[A-Z0-9][A-Z0-9_]{1,29}$'),
  CONSTRAINT price_list_name_length CHECK (length(btrim(name)) BETWEEN 1 AND 80),
  CONSTRAINT price_list_status CHECK (status IN ('ACTIVE', 'INACTIVE')),
  CONSTRAINT price_list_general_active CHECK (code <> 'GENERAL' OR status = 'ACTIVE'),
  -- Only GENERAL, created by this migration or on first use, has no author.
  CONSTRAINT price_list_author CHECK (created_by IS NOT NULL OR code = 'GENERAL'),
  CONSTRAINT price_list_version_positive CHECK (version >= 1)
);

CREATE FUNCTION sal.price_list_header_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'sal.price_list rows cannot be deleted; deactivate the list';
  END IF;
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'ACTIVE' OR NEW.version <> 1 THEN
      RAISE EXCEPTION 'sal.price_list: a list is created ACTIVE with version 1';
    END IF;
    RETURN NEW;
  END IF;
  IF ROW(NEW.price_list_id, NEW.company_id, NEW.code, NEW.created_by) IS DISTINCT FROM ROW(OLD.price_list_id, OLD.company_id, OLD.code, OLD.created_by)
     OR NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'sal.price_list: identity columns are immutable and the version increases by 1';
  END IF;
  IF NEW.status = 'INACTIVE' AND OLD.status = 'ACTIVE' AND EXISTS (
       SELECT 1 FROM sal.customer_terms_version t WHERE t.price_list_id = NEW.price_list_id AND t.status IN ('ACTIVE', 'DRAFT')) THEN
    RAISE EXCEPTION 'sal.price_list: % is the list of a customer (terms in force or pending)', NEW.code;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER price_list_header_guard BEFORE INSERT OR UPDATE OR DELETE ON sal.price_list
  FOR EACH ROW EXECUTE FUNCTION sal.price_list_header_guard();
CREATE TRIGGER price_list_header_no_truncate BEFORE TRUNCATE ON sal.price_list FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
CREATE CONSTRAINT TRIGGER price_list_header_evidence_on_change AFTER UPDATE ON sal.price_list
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('PriceListHeader', 'price_list_id');

-- GENERAL of a company, created on first use (a company created after this migration, E-PRC1-4).
CREATE FUNCTION sal.general_price_list(p_company uuid) RETURNS uuid
  LANGUAGE plpgsql AS $$
DECLARE
  v_id uuid;
BEGIN
  SELECT price_list_id INTO v_id FROM sal.price_list WHERE company_id = p_company AND code = 'GENERAL';
  IF v_id IS NULL THEN
    v_id := gen_random_uuid();
    INSERT INTO sal.price_list (price_list_id, company_id, code, name, status, created_by, version)
    VALUES (v_id, p_company, 'GENERAL', 'General', 'ACTIVE', NULL, 1);
  END IF;
  RETURN v_id;
END $$;

-- E-PRC1-4: the list of today becomes GENERAL.
INSERT INTO sal.price_list (price_list_id, company_id, code, name, status, created_by, version)
SELECT gen_random_uuid(), c.company_id, 'GENERAL', 'General', 'ACTIVE', NULL, 1 FROM md.company c;

ALTER TABLE sal.price_list_version ADD COLUMN price_list_id uuid;
UPDATE sal.price_list_version v SET price_list_id = l.price_list_id FROM sal.price_list l WHERE l.company_id = v.company_id AND l.code = 'GENERAL';
ALTER TABLE sal.price_list_version
  ALTER COLUMN price_list_id SET NOT NULL,
  ADD CONSTRAINT price_list_version_list_fk FOREIGN KEY (company_id, price_list_id) REFERENCES sal.price_list (company_id, price_list_id),
  DROP CONSTRAINT price_list_version_no_uq,
  ADD CONSTRAINT price_list_version_no_uq UNIQUE (price_list_id, version);
DROP INDEX sal.price_list_one_active;
CREATE UNIQUE INDEX price_list_one_active ON sal.price_list_version (price_list_id) WHERE status = 'ACTIVE';

-- A version prepared without a list is a version of GENERAL; a new version needs an ACTIVE list.
CREATE FUNCTION sal.price_list_version_list() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.price_list_id IS NULL THEN
    NEW.price_list_id := sal.general_price_list(NEW.company_id);
  END IF;
  IF NOT EXISTS (SELECT 1 FROM sal.price_list l WHERE l.price_list_id = NEW.price_list_id AND l.status = 'ACTIVE') THEN
    RAISE EXCEPTION 'sal.price_list_version: the list is inactive';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER price_list_version_list BEFORE INSERT ON sal.price_list_version FOR EACH ROW EXECUTE FUNCTION sal.price_list_version_list();

-- ---------------------------------------------------------------------------------------------
-- The customer's list is part of the customer terms (E-PRC1-2, E-PRC1-6); GENERAL unless another is named (E-PRS-01-8).
-- ---------------------------------------------------------------------------------------------
ALTER TABLE sal.customer_terms_version ADD COLUMN price_list_id uuid;
UPDATE sal.customer_terms_version t SET price_list_id = l.price_list_id FROM sal.price_list l WHERE l.company_id = t.company_id AND l.code = 'GENERAL';
ALTER TABLE sal.customer_terms_version
  ALTER COLUMN price_list_id SET NOT NULL,
  ADD CONSTRAINT customer_terms_price_list_fk FOREIGN KEY (company_id, price_list_id) REFERENCES sal.price_list (company_id, price_list_id);

CREATE FUNCTION sal.customer_terms_price_list() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' AND NEW.price_list_id IS NULL THEN
    NEW.price_list_id := sal.general_price_list(NEW.company_id);
  END IF;
  IF TG_OP = 'UPDATE' AND NEW.price_list_id IS DISTINCT FROM OLD.price_list_id AND OLD.status <> 'DRAFT' THEN
    RAISE EXCEPTION 'sal.customer_terms_version: the price list of approved terms does not change; prepare new terms';
  END IF;
  IF (TG_OP = 'INSERT' OR NEW.price_list_id IS DISTINCT FROM OLD.price_list_id)
     AND NOT EXISTS (SELECT 1 FROM sal.price_list l WHERE l.price_list_id = NEW.price_list_id AND l.status = 'ACTIVE') THEN
    RAISE EXCEPTION 'sal.customer_terms_version: the price list is inactive';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER customer_terms_price_list BEFORE INSERT OR UPDATE ON sal.customer_terms_version
  FOR EACH ROW EXECUTE FUNCTION sal.customer_terms_price_list();

-- ---------------------------------------------------------------------------------------------
-- Delivery zones (E-SRV1-2, E-SRV1-9, E-PRS-01-6): name, ACTIVE ⇄ INACTIVE, never deleted; no approval.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE sal.delivery_zone (
  zone_id     uuid    NOT NULL,
  company_id  uuid    NOT NULL,
  name        text    NOT NULL,
  status      text    NOT NULL,
  version     bigint  NOT NULL,
  CONSTRAINT delivery_zone_pk PRIMARY KEY (zone_id),
  CONSTRAINT delivery_zone_company_uq UNIQUE (company_id, zone_id),
  CONSTRAINT delivery_zone_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT delivery_zone_name_length CHECK (length(btrim(name)) BETWEEN 1 AND 60 AND name = btrim(name)),
  CONSTRAINT delivery_zone_status CHECK (status IN ('ACTIVE', 'INACTIVE')),
  CONSTRAINT delivery_zone_version_positive CHECK (version >= 1)
);
CREATE UNIQUE INDEX delivery_zone_name_uq ON sal.delivery_zone (company_id, lower(name));

CREATE FUNCTION sal.delivery_zone_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'sal.delivery_zone rows cannot be deleted; deactivate the zone';
  END IF;
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'ACTIVE' OR NEW.version <> 1 THEN
      RAISE EXCEPTION 'sal.delivery_zone: a zone is created ACTIVE with version 1';
    END IF;
    RETURN NEW;
  END IF;
  IF ROW(NEW.zone_id, NEW.company_id) IS DISTINCT FROM ROW(OLD.zone_id, OLD.company_id) OR NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'sal.delivery_zone: identity columns are immutable and the version increases by 1';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER delivery_zone_guard BEFORE INSERT OR UPDATE OR DELETE ON sal.delivery_zone FOR EACH ROW EXECUTE FUNCTION sal.delivery_zone_guard();
CREATE TRIGGER delivery_zone_no_truncate BEFORE TRUNCATE ON sal.delivery_zone FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- The freight item (E-SRV1-1, E-SRV1-8, E-PRS-01-7): item type SERVICE, category TRANSPORTE, one per company. The finished-good
-- and raw-material guards of price lists, costs, recipes, opening stock, orders and quotes already refuse it.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE md.item
  DROP CONSTRAINT item_type_value,
  DROP CONSTRAINT item_category_value,
  ADD CONSTRAINT item_type_value CHECK (item_type IN ('RAW_MATERIAL', 'FINISHED_GOOD', 'SERVICE')),
  ADD CONSTRAINT item_category_value CHECK (
    (item_type = 'RAW_MATERIAL' AND item_category IN ('CEMENTO', 'AGREGADO', 'ADITIVO', 'OTRA_MATERIA_PRIMA'))
    OR (item_type = 'FINISHED_GOOD' AND item_category IN ('BLOQUE', 'ADOQUIN', 'OTRO_PT'))
    OR (item_type = 'SERVICE' AND item_category = 'TRANSPORTE'));
CREATE UNIQUE INDEX item_one_freight ON md.item (company_id) WHERE item_category = 'TRANSPORTE';

-- ---------------------------------------------------------------------------------------------
-- Freight prices of a list version (E-SRV1-10): product, unit, zone → price per unit of the product. Written with the DRAFT
-- version, never changed (a correction is a new version).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE sal.price_list_freight (
  price_list_version_id  uuid          NOT NULL,
  company_id             uuid          NOT NULL,
  item_id                uuid          NOT NULL,
  uom                    text          NOT NULL,
  zone_id                uuid          NOT NULL,
  unit_price             numeric(19,4) NOT NULL,
  CONSTRAINT price_list_freight_pk PRIMARY KEY (price_list_version_id, item_id, uom, zone_id),
  CONSTRAINT price_list_freight_version_fk FOREIGN KEY (company_id, price_list_version_id) REFERENCES sal.price_list_version (company_id, price_list_version_id),
  CONSTRAINT price_list_freight_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT price_list_freight_uom_fk FOREIGN KEY (uom) REFERENCES md.uom (uom_code),
  CONSTRAINT price_list_freight_zone_fk FOREIGN KEY (company_id, zone_id) REFERENCES sal.delivery_zone (company_id, zone_id),
  CONSTRAINT price_list_freight_price CHECK (unit_price > 0)
);

CREATE FUNCTION sal.price_list_freight_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'INSERT' THEN
    RAISE EXCEPTION 'sal.price_list_freight rows cannot be changed or deleted; prepare a new version';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM sal.price_list_version v WHERE v.price_list_version_id = NEW.price_list_version_id AND v.status = 'DRAFT') THEN
    RAISE EXCEPTION 'sal.price_list_freight: freight prices are added only to a DRAFT version';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM md.item i WHERE i.item_id = NEW.item_id AND i.item_type = 'FINISHED_GOOD') THEN
    RAISE EXCEPTION 'sal.price_list_freight: item % is not a finished good', NEW.item_id;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER price_list_freight_guard BEFORE INSERT OR UPDATE OR DELETE ON sal.price_list_freight
  FOR EACH ROW EXECUTE FUNCTION sal.price_list_freight_guard();
CREATE TRIGGER price_list_freight_no_truncate BEFORE TRUNCATE ON sal.price_list_freight FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- Orders and quotes: the zone (own truck only, E-SRV1-11), the freight on the product's own line (E-PRS-01-1), and the list version
-- each product price came from (the customer's or GENERAL, E-PRC1-7; NULL for a quoted price). total_net includes the freight.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE sal.sales_order
  ADD COLUMN delivery_zone_id uuid,
  ADD CONSTRAINT sales_order_zone_fk FOREIGN KEY (company_id, delivery_zone_id) REFERENCES sal.delivery_zone (company_id, zone_id),
  ADD CONSTRAINT sales_order_zone_own_truck CHECK (delivery_zone_id IS NULL OR delivery_term_code = 'DELIVERED_OWN_TRANSPORT');
ALTER TABLE sal.quote
  ADD COLUMN delivery_zone_id uuid,
  ADD CONSTRAINT quote_zone_fk FOREIGN KEY (company_id, delivery_zone_id) REFERENCES sal.delivery_zone (company_id, zone_id),
  ADD CONSTRAINT quote_zone_own_truck CHECK (delivery_zone_id IS NULL OR delivery_term_code = 'DELIVERED_OWN_TRANSPORT');

-- The zone changes only while the document is DRAFT, and a zone given is ACTIVE.
CREATE FUNCTION sal.document_zone_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'UPDATE' AND NEW.delivery_zone_id IS DISTINCT FROM OLD.delivery_zone_id AND OLD.status <> 'DRAFT' THEN
    RAISE EXCEPTION '%.%: the zone changes only while DRAFT', TG_TABLE_SCHEMA, TG_TABLE_NAME;
  END IF;
  IF NEW.delivery_zone_id IS NOT NULL AND (TG_OP = 'INSERT' OR NEW.delivery_zone_id IS DISTINCT FROM OLD.delivery_zone_id)
     AND NOT EXISTS (SELECT 1 FROM sal.delivery_zone z WHERE z.zone_id = NEW.delivery_zone_id AND z.status = 'ACTIVE') THEN
    RAISE EXCEPTION '%.%: the zone is inactive', TG_TABLE_SCHEMA, TG_TABLE_NAME;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER sales_order_zone_guard BEFORE INSERT OR UPDATE ON sal.sales_order FOR EACH ROW EXECUTE FUNCTION sal.document_zone_guard();
CREATE TRIGGER quote_zone_guard BEFORE INSERT OR UPDATE ON sal.quote FOR EACH ROW EXECUTE FUNCTION sal.document_zone_guard();

ALTER TABLE sal.sales_order_line
  ADD COLUMN price_list_version_id uuid,
  ADD COLUMN freight_unit_price numeric(19,4),
  ADD COLUMN freight_amount numeric(19,4),
  ADD CONSTRAINT sales_order_line_price_list_fk FOREIGN KEY (company_id, price_list_version_id) REFERENCES sal.price_list_version (company_id, price_list_version_id),
  ADD CONSTRAINT sales_order_line_freight CHECK ((freight_unit_price IS NULL) = (freight_amount IS NULL)
    AND (freight_unit_price IS NULL OR (freight_unit_price > 0 AND freight_amount > 0 AND freight_amount = round(freight_amount, 2))));
ALTER TABLE sal.quote_line
  ADD COLUMN freight_unit_price numeric(19,4),
  ADD COLUMN freight_amount numeric(19,4),
  ADD CONSTRAINT quote_line_freight CHECK ((freight_unit_price IS NULL) = (freight_amount IS NULL)
    AND (freight_unit_price IS NULL OR (freight_unit_price > 0 AND freight_amount > 0 AND freight_amount = round(freight_amount, 2))));

-- E-SRV1-11, E-SRV1-17: freight only on an own-truck document with a zone, and never on an order whose exemption is pending.
CREATE FUNCTION sal.order_line_freight_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF (NEW.freight_amount IS NOT NULL OR NEW.freight_unit_price IS NOT NULL) AND NOT EXISTS (
       SELECT 1 FROM sal.sales_order o WHERE o.sales_order_id = NEW.sales_order_id AND o.delivery_zone_id IS NOT NULL AND NOT o.exemption_pending) THEN
    RAISE EXCEPTION 'sal.sales_order_line: freight needs an own-truck order with a zone and no pending exemption';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER sales_order_line_freight_guard BEFORE INSERT ON sal.sales_order_line FOR EACH ROW EXECUTE FUNCTION sal.order_line_freight_guard();

CREATE FUNCTION sal.quote_line_freight_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.freight_amount IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sal.quote q WHERE q.quote_id = NEW.quote_id AND q.delivery_zone_id IS NOT NULL) THEN
    RAISE EXCEPTION 'sal.quote_line: freight needs an own-truck quote with a zone';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER quote_line_freight_guard BEFORE INSERT ON sal.quote_line FOR EACH ROW EXECUTE FUNCTION sal.quote_line_freight_guard();

-- ---------------------------------------------------------------------------------------------
-- Invoice and proforma lines (E-PRS-01-2): a delivered line gives a PRODUCT line and, with freight, a FREIGHT line of the freight
-- item, each with its own number. Existing lines are products.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE sal.invoice_line
  ADD COLUMN line_kind text NOT NULL DEFAULT 'PRODUCT',
  ADD CONSTRAINT invoice_line_kind CHECK (line_kind IN ('PRODUCT', 'FREIGHT')),
  DROP CONSTRAINT invoice_line_delivery_uq,
  ADD CONSTRAINT invoice_line_delivery_uq UNIQUE (invoice_id, delivery_line_id, line_kind);
ALTER TABLE sal.proforma_line
  ADD COLUMN line_kind text NOT NULL DEFAULT 'PRODUCT',
  ADD CONSTRAINT proforma_line_kind CHECK (line_kind IN ('PRODUCT', 'FREIGHT')),
  DROP CONSTRAINT proforma_line_delivery_uq,
  ADD CONSTRAINT proforma_line_delivery_uq UNIQUE (company_id, delivery_line_id, line_kind);

-- A PRODUCT line is the delivered finished good; a FREIGHT line is the freight item, without ITBIS on a proforma.
CREATE FUNCTION sal.document_line_kind_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  v_type text := (SELECT item_type FROM md.item WHERE item_id = NEW.item_id);
BEGIN
  IF (NEW.line_kind = 'PRODUCT') <> (v_type = 'FINISHED_GOOD') OR (NEW.line_kind = 'FREIGHT') <> (v_type = 'SERVICE') THEN
    RAISE EXCEPTION '%.%: a % line is not of item type %', TG_TABLE_SCHEMA, TG_TABLE_NAME, NEW.line_kind, v_type;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER invoice_line_kind_guard BEFORE INSERT ON sal.invoice_line FOR EACH ROW EXECUTE FUNCTION sal.document_line_kind_guard();
CREATE TRIGGER proforma_line_kind_guard BEFORE INSERT ON sal.proforma_line FOR EACH ROW EXECUTE FUNCTION sal.document_line_kind_guard();
ALTER TABLE sal.proforma_line
  ADD CONSTRAINT proforma_line_freight_no_itbis CHECK (line_kind = 'PRODUCT' OR itbis_amount = 0);

-- ---------------------------------------------------------------------------------------------
-- Freight revenue (E-SRV1-7, E-SRV1-14, E-SRV1-18): its own role, mapped by the Controller (A-01).
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.account_role (role_code, is_control, description, name) VALUES
  ('FREIGHT_REVENUE', false, 'Ingresos por el flete de las entregas con camión propio (sin costo de ventas)', 'Ingresos por transporte');

-- ---------------------------------------------------------------------------------------------
-- Permission and grants (E-PRS-01-6).
-- ---------------------------------------------------------------------------------------------
INSERT INTO iam.permission (permission_code, access) VALUES ('delivery_zone:manage', 'WRITE');
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, 'delivery_zone:manage' FROM iam.role r WHERE r.code IN ('CONTROLLER', 'CREDITO');

GRANT SELECT, INSERT ON sal.price_list, sal.delivery_zone, sal.price_list_freight TO rochell_app;
GRANT UPDATE (name, status, version) ON sal.price_list TO rochell_app;
GRANT UPDATE (name, status, version) ON sal.delivery_zone TO rochell_app;
GRANT UPDATE (price_list_id) ON sal.customer_terms_version TO rochell_app;
GRANT UPDATE (delivery_zone_id) ON sal.sales_order, sal.quote TO rochell_app;
