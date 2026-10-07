-- VS4-02 · e-CF gateway: queue, status polling and contingency (approved errata E-VS4-02-1…7).
--   - tax.ecf_gateway_state: per company, Alanube's health as the gateway sees it — consecutive failures since when, the last good
--     answer, and whether the company is in contingency (E-VS4-02-4);
--   - REVENUE_ACCOUNTING ecf_contingency_minutes (30): minutes without an answer before pending e-CF move to contingency, with the
--     new parameter unit MINUTES;
--   - ecf:process for the daily process (PROCESO_DIARIO): the server's worker sends, polls and takes webhook nudges (E-VS4-02-1/5).

CREATE TABLE tax.ecf_gateway_state (
  company_id             uuid        NOT NULL,
  consecutive_failures   integer     NOT NULL,
  failing_since          timestamptz,
  last_ok_at             timestamptz,
  in_contingency         boolean     NOT NULL,
  version                bigint      NOT NULL,
  CONSTRAINT ecf_gateway_state_pk PRIMARY KEY (company_id),
  CONSTRAINT ecf_gateway_state_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT ecf_gateway_state_failures CHECK (consecutive_failures >= 0 AND (consecutive_failures = 0) = (failing_since IS NULL) AND version >= 1),
  CONSTRAINT ecf_gateway_state_contingency CHECK (NOT in_contingency OR failing_since IS NOT NULL)
);
GRANT SELECT, INSERT ON tax.ecf_gateway_state TO rochell_app;
GRANT UPDATE (consecutive_failures, failing_since, last_ok_at, in_contingency, version) ON tax.ecf_gateway_state TO rochell_app;

ALTER TABLE acc.policy_parameter_definition DROP CONSTRAINT policy_parameter_definition_unit,
  ADD CONSTRAINT policy_parameter_definition_unit CHECK (unit IS NULL OR unit IN ('PERCENT', 'AMOUNT', 'DAYS', 'HOURS', 'MINUTES', 'OPTION'));

INSERT INTO acc.policy_parameter_definition (param_code, policy_code, value_type, min_value, max_value, allowed_values, description, label, unit, example, affects) VALUES
  ('ecf_contingency_minutes', 'REVENUE_ACCOUNTING', 'INTEGER', 5, 1440, NULL,
   'Minutos sin respuesta de Alanube antes de pasar los e-CF pendientes a contingencia (E-VS4-02-4)',
   'Contingencia del e-CF', 'MINUTES', '30 minutos',
   'Si Alanube no responde durante estos minutos seguidos, los e-CF pendientes pasan a contingencia y Inicio avisa a Facturación y Fiscal; vuelven a la cola solos cuando Alanube responde.');

INSERT INTO iam.permission (permission_code, access) VALUES ('ecf:process', 'WRITE');
INSERT INTO iam.role_permission (role_id, permission_code) SELECT role_id, 'ecf:process' FROM iam.role WHERE code = 'PROCESO_DIARIO';
