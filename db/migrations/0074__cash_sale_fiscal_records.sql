-- CF1-03 · The fiscal records of cash sales (approved errata E-CF1-6, E-CF1-8, E-CF1-01-7): the e-CF 34 that credits the final
-- consumer's e-CF 32 is recorded like it — without a receiver RNC, or with the buyer's passport. The check of 0071 looked only at
-- the invoice of the record; a credit note's record is checked through the invoice it credits.
CREATE OR REPLACE FUNCTION tax.external_fiscal_record_receiver() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.receiver_rnc IS NULL AND NOT EXISTS (
       SELECT 1 FROM sal.invoice i JOIN md.party p ON p.party_id = i.party_id
       WHERE i.invoice_id = coalesce(NEW.invoice_id, (SELECT n.invoice_id FROM sal.credit_note n WHERE n.credit_note_id = NEW.credit_note_id))
         AND i.ecf_type = '32' AND p.party_kind = 'CONSUMER') THEN
    RAISE EXCEPTION 'tax.external_fiscal_record: only the e-CF of the final consumer is recorded without a receiver RNC (E-CF1-01-7)';
  END IF;
  RETURN NEW;
END $$;
