# FIS-2 acceptance — report 606 and IT-1 / IR-17 summaries

Baseline FIS-2 (`docs/architecture/fis2/frozen-baseline-fis2.md`) §3, with the approved errata E-FIS2-1…14 and E-FIS2-01…03.
Every acceptance ID has at least one test tagged `[Trait("AcceptanceFis2", "<ID>")]`; `AcceptanceFis2TraceabilityTests` fails if
an ID has no tagged test or is missing from this matrix (E-FIS2-03-7).

## Matrix

| ID | What | Tests |
| --- | --- | --- |
| F2-01 | Two invoices (one paid with a withholding, one on credit): two records with the 23 fields and the right codes | `Rochell.Treasury.Tests` · `Report606Tests.F201_the_606_of_the_month_carries_each_invoice_with_its_payment_withholding_and_method_and_its_CSV_follows_the_DGII_tool` |
| F2-02 | A reversed invoice is not reported (and a withholding paid next month comes back then, without ITBIS) | `Rochell.Treasury.Tests` · `Report606Tests.F202_a_reversed_invoice_is_not_reported_and_a_withholding_paid_next_month_is_reported_then_without_ITBIS` |
| F2-03 | CSV in the DGII tool's column order, decimal point, no thousands separator | `Rochell.Treasury.Tests` · `Report606Tests.F201_…`; API (no BOM): `AcceptanceTests.E2EF2_…` |
| F2-04 | IT-1 summary: sales by e-CF type, ITBIS = ITBIS_PAYABLE of the month | `Rochell.Sales.Tests` · `It1SummaryTests.F204_the_IT1_summary_has_sales_by_eCF_type_credit_notes_and_withholdings_and_its_ITBIS_matches_the_ledger` |
| F2-05 | IR-17 summary: withholdings by type = the month's determinations | `Rochell.Treasury.Tests` · `Report606Tests.F201_…` |
| F2-06 | TAX-606 warns of an ITBIS difference (and of missing classifications and ISR types) | `Rochell.Treasury.Tests` · `Report606Tests.F206_TAX_606_warns_of_an_ITBIS_difference_a_missing_classification_and_an_ISR_withholding_without_its_type`; a consistent slice is MATCHED: `CloseTests.A_consistent_slice_reconciles_with_no_findings` |
| E2E-F2 | Purchase → invoice → payment with a withholding → 606, CSV and summaries, over the API and the UI | API: `Rochell.Api.Tests` · `AcceptanceTests.E2EF2_purchase_invoice_payment_with_withholding_then_606_CSV_and_summaries_over_HTTP`. UI: `web/e2e/fiscal-reports-journey.spec.ts` (Playwright) |

## Open conditions

- **X-1** — the accountant confirms: 607 / 608 not filed as a fully electronic issuer (E-FIS2-1); the payment-month record
  for a withholding of an earlier NCF (E-FIS2-3, E-FIS2-02-11).
- **A-01** — the Analista / Especialista activate the real 606 classification and the ISR withholding types on the rules.
- **No real data** until B-02 or a zero-difference parallel run (E-VS1-2).
