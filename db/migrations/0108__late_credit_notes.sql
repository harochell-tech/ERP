-- X1-01 · credit notes issued more than 30 days after their invoice carry no ITBIS (approved errata E-X1-5, E-X1-01-1…3).
--   A DRAFT note's ITBIS is recomputed when it is issued: its tax and total, and its lines' ITBIS, may go to zero while it is still DRAFT.
--   Nothing else of a note changes; a CONFIRMED note never changes.

CREATE OR REPLACE FUNCTION sal.credit_note_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.commercial_status <> 'DRAFT' OR NEW.version <> 1 THEN
      RAISE EXCEPTION 'sal.credit_note: a credit note is created DRAFT with version 1';
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'sal.credit_note rows cannot be deleted';
  END IF;
  -- E-X1-01-2: a DRAFT note issued after 30 days loses its ITBIS (tax 0, total = net); nothing else of its content moves.
  IF NOT (OLD.commercial_status = 'DRAFT' AND NEW.tax_total = 0 AND NEW.total = NEW.net_total)
     AND ROW(NEW.tax_total, NEW.total) IS DISTINCT FROM ROW(OLD.tax_total, OLD.total) THEN
    RAISE EXCEPTION 'sal.credit_note: its content, posting and e-NCF never change once set';
  END IF;
  IF ROW(NEW.credit_note_id, NEW.company_id, NEW.credit_note_no, NEW.invoice_id, NEW.party_id, NEW.reason_category, NEW.reason, NEW.net_total, NEW.created_by)
     IS DISTINCT FROM ROW(OLD.credit_note_id, OLD.company_id, OLD.credit_note_no, OLD.invoice_id, OLD.party_id, OLD.reason_category, OLD.reason, OLD.net_total, OLD.created_by)
     OR NEW.version <> OLD.version + 1
     OR (OLD.commercial_status <> 'DRAFT' AND ROW(NEW.credit_date, NEW.posting_event_id, NEW.issued_by) IS DISTINCT FROM ROW(OLD.credit_date, OLD.posting_event_id, OLD.issued_by))
     OR (OLD.encf IS NOT NULL AND NEW.encf IS DISTINCT FROM OLD.encf) THEN
    RAISE EXCEPTION 'sal.credit_note: its content, posting and e-NCF never change once set';
  END IF;
  RETURN NEW;
END $$;

CREATE OR REPLACE FUNCTION sal.credit_note_line_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'sal.credit_note_line rows cannot be changed or deleted';
  END IF;
  IF TG_OP = 'UPDATE' THEN
    -- E-X1-01-2: only the ITBIS, only to zero, only while the note is DRAFT.
    IF ROW(NEW.credit_note_line_id, NEW.company_id, NEW.credit_note_id, NEW.line_no, NEW.invoice_line_id, NEW.net_amount, NEW.rate)
         IS DISTINCT FROM ROW(OLD.credit_note_line_id, OLD.company_id, OLD.credit_note_id, OLD.line_no, OLD.invoice_line_id, OLD.net_amount, OLD.rate)
       OR NEW.itbis <> 0
       OR NOT EXISTS (SELECT 1 FROM sal.credit_note n WHERE n.credit_note_id = NEW.credit_note_id AND n.commercial_status = 'DRAFT') THEN
      RAISE EXCEPTION 'sal.credit_note_line rows cannot be changed or deleted';
    END IF;
    RETURN NEW;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM sal.credit_note n WHERE n.credit_note_id = NEW.credit_note_id AND n.commercial_status = 'DRAFT') THEN
    RAISE EXCEPTION 'sal.credit_note_line: lines are written only while the credit note is DRAFT';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM sal.invoice_line il JOIN sal.credit_note n ON n.invoice_id = il.invoice_id
                 WHERE il.invoice_line_id = NEW.invoice_line_id AND n.credit_note_id = NEW.credit_note_id) THEN
    RAISE EXCEPTION 'sal.credit_note_line: the line belongs to another invoice';
  END IF;
  RETURN NEW;
END $$;

GRANT UPDATE (tax_total, total) ON sal.credit_note TO rochell_app;
GRANT UPDATE (itbis) ON sal.credit_note_line TO rochell_app;
