-- FIS1-01 · Fiscal authorizations (DGII exemption certificates for CONFOTUR projects), their scope, documents and consumption,
-- e-CF 44 on invoices, permissions. Frozen Baseline FIS-1 §2 and §4; approved errata E-FIS1-1…16 and E-FIS1-01-1…10. The
-- commands arrive with FIS1-02 and FIS1-03 (E-FIS1-01-1).

-- ---------------------------------------------------------------------------------------------
-- E-FIS1-01-3, E-FIS1-01-8: one authorization per DGII certificate; the verifier differs from the registrar.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE tax.fiscal_authorization (
  authorization_id        uuid   NOT NULL,
  company_id              uuid   NOT NULL,
  party_id                uuid   NOT NULL,
  regime                  text   NOT NULL,
  certificate_no          text   NOT NULL,
  issued_on               date   NOT NULL,
  valid_until             date,
  project_name            text   NOT NULL,
  confotur_resolution_no  text   NOT NULL,
  project_term_ends_on    date,
  sales_order_id          uuid,
  status                  text   NOT NULL,
  registered_by           uuid   NOT NULL,
  verified_by             uuid,
  version                 bigint NOT NULL,
  CONSTRAINT fiscal_authorization_pk PRIMARY KEY (authorization_id),
  CONSTRAINT fiscal_authorization_company_uq UNIQUE (company_id, authorization_id),
  CONSTRAINT fiscal_authorization_certificate_uq UNIQUE (company_id, certificate_no),
  CONSTRAINT fiscal_authorization_party_fk FOREIGN KEY (company_id, party_id) REFERENCES md.party (company_id, party_id),
  CONSTRAINT fiscal_authorization_order_fk FOREIGN KEY (company_id, sales_order_id) REFERENCES sal.sales_order (company_id, sales_order_id),
  CONSTRAINT fiscal_authorization_registered_fk FOREIGN KEY (registered_by) REFERENCES iam.user (user_id),
  CONSTRAINT fiscal_authorization_verified_fk FOREIGN KEY (verified_by) REFERENCES iam.user (user_id),
  CONSTRAINT fiscal_authorization_regime CHECK (regime IN ('CONFOTUR')),
  CONSTRAINT fiscal_authorization_certificate CHECK (length(btrim(certificate_no)) BETWEEN 1 AND 60),
  CONSTRAINT fiscal_authorization_project CHECK (length(btrim(project_name)) BETWEEN 1 AND 200 AND length(btrim(confotur_resolution_no)) BETWEEN 1 AND 60),
  CONSTRAINT fiscal_authorization_validity CHECK (valid_until IS NULL OR valid_until >= issued_on),
  CONSTRAINT fiscal_authorization_status CHECK (status IN ('DRAFT', 'PENDING_VERIFICATION', 'ACTIVE', 'SUSPENDED', 'EXHAUSTED', 'EXPIRED', 'REJECTED')),
  CONSTRAINT fiscal_authorization_four_eyes CHECK (verified_by IS NULL OR verified_by <> registered_by),
  CONSTRAINT fiscal_authorization_verified CHECK ((status IN ('ACTIVE', 'SUSPENDED', 'EXHAUSTED', 'EXPIRED')) = (verified_by IS NOT NULL)),
  CONSTRAINT fiscal_authorization_version_positive CHECK (version >= 1)
);

CREATE FUNCTION tax.fiscal_authorization_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'DRAFT' OR NEW.version <> 1 THEN
      RAISE EXCEPTION 'tax.fiscal_authorization: registered as DRAFT with version 1';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM md.party p WHERE p.party_id = NEW.party_id AND p.is_customer AND p.rnc IS NOT NULL) THEN
      RAISE EXCEPTION 'tax.fiscal_authorization: party % is not a customer with an RNC', NEW.party_id;
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'tax.fiscal_authorization rows cannot be deleted';
  END IF;
  IF ROW(NEW.authorization_id, NEW.company_id, NEW.party_id, NEW.regime, NEW.registered_by)
     IS DISTINCT FROM ROW(OLD.authorization_id, OLD.company_id, OLD.party_id, OLD.regime, OLD.registered_by)
     OR NEW.version <> OLD.version + 1
     OR NOT ((OLD.status = NEW.status AND OLD.status = 'DRAFT')
             OR (OLD.status = 'DRAFT' AND NEW.status = 'PENDING_VERIFICATION')
             OR (OLD.status = 'PENDING_VERIFICATION' AND NEW.status IN ('DRAFT', 'ACTIVE', 'REJECTED'))
             OR (OLD.status = 'ACTIVE' AND NEW.status IN ('ACTIVE', 'SUSPENDED', 'EXHAUSTED', 'EXPIRED'))
             OR (OLD.status = 'SUSPENDED' AND NEW.status IN ('ACTIVE', 'EXPIRED'))
             OR (OLD.status = 'EXHAUSTED' AND NEW.status IN ('ACTIVE', 'EXHAUSTED', 'EXPIRED')))
     OR (OLD.status NOT IN ('DRAFT') AND ROW(NEW.certificate_no, NEW.issued_on, NEW.valid_until, NEW.project_name, NEW.confotur_resolution_no, NEW.project_term_ends_on, NEW.sales_order_id)
                                         IS DISTINCT FROM ROW(OLD.certificate_no, OLD.issued_on, OLD.valid_until, OLD.project_name, OLD.confotur_resolution_no, OLD.project_term_ends_on, OLD.sales_order_id)) THEN
    RAISE EXCEPTION 'tax.fiscal_authorization: % → % or a change of a submitted authorization is not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER fiscal_authorization_guard BEFORE INSERT OR UPDATE OR DELETE ON tax.fiscal_authorization FOR EACH ROW EXECUTE FUNCTION tax.fiscal_authorization_guard();
CREATE TRIGGER fiscal_authorization_no_truncate BEFORE TRUNCATE ON tax.fiscal_authorization FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
CREATE CONSTRAINT TRIGGER fiscal_authorization_evidence_on_insert AFTER INSERT ON tax.fiscal_authorization
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('FiscalAuthorization', 'authorization_id');
CREATE CONSTRAINT TRIGGER fiscal_authorization_evidence_on_change AFTER UPDATE ON tax.fiscal_authorization
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('FiscalAuthorization', 'authorization_id');

-- ---------------------------------------------------------------------------------------------
-- E-FIS1-01-4: the scope — finished good, unit, authorized quantity and net amount, and what has been consumed of each.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE tax.fiscal_authorization_line (
  authorization_id  uuid          NOT NULL,
  line_no           integer       NOT NULL,
  company_id        uuid          NOT NULL,
  item_id           uuid          NOT NULL,
  uom               text          NOT NULL,
  qty_authorized    numeric(18,6) NOT NULL,
  net_authorized    numeric(19,4) NOT NULL,
  qty_consumed      numeric(18,6) NOT NULL,
  net_consumed      numeric(19,4) NOT NULL,
  CONSTRAINT fiscal_authorization_line_pk PRIMARY KEY (authorization_id, line_no),
  CONSTRAINT fiscal_authorization_line_item_uq UNIQUE (authorization_id, item_id, uom),
  CONSTRAINT fiscal_authorization_line_header_fk FOREIGN KEY (company_id, authorization_id) REFERENCES tax.fiscal_authorization (company_id, authorization_id),
  CONSTRAINT fiscal_authorization_line_item_fk FOREIGN KEY (company_id, item_id) REFERENCES md.item (company_id, item_id),
  CONSTRAINT fiscal_authorization_line_uom_fk FOREIGN KEY (uom) REFERENCES md.uom (uom_code),
  CONSTRAINT fiscal_authorization_line_no_positive CHECK (line_no >= 1),
  CONSTRAINT fiscal_authorization_line_authorized CHECK (qty_authorized > 0 AND net_authorized > 0 AND net_authorized = round(net_authorized, 2)),
  CONSTRAINT fiscal_authorization_line_consumed CHECK (qty_consumed BETWEEN 0 AND qty_authorized AND net_consumed BETWEEN 0 AND net_authorized)
);

CREATE FUNCTION tax.fiscal_authorization_line_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  header_status text;
BEGIN
  SELECT status INTO header_status FROM tax.fiscal_authorization
  WHERE authorization_id = CASE WHEN TG_OP = 'DELETE' THEN OLD.authorization_id ELSE NEW.authorization_id END;
  IF TG_OP = 'INSERT' THEN
    IF header_status <> 'DRAFT' THEN
      RAISE EXCEPTION 'tax.fiscal_authorization_line: lines are added only to a DRAFT authorization';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM md.item i WHERE i.item_id = NEW.item_id AND i.item_type = 'FINISHED_GOOD') THEN
      RAISE EXCEPTION 'tax.fiscal_authorization_line: item % is not a finished good', NEW.item_id;
    END IF;
    IF NEW.qty_consumed <> 0 OR NEW.net_consumed <> 0 THEN
      RAISE EXCEPTION 'tax.fiscal_authorization_line: a new line has nothing consumed';
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    IF header_status <> 'DRAFT' THEN
      RAISE EXCEPTION 'tax.fiscal_authorization_line: lines are removed only from a DRAFT authorization';
    END IF;
    RETURN OLD;
  END IF;
  IF ROW(NEW.authorization_id, NEW.line_no, NEW.company_id, NEW.item_id, NEW.uom, NEW.qty_authorized, NEW.net_authorized)
     IS DISTINCT FROM ROW(OLD.authorization_id, OLD.line_no, OLD.company_id, OLD.item_id, OLD.uom, OLD.qty_authorized, OLD.net_authorized) THEN
    RAISE EXCEPTION 'tax.fiscal_authorization_line: only the consumption of a line changes (a scope change is a DRAFT edit)';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER fiscal_authorization_line_guard BEFORE INSERT OR UPDATE OR DELETE ON tax.fiscal_authorization_line
  FOR EACH ROW EXECUTE FUNCTION tax.fiscal_authorization_line_guard();

-- ---------------------------------------------------------------------------------------------
-- E-FIS1-01-5: the evidence (reference + SHA-256), never deleted.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE tax.fiscal_authorization_document (
  document_id       uuid        NOT NULL,
  company_id        uuid        NOT NULL,
  authorization_id  uuid        NOT NULL,
  kind              text        NOT NULL,
  evidence_ref      text        NOT NULL,
  evidence_sha256   text        NOT NULL,
  added_by          uuid        NOT NULL,
  added_at          timestamptz NOT NULL,
  CONSTRAINT fiscal_authorization_document_pk PRIMARY KEY (document_id),
  CONSTRAINT fiscal_authorization_document_header_fk FOREIGN KEY (company_id, authorization_id) REFERENCES tax.fiscal_authorization (company_id, authorization_id),
  CONSTRAINT fiscal_authorization_document_added_fk FOREIGN KEY (added_by) REFERENCES iam.user (user_id),
  CONSTRAINT fiscal_authorization_document_kind CHECK (kind IN ('CERTIFICADO_DGII', 'RESOLUCION_CONFOTUR', 'LISTA_MATERIALES', 'PROFORMA')),
  CONSTRAINT fiscal_authorization_document_ref CHECK (length(btrim(evidence_ref)) BETWEEN 1 AND 300),
  CONSTRAINT fiscal_authorization_document_sha CHECK (evidence_sha256 ~ '^[0-9a-f]{64}$')
);
CREATE TRIGGER fiscal_authorization_document_immutable BEFORE UPDATE OR DELETE ON tax.fiscal_authorization_document
  FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- E-FIS1-01-6: consumption per exempt invoice line; a release is an inverse row, never an edit.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE tax.fiscal_authorization_consumption (
  consumption_id           uuid          NOT NULL,
  company_id               uuid          NOT NULL,
  authorization_id         uuid          NOT NULL,
  line_no                  integer       NOT NULL,
  invoice_line_id          uuid          NOT NULL,
  qty                      numeric(18,6) NOT NULL,
  net                      numeric(19,4) NOT NULL,
  reverses_consumption_id  uuid,
  event_id                 uuid          NOT NULL,
  CONSTRAINT fiscal_authorization_consumption_pk PRIMARY KEY (consumption_id),
  CONSTRAINT fiscal_authorization_consumption_reverses_uq UNIQUE (reverses_consumption_id),
  CONSTRAINT fiscal_authorization_consumption_line_fk FOREIGN KEY (authorization_id, line_no) REFERENCES tax.fiscal_authorization_line (authorization_id, line_no),
  CONSTRAINT fiscal_authorization_consumption_invoice_line_fk FOREIGN KEY (company_id, invoice_line_id) REFERENCES sal.invoice_line (company_id, invoice_line_id),
  CONSTRAINT fiscal_authorization_consumption_reverses_fk FOREIGN KEY (reverses_consumption_id) REFERENCES tax.fiscal_authorization_consumption (consumption_id),
  CONSTRAINT fiscal_authorization_consumption_event_fk FOREIGN KEY (company_id, event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT fiscal_authorization_consumption_amounts CHECK (qty > 0 AND net > 0)
);
CREATE TRIGGER fiscal_authorization_consumption_immutable BEFORE UPDATE OR DELETE ON tax.fiscal_authorization_consumption
  FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- E-FIS1-01-7: e-CF 44 ⇔ a fiscal authorization, and no ITBIS on it.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE sal.invoice
  ADD COLUMN fiscal_authorization_id uuid,
  ADD CONSTRAINT invoice_fiscal_authorization_fk FOREIGN KEY (company_id, fiscal_authorization_id) REFERENCES tax.fiscal_authorization (company_id, authorization_id),
  DROP CONSTRAINT invoice_ecf_type,
  ADD CONSTRAINT invoice_ecf_type CHECK (ecf_type IN ('31', '32', '44')),
  ADD CONSTRAINT invoice_exempt CHECK ((ecf_type = '44') = (fiscal_authorization_id IS NOT NULL) AND (ecf_type <> '44' OR tax_total IS NULL OR tax_total = 0));
GRANT UPDATE (fiscal_authorization_id) ON sal.invoice TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- E-FIS1-01-9: permissions and segregation of duties.
-- ---------------------------------------------------------------------------------------------
INSERT INTO iam.permission (permission_code, access) VALUES
  ('fiscal_authorization:register', 'WRITE'), ('fiscal_authorization:verify', 'WRITE'), ('fiscal_authorization:suspend', 'WRITE');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES
  ('CREDITO', 'fiscal_authorization:register'), ('FACTURACION', 'fiscal_authorization:register'),
  ('ESPECIALISTA_FISCAL', 'fiscal_authorization:verify'), ('ESPECIALISTA_FISCAL', 'fiscal_authorization:suspend'), ('ESPECIALISTA_FISCAL', 'sales:read')
) AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;

INSERT INTO iam.sod_rule (permission_a, permission_b) VALUES ('fiscal_authorization:register', 'fiscal_authorization:verify');

-- ---------------------------------------------------------------------------------------------
-- Row-level security and privileges.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['tax.fiscal_authorization', 'tax.fiscal_authorization_line', 'tax.fiscal_authorization_document', 'tax.fiscal_authorization_consumption'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON tax.fiscal_authorization, tax.fiscal_authorization_line, tax.fiscal_authorization_document, tax.fiscal_authorization_consumption TO rochell_app;
GRANT DELETE ON tax.fiscal_authorization_line TO rochell_app;
GRANT UPDATE (certificate_no, issued_on, valid_until, project_name, confotur_resolution_no, project_term_ends_on, sales_order_id, status, verified_by, version)
  ON tax.fiscal_authorization TO rochell_app;
GRANT UPDATE (qty_consumed, net_consumed) ON tax.fiscal_authorization_line TO rochell_app;
