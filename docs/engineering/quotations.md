# Quotations (QUO-1)

Frozen baseline: `docs/architecture/quo1/frozen-baseline-quo1.md` (E-QUO1-1…14). A quote posts nothing, moves no stock and issues
no e-CF.

## QUO1-01 — schema (migration 0057, E-QUO1-01-1…12)

| Table | Holds |
| --- | --- |
| `sal.quote` | `COT-000001` per company: customer, plant, dates (`valid_until >= quote_date`), delivery term and site, customer reference, notes, the price list version in force, status, net total, `lines_version`, author, `copied_from_quote_id`, the price approval (`price_approved_by`, `price_approved_at`, `approved_lines_version`), the order it became (`sales_order_id`) and the closing reason |
| `sal.quote_line` | Per `lines_version`: finished good, unit, quantity, list price, quoted price and net (2 decimals) |
| `sal.sales_order.quote_id` | The quote an order came from (QUOTED_AS): unique, part of the order's immutable identity |

Guards (`sal.quote_guard`, `sal.quote_line_guard`):
- Created DRAFT, version 1, no approval. Only a DRAFT quote changes its header or gets new lines; lines never change or disappear.
- Transitions: DRAFT → PENDING_APPROVAL / SENT / CANCELLED; PENDING_APPROVAL → DRAFT; SENT → CONVERTED / LOST / CANCELLED.
- The approval is written only when a PENDING_APPROVAL quote returns to DRAFT, for its current `lines_version`, by someone other
  than the author (CHECK). A quote with a line below its list price goes SENT only if `approved_lines_version = lines_version`, so
  editing the lines after an approval needs a new one.
- CONVERTED ⇔ `sales_order_id`; LOST / CANCELLED ⇔ a closing reason of 1 to 500 characters.
- Every status change has its `core.state_history` row (aggregate `Quote`).

Permissions: `quote:manage` (VENDEDOR), `quote:approve_price` (APROBADOR_POLITICAS), reading with `sales:read`; SoD pair
(`quote:approve_price`, `quote:manage`). Tests: `QuoteSchemaTests`.

## QUO1-02 — commands, queries and print (E-QUO1-02-1…12)

| Command | Permission | Does |
| --- | --- | --- |
| `CreateQuote` | `quote:manage` | DRAFT `COT-…` for a DRAFT or ACTIVE customer; each line at its given price or the list's (list in force, `PRICE_MISSING` otherwise); validity from today |
| `UpdateDraftQuote` | `quote:manage` | New header and a new lines version (an earlier approval no longer covers them) |
| `SubmitQuoteForApproval` | `quote:manage` | DRAFT → PENDING_APPROVAL when a line is below its list price without approval |
| `ApproveQuotePrices` | `quote:approve_price`, step-up | PENDING_APPROVAL → DRAFT with the current lines approved; never the author |
| `ReturnQuoteToDraft` | `quote:approve_price` | PENDING_APPROVAL → DRAFT with a reason, nothing approved |
| `SendQuote` | `quote:manage` | DRAFT → SENT (frozen) if not expired and special prices are approved |
| `MarkQuoteLost` / `CancelQuote` | `quote:manage` | SENT → LOST / DRAFT or SENT → CANCELLED, with a reason |
| `CopyQuote` | `quote:manage` | A new DRAFT from any quote with its quoted prices, list prices in force and a new validity |

Queries (`sales:read`): `GET /sales/quotes` (status, customer, `expiredOnly`), `/sales/quotes/{id}` (lines with list and quoted
prices, the approval and whether it covers the current lines, history, order, copies), `/sales/quotes/{id}/print` (informative
ITBIS with the SALES_ITBIS rule in force at the quote date; nothing is stored). "Expired" = SENT with `valid_until` before today,
computed on read. Tests: `QuoteTests` (QUO-01, QUO-02, QUO-03, QUO-07).
