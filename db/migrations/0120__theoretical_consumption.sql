-- MFG2 · the recipe's theoretical consumption for machines without a batch plant (approved errata E-MFG2-15).
--   A portal draft of a machine paired without a batch plant takes, once its shift ended, the recipe's theoretical consumption
--   (recipe per unit × blocks) as THEORETICAL: it can be posted like a typed one. A machine with a batch plant still waits for its
--   post (PENDING, E-MFG2-8); a draft whose shift is still running stays PENDING too.

ALTER TABLE mfg.shift_summary
  DROP CONSTRAINT shift_summary_consumption_source,
  ADD CONSTRAINT shift_summary_consumption_source CHECK (consumption_source IN ('MANUAL', 'BATCH_PLANT', 'PENDING', 'THEORETICAL')
    AND (consumption_source = 'BATCH_PLANT') = (portal_consumption_id IS NOT NULL)
    AND (consumption_reason IS NULL OR length(btrim(consumption_reason)) BETWEEN 10 AND 300)
    AND (status = 'DRAFT' OR consumption_source <> 'PENDING')
    AND (consumption_source <> 'THEORETICAL' OR source = 'PORTAL'));
