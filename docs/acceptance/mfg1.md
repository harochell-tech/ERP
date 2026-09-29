# MFG-1 acceptance — production, curing, release and standard cost

Baseline MFG-1 (`docs/architecture/mfg1/frozen-baseline-mfg1.md`) §9, with the approved errata E-MFG1-1…18 and E-MFG1-01…06.
Every acceptance ID has at least one test tagged `[Trait("AcceptanceMfg1", "<ID>")]`; `AcceptanceMfg1TraceabilityTests` fails if
an ID has no tagged test (except the ones marked pending below) or is missing from this matrix (E-MFG1-06-4).

## Matrix

| ID | What | Tests |
| --- | --- | --- |
| MFG-01 | A run needs the ACTIVE recipe; the preparer cannot approve it | `ProductionRunTests.MFG01_a_run_needs_the_products_active_recipe_on_the_machine`; `ProductionMasterTests.A_recipe_is_prepared_by_the_supervisor_and_approved_by_the_plant_manager_replacing_the_active_one` |
| MFG-02 | Posted summary: P-08 at moving average, P-10 at standard, racks, lot in CURADO | `ProductionRunTests.A_posted_shift_summary_consumes_at_moving_average_and_receives_the_lot_into_curing_at_standard` |
| MFG-03 | Consumption above the stock is refused and nothing is posted | `ProductionRunTests.Production_needs_a_standard_with_breakdown_enough_stock_and_is_reversed_exactly_while_the_lot_is_in_curing` |
| MFG-04 | Dispatch never takes units from CURADO | `CuringTests.Calidad_releases_a_cured_lot_to_the_yard_without_a_journal_and_nothing_in_curing_is_offered_for_dispatch`; API: `ManufacturingAcceptanceTests` (confirm-loaded from CURADO → `LOCATION_INVALID`) |
| MFG-05 | Release before the minimum curing is refused; afterwards a transfer to the yard without a journal | `CuringTests.Calidad_releases_a_cured_lot_to_the_yard_without_a_journal_and_nothing_in_curing_is_offered_for_dispatch` |
| MFG-06 | Scrap of finished goods: P-12, stock and value down | `CuringTests.A_blocked_lot_is_not_released_and_scrap_is_posted_at_valuation_cost_until_the_lot_is_scrapped` |
| MFG-07 | Settlement of the baseline's example: usage and price variances, WIP 0 | `SettlementTests.MFG07_the_settlement_splits_the_WIP_into_usage_and_price_variances_and_leaves_it_at_zero` |
| MFG-08 | New standard with stock: revaluation REVAL, P-3 balances | `ProductionMasterTests.Approving_a_new_standard_with_stock_revalues_it_with_REVAL_unless_units_are_in_transit` |
| MFG-09 | Reversal of a posted summary: exact reversals, stock and WIP as before | `ProductionRunTests.Production_needs_a_standard_with_breakdown_enough_stock_and_is_reversed_exactly_while_the_lot_is_in_curing` |
| MFG-10 | Two simultaneous postings of one summary: one receipt | **Pending — MFG1-08** |
| MFG-11 | Period with production: SHIFT-OPEN, WIP-OPEN, WIP-GL; OP-DAY → COST-SET → INV-MOV | `SettlementTests.MFG11_production_reconciliations_report_open_runs_usage_out_of_tolerance_and_the_close_order`; API: `ManufacturingAcceptanceTests` (INV-MOV refused first, then the three close in order) |
| E2E-M1 | Recipe → standard → run → summary → curing → release → delivery → invoice → settlement, over the API and the UI | API: `Rochell.Api.Tests` · `ManufacturingAcceptanceTests.E2EM1_recipe_standard_run_summary_curing_release_sale_settlement_and_close_over_HTTP` (E-MFG1-06-2). UI: `web/e2e/production-journey.spec.ts` (Playwright, MFG1-07; the settlement needs the month to end, so it stays in the API test) |
| INV-M | Random sequences keep P-1, P-3, WIP-GL, released ⇔ in the yard, nothing dispatched from CURADO | **Pending — MFG1-08** |

Tests without a project name are in `Rochell.Manufacturing.Tests`.
