# GAS-1 acceptance — purchases of expenses and services

Baseline GAS-1 (`docs/architecture/gas1/frozen-baseline-gas1.md`) §5, with the approved errata E-GAS-1…12, E-GAS-01-1…11,
E-GAS-02-1…7, E-GAS-03-1…10, E-GAS-04-1…7, E-GAS-05-1…6, E-GAS-06-1…6 and E-GAS-07-1…8. Every acceptance ID has at least one
test tagged `[Trait("AcceptanceGas1", "<ID>")]`; `AcceptanceGas1TraceabilityTests` fails if an ID has no tagged test or is missing
from this matrix (E-GAS-07-8).

## Matrix

| ID | What | Tests |
| --- | --- | --- |
| GAS-01 | A category prepared by one person is approved by another; the preparer cannot approve it; a control account is refused | `ExpenseCategoryTests.A_batch_approves_what_it_can_and_reports_the_rest` |
| GAS-02 | ITBIS 18 % and 16 % in force together: each line carries only its own type | `Rochell.Tax.Tests` · `PurchaseTaxTypeTests.Each_expense_line_carries_only_the_components_of_its_own_tax_type` |
| GAS-03 | A repair of 10,000.00 + ITBIS 18 % without an order, under the approval amount: P-37 expense / ITBIS / AP | `ExpenseInvoiceTests.GAS03_a_repair_below_the_approval_amount_is_matched_and_posted_to_its_categorys_account` |
| GAS-04 | Over the approval amount: pending until the Controller approves it, never who registered it | `ExpenseInvoiceTests.GAS04_08_from_the_approval_amount_the_Controller_approves_and_each_tax_type_goes_to_its_accounts` |
| GAS-05 | Telephone 5,000.00 (Telecomunicaciones): ITBIS 900.00, ISC 500.00, CDT 100.00; payable 6,500.00 | the same test as GAS-04 |
| GAS-06 | Insurance 20,000.00: selective tax 3,200.00 to expense, no ITBIS; payable 23,200.00 | the same test as GAS-04 |
| GAS-07 | Consumption 2,000.00 with tip: ITBIS 360.00, tip 200.00 to expense; payable 2,560.00 | the same test as GAS-04 |
| GAS-08 | Fuel, exempt: no taxes; payable equals the net | the same test as GAS-04 |
| GAS-09 | An individual's service with withholding rules: withheld to their accounts; no ISR on a line that is a good | `ExpenseInvoiceTests.GAS09_an_individual_is_withheld_ISR_on_services_only_and_ITBIS_on_every_lines_ITBIS` |
| GAS-10 | An expense order of 10 services billed with 6 then 5: the second exceeds what is left; nothing is received | `ExpensePurchaseOrderTests.GAS10_an_expense_order_is_billed_up_to_what_it_ordered_closes_when_complete_and_reopens_with_a_reversal` |
| GAS-11 | An expense order is not in «Por recibir» and cannot be received | `ExpensePurchaseOrderTests.GAS11_an_expense_order_is_not_received_and_is_closed_or_cancelled_only_as_its_rules_allow` |
| GAS-12 | A document with inventory and expense lines is refused | `ExpensePurchaseSchemaTests.Order_lines_are_of_the_class_of_their_order`, `ExpensePurchaseSchemaTests.Invoice_lines_are_of_the_class_of_their_invoice` |
| GAS-13 | A posted expense invoice without payments is reversed: P-37R, everything at zero, the NCF free again | `ExpenseInvoiceTests.GAS13_a_posted_expense_invoice_without_payments_is_reversed_and_its_fiscal_number_is_free_again` |
| GAS-14 | The month's 606: the category's type, services or goods, ITBIS, selective tax, other taxes and tip in their fields; TAX-606 agrees | `ExpenseReport606Tests.GAS14_expense_invoices_report_their_categorys_type_services_and_goods_apart_and_each_tax_in_its_field` |
| GAS-15 | A posted expense invoice is paid and matched with the bank as an inventory one; AP-GL clean | `Rochell.Api.Tests` · `ExpenseAcceptanceTests.E2EG1_category_telephone_bill_approved_posted_paid_matched_and_in_the_606_over_HTTP` |
| E2E-G1 | Category → telephone bill without an order → approval → posting → payment → 606, over the API and the UI | API: the same test as GAS-15; UI: `web/e2e/expense-journey.spec.ts` (desktop and mobile) |

Tests without a project name are in `Rochell.Procurement.Tests`. Also: the category's commands and the pack load
(`ExpenseCategoryTests`), the schema and its guards (`ExpensePurchaseSchemaTests`), the tax types' gate and pack
(`PurchaseTaxTypeTests`), the expense invoice's requirements (`ExpenseInvoiceTests`), the order's price exception, its rules for an
invoice and the previews (`ExpensePurchaseOrderTests`), the TAX-606 warning (`ExpenseReport606Tests`), the guided tax type form
and the lines' checks (`web/tests/unit/expenses.test.ts`).

## Open conditions

- **X-1** — the accountant confirms each tax type against its norm (insurance ITBIS, the telecommunications base) before the
  Especialista fiscal activates it; staging has the six types READY.
- **A-01** — the Controller approves the 37 categories, P-37, the maps of `SELECTIVE_TAX_EXPENSE`, `OTHER_TAX_EXPENSE`,
  `LEGAL_TIP_EXPENSE` and `ITBIS_RECOVERABLE`, and a PURCHASING version with `expense_invoice_approval_threshold`.
- **B-02** — second reviewer of the ledger PRs (P-37 / P-37R).
