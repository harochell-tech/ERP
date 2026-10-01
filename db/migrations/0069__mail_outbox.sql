-- MAIL-01 · Outgoing mail (approved errata E-MAIL-1…10, E-MAIL-01-1…11): the queue of documents sent to customers by e-mail. A
-- command snapshots the document as HTML and queues the message in its own transaction; the dispatcher renders the PDF, sends it
-- through SMTP and records every attempt. What was sent stays: recipients, subject, body, the HTML and the exact PDF.

-- ---------------------------------------------------------------------------------------------
-- The message. QUEUED → SENT, or QUEUED → FAILED after the attempts allowed (E-MAIL-01-10); FAILED → QUEUED is the retry asked
-- by a person. Not a business aggregate: its trail is core.mail_attempt, written by the dispatcher (no command, no state history).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE core.mail_message (
  mail_id           uuid        NOT NULL,
  company_id        uuid        NOT NULL,
  document_type     text        NOT NULL,
  document_id       uuid        NOT NULL,
  document_no       text        NOT NULL,
  party_id          uuid,
  recipients        text[]      NOT NULL,
  subject           text        NOT NULL,
  body_text         text        NOT NULL,
  file_name         text        NOT NULL,
  html              text        NOT NULL,
  pdf               bytea,
  pdf_sha256        text,
  status            text        NOT NULL,
  attempts          integer     NOT NULL DEFAULT 0,
  next_attempt_at   timestamptz NOT NULL,
  last_error        text,
  sent_at           timestamptz,
  delivery_mode     text,
  delivered_to      text[],
  request_event_id  uuid        NOT NULL,
  requested_by      uuid        NOT NULL,
  requested_at      timestamptz NOT NULL,
  CONSTRAINT mail_message_pk PRIMARY KEY (mail_id),
  CONSTRAINT mail_message_company_uq UNIQUE (company_id, mail_id),
  CONSTRAINT mail_message_company_fk FOREIGN KEY (company_id) REFERENCES md.company (company_id),
  CONSTRAINT mail_message_party_fk FOREIGN KEY (company_id, party_id) REFERENCES md.party (company_id, party_id),
  CONSTRAINT mail_message_event_fk FOREIGN KEY (company_id, request_event_id) REFERENCES core.domain_event (company_id, event_id),
  CONSTRAINT mail_message_requested_by_fk FOREIGN KEY (requested_by) REFERENCES iam.user (user_id),
  CONSTRAINT mail_message_document_type CHECK (document_type ~ '^[A-Z][A-Z_]{1,39}$'),
  CONSTRAINT mail_message_document_no CHECK (length(btrim(document_no)) BETWEEN 1 AND 100),
  CONSTRAINT mail_message_recipients CHECK (cardinality(recipients) BETWEEN 1 AND 10 AND array_position(recipients, NULL) IS NULL),
  CONSTRAINT mail_message_subject CHECK (length(btrim(subject)) BETWEEN 1 AND 200 AND subject !~ '[\r\n]'),
  CONSTRAINT mail_message_body CHECK (length(body_text) BETWEEN 1 AND 5000),
  CONSTRAINT mail_message_file_name CHECK (file_name ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,95}\.pdf$'),
  CONSTRAINT mail_message_html CHECK (length(html) BETWEEN 1 AND 2000000),
  CONSTRAINT mail_message_pdf CHECK ((pdf IS NULL) = (pdf_sha256 IS NULL) AND (pdf_sha256 IS NULL OR pdf_sha256 ~ '^[0-9a-f]{64}$')),
  CONSTRAINT mail_message_status CHECK (status IN ('QUEUED', 'SENT', 'FAILED')),
  CONSTRAINT mail_message_attempts CHECK (attempts >= 0),
  CONSTRAINT mail_message_delivery_mode CHECK (delivery_mode IS NULL OR delivery_mode IN ('LIVE', 'REDIRECT')),
  CONSTRAINT mail_message_sent CHECK ((status = 'SENT') = (sent_at IS NOT NULL AND delivery_mode IS NOT NULL AND delivered_to IS NOT NULL AND pdf IS NOT NULL)),
  CONSTRAINT mail_message_failed CHECK (status <> 'FAILED' OR last_error IS NOT NULL)
);
CREATE INDEX mail_message_document ON core.mail_message (company_id, document_type, document_id, requested_at);
CREATE INDEX mail_message_pending ON core.mail_message (next_attempt_at) WHERE status = 'QUEUED';

CREATE FUNCTION core.mail_message_guard() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  address text;
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'core.mail_message rows cannot be deleted';
  END IF;
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'QUEUED' OR NEW.attempts <> 0 OR NEW.pdf IS NOT NULL OR NEW.last_error IS NOT NULL THEN
      RAISE EXCEPTION 'core.mail_message: a message is queued QUEUED, without attempts or PDF';
    END IF;
    FOREACH address IN ARRAY NEW.recipients LOOP
      IF address !~ '^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$' OR length(address) > 200 OR address <> lower(address) THEN
        RAISE EXCEPTION 'core.mail_message: % is not a valid lower-case e-mail address', address;
      END IF;
    END LOOP;
    RETURN NEW;
  END IF;
  IF ROW(NEW.mail_id, NEW.company_id, NEW.document_type, NEW.document_id, NEW.document_no, NEW.party_id, NEW.recipients, NEW.subject, NEW.body_text, NEW.file_name, NEW.html,
         NEW.request_event_id, NEW.requested_by, NEW.requested_at)
     IS DISTINCT FROM ROW(OLD.mail_id, OLD.company_id, OLD.document_type, OLD.document_id, OLD.document_no, OLD.party_id, OLD.recipients, OLD.subject, OLD.body_text, OLD.file_name,
                          OLD.html, OLD.request_event_id, OLD.requested_by, OLD.requested_at) THEN
    RAISE EXCEPTION 'core.mail_message: what was queued is immutable';
  END IF;
  IF OLD.pdf IS NOT NULL AND (NEW.pdf IS DISTINCT FROM OLD.pdf OR NEW.pdf_sha256 IS DISTINCT FROM OLD.pdf_sha256) THEN
    RAISE EXCEPTION 'core.mail_message: the PDF is rendered once';
  END IF;
  IF OLD.status = 'SENT' THEN
    RAISE EXCEPTION 'core.mail_message: a sent message does not change';
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
       (OLD.status = 'QUEUED' AND NEW.status IN ('SENT', 'FAILED')) OR (OLD.status = 'FAILED' AND NEW.status = 'QUEUED')) THEN
    RAISE EXCEPTION 'core.mail_message: % → % is not allowed', OLD.status, NEW.status;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER mail_message_guard BEFORE INSERT OR UPDATE OR DELETE ON core.mail_message FOR EACH ROW EXECUTE FUNCTION core.mail_message_guard();
CREATE TRIGGER mail_message_no_truncate BEFORE TRUNCATE ON core.mail_message FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

ALTER TABLE core.mail_message ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON core.mail_message
  USING (company_id = nullif(current_setting('app.company_id', true), '')::uuid)
  WITH CHECK (company_id = nullif(current_setting('app.company_id', true), '')::uuid);
GRANT SELECT, INSERT ON core.mail_message TO rochell_app;
GRANT UPDATE (pdf, pdf_sha256, status, attempts, next_attempt_at, last_error, sent_at, delivery_mode, delivered_to) ON core.mail_message TO rochell_app;

-- ---------------------------------------------------------------------------------------------
-- Every attempt of the dispatcher, append-only: when, how it ended and, when it failed, why. Numbered over the message's whole
-- life (mail_message.attempts counts only those since it was last queued).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE core.mail_attempt (
  company_id    uuid        NOT NULL,
  mail_id       uuid        NOT NULL,
  attempt_no    integer     NOT NULL,
  attempted_at  timestamptz NOT NULL,
  outcome       text        NOT NULL,
  error         text,
  CONSTRAINT mail_attempt_pk PRIMARY KEY (mail_id, attempt_no),
  CONSTRAINT mail_attempt_message_fk FOREIGN KEY (company_id, mail_id) REFERENCES core.mail_message (company_id, mail_id),
  CONSTRAINT mail_attempt_no_positive CHECK (attempt_no >= 1),
  CONSTRAINT mail_attempt_outcome CHECK (outcome IN ('SENT', 'FAILED')),
  CONSTRAINT mail_attempt_error CHECK ((outcome = 'FAILED') = (error IS NOT NULL))
);
CREATE TRIGGER mail_attempt_append_only BEFORE UPDATE OR DELETE ON core.mail_attempt FOR EACH ROW EXECUTE FUNCTION core.reject_mutation();
CREATE TRIGGER mail_attempt_no_truncate BEFORE TRUNCATE ON core.mail_attempt FOR EACH STATEMENT EXECUTE FUNCTION core.reject_mutation();

ALTER TABLE core.mail_attempt ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON core.mail_attempt
  USING (company_id = nullif(current_setting('app.company_id', true), '')::uuid)
  WITH CHECK (company_id = nullif(current_setting('app.company_id', true), '')::uuid);
GRANT SELECT, INSERT ON core.mail_attempt TO rochell_app;
