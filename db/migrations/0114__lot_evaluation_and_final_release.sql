-- LAB1-02 · the lot's evaluation, final release and the block of released lots (approved errata E-LAB1-4, 5, 10 and E-LAB1-02-1…15).
--   - `mfg.fg_lot` gains the status FINAL_RELEASED (E-LAB1-4), who released it for good and when, and what a block needs to be undone:
--     its cause (MANUAL or LAB), the status it came from and the evaluation that caused it (E-LAB1-02-6).
--     CURING → RELEASED → FINAL_RELEASED; any of the three → BLOCKED → back to the one it came from (or SCRAPPED).
--   - `qa.lot_evaluation` (E-LAB1-02-1): one row per evaluation, never changed; the latest of a lot is in force.
--   - `qa.own_age_factors` / `qa.age_factor` (E-LAB1-02-3, 4, 13): the factor of an age from the lots broken early and at 28 days.

ALTER TABLE mfg.fg_lot
  DROP CONSTRAINT fg_lot_status,
  ADD CONSTRAINT fg_lot_status CHECK (status IN ('CURING', 'RELEASED', 'FINAL_RELEASED', 'BLOCKED', 'SCRAPPED', 'VOIDED')),
  ADD COLUMN block_cause text,
  ADD COLUMN blocked_from text,
  ADD COLUMN block_evaluation_id uuid,
  ADD COLUMN final_released_by uuid,
  ADD COLUMN final_released_at timestamptz,
  ADD CONSTRAINT fg_lot_final_released_by_fk FOREIGN KEY (final_released_by) REFERENCES iam.user (user_id),
  ADD CONSTRAINT fg_lot_final_release_data CHECK ((final_released_by IS NULL) = (final_released_at IS NULL));

-- Lots blocked before LAB-1 were blocked by Calidad while curing (E-MFG1-04-3).
-- The guard refuses a change that is not a status change; no lot changes status here.
ALTER TABLE mfg.fg_lot DISABLE TRIGGER fg_lot_guard;
UPDATE mfg.fg_lot SET block_cause = 'MANUAL', blocked_from = 'CURING' WHERE status = 'BLOCKED';
ALTER TABLE mfg.fg_lot ENABLE TRIGGER fg_lot_guard;

ALTER TABLE mfg.fg_lot
  ADD CONSTRAINT fg_lot_block_data CHECK ((status = 'BLOCKED') = (block_cause IS NOT NULL) AND (status = 'BLOCKED') = (blocked_from IS NOT NULL)
    AND (block_cause IS NULL OR block_cause IN ('MANUAL', 'LAB')) AND (blocked_from IS NULL OR blocked_from IN ('CURING', 'RELEASED', 'FINAL_RELEASED'))
    AND (block_evaluation_id IS NULL OR block_cause = 'LAB'));

CREATE OR REPLACE FUNCTION mfg.fg_lot_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF ROW(NEW.lot_id, NEW.company_id, NEW.summary_id, NEW.run_id, NEW.curing_from, NEW.releasable_at)
     IS DISTINCT FROM ROW(OLD.lot_id, OLD.company_id, OLD.summary_id, OLD.run_id, OLD.curing_from, OLD.releasable_at)
     OR NEW.version <> OLD.version + 1
     OR (OLD.field_code IS NOT NULL AND NEW.field_code IS DISTINCT FROM OLD.field_code)
     OR NOT ((OLD.status = 'CURING' AND NEW.status IN ('RELEASED', 'BLOCKED', 'SCRAPPED', 'VOIDED'))
             OR (OLD.status = 'RELEASED' AND NEW.status IN ('FINAL_RELEASED', 'BLOCKED', 'SCRAPPED'))
             OR (OLD.status = 'FINAL_RELEASED' AND NEW.status IN ('BLOCKED', 'SCRAPPED'))
             OR (OLD.status = 'BLOCKED' AND (NEW.status = OLD.blocked_from OR NEW.status = 'SCRAPPED'))
             OR (OLD.status = NEW.status AND OLD.field_code IS NULL AND NEW.field_code IS NOT NULL)) THEN
    RAISE EXCEPTION 'mfg.fg_lot: % → % or a change of the lot''s identity is not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;

GRANT UPDATE (block_cause, blocked_from, block_evaluation_id, final_released_by, final_released_at) ON mfg.fg_lot TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- E-LAB1-02-1: the evaluation of a lot (baseline §4.2). Strengths in kg/cm², 6 decimals; CV as a fraction.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE qa.lot_evaluation (
  evaluation_id       uuid          NOT NULL,
  company_id          uuid          NOT NULL,
  lot_id              uuid          NOT NULL,
  seq                 integer       NOT NULL,
  cause               text          NOT NULL,
  specimens           integer       NOT NULL,
  age_min             integer,
  age_max             integer,
  avg_strength        numeric(18,6),
  min_strength        numeric(18,6),
  max_strength        numeric(18,6),
  std_dev             numeric(18,6),
  cv                  numeric(18,6),
  early_age           integer,
  early_avg           numeric(18,6),
  early_min           numeric(18,6),
  real_avg_28d        numeric(18,6),
  real_min_28d        numeric(18,6),
  factor_used         numeric(18,6),
  factor_source       text,
  strength_28d        numeric(18,6),
  min_28d             numeric(18,6),
  basis               text,
  spec_version        integer,
  min_avg_required    numeric(18,6),
  min_individual_required numeric(18,6),
  absorption_kgm3     numeric(18,6),
  absorption_limit_kgm3 numeric(18,6),
  verdict             text          NOT NULL,
  alerts              text[]        NOT NULL,
  evaluated_by        uuid          NOT NULL,
  evaluated_at        timestamptz   NOT NULL,
  CONSTRAINT lot_evaluation_pk PRIMARY KEY (evaluation_id),
  CONSTRAINT lot_evaluation_company_uq UNIQUE (company_id, evaluation_id),
  CONSTRAINT lot_evaluation_seq_uq UNIQUE (lot_id, seq),
  CONSTRAINT lot_evaluation_lot_fk FOREIGN KEY (company_id, lot_id) REFERENCES mfg.fg_lot (company_id, lot_id),
  CONSTRAINT lot_evaluation_by_fk FOREIGN KEY (evaluated_by) REFERENCES iam.user (user_id),
  CONSTRAINT lot_evaluation_cause CHECK (cause IN ('TESTS', 'REEVALUATION') AND seq >= 1 AND specimens >= 0),
  CONSTRAINT lot_evaluation_basis CHECK ((basis IS NULL) = (strength_28d IS NULL) AND (basis IS NULL OR basis IN ('REAL', 'ESTIMATED'))
    AND (basis IS DISTINCT FROM 'ESTIMATED' OR (factor_used IS NOT NULL AND factor_source IN ('OWN', 'INITIAL')))),
  CONSTRAINT lot_evaluation_verdict CHECK (verdict IN ('COMPLIES', 'FAILS', 'NO_SPEC', 'NO_DATA')
    AND (verdict IN ('COMPLIES', 'FAILS')) = (min_avg_required IS NOT NULL AND strength_28d IS NOT NULL)
    AND (verdict = 'NO_SPEC') = (min_avg_required IS NULL)),
  CONSTRAINT lot_evaluation_alerts CHECK (alerts <@ ARRAY['NO_TESTS', 'FEW_SPECIMENS', 'HIGH_CV', 'HIGH_ABSORPTION'])
);
CREATE INDEX lot_evaluation_latest ON qa.lot_evaluation (company_id, lot_id, seq DESC);
CREATE TRIGGER lot_evaluation_append_only BEFORE UPDATE OR DELETE ON qa.lot_evaluation FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();

ALTER TABLE mfg.fg_lot
  ADD CONSTRAINT fg_lot_block_evaluation_fk FOREIGN KEY (company_id, block_evaluation_id) REFERENCES qa.lot_evaluation (company_id, evaluation_id);

ALTER TABLE qa.lot_evaluation ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON qa.lot_evaluation USING (company_id = nullif(current_setting('app.company_id', true), '')::uuid)
  WITH CHECK (company_id = nullif(current_setting('app.company_id', true), '')::uuid);
GRANT SELECT, INSERT ON qa.lot_evaluation TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- E-LAB1-02-3, 4, 13 (baseline §4.3): the factor of a lot = its average at its early age (the minimum age of 1 day or more, below the
-- age that counts as 28 days) ÷ its average at 28 days, only for lots with both; the own factor of an age = the average of the
-- factors of the lots whose early age it is. One curve for every product and machine.
-- ---------------------------------------------------------------------------------------------
CREATE FUNCTION qa.own_age_factors(company uuid) RETURNS TABLE (age_days integer, factor numeric, lots integer)
  LANGUAGE sql STABLE AS $$
  WITH t AS (
    SELECT c.lot_id, c.age_days, c.strength_kgcm2, qa.parameter_number(company, 'AGE_28D_MIN_DAYS') AS a28
    FROM qa.compression_test c WHERE c.company_id = company AND c.status = 'RECORDED'),
  l AS (
    SELECT t.lot_id, min(t.age_days) FILTER (WHERE t.age_days >= 1 AND t.age_days < t.a28) AS early_age,
           avg(t.strength_kgcm2) FILTER (WHERE t.age_days >= t.a28) AS avg_28d
    FROM t GROUP BY t.lot_id),
  f AS (
    SELECT l.early_age, (SELECT avg(t.strength_kgcm2) FROM t WHERE t.lot_id = l.lot_id AND t.age_days = l.early_age) / l.avg_28d AS factor
    FROM l WHERE l.early_age IS NOT NULL AND l.avg_28d IS NOT NULL)
  SELECT f.early_age, round(avg(f.factor), 6), count(*)::integer FROM f GROUP BY f.early_age
$$;

-- The factor used for an age (bounded to 1…28): the own one with enough lots, else the initial one (a parameter).
CREATE FUNCTION qa.age_factor(company uuid, age integer) RETURNS TABLE (factor numeric, source text)
  LANGUAGE sql STABLE AS $$
  SELECT coalesce(o.factor, qa.parameter_number(company, 'AGE_FACTOR_' || to_char(a.age, 'FM00'))), CASE WHEN o.factor IS NULL THEN 'INITIAL' ELSE 'OWN' END
  FROM (SELECT least(greatest(age, 1), 28) AS age) a
  LEFT JOIN qa.own_age_factors(company) o ON o.age_days = a.age AND o.lots >= qa.parameter_number(company, 'OWN_FACTOR_MIN_LOTS')
$$;

GRANT EXECUTE ON FUNCTION qa.own_age_factors(uuid), qa.age_factor(uuid, integer) TO rochell_app;
