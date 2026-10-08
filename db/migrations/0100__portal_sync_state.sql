-- MFG2-02 · reading the machines' portal (E-MFG2-1, E-MFG2-3): the state of the connection per company — the last good read, the last
-- failure and what could not be imported (unpaired machines, moulds, materials, missing recipes), for Producción › Portal and Inicio.

CREATE TABLE mfg.portal_sync_state (
  company_id     uuid        NOT NULL,
  last_ok_at     timestamptz,
  last_error     text,
  last_error_at  timestamptz,
  warnings       jsonb       NOT NULL DEFAULT '[]',
  version        bigint      NOT NULL,
  CONSTRAINT portal_sync_state_pk PRIMARY KEY (company_id),
  CONSTRAINT portal_sync_state_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT portal_sync_state_values CHECK (jsonb_typeof(warnings) = 'array' AND version >= 1 AND (last_error IS NULL OR length(last_error) <= 2000)
    AND (last_error IS NULL) = (last_error_at IS NULL))
);
GRANT SELECT, INSERT ON mfg.portal_sync_state TO rochell_app;
GRANT UPDATE (last_ok_at, last_error, last_error_at, warnings, version) ON mfg.portal_sync_state TO rochell_app;
