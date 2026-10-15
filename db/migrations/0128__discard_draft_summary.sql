-- MFG2-05 · Cancelling a run whose shift summary is still a draft (E-MFG2-19).
-- The draft is discarded with the run (status DISCARDED, its reason in core.state_history); nothing was posted, so nothing is
-- reversed. A posted or reversed summary still blocks the cancellation (it is reversed as before).

ALTER TABLE mfg.shift_summary
  DROP CONSTRAINT shift_summary_status,
  ADD CONSTRAINT shift_summary_status CHECK (status IN ('DRAFT', 'POSTED', 'REVERSED', 'DISCARDED')),
  DROP CONSTRAINT shift_summary_posted,
  ADD CONSTRAINT shift_summary_posted CHECK ((status IN ('DRAFT', 'DISCARDED')) = (posted_by IS NULL) AND (status IN ('DRAFT', 'DISCARDED')) = (material_event_id IS NULL)
    AND (status IN ('DRAFT', 'DISCARDED')) = (receipt_event_id IS NULL));

CREATE OR REPLACE FUNCTION mfg.summary_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'mfg.shift_summary rows cannot be deleted';
  END IF;
  IF NEW.run_id <> OLD.run_id OR NEW.recorded_by <> OLD.recorded_by OR NEW.version <> OLD.version + 1
     OR NOT ((OLD.status = NEW.status AND OLD.status = 'DRAFT') OR (OLD.status = 'DRAFT' AND NEW.status IN ('POSTED', 'DISCARDED'))
             OR (OLD.status = 'POSTED' AND NEW.status = 'REVERSED'))
     OR (OLD.status <> 'DRAFT' AND ROW(NEW.batches, NEW.good_units, NEW.mix_scrap_units, NEW.fresh_scrap_units)
                                   IS DISTINCT FROM ROW(OLD.batches, OLD.good_units, OLD.mix_scrap_units, OLD.fresh_scrap_units))
     OR (NEW.status = 'DISCARDED' AND ROW(NEW.batches, NEW.good_units, NEW.mix_scrap_units, NEW.fresh_scrap_units)
                                      IS DISTINCT FROM ROW(OLD.batches, OLD.good_units, OLD.mix_scrap_units, OLD.fresh_scrap_units)) THEN
    RAISE EXCEPTION 'mfg.shift_summary: % → % or a change of a posted summary is not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;
