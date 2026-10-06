# USD-1 acceptance — imports and foreign suppliers in USD

Baseline USD-1 (`docs/architecture/usd1/frozen-baseline-usd1.md`) §4, with the approved errata E-USD-1…15, E-USD1-01-1…7, E-USD1-02-1…8,
E-USD1-03-1…10, E-USD1-04-1…9, E-USD1-05-1…9, E-USD1-05b-1…5, E-USD1-06-1…5 and E-USD1-07-1…7. Every acceptance ID has at least one test
tagged `[Trait("AcceptanceUsd1", "<ID>")]`, or is listed as pending; `AcceptanceUsd1TraceabilityTests` fails if an ID has no tagged test and is
not pending, or is missing from this matrix (E-USD1-07-7).

## Matrix

| ID | What | Tests |
| --- | --- | --- |
| USD-01 | The day's rate prepared by Tesorería, approved by the Controller; the preparer cannot approve; without the day's rate a USD document is refused | `ExchangeRateTests.USD01_a_rate_is_prepared_by_Tesoreria_approved_by_the_Controller_and_without_the_days_rate_none_applies`; the weekend rule and corrections: `ExchangeRateTests.A_weekend_takes_Fridays_rate_a_weekday_needs_its_own_and_a_correction_supersedes_the_rate_of_its_day` |
| USD-02 | A foreign supplier's order in USD, approved like any order | `ForeignInvoiceTests.USD02_an_order_in_USD_is_approved_on_its_peso_value_and_billed_at_its_USD_price` |
| USD-03 | The invoice of USD 10,000.00 posts 600,000.00 to the asset (480,000.00) and the parts (120,000.00); no NCF, ITBIS or withholding | `ForeignInvoiceTests.USD03_a_foreign_invoice_of_USD_10000_posts_600000_pesos_to_the_asset_and_the_parts_against_foreign_payables` |
| USD-04 | Imported raw material received against «Importaciones por liquidar» | **Pending**: raw material in USD waits until it is imported (E-USD1-03-1, E-USD1-04-8) |
| USD-05 | Freight USD 1,000.00, duties 30,000.00, agent 15,000.00: 105,000.00 → 84,000.00 / 21,000.00; the forklift costs 564,000.00 | `ImportSettlementTests.USD05_the_shipments_105000_of_costs_go_84000_to_the_forklift_and_21000_to_the_parts` |
| USD-06 | The DUA's ITBIS 124,200.00 is recoverable; the 606 | `ImportSettlementTests.A_DUA_is_owed_to_a_local_supplier_once_per_number_and_reversed_while_unpaid`; the customs ITBIS in the IT-1 summary: `FxRevaluationTests.USD10_…`. The DUA stays out of the 606 until the accountant confirms (X-1, E-USD1-06-5) |
| USD-07 | USD 10,000.00 paid from a peso account at 61.00: bank 610,000.00, realized loss 10,000.00, payable zero | `ForeignPaymentTests.USD07_USD_10000_paid_from_a_peso_account_at_61_costs_610000_and_a_realized_loss_of_10000`; partial payments from a USD account: `ForeignPaymentTests.From_a_USD_account_at_the_days_rate_partial_payments_relieve_the_payable_pro_rata_and_the_last_takes_the_rest` |
| USD-08 | Open USD 10,000.00 at 60.50: unrealized loss 5,000.00, reversed the next day | `FxRevaluationTests.USD08_an_open_invoice_of_USD_10000_at_60_50_carries_a_loss_of_5000_on_the_last_day_reversed_the_next` |
| USD-09 | A USD bank account: paid and reconciled in USD; the peso ledger squares | `BankTransferTests.USD09_USD_bought_from_the_peso_account_are_matched_in_USD_and_the_USD_account_reconciles_in_USD`; payments from a USD account: `ForeignPaymentTests` |
| USD-10 | AP-GL (pesos and USD), «Importaciones por liquidar» and banks reconcile | `FxRevaluationTests.USD10_import_clearing_reconciles_per_DUA_warns_when_unsettled_and_the_IT1_shows_the_customs_ITBIS`; AP-GL in USD within `FxRevaluationTests.USD08_…`; BANK-GL in USD within `BankTransferTests.USD09_…` |
| E2E-U1 | Rate → order → invoice → DUA → settlement → payment with exchange difference → revaluation, over the API | `UsdAcceptanceTests.E2EU1_rate_order_invoice_DUA_settlement_revaluation_and_payment_with_exchange_difference_over_HTTP`; the screens: Playwright `usd-journey.spec.ts` |

## Open

- **X-1** (accountant): the DUA in the 606 and the IT-1, the ISR withholding on services from abroad, the base of the exchange difference.
- **A-01** (Controller): the accounts and maps of AP_FOREIGN, IMPORT_CLEARING, FX_GAIN, FX_LOSS, FX_UNREALIZED; rules P-38…P-43R; the policy
  parameter `import_settlement_alert_days`; fixed-asset categories.
- **B-02**: the ledger changes of USD1-03…06 (USD amounts in the row hash, the IMPORT subledger, P-38…P-43R, BANK-GL in USD).
- USD-04 (raw material in USD) and USD-2 (sales in USD) need their own decision when they are needed.
