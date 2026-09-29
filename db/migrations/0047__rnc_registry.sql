-- DGII taxpayer registry (padrón RNC) for looking up customers and suppliers by RNC or cédula. Approved errata E-RNC-1…8.

-- ---------------------------------------------------------------------------------------------
-- E-RNC-1/3: the DGII's weekly "Listado de todos los RNC", replaced whole on each import. Public reference data shared by every
-- company: no company_id and no RLS; the application only reads it (the deployment role imports it, E-RNC-2).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE md.rnc_registry (
  rnc           text NOT NULL,
  legal_name    text NOT NULL,
  trade_name    text,
  activity      text,
  started_on    date,
  status        text NOT NULL,
  regime        text,
  CONSTRAINT rnc_registry_pk PRIMARY KEY (rnc),
  CONSTRAINT rnc_registry_rnc_format CHECK (rnc ~ '^[0-9]{9}$|^[0-9]{11}$'),
  CONSTRAINT rnc_registry_name_present CHECK (length(btrim(legal_name)) > 0),
  CONSTRAINT rnc_registry_status_present CHECK (length(btrim(status)) > 0)
);

CREATE TABLE md.rnc_registry_import (
  import_id    uuid        NOT NULL,
  file_name    text        NOT NULL,
  file_sha256  bytea       NOT NULL,
  source_date  date        NOT NULL,
  row_count    integer     NOT NULL,
  skipped      integer     NOT NULL,
  imported_by  text        NOT NULL,
  imported_at  timestamptz NOT NULL,
  CONSTRAINT rnc_registry_import_pk PRIMARY KEY (import_id),
  CONSTRAINT rnc_registry_import_hash CHECK (octet_length(file_sha256) = 32),
  CONSTRAINT rnc_registry_import_counts CHECK (row_count > 0 AND skipped >= 0)
);
CREATE TRIGGER rnc_registry_import_append_only BEFORE UPDATE OR DELETE ON md.rnc_registry_import FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

GRANT SELECT ON md.rnc_registry, md.rnc_registry_import TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- E-RNC-4: who looks up the registry — the roles that create or review customers and suppliers.
-- ---------------------------------------------------------------------------------------------
INSERT INTO iam.permission (permission_code, access) VALUES ('rnc:read', 'READ');

INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, 'rnc:read' FROM iam.role r
WHERE r.code IN ('VENDEDOR', 'CREDITO', 'COMPRADOR', 'CUENTAS_POR_PAGAR', 'CONTROLLER', 'AUDITOR', 'DIRECTOR');
