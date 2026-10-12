-- MFG3-06 · breaks and the scheduled end of a portal shift (approved errata E-MFG3-12…17).
--   - E-MFG3-12: the portal sends each shift's breaks (lunch); they are not planned time and the stoppages inside them do not count.
--   - E-MFG3-14: the portal lengthens a shift that started late («reponer tiempo»); planned time ends at the scheduled end it now sends.
-- Readings from a portal without these fields keep NULL and are computed as before (the window's end, no breaks).

ALTER TABLE mfg.portal_reading
  ADD COLUMN scheduled_to timestamp,
  ADD COLUMN breaks       jsonb,
  ADD CONSTRAINT portal_reading_schedule CHECK ((scheduled_to IS NULL OR scheduled_to > window_from) AND (breaks IS NULL OR jsonb_typeof(breaks) = 'array'));
