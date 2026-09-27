-- VS2-04 · Payment reversal. Approved errata E-VS2-04-1…7.
-- A reversed payment has no live application (every one has its reversal row) and no live journal of its posting event (the
-- R-09 journal carries its exact reversal). E-VS2-04-1: a CLEARED payment's DEBIT statement line stays MATCHED to it; the bank's
-- return (a CREDIT line) is matched to the reversal in VS2-05.

CREATE OR REPLACE FUNCTION fin.payment_amount_allocated() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  p fin.payment;
  total numeric;
  expected numeric;
BEGIN
  SELECT * INTO p FROM fin.payment WHERE payment_id = NEW.payment_id;
  IF p.status::text = 'PREPARED' THEN
    SELECT coalesce(sum(amount), 0) INTO total FROM fin.payment_allocation WHERE payment_id = p.payment_id AND payment_version = p.version;
    expected := p.amount;
  ELSIF p.status::text IN ('RELEASED', 'CLEARED', 'REVERSED') THEN
    SELECT coalesce(sum(CASE WHEN a.reverses_application_id IS NULL THEN a.amount ELSE -a.amount END), 0) INTO total
    FROM fin.ap_application a WHERE a.payment_id = p.payment_id;
    expected := CASE WHEN p.status::text = 'REVERSED' THEN 0 ELSE p.amount END;
  ELSE
    RETURN NULL;
  END IF;
  IF total <> expected THEN
    RAISE EXCEPTION 'fin.payment %: live applications add up to %, expected % for %', p.payment_id, total, expected, p.status;
  END IF;
  RETURN NULL;
END $$;

CREATE OR REPLACE FUNCTION fin.payment_posting_evidence() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  live boolean;
BEGIN
  SELECT EXISTS (
    SELECT 1 FROM fin.gl_journal j
    WHERE j.company_id = NEW.company_id AND j.source_event_id = NEW.posting_event_id AND j.journal_type = 'AUTO'
      AND NOT EXISTS (SELECT 1 FROM fin.gl_journal r WHERE r.reverses_journal_id = j.journal_id)) INTO live;
  IF NEW.status::text IN ('RELEASED', 'CLEARED') AND NOT live THEN
    RAISE EXCEPTION 'fin.payment %: % without the journal of its posting event (K-25)', NEW.payment_id, NEW.status;
  END IF;
  IF NEW.status::text = 'REVERSED' AND live THEN
    RAISE EXCEPTION 'fin.payment %: REVERSED while the journal of its posting event is still live (K-25)', NEW.payment_id;
  END IF;
  RETURN NULL;
END $$;
