# FIN-1 acceptance — adjustment journal, trial balance and statements

Frozen Baseline FIN-1 (`docs/architecture/fin1/frozen-baseline-fin1.md`) §7, with the approved errata E-FIN1-1…10,
E-FIN1-01-1…7, E-FIN1-02-1…4 and E-FIN1-03-1…11. Every acceptance ID has at least one test tagged
`[Trait("AcceptanceFin1", "<ID>")]`; `AcceptanceFin1TraceabilityTests` fails if an ID of the baseline has no tagged test or is
missing from the matrix below.

## Matrix

| ID | What | Tests |
| --- | --- | --- |
| GL-01 | Balanced adjustment prepared, submitted and approved by another person: POSTED, MANUAL_ADJUSTMENT journal, reflected in the ledger | `Rochell.Finance.Tests` · `ManualJournalTests.GL01_a_balanced_adjustment_is_prepared_submitted_and_approved_by_another_person` |
| GL-02 | Adjustment to a control (or inactive) account refused — command and database | `ManualJournalTests.GL02_an_adjustment_to_a_control_or_inactive_account_is_refused`; `LedgerSchemaTests.An_adjustment_takes_only_active_non_control_accounts_and_is_sent_balanced` |
| GL-03 | The preparer cannot approve (or reject) | `ManualJournalTests.GL03_the_preparer_cannot_approve_or_reject_and_a_rejection_needs_a_reason` |
| GL-04 | Auto-reversing adjustment reversed on the 1st of the next month | `ManualJournalTests.GL04_an_auto_reversing_adjustment_reverses_on_the_first_day_of_the_next_month` |
| GL-05 | Month with purchases, payments, charges and adjustments: debits = credits; each account = Σ entries | `Rochell.Reconciliation.Tests` · `TrialBalanceTests.GL05_a_month_with_purchases_payments_charges_and_adjustments_balances_and_each_account_is_the_sum_of_its_entries` |
| GL-06 | Approved structure: assets = liabilities + equity + result; each account on one line | `StatementTests.GL06_an_approved_structure_presents_a_balance_sheet_that_balances_and_an_income_statement`; `StatementTests.A_structure_holds_each_account_once_covers_every_active_account_of_its_classes_and_supersedes_the_previous_one` |
| GL-07 | Account with movements: deactivated only without a balance; never deleted | `ManualJournalTests.GL07_an_account_with_a_balance_is_not_deactivated_and_accounts_are_created_with_their_class`; `LedgerSchemaTests.Accounts_keep_code_and_control_flag_and_are_never_deleted_and_the_ACR_components_open` |

Other FIN-1 tests: `ManualJournalTests` (reversal with a reason, withdraw and correct, closed period), `AdjustmentCloseTests`
(ACR-NTX close), `StatementTests` (trial balance opening rules, account ledger, statements without class or structure, CSV),
`LedgerReportApiTests` (CSV over HTTP).
