# VS#3 acceptance — sales, dispatch and receipts (order-to-cash)

Frozen Baseline VS#3 (`docs/architecture/vs3/frozen-baseline-vs3.md`) §9, with the approved errata E-VS3-1…17 and E-VS3-01…11.
Every acceptance ID has at least one test tagged `[Trait("AcceptanceVs3", "<ID>")]`; `AcceptanceVs3TraceabilityTests` fails if
an ID of the baseline has no tagged test or is missing from the matrix below (E-VS3-11-4).

## Matrix

| ID | What | Tests |
| --- | --- | --- |
| SAL-01 | Order within the limit: credit auto-approved, CONFIRMED | `SalesOrderTests.SAL01_an_order_within_the_limit_is_auto_approved_and_confirmed`; concurrency: `SalesOrderTests.Concurrent_submissions_of_one_customer_never_auto_approve_above_the_limit` |
| SAL-02 | Above the available credit: PENDING_CREDIT; Crédito approves, the seller cannot | `SalesOrderTests.SAL02_an_order_above_the_available_credit_waits_for_credit_which_the_seller_cannot_give` |
| SAL-03 | Pickup: control at the gate, P-16 (cost, revenue, contract asset) | `DeliveryTests.SAL03_a_pickup_transfers_control_at_the_gate_with_cost_revenue_and_contract_asset`; concurrency: `SalesConcurrencyTests.Gate_outs_that_exhaust_the_stock_at_once_never_leave_it_negative` |
| SAL-04 | Site delivery: P-15 at the gate, P-16 at the POD, no revenue before the POD | `DeliveryTests.SAL04_a_site_delivery_moves_to_transit_at_the_gate_and_recognizes_revenue_only_at_the_POD` |
| SAL-05 | POD with a shortage: control of what was received, P-30 for the difference | `DeliveryTests.SAL05_a_POD_with_a_shortage_transfers_only_what_was_received_returns_the_rest_and_writes_off_the_loss` |
| SAL-06 | Invoice of what was delivered: P-18, contract asset 0, never billed twice | `InvoiceTests.SAL06_the_invoice_of_the_delivered_quantity_moves_the_contract_asset_to_receivables_with_ITBIS_and_never_bills_twice` |
| SAL-07 | External e-CF with different totals refused; equal totals → ACCEPTED_EXTERNAL | `InvoiceTests.SAL07_an_external_eCF_with_different_totals_is_refused_and_one_that_matches_the_package_fiscalizes_the_invoice` |
| SAL-08 | Partial credit note: P-22, AR down, original e-NCF referenced | `CreditNoteTests.SAL08_a_partial_credit_note_posts_P22_lowers_the_receivable_and_references_the_original_eNCF`; concurrency: `SalesConcurrencyTests.Credit_notes_on_one_invoice_line_issued_at_once_never_credit_more_than_its_net` |
| SAL-09 | Two invoices of the same lines issued at once: one invoice for what was delivered | `InvoiceTests.SAL09_two_invoices_of_the_same_delivery_lines_issued_at_once_bill_the_delivery_only_once` |
| AR-01 | 118,000.00 invoice paid by a 100,000.00 receipt and an 18,000.00 withholding: PAID, P-23, P-25, P-27 | `ReceiptTests.AR01_a_transfer_and_the_customers_ITBIS_withholding_pay_the_invoice_with_P23_P25_P27`; concurrency: `ReceiptTests.Two_applications_of_the_same_receipt_at_once_never_apply_more_than_it_has` |
| AR-02 | Bounced cheque: applications reversed, invoice reopened | `ReceiptTests.AR02_a_bounced_cheque_undoes_its_applications_reopens_the_invoice_and_leaves_the_bank`; concurrency: `SalesConcurrencyTests.An_application_racing_the_bounce_of_its_cheque_leaves_a_coherent_receipt_and_invoice` |
| AR-03 | Transfer matched to the statement's CREDIT line; BANK-GL balances | `ReceiptTests.AR03_a_transfer_is_matched_to_the_CREDIT_line_and_BANK_GL_balances` |
| AR-04 | A month with sales and receipts: AR-GL, CONTRACT-ASSET, RECEIPT-APPL MATCHED, AR-REC closes | `ArCloseTests.AR04_a_month_with_sales_and_receipts_reconciles_and_AR_REC_closes`; concurrency: `SalesConcurrencyTests.A_receipt_dated_in_the_month_racing_its_AR_REC_close_never_posts_into_the_closed_month` |
| E2E-S1 | Order → delivery → POD → invoice → external e-CF → receipt → match, over the API and the UI | API: `Rochell.Api.Tests` · `SalesAcceptanceTests.E2ES1_order_delivery_POD_invoice_eCF_receipt_and_match_close_AR_REC_and_BANK_REC_over_HTTP` (E-VS3-09-7). UI: `web/e2e/sales-journey.spec.ts` (Playwright, VS3-10b) |
| INV-S | Random sequences keep the invariants (AR, contract asset, finished goods, credit) | `SalesPropertyTests.Random_sales_sequences_keep_every_invariant` (seeds 1–3 × 150 steps in CI; workflow `inv-s` for long runs, E-VS3-11-1/2/5) |

Tests without a project name are in `Rochell.Sales.Tests`.

## What VS3-11 found

The first long runs of the concurrency suite found that a second gate-out of the same order, which had only waited for the first
one's lock, was refused as a version conflict: the order's version was read before the lock and compared after it. VS3-11 locks
the order and takes it as it stands (`Orders.LockCurrentAsync`), with no approved rule changed (E-VS3-11-7).

## Open conditions (E-VS3-11-6)

- **B-02** — a second reviewer for the ledger PRs.
- **A-01** — the Controller approves the policy values (CREDIT with its AR aging buckets, REVENUE_ACCOUNTING with its alert
  thresholds), the account maps and the standard costs and prices.
- **A-02** — official DGII sources for sales ITBIS, customer withholdings and the taxable event (delivery or invoice, E-VS3-10);
  staging uses TEST sources.
- **X-1** — the e-CF provider and its test environment; VS#3 records the provider's e-CF by hand, the automatic gateway is VS#4.
- The real banks' statement formats (owner's samples).
- No real commercial, fiscal or accounting data until B-02 closes or a parallel run reconciles with zero difference
  (E-VS1-2, E-VS2-10).
