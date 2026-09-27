# VS#2 acceptance — supplier payments and banks

Frozen Baseline VS#2 (`docs/architecture/vs2/frozen-baseline-vs2.md`) §9, with the approved errata E-VS2-1…10 and E-VS2-01…09.
Every acceptance ID has at least one test tagged `[Trait("AcceptanceVs2", "<ID>")]`; `AcceptanceVs2TraceabilityTests` fails if
an ID of the baseline has no tagged test or is missing from the matrix below (E-VS2-09-4).

## Matrix

| ID | What | Tests |
| --- | --- | --- |
| PAY-01 | Pay an invoice in full: open 0, R-09, AP-GL and PAY-APPL matched | `Rochell.Treasury.Tests` · `PaymentTests.Paying_an_invoice_in_full_clears_its_balance_and_posts_R09` |
| PAY-02 | One partial payment over two invoices | `PaymentTests.One_partial_payment_covers_the_first_invoice_and_part_of_the_second` |
| PAY-03 | Application above the open amount refused, nothing written | `PaymentTests.An_application_above_the_open_amount_is_refused_and_nothing_is_written` |
| PAY-04 | The preparer cannot release (command and CHECK) | `PaymentTests.The_preparer_cannot_release`; `Rochell.Finance.Tests` · `BankSchemaTests.Payment_four_eyes_method_and_supplier_account` |
| PAY-05 | Two releases of the same invoice at once: never a negative balance | `PaymentTests.Two_payments_of_the_same_invoice_released_at_once_never_leave_a_negative_balance`; also `PaymentConcurrencyTests.Concurrent_releases_against_one_invoice_never_overdraw_it` (8 racers) |
| PAY-06 | Supplier account payable 72 h after verification | `PartyBankAccountTests.A_verified_account_becomes_payable_72_calendar_hours_after_the_verification`; `PaymentTests.A_transfer_is_released_only_72_hours_after_the_supplier_account_was_verified` |
| PAY-07 | A new account is not payable until verified; the old one pays until replaced | `PartyBankAccountTests.A_new_account_is_not_payable_until_verified_and_held_and_the_old_one_pays_until_replaced`; `PaymentTests.A_new_unverified_account_is_refused_and_a_superseded_one_stops_paying` |
| PAY-08 | Reversal: exact reversal of R-09, balances restored, invoice reversible again | `PaymentReversalTests.Reversing_a_released_payment_restores_the_invoice_and_reverses_R09_exactly` |
| PAY-09 | Release without recent step-up: STEP_UP_REQUIRED | `PaymentTests.Release_needs_a_recent_reauthentication` |
| PAY-10 | The requester cannot verify the supplier account (command and CHECK) | `PartyBankAccountTests.The_requester_cannot_verify_and_evidence_is_mandatory`; `BankSchemaTests.Requester_cannot_verify_their_own_supplier_bank_account` |
| BNK-01 | Same or overlapping statement: no new lines, duplicates reported (IDM-04) | `BankStatementTests.The_same_file_is_refused_and_an_overlapping_one_adds_only_its_new_lines`; `PaymentConcurrencyTests.The_same_file_imported_concurrently_makes_one_statement` |
| BNK-02 | Suggested match confirmed: line MATCHED, payment CLEARED | `BankStatementTests.A_suggested_match_confirmed_by_the_treasurer_clears_the_payment` |
| BNK-03 | Bank charge recognized: R-10, line CHARGE_RECOGNIZED | `BankStatementTests.A_bank_charge_is_posted_with_R10_at_the_line_date` |
| BNK-04 | A month with matched payments and charges: BANK-GL MATCHED, BANK-REC closes | `Rochell.Reconciliation.Tests` · `BankReconciliationTests.A_month_with_matched_payments_and_charges_reconciles_and_BANK_REC_closes` |
| BNK-05 | Released payment without its line: in-transit item, MATCHED net of it | `BankReconciliationTests.A_released_payment_without_its_statement_line_is_an_in_transit_item` |
| E2E-01 | VS#1 flow + payment + statement, end to end | API: `Rochell.Api.Tests` · `AcceptanceTests.E2E01_invoice_payment_statement_match_charge_and_BANK_REC_close_over_HTTP` (E-VS2-07-6). UI: `web/e2e/treasury-journey.spec.ts` (Playwright, VS2-08) after `web/e2e/purchase-journey.spec.ts` |
| INV-P | Random prepare / release / reverse / reconcile sequences keep the invariants | `PaymentPropertyTests.Random_payment_and_bank_sequences_keep_every_invariant` (seeds 1–3 × 150 steps in CI; workflow `inv-p` for long runs, E-VS2-09-1/2/5) |

Tests without a project name are in `Rochell.Treasury.Tests`.

## Concurrency (E-VS2-09-3)

`PaymentConcurrencyTests`, 8 racers started together behind one gate:

| Scenario | Guarantee |
| --- | --- |
| Releases of 8 payments of the whole invoice | exactly one RELEASED, the rest APPLICATION_EXCEEDS_OPEN_AMOUNT; open amount 0, one R-09 journal |
| One line and two payments; two lines and one payment | one match each; the losers VERSION_CONFLICT / LINE_NOT_UNMATCHED / PAYMENT_NOT_RELEASED; no payment matched twice |
| The same file imported 8 times at once | one statement, one file, its lines once; the rest STATEMENT_ALREADY_IMPORTED |
| A charge racing the BANK-REC close of its month | never a closed month with the charge inside it: either the close wins and the charge is a late entry in the next month (the snapshot lists the unrecorded debit), or the charge wins, posts into the month, and the close is refused because its ledger group is not sealed yet (INTEGRITY_NOT_SEALED) |

## Long runs

The same property test at other seeds and lengths: Actions → `inv-p` → Run workflow (inputs `seeds`, e.g. `1,2,3,4,5,6,7,8`, and
`steps`, e.g. `600`). Locally: `ROCHELL_INVP_SEEDS=1,2,3,4,5,6,7,8 ROCHELL_INVP_STEPS=600 dotnet test tests/Rochell.Treasury.Tests
--filter PaymentPropertyTests`. Run on 2026-09-27: 8 seeds × 600 steps, all invariants held (333–436 commands per seed).

## Open conditions (E-VS2-09-6)

| # | Condition | Owner |
| --- | --- | --- |
| Bank formats | The real banks' statement formats are seeded from the owner's CSV samples (E-VS2-05-1); until then only the test formats exist and a real statement cannot be imported | Alexander (samples), tech lead (migration) |
| B-02 | Second reviewer for ledger PRs — VS#2 posts R-09 / R-10 and adds BANK-REC to the close | Tech lead |
| Real data | No real accounting or bank data until B-02 or a parallel run that reconciles to zero (E-VS1-2, E-VS2-10) | Alexander |
| A-01 | The Controller approves R-09, R-10, the BANK / BANK_CHARGES accounts and the TREASURY aging buckets | Alexander |
