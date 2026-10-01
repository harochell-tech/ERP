-- FIS1b-01 · The proforma as a collection document (approved errata E-FIS1b-1…11, E-FIS1b-01-1…14): goods delivered to a customer
-- whose DGII exemption is still in process are collected against a numbered proforma, one per delivery, long before the fiscal
-- invoice (e-CF 44 with the certification, 31 / 32 without it) can be issued. Schema only; the commands follow in FIS1b-02…05.
-- Accounting option A: nothing here posts — the delivery already recognised the unbilled receivable (P-16) and a receipt stays
-- in UNAPPLIED_RECEIPTS until the invoice exists; assigning it to a proforma is a subledger fact.

-- ---------------------------------------------------------------------------------------------
-- E-FIS1b-2, E-FIS1b-01-1: the order says that its deliveries are collected on proformas ("exención en trámite") and whether the
-- customer pays the ITBIS meanwhile. Never a mark on the customer. Both change only while the order is DRAFT.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE sal.sales_order
  ADD COLUMN exemption_pending boolean NOT NULL DEFAULT false,
  ADD COLUMN proforma_collects_itbis boolean,
  ADD CONSTRAINT sales_order_exemption_pending CHECK (exemption_pending = (proforma_collects_itbis IS NOT NULL));
GRANT UPDATE (exemption_pending, proforma_collects_itbis) ON sal.sales_order TO rochell_app;

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
                                   NEW.price_list_version_id, NEW.total_net, NEW.lines_version, NEW.exemption_pending, NEW.proforma_collects_itbis)
                        IS DISTINCT FROM ROW(OLD.plant_id, OLD.order_date, OLD.delivery_term_code, OLD.site_address, OLD.requested_date, OLD.customer_po_ref,
                                   OLD.price_list_version_id, OLD.total_net, OLD.lines_version, OLD.exemption_pending, OLD.proforma_collects_itbis) THEN
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
-- E-FIS1b-1, E-FIS1b-3, E-FIS1b-01-2/3: PF-000001 per company, one per delivered delivery. It carries the delivery's lines at the
-- order price, the ITBIS of the rule in force that day, a due date from the customer's terms and what it collects (the total or
-- the net). OPEN → INVOICED when its fiscal invoice is issued (back to OPEN if that invoice is voided) or VOIDED (E-FIS1b-10).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE sal.proforma (
  proforma_id       uuid          NOT NULL,
  company_id        uuid          NOT NULL,
  proforma_no       text          NOT NULL,
  party_id          uuid          NOT NULL,
  sales_order_id    uuid          NOT NULL,
  delivery_id       uuid          NOT NULL,
  proforma_date     date          NOT NULL,
  due_date          date          NOT NULL,
  collects_itbis    boolean       NOT NULL,
  net_total         numeric(19,4) NOT NULL,
  itbis_total       numeric(19,4) NOT NULL,
  total             numeric(19,4) NOT NULL,
  allocated_amount  numeric(19,4) NOT NULL,
  status            text          NOT NULL,
  invoice_id        uuid,
  issue_event_id    uuid          NOT NULL,
  void_reason       text,
  issued_by         uuid          NOT NULL,
  version           bigint        NOT NULL,
  CONSTRAINT proforma_pk PRIMARY KEY (proforma_id),
  CONSTRAINT proforma_company_uq UNIQUE (company_id, proforma_id),
  CONSTRAINT proforma_no_uq UNIQUE (company_id, proforma_no),
  CONSTRAINT proforma_delivery_uq UNIQUE (company_id, delivery_id),
  CONSTRAINT proforma_party_fk FOREIGN KEY (company_id, party_id) REFERENCES md.party (company_id, party_id),
  CONSTRAINT proforma_order_fk FOREIGN KEY (company_id, sales_order_id) REFERENCES sal.sales_order (company_id, sales_order_id),
  CONSTRAINT proforma_delivery_fk FOREIGN KEY (company_id, delivery_id) REFERENCES log.delivery (company_id, delivery_id),
  CONSTRAINT proforma_invoice_fk FOREIGN KEY (company_id, invoice_id) REFERENCES sal.invoice (company_id, invoice_id),
  CONSTRAINT proforma_issue_event_fk FOREIGN KEY (company_id, issue_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT proforma_issued_by_fk FOREIGN KEY (issued_by) REFERENCES iam.user (user_id),
  CONSTRAINT proforma_no_format CHECK (proforma_no ~ '^PF-[0-9]{6,}$'),
  CONSTRAINT proforma_due CHECK (due_date >= proforma_date),
  CONSTRAINT proforma_totals CHECK (net_total > 0 AND net_total = round(net_total, 2) AND itbis_total >= 0 AND itbis_total = round(itbis_total, 2)
    AND total = net_total + itbis_total),
  CONSTRAINT proforma_allocated CHECK (allocated_amount >= 0 AND allocated_amount = round(allocated_amount, 2)
    AND allocated_amount <= CASE WHEN collects_itbis THEN total ELSE net_total END),
  CONSTRAINT proforma_status CHECK (status IN ('OPEN', 'INVOICED', 'VOIDED')),
  CONSTRAINT proforma_invoiced CHECK ((status = 'INVOICED') = (invoice_id IS NOT NULL)),
  CONSTRAINT proforma_voided CHECK ((status = 'VOIDED') = coalesce(length(btrim(void_reason)) > 0, false)),
  CONSTRAINT proforma_closed_unallocated CHECK (status = 'OPEN' OR allocated_amount = 0),
  CONSTRAINT proforma_version_positive CHECK (version >= 1)
);
CREATE INDEX proforma_party_open ON sal.proforma (company_id, party_id) WHERE status = 'OPEN';

CREATE TABLE sal.proforma_line (
  proforma_line_id  uuid          NOT NULL,
  company_id        uuid          NOT NULL,
  proforma_id       uuid          NOT NULL,
  line_no           integer       NOT NULL,
  delivery_line_id  uuid          NOT NULL,
  item_id           uuid          NOT NULL,
  uom               text          NOT NULL,
  quantity          numeric(18,6) NOT NULL,
  unit_price        numeric(19,4) NOT NULL,
  net_amount        numeric(19,4) NOT NULL,
  itbis_amount      numeric(19,4) NOT NULL,
  CONSTRAINT proforma_line_pk PRIMARY KEY (proforma_line_id),
  CONSTRAINT proforma_line_company_uq UNIQUE (company_id, proforma_line_id),
  CONSTRAINT proforma_line_no_uq UNIQUE (proforma_id, line_no),
  CONSTRAINT proforma_line_delivery_uq UNIQUE (company_id, delivery_line_id),
  CONSTRAINT proforma_line_header_fk FOREIGN KEY (company_id, proforma_id) REFERENCES sal.proforma (company_id, proforma_id),
  CONSTRAINT proforma_line_delivery_fk FOREIGN KEY (company_id, delivery_line_id) REFERENCES log.delivery_line (company_id, delivery_line_id),
  CONSTRAINT proforma_line_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT proforma_line_amounts CHECK (quantity > 0 AND unit_price > 0 AND net_amount > 0 AND net_amount = round(net_amount, 2)
    AND itbis_amount >= 0 AND itbis_amount = round(itbis_amount, 2))
);

CREATE FUNCTION sal.proforma_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'OPEN' OR NEW.allocated_amount <> 0 OR NEW.version <> 1 THEN
      RAISE EXCEPTION 'sal.proforma: a proforma is issued OPEN, with nothing allocated, with version 1';
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'sal.proforma rows cannot be deleted; void the proforma';
  END IF;
  IF ROW(NEW.proforma_id, NEW.company_id, NEW.proforma_no, NEW.party_id, NEW.sales_order_id, NEW.delivery_id, NEW.proforma_date, NEW.due_date,
         NEW.collects_itbis, NEW.net_total, NEW.itbis_total, NEW.total, NEW.issue_event_id, NEW.issued_by)
     IS DISTINCT FROM ROW(OLD.proforma_id, OLD.company_id, OLD.proforma_no, OLD.party_id, OLD.sales_order_id, OLD.delivery_id, OLD.proforma_date, OLD.due_date,
         OLD.collects_itbis, OLD.net_total, OLD.itbis_total, OLD.total, OLD.issue_event_id, OLD.issued_by)
     OR NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'sal.proforma: what was issued is immutable and the version increases by 1';
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
       (OLD.status = 'OPEN' AND NEW.status IN ('INVOICED', 'VOIDED')) OR (OLD.status = 'INVOICED' AND NEW.status = 'OPEN')) THEN
    RAISE EXCEPTION 'sal.proforma: % → % is not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER proforma_guard BEFORE INSERT OR UPDATE OR DELETE ON sal.proforma FOR EACH ROW EXECUTE FUNCTION sal.proforma_guard();
CREATE TRIGGER proforma_no_truncate BEFORE TRUNCATE ON sal.proforma FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ADR-027 for the proforma's status.
CREATE CONSTRAINT TRIGGER proforma_evidence_on_insert AFTER INSERT ON sal.proforma
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('Proforma', 'proforma_id');
CREATE CONSTRAINT TRIGGER proforma_evidence_on_change AFTER UPDATE ON sal.proforma
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('Proforma', 'proforma_id');

CREATE FUNCTION sal.proforma_line_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP <> 'INSERT' THEN
    RAISE EXCEPTION 'sal.proforma_line rows cannot be changed or deleted';
  END IF;
  IF NOT EXISTS (
       SELECT 1 FROM sal.proforma p JOIN log.delivery_line dl ON dl.delivery_id = p.delivery_id
       WHERE p.proforma_id = NEW.proforma_id AND dl.delivery_line_id = NEW.delivery_line_id AND p.xmin = pg_current_xact_id()::xid) THEN
    RAISE EXCEPTION 'sal.proforma_line: lines are written with the proforma, from its own delivery';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER proforma_line_guard BEFORE INSERT OR UPDATE OR DELETE ON sal.proforma_line FOR EACH ROW EXECUTE FUNCTION sal.proforma_line_guard();
CREATE TRIGGER proforma_line_no_truncate BEFORE TRUNCATE ON sal.proforma_line FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- E-FIS1b-4, E-FIS1b-01-4 (option A): a receipt is allocated to proformas of its customer; undoing is an inverse row. The money
-- stays in UNAPPLIED_RECEIPTS (the receipt's unapplied amount does not move): `allocated_amount` only keeps it from being applied
-- or allocated twice. When the invoice is issued the allocations become applications (P-25) and are released here.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE fin.receipt
  ADD COLUMN allocated_amount numeric(19,4) NOT NULL DEFAULT 0,
  ADD CONSTRAINT receipt_allocated CHECK (allocated_amount >= 0 AND allocated_amount = round(allocated_amount, 2) AND allocated_amount <= unapplied_amount
    AND (status = 'RECORDED' OR allocated_amount = 0));
GRANT UPDATE (allocated_amount) ON fin.receipt TO rochell_app;

CREATE TABLE fin.proforma_allocation (
  allocation_id           uuid          NOT NULL,
  company_id              uuid          NOT NULL,
  receipt_id              uuid          NOT NULL,
  proforma_id             uuid          NOT NULL,
  amount                  numeric(19,4) NOT NULL,
  event_id                uuid          NOT NULL,
  reverses_allocation_id  uuid,
  CONSTRAINT proforma_allocation_pk PRIMARY KEY (allocation_id),
  CONSTRAINT proforma_allocation_company_uq UNIQUE (company_id, allocation_id),
  CONSTRAINT proforma_allocation_reverses_uq UNIQUE (reverses_allocation_id),
  CONSTRAINT proforma_allocation_receipt_fk FOREIGN KEY (company_id, receipt_id) REFERENCES fin.receipt (company_id, receipt_id),
  CONSTRAINT proforma_allocation_proforma_fk FOREIGN KEY (company_id, proforma_id) REFERENCES sal.proforma (company_id, proforma_id),
  CONSTRAINT proforma_allocation_event_fk FOREIGN KEY (company_id, event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT proforma_allocation_reverses_fk FOREIGN KEY (company_id, reverses_allocation_id) REFERENCES fin.proforma_allocation (company_id, allocation_id),
  CONSTRAINT proforma_allocation_amount CHECK (amount > 0 AND amount = round(amount, 2)),
  CONSTRAINT proforma_allocation_not_self CHECK (reverses_allocation_id IS DISTINCT FROM allocation_id)
);
CREATE INDEX proforma_allocation_proforma ON fin.proforma_allocation (company_id, proforma_id);
CREATE INDEX proforma_allocation_receipt ON fin.proforma_allocation (company_id, receipt_id);

CREATE FUNCTION fin.proforma_allocation_before_insert() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  original fin.proforma_allocation;
BEGIN
  IF (SELECT party_id FROM sal.proforma WHERE proforma_id = NEW.proforma_id) IS DISTINCT FROM (SELECT party_id FROM fin.receipt WHERE receipt_id = NEW.receipt_id) THEN
    RAISE EXCEPTION 'fin.proforma_allocation: proforma % does not belong to the receipt''s customer', NEW.proforma_id;
  END IF;
  IF NEW.reverses_allocation_id IS NOT NULL THEN
    SELECT * INTO original FROM fin.proforma_allocation WHERE allocation_id = NEW.reverses_allocation_id;
    IF original.reverses_allocation_id IS NOT NULL
       OR ROW(original.receipt_id, original.proforma_id, original.amount) IS DISTINCT FROM ROW(NEW.receipt_id, NEW.proforma_id, NEW.amount) THEN
      RAISE EXCEPTION 'fin.proforma_allocation: a release mirrors one original allocation (same receipt, proforma and amount)';
    END IF;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER proforma_allocation_before_insert BEFORE INSERT ON fin.proforma_allocation FOR EACH ROW EXECUTE FUNCTION fin.proforma_allocation_before_insert();
CREATE TRIGGER proforma_allocation_append_only BEFORE UPDATE OR DELETE ON fin.proforma_allocation FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER proforma_allocation_no_truncate BEFORE TRUNCATE ON fin.proforma_allocation FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- E-FIS1b-5, E-FIS1b-01-8: the proformas a DGII certification cites. Listed while the authorization is DRAFT; its scope is then
-- computed from their lines. An authorization without proformas keeps the manual scope of FIS-1.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE tax.fiscal_authorization_proforma (
  company_id        uuid NOT NULL,
  authorization_id  uuid NOT NULL,
  proforma_id       uuid NOT NULL,
  CONSTRAINT fiscal_authorization_proforma_pk PRIMARY KEY (authorization_id, proforma_id),
  CONSTRAINT fiscal_authorization_proforma_header_fk FOREIGN KEY (company_id, authorization_id) REFERENCES tax.fiscal_authorization (company_id, authorization_id),
  CONSTRAINT fiscal_authorization_proforma_proforma_fk FOREIGN KEY (company_id, proforma_id) REFERENCES sal.proforma (company_id, proforma_id)
);
CREATE INDEX fiscal_authorization_proforma_proforma ON tax.fiscal_authorization_proforma (company_id, proforma_id);

CREATE FUNCTION tax.fiscal_authorization_proforma_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  header tax.fiscal_authorization;
  target uuid := CASE WHEN TG_OP = 'DELETE' THEN OLD.authorization_id ELSE NEW.authorization_id END;
BEGIN
  IF TG_OP = 'UPDATE' THEN
    RAISE EXCEPTION 'tax.fiscal_authorization_proforma rows are not changed; remove and add';
  END IF;
  SELECT * INTO header FROM tax.fiscal_authorization WHERE authorization_id = target;
  IF header.status <> 'DRAFT' THEN
    RAISE EXCEPTION 'tax.fiscal_authorization_proforma: the proformas of a % authorization do not change', header.status;
  END IF;
  IF TG_OP = 'INSERT' AND (SELECT party_id FROM sal.proforma WHERE proforma_id = NEW.proforma_id) IS DISTINCT FROM header.party_id THEN
    RAISE EXCEPTION 'tax.fiscal_authorization_proforma: proforma % belongs to another customer', NEW.proforma_id;
  END IF;
  RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END $$;
CREATE TRIGGER fiscal_authorization_proforma_guard BEFORE INSERT OR UPDATE OR DELETE ON tax.fiscal_authorization_proforma
  FOR EACH ROW EXECUTE FUNCTION tax.fiscal_authorization_proforma_guard();
CREATE TRIGGER fiscal_authorization_proforma_no_truncate BEFORE TRUNCATE ON tax.fiscal_authorization_proforma
  FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['sal.proforma', 'sal.proforma_line', 'fin.proforma_allocation', 'tax.fiscal_authorization_proforma'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON sal.proforma, sal.proforma_line, fin.proforma_allocation, tax.fiscal_authorization_proforma TO rochell_app;
GRANT DELETE ON tax.fiscal_authorization_proforma TO rochell_app;
GRANT UPDATE (allocated_amount, status, invoice_id, void_reason, version) ON sal.proforma TO rochell_app;

-- E-FIS1b-10, E-FIS1b-01-11: voiding a proforma (an order marked by mistake) is Facturación's, like the invoice it stands for.
INSERT INTO iam.permission (permission_code, access) VALUES ('proforma:void', 'WRITE');
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, 'proforma:void' FROM iam.role r WHERE r.code = 'FACTURACION';
