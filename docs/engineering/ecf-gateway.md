# e-CF gateway with Alanube (VS#4)

Baseline: `docs/architecture/vs4/frozen-baseline-vs4.md` (E-VS4-1…14). Alanube's API: `docs/fiscal/alanube-api.md`.

## VS4-01 — schema (migration 0095, E-VS4-01-1…10)

| Table | What it holds |
| --- | --- |
| `tax.ecf_series` | A DGII-authorized e-NCF range per e-CF type (31, 32, 34, 44): optional DGII authorization, `range_from`…`range_to`, `next_number`, `valid_until` (the e-CF's `sequenceDueDate`). DRAFT → ACTIVE (four eyes) → CLOSED → CANCELLED (the unused tail voided through Alanube, E-VS4-12); one ACTIVE per type; live ranges of a type never overlap; `next_number` only moves forward and only on an ACTIVE range; an approved range never changes. At the cut-over from the previous provider a range starts at the first number it did not use (E-VS4-01-1). |
| `tax.ecf_document` | One e-CF attempt of an invoice or credit note: type, e-NCF (unique per company), range, status (PENDING, SUBMITTED, UNKNOWN_OUTCOME, ACCEPTED, ACCEPTED_CONDITIONAL, REJECTED, REQUIRES_ACTION, CONTINGENCY), the payload sent and its SHA-256, Alanube's id and trackId, security code, signature date, stamp (QR) URL, the DGII answer, poll schedule. What was sent never changes; a final answer never changes; one live (non-rejected) attempt per source. |
| `tax.ecf_call` | Every call to Alanube (SUBMIT, QUERY, CANCEL, WEBHOOK, DOWNLOAD): mode, HTTP status, outcome, provider code, message, time, duration — never the token. Append-only. |
| `tax.ecf_file` | The signed XML and PDF of an e-CF, kept in the database (and its daily backups), append-only (E-VS4-01-6). |

Invoices and credit notes gain the fiscal statuses `ECF_SENDING`, `ECF_ACCEPTED`, `ECF_REJECTED`, `ECF_ACTION` beside `PENDING_EXTERNAL` /
`ACCEPTED_EXTERNAL` (the manual channel, now contingency): the e-NCF is on the invoice once accepted; a rejected invoice is corrected and
sent with another e-NCF when the amounts stand, or voided (E-VS4-01-2/3). A credited invoice is `ACCEPTED_EXTERNAL` or `ECF_ACCEPTED`.

`md.uom.dgii_code`: kg 21, l 24, m³ 28, t 39, un 43 (E-VS4-01-9). REVENUE_ACCOUNTING gains `ecf_range_alert_pct` (fraction left) and
`ecf_range_alert_days` (E-VS4-01-4). Permissions `ecf_series:prepare` (Especialista fiscal), `ecf_series:approve` (Controller), an SoD
pair, and `ecf:resolve` (Especialista fiscal, Facturación) — 141 permissions, 49 SoD rules.

The gateway's mode (Off / Sandbox / Production), Alanube's URL and token and the webhook secret are server settings, never in the
repository (E-VS4-01-7) — VS4-02.

## VS4-02 — gateway, queue and status polling (migration 0096, E-VS4-02-1…7)

**Ranges.** `PrepareEcfSeries` (Especialista fiscal: type, first and last number — the 10 digits after `E` + type —, due date,
optional DGII authorization) → `ApproveEcfSeries` (Controller, step-up, not the preparer; closes the type's ACTIVE range) /
`DiscardEcfSeries`; `CloseEcfSeries` (Controller) closes an ACTIVE range. `ListEcfSeries` shows each range with its next e-NCF and how
many numbers are left.

**Queue.** `EcfQueue.EnqueueAsync` is what Sales calls inside the command that issues an invoice or credit note (VS4-03): it locks the
type's ACTIVE range, takes its next number (an exhausted range closes; none or an expired one refuses the issuance with
`ECF_SERIES_MISSING` / `ECF_SERIES_EXPIRED`), builds the e-CF with the e-NCF and the range's due date, and inserts a PENDING
`tax.ecf_document` with its SHA-256.

**Steps.** `AdvanceEcfDocument` (permission `ecf:process`, the daily process only) takes one e-CF one move forward:

| From | Alanube answers | To |
| --- | --- | --- |
| PENDING / UNKNOWN_OUTCOME / CONTINGENCY without Alanube's id | 201 | SUBMITTED (or the DGII answer at once) |
| | duplicate with id (AP3011) | SUBMITTED, following that id |
| | duplicate without id, or content refused (400) | REQUIRES_ACTION, with Alanube's code |
| | nothing within 30 s, network error, 5xx | UNKNOWN_OUTCOME (or CONTINGENCY) |
| SUBMITTED / CONTINGENCY with id | FINISHED + ACCEPTED / ACCEPTED_WITH_OBSERVATIONS | ACCEPTED / ACCEPTED_CONDITIONAL, then the signed files |
| | FINISHED + REJECTED, or FAILED | REJECTED, reason «DGII: …» or «Alanube: …» |
| | still in process | SUBMITTED, next query on the schedule |
| | not found | REQUIRES_ACTION |
| any non-final, 24 h after queued | — | REQUIRES_ACTION |

Every call is a `tax.ecf_call` row (never the token). Every status change has its event and history row, and the source's
`IEcfSourceUpdater` (Sales, VS4-03) is told inside the same transaction when the e-CF reaches an answer or needs attention.
`ListDueEcfDocuments` lists the e-CF whose next query has come and the accepted ones still without both files.

**Contingency.** `tax.ecf_gateway_state` counts consecutive failures per company. After REVENUE_ACCOUNTING `ecf_contingency_minutes`
without a good answer, every e-CF in flight moves to CONTINGENCY (retried every 15 min); the first good answer ends it and returns them
to the queue (PENDING, or SUBMITTED when Alanube holds them).

**API.** `EcfService` runs every `Rochell:Ecf:Interval` (15 s) as PROCESO_DIARIO when the mode is not Off. `POST /api/v1/ecf/webhook`
needs `X-Rochell-Ecf-Secret` and only runs `NudgeEcfDocuments` (brings the query forward). Settings, all server-side:

| Setting | Values |
| --- | --- |
| `Rochell__Ecf__Mode` | `OFF` (default), `SANDBOX`, `PRODUCTION`, `SIMULATED` (Development / Test only) |
| `Rochell__Ecf__BaseUrl` | `https://sandbox.alanube.co/dom/v1/` or `https://api.alanube.co/dom/v1/` |
| `Rochell__Ecf__Token` | Alanube's token (a secret in the server's environment file) |
| `Rochell__Ecf__WebhookSecret` | the value Alanube is configured to send in `X-Rochell-Ecf-Secret` |
| `Rochell__Ecf__Interval`, `Rochell__Ecf__CallTimeout` | `00:00:15`, `00:00:30` |

Endpoints: `ecf/prepare-ecf-series`, `approve-ecf-series`, `discard-ecf-series`, `close-ecf-series`, `advance-ecf-document`,
`nudge-ecf-documents`; `GET ecf/series`, `GET ecf/due`. 258 commands, 142 permissions.

## VS4-03 — e-CF from invoices and credit notes (migration 0097, E-VS4-03-1…11)

**Channel.** `IssueInvoice` and `IssueCreditNote` send through the gateway when the deployment's `EcfSwitch` is on (mode not Off)
and the document's e-CF type has an ACTIVE range (`EcfQueue.UsesGatewayAsync`); the document is then `ECF_SENDING` and its e-CF is
queued in the same transaction (the result carries `ecfNumber`). Otherwise it is `PENDING_EXTERNAL` as before (manual channel).

**Issuer.** `md.company` gains `address` (required by Alanube, ≤ 100), `trade_name`, `phone` (809-555-1234) and `email`, set by
`UpdateCompanyContact` (company:manage, step-up) on Configuración › Empresa; without an address the gateway refuses with
`ECF_ISSUER_INCOMPLETE`.

**Payload** (`Rochell.Sales/Ecf/EcfPayloads.cs`):

| Part | From |
| --- | --- |
| `idDoc` | e-NCF; `sequenceDueDate` = the range's due date (not on 32 / 34); `taxAmountIndicator` 0 when there is ITBIS (not on 44); `incomeType` 1; `paymentType` 2 with `paymentDeadline` and `paymentTerm` («30 días») when due after the invoice date, else 1; `paymentFormsTable`: credit → form 4, cash → the receipts applied to the invoice (cash 1, cheque / transfer 2). 34: `creditNoteIndicator` (1 after 30 days), `incomeType`, `paymentType` |
| `sender` | RNC, legal name (cut to 150), trade name, address, phone, e-mail, `internalInvoiceNumber` (FA- / NC-), `stampDate` |
| `buyer` | 31 / 44: the customer's RNC and legal name; 32: only an identified buyer (cédula / RNC, or a passport as `foreignIdentifier`); never the e-mail. 44: `additionalInformation` «CONFOTUR certificación …» |
| `itemDetails` | per line: internal item code, billing indicator 1 (the sales ITBIS rate) or 4 (exempt, every 44 line), good 1 / service 2 (freight and SERVICE items), DGII unit code, quantity (≤ 2 decimals, else `ECF_PAYLOAD_INVALID`), unit price (4 decimals), amount. 34: the credited invoice line — its quantity and price when credited whole, else 1 × the credited amount |
| `totals` | `totalTaxedAmount` / `i1AmountTaxed`, `exemptAmount`, `itbisS1` (rate × 100), `itbisTotal` / `itbis1Total` = Core's ITBIS, `totalAmount` |
| `informationReference` (34) | the invoice's e-NCF and date, code 1 (the note credits the whole invoice) or 3, the reason cut to 90 |
| `config.pdf.note` (44) | «Exento de ITBIS por CONFOTUR, certificación …» |

Before queueing, the lines are checked against the document (net, ITBIS, total to the cent; one ITBIS rate; 44 all exempt) —
`ECF_PAYLOAD_INVALID` otherwise.

**Answers.** `InvoiceEcfUpdater` / `CreditNoteEcfUpdater` (registered in the API for the worker) move the document from
`ECF_SENDING` / `ECF_ACTION`: accepted → `ECF_ACCEPTED` with the e-NCF on the document; rejected → `ECF_REJECTED`; needs attention →
`ECF_ACTION`, each with an event (`InvoiceFiscalStatusChanged`, `CreditNoteFiscalStatusChanged`). A rejected document is sent again by
`ResendInvoiceEcf` / `ResendCreditNoteEcf` (invoice:issue / credit_note:issue, step-up): a new attempt with the next e-NCF, built from
the current customer and company data. `VoidUnfiscalizedInvoice` also voids a rejected invoice. Credit notes are offered on
`ECF_ACCEPTED` invoices too. FISC-DOC counts `ECF_SENDING`, `ECF_REJECTED` and `ECF_ACTION` as not fiscalized. 261 commands.
