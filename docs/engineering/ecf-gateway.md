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
