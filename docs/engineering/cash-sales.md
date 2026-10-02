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

## CF1-02 — the cash sale and its payment (migration 0073; E-CF1-02-1…3)

| Command (`Rochell.Sales/CashSales`) | Permission | What |
| --- | --- | --- |
| `CreateCashSale` | `cash_sale:create` | A DRAFT order of the company's «Consumidor final» with its buyer. The consumer is created by the first sale (`FinalConsumerCreated`, ACTIVE, under an advisory lock) |
| `UpdateCashSaleDraft` | `cash_sale:create` | Header, lines and buyer of a DRAFT |
| `SubmitCashSaleForPayment` | `cash_sale:create` | DRAFT → PENDING_PAYMENT; `payment_total` = net + the ITBIS of today's rules (`TaxEngine.PreviewSalesItbisAsync`). Needs the `CONSUMER_ID_THRESHOLD` rule in force (`CONSUMER_ID_RULE_MISSING`); from its amount the buyer's identification is required (`BUYER_ID_REQUIRED`), compared with the order's total with ITBIS (E-CF1-02-3) |
| `ReturnCashSaleToDraft` | `cash_sale:create` | PENDING_PAYMENT → DRAFT, with nothing assigned |
| `AllocateReceiptToOrder` | `receipt:apply` | A receipt of the consumer assigned to the order, up to what is still to pay (`ALLOCATION_EXCEEDS_DUE`) and to what the receipt has free; no journal. Confirms the order at once when the money that counts covers it |
| `ReleaseOrderAllocation` | `receipt:apply` | Releases an assignment with a reason while the order is PENDING_PAYMENT |
| `ConfirmCashSale` | `cash_sale:create` | «Verificar pago»: confirms when covered, else `CASH_SALE_NOT_PAID` (E-CF1-02-1: a cheque is matched in Treasury, a separate module, so someone asks again) |

- **Money that counts** (`CashSaleStore.PaidAsync`): live assignments of RECORDED receipts — cash and transfers at once, a cheque
  only when its deposit is MATCHED with the bank statement — plus what the order's invoices already took.
- **Dispatch** (E-CF1-02-2): `PlanDelivery` and `RecordGateOut` refuse a cash order that is not covered. A bounced cheque releases
  its assignments (`MarkReceiptBounced`), the order keeps its state, and nothing more is planned or leaves until it is paid again.
- **Credit commands refuse cash sales** (`USE_CASH_SALE`): `CreateSalesOrder` for the consumer, `UpdateSalesOrderDraft`,
  `SubmitForCredit`. A quote of the consumer converts into a cash order. `CancelSalesOrder` also cancels a PENDING_PAYMENT order and
  refuses one with receipts assigned (the paid cancellation comes in CF1-03).
- **Fiscal rule kind `CONSUMER_ID_THRESHOLD`**: `{"amount":"250000.00"}`; computes no tax (no test runs, READY with its source,
  one active at a time); `TaxEngine.ConsumerIdThresholdAsync`. Migration 0073 exempts it from the test-run gate, like the 606
  classification. The rule screen's guided form for it comes with CF1-05; until then it is loaded with the configuration load.
- 194 commands. Tests: `Rochell.Sales.Tests.CashSaleTests` (CF-01…07).

## CF1-03 — invoice, e-CF record, credit note and paid cancellation (migration 0074; E-CF1-03-1…3)

- **Invoice.** `CreateInvoiceFromDeliveries` for the final consumer takes the delivery lines of one order (`CASH_INVOICE_ONE_ORDER`),
  copies its buyer to `sal.invoice` and refuses a fiscal authorization (E-CF1-9); the e-CF type is 32. `IssueInvoice` locks the cash
  order first (sales order → proformas → invoice → AR document → receipts), makes the invoice due the day it is issued (no terms)
  and, after P-18, calls `CashSaleStore.InheritAsync`: the receipts assigned to the order, oldest first, are released and applied to
  the new AR document (P-25) up to the invoice's total; what an assignment has left is assigned again to the order for its next
  deliveries (E-CF1-13). The result carries `collectedOnOrder`; the invoice is born PAID.
- **e-CF record.** `FiscalReceiver` gives who the e-CF names: the customer's RNC or cédula; for the consumer the buyer's cédula or
  RNC, the passport (`ReceiverPassport`, new optional member of `RecordExternalFiscalDocument` and
  `RecordExternalCreditNoteDocument`), or nobody (`ReceiverRnc` empty or null). Anything else is `FISCAL_DOCUMENT_MISMATCH`.
  Migration 0074: the receiver check of 0071 also covers a credit note's record, through the invoice it credits.
- **Credit note and refund (E-CF1-8, E-CF1-03-3).** No new command: Cobros undoes the paid invoice's application (`UnapplyReceipt`),
  Facturación creates the note and another person issues it (e-CF 34, P-22), Cobros applies the receipt again to what the invoice
  still owes, and what is left on the receipt is refunded with `PrepareCustomerRefund` (TRANSFER or CHEQUE only) and
  `ReleaseCustomerRefund` by the Controller (P-36).
- **`CancelCashSale`** (`cash_sale:create`: Vendedor, Caja; E-CF1-03-2): DRAFT, PENDING_PAYMENT or CONFIRMED without deliveries
  made or planned; releases every receipt assigned (`ReceiptOrderAllocationReleased`) and cancels with the reason. The money stays
  on the receipts, to be refunded or assigned to another sale. 195 commands.
- Tests: `Rochell.Sales.Tests.CashSaleInvoiceTests` (CF-08…11).

