# CF-1 acceptance — cash sales to the final consumer (e-CF 32)

Baseline CF-1 (`docs/architecture/cf1/frozen-baseline-cf1.md`) §5, with the approved errata E-CF1-1…14, E-CF1-01-1…9, E-CF1-02-1…3,
E-CF1-03-1…3 and E-CF1-05-1…14. Every acceptance ID has at least one test tagged `[Trait("AcceptanceCf1", "<ID>")]`;
`AcceptanceCf1TraceabilityTests` fails if an ID has no tagged test or is missing from this matrix (E-CF1-05-11).

## Matrix

| ID | What | Tests |
| --- | --- | --- |
| CF-01 | The first cash sale creates «Consumidor final» once; the order is a draft with its buyer | `CashSaleTests.CF01_the_first_cash_sale_creates_the_final_consumer_once_and_a_draft_order_with_its_buyer` |
| CF-02 | A sale of 5,000.00 net sent to payment: PENDING_PAYMENT for 5,900.00 (the ITBIS of the day); never through credit | `CashSaleTests.CF02_03_a_sale_sent_to_payment_owes_its_net_plus_ITBIS_and_is_confirmed_when_cash_assigned_covers_it_without_a_journal` |
| CF-03 | A cash receipt of 5,900.00 assigned: CONFIRMED, no journal beyond the receipt's | the same test as CF-02 |
| CF-04 | A cheque assigned, not deposited or not matched: still pending; confirmed once the deposit is matched with the statement | `CashSaleTests.CF04_05_a_cheque_counts_once_its_deposit_is_matched_with_the_statement_and_a_bounce_stops_the_dispatch` |
| CF-05 | A cash order not paid in full: planning a delivery is refused | the same test as CF-04 |
| CF-06 | A sale over the rule's amount without identification: not sent to payment | `CashSaleTests.CF06_07_the_buyers_identification_is_mandatory_from_the_rules_amount_and_without_the_rule_nothing_goes_to_payment` |
| CF-07 | Without a `CONSUMER_ID_THRESHOLD` rule in force: refused with a clear message | the same test as CF-06 |
| CF-08 | A paid order picked up in two deliveries: two e-CF 32 with the buyer, each born paid from what was assigned to the order | `CashSaleInvoiceTests.CF08_each_delivery_of_a_paid_sale_is_invoiced_as_an_eCF_32_with_its_buyer_and_is_born_paid` |
| CF-09 | An E32 without identification is recorded without a receiver; with a passport, the passport is kept | `CashSaleInvoiceTests.CF09_the_eCF_32_is_recorded_without_a_receiver_or_with_the_buyers_passport` |
| CF-10 | A credit note (e-CF 34) on a paid E32 and the refund DEV-… released by the Controller, through the bank | `CashSaleInvoiceTests.CF10_a_credit_note_on_the_eCF_32_and_a_refund_through_the_bank_return_money_to_the_consumer` |
| CF-11 | A paid order without deliveries is cancelled: its assignments are released, the receipt is left to refund | `CashSaleInvoiceTests.CF11_a_paid_sale_without_deliveries_is_cancelled_and_its_receipt_is_left_free_to_refund` |
| CF-12 | A month with cash sales: CASH-SALE and the existing reconciliations MATCHED; a delivery without full payment blocks | `CashSaleReconciliationTests.CF12_a_month_with_cash_sales_paid_delivered_and_invoiced_reconciles`; the exceptions: `CashSaleReconciliationTests.A_delivery_left_unpaid_by_a_bounced_cheque_blocks_and_cash_kept_without_depositing_is_warned_about` |
| E2E-C1 | Cash sale → payment → delivery → e-CF 32 → close, over the API and the UI | API: `Rochell.Api.Tests` · `CashSaleAcceptanceTests.E2EC1_cash_sale_paid_picked_up_twice_invoiced_as_eCF_32_and_the_month_closed_over_HTTP`; UI: `web/e2e/cash-sale-journey.spec.ts` (desktop and mobile) |

Tests without a project name are in `Rochell.Sales.Tests`. Also: releasing an assignment and what a sale with money assigned refuses
(`CashSaleTests`), what the screen reads — buyer, amounts, which payments count, free receipts, the identification amount and the
preview (`CashSaleTests`), the missing alert parameter and a tampered order (`CashSaleReconciliationTests`), the schema
(`CashSaleSchemaTests`), the rule's guided form and the payment checks of the screen (`web/tests/unit/cashSales.test.ts`).

## Open conditions

- **X-1** — the accountant confirms the amount from which a consumer's invoice must identify its buyer, with its DGII norm; until the
  `CONSUMER_ID_THRESHOLD` rule is active no cash sale goes to payment.
- **A-01** — the Controller approves `cash_deposit_alert_days` (REVENUE_ACCOUNTING); without it CASH-SALE fails, and blocks AR-REC,
  as soon as cash or cheques wait for their deposit.
- **VS#4** — the DGII summary of low-amount e-CF 32 and the invoice by e-mail wait for the e-CF provider.
- **B-02** — second reviewer of the ledger PRs (the invoice of a cash order inherits its collections through P-25).
