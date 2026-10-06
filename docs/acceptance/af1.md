# AF-1 acceptance — fixed assets: register and depreciation

Baseline AF-1 (`docs/architecture/af1/frozen-baseline-af1.md`) §4, with the approved errata E-AF-1…12, E-AF1-01-1…9, E-AF1-02-1…10,
E-AF1-03-1…11, E-AF1-04-1…9 and E-AF1-05-1…10. Every acceptance ID has at least one test tagged `[Trait("AcceptanceAf1", "<ID>")]`;
`AcceptanceAf1TraceabilityTests` fails if an ID has no tagged test or is missing from this matrix (E-AF1-05-10).

## Matrix

| ID | What | Tests |
| --- | --- | --- |
| AF-01 | A class prepared by the Contador, approved by the Controller; the preparer cannot approve; a class without life or accounts is refused | `FixedAssetCardTests.AF01_a_class_is_prepared_by_the_Contador_approved_by_the_Controller_and_a_new_version_replaces_it` |
| AF-02 | A posted invoice with a fixed-asset line gives a card awaiting service at the line's cost; an expense line gives none | `FixedAssetCardTests.AF02_the_forklift_is_born_with_its_invoice_takes_the_settlements_cost_and_is_put_into_service_with_its_class` |
| AF-03 | The settlement adds 84,000.00 (564,000.00); its reversal takes it back (480,000.00) | `FixedAssetCardTests.AF02_…` |
| AF-04 | Put into service mid-month: the first depreciation is the next month | `FixedAssetCardTests.AF02_…` (class required, date); `FixedAssetDepreciationTests.AF05_…` (the service month is not depreciated) |
| AF-05 | 8,460.00 a month to depreciation (plant) against accumulated; once a month; undone | `FixedAssetDepreciationTests.AF05_the_forklift_depreciates_8460_a_month_once_in_order_and_the_latest_month_can_be_undone` |
| AF-06 | A cost added while depreciating is spread over the months left | `FixedAssetDepreciationTests.AF06_a_cost_added_while_depreciating_is_spread_over_the_months_left_and_the_last_month_closes_exact` |
| AF-07 | The last month closes exactly at cost − residual | `FixedAssetDepreciationTests.AF06_…` |
| AF-08 | After 12 months (101,520.00), sold for 450,000.00: book value 462,480.00, loss 12,480.00; no more depreciation | `FixedAssetDepreciationTests.AF08_after_12_months_the_forklift_is_sold_for_450000_with_a_loss_of_12480_approved_by_the_Controller`; scrap: `FixedAssetDepreciationTests.A_card_is_scrapped_in_its_first_month_at_its_cost_and_a_draft_disposal_can_be_cancelled` |
| AF-09 | An existing truck (2,400,000.00 / 960,000.00, 96 months, bought 36 months before) loaded: 20,000.00 a month for 60 months, against the opening balances | `FixedAssetLoadTests.AF09_an_existing_truck_is_loaded_against_the_opening_balances_and_depreciates_20000_for_its_60_months_left` |
| AF-10 | A transfer posts nothing; the next depreciation goes to the new plant | `FixedAssetCardTests.AF10_a_card_moves_to_another_plant_without_a_journal_and_its_invoice_reversal_cancels_it`; `FixedAssetDepreciationTests.AF10_after_a_transfer_the_next_depreciation_goes_to_the_new_plant` |
| AF-11 | FA-GL squares cost and accumulated with the ledger; a month without depreciation warns; FA-REC blocked by differences | `FixedAssetLoadTests.AF11_FA_GL_squares_the_cards_with_the_ledger_and_flags_a_month_left_without_depreciation` |
| E2E-AF1 | Class → invoice with an asset → settlement → service → depreciation → sale → reconciliation, over the API and the screens | `FixedAssetAcceptanceTests.E2EAF1_class_invoice_settlement_service_depreciation_sale_and_reconciliation_over_HTTP`; the screens: Playwright `fixed-assets-journey.spec.ts` |

## Open

- **X-1** (accountant): tax depreciation (DGII art. 287); whether the sale of an asset carries ITBIS.
- **A-01** (Controller): the classes (lives, residuals, accounts), the accounts and maps of ASSET_SALE_RECEIVABLE, ASSET_DISPOSAL_GAIN and
  ASSET_DISPOSAL_LOSS, rules P-44 / P-45 / P-46.
- **Owner**: the list of existing assets with their cost and accumulated depreciation at the cut-off.
- **B-02**: the posting engine taking the FIXED_ASSET_* accounts from the command (E-AF1-01-7) and P-44 / P-45 / P-46.
