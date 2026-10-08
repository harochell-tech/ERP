# LAB-1 acceptance — quality lab and lot traceability

Baseline LAB-1 (`docs/architecture/lab1/frozen-baseline-lab1.md`) §10, with the approved errata E-LAB1-1…10 and E-LAB1-01-1…17. The «PR»
column names the PR that delivers each one (baseline §12). Every acceptance ID gets at least one test tagged
`[Trait("AcceptanceLab1", "<ID>")]`; `AcceptanceLab1TraceabilityTests` fails if an ID has neither a tagged test nor a place in its
pending list (the IDs of LAB1-02…04), or is missing from this matrix.

Expected values come from the validated Excel through `docs/architecture/lab1/reference/historico_ensayos.csv`.

## Matrix

| ID | What | PR | Tests |
| --- | --- | --- | --- |
| LAB-01 | Five specimens at 3 days (lot `8160924P1`): age, gross area, kg/cm² and MPa per specimen; lot average 77.1596, minimum 57.1243, maximum 98.3660, deviation 14.8241, CV 19.21 %, factor 0.84, estimated 28-day strength 91.8567, estimated minimum 68.0051 | LAB1-01 (records), LAB1-02 (evaluation) | `QualityLabTests` `Five_specimens_at_three_days_…` (specimens: age, area, kg/cm², MPa); the lot's evaluation pending (LAB1-02) |
| LAB-02 | A specimen without measures uses the item's nominal ones | LAB1-01 | `QualityLabTests` `Five_specimens_at_three_days_…` (fifth specimen, 19.5 × 39.5 = 770.25) |
| LAB-03 | Specimens at 28 days (lot `8121124P1`): real 28-day strength 80.3975, not marked as estimated | LAB1-02 | pending |
| LAB-04 | An item without a requirement gives «Sin requisito»: the lot is neither blocked nor released (E-LAB1-10) | LAB1-02 | pending |
| LAB-05 | A real NO CUMPLE on a RELEASED lot with stock and a delivery: lot BLOCKED, the gate-out no longer takes it, recall lists the delivery and the remaining stock (E-LAB1-4) | LAB1-02 | pending |
| LAB-06 | An estimated NO CUMPLE blocks preventively; `FinalReleaseLot` is refused (E-LAB1-5) | LAB1-02 | pending |
| LAB-07 | `FinalReleaseLot` is refused on an estimated CUMPLE and gives FINAL_RELEASED on a real one (E-LAB1-4, 5) | LAB1-02 | pending |
| LAB-08 | A lot blocked by an estimate, then a real CUMPLE at 28 days and `UnblockLot` with a reason: back to its previous status | LAB1-02 | pending |
| LAB-09 | Two specimens with CV above 15 %: alerts «menos de 3 probetas» and «CV alto» | LAB1-02 | pending |
| LAB-10 | Three absorption blocks: absorption, density, class and limit; alert when the lot average exceeds the limit | LAB1-01 (records), LAB1-02 (alert) | `QualityLabTests` `Three_absorption_blocks_give_absorption_density_class_and_limit`; the alert pending (LAB1-02) |
| LAB-11 | Two lots broken at 3 and at 28 days: the 3-day factor is their own; with one lot, the initial factor | LAB1-02 | pending |
| LAB-12 | Runs of P1 and P2 with the same product, shift and day get different field codes; T2 carries its suffix (E-LAB1-1, 3) | LAB1-01 | `QualityLabTests` `Runs_of_two_machines_get_different_field_codes_…` |
| LAB-13 | Gate-out with a scanned rack label takes that lot; without a scan, FIFO (E-LAB1-7) | LAB1-03 | pending |
| LAB-14 | Recall forward (deliveries, customers, sites) and backward (run, shift, machine, consumption, tests) | LAB1-02 | pending |
| LAB-15 | Certificate `CR-<field code>-<DDMMYY>` with the baseline's content and a public QR that verifies it | LAB1-03 | pending |
| LAB-16 | Import of the history: 42 lots and 216 specimens, read-only, computed values equal to the CSV's; a second import duplicates nothing (E-LAB1-6) | LAB1-04 | pending |
| LAB-17 | A technician without `fg_lot:final_release` cannot release (E-LAB1-9) | LAB1-01 (permissions), LAB1-02 (command) | `QualityLabTests` `The_lab_technician_records_tests_and_reads_but_holds_neither_…` (role and SoD); the command pending (LAB1-02) |
| E2E-L1 | Whole flow over the API and the UI: run → lot → preliminary release → delivery → early test → 28 days → final release → certificate | LAB1-04 | pending |
