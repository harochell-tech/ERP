-- AF1-04 · Fixed assets: the initial load and the FA-GL reconciliation (approved errata E-AF1-04-1…9).
--   - a posted load can be reversed while none of its cards was depreciated or disposed of since: status REVERSED, and its cards
--     (born with accumulated depreciation) are cancelled (E-AF1-04-6);
--   - FA-GL: per account, the live cards' cost = the asset account and their accumulated = the accumulated depreciation account (an
--     error that blocks FA-REC); months ended with depreciation pending (a warning, an error for the period being closed) (E-AF1-04-7/8).

ALTER TABLE fa.asset_load DROP CONSTRAINT asset_load_status,
  ADD CONSTRAINT asset_load_status CHECK (status IN ('DRAFT', 'POSTED', 'REVERSED', 'DISCARDED'));
ALTER TABLE fa.asset_load DROP CONSTRAINT asset_load_posted,
  ADD CONSTRAINT asset_load_posted CHECK ((status IN ('POSTED', 'REVERSED')) = (approved_by IS NOT NULL AND approved_at IS NOT NULL AND posting_event_id IS NOT NULL));
ALTER TABLE fa.asset_load ADD CONSTRAINT asset_load_cutoff_month_end CHECK (cutoff_date = (date_trunc('month', cutoff_date) + interval '1 month' - interval '1 day')::date);

-- A card of the initial load is born with accumulated depreciation; its load's reversal cancels it (E-AF1-04-6).
CREATE OR REPLACE FUNCTION fa.asset_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'fa.asset rows cannot be deleted; a card is cancelled or disposed';
  END IF;
  IF TG_OP = 'INSERT' THEN
    IF NEW.version <> 1 OR NEW.status NOT IN ('AWAITING_SERVICE', 'IN_SERVICE') OR (NEW.source_kind = 'INVOICE_LINE' AND NEW.status <> 'AWAITING_SERVICE') THEN
      RAISE EXCEPTION 'fa.asset: an invoice''s card is born awaiting service; only the initial load creates cards in service';
    END IF;
    RETURN NEW;
  END IF;
  IF ROW(NEW.asset_id, NEW.company_id, NEW.asset_no, NEW.expense_category_id, NEW.source_kind, NEW.si_line_id, NEW.load_id, NEW.external_code, NEW.acquired_on,
         NEW.created_by)
     IS DISTINCT FROM ROW(OLD.asset_id, OLD.company_id, OLD.asset_no, OLD.expense_category_id, OLD.source_kind, OLD.si_line_id, OLD.load_id, OLD.external_code,
         OLD.acquired_on, OLD.created_by) THEN
    RAISE EXCEPTION 'fa.asset: identity columns are immutable';
  END IF;
  IF OLD.status IN ('DISPOSED', 'CANCELLED') THEN
    RAISE EXCEPTION 'fa.asset: a % card never changes', OLD.status;
  END IF;
  IF OLD.status = 'IN_SERVICE' AND ROW(NEW.asset_class_id, NEW.useful_life_months, NEW.residual_pct, NEW.in_service_on)
     IS DISTINCT FROM ROW(OLD.asset_class_id, OLD.useful_life_months, OLD.residual_pct, OLD.in_service_on) THEN
    RAISE EXCEPTION 'fa.asset: a card in service keeps its class, life, residual and service date (E-AF1-01-2)';
  END IF;
  IF NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'fa.asset: version must increase by exactly 1';
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
       (OLD.status = 'AWAITING_SERVICE' AND NEW.status IN ('IN_SERVICE', 'CANCELLED')) OR (OLD.status = 'IN_SERVICE' AND NEW.status IN ('DISPOSED', 'CANCELLED'))) THEN
    RAISE EXCEPTION 'fa.asset: % → % is not allowed', OLD.status, NEW.status;
  END IF;
  IF NEW.status = 'CANCELLED' AND OLD.source_kind = 'INVOICE_LINE' AND OLD.accumulated <> 0 THEN
    RAISE EXCEPTION 'fa.asset: a card with depreciation is disposed, never cancelled (E-AF1-01-5)';
  END IF;
  IF NEW.status = 'CANCELLED' AND OLD.source_kind = 'OPENING'
     AND EXISTS (SELECT 1 FROM fa.asset_movement m WHERE m.asset_id = OLD.asset_id AND m.kind = 'DEPRECIATION') THEN
    RAISE EXCEPTION 'fa.asset: a loaded card depreciated since the load is disposed, never cancelled (E-AF1-04-6)';
  END IF;
  RETURN NEW;
END $$;

-- ---------------------------------------------------------------------------------------------
-- FA-GL (E-AF-10, E-AF1-04-7/8/9): blocks FA-REC.
-- ---------------------------------------------------------------------------------------------
INSERT INTO rec.recon_definition (recon_code, description, severity, name, guidance) VALUES
  ('FA-GL',
   'Por cuenta: costo de los activos vivos = cuenta de activo; su depreciación acumulada = cuenta de depreciación acumulada; meses terminados sin depreciar (E-AF-10)',
   'ERROR',
   'Activos fijos contra el mayor',
   'Comprueba que lo que dicen las fichas de activos fijos (costo y depreciación acumulada de los activos en espera o en servicio) sea lo que tienen sus cuentas en el mayor, y avisa de los meses terminados en los que quedó depreciación por registrar.');
INSERT INTO rec.recon_blocking (recon_code, component) VALUES ('FA-GL', 'FA-REC');

INSERT INTO rec.recon_classification (classification, name, guidance) VALUES
  ('FA_COST_DIFFERENCE', 'Costo de los activos distinto del mayor',
   'La suma del costo de las fichas vivas no es el saldo de la cuenta de activo. Revise asientos manuales a esa cuenta y facturas de activos contabilizadas antes de AF-1 sin ficha (Activos fijos › Crear fichas de facturas ya contabilizadas).'),
  ('FA_ACCUMULATED_DIFFERENCE', 'Depreciación acumulada distinta del mayor',
   'La suma de la depreciación acumulada de las fichas no es el saldo de su cuenta. Revise asientos manuales a esa cuenta.'),
  ('FA_DEPRECIATION_MISSING', 'Mes sin depreciar',
   'El mes terminó con activos en servicio por depreciar y sin la depreciación registrada. Regístrela en Contabilidad › Activos fijos › Depreciación del mes.');
