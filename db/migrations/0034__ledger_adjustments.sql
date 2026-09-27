-- FIN1-02 · Adjustment journal commands and the ACR close. E-FIN1-1…10, E-FIN1-01-3.
INSERT INTO rec.recon_definition (recon_code, description, severity) VALUES
  ('MANUAL-EVIDENCE', 'Cada ajuste contabilizado con su asiento MANUAL_ADJUSTMENT (y su reversa si aplica); ningún asiento sin regla fuera de un ajuste (E-FIN1-01-3)', 'ERROR'),
  ('TB-BALANCED', 'La balanza cuadra: débitos = créditos por asiento y en total (E-FIN1-01-3)', 'ERROR');

INSERT INTO rec.recon_blocking (recon_code, component) VALUES
  ('MANUAL-EVIDENCE', 'ACR-NTX'), ('MANUAL-EVIDENCE', 'ACR-TAX'), ('TB-BALANCED', 'ACR-NTX'), ('TB-BALANCED', 'ACR-TAX');

-- 0033's check of rule-less journals named its variable like the column (42702 "component is ambiguous"): same logic, renamed.
CREATE OR REPLACE FUNCTION fin.gl_journal_component_open() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  adjustment_component text;
BEGIN
  IF NEW.posting_rule_id IS NULL THEN
    SELECT m.close_component INTO adjustment_component FROM fin.manual_journal m
    WHERE m.company_id = NEW.company_id AND m.posting_event_id = coalesce(
      (SELECT o.source_event_id FROM fin.gl_journal o WHERE o.journal_id = NEW.reverses_journal_id), NEW.source_event_id);
    IF adjustment_component IS NULL THEN
      RAISE EXCEPTION 'fin.gl_journal %: a journal without a posting rule belongs to an adjustment', NEW.journal_id;
    END IF;
    IF EXISTS (SELECT 1 FROM fin.close_component_state s WHERE s.period_id = NEW.period_id AND s.component = adjustment_component AND s.status = 'CLOSED') THEN
      RAISE EXCEPTION 'fin.gl_journal %: % is CLOSED in its period', NEW.journal_id, adjustment_component;
    END IF;
    RETURN NEW;
  END IF;
  IF EXISTS (
       SELECT 1
       FROM fin.posting_rule_version v
       JOIN fin.close_component_state s ON s.period_id = NEW.period_id
         AND (s.component = v.close_component OR s.component = ANY (v.also_requires_components))
       WHERE v.posting_rule_id = NEW.posting_rule_id AND v.version = NEW.posting_rule_version AND s.status = 'CLOSED') THEN
    RAISE EXCEPTION 'fin.gl_journal %: its period and a close component it requires are CLOSED (E-VS1-9, E-VS2-03-3)', NEW.journal_id;
  END IF;
  RETURN NEW;
END $$;
