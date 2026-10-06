-- USD1-06 · Month-end revaluation and the USD reconciliations (approved errata E-USD1-06-1…5).
--   - a revaluation per month: every open USD payable and every USD bank account at the month's last approved rate, the difference to
--     «Diferencia cambiaria no realizada» (P-43 on the last day), and its exact opposite on the next day (P-43R) (E-USD1-06-1/2);
--   - run by the Contador or the Controller (fx_revaluation:post, step-up); undone — both journals reversed — to redo it (E-USD1-06-3);
--   - IMPORT-CLEARING and FX-REVAL reconciliations; AP-GL also in USD (E-USD1-06-4).

INSERT INTO iam.permission (permission_code, access) VALUES ('fx_revaluation:post', 'WRITE');
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, 'fx_revaluation:post' FROM iam.role r WHERE r.code IN ('CONTADOR', 'CONTROLLER');

CREATE TABLE fin.fx_revaluation (
  revaluation_id     uuid          NOT NULL,
  company_id         uuid          NOT NULL,
  month              date          NOT NULL,
  rate_date          date          NOT NULL,
  rate               numeric(18,4) NOT NULL,
  difference         numeric(19,4) NOT NULL,
  status             text          NOT NULL,
  posted_by          uuid          NOT NULL,
  posting_event_id   uuid          NOT NULL,
  reversal_event_id  uuid          NOT NULL,
  version            bigint        NOT NULL,
  CONSTRAINT fx_revaluation_pk PRIMARY KEY (revaluation_id),
  CONSTRAINT fx_revaluation_company_uq UNIQUE (company_id, revaluation_id),
  CONSTRAINT fx_revaluation_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT fx_revaluation_posted_fk FOREIGN KEY (posted_by) REFERENCES iam.user (user_id),
  CONSTRAINT fx_revaluation_event_fk FOREIGN KEY (company_id, posting_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT fx_revaluation_reversal_fk FOREIGN KEY (company_id, reversal_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT fx_revaluation_month CHECK (month = date_trunc('month', month)::date AND rate_date <= (month + interval '1 month' - interval '1 day')::date),
  CONSTRAINT fx_revaluation_rate CHECK (rate > 0),
  CONSTRAINT fx_revaluation_status CHECK (status IN ('POSTED', 'UNDONE')),
  CONSTRAINT fx_revaluation_version_positive CHECK (version >= 1)
);
CREATE UNIQUE INDEX fx_revaluation_one_per_month ON fin.fx_revaluation (company_id, month) WHERE status = 'POSTED';
CREATE CONSTRAINT TRIGGER fx_revaluation_evidence_on_insert AFTER INSERT ON fin.fx_revaluation
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('FxRevaluation', 'revaluation_id');
CREATE CONSTRAINT TRIGGER fx_revaluation_evidence_on_change AFTER UPDATE ON fin.fx_revaluation
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('FxRevaluation', 'revaluation_id');
CREATE TRIGGER fx_revaluation_no_delete BEFORE DELETE OR TRUNCATE ON fin.fx_revaluation FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();
GRANT SELECT, INSERT ON fin.fx_revaluation TO rochell_app;
GRANT UPDATE (status, version) ON fin.fx_revaluation TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- P-43 / P-43R (E-USD1-06-1/2): the revaluation on the month's last day and its opposite the next day. Each USD payable (subledger AP)
-- and USD bank account (subledger BANK) moves to its USD × rate; the net goes to FX_UNREALIZED. The lines are in pesos only.
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type) VALUES
  ('0192f001-0000-7000-8000-000000000035', 'P-43', 'FxRevaluationPosted'),
  ('0192f001-0000-7000-8000-000000000036', 'P-43R', 'FxRevaluationReversed');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, also_requires_components, effective_from, status) VALUES
  ('0192f001-0000-7000-8000-000000000035', 1,
   '{"lines": [
      {"code": "P43-DR-AP", "side": "DEBIT", "account_role": "AP_FOREIGN", "amount": "ap_decrease", "dimensions": ["party"], "subledger": "AP"},
      {"code": "P43-CR-AP", "side": "CREDIT", "account_role": "AP_FOREIGN", "amount": "ap_increase", "dimensions": ["party"], "subledger": "AP"},
      {"code": "P43-DR-BANK", "side": "DEBIT", "account_role": "BANK", "amount": "bank_increase", "dimensions": [], "subledger": "BANK"},
      {"code": "P43-CR-BANK", "side": "CREDIT", "account_role": "BANK", "amount": "bank_decrease", "dimensions": [], "subledger": "BANK"},
      {"code": "P43-DR-FXU", "side": "DEBIT", "account_role": "FX_UNREALIZED", "amount": "fx_loss", "dimensions": []},
      {"code": "P43-CR-FXU", "side": "CREDIT", "account_role": "FX_UNREALIZED", "amount": "fx_gain", "dimensions": []}
    ]}',
   '{"P43-DR-AP": "Revaluación de {month}: la factura {number} (USD {amount_usd}) baja a la tasa {rate}.",
     "P43-CR-AP": "Revaluación de {month}: la factura {number} (USD {amount_usd}) sube a la tasa {rate}.",
     "P43-DR-BANK": "Revaluación de {month}: la cuenta en dólares (USD {amount_usd}) sube a la tasa {rate}.",
     "P43-CR-BANK": "Revaluación de {month}: la cuenta en dólares (USD {amount_usd}) baja a la tasa {rate}.",
     "P43-DR-FXU": "Revaluación de {month}: pérdida cambiaria no realizada a la tasa {rate}; se reversa el día siguiente.",
     "P43-CR-FXU": "Revaluación de {month}: ganancia cambiaria no realizada a la tasa {rate}; se reversa el día siguiente."}',
   'AP-REC', ARRAY['BANK-REC'], DATE '2026-01-01', 'DRAFT'),
  ('0192f001-0000-7000-8000-000000000036', 1,
   '{"lines": [
      {"code": "P43R-DR-AP", "side": "DEBIT", "account_role": "AP_FOREIGN", "amount": "ap_decrease", "dimensions": ["party"], "subledger": "AP"},
      {"code": "P43R-CR-AP", "side": "CREDIT", "account_role": "AP_FOREIGN", "amount": "ap_increase", "dimensions": ["party"], "subledger": "AP"},
      {"code": "P43R-DR-BANK", "side": "DEBIT", "account_role": "BANK", "amount": "bank_increase", "dimensions": [], "subledger": "BANK"},
      {"code": "P43R-CR-BANK", "side": "CREDIT", "account_role": "BANK", "amount": "bank_decrease", "dimensions": [], "subledger": "BANK"},
      {"code": "P43R-DR-FXU", "side": "DEBIT", "account_role": "FX_UNREALIZED", "amount": "fx_loss", "dimensions": []},
      {"code": "P43R-CR-FXU", "side": "CREDIT", "account_role": "FX_UNREALIZED", "amount": "fx_gain", "dimensions": []}
    ]}',
   '{"P43R-DR-AP": "Reversa de la revaluación de {month}: la factura {number} vuelve a su tasa.",
     "P43R-CR-AP": "Reversa de la revaluación de {month}: la factura {number} vuelve a su tasa.",
     "P43R-DR-BANK": "Reversa de la revaluación de {month}: la cuenta en dólares vuelve a su valor.",
     "P43R-CR-BANK": "Reversa de la revaluación de {month}: la cuenta en dólares vuelve a su valor.",
     "P43R-DR-FXU": "Reversa de la ganancia cambiaria no realizada de {month}.",
     "P43R-CR-FXU": "Reversa de la pérdida cambiaria no realizada de {month}."}',
   'AP-REC', ARRAY['BANK-REC'], DATE '2026-01-01', 'DRAFT');

-- ---------------------------------------------------------------------------------------------
-- Reconciliations (E-USD1-06-4).
-- ---------------------------------------------------------------------------------------------
INSERT INTO acc.policy_parameter_definition (param_code, policy_code, value_type, min_value, max_value, allowed_values, description, label, unit, example, affects) VALUES
  ('import_settlement_alert_days', 'PURCHASING', 'INTEGER', 1, 365, NULL, 'Días que un DUA puede estar sin liquidar antes de avisar (E-USD1-06-4)',
   'Aviso de DUA sin liquidar', 'DAYS', '30 días',
   'Días desde la fecha del DUA sin una liquidación de importación contabilizada a partir de los cuales la conciliación IMPORT-CLEARING avisa.');

INSERT INTO rec.recon_definition (recon_code, description, severity, name, guidance) VALUES
  ('IMPORT-CLEARING',
   'Saldo de «Importaciones por liquidar» por DUA = aranceles y otros cargos de los DUA sin liquidación contabilizada; DUA sin liquidar después del plazo (E-USD1-06-4)',
   'ERROR',
   'Importaciones por liquidar',
   'Comprueba que lo que guarda «Importaciones por liquidar» por cada DUA sea lo que el DUA trajo mientras no tenga una liquidación contabilizada, y cero después. También avisa de los DUA que llevan más días sin liquidar de lo que fija la política de compras.'),
  ('FX-REVAL',
   'Cada mes cerrado con saldos en dólares tiene su revaluación (E-USD1-06-4)',
   'WARNING',
   'Revaluación de saldos en dólares',
   'Avisa de los meses terminados en los que quedaron cuentas por pagar o bancos en dólares con saldo y no se registró la revaluación de fin de mes.');
INSERT INTO rec.recon_blocking (recon_code, component) VALUES ('IMPORT-CLEARING', 'AP-REC');

INSERT INTO rec.recon_classification (classification, name, guidance) VALUES
  ('AP_USD_DIFFERENCE', 'Saldo en dólares de la factura distinto del mayor',
   'Los dólares pendientes de una factura del exterior no coinciden con los dólares de «Proveedores del exterior» de esa factura. Es un error interno: repórtelo a soporte con el documento.'),
  ('IMPORT_CLEARING_DIFFERENCE', 'Importaciones por liquidar distinto del DUA',
   'Lo que «Importaciones por liquidar» guarda para un DUA no es lo que el DUA trajo (sin liquidar) ni cero (liquidado). Revise asientos manuales a esa cuenta y las liquidaciones reversadas.'),
  ('IMPORT_SETTLEMENT_OVERDUE', 'DUA sin liquidar',
   'Un DUA lleva más días sin liquidar de los que permite la política. Prepare la liquidación del embarque en Compras › Liquidaciones de importación.'),
  ('FX_REVALUATION_MISSING', 'Mes sin revaluación de saldos en dólares',
   'El mes terminó con cuentas por pagar o bancos en dólares con saldo y sin revaluación. Regístrela en Contabilidad › Revaluación de saldos en dólares.');
