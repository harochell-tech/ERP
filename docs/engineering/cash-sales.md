# Cash sales to final consumers (CF-1)

Baseline: `docs/architecture/cf1/frozen-baseline-cf1.md`; approved errata E-CF1-1…14 and E-CF1-01-1…9.

## CF1-01 — schema (migration 0071)

| Object | What |
| --- | --- |
| `md.party` | `party_kind = 'CONSUMER'`: one per company (`party_consumer_uq`), no RNC, a customer and never a supplier, no contact data (`party_consumer`); it is not renamed or blocked and no party becomes it (`party_consumer_guard`). No credit terms (`customer_terms_not_consumer`) and no saved e-mails (`party_email_not_consumer`). It is created by the first cash sale's command (CF1-02), with its events — a migration cannot write the customer's state history |
| `sal.sales_order` | `cash_sale` — true for the consumer's orders and only for them (guard on insert), immutable; `buyer_name`, `buyer_phone`, `buyer_id_kind` (CEDULA 11 digits, RNC 9, PASAPORTE 5–20 A–Z 0–9), `buyer_id` — only on a cash order, only while DRAFT; `payment_total` (net + ITBIS, fixed when sent to payment); `allocated_amount`. State `PENDING_PAYMENT`: a cash order never is PENDING_CREDIT and a credit order never PENDING_PAYMENT; `CONFIRMED` only from PENDING_PAYMENT with `allocated_amount ≥ payment_total`; DRAFT or CANCELLED keep nothing assigned; never `exemption_pending` |
| `fin.order_allocation` | A receipt assigned to a cash order of its customer; a release is an inverse row of the same receipt, order and amount; append-only. `fin.receipt.allocated_amount` counts proformas and orders |
| `sal.invoice` | `buyer_name`, `buyer_id_kind`, `buyer_id`: only on an e-CF 32, written when the invoice is created (no UPDATE privilege) |
| `tax.external_fiscal_record` | `receiver_rnc` nullable and `receiver_passport`: only the consumer's e-CF 32 is recorded without a receiver RNC (`external_fiscal_record_receiver`) |
| `tax.fiscal_rule` | Rule kind `CONSUMER_ID_THRESHOLD` (E-CF1-3) |
| IAM | `cash_sale:create` → VENDEDOR, CAJA; role `CAJA` (`cash_sale:create`, `sales:read`, `receipt:record`, `receipt:apply`). 127 permissions |

Whether a cheque counts (its deposit matched with the statement, E-CF1-4) is decided by the command that confirms the order
(CF1-02): the schema only requires the assigned amount to cover what must be paid.

Tests: `Rochell.Sales.Tests.CashSaleSchemaTests`.
