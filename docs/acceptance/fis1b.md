# FIS-1b acceptance — the CONFOTUR proforma as a collection document

Baseline FIS-1b (`docs/architecture/fis1/frozen-baseline-fis1b.md`) §5, with the approved errata E-FIS1b-1…11, E-FIS1b-01-1…14 and
E-FIS1b-05-1. Every acceptance ID has at least one test tagged `[Trait("AcceptanceFis1b", "<ID>")]`;
`AcceptanceFis1bTraceabilityTests` fails if an ID has no tagged test or is missing from this matrix (E-FIS1b-01-14).

## Matrix

| ID | What | Tests |
| --- | --- | --- |
| PRF-01 | A delivery of an order marked "exemption in process" issues PF-000001: the delivery's lines, the ITBIS of the rule in force, due at the customer's days; no journal | `ProformaTests.PRF01_a_delivery_of_a_marked_order_issues_its_proforma_without_a_journal` |
| PRF-02 | An order without the mark: no proforma, invoiced from its delivery as always | `ProformaTests.PRF02_an_order_without_the_mark_has_no_proforma_and_is_invoiced_from_its_delivery` |
| PRF-03 | A receipt of 11,800.00 allocated to a proforma that collects with ITBIS: balance 0, credit used goes down, the receipt stays an advance in the ledger | `ProformaAllocationTests.PRF03_a_receipt_allocated_to_a_proforma_settles_it_and_frees_credit_without_a_journal` |
| PRF-04 | A proforma that collects without ITBIS takes at most its net | `ProformaAllocationTests.PRF04_a_proforma_that_collects_without_ITBIS_takes_its_net_and_an_allocation_is_released_whole` |
| PRF-05 | An overdue proforma with a balance counts as overdue days in the credit check | `ProformaAllocationTests.PRF05_an_overdue_proforma_with_a_balance_counts_as_overdue_days` |
| PRF-06 | Two proformas in an ACTIVE authorization → one e-CF 44; what was allocated is applied up to the total, the ITBIS advanced stays as credit balance | `ProformaInvoiceTests.PRF06_a_certification_of_two_proformas_gives_an_eCF_44_that_inherits_the_net_and_leaves_the_ITBIS_as_credit_balance` |
| PRF-07 | Without the certification, a proforma collected with ITBIS becomes a paid e-CF 31 | `ProformaInvoiceTests.PRF07_without_the_certification_a_proforma_collected_with_ITBIS_becomes_a_paid_eCF_31` |
| PRF-08 | A proforma collected without ITBIS, invoiced with ITBIS: the ITBIS is the open balance | `ProformaInvoiceTests.PRF08_a_proforma_collected_without_ITBIS_leaves_the_ITBIS_open_on_its_eCF_31` |
| PRF-09 | The credit balance after an e-CF 44 is refunded: Cobros prepares, the Controller releases (E-FIS1b-05-1), Treasury matches it with the statement; DEV-000001 | `CustomerRefundTests.PRF09_the_ITBIS_advanced_is_refunded_by_two_people_and_matched_with_the_statement` |
| PRF-10 | A bounced cheque releases its allocations; the proforma recovers its balance | `ProformaAllocationTests.PRF10_a_bounced_cheque_releases_its_allocations` |
| PRF-11 | A delivery with a proforma is not invoiced through the delivery path | `ProformaTests.PRF11_a_delivery_with_a_proforma_is_not_invoiced_through_the_delivery_path` |
| PRF-12 | A proforma without collections or invoice is voided with a reason; its delivery returns to normal invoicing | `ProformaInvoiceTests.PRF12_a_proforma_without_collections_is_voided_and_its_delivery_returns_to_normal_invoicing` |
| PRF-13 | A month with proformas: PROFORMA-ASIG and the existing reconciliations MATCHED | `ProformaReconciliationTests.PRF13_a_month_with_proformas_allocations_and_an_invoice_from_a_proforma_reconciles`; tampering is an exception: `ProformaReconciliationTests.A_proforma_or_a_receipt_that_disagrees_with_its_allocations_or_its_delivery_is_an_exception` |
| E2E-P1 | Marked order → deliveries → proformas → receipt with ITBIS → certification of two proformas → e-CF 44 → refund, over the API and the UI | API: `Rochell.Api.Tests` · `ProformaAcceptanceTests.E2EP1_proformas_collected_with_ITBIS_certified_invoiced_as_eCF_44_and_the_ITBIS_refunded_over_HTTP`; UI: `web/e2e/proforma-journey.spec.ts` (desktop and mobile) |

Tests without a project name are in `Rochell.Sales.Tests`. Also: the POD of a site delivery, the missing ITBIS rule and the order
mark (`ProformaTests`), the aging and the statement (`ProformaAllocationTests`), an authorization that cites proformas and the void
of their invoice (`ProformaInvoiceTests`), the void of a prepared refund (`CustomerRefundTests`).

## Open conditions

- **X-1** — the accountant confirms that the ITBIS collected on a proforma before its e-CF is a customer deposit (not ITBIS payable)
  and when the taxable event occurs for goods delivered under a proforma.
- **A-01** — the Controller approves P-36 (customer refund) and its account roles in the parallel run.
- **B-02** — second reviewer of the ledger PRs (P-36, the release of allocations at `IssueInvoice`).
