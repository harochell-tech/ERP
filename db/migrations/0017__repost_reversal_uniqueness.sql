-- PR-14 · A value entry can be reversed once by its document (receipt / invoice reversal) and once by a repost (E-PR14-3:
-- the repost reversal points to the entry it reverses). Separate migration: 'REPOST' is compared as an enum value in index
-- predicates, which is only possible once 0016 (which adds it) has committed.
ALTER TABLE inv.inv_value_entry DROP CONSTRAINT inv_value_entry_single_reversal_uq;
CREATE UNIQUE INDEX inv_value_entry_single_document_reversal_uq ON inv.inv_value_entry (reverses_value_entry_id)
  WHERE reverses_value_entry_id IS NOT NULL AND movement_type <> 'REPOST';
CREATE UNIQUE INDEX inv_value_entry_single_repost_reversal_uq ON inv.inv_value_entry (reverses_value_entry_id)
  WHERE reverses_value_entry_id IS NOT NULL AND movement_type = 'REPOST';
