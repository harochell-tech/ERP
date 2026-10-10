-- LAB1-03d · early release rules (approved errata E-LAB1-11…17; they amend baseline D-04 / D-05).
--   - `qa.item_spec` gains the minimum average at 1 day (initial release) and at 3 days (final release), and whether the item is tested
--     at all (`lab_tested`: Block 4" is not structural and is never tested, E-LAB1-16).
--   - `qa.lot_evaluation` keeps the two checks — the best of the breaks at 1…2 days and the breaks at 3 days — and the verdict NOT_TESTED.
--   - Parameters: the ages of each check and the starting values the requirement form offers (40 and 70 kg/cm²).

ALTER TABLE qa.item_spec
  ADD COLUMN min_avg_1d numeric(18,6),
  ADD COLUMN min_avg_3d numeric(18,6),
  ADD COLUMN lab_tested boolean NOT NULL DEFAULT true,
  ADD CONSTRAINT item_spec_early_minimums CHECK ((min_avg_1d IS NULL OR min_avg_1d > 0) AND (min_avg_3d IS NULL OR min_avg_3d > 0)),
  ADD CONSTRAINT item_spec_not_tested CHECK (lab_tested OR (min_avg_1d IS NULL AND min_avg_3d IS NULL AND min_avg_28d IS NULL AND min_individual_28d IS NULL));

ALTER TABLE qa.lot_evaluation
  ADD COLUMN initial_check text,
  ADD COLUMN initial_age integer,
  ADD COLUMN initial_specimens integer,
  ADD COLUMN initial_avg numeric(18,6),
  ADD COLUMN initial_required numeric(18,6),
  ADD COLUMN final_check text,
  ADD COLUMN final_specimens integer,
  ADD COLUMN final_avg numeric(18,6),
  ADD COLUMN final_required numeric(18,6),
  ADD CONSTRAINT lot_evaluation_checks CHECK ((initial_check IS NULL OR initial_check IN ('PASS', 'FAIL', 'FEW_SPECIMENS'))
    AND (final_check IS NULL OR final_check IN ('PASS', 'FAIL', 'FEW_SPECIMENS'))
    AND (initial_check IS NULL) = (initial_avg IS NULL) AND (final_check IS NULL) = (final_avg IS NULL)),
  DROP CONSTRAINT lot_evaluation_verdict,
  ADD CONSTRAINT lot_evaluation_verdict CHECK (verdict IN ('COMPLIES', 'FAILS', 'NO_SPEC', 'NO_DATA', 'NOT_TESTED')
    AND (verdict IN ('COMPLIES', 'FAILS')) = (min_avg_required IS NOT NULL AND strength_28d IS NOT NULL)
    AND (verdict IN ('NO_SPEC', 'NOT_TESTED')) = (min_avg_required IS NULL));

INSERT INTO qa.parameter_default (code, name, kind, number_value, text_value, min_value, max_value, whole, sort_order) VALUES
  ('INITIAL_RELEASE_MAX_AGE', 'Liberación inicial: edad máxima de las probetas (días; desde 1)', 'NUMBER', 2, NULL, 1, 7, true, 160),
  ('FINAL_RELEASE_AGE', 'Liberación final: edad de las probetas (días)', 'NUMBER', 3, NULL, 1, 28, true, 170),
  ('DEFAULT_MIN_AVG_1D', 'Requisito sugerido: promedio mínimo a 1 día (kg/cm²)', 'NUMBER', 40, NULL, 1, 500, false, 180),
  ('DEFAULT_MIN_AVG_3D', 'Requisito sugerido: promedio mínimo a 3 días (kg/cm²)', 'NUMBER', 70, NULL, 1, 500, false, 190);
