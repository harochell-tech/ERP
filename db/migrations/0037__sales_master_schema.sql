-- VS3-01 · Sales master data: customers, customer terms, finished goods and standard cost, price list, vehicles, drivers,
-- account roles, roles and permissions (no commands yet). Frozen Baseline VS#3 §2 and §7; approved errata E-VS3-1…17 and
-- E-VS3-01-1…17. The documents of the slice (orders, deliveries, invoices, receipts, …) arrive with their PRs (E-VS3-01-17).

CREATE SCHEMA sal;
CREATE SCHEMA log;
GRANT USAGE ON SCHEMA sal, log TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- Customers (E-VS3-01-1, E-VS3-01-8, E-VS3-01-9): the same md.party flagged is_customer, with its own customer status and
-- optional contact data. RNC and legal name change only while the party is DRAFT (as for suppliers, E-PR04-4); contact data
-- changes at any time; an ACTIVE party may become a customer without touching its supplier status.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE md.party
  ADD COLUMN is_customer boolean NOT NULL DEFAULT false,
  ADD COLUMN customer_status text,
  ADD COLUMN phone text,
  ADD COLUMN email text,
  ADD COLUMN address text,
  ADD CONSTRAINT party_customer_status CHECK ((customer_status IS NULL) = (NOT is_customer)
    AND (customer_status IS NULL OR customer_status IN ('DRAFT', 'ACTIVE', 'BLOCKED'))),
  ADD CONSTRAINT party_phone_format CHECK (phone IS NULL OR (length(btrim(phone)) BETWEEN 1 AND 30)),
  ADD CONSTRAINT party_email_format CHECK (email IS NULL OR (email ~ '^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$' AND length(email) <= 200)),
  ADD CONSTRAINT party_address_length CHECK (address IS NULL OR (length(btrim(address)) BETWEEN 1 AND 300));

CREATE FUNCTION md.party_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'UPDATE' THEN
    RAISE EXCEPTION 'md.party rows cannot be deleted';
  END IF;
  IF NEW.company_id <> OLD.company_id OR NEW.is_supplier <> OLD.is_supplier THEN
    RAISE EXCEPTION 'md.party: company and supplier flag cannot change';
  END IF;
  IF NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'md.party: version must increase by exactly 1 (expected %, got %)', OLD.version + 1, NEW.version;
  END IF;
  -- Party identity: editable while DRAFT; the only party transition is DRAFT → ACTIVE.
  IF OLD.status <> 'DRAFT' AND ROW(NEW.rnc, NEW.legal_name, NEW.status, NEW.rnc_validated_at)
                               IS DISTINCT FROM ROW(OLD.rnc, OLD.legal_name, OLD.status, OLD.rnc_validated_at) THEN
    RAISE EXCEPTION 'md.party: RNC, legal name and status of an % party cannot change', OLD.status;
  END IF;
  IF NEW.status NOT IN ('DRAFT', 'ACTIVE') THEN
    RAISE EXCEPTION 'md.party: status % is not used', NEW.status;
  END IF;
  -- Customer role (E-VS3-01-8): once a customer, always a customer; DRAFT → ACTIVE, ACTIVE ⇄ BLOCKED.
  IF OLD.is_customer AND NOT NEW.is_customer THEN
    RAISE EXCEPTION 'md.party: a customer stays a customer';
  END IF;
  IF NEW.customer_status IS DISTINCT FROM OLD.customer_status
     AND NOT ((OLD.customer_status IS NULL AND NEW.customer_status = 'DRAFT')
              OR (OLD.customer_status = 'DRAFT' AND NEW.customer_status = 'ACTIVE')
              OR (OLD.customer_status = 'ACTIVE' AND NEW.customer_status = 'BLOCKED')
              OR (OLD.customer_status = 'BLOCKED' AND NEW.customer_status = 'ACTIVE')) THEN
    RAISE EXCEPTION 'md.party: customer transition % → % is not allowed', OLD.customer_status, NEW.customer_status;
  END IF;
  RETURN NEW;
END $$;
DROP TRIGGER party_guard ON md.party;
CREATE TRIGGER party_guard BEFORE UPDATE OR DELETE ON md.party FOR EACH ROW EXECUTE FUNCTION md.party_guard();

-- ADR-027 for the customer status (the party's own status keeps the rules of VS#1).
CREATE FUNCTION md.require_customer_history() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.customer_status IS NOT NULL AND NOT EXISTS (
       SELECT 1 FROM core.state_history h
       WHERE h.aggregate_type = 'Customer' AND h.aggregate_id = NEW.party_id AND h.to_state = NEW.customer_status
         AND h.xmin = pg_current_xact_id()::xid) THEN
    RAISE EXCEPTION 'Customer %: status % without its state_history row (ADR-027)', NEW.party_id, NEW.customer_status;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER party_customer_evidence_on_insert AFTER INSERT ON md.party
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.customer_status IS NOT NULL) EXECUTE FUNCTION md.require_customer_history();
CREATE CONSTRAINT TRIGGER party_customer_evidence_on_change AFTER UPDATE ON md.party
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.customer_status IS DISTINCT FROM NEW.customer_status)
  EXECUTE FUNCTION md.require_customer_history();

GRANT UPDATE (is_customer, customer_status, phone, email, address) ON md.party TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- Finished goods (E-VS3-01-2): item type FINISHED_GOOD with categories BLOQUE, ADOQUIN, OTRO_PT.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE md.item
  DROP CONSTRAINT item_type_vs1,
  DROP CONSTRAINT item_category_value,
  ADD CONSTRAINT item_type_value CHECK (item_type IN ('RAW_MATERIAL', 'FINISHED_GOOD')),
  ADD CONSTRAINT item_category_value CHECK (
    (item_type = 'RAW_MATERIAL' AND item_category IN ('CEMENTO', 'AGREGADO', 'ADITIVO', 'OTRA_MATERIA_PRIMA'))
    OR (item_type = 'FINISHED_GOOD' AND item_category IN ('BLOQUE', 'ADOQUIN', 'OTRO_PT')));

-- ---------------------------------------------------------------------------------------------
-- Account roles of VS#3 (E-VS3-01-7, E-VS3-01-16), seeded unmapped. New subledger AR (per customer; per receipt for cash in
-- transit); finished goods use the INV subledger. Role maps by category accept the finished-good categories.
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.account_role (role_code, is_control, description) VALUES
  ('AR_CONTROL', true, 'Cuentas por cobrar clientes (subledger AR)'),
  ('CONTRACT_ASSET', true, 'Activo de contrato: entregado no facturado (subledger AR)'),
  ('UNAPPLIED_RECEIPTS', true, 'Cobros no aplicados (subledger AR)'),
  ('CASH_IN_TRANSIT', true, 'Efectivo y cheques en tránsito hasta el depósito (subledger AR, por recibo)'),
  ('FINISHED_GOODS', true, 'Inventario de producto terminado (subledger INV)'),
  ('FINISHED_GOODS_IN_TRANSIT', true, 'Producto terminado en tránsito (subledger INV)'),
  ('COGS', false, 'Costo de ventas'),
  ('REVENUE_PRODUCT', false, 'Ingresos por venta de producto'),
  ('SALES_DISCOUNTS', false, 'Descuentos y rebajas sobre ventas'),
  ('ITBIS_PAYABLE', false, 'ITBIS por pagar (ventas)'),
  ('WITHHOLDING_RECEIVABLE', false, 'Retenciones hechas por clientes'),
  ('TRANSIT_LOSS', false, 'Pérdida en tránsito'),
  ('MIGRATION_CLEARING', false, 'Contrapartida de saldos de apertura');

ALTER TABLE fin.gl_entry
  DROP CONSTRAINT gl_entry_subledger_type,
  ADD CONSTRAINT gl_entry_subledger_type CHECK (subledger_type IS NULL OR subledger_type IN ('AP', 'INV', 'BANK', 'AR')),
  DROP CONSTRAINT gl_entry_role_subledger,
  ADD CONSTRAINT gl_entry_role_subledger CHECK (
    (account_role NOT IN ('RAW_MATERIAL', 'FINISHED_GOODS', 'FINISHED_GOODS_IN_TRANSIT') OR subledger_type = 'INV')
    AND (account_role <> 'AP_CONTROL' OR subledger_type = 'AP')
    AND (account_role NOT IN ('AR_CONTROL', 'CONTRACT_ASSET', 'UNAPPLIED_RECEIPTS', 'CASH_IN_TRANSIT') OR subledger_type = 'AR')
    AND ((account_role = 'BANK') = (subledger_type IS NOT DISTINCT FROM 'BANK')));

ALTER TABLE fin.account_role_map
  DROP CONSTRAINT account_role_map_category,
  ADD CONSTRAINT account_role_map_category CHECK (item_category IS NULL
    OR item_category IN ('CEMENTO', 'AGREGADO', 'ADITIVO', 'OTRA_MATERIA_PRIMA', 'BLOQUE', 'ADOQUIN', 'OTRO_PT'));

-- ---------------------------------------------------------------------------------------------
-- Versioned approvals shared by customer terms, standard cost and the price list: DRAFT → ACTIVE (the previous ACTIVE →
-- SUPERSEDED in the same transaction); the approver differs from the preparer; content changes only while DRAFT.
-- TG_ARGV: the columns that are content (editable in DRAFT) are listed by each table's guard below.
-- ---------------------------------------------------------------------------------------------
CREATE FUNCTION sal.version_transition_allowed(old_status text, new_status text) RETURNS boolean
  LANGUAGE sql IMMUTABLE AS $$
  SELECT old_status = new_status OR (old_status = 'DRAFT' AND new_status = 'ACTIVE') OR (old_status = 'ACTIVE' AND new_status = 'SUPERSEDED')
$$;

-- Customer terms (E-VS3-17 (a), E-VS3-01-10): prepared by Crédito, approved by the Controller.
CREATE TABLE sal.customer_terms_version (
  terms_version_id    uuid          NOT NULL,
  company_id          uuid          NOT NULL,
  party_id            uuid          NOT NULL,
  version             integer       NOT NULL,
  effective_from      date          NOT NULL,
  payment_terms_days  integer       NOT NULL,
  credit_limit        numeric(19,4) NOT NULL,
  credit_hold         boolean       NOT NULL,
  status              text          NOT NULL,
  prepared_by         uuid          NOT NULL,
  approved_by         uuid,
  CONSTRAINT customer_terms_version_pk PRIMARY KEY (terms_version_id),
  CONSTRAINT customer_terms_version_company_uq UNIQUE (company_id, terms_version_id),
  CONSTRAINT customer_terms_version_no_uq UNIQUE (company_id, party_id, version),
  CONSTRAINT customer_terms_version_party_fk FOREIGN KEY (company_id, party_id) REFERENCES md.party (company_id, party_id),
  CONSTRAINT customer_terms_version_prepared_fk FOREIGN KEY (prepared_by) REFERENCES iam.user (user_id),
  CONSTRAINT customer_terms_version_approved_fk FOREIGN KEY (approved_by) REFERENCES iam.user (user_id),
  CONSTRAINT customer_terms_version_positive CHECK (version >= 1),
  CONSTRAINT customer_terms_days CHECK (payment_terms_days BETWEEN 0 AND 365),
  CONSTRAINT customer_terms_limit CHECK (credit_limit >= 0),
  CONSTRAINT customer_terms_status CHECK (status IN ('DRAFT', 'ACTIVE', 'SUPERSEDED')),
  CONSTRAINT customer_terms_four_eyes CHECK (approved_by IS NULL OR approved_by <> prepared_by),
  CONSTRAINT customer_terms_approved CHECK ((status IN ('ACTIVE', 'SUPERSEDED')) = (approved_by IS NOT NULL))
);
CREATE UNIQUE INDEX customer_terms_one_active ON sal.customer_terms_version (company_id, party_id) WHERE status = 'ACTIVE';

CREATE FUNCTION sal.customer_terms_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'DRAFT' THEN
      RAISE EXCEPTION 'sal.customer_terms_version: a version is prepared as DRAFT';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM md.party p WHERE p.party_id = NEW.party_id AND p.is_customer) THEN
      RAISE EXCEPTION 'sal.customer_terms_version: party % is not a customer', NEW.party_id;
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'sal.customer_terms_version rows cannot be deleted';
  END IF;
  IF ROW(NEW.terms_version_id, NEW.company_id, NEW.party_id, NEW.version, NEW.prepared_by)
     IS DISTINCT FROM ROW(OLD.terms_version_id, OLD.company_id, OLD.party_id, OLD.version, OLD.prepared_by)
     OR NOT sal.version_transition_allowed(OLD.status, NEW.status)
     OR (OLD.status <> 'DRAFT' AND ROW(NEW.effective_from, NEW.payment_terms_days, NEW.credit_limit, NEW.credit_hold)
                                   IS DISTINCT FROM ROW(OLD.effective_from, OLD.payment_terms_days, OLD.credit_limit, OLD.credit_hold)) THEN
    RAISE EXCEPTION 'sal.customer_terms_version: % → % or a change of an approved version is not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER customer_terms_guard BEFORE INSERT OR UPDATE OR DELETE ON sal.customer_terms_version
  FOR EACH ROW EXECUTE FUNCTION sal.customer_terms_guard();
CREATE TRIGGER customer_terms_no_truncate BEFORE TRUNCATE ON sal.customer_terms_version FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
CREATE CONSTRAINT TRIGGER customer_terms_evidence_on_insert AFTER INSERT ON sal.customer_terms_version
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('CustomerTerms', 'terms_version_id');
CREATE CONSTRAINT TRIGGER customer_terms_evidence_on_change AFTER UPDATE ON sal.customer_terms_version
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('CustomerTerms', 'terms_version_id');

-- Standard cost of a finished good per valuation area (E-VS3-01-3, E-VS3-01-13): unit cost in the base UoM.
CREATE TABLE md.standard_cost_version (
  cost_version_id    uuid          NOT NULL,
  company_id         uuid          NOT NULL,
  item_id            uuid          NOT NULL,
  valuation_area_id  uuid          NOT NULL,
  version            integer       NOT NULL,
  effective_from     date          NOT NULL,
  unit_cost          numeric(19,4) NOT NULL,
  status             text          NOT NULL,
  prepared_by        uuid          NOT NULL,
  approved_by        uuid,
  CONSTRAINT standard_cost_version_pk PRIMARY KEY (cost_version_id),
  CONSTRAINT standard_cost_version_company_uq UNIQUE (company_id, cost_version_id),
  CONSTRAINT standard_cost_version_no_uq UNIQUE (company_id, item_id, valuation_area_id, version),
  CONSTRAINT standard_cost_version_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT standard_cost_version_area_fk FOREIGN KEY (valuation_area_id) REFERENCES md.valuation_area (valuation_area_id),
  CONSTRAINT standard_cost_version_prepared_fk FOREIGN KEY (prepared_by) REFERENCES iam.user (user_id),
  CONSTRAINT standard_cost_version_approved_fk FOREIGN KEY (approved_by) REFERENCES iam.user (user_id),
  CONSTRAINT standard_cost_version_positive CHECK (version >= 1),
  CONSTRAINT standard_cost_unit_cost CHECK (unit_cost > 0),
  CONSTRAINT standard_cost_status CHECK (status IN ('DRAFT', 'ACTIVE', 'SUPERSEDED')),
  CONSTRAINT standard_cost_four_eyes CHECK (approved_by IS NULL OR approved_by <> prepared_by),
  CONSTRAINT standard_cost_approved CHECK ((status IN ('ACTIVE', 'SUPERSEDED')) = (approved_by IS NOT NULL))
);
CREATE UNIQUE INDEX standard_cost_one_active ON md.standard_cost_version (company_id, item_id, valuation_area_id) WHERE status = 'ACTIVE';

CREATE FUNCTION md.standard_cost_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'DRAFT' THEN
      RAISE EXCEPTION 'md.standard_cost_version: a version is prepared as DRAFT';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM md.item i WHERE i.item_id = NEW.item_id AND i.item_type = 'FINISHED_GOOD') THEN
      RAISE EXCEPTION 'md.standard_cost_version: item % is not a finished good', NEW.item_id;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM md.valuation_area a WHERE a.valuation_area_id = NEW.valuation_area_id AND a.company_id = NEW.company_id) THEN
      RAISE EXCEPTION 'md.standard_cost_version: valuation area % is not of the company', NEW.valuation_area_id;
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'md.standard_cost_version rows cannot be deleted';
  END IF;
  IF ROW(NEW.cost_version_id, NEW.company_id, NEW.item_id, NEW.valuation_area_id, NEW.version, NEW.prepared_by)
     IS DISTINCT FROM ROW(OLD.cost_version_id, OLD.company_id, OLD.item_id, OLD.valuation_area_id, OLD.version, OLD.prepared_by)
     OR NOT sal.version_transition_allowed(OLD.status, NEW.status)
     OR (OLD.status <> 'DRAFT' AND ROW(NEW.effective_from, NEW.unit_cost) IS DISTINCT FROM ROW(OLD.effective_from, OLD.unit_cost)) THEN
    RAISE EXCEPTION 'md.standard_cost_version: % → % or a change of an approved version is not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER standard_cost_guard BEFORE INSERT OR UPDATE OR DELETE ON md.standard_cost_version
  FOR EACH ROW EXECUTE FUNCTION md.standard_cost_guard();
CREATE TRIGGER standard_cost_no_truncate BEFORE TRUNCATE ON md.standard_cost_version FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
CREATE CONSTRAINT TRIGGER standard_cost_evidence_on_insert AFTER INSERT ON md.standard_cost_version
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('StandardCost', 'cost_version_id');
CREATE CONSTRAINT TRIGGER standard_cost_evidence_on_change AFTER UPDATE ON md.standard_cost_version
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('StandardCost', 'cost_version_id');

-- Price list (E-VS3-01-3, E-VS3-01-14): one list in force per company; DOP without ITBIS, per finished good and unit.
CREATE TABLE sal.price_list_version (
  price_list_version_id  uuid    NOT NULL,
  company_id             uuid    NOT NULL,
  version                integer NOT NULL,
  effective_from         date    NOT NULL,
  status                 text    NOT NULL,
  prepared_by            uuid    NOT NULL,
  approved_by            uuid,
  CONSTRAINT price_list_version_pk PRIMARY KEY (price_list_version_id),
  CONSTRAINT price_list_version_company_uq UNIQUE (company_id, price_list_version_id),
  CONSTRAINT price_list_version_no_uq UNIQUE (company_id, version),
  CONSTRAINT price_list_version_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT price_list_version_prepared_fk FOREIGN KEY (prepared_by) REFERENCES iam.user (user_id),
  CONSTRAINT price_list_version_approved_fk FOREIGN KEY (approved_by) REFERENCES iam.user (user_id),
  CONSTRAINT price_list_version_positive CHECK (version >= 1),
  CONSTRAINT price_list_status CHECK (status IN ('DRAFT', 'ACTIVE', 'SUPERSEDED')),
  CONSTRAINT price_list_four_eyes CHECK (approved_by IS NULL OR approved_by <> prepared_by),
  CONSTRAINT price_list_approved CHECK ((status IN ('ACTIVE', 'SUPERSEDED')) = (approved_by IS NOT NULL))
);
CREATE UNIQUE INDEX price_list_one_active ON sal.price_list_version (company_id) WHERE status = 'ACTIVE';

CREATE TABLE sal.price_list_line (
  price_list_version_id  uuid          NOT NULL,
  company_id             uuid          NOT NULL,
  item_id                uuid          NOT NULL,
  uom                    text          NOT NULL,
  unit_price             numeric(19,4) NOT NULL,
  CONSTRAINT price_list_line_pk PRIMARY KEY (price_list_version_id, item_id, uom),
  CONSTRAINT price_list_line_version_fk FOREIGN KEY (company_id, price_list_version_id) REFERENCES sal.price_list_version (company_id, price_list_version_id),
  CONSTRAINT price_list_line_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT price_list_line_uom_fk FOREIGN KEY (uom) REFERENCES md.uom (uom_code),
  CONSTRAINT price_list_line_price CHECK (unit_price > 0)
);

CREATE FUNCTION sal.price_list_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'DRAFT' THEN
      RAISE EXCEPTION 'sal.price_list_version: a version is prepared as DRAFT';
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'sal.price_list_version rows cannot be deleted';
  END IF;
  IF ROW(NEW.price_list_version_id, NEW.company_id, NEW.version, NEW.prepared_by)
     IS DISTINCT FROM ROW(OLD.price_list_version_id, OLD.company_id, OLD.version, OLD.prepared_by)
     OR NOT sal.version_transition_allowed(OLD.status, NEW.status)
     OR (OLD.status <> 'DRAFT' AND NEW.effective_from IS DISTINCT FROM OLD.effective_from) THEN
    RAISE EXCEPTION 'sal.price_list_version: % → % or a change of an approved version is not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER price_list_guard BEFORE INSERT OR UPDATE OR DELETE ON sal.price_list_version
  FOR EACH ROW EXECUTE FUNCTION sal.price_list_guard();
CREATE TRIGGER price_list_no_truncate BEFORE TRUNCATE ON sal.price_list_version FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
CREATE CONSTRAINT TRIGGER price_list_evidence_on_insert AFTER INSERT ON sal.price_list_version
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('PriceList', 'price_list_version_id');
CREATE CONSTRAINT TRIGGER price_list_evidence_on_change AFTER UPDATE ON sal.price_list_version
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('PriceList', 'price_list_version_id');

-- Lines of a DRAFT list only, of finished goods only, never changed or deleted (a correction is a new version).
CREATE FUNCTION sal.price_list_line_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'INSERT' THEN
    RAISE EXCEPTION 'sal.price_list_line rows cannot be changed or deleted; prepare a new version';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM sal.price_list_version v WHERE v.price_list_version_id = NEW.price_list_version_id AND v.status = 'DRAFT') THEN
    RAISE EXCEPTION 'sal.price_list_line: lines are added only to a DRAFT version';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM md.item i WHERE i.item_id = NEW.item_id AND i.item_type = 'FINISHED_GOOD') THEN
    RAISE EXCEPTION 'sal.price_list_line: item % is not a finished good', NEW.item_id;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER price_list_line_guard BEFORE INSERT OR UPDATE OR DELETE ON sal.price_list_line
  FOR EACH ROW EXECUTE FUNCTION sal.price_list_line_guard();
CREATE TRIGGER price_list_line_no_truncate BEFORE TRUNCATE ON sal.price_list_line FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- Vehicles and drivers (E-VS3-01-4, E-VS3-01-15): registered by Despacho; ACTIVE ⇄ INACTIVE; never deleted.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE log.vehicle (
  vehicle_id   uuid          NOT NULL,
  company_id   uuid          NOT NULL,
  plate        text          NOT NULL,
  capacity_kg  numeric(18,6) NOT NULL,
  status       text          NOT NULL,
  version      bigint        NOT NULL,
  CONSTRAINT vehicle_pk PRIMARY KEY (vehicle_id),
  CONSTRAINT vehicle_company_uq UNIQUE (company_id, vehicle_id),
  CONSTRAINT vehicle_plate_uq UNIQUE (company_id, plate),
  CONSTRAINT vehicle_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT vehicle_plate_format CHECK (plate ~ '^[A-Z0-9]{5,10}$'),
  CONSTRAINT vehicle_capacity CHECK (capacity_kg > 0),
  CONSTRAINT vehicle_status CHECK (status IN ('ACTIVE', 'INACTIVE')),
  CONSTRAINT vehicle_version_positive CHECK (version >= 1)
);

CREATE TABLE log.driver (
  driver_id    uuid   NOT NULL,
  company_id   uuid   NOT NULL,
  full_name    text   NOT NULL,
  national_id  text   NOT NULL,
  status       text   NOT NULL,
  version      bigint NOT NULL,
  CONSTRAINT driver_pk PRIMARY KEY (driver_id),
  CONSTRAINT driver_company_uq UNIQUE (company_id, driver_id),
  CONSTRAINT driver_national_id_uq UNIQUE (company_id, national_id),
  CONSTRAINT driver_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT driver_national_id_format CHECK (national_id ~ '^[0-9]{11}$'),
  CONSTRAINT driver_name_present CHECK (length(btrim(full_name)) BETWEEN 1 AND 200),
  CONSTRAINT driver_status CHECK (status IN ('ACTIVE', 'INACTIVE')),
  CONSTRAINT driver_version_positive CHECK (version >= 1)
);

-- Shared guard: registered ACTIVE with version 1; the identity column (plate, cédula) never changes; version +1 per change.
CREATE FUNCTION log.fleet_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  identity_column text := TG_ARGV[0];
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'ACTIVE' OR NEW.version <> 1 THEN
      RAISE EXCEPTION 'log.%: registered ACTIVE with version 1', TG_TABLE_NAME;
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'log.% rows cannot be deleted; make them INACTIVE', TG_TABLE_NAME;
  END IF;
  IF NEW.company_id <> OLD.company_id OR (to_jsonb(NEW) ->> identity_column) IS DISTINCT FROM (to_jsonb(OLD) ->> identity_column) THEN
    RAISE EXCEPTION 'log.%: company and % cannot change', TG_TABLE_NAME, identity_column;
  END IF;
  IF NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'log.%: version must increase by exactly 1', TG_TABLE_NAME;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER vehicle_guard BEFORE INSERT OR UPDATE OR DELETE ON log.vehicle FOR EACH ROW EXECUTE FUNCTION log.fleet_guard('plate');
CREATE TRIGGER vehicle_no_truncate BEFORE TRUNCATE ON log.vehicle FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER driver_guard BEFORE INSERT OR UPDATE OR DELETE ON log.driver FOR EACH ROW EXECUTE FUNCTION log.fleet_guard('national_id');
CREATE TRIGGER driver_no_truncate BEFORE TRUNCATE ON log.driver FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
CREATE CONSTRAINT TRIGGER vehicle_evidence_on_insert AFTER INSERT ON log.vehicle
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('Vehicle', 'vehicle_id');
CREATE CONSTRAINT TRIGGER vehicle_evidence_on_change AFTER UPDATE ON log.vehicle
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('Vehicle', 'vehicle_id');
CREATE CONSTRAINT TRIGGER driver_evidence_on_insert AFTER INSERT ON log.driver
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('Driver', 'driver_id');
CREATE CONSTRAINT TRIGGER driver_evidence_on_change AFTER UPDATE ON log.driver
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('Driver', 'driver_id');

-- ---------------------------------------------------------------------------------------------
-- Roles, permissions and segregation of duties (E-VS3-3, E-VS3-01-11, E-VS3-01-12).
-- ---------------------------------------------------------------------------------------------
INSERT INTO iam.permission (permission_code, access) VALUES
  ('customer:create', 'WRITE'), ('customer:update', 'WRITE'), ('customer:activate', 'WRITE'),
  ('customer_terms:prepare', 'WRITE'), ('customer_terms:approve', 'WRITE'),
  ('standard_cost:prepare', 'WRITE'), ('standard_cost:approve', 'WRITE'),
  ('price_list:prepare', 'WRITE'), ('price_list:approve', 'WRITE'),
  ('fleet:manage', 'WRITE'), ('sales:read', 'READ');

INSERT INTO iam.role (role_id, code, name) VALUES
  (gen_random_uuid(), 'VENDEDOR', 'Vendedor'),
  (gen_random_uuid(), 'CREDITO', 'Crédito'),
  (gen_random_uuid(), 'DESPACHO', 'Despacho'),
  (gen_random_uuid(), 'FACTURACION', 'Facturación'),
  (gen_random_uuid(), 'COBROS', 'Cobros');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES
  ('VENDEDOR', 'customer:create'), ('VENDEDOR', 'customer:update'),
  ('CREDITO', 'customer:activate'), ('CREDITO', 'customer_terms:prepare'),
  ('CONTROLLER', 'customer_terms:approve'), ('CONTROLLER', 'standard_cost:prepare'), ('CONTROLLER', 'price_list:prepare'),
  ('APROBADOR_POLITICAS', 'standard_cost:approve'), ('APROBADOR_POLITICAS', 'price_list:approve'),
  ('DESPACHO', 'fleet:manage'),
  ('VENDEDOR', 'sales:read'), ('CREDITO', 'sales:read'), ('DESPACHO', 'sales:read'), ('FACTURACION', 'sales:read'), ('COBROS', 'sales:read'),
  ('CONTROLLER', 'sales:read'), ('AUDITOR', 'sales:read'), ('DIRECTOR', 'sales:read')
) AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;

INSERT INTO iam.sod_rule (permission_a, permission_b)
SELECT least(a, b), greatest(a, b) FROM (VALUES
  ('customer:create', 'customer:activate'),
  ('customer_terms:prepare', 'customer_terms:approve'),
  ('standard_cost:prepare', 'standard_cost:approve'),
  ('price_list:prepare', 'price_list:approve')
) AS v (a, b);

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['sal.customer_terms_version', 'md.standard_cost_version', 'sal.price_list_version', 'sal.price_list_line',
                           'log.vehicle', 'log.driver'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON sal.customer_terms_version, md.standard_cost_version, sal.price_list_version, sal.price_list_line,
  log.vehicle, log.driver TO rochell_app;
GRANT UPDATE (effective_from, payment_terms_days, credit_limit, credit_hold, status, approved_by) ON sal.customer_terms_version TO rochell_app;
GRANT UPDATE (effective_from, unit_cost, status, approved_by) ON md.standard_cost_version TO rochell_app;
GRANT UPDATE (effective_from, status, approved_by) ON sal.price_list_version TO rochell_app;
GRANT UPDATE (capacity_kg, status, version) ON log.vehicle TO rochell_app;
GRANT UPDATE (full_name, status, version) ON log.driver TO rochell_app;
