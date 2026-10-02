# Outgoing mail (MAIL)

Approved errata E-MAIL-1…10 and E-MAIL-01-1…11 (`docs/architecture/errata.md`). Documents are sent to customers as a PDF attached
to an e-mail from `industrias@rochell.com.do`, through the Google Workspace SMTP relay. Invoices wait for the e-CF's QR (VS#4).

Plan: **MAIL-01** queue, dispatcher, SMTP transport, PDF renderer (this document) · **MAIL-02** the documents' HTML and the
commands that send them · **MAIL-03** screens, Playwright, staging configuration.

## MAIL-01 — queue and delivery (migration 0069)

### Schema

| Table | What |
| --- | --- |
| `core.mail_message` | One message: document (`document_type`, `document_id`, `document_no`, `party_id`), `recipients` (1–10, lower case), subject, plain-text body, `file_name`, the document's `html` snapshot; then `pdf` + `pdf_sha256` (rendered once), `status` QUEUED → SENT / FAILED (FAILED → QUEUED is the retry), `attempts` since it was last queued, `next_attempt_at`, `last_error`, `sent_at`, `delivery_mode` (LIVE / REDIRECT), `delivered_to` (the envelope), who asked and the event of the command that asked |
| `core.mail_attempt` | Append-only trail: one row per attempt of the dispatcher (numbered over the message's whole life), SENT or FAILED with its error |

What was queued is immutable (guard trigger; the application role holds UPDATE only on the delivery columns); a SENT message never
changes; rows are never deleted. Not a business aggregate: no state history — the dispatcher is not a command and the trail is
`core.mail_attempt`.

### Code

- `Rochell.Platform.Mail.MailOutbox.EnqueueAsync(context, MailDraft, requestEventId, requestedBy)` — called inside the command that
  sends a document, so the message exists only if the command commits. Recipients are trimmed, lower-cased, de-duplicated; an empty
  list, more than 10 or a malformed address is `MAIL_RECIPIENT_INVALID`; subject (one line, ≤ 200), body (≤ 5,000) and the `.pdf`
  file name are `MAIL_FIELD_INVALID`.
- `MailDispatcher.DispatchPendingAsync` — per company, claims one due QUEUED message at a time (`FOR UPDATE SKIP LOCKED`), renders
  the PDF if it has none (`IPdfRenderer`; must start with `%PDF-`), sends it (`IMailTransport`), and records the attempt in the
  same transaction. A failure waits 1, 5, 15, 60 minutes; the fifth leaves it FAILED (E-MAIL-01-10). Delivery is at-least-once: a
  crash between the SMTP acceptance and the commit sends it again.
- Modes (`MailDelivery`, E-MAIL-01-4): **Off** — nothing is dispatched, messages wait. **Redirect** — the envelope carries only
  `RedirectTo`; the subject becomes `[Redirigido — para: first@… +N] …` and the body starts with the intended recipients; no customer
  address and no blind copy. **Live** — to the recipients, blind copy to `ArchiveBcc` (E-MAIL-01-5).
- Host (`Rochell.Api/Mail`): `SmtpMailTransport` (MailKit: STARTTLS, EHLO name `Smtp:LocalDomain`, optional user / password),
  `GotenbergPdfRenderer` (POST `/forms/chromium/convert/html`, letter, 0.5 in margins, backgrounds), `MailService` (every
  `Mail:Interval`, 15 s).

### Configuration (`Rochell:Mail`)

| Key | Meaning |
| --- | --- |
| `Mode` | `Off` (default), `Redirect`, `Live`. Outside Off the host refuses to start without `FromAddress`, `Smtp:Host` and `RendererUrl` (and `RedirectTo` in Redirect) |
| `FromAddress`, `FromName` | The one sender (E-MAIL-01-1); name «Industrias Rochell» |
| `RedirectTo`, `ArchiveBcc` | The internal mailbox of Redirect; the archive copy of Live |
| `MaxAttempts`, `Interval` | 5; 00:00:15 |
| `RendererUrl` | The Gotenberg container, e.g. `http://pdf:3000` (image `gotenberg/gotenberg:8.37.0-chromium`, internal network only) |
| `Smtp:Host`, `Port`, `StartTls`, `LocalDomain`, `User`, `Password`, `Timeout` | Workspace relay: `smtp-relay.gmail.com`, 587, STARTTLS, the server's host name, no user (the relay authorizes the server's address, E-MAIL-01-3) |

### Tests

- `Rochell.Platform.Tests.MailDispatcherTests` (recording transport, fake renderer, `FakeClock`): Live with the archive copy and
  the PDF kept with its SHA-256; Redirect without customer addresses; the 1 / 5 / 15 / 60 minute waits, FAILED at the fifth and the
  retry; a renderer that returns no PDF; Off; three dispatchers at once send each message once; validation and immutability.
- `Rochell.Api.Tests.MailDeliveryTests` (`MailFixture`: Mailpit and the production Gotenberg image): the host's service renders
  and sends a queued message in Redirect mode — the attachment received is the PDF whose SHA-256 was stored; a Live envelope
  reaches its recipients and the blind copy; the host does not start in Live without its settings.

## MAIL-02 — the documents and the commands that send them (migration 0070)

| Command (`Rochell.Sales/Mail`) | Permission (E-MAIL-01-8) | Sendable when (E-MAIL-01-7) |
| --- | --- | --- |
| `SendQuoteByEmail` | `quote:email` — Vendedor | SENT and not expired, or CONVERTED |
| `SendProformaByEmail` | `proforma:email` — Facturación | Not VOIDED |
| `SendDeliveryByEmail` | `delivery:email` — Facturación, Despacho | After the gate-out |
| `SendStatementByEmail` (customer, from, to) | `statement:email` — Cobros | Always (≤ 366 days, as on screen) |
| `SendArAgingByEmail` (customer, today) | `statement:email` — Cobros | The customer has open invoices or proformas |
| `RetryDocumentEmail` | `mail:retry` — the four roles | The message is FAILED: the same content goes to the same recipients |

- Each command reads its document through the print query's own handler inside its transaction (`DocumentMail.ReadAsync`), so the
  PDF carries exactly what the print view shows; `DocumentHtml` turns it into self-contained HTML (inline CSS, no scripts, only
  `& < > " '` escaped). `MAIL_DOCUMENT_NOT_SENDABLE` otherwise.
- Texts (E-MAIL-01-9): subject `<Documento> <número> — <razón social>`; body «Estimado cliente:», one fixed sentence naming the
  document, the sender's optional message (≤ 2,000 characters), «Atentamente,», the sender's name and the company.
- The command's result id is the mail's id; event `DocumentEmailRequested` (aggregate `DocumentMail`), `DocumentEmailRetried`.
- `MailSwitch` (host: mode ≠ Off): with mail off the commands answer `MAIL_DISABLED` instead of queueing messages that would leave,
  stale, the day the mode changes.
- `mail_message_document_type_known`: QUOTE, PROFORMA, DELIVERY, STATEMENT, AR_AGING. Invoices are not sent (E-MAIL-01-2).
- Queries (`sales:read`): `GET /sales/mail?documentType=&documentId=` (for a statement or the aging the document is the customer)
  and `GET /sales/mail/{mailId}/pdf` (file name, SHA-256, base64 of the PDF sent).
- Tests: `Rochell.Sales.Tests.DocumentMailTests` — who may send what, when; the texts; the HTML against hand-derived amounts; the
  history, the PDF and the retry. `MailDeliveryTests` renders a quote's HTML with the production renderer: one letter page.

## MAIL-03 — screens and staging

- `GET /api/v1/environment` also returns `mailMode` (OFF, REDIRECT, LIVE); `GET /sales/mail` also returns `savedEmails`, the e-mails
  kept for the document's customer (E-MAIL-5).
- Web: `components/DocumentMail.tsx` (`QuoteMail`, `ProformaMail`, `DeliveryMail`, `StatementMail`, `AgingMail`) on the quote, the
  proforma, the delivery note and the statement of account; `lib/mail.ts` (unit-tested). See `web.md`.
- Dev stack: mail in Redirect mode with `RecordingMailTransport` and `FakePdfRenderer` — nothing leaves; Constructora Uno has two
  saved e-mails. Playwright `mail-journey.spec.ts` (desktop and mobile).
- Staging: compose service `pdf` and the `MAIL_*` variables, Off until set; the Workspace relay steps are in `staging.md`.

