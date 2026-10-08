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

## MFG2-01 — portal schema (migration 0099, E-MFG2-01-1…8)

Baseline `docs/architecture/mfg2/frozen-baseline-mfg2.md` (E-MFG2-1…14); the portal's side lives in `harochell-tech/portal`
(`data/exportar.php`, `data/consumo.php`, maintenance windows).

| Table | What it holds |
| --- | --- |
| `mfg.portal_machine` | Portal machine code (`planta2`) → Core machine and its batch plant (`dosificadora2`; NULL = offline, consumption typed, E-MFG2-11) |
| `mfg.portal_mould` | Portal mould (`4`, `6`, `8`) → finished good |
| `mfg.portal_shift` | Portal shift number → Core shift (E-MFG2-13, E-MFG2-01-4) |
| `mfg.portal_material` | Batch plant + material code (`CEMENTO`) → item, unit and the location it is issued from (E-MFG2-01-3) |
| `mfg.portal_reading` | A machine's shift as read (window, closed, cycles, without mould, maintenance cycles, dead minutes, first / last cycle, moulds as JSON, SHA-256); append-only, a row only when it changed (E-MFG2-01-6) |
| `mfg.portal_consumption` | A batch plant's post for a shift (the portal's id, batches, materials as JSON); append-only, the highest portal id of a shift counts (E-MFG2-01-5) |

`mfg.shift_summary` gains `source` (MANUAL / PORTAL, written once, PORTAL names its `portal_reading_id`), `edited_by` (a person
changed the draft: the portal no longer replaces it, E-MFG2-01-2), `consumption_source` (MANUAL / BATCH_PLANT with its
`portal_consumption_id` / PENDING — the recipe's theoretical as a placeholder, never posted: a CHECK, E-MFG2-8, E-MFG2-01-8) and
`consumption_reason` (10–300 characters, a typed consumption on an online batch plant). Permission `portal:manage` (plant manager,
E-MFG2-01-7); PROCESO_DIARIO gains `shift_summary:record` and `production_run:manage` (E-MFG2-6) — 144 permissions.

## MFG2-02 — reading the portal, drafts from it (migration 0100, E-MFG2-1…14, E-MFG2-01-1…8)

- `PortalService` (API, every `Rochell:Portal:Interval`, 15 min) runs `PortalRunner.RunOnceAsync` per company with pairings, as the
  daily process: `ImportPortalData` (yesterday and today) → `StartProductionRun` for the runs a group needs → `SyncPortalShift` per
  group (a batch plant's machines, or an offline machine alone, per date and portal shift). Nothing is posted.
- `ImportPortalData` (`IPortalSource`, `PortalHttpSource` = GET `data/exportar.php` with `X-Core-Token`) keeps a reading only when a
  machine's shift changed (SHA-256) and each batch-plant post once (its portal id); unpaired machines / batch plants are warnings; a
  failed read is recorded in `mfg.portal_sync_state` (last good read, last failure, warnings).
- `SyncPortalShift`: per machine and mould with blocks, the run's DRAFT summary (`source` PORTAL): units = blocks; batches = the post's
  batches split by units, else ⌈units ÷ units per batch⌉; consumption = the latest post of the batch plant once every machine of the
  group ended its shift, split by each run's theoretical consumption (the post's unit must be the pairing's; converted to the base
  unit) — `BATCH_PLANT`; otherwise the theoretical as a `PENDING` placeholder. Theoretical = per batch ÷ units per batch × units
  (E-MFG2-7). A posted summary, a MANUAL one or a draft a person changed (`edited_by`) is left alone; an unchanged draft is not
  rewritten. Locations come from `mfg.portal_material` (the group's batch plant first).
- `RecordShiftSummary` (+ `ConsumptionReason`): on a PORTAL draft a person's save sets `edited_by`; the same consumption keeps its
  source, another one becomes MANUAL and needs a reason of 10–300 characters when the machine's batch plant is online
  (`REASON_REQUIRED`). `PostShiftSummary` refuses `PENDING` (`CONSUMPTION_PENDING`) and treats `edited_by` like the recorder for
  four eyes. The writing is shared: `ShiftSummaryWriter`.
- Staging: `PORTAL_URL` (GitHub Environment) and `secrets/portal/core-token` on the server (`staging.md`). 266 commands.

## MFG2-03 — Producción › Portal, the run's portal block, Inicio (E-MFG2-3/7/8, E-MFG2-01-7)

- Commands (`portal:manage`, Gerente de planta): `SetPortalMachine` (portal code, machine, batch plant or none), `SetPortalMould`
  (finished good), `SetPortalShift` (1–9 → shift), `SetPortalMaterial` (batch plant, code in upper case, raw material, a unit it
  converts from, a stock location), `RemovePortalPairing` (`MACHINE` / `MOULD` / `SHIFT` / `MATERIAL` `bp/CODE`); every change is a
  `PortalPairingChanged` event of the company's `PortalPairing` aggregate. 271 commands.
- `GetPortalSetup` (`production:read`): the pairings, the last good read and failure, the warnings, machines the portal sends that are
  not paired, PORTAL drafts with PENDING consumption and with batch-plant consumption beyond `usage_tolerance_pct`.
- `GetProductionRun` carries `portal` (`RunPortalView`): paired, portal code, batch plant, source, consumption source and reason, who
  changed the draft, the portal's blocks of the run's product, cycles, maintenance cycles, dead minutes, shift ended, last cycle (local).
- Web: Producción › Portal de máquinas (`/produccion/portal/`); the run page shows «Del portal de máquinas» with the consumption's state
  and asks a reason when the consumption of an online batch plant is changed; Inicio counts drafts without the batch plant's
  consumption (Supervisor), consumption beyond tolerance (Gerente), and the portal's warnings (`portal:manage`). Journey:
  `e2e/portal-journey.spec.ts`.

## MFG3-01 — efficiency and preventive maintenance: schema (E-MFG3-1…11, E-MFG3-00-1…4, E-MFG3-01-1…5)

Migration 0104:

- `mfg.portal_stoppage`, `mfg.portal_maintenance`, `mfg.portal_daily_report`: each distinct version (unique by the content's SHA-256)
  of a portal stoppage (start, end, seconds, reason, detail), maintenance window (start, end, reason, the task it names) and machine's
  daily report (broken blocks, good blocks confirmed after curing per size), append-only; portal times are Dominican local `timestamp`.
- `mfg.ideal_cycle`: seconds per machine and product from a date on (append-only; a later date replaces it), set with
  `production_master:manage`.
- `mfg.maintenance_task`: per machine, a code, name, every N CYCLES / RUNNING_HOURS / DAYS, instructions, ACTIVE / INACTIVE.
  `mfg.maintenance_done`: append-only, from the PORTAL (its maintenance id) or from CORE (who recorded it), never both.
- Permission `maintenance_plan:manage` for GERENTE_PLANTA (149 permissions).

## MFG3-02 — the portal's stoppages, maintenance and reports; ideal cycle; efficiency (E-MFG3-2…7, E-MFG3-01-1/2/4)

- `ImportPortalData` also keeps each new version of a paired machine's stoppages (`paros`), maintenance windows (`mantenimientos`)
  and daily reports (`reportes`) — an older portal without them still imports. A new report version brings its machine's day (shift 1)
  up to date.
- `SyncPortalShift`: the latest daily report's broken blocks of a machine with a single shift that day become fresh scrap, split
  among its products by units (good = blocks − broken); two shifts that day, or more broken than blocks, leave a warning instead.
  A draft a person changed is kept, as before.
- `SetIdealCycle` (`production_master:manage`): seconds per machine and product from a date (one per date; a later date replaces it).
- `GetMachineEfficiency` (`production:read`, `GET …/manufacturing/efficiency?from&to`, at most 93 days): per paired machine and portal
  shift, from the latest reading and the latest version of each stoppage and maintenance window — planned minutes = window (up to
  now) − maintenance; stoppages starting inside maintenance do not count; running = planned − stoppages; availability = running ÷
  planned; performance = Σ cycles × ideal seconds ÷ running seconds (null when a mould's product has no ideal cycle, listed in
  `missingIdealCycles`); quality = good ÷ (good + scrap) of the shift's summaries; OEE = their product (ratios to 4 decimals); lost
  blocks = stoppage seconds ÷ the main mould's ideal cycle × its blocks per cycle, valued at the ACTIVE standard cost; stoppages by
  reason (`SIN_RAZON` without one). Totals per machine over the period. 280 commands.

## MFG3-03 — preventive maintenance plans (E-MFG3-8…10, E-MFG3-01-3/5)

- `DefineMaintenanceTask` (code in capitals, unique — what the mechanic chooses in the portal), `UpdateMaintenanceTask`,
  `SetMaintenanceTaskStatus` (ACTIVE / INACTIVE, never deleted), `RecordMaintenanceDone` (CORE, by the plant manager): all
  `maintenance_plan:manage`. 284 commands.
- `ImportPortalData`: a closed portal maintenance window whose `tarea` is the code of an ACTIVE task of its machine records that task
  done (PORTAL, once per window).
- `ListMaintenanceTasks` (`production:read`, `GET …/manufacturing/maintenance-tasks`): each task with what has gone since it was last
  done (or created) — cycles of the machine's shifts, running hours (planned − maintenance − stoppages up to now) or days —, the share
  of its interval (`used`), the state OK / POR_VENCER (≥ 90 %) / VENCIDA (≥ 100 %) / INACTIVE, the last five done (local time), and
  the counts `dueSoon` / `overdue` for Inicio.
- Publishing the task list to the portal and its push notices come with the portal's MFG3-00b.

## MFG3-04 — efficiency and maintenance screens (E-MFG3-4…10)

- `ListIdealCycles` (`production:read`, `GET …/manufacturing/ideal-cycles`): every ideal cycle and which is in force today.
- Web: Producción › Eficiencia (`/produccion/eficiencia/`: per machine and per shift — availability, performance, quality, overall
  efficiency as percentages moved from the server's ratios, stoppage minutes by reason, lost blocks and their cost; notices for
  missing ideal cycles and stoppages without a reason); Máquinas y turnos gains «Ciclo ideal»; Producción › Mantenimiento preventivo
  (`/produccion/mantenimiento/`: tasks with their state, define, mark done, deactivate). Inicio: overdue and due-soon maintenance
  (`maintenance_plan:manage`) and the week's stoppages without a reason. Journey: `e2e/maintenance-journey.spec.ts`.

## MFG3-05 — Core's tasks in the portal (E-MFG3-9/10; portal MFG3-00b)

- After every read, `PortalRunner` runs `PublishMaintenanceTasks` (`shift_summary:record`, the daily process): the ACTIVE tasks of
  paired machines — code, portal machine, name, state, what has gone, every, unit — go to the portal's `data/planes.php`
  (`IPortalSource.PublishTasksAsync`, header X-Core-Token); a failure is a warning of the pass. 285 commands.
- Portal (harochell-tech/portal, MFG3-00b): `inc/tareas.php` creates `tareas_mantenimiento` and `mantenimientos.tarea` on first use;
  `data/planes.php` replaces the list and sends a push to users with the Mantenimiento (or admin) role when a task becomes «Por
  vencer» or «Vencida»; Mantenimientos lists the tasks and asks which one was done when a window ends; the export carries `tarea`,
  which Core reads back as the task done.
