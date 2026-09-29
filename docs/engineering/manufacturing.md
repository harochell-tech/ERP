# Manufacturing (MFG-1)

Frozen Baseline `docs/architecture/mfg1/frozen-baseline-mfg1.md` (E-MFG1-1…18). This page records what each merged PR built.

## MFG1-01 — master data schema (migration 0048, E-MFG1-01-1…13)

| Object | Table | Rules |
| --- | --- | --- |
| Machine | `md.machine` | Per plant; code unique per company and immutable; registered ACTIVE v1; ACTIVE ⇄ INACTIVE with `core.state_history`; version + 1; never deleted |
| Shift | `mfg.shift` | Per plant; start ≠ end (a night shift crosses midnight); business date = start date (E-MFG1-15); same lifecycle as machines |
| Recipe | `mfg.recipe_version`, `mfg.recipe_line` | Finished good × machine; carries units per batch / cycle / rack and the curing window (min < max hours); DRAFT → ACTIVE → SUPERSEDED, approver ≠ preparer, one ACTIVE per product × machine; lines (raw materials, qty per batch in the material's base UoM) only on a DRAFT |
| Standard cost breakdown | `md.standard_cost_version.material_cost / conversion_cost`, `md.standard_cost_material` | Both NULL (VS#3 standards) or material + conversion = unit cost; material lines only on a DRAFT, raw materials only |
| Curing location | `md.location.is_curing` | Code CURADO, one per plant, never also a transit location |

Inventory movement types `PRODUCTION_ISSUE` (negative) and `PRODUCTION_RECEIPT` (positive). Account roles WIP (control, subledger
`WIP` — `gl_entry.subledger_ref` is the cost collector), CONVERSION_ABSORPTION, MATERIAL_PRICE_VARIANCE, PRODUCTION_SCRAP,
STANDARD_REVALUATION, seeded unmapped. Roles SUPERVISOR_PRODUCCION, GERENTE_PLANTA and CALIDAD with ten permissions and four SoD
rules (108 permissions and 40 SoD rules in total).

## MFG1-02 — master data commands, standard cost from a recipe, revaluation (migration 0049, E-MFG1-02-1…10)

Module `Rochell.Manufacturing` (may use Platform, Finance, Inventory, MasterData).

| Command | Permission | Rules |
| --- | --- | --- |
| `CreateMachine`, `RenameMachine`, `SetMachineStatus` | `production_master:manage` | Plant-scoped; code upper case, unique per company (`CODE_DUPLICATE`); `ExpectedVersion` (`VERSION_CONFLICT`); the row must be of the command's plant (`PLANT_MISMATCH`) |
| `DefineShift`, `UpdateShiftTimes`, `SetShiftStatus` | `production_master:manage` | Start ≠ end in whole minutes; an end before the start is a night shift (`crossesMidnight` in `ListShifts`) |
| `PrepareRecipe` | `recipe:prepare` (Supervisor) | New DRAFT version per preparation; ACTIVE finished good and machine of the plant; ACTIVE raw materials, once each; curing 0 ≤ min < max |
| `ApproveRecipe` | `recipe:approve` (Gerente de planta) | Four eyes; the ACTIVE version of the product × machine becomes SUPERSEDED |
| `PrepareStandardCostFromRecipe` (Sales) | `standard_cost:prepare` | ACTIVE recipe; one price per recipe material; qty/unit = qty/batch ÷ units/batch (6 dec.), material = Σ qty × price (4 dec.), unit = material + conversion |
| `ApproveStandardCost` (Sales, extended) | `standard_cost:approve` + step-up | With stock: value → round(qty × new standard, 2) by VALUATION_ADJUSTMENT + REVAL (UP: Dr FINISHED_GOODS / Cr STANDARD_REVALUATION; DOWN the reverse); refused with `IN_TRANSIT_EXISTS` while units are in TRANSITO |

Queries (`production:read`): `GET /manufacturing/machines`, `/shifts`, `/recipes`, `/recipes/{id}`.

Worked example (test `The_standard_cost_from_the_recipe_breaks_down_materials_and_conversion`): recipe 150 units per batch with
180 kg cement, 1.8 t sand, 1.5 l admixture; prices 8.00, 1,000.00, 50.00 → 1.2 × 8 + 0.012 × 1,000 + 0.01 × 50 = 22.10 + conversion
5.90 = 28.00. Revaluation test: 100.5 units at 28.00 (2,814.00) → new standard 30.1234 → 3,027.40, REVAL +213.40.

## MFG1-03 — runs, shift summaries, consumption and the lot into curing (migration 0050, E-MFG1-03-1…12)

| Command | Permission | Effect |
| --- | --- | --- |
| `StartProductionRun` | `production_run:manage` | Run `PR-000001…` IN_PROGRESS with the ACTIVE recipe and standard (with breakdown); creates the month's cost collector |
| `CancelProductionRun` | `production_run:manage` | Only without a shift summary; reason required |
| `RecordShiftSummary` | `shift_summary:record` | DRAFT (replaced while DRAFT); consumption of every recipe material, converted to the base unit; theoretical stored |
| `PostShiftSummary` | `shift_summary:post` | Four eyes; FIFO lots by code per location; PRODUCTION_ISSUE + P-08; lot `PT-…` into CURADO (PRODUCTION_RECEIPT at standard) + P-10; `mfg.fg_lot` CURING; racks |
| `ReverseShiftSummary` | `shift_summary:post` + step-up | Exact reversals while the lot is CURING and unmoved; new DRAFT for the run |

P-08: Dr WIP [plant, product; subledger WIP = collector] / Cr RAW_MATERIAL [plant, material; INV = value entry], one pair per lot
issued. P-10: Dr FINISHED_GOODS at round(units × standard, 2) / Cr WIP round(units × material standard, 2) / Cr CONVERSION_ABSORPTION
the difference. The WIP balance of a collector (consumption − material standard) is settled in MFG1-05.

Worked example (`ProductionRunTests`): 10 batches, 1,480 good units; cement 1,850 kg (15,170.00), sand 12.5 m³ × 1.47 = 18.375 t
FIFO 10 t + 8.375 t (9,990.91 + 8,367.39), admixture 15 l (750.00) → P-08 34,278.30; P-10 41,440.00 = 32,708.00 + 8,732.00; WIP
1,570.30; racks 600 + 600 + 280; curing from 19:00 of the business date, releasable 24 hours later.

Queries (`production:read`): `GET /manufacturing/runs`, `/runs/{id}`.

## MFG1-04 — curing release, block, scrap and CURADO out of dispatch (migration 0051, E-MFG1-04-1…7)

| Command | Permission | Effect |
| --- | --- | --- |
| `ReleaseLot` | `fg_lot:release` (Calidad) | CURING lot after its minimum curing → RELEASED; whole quantity CURADO → the chosen stock location (TRANSFER, quantity only, no journal); racks RELEASED |
| `BlockLot` / `UnblockLot` | `fg_lot:release` | CURING ⇄ BLOCKED with a reason; a blocked lot is not released |
| `ScrapLot` | `fg_lot:scrap` (Gerente de planta) + step-up | Quantity from a location of the lot at the area's valuation cost: ISSUE + P-12 (Dr PRODUCTION_SCRAP / Cr FINISHED_GOODS); point CURING / YARD; no stock left → SCRAPPED |

Lot lifecycle (database guard): CURING → RELEASED | BLOCKED | SCRAPPED | VOIDED (summary reversed); BLOCKED → CURING | SCRAPPED;
RELEASED → SCRAPPED. Dispatch and `/sales/plants` never offer CURADO. Query `GET /manufacturing/lots`.

Example (`CuringTests`): 1,480 units for 41,440.00; scrap 100 in curing → 2,800.00; release; scrap the remaining 1,380 in the yard →
38,640.00 (the last units take the remaining value); lot SCRAPPED, FINISHED_GOODS 0.00.

## MFG1-05 — collector settlement, production reconciliations and close (migration 0052, E-MFG1-05-1…8)

`SettleCostCollector` (`cost_collector:settle` + step-up) after the collector's month, with no run IN_PROGRESS: usage variance per
summary and material at standard price, price variance = WIP − usage; P-13 on the month's last day (component COST-SET); the collector
is SETTLED for good and its runs can no longer be reversed.

| Reconciliation | Checks | Blocks |
| --- | --- | --- |
| WIP-GL | GL WIP per collector = consumption − material standard − settled variances | COST-SET |
| WIP-OPEN | Collectors of months ended by the cutoff still OPEN | COST-SET |
| SHIFT-OPEN | Runs up to the cutoff still IN_PROGRESS | OP-DAY, COST-SET |
| USAGE-TOLERANCE | Real vs theoretical beyond PRODUCTION.usage_tolerance_pct | — (warning) |
| CURING-OVERDUE | Lots CURING beyond the recipe's maximum hours | — (warning) |
| PRODUCTION-CLOSE-ORDER | In a period with production: OP-DAY closed before COST-SET, COST-SET before INV-MOV | COST-SET, INV-MOV |

Close snapshots: OP-DAY records the runs by status; COST-SET the collectors and the WIP balance by collector. `rochell-migrate
open-periods` opens OP-DAY and COST-SET with the other components. Query `GET /manufacturing/cost-collectors`.

## MFG1-06 — production day, E2E-M1 over the API, acceptance matrix (E-MFG1-06-1…5)

`GET /manufacturing/production-day?plantId=&businessDate=` (`production:read`): the day's runs with their summary and lot, and the
consumption per material (real, theoretical, difference). E2E-M1 runs over HTTP in `Rochell.Api.Tests`
(`ManufacturingAcceptanceTests`); the acceptance matrix is `docs/acceptance/mfg1.md` (MFG-10 and INV-M pending for MFG1-08, the UI
half of E2E-M1 for MFG1-07).

## MFG1-07 — production screens and the Playwright journey (migration 0053, E-MFG1-07-1…11)

Menu **Producción** (`web/src/app/produccion/`): *Producción del día* (`dia`, start runs), the run page (`corrida`: record,
post, reverse the shift summary, cancel the run), *Curado y liberación* (`lotes`: release, block, unblock, scrap), *Recetas*
(`recetas`, `receta`: prepare, approve), *Máquinas y turnos* (`maquinas`), *Costos de producción* (`costos`: settle). *Maestros ›
Costos estándar* prepares a standard from a recipe. Shared code: `web/src/components/Production.tsx`, `web/src/lib/production.ts`.
Migration 0053 gives `master_data:read` to the three production roles and `sales:read` to the Aprobador de políticas.

Dev data: `tests/Rochell.DevStack/MfgSeed.cs` (ADOQUIN-H without recipe, materials with stock, production rules approved, one user
per production role). Journey: `web/e2e/production-journey.spec.ts` (machine, shift, recipe of 0 minimum curing hours, standard from
the recipe 22.34 + 5.90 = 28.24, run of yesterday, summary, posting, release). Staging: `deploy/staging/seed-mfg.sh` (`staging.md`).

## MFG1-08 — properties, concurrency and the acceptance matrix (E-MFG1-08-1…8)

`ProductionPropertyTests` (INV-M) and `ProductionConcurrencyTests` (MFG-10 and the other races) in `Rochell.Manufacturing.Tests`;
workflow `inv-m` for long runs; the acceptance matrix `docs/acceptance/mfg1.md` is complete. MFG-1 is code-complete.
