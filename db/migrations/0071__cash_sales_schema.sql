-- CF1-01 · Cash sales to final consumers (approved errata E-CF1-1…14, E-CF1-01-1…9): a buyer without RNC or cédula is sold to
-- through the company's one "Consumidor final" party, always paid in full before anything is dispatched, and invoiced as e-CF 32.
-- Schema only; the commands follow in CF1-02…05. Nothing here posts: a receipt assigned to an order stays in UNAPPLIED_RECEIPTS
-- until the invoice exists (the option A of FIS-1b).

-- ---------------------------------------------------------------------------------------------
-- E-CF1-1, E-CF1-01-1: the final consumer. One party per company, of its own kind, without RNC: a customer, never a supplier,
-- created by the system (the first cash sale creates it through its command, with its events), never edited and without credit
-- terms.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE md.party
  DROP CONSTRAINT party_kind_value,
  ADD CONSTRAINT party_kind_value CHECK (party_kind IN ('LOCAL', 'FOREIGN', 'CONSUMER')),
  DROP CONSTRAINT party_rnc_required,
  ADD CONSTRAINT party_rnc_required CHECK (rnc IS NOT NULL OR party_kind IN ('FOREIGN', 'CONSUMER')),
  ADD CONSTRAINT party_consumer CHECK (party_kind <> 'CONSUMER'
    OR (rnc IS NULL AND is_customer AND NOT is_supplier AND phone IS NULL AND email IS NULL AND address IS NULL));
CREATE UNIQUE INDEX party_consumer_uq ON md.party (company_id) WHERE party_kind = 'CONSUMER';

CREATE FUNCTION md.party_consumer_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.party_kind IS DISTINCT FROM OLD.party_kind AND 'CONSUMER' IN (NEW.party_kind, OLD.party_kind) THEN
    RAISE EXCEPTION 'md.party: a party does not become, or stop being, the final consumer';
  END IF;
  IF OLD.party_kind = 'CONSUMER' AND (NEW.legal_name IS DISTINCT FROM OLD.legal_name OR NEW.customer_status = 'BLOCKED') THEN
    RAISE EXCEPTION 'md.party: the final consumer is not renamed or blocked';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER party_consumer_guard BEFORE UPDATE ON md.party FOR EACH ROW EXECUTE FUNCTION md.party_consumer_guard();

CREATE FUNCTION sal.consumer_has_no_terms() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF (SELECT party_kind FROM md.party WHERE party_id = NEW.party_id) = 'CONSUMER' THEN
    RAISE EXCEPTION 'sal.customer_terms_version: the final consumer has no credit terms; its sales are paid in full (E-CF1-4)';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER customer_terms_not_consumer BEFORE INSERT ON sal.customer_terms_version FOR EACH ROW EXECUTE FUNCTION sal.consumer_has_no_terms();

CREATE FUNCTION md.consumer_has_no_emails() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF (SELECT party_kind FROM md.party WHERE party_id = NEW.party_id) = 'CONSUMER' THEN
    RAISE EXCEPTION 'md.party_email: the final consumer keeps no contact data; the buyer is written on each sale (E-CF1-2)';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER party_email_not_consumer BEFORE INSERT ON md.party_email FOR EACH ROW EXECUTE FUNCTION md.consumer_has_no_emails();

-- ---------------------------------------------------------------------------------------------
-- E-CF1-2, E-CF1-4, E-CF1-9, E-CF1-01-2…4: the cash order. It is the final consumer's kind of order and only its: it carries who
-- bought (name, phone and an identification, all optional here — the amount that makes the identification mandatory is a fiscal
-- rule, checked by the command), what must be paid (`payment_total`: net + the ITBIS of the day it was sent to payment) and what
-- receipts have been assigned to it. DRAFT → PENDING_PAYMENT → CONFIRMED (assigned ≥ payment_total); never through credit, never
-- with an exemption in process.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE sal.sales_order
  ADD COLUMN cash_sale boolean NOT NULL DEFAULT false,
  ADD COLUMN buyer_name text,
  ADD COLUMN buyer_phone text,
  ADD COLUMN buyer_id_kind text,
  ADD COLUMN buyer_id text,
  ADD COLUMN payment_total numeric(19,4),
  ADD COLUMN allocated_amount numeric(19,4) NOT NULL DEFAULT 0,
  DROP CONSTRAINT sales_order_status,
  ADD CONSTRAINT sales_order_status CHECK (status IN ('DRAFT', 'PENDING_CREDIT', 'PENDING_PAYMENT', 'CONFIRMED', 'PARTIALLY_DELIVERED', 'DELIVERED', 'CLOSED', 'CANCELLED')),
  ADD CONSTRAINT sales_order_cash_status CHECK (CASE WHEN cash_sale THEN status <> 'PENDING_CREDIT' ELSE status <> 'PENDING_PAYMENT' END),
  ADD CONSTRAINT sales_order_cash_no_exemption CHECK (NOT (cash_sale AND exemption_pending)),
  ADD CONSTRAINT sales_order_buyer CHECK (cash_sale OR (buyer_name IS NULL AND buyer_phone IS NULL AND buyer_id_kind IS NULL AND buyer_id IS NULL)),
  ADD CONSTRAINT sales_order_buyer_texts CHECK ((buyer_name IS NULL OR length(btrim(buyer_name)) BETWEEN 1 AND 150)
    AND (buyer_phone IS NULL OR length(btrim(buyer_phone)) BETWEEN 1 AND 30)),
  ADD CONSTRAINT sales_order_buyer_id CHECK ((buyer_id_kind IS NULL) = (buyer_id IS NULL) AND CASE buyer_id_kind
    WHEN 'CEDULA' THEN buyer_id ~ '^[0-9]{11}$' WHEN 'RNC' THEN buyer_id ~ '^[0-9]{9}$' WHEN 'PASAPORTE' THEN buyer_id ~ '^[A-Z0-9]{5,20}$'
    ELSE buyer_id_kind IS NULL END),
  ADD CONSTRAINT sales_order_payment_total CHECK ((payment_total IS NULL OR (cash_sale AND payment_total >= total_net AND payment_total = round(payment_total, 2)))
    AND (NOT cash_sale OR status = 'CANCELLED' OR (payment_total IS NULL) = (status = 'DRAFT'))),
  ADD CONSTRAINT sales_order_allocated CHECK (allocated_amount >= 0 AND allocated_amount = round(allocated_amount, 2) AND (cash_sale OR allocated_amount = 0));
GRANT UPDATE (buyer_name, buyer_phone, buyer_id_kind, buyer_id, payment_total, allocated_amount) ON sal.sales_order TO rochell_app;

CREATE OR REPLACE FUNCTION sal.sales_order_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'DRAFT' OR NEW.version <> 1 OR NEW.lines_version <> 1 THEN
      RAISE EXCEPTION 'sal.sales_order: an order is created DRAFT with version 1';
    END IF;
    IF NEW.cash_sale IS DISTINCT FROM ((SELECT party_kind FROM md.party WHERE party_id = NEW.party_id) = 'CONSUMER') THEN
      RAISE EXCEPTION 'sal.sales_order: a cash sale is the order of the final consumer, and only it (E-CF1-4)';
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'sal.sales_order rows cannot be deleted; cancel the order';
  END IF;
  IF ROW(NEW.sales_order_id, NEW.company_id, NEW.order_no, NEW.party_id, NEW.created_by, NEW.cash_sale)
     IS DISTINCT FROM ROW(OLD.sales_order_id, OLD.company_id, OLD.order_no, OLD.party_id, OLD.created_by, OLD.cash_sale)
     OR NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'sal.sales_order: identity columns are immutable and the version increases by 1';
  END IF;
  IF OLD.status <> 'DRAFT' AND ROW(NEW.plant_id, NEW.order_date, NEW.delivery_term_code, NEW.site_address, NEW.requested_date, NEW.customer_po_ref,
                                   NEW.price_list_version_id, NEW.total_net, NEW.lines_version, NEW.exemption_pending, NEW.proforma_collects_itbis,
                                   NEW.buyer_name, NEW.buyer_phone, NEW.buyer_id_kind, NEW.buyer_id)
                        IS DISTINCT FROM ROW(OLD.plant_id, OLD.order_date, OLD.delivery_term_code, OLD.site_address, OLD.requested_date, OLD.customer_po_ref,
                                   OLD.price_list_version_id, OLD.total_net, OLD.lines_version, OLD.exemption_pending, OLD.proforma_collects_itbis,
                                   OLD.buyer_name, OLD.buyer_phone, OLD.buyer_id_kind, OLD.buyer_id) THEN
    RAISE EXCEPTION 'sal.sales_order: only a DRAFT order changes';
  END IF;
  -- What must be paid is fixed when the order is sent to payment and stays until it goes back to DRAFT.
  IF NEW.payment_total IS DISTINCT FROM OLD.payment_total AND NOT (OLD.status = 'DRAFT' OR NEW.status IN ('DRAFT', 'CANCELLED')) THEN
    RAISE EXCEPTION 'sal.sales_order: what must be paid is fixed when the order is sent to payment';
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
       (OLD.status = 'DRAFT' AND NEW.status IN ('PENDING_CREDIT', 'PENDING_PAYMENT', 'CONFIRMED', 'CANCELLED'))
    OR (OLD.status = 'PENDING_CREDIT' AND NEW.status IN ('CONFIRMED', 'DRAFT', 'CANCELLED'))
    OR (OLD.status = 'PENDING_PAYMENT' AND NEW.status IN ('CONFIRMED', 'DRAFT', 'CANCELLED'))
    OR (OLD.status = 'CONFIRMED' AND NEW.status = 'CANCELLED'
        AND NOT EXISTS (SELECT 1 FROM sal.sales_order_line l WHERE l.sales_order_id = OLD.sales_order_id AND l.qty_delivered > 0))
    OR (OLD.status IN ('CONFIRMED', 'PARTIALLY_DELIVERED') AND NEW.status IN ('PARTIALLY_DELIVERED', 'DELIVERED'))
    OR (OLD.status = 'PARTIALLY_DELIVERED' AND NEW.status = 'CLOSED')) THEN
    RAISE EXCEPTION 'sal.sales_order: % → % is not allowed', OLD.status, NEW.status;
  END IF;
  -- E-CF1-4: a cash order is confirmed only from PENDING_PAYMENT and only once the receipts assigned to it cover what must be paid.
  IF NEW.cash_sale AND NEW.status = 'CONFIRMED' AND OLD.status <> 'CONFIRMED'
     AND (OLD.status <> 'PENDING_PAYMENT' OR NEW.allocated_amount < NEW.payment_total) THEN
    RAISE EXCEPTION 'sal.sales_order: a cash order is confirmed when it is paid in full (assigned %, to pay %)', NEW.allocated_amount, NEW.payment_total;
  END IF;
  -- A cancelled or redrafted cash order keeps no receipt assigned.
  IF NEW.cash_sale AND NEW.status IN ('DRAFT', 'CANCELLED') AND NEW.allocated_amount <> 0 THEN
    RAISE EXCEPTION 'sal.sales_order: release the receipts assigned to the order first';
  END IF;
  RETURN NEW;
END $$;

-- ---------------------------------------------------------------------------------------------
-- E-CF1-5, E-CF1-01-5: a receipt assigned to a cash order of its customer; undoing is an inverse row (as fin.proforma_allocation).
-- The receipt's `allocated_amount` counts both kinds, so the same money is never applied or assigned twice. When an invoice of the
-- order is issued the assignments become applications (P-25) and are released here.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fin.order_allocation (
  allocation_id           uuid          NOT NULL,
  company_id              uuid          NOT NULL,
  receipt_id              uuid          NOT NULL,
  sales_order_id          uuid          NOT NULL,
  amount                  numeric(19,4) NOT NULL,
  event_id                uuid          NOT NULL,
  reverses_allocation_id  uuid,
  CONSTRAINT order_allocation_pk PRIMARY KEY (allocation_id),
  CONSTRAINT order_allocation_company_uq UNIQUE (company_id, allocation_id),
  CONSTRAINT order_allocation_reverses_uq UNIQUE (reverses_allocation_id),
  CONSTRAINT order_allocation_receipt_fk FOREIGN KEY (company_id, receipt_id) REFERENCES fin.receipt (company_id, receipt_id),
  CONSTRAINT order_allocation_order_fk FOREIGN KEY (company_id, sales_order_id) REFERENCES sal.sales_order (company_id, sales_order_id),
  CONSTRAINT order_allocation_event_fk FOREIGN KEY (company_id, event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT order_allocation_reverses_fk FOREIGN KEY (company_id, reverses_allocation_id) REFERENCES fin.order_allocation (company_id, allocation_id),
  CONSTRAINT order_allocation_amount CHECK (amount > 0 AND amount = round(amount, 2)),
  CONSTRAINT order_allocation_not_self CHECK (reverses_allocation_id IS DISTINCT FROM allocation_id)
);
CREATE INDEX order_allocation_order ON fin.order_allocation (company_id, sales_order_id);
CREATE INDEX order_allocation_receipt ON fin.order_allocation (company_id, receipt_id);

CREATE FUNCTION fin.order_allocation_before_insert() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  original fin.order_allocation;
  target sal.sales_order;
BEGIN
  SELECT * INTO target FROM sal.sales_order WHERE sales_order_id = NEW.sales_order_id;
  IF NOT target.cash_sale OR target.party_id IS DISTINCT FROM (SELECT party_id FROM fin.receipt WHERE receipt_id = NEW.receipt_id) THEN
    RAISE EXCEPTION 'fin.order_allocation: order % is not a cash order of the receipt''s customer', NEW.sales_order_id;
  END IF;
  IF NEW.reverses_allocation_id IS NOT NULL THEN
    SELECT * INTO original FROM fin.order_allocation WHERE allocation_id = NEW.reverses_allocation_id;
    IF original.reverses_allocation_id IS NOT NULL
       OR ROW(original.receipt_id, original.sales_order_id, original.amount) IS DISTINCT FROM ROW(NEW.receipt_id, NEW.sales_order_id, NEW.amount) THEN
      RAISE EXCEPTION 'fin.order_allocation: a release mirrors one original allocation (same receipt, order and amount)';
    END IF;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER order_allocation_before_insert BEFORE INSERT ON fin.order_allocation FOR EACH ROW EXECUTE FUNCTION fin.order_allocation_before_insert();
CREATE TRIGGER order_allocation_append_only BEFORE UPDATE OR DELETE ON fin.order_allocation FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER order_allocation_no_truncate BEFORE TRUNCATE ON fin.order_allocation FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

ALTER TABLE fin.order_allocation ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON fin.order_allocation
  USING (company_id = nullif(current_setting('app.company_id', true), '')::uuid)
  WITH CHECK (company_id = nullif(current_setting('app.company_id', true), '')::uuid);
GRANT SELECT, INSERT ON fin.order_allocation TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- E-CF1-2, E-CF1-6, E-CF1-01-2: the invoice of a cash order carries its buyer, written once when the invoice is created (the
-- application role holds no UPDATE on these columns). Only an e-CF 32 has one.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE sal.invoice
  ADD COLUMN buyer_name text,
  ADD COLUMN buyer_id_kind text,
  ADD COLUMN buyer_id text,
  ADD CONSTRAINT invoice_buyer CHECK ((buyer_id_kind IS NULL) = (buyer_id IS NULL) AND (buyer_id_kind IS NULL OR buyer_id_kind IN ('CEDULA', 'RNC', 'PASAPORTE'))
    AND (buyer_name IS NULL OR length(btrim(buyer_name)) BETWEEN 1 AND 150)
    AND ((buyer_name IS NULL AND buyer_id IS NULL) OR ecf_type = '32'));

-- E-CF1-6, E-CF1-01-7: the e-CF 32 of the final consumer is recorded without a receiver, or with the buyer's cédula, RNC or
-- passport. Every other invoice keeps a receiver RNC.
ALTER TABLE tax.external_fiscal_record
  ALTER COLUMN receiver_rnc DROP NOT NULL,
  ADD COLUMN receiver_passport text,
  ADD CONSTRAINT external_fiscal_record_receiver CHECK ((receiver_rnc IS NULL OR receiver_rnc ~ '^([0-9]{9}|[0-9]{11})$')
    AND (receiver_passport IS NULL OR (receiver_passport ~ '^[A-Z0-9]{5,20}$' AND receiver_rnc IS NULL)));

CREATE FUNCTION tax.external_fiscal_record_receiver() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.receiver_rnc IS NULL AND NOT EXISTS (
       SELECT 1 FROM sal.invoice i JOIN md.party p ON p.party_id = i.party_id
       WHERE i.invoice_id = NEW.invoice_id AND i.ecf_type = '32' AND p.party_kind = 'CONSUMER') THEN
    RAISE EXCEPTION 'tax.external_fiscal_record: only the e-CF 32 of the final consumer is recorded without a receiver RNC (E-CF1-01-7)';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER external_fiscal_record_receiver BEFORE INSERT ON tax.external_fiscal_record FOR EACH ROW EXECUTE FUNCTION tax.external_fiscal_record_receiver();

-- ---------------------------------------------------------------------------------------------
-- E-CF1-3, E-CF1-01-6: the amount from which the buyer's identification is mandatory is a fiscal rule with its official source,
-- never a number in code. Without the rule in force no sale to the final consumer is confirmed.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE tax.fiscal_rule DROP CONSTRAINT fiscal_rule_kind,
  ADD CONSTRAINT fiscal_rule_kind CHECK (rule_kind IN ('PURCHASE_ITBIS', 'PURCHASE_WITHHOLDING', 'SALES_ITBIS', 'REPORT_606_CLASSIFICATION', 'CONSUMER_ID_THRESHOLD'));

-- ---------------------------------------------------------------------------------------------
-- E-CF1-11: the counter. One permission to make a cash sale, held by the Vendedor and by the new role Caja, who also records the
-- payment and assigns it (the deposit and the match with the bank stay with Cobros and Tesorería). SUPERADMIN receives the
-- permission through its trigger (E-ADM-2).
-- ---------------------------------------------------------------------------------------------
INSERT INTO iam.permission (permission_code, access) VALUES ('cash_sale:create', 'WRITE');
INSERT INTO iam.role (role_id, code, name, description)
VALUES (gen_random_uuid(), 'CAJA', 'Caja', 'Hace ventas de contado a consumidor final y registra y asigna su cobro; no deposita ni concilia con el banco.');
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES ('VENDEDOR', 'cash_sale:create'), ('CAJA', 'cash_sale:create'), ('CAJA', 'sales:read'), ('CAJA', 'receipt:record'), ('CAJA', 'receipt:apply')) AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;
