-- PRT-01 · print formats (approved errata E-PRT-1…10, E-PRT-01-1…8).
--   - One format per document type and company, kept as versions (E-PRT-6): DRAFT while it is edited, ACTIVE the one that prints
--     (at most one per type), RETIRED once replaced; a company without an ACTIVE version prints the built-in «Rochell» format.
--   - The body is a Liquid template and the css its own styles (E-PRT-01-1); settings keeps what the simple screen chose (PRT-02).
--   - One logo per company (E-PRT-8): PNG or JPEG up to 1 MB, its SHA-256 and who set it.
--   - print_format:manage for the Director (E-PRT-9); the Superadministrador holds every permission.

CREATE TABLE md.print_format (
  company_id     uuid        NOT NULL,
  document_type  text        NOT NULL,
  version        integer     NOT NULL,
  status         text        NOT NULL,
  settings       jsonb       NOT NULL DEFAULT '{}',
  body           text        NOT NULL,
  css            text        NOT NULL DEFAULT '',
  note           text,
  created_by     uuid        NOT NULL,
  created_at     timestamptz NOT NULL,
  activated_by   uuid,
  activated_at   timestamptz,
  CONSTRAINT print_format_pk PRIMARY KEY (company_id, document_type, version),
  CONSTRAINT print_format_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT print_format_created_by_fk FOREIGN KEY (created_by) REFERENCES iam.user (user_id),
  CONSTRAINT print_format_activated_by_fk FOREIGN KEY (activated_by) REFERENCES iam.user (user_id),
  CONSTRAINT print_format_type CHECK (document_type IN ('DELIVERY_NOTE', 'INVOICE', 'CREDIT_NOTE', 'QUOTE', 'PROFORMA', 'ORDER_PROFORMA', 'STATEMENT', 'AR_AGING',
                                                         'RECEIPT', 'CUSTOMER_REFUND', 'PURCHASE_ORDER')),
  CONSTRAINT print_format_version CHECK (version >= 1),
  CONSTRAINT print_format_status CHECK (status IN ('DRAFT', 'ACTIVE', 'RETIRED')),
  CONSTRAINT print_format_activation CHECK ((status = 'DRAFT') = (activated_at IS NULL) AND (activated_at IS NULL) = (activated_by IS NULL)),
  CONSTRAINT print_format_body CHECK (length(body) BETWEEN 1 AND 200000 AND length(css) <= 100000),
  CONSTRAINT print_format_note CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 500)
);
CREATE UNIQUE INDEX print_format_one_active ON md.print_format (company_id, document_type) WHERE status = 'ACTIVE';
GRANT SELECT, INSERT ON md.print_format TO rochell_app;
GRANT UPDATE (status, settings, body, css, note, activated_by, activated_at) ON md.print_format TO rochell_app;

CREATE TABLE md.company_logo (
  company_id    uuid        NOT NULL,
  content       bytea       NOT NULL,
  content_type  text        NOT NULL,
  sha256        bytea       NOT NULL,
  set_by        uuid        NOT NULL,
  set_at        timestamptz NOT NULL,
  version       bigint      NOT NULL,
  CONSTRAINT company_logo_pk PRIMARY KEY (company_id),
  CONSTRAINT company_logo_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT company_logo_set_by_fk FOREIGN KEY (set_by) REFERENCES iam.user (user_id),
  CONSTRAINT company_logo_type CHECK (content_type IN ('image/png', 'image/jpeg')),
  CONSTRAINT company_logo_size CHECK (octet_length(content) BETWEEN 1 AND 1048576 AND octet_length(sha256) = 32),
  CONSTRAINT company_logo_version CHECK (version >= 1)
);
GRANT SELECT, INSERT ON md.company_logo TO rochell_app;
GRANT UPDATE (content, content_type, sha256, set_by, set_at, version) ON md.company_logo TO rochell_app;

INSERT INTO iam.permission (permission_code, access) VALUES ('print_format:manage', 'WRITE');
INSERT INTO iam.role_permission (role_id, permission_code) SELECT role_id, 'print_format:manage' FROM iam.role WHERE code = 'DIRECTOR';

-- E-PRT-7: an e-mailed document records the format version it was drawn with (0: the built-in «Rochell» format); its HTML and PDF are
-- kept on the message already.
ALTER TABLE core.mail_message
  ADD COLUMN print_format_version integer,
  ADD CONSTRAINT mail_message_print_format_version CHECK (print_format_version IS NULL OR print_format_version >= 0);
