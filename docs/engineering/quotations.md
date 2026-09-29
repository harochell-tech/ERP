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
