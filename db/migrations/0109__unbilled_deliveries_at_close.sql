-- X1-01b · deliveries of the month not invoiced hold its close (approved errata E-X1-3, E-X1-01-6, E-X1-01b-1/2).
--   - CONTRACT-ASSET reports UNBILLED_AT_CLOSE (ERROR, blocks AR-REC) for each delivery line with control transferred by a month-end
--     cutoff and not yet invoiced, unless the Controller accepted it for that month;
--   - sal.unbilled_acceptance / _line: the Controller's acceptance of a month, with its reason and the lines it covered then
--     (a line that appears later holds the close again), append-only;
--   - unbilled_delivery:accept for the Controller (156 permissions).

CREATE TABLE sal.unbilled_acceptance (
  acceptance_id  uuid        NOT NULL,
  company_id     uuid        NOT NULL,
  period_id      uuid        NOT NULL,
  reason         text        NOT NULL,
  accepted_by    uuid        NOT NULL,
  accepted_at    timestamptz NOT NULL,
  event_id       uuid        NOT NULL,
  CONSTRAINT unbilled_acceptance_pk PRIMARY KEY (acceptance_id),
  CONSTRAINT unbilled_acceptance_company_uq UNIQUE (company_id, acceptance_id),
  CONSTRAINT unbilled_acceptance_period_fk FOREIGN KEY (company_id, period_id) REFERENCES fin.period (company_id, period_id),
  CONSTRAINT unbilled_acceptance_by_fk FOREIGN KEY (accepted_by) REFERENCES iam.user (user_id),
  CONSTRAINT unbilled_acceptance_event_fk FOREIGN KEY (company_id, event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT unbilled_acceptance_reason CHECK (length(btrim(reason)) BETWEEN 3 AND 500)
);

CREATE TABLE sal.unbilled_acceptance_line (
  acceptance_id     uuid          NOT NULL,
  company_id        uuid          NOT NULL,
  delivery_line_id  uuid          NOT NULL,
  unbilled_amount   numeric(19,4) NOT NULL,
  CONSTRAINT unbilled_acceptance_line_pk PRIMARY KEY (acceptance_id, delivery_line_id),
  CONSTRAINT unbilled_acceptance_line_acceptance_fk FOREIGN KEY (company_id, acceptance_id) REFERENCES sal.unbilled_acceptance (company_id, acceptance_id),
  CONSTRAINT unbilled_acceptance_line_delivery_fk FOREIGN KEY (company_id, delivery_line_id) REFERENCES log.delivery_line (company_id, delivery_line_id),
  CONSTRAINT unbilled_acceptance_line_amount CHECK (unbilled_amount > 0)
);

CREATE TRIGGER unbilled_acceptance_append_only BEFORE UPDATE OR DELETE ON sal.unbilled_acceptance FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER unbilled_acceptance_line_append_only BEFORE UPDATE OR DELETE ON sal.unbilled_acceptance_line FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

INSERT INTO rec.recon_classification (classification, name, guidance) VALUES
  ('UNBILLED_AT_CLOSE', 'Entregado en el mes sin facturar',
   'La línea se entregó dentro del mes que se cierra y no se ha facturado: el ITBIS nace con la entrega (art. 338.1). Facture la entrega desde Facturación › Por facturar, o el Controller acepta los conduces del mes con un motivo.');

INSERT INTO iam.permission (permission_code, access) VALUES ('unbilled_delivery:accept', 'WRITE');
INSERT INTO iam.role_permission (role_id, permission_code) SELECT role_id, 'unbilled_delivery:accept' FROM iam.role WHERE code = 'CONTROLLER';

DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['sal.unbilled_acceptance', 'sal.unbilled_acceptance_line'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid) '
      'WITH CHECK (company_id = nullif(current_setting(''app.company_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON sal.unbilled_acceptance, sal.unbilled_acceptance_line TO rochell_app;
