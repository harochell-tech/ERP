-- AF1-01 · Fixed assets: schema (approved errata E-AF-1…12, E-AF1-01-1…9).
--   - fa.asset_class: an expense category of fixed assets (606 type 04) completed with useful life, residual %, accumulated depreciation and
--     depreciation expense accounts; prepared by the Contador, approved by the Controller; a change is a new version (E-AF-2, E-AF1-01-3);
--   - fa.asset: the card, born from a posted invoice line (or the initial load), AWAITING_SERVICE → IN_SERVICE → DISPOSED (or CANCELLED with its
--     invoice); it copies its class's life and residual when it is put into service (E-AF-3, E-AF-5, E-AF1-01-2/4/5);
--   - fa.asset_movement: what happened to a card, append-only; fa.depreciation_run / _line: one POSTED run per month (E-AF-6);
--   - fa.asset_disposal (E-AF-7) and fa.asset_load / _line (E-AF-8);
--   - roles, technical document roles, permissions with SoD, rules P-44 / P-45 / P-46 in DRAFT and the close component FA-REC (E-AF1-01-7…9).

CREATE SCHEMA fa;
GRANT USAGE ON SCHEMA fa TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- Asset classes (E-AF-2, E-AF1-01-3): one ACTIVE version per fixed-asset category.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fa.asset_class (
  asset_class_id          uuid          NOT NULL,
  company_id              uuid          NOT NULL,
  expense_category_id     uuid          NOT NULL,
  class_version           integer       NOT NULL,
  useful_life_months      integer       NOT NULL,
  residual_pct            numeric(5,2)  NOT NULL,
  accumulated_account_id  uuid          NOT NULL,
  expense_account_id      uuid          NOT NULL,
  status                  text          NOT NULL,
  prepared_by             uuid          NOT NULL,
  approved_by             uuid,
  approved_at             timestamptz,
  version                 bigint        NOT NULL,
  CONSTRAINT asset_class_pk PRIMARY KEY (asset_class_id),
  CONSTRAINT asset_class_company_uq UNIQUE (company_id, asset_class_id),
  CONSTRAINT asset_class_version_uq UNIQUE (expense_category_id, class_version),
  CONSTRAINT asset_class_category_fk FOREIGN KEY (company_id, expense_category_id) REFERENCES pur.expense_category (company_id, expense_category_id),
  CONSTRAINT asset_class_accumulated_fk FOREIGN KEY (company_id, accumulated_account_id) REFERENCES fin.account (company_id, account_id),
  CONSTRAINT asset_class_expense_fk FOREIGN KEY (company_id, expense_account_id) REFERENCES fin.account (company_id, account_id),
  CONSTRAINT asset_class_prepared_fk FOREIGN KEY (prepared_by) REFERENCES iam.user (user_id),
  CONSTRAINT asset_class_approved_fk FOREIGN KEY (approved_by) REFERENCES iam.user (user_id),
  CONSTRAINT asset_class_life CHECK (useful_life_months BETWEEN 1 AND 600),
  CONSTRAINT asset_class_residual CHECK (residual_pct >= 0 AND residual_pct < 100),
  CONSTRAINT asset_class_accounts CHECK (accumulated_account_id <> expense_account_id),
  CONSTRAINT asset_class_status CHECK (status IN ('DRAFT', 'ACTIVE', 'SUPERSEDED', 'DISCARDED')),
  CONSTRAINT asset_class_approved CHECK ((status IN ('ACTIVE', 'SUPERSEDED')) = (approved_by IS NOT NULL AND approved_at IS NOT NULL)),
  CONSTRAINT asset_class_version_positive CHECK (version >= 1 AND class_version >= 1)
);
CREATE UNIQUE INDEX asset_class_one_active ON fa.asset_class (expense_category_id) WHERE status = 'ACTIVE';
CREATE UNIQUE INDEX asset_class_one_draft ON fa.asset_class (expense_category_id) WHERE status = 'DRAFT';

-- The category is of fixed assets (an ASSET account, type 04); accumulated depreciation an ASSET account and the expense an EXPENSE or COST account,
-- neither a control account; values change only while DRAFT.
CREATE FUNCTION fa.asset_class_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'fa.asset_class rows cannot be deleted';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pur.expense_category c JOIN fin.account a ON a.account_id = c.account_id
                 WHERE c.expense_category_id = NEW.expense_category_id AND c.goods_type_606 = '04' AND a.account_class = 'ASSET') THEN
    RAISE EXCEPTION 'fa.asset_class: the category is not of fixed assets (an asset account with 606 type 04)';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM fin.account WHERE account_id = NEW.accumulated_account_id AND account_class = 'ASSET' AND NOT is_control)
     OR NOT EXISTS (SELECT 1 FROM fin.account WHERE account_id = NEW.expense_account_id AND account_class IN ('EXPENSE', 'COST') AND NOT is_control) THEN
    RAISE EXCEPTION 'fa.asset_class: accumulated depreciation is an asset account and the expense an expense or cost account, neither a control account';
  END IF;
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'DRAFT' OR NEW.version <> 1 OR NEW.approved_by IS NOT NULL THEN
      RAISE EXCEPTION 'fa.asset_class: a class is prepared DRAFT, version 1, without approval';
    END IF;
    RETURN NEW;
  END IF;
  IF ROW(NEW.asset_class_id, NEW.company_id, NEW.expense_category_id, NEW.class_version, NEW.prepared_by)
     IS DISTINCT FROM ROW(OLD.asset_class_id, OLD.company_id, OLD.expense_category_id, OLD.class_version, OLD.prepared_by) THEN
    RAISE EXCEPTION 'fa.asset_class: identity columns are immutable';
  END IF;
  IF OLD.status <> 'DRAFT' AND ROW(NEW.useful_life_months, NEW.residual_pct, NEW.accumulated_account_id, NEW.expense_account_id)
     IS DISTINCT FROM ROW(OLD.useful_life_months, OLD.residual_pct, OLD.accumulated_account_id, OLD.expense_account_id) THEN
    RAISE EXCEPTION 'fa.asset_class: an approved class never changes; prepare a new version (E-AF1-01-3)';
  END IF;
  IF NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION 'fa.asset_class: version must increase by exactly 1';
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
       (OLD.status = 'DRAFT' AND NEW.status IN ('ACTIVE', 'DISCARDED')) OR (OLD.status = 'ACTIVE' AND NEW.status = 'SUPERSEDED')) THEN
    RAISE EXCEPTION 'fa.asset_class: % → % is not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER asset_class_guard BEFORE INSERT OR UPDATE OR DELETE ON fa.asset_class FOR EACH ROW EXECUTE FUNCTION fa.asset_class_guard();
CREATE TRIGGER asset_class_four_eyes BEFORE INSERT OR UPDATE ON fa.asset_class
  FOR EACH ROW EXECUTE FUNCTION core.four_eyes('approved_by', 'prepared_by', 'asset_class_four_eyes');
CREATE CONSTRAINT TRIGGER asset_class_evidence_on_insert AFTER INSERT ON fa.asset_class
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('AssetClass', 'asset_class_id');
CREATE CONSTRAINT TRIGGER asset_class_evidence_on_change AFTER UPDATE ON fa.asset_class
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('AssetClass', 'asset_class_id');

-- ---------------------------------------------------------------------------------------------
-- The initial load (E-AF-8): a batch of existing assets prepared by the Contador and approved (posted, P-46) by the Controller.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fa.asset_load (
  load_id           uuid        NOT NULL,
  company_id        uuid        NOT NULL,
  cutoff_date       date        NOT NULL,
  file_name         text        NOT NULL,
  status            text        NOT NULL,
  prepared_by       uuid        NOT NULL,
  approved_by       uuid,
  approved_at       timestamptz,
  posting_event_id  uuid,
  version           bigint      NOT NULL,
  CONSTRAINT asset_load_pk PRIMARY KEY (load_id),
  CONSTRAINT asset_load_company_uq UNIQUE (company_id, load_id),
  CONSTRAINT asset_load_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT asset_load_event_fk FOREIGN KEY (company_id, posting_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT asset_load_prepared_fk FOREIGN KEY (prepared_by) REFERENCES iam.user (user_id),
  CONSTRAINT asset_load_approved_fk FOREIGN KEY (approved_by) REFERENCES iam.user (user_id),
  CONSTRAINT asset_load_file CHECK (length(btrim(file_name)) BETWEEN 1 AND 200),
  CONSTRAINT asset_load_status CHECK (status IN ('DRAFT', 'POSTED', 'DISCARDED')),
  CONSTRAINT asset_load_posted CHECK ((status = 'POSTED') = (approved_by IS NOT NULL AND approved_at IS NOT NULL AND posting_event_id IS NOT NULL)),
  CONSTRAINT asset_load_version_positive CHECK (version >= 1)
);
CREATE TRIGGER asset_load_four_eyes BEFORE INSERT OR UPDATE ON fa.asset_load
  FOR EACH ROW EXECUTE FUNCTION core.four_eyes('approved_by', 'prepared_by', 'asset_load_four_eyes');
CREATE CONSTRAINT TRIGGER asset_load_evidence_on_insert AFTER INSERT ON fa.asset_load
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('AssetLoad', 'load_id');
CREATE CONSTRAINT TRIGGER asset_load_evidence_on_change AFTER UPDATE ON fa.asset_load
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('AssetLoad', 'load_id');

CREATE TABLE fa.asset_load_line (
  load_id              uuid          NOT NULL,
  company_id           uuid          NOT NULL,
  line_no              integer       NOT NULL,
  external_code        text          NOT NULL,
  description          text          NOT NULL,
  expense_category_id  uuid          NOT NULL,
  plant_id             uuid          NOT NULL,
  acquired_on          date          NOT NULL,
  cost                 numeric(19,4) NOT NULL,
  accumulated          numeric(19,4) NOT NULL,
  CONSTRAINT asset_load_line_pk PRIMARY KEY (load_id, line_no),
  CONSTRAINT asset_load_line_code_uq UNIQUE (load_id, external_code),
  CONSTRAINT asset_load_line_header_fk FOREIGN KEY (company_id, load_id) REFERENCES fa.asset_load (company_id, load_id),
  CONSTRAINT asset_load_line_category_fk FOREIGN KEY (company_id, expense_category_id) REFERENCES pur.expense_category (company_id, expense_category_id),
  CONSTRAINT asset_load_line_plant_fk FOREIGN KEY (company_id, plant_id) REFERENCES md.plant (company_id, plant_id),
  CONSTRAINT asset_load_line_code CHECK (length(btrim(external_code)) BETWEEN 1 AND 40),
  CONSTRAINT asset_load_line_description CHECK (length(btrim(description)) BETWEEN 1 AND 200),
  CONSTRAINT asset_load_line_amounts CHECK (cost > 0 AND accumulated >= 0 AND accumulated <= cost AND cost = round(cost, 2) AND accumulated = round(accumulated, 2)),
  CONSTRAINT asset_load_line_no CHECK (line_no >= 1)
);

-- ---------------------------------------------------------------------------------------------
-- The asset card (E-AF-3…5, E-AF-9, E-AF1-01-2/4/5/6).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fa.asset (
  asset_id             uuid          NOT NULL,
  company_id           uuid          NOT NULL,
  asset_no             text          NOT NULL,
  expense_category_id  uuid          NOT NULL,
  description          text          NOT NULL,
  quantity             numeric(18,6) NOT NULL,
  source_kind          text          NOT NULL,
  si_line_id           uuid,
  load_id              uuid,
  external_code        text,
  plant_id             uuid          NOT NULL,
  acquired_on          date          NOT NULL,
  status               text          NOT NULL,
  in_service_on        date,
  responsible          text,
  asset_class_id       uuid,
  useful_life_months   integer,
  residual_pct         numeric(5,2),
  months_depreciated   integer       NOT NULL,
  cost                 numeric(19,4) NOT NULL,
  accumulated          numeric(19,4) NOT NULL,
  created_by           uuid          NOT NULL,
  version              bigint        NOT NULL,
  CONSTRAINT asset_pk PRIMARY KEY (asset_id),
  CONSTRAINT asset_company_uq UNIQUE (company_id, asset_id),
  CONSTRAINT asset_no_uq UNIQUE (company_id, asset_no),
  CONSTRAINT asset_category_fk FOREIGN KEY (company_id, expense_category_id) REFERENCES pur.expense_category (company_id, expense_category_id),
  CONSTRAINT asset_line_fk FOREIGN KEY (company_id, si_line_id) REFERENCES pur.supplier_invoice_line (company_id, si_line_id),
  CONSTRAINT asset_load_fk FOREIGN KEY (company_id, load_id) REFERENCES fa.asset_load (company_id, load_id),
  CONSTRAINT asset_plant_fk FOREIGN KEY (company_id, plant_id) REFERENCES md.plant (company_id, plant_id),
  CONSTRAINT asset_class_fk FOREIGN KEY (company_id, asset_class_id) REFERENCES fa.asset_class (company_id, asset_class_id),
  CONSTRAINT asset_created_fk FOREIGN KEY (created_by) REFERENCES iam.user (user_id),
  CONSTRAINT asset_no_format CHECK (asset_no ~ '^AF-[0-9]{4}-[0-9]{6}$'),
  CONSTRAINT asset_description CHECK (length(btrim(description)) BETWEEN 1 AND 200),
  CONSTRAINT asset_quantity CHECK (quantity > 0),
  CONSTRAINT asset_source CHECK ((source_kind = 'INVOICE_LINE' AND si_line_id IS NOT NULL AND load_id IS NULL)
    OR (source_kind = 'OPENING' AND load_id IS NOT NULL AND si_line_id IS NULL AND external_code IS NOT NULL)),
  CONSTRAINT asset_status CHECK (status IN ('AWAITING_SERVICE', 'IN_SERVICE', 'DISPOSED', 'CANCELLED')),
  -- In service (and after) a card carries its class version, life and residual; awaiting service it has none.
  CONSTRAINT asset_service CHECK ((status IN ('IN_SERVICE', 'DISPOSED')) = (in_service_on IS NOT NULL AND asset_class_id IS NOT NULL AND useful_life_months IS NOT NULL
    AND residual_pct IS NOT NULL) OR (status = 'CANCELLED' AND in_service_on IS NULL)),
  CONSTRAINT asset_responsible CHECK (responsible IS NULL OR length(btrim(responsible)) BETWEEN 1 AND 120),
  CONSTRAINT asset_amounts CHECK (cost >= 0 AND accumulated >= 0 AND accumulated <= cost AND months_depreciated >= 0
    AND (useful_life_months IS NULL OR months_depreciated <= useful_life_months)),
  CONSTRAINT asset_version_positive CHECK (version >= 1)
);
-- An invoice line has one live card (a cancelled one frees it).
CREATE UNIQUE INDEX asset_one_per_line ON fa.asset (si_line_id) WHERE si_line_id IS NOT NULL AND status <> 'CANCELLED';
CREATE CONSTRAINT TRIGGER asset_evidence_on_insert AFTER INSERT ON fa.asset
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('FixedAsset', 'asset_id');
CREATE CONSTRAINT TRIGGER asset_evidence_on_change AFTER UPDATE ON fa.asset
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('FixedAsset', 'asset_id');

CREATE FUNCTION fa.asset_guard() RETURNS trigger
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
  IF NEW.status = 'CANCELLED' AND OLD.accumulated <> 0 THEN
    RAISE EXCEPTION 'fa.asset: a card with depreciation is disposed, never cancelled (E-AF1-01-5)';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER asset_guard BEFORE INSERT OR UPDATE OR DELETE ON fa.asset FOR EACH ROW EXECUTE FUNCTION fa.asset_guard();

-- What happened to a card: append-only.
CREATE TABLE fa.asset_movement (
  movement_id    uuid          NOT NULL,
  company_id     uuid          NOT NULL,
  asset_id       uuid          NOT NULL,
  kind           text          NOT NULL,
  movement_date  date          NOT NULL,
  amount         numeric(19,4),
  plant_id       uuid,
  source_ref     uuid,
  event_id       uuid          NOT NULL,
  CONSTRAINT asset_movement_pk PRIMARY KEY (movement_id),
  CONSTRAINT asset_movement_asset_fk FOREIGN KEY (company_id, asset_id) REFERENCES fa.asset (company_id, asset_id),
  CONSTRAINT asset_movement_plant_fk FOREIGN KEY (company_id, plant_id) REFERENCES md.plant (company_id, plant_id),
  CONSTRAINT asset_movement_event_fk FOREIGN KEY (company_id, event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT asset_movement_kind CHECK (kind IN ('ACQUISITION', 'COST_ADDED', 'COST_REMOVED', 'IN_SERVICE', 'TRANSFER', 'DEPRECIATION', 'DEPRECIATION_UNDONE',
    'DISPOSAL', 'OPENING', 'CANCELLED')),
  CONSTRAINT asset_movement_amount CHECK (amount IS NULL OR (amount >= 0 AND amount = round(amount, 2)))
);
CREATE INDEX asset_movement_asset ON fa.asset_movement (asset_id, movement_date);
CREATE TRIGGER asset_movement_append_only BEFORE UPDATE OR DELETE ON fa.asset_movement FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER asset_movement_no_truncate BEFORE TRUNCATE ON fa.asset_movement FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- The month's depreciation (E-AF-6): one POSTED run per month; undone while the period is open.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fa.depreciation_run (
  run_id             uuid          NOT NULL,
  company_id         uuid          NOT NULL,
  month              date          NOT NULL,
  total              numeric(19,4) NOT NULL,
  status             text          NOT NULL,
  posted_by          uuid          NOT NULL,
  posting_event_id   uuid          NOT NULL,
  version            bigint        NOT NULL,
  CONSTRAINT depreciation_run_pk PRIMARY KEY (run_id),
  CONSTRAINT depreciation_run_company_uq UNIQUE (company_id, run_id),
  CONSTRAINT depreciation_run_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT depreciation_run_posted_fk FOREIGN KEY (posted_by) REFERENCES iam.user (user_id),
  CONSTRAINT depreciation_run_event_fk FOREIGN KEY (company_id, posting_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT depreciation_run_month CHECK (month = date_trunc('month', month)::date),
  CONSTRAINT depreciation_run_total CHECK (total > 0 AND total = round(total, 2)),
  CONSTRAINT depreciation_run_status CHECK (status IN ('POSTED', 'UNDONE')),
  CONSTRAINT depreciation_run_version_positive CHECK (version >= 1)
);
CREATE UNIQUE INDEX depreciation_run_one_per_month ON fa.depreciation_run (company_id, month) WHERE status = 'POSTED';
CREATE CONSTRAINT TRIGGER depreciation_run_evidence_on_insert AFTER INSERT ON fa.depreciation_run
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('DepreciationRun', 'run_id');
CREATE CONSTRAINT TRIGGER depreciation_run_evidence_on_change AFTER UPDATE ON fa.depreciation_run
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('DepreciationRun', 'run_id');

CREATE TABLE fa.depreciation_line (
  run_id      uuid          NOT NULL,
  company_id  uuid          NOT NULL,
  asset_id    uuid          NOT NULL,
  plant_id    uuid          NOT NULL,
  amount      numeric(19,4) NOT NULL,
  CONSTRAINT depreciation_line_pk PRIMARY KEY (run_id, asset_id),
  CONSTRAINT depreciation_line_run_fk FOREIGN KEY (company_id, run_id) REFERENCES fa.depreciation_run (company_id, run_id),
  CONSTRAINT depreciation_line_asset_fk FOREIGN KEY (company_id, asset_id) REFERENCES fa.asset (company_id, asset_id),
  CONSTRAINT depreciation_line_plant_fk FOREIGN KEY (company_id, plant_id) REFERENCES md.plant (company_id, plant_id),
  CONSTRAINT depreciation_line_amount CHECK (amount > 0 AND amount = round(amount, 2))
);
CREATE TRIGGER depreciation_line_append_only BEFORE UPDATE OR DELETE ON fa.depreciation_line FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

-- ---------------------------------------------------------------------------------------------
-- Disposal and sale (E-AF-7): prepared by the Contador, approved (posted, P-45) by the Controller; one live disposal per card.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE fa.asset_disposal (
  disposal_id       uuid          NOT NULL,
  company_id        uuid          NOT NULL,
  asset_id          uuid          NOT NULL,
  kind              text          NOT NULL,
  disposal_date     date          NOT NULL,
  price             numeric(19,4),
  reason            text          NOT NULL,
  status            text          NOT NULL,
  prepared_by       uuid          NOT NULL,
  approved_by       uuid,
  approved_at       timestamptz,
  posting_event_id  uuid,
  version           bigint        NOT NULL,
  CONSTRAINT asset_disposal_pk PRIMARY KEY (disposal_id),
  CONSTRAINT asset_disposal_company_uq UNIQUE (company_id, disposal_id),
  CONSTRAINT asset_disposal_asset_fk FOREIGN KEY (company_id, asset_id) REFERENCES fa.asset (company_id, asset_id),
  CONSTRAINT asset_disposal_event_fk FOREIGN KEY (company_id, posting_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT asset_disposal_prepared_fk FOREIGN KEY (prepared_by) REFERENCES iam.user (user_id),
  CONSTRAINT asset_disposal_approved_fk FOREIGN KEY (approved_by) REFERENCES iam.user (user_id),
  CONSTRAINT asset_disposal_kind CHECK ((kind = 'SCRAP' AND price IS NULL) OR (kind = 'SALE' AND price IS NOT NULL AND price > 0 AND price = round(price, 2))),
  CONSTRAINT asset_disposal_reason CHECK (length(btrim(reason)) BETWEEN 3 AND 300),
  CONSTRAINT asset_disposal_status CHECK (status IN ('DRAFT', 'POSTED', 'CANCELLED')),
  CONSTRAINT asset_disposal_posted CHECK ((status = 'POSTED') = (approved_by IS NOT NULL AND approved_at IS NOT NULL AND posting_event_id IS NOT NULL)),
  CONSTRAINT asset_disposal_version_positive CHECK (version >= 1)
);
CREATE UNIQUE INDEX asset_disposal_one_live ON fa.asset_disposal (asset_id) WHERE status IN ('DRAFT', 'POSTED');
CREATE TRIGGER asset_disposal_four_eyes BEFORE INSERT OR UPDATE ON fa.asset_disposal
  FOR EACH ROW EXECUTE FUNCTION core.four_eyes('approved_by', 'prepared_by', 'asset_disposal_four_eyes');
CREATE CONSTRAINT TRIGGER asset_disposal_evidence_on_insert AFTER INSERT ON fa.asset_disposal
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fin.require_state_history('AssetDisposal', 'disposal_id');
CREATE CONSTRAINT TRIGGER asset_disposal_evidence_on_change AFTER UPDATE ON fa.asset_disposal
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
  EXECUTE FUNCTION fin.require_state_history('AssetDisposal', 'disposal_id');

-- ---------------------------------------------------------------------------------------------
-- Account roles (E-AF1-01-7/8): three mapped by the Controller (A-01), three technical ones whose account comes from the card's class or
-- category and are never mapped.
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.account_role (role_code, is_control, description, name) VALUES
  ('ASSET_SALE_RECEIVABLE', false, 'Precio por cobrar de los activos fijos vendidos, hasta que se factura y cobra al comprador', 'Venta de activos por cobrar'),
  ('ASSET_DISPOSAL_GAIN', false, 'Ganancia cuando el precio de venta de un activo supera su valor en libros', 'Ganancia en venta de activos'),
  ('ASSET_DISPOSAL_LOSS', false, 'Pérdida cuando un activo se desecha o se vende por debajo de su valor en libros', 'Pérdida en baja de activos'),
  ('FIXED_ASSET_COST', false, 'Costo de un activo fijo; rol técnico: la cuenta es la de su categoría, nunca se mapea', 'Costo del activo (categoría)'),
  ('FIXED_ASSET_ACCUMULATED', false, 'Depreciación acumulada; rol técnico: la cuenta es la de la clase del activo, nunca se mapea', 'Depreciación acumulada (clase)'),
  ('FIXED_ASSET_DEPRECIATION', false, 'Gasto de depreciación; rol técnico: la cuenta es la de la clase del activo, nunca se mapea', 'Gasto de depreciación (clase)');

CREATE OR REPLACE FUNCTION fin.account_role_map_not_expense() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.account_role IN ('PURCHASE_EXPENSE', 'FIXED_ASSET_COST', 'FIXED_ASSET_ACCUMULATED', 'FIXED_ASSET_DEPRECIATION') THEN
    RAISE EXCEPTION 'fin.account_role_map: % is a technical role and is never mapped; the account comes from the document (E-GAS-2, E-AF1-01-7)', NEW.account_role;
  END IF;
  RETURN NEW;
END $$;

-- ---------------------------------------------------------------------------------------------
-- The close component FA-REC (E-AF1-01-9), accepted everywhere a component is named and OPEN in every existing period.
-- ---------------------------------------------------------------------------------------------
ALTER TABLE fin.posting_rule_version DROP CONSTRAINT posting_rule_version_component,
  ADD CONSTRAINT posting_rule_version_component CHECK (close_component IN ('INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX', 'AR-REC', 'OP-DAY', 'COST-SET', 'FA-REC'));
ALTER TABLE fin.posting_rule_version DROP CONSTRAINT posting_rule_version_also_requires,
  ADD CONSTRAINT posting_rule_version_also_requires CHECK (also_requires_components <@ ARRAY['INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX', 'AR-REC', 'OP-DAY', 'COST-SET', 'FA-REC']::text[]);
ALTER TABLE fin.close_component_state DROP CONSTRAINT close_component_state_component,
  ADD CONSTRAINT close_component_state_component CHECK (component IN ('INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX', 'AR-REC', 'OP-DAY', 'COST-SET', 'FA-REC'));
ALTER TABLE fin.close_snapshot DROP CONSTRAINT close_snapshot_component,
  ADD CONSTRAINT close_snapshot_component CHECK (component IN ('INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX', 'AR-REC', 'OP-DAY', 'COST-SET', 'FA-REC'));
ALTER TABLE fin.reopen_request DROP CONSTRAINT reopen_request_component,
  ADD CONSTRAINT reopen_request_component CHECK (component IN ('INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX', 'AR-REC', 'OP-DAY', 'COST-SET', 'FA-REC'));
ALTER TABLE rec.recon_blocking DROP CONSTRAINT recon_blocking_component,
  ADD CONSTRAINT recon_blocking_component CHECK (component IN ('INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX', 'AR-REC', 'OP-DAY', 'COST-SET', 'FA-REC'));
ALTER TABLE rec.recon_exception DROP CONSTRAINT recon_exception_component,
  ADD CONSTRAINT recon_exception_component CHECK (component IS NULL OR component IN ('INV-MOV', 'AP-REC', 'BANK-REC', 'ACR-NTX', 'ACR-TAX', 'AR-REC', 'OP-DAY', 'COST-SET', 'FA-REC'));

INSERT INTO fin.close_component_state (company_id, period_id, component, status, version)
SELECT company_id, period_id, 'FA-REC', 'OPEN', 1 FROM fin.period
ON CONFLICT (period_id, component) DO NOTHING;

-- ---------------------------------------------------------------------------------------------
-- P-44 depreciation, P-45 disposal or sale, P-46 initial load (E-AF1-01-8): DRAFT until the Controller approves them (A-01).
-- ---------------------------------------------------------------------------------------------
INSERT INTO fin.posting_rule (posting_rule_id, code, event_type) VALUES
  ('0192f001-0000-7000-8000-000000000037', 'P-44', 'DepreciationPosted'),
  ('0192f001-0000-7000-8000-000000000038', 'P-45', 'AssetDisposalPosted'),
  ('0192f001-0000-7000-8000-000000000039', 'P-46', 'AssetLoadPosted');

INSERT INTO fin.posting_rule_version (posting_rule_id, version, definition, explanation_templates, close_component, effective_from, status) VALUES
  ('0192f001-0000-7000-8000-000000000037', 1,
   '{"lines": [
      {"code": "P44-DR-DEP", "side": "DEBIT", "account_role": "FIXED_ASSET_DEPRECIATION", "amount": "depreciation", "dimensions": ["plant"]},
      {"code": "P44-CR-ACC", "side": "CREDIT", "account_role": "FIXED_ASSET_ACCUMULATED", "amount": "depreciation", "dimensions": ["plant"]}
    ]}',
   '{"P44-DR-DEP": "Depreciación de {month}: {asset} ({description}), cuota {months} de {life}.",
     "P44-CR-ACC": "Depreciación de {month}: acumulada de {asset}."}',
   'FA-REC', DATE '2026-01-01', 'DRAFT'),
  ('0192f001-0000-7000-8000-000000000038', 1,
   '{"lines": [
      {"code": "P45-DR-ACC", "side": "DEBIT", "account_role": "FIXED_ASSET_ACCUMULATED", "amount": "accumulated", "dimensions": ["plant"]},
      {"code": "P45-DR-REC", "side": "DEBIT", "account_role": "ASSET_SALE_RECEIVABLE", "amount": "price", "dimensions": ["plant"]},
      {"code": "P45-DR-LOSS", "side": "DEBIT", "account_role": "ASSET_DISPOSAL_LOSS", "amount": "loss", "dimensions": ["plant"]},
      {"code": "P45-CR-COST", "side": "CREDIT", "account_role": "FIXED_ASSET_COST", "amount": "cost", "dimensions": ["plant"]},
      {"code": "P45-CR-GAIN", "side": "CREDIT", "account_role": "ASSET_DISPOSAL_GAIN", "amount": "gain", "dimensions": ["plant"]}
    ]}',
   '{"P45-DR-ACC": "Baja de {asset}: sale su depreciación acumulada.",
     "P45-DR-REC": "Venta de {asset}: precio por cobrar al comprador.",
     "P45-DR-LOSS": "Baja de {asset}: pérdida (valor en libros sobre el precio).",
     "P45-CR-COST": "Baja de {asset}: sale su costo.",
     "P45-CR-GAIN": "Venta de {asset}: ganancia (precio sobre el valor en libros)."}',
   'FA-REC', DATE '2026-01-01', 'DRAFT'),
  ('0192f001-0000-7000-8000-000000000039', 1,
   '{"lines": [
      {"code": "P46-DR-COST", "side": "DEBIT", "account_role": "FIXED_ASSET_COST", "amount": "cost", "dimensions": ["plant"]},
      {"code": "P46-CR-ACC", "side": "CREDIT", "account_role": "FIXED_ASSET_ACCUMULATED", "amount": "accumulated", "dimensions": ["plant"]},
      {"code": "P46-CR-OPEN", "side": "CREDIT", "account_role": "MIGRATION_CLEARING", "amount": "book_value", "dimensions": ["plant"]}
    ]}',
   '{"P46-DR-COST": "Carga inicial: costo de {asset} ({description}).",
     "P46-CR-ACC": "Carga inicial: depreciación acumulada de {asset} al {cutoff}.",
     "P46-CR-OPEN": "Carga inicial: valor en libros de {asset} contra la contrapartida de saldos de apertura."}',
   'FA-REC', DATE '2026-01-01', 'DRAFT');

-- ---------------------------------------------------------------------------------------------
-- Permissions (E-AF-11): the Contador manages, the Controller approves; never the same person.
-- ---------------------------------------------------------------------------------------------
INSERT INTO iam.permission (permission_code, access) VALUES ('fixed_asset:manage', 'WRITE'), ('fixed_asset:approve', 'WRITE');
INSERT INTO iam.role_permission (role_id, permission_code)
SELECT r.role_id, v.permission_code
FROM (VALUES ('CONTADOR', 'fixed_asset:manage'), ('CONTROLLER', 'fixed_asset:approve')) AS v (role_code, permission_code)
JOIN iam.role r ON r.code = v.role_code;
INSERT INTO iam.sod_rule (permission_a, permission_b) VALUES ('fixed_asset:approve', 'fixed_asset:manage');

GRANT SELECT, INSERT ON fa.asset_class, fa.asset, fa.asset_movement, fa.depreciation_run, fa.depreciation_line, fa.asset_disposal, fa.asset_load,
  fa.asset_load_line TO rochell_app;
GRANT UPDATE (useful_life_months, residual_pct, accumulated_account_id, expense_account_id, status, approved_by, approved_at, version) ON fa.asset_class TO rochell_app;
GRANT UPDATE (description, plant_id, status, in_service_on, responsible, asset_class_id, useful_life_months, residual_pct, months_depreciated, cost, accumulated, version)
  ON fa.asset TO rochell_app;
GRANT UPDATE (status, version) ON fa.depreciation_run TO rochell_app;
GRANT UPDATE (status, approved_by, approved_at, posting_event_id, version) ON fa.asset_disposal TO rochell_app;
GRANT UPDATE (status, approved_by, approved_at, posting_event_id, version) ON fa.asset_load TO rochell_app;
GRANT DELETE ON fa.asset_load_line TO rochell_app;
