# Quality lab (LAB-1)

Baseline: `docs/architecture/lab1/frozen-baseline-lab1.md` (E-LAB1-1…10). Acceptance matrix: `docs/acceptance/lab1.md`.
The lab's code lives in `Rochell.Manufacturing/Quality` with its own schema `qa` (E-LAB1-01-1); the module graph does not change.
LAB-1 posts nothing to the ledger.

## LAB1-01 — schema, field code, short code, requirements, tests (E-LAB1-01-1…17; migration 0105)

### The lot's field code and the machine's short code

- `md.machine.short_code` (E-LAB1-3): 1–6 capital letters or digits (P1, P2, P3), unique per company. `SetMachineShortCode`
  (`lab_spec:manage`, plant-scoped). `ListMachines` returns it.
- `mfg.fg_lot.field_code` (E-LAB1-1): `<item's lot prefix><DDMMYY of the run's business date><machine short code>`, with `-<shift>`
  for every shift whose code is not `T1` (`8070325P1`, `8070325P1-T2`). The internal `PT-…` code stays. Unique among live lots
  (`fg_lot_field_code_live`, `status <> 'VOIDED'`): the lot of a run redone after a reversal reuses the VOIDED lot's code
  (E-LAB1-01-5). Set once: `mfg.fg_lot_guard` refuses a change.
- `PostShiftSummary` never fails for a missing prefix or short code (E-LAB1-01-4): the lot is born without field code
  (`fieldCode: null` in the result). `SetItemSpec` and `SetMachineShortCode` then give their code to every live lot that was waiting
  (`lotsCoded` in their results). `ListLabLots` says how many lots wait and, per lot, for what (`ITEM_PREFIX`,
  `MACHINE_SHORT_CODE`, `DUPLICATE` — another live lot already has the code it would get).

### Requirements, parameters, failure types

- `qa.item_spec` (E-LAB1-01-2), append-only versions per finished good: lot prefix, nominal width / height / length (cm), net-area
  fraction, minimum 28-day average and individual strength (kg/cm², both optional: without them the verdict will be «Sin requisito»,
  E-LAB1-10). `SetItemSpec` (`lab_spec:manage`, step-up) saves in one step; the highest version counts; a test keeps the version it
  was recorded with.
- `qa.parameter_default` holds the validated Excel's values, shared by every company — maximum CV 0.15, minimum specimens 3, the age
  that counts as 28 days 26, lots for an own age factor 2, kg/cm² → MPa 0.0980665, density classes from 1,680 / 2,000 kg/m³, absorption
  limits 288 / 240 / 208 kg/m³, the press (TEST MARK, CM-2500-iD, 220808) and the 28 initial age factors (`AGE_FACTOR_01…28`) — with
  each number's range. `qa.parameter_value` is the company's own value, appended, never changed (who, when): the latest one counts
  (`qa.parameter_number` / `qa.parameter_text`). `SetLabParameter` (`lab_spec:manage`). No decimal literal lives in `src/`.
- Failure types (E-LAB1-01-11): the Excel's seven are shared (`qa.failure_type_default`); `DefineFailureType` adds one or renames any,
  `SetFailureTypeStatus` deactivates or reactivates (`qa.failure_type`, the company's own rows). Block condition is the fixed list
  `SECO_AL_AIRE`, `HUMEDO`, `SATURADO`. Both are optional on a test.

### Tests

- `RecordCompressionTests` (`lab_test:record`, plant-scoped): 1–30 specimens of one lot broken on one date. The lot may be in any
  status but VOIDED (E-LAB1-01-7); the break date is not before the lot's production date (its run's business date) nor after today
  (E-LAB1-01-6, E-LAB1-01-17; the same day is age 0, flagged `ageZero`). Per specimen (baseline §4.1): gross area = width × length — a missing
  measure is the item's nominal one, and without requirements the specimen is refused (`LAB_SPEC_MISSING`); gross strength = load ÷
  area. `qa.compression_test` keeps the measures as entered, `nominal_used`, the spec version, and the area and strength computed
  then, with 6 decimals (E-LAB1-01-8). MPa (× the conversion parameter) and the net-area strength (÷ the item's net-area fraction)
  are computed when shown. The technician is the session's user (E-LAB1-01-10).
- `RecordAbsorptionTests` (`lab_test:record`): 1–30 blocks with Ws, Wi, Wd (kg), refused unless Ws > Wi, Ws ≥ Wd and Wd > 0
  (E-LAB1-01-12). `qa.absorption_test` computes absorption (kg/m³ and fraction of the dry weight) and density as generated columns
  (baseline §4.4). The class (LIVIANO / MEDIANO / NORMAL) and its absorption limit come from the parameters when shown; `guide`
  OK / ALTA per block, and the lot's averages with the class and limit of the average density.
- `VoidCompressionTest` / `VoidAbsorptionTest` (`lab_test:record`, step-up, reason): RECORDED → VOIDED, once (E-LAB1-01-9). Nothing
  else of a test ever changes (`qa.test_guard`); a voided test stays visible and does not count. Every test has its
  `core.state_history` rows (ADR-027).

### Queries (`lab:read`)

| Query | Route | Gives |
| --- | --- | --- |
| `ListLabLots` | `GET …/manufacturing/lab/lots?search=` | Live lots, newest first, by field code or internal code; valid tests per lot; how many lots wait for a field code |
| `GetLabLot` | `GET …/manufacturing/lab/lots/{lotId}` | The lot, its item's requirements, its compression and absorption tests (voided ones too), the absorption summary |
| `ListItemSpecs` | `GET …/manufacturing/lab/item-specs` | Every finished good with its requirements in force |
| `GetLabSettings` | `GET …/manufacturing/lab/settings` | Parameters (value, starting value, range, who changed it), the last 100 changes, failure types, block conditions |
| `PreviewCompressionTests` | `POST …/manufacturing/lab/compression-tests/preview` | What the typed specimens would give — the same computation the record uses (E-LAB1-01-14) |

### Permissions (E-LAB1-9, E-LAB1-01-13; 153 permissions, 50 SoD rules)

`lab_test:record` (LABORATORIO, CALIDAD), `fg_lot:final_release` and `lab_spec:manage` (CALIDAD), `lab:read` (LABORATORIO, CALIDAD,
GERENTE_PLANTA, SUPERVISOR_PRODUCCION, DIRECTOR, AUDITOR). New role LABORATORIO. SoD: `fg_lot:final_release` ≠
`shift_summary:record`. `FinalReleaseLot` itself arrives with LAB1-02. 294 commands.

### Web (E-LAB1-01-14)

Menu group «Calidad»: Laboratorio (`/calidad/laboratorio/`, made for a phone next to the press: the lot by field code, the specimens
of one date with the server's strength before saving, void with a reason, absorption blocks), Requisitos por ítem
(`/calidad/requisitos/`, also the machines' short codes) and Parámetros (`/calidad/parametros/`, parameters, initial age factors,
failure types, history). They need a connection. Journey: `e2e/lab-journey.spec.ts`.

## LAB1-02 — evaluation, final release, automatic block, recall (E-LAB1-02-1…15; migration 0114)

### The lot's evaluation (baseline §4.2)

- `qa.lot_evaluation`: one row per evaluation, never changed; the latest of a lot is in force (E-LAB1-02-1). A lot is evaluated in
  the same transaction every time one of its tests is recorded or voided (`cause` TESTS), and when Calidad asks with `ReevaluateLot`
  (`lab_spec:manage`, `cause` REEVALUATION): new requirements or parameters never judge past lots by themselves (E-LAB1-02-2).
- Over the lot's valid specimens: number, minimum and maximum age, average, minimum, maximum, sample deviation (`stddev_samp` in
  PostgreSQL) and CV = deviation ÷ average. Early age = the minimum age of 1 day or more below the age that counts as 28 days
  (E-LAB1-02-3/4: specimens of age 0 count for the indicators and never for an estimate).
- 28-day strength: **REAL** = average (and minimum) of the specimens at the 28-day age when there are any; otherwise **ESTIMATED** =
  average (and minimum) at the early age ÷ the factor of that age.
- Verdict: `NO_SPEC` (the item has no minimum average), `NO_DATA` (a requirement but no 28-day strength, E-LAB1-02-5), `COMPLIES`
  (strength ≥ minimum average and, when there is one, minimum ≥ individual minimum) or `FAILS`.
- Alerts: `NO_TESTS`, `FEW_SPECIMENS` (below `MIN_SPECIMENS`), `HIGH_CV` (above `MAX_CV`), `HIGH_ABSORPTION` (the lot's average
  absorption above the limit of its density class).
- Age curve (E-LAB1-02-13): `qa.own_age_factors(company)` gives, per early age, the average of the factors (early average ÷ 28-day
  average) of the lots that have both; `qa.age_factor(company, age)` returns the own factor when at least `OWN_FACTOR_MIN_LOTS` lots
  have it, else the initial one (`AGE_FACTOR_nn`). One curve for every product and machine. Evaluations keep the factor they used.

### Lot statuses (baseline §7)

```
CURING ──ReleaseLot──▶ RELEASED ──FinalReleaseLot──▶ FINAL_RELEASED
any of the three ──BlockLot / a NO CUMPLE──▶ BLOCKED ──UnblockLot──▶ the status it came from
```

- `mfg.fg_lot` keeps `block_cause` (MANUAL or LAB), `blocked_from`, `block_evaluation_id`, `final_released_by` / `_at`.
- **Automatic block** (E-LAB1-4/5, E-LAB1-02-6/7): an evaluation that gives `FAILS` — real or estimated — blocks a CURING, RELEASED or
  FINAL_RELEASED lot inside the command that recorded the test, with cause LAB, the evaluation and a reason in words; its racks
  follow. A lot with no stock left is blocked too; a SCRAPPED one keeps its status.
- It blocks once per verdict (E-LAB1-02-9): again only when the verdict becomes `FAILS` from another one, or goes from estimated to
  real. Nothing unblocks by itself (E-LAB1-02-8) — not a voided test, not a later real CUMPLE: Calidad uses `UnblockLot` with a reason.
- `BlockLot` / `UnblockLot` (`fg_lot:release`) now work on released lots; unblocking returns the lot to `blocked_from`.
- `FinalReleaseLot` (`fg_lot:final_release`, step-up, E-LAB1-02-10): only from RELEASED with the evaluation in force `COMPLIES` on
  REAL data; otherwise `LAB_FINAL_RELEASE_REFUSED`. Alerts do not prevent it and stay in the event. 304 commands.

### Dispatch (E-LAB1-02-11/12)

The gate-out's FIFO (`Deliveries.FifoAsync`, Sales) skips lots whose `mfg.fg_lot.status` is BLOCKED — read through SQL, no module
reference. Lots without a production record are unaffected. When the location covers the quantity only with blocked lots the error is
`STOCK_BLOCKED_BY_QUALITY`. Sales has no availability query of its own today (orders do not reserve stock), so nothing else changes.

### Recall (baseline §4.7, E-LAB1-02-14; `lab:read`)

- `GetLotRecall` (`GET …/manufacturing/lab/lots/{lotId}/recall`): the deliveries that took the lot (delivery, gate-out, status,
  customer, order, site address, quantity, invoices not voided), the stock left per location, totals and the number of customers.
- `GetDeliveryRecall` (`GET …/manufacturing/lab/deliveries/{deliveryId}/recall`): the delivery's lots with machine, shift, run,
  recipe version, the shift summary's consumption (real and theoretical) and the lot's verdict; lots without a production record by
  their code.
- `ListLabLots` gains `view` (`BLOCKED_BY_LAB`, `READY_FINAL`), the verdict per lot and the two counts Inicio shows; `GetLabLot`
  returns the evaluation in force.

### Web (E-LAB1-02-15)

Calidad › Lotes y veredicto (`/calidad/lotes/`): the list with verdict, 28-day strength and alerts; the lot with its evaluation, the
recall and Calidad's actions (final release, block, unblock, evaluate again). Laboratorio shows the verdict after saving. Inicio:
«Lotes bloqueados por laboratorio» (`fg_lot:release`) and «Lotes listos para liberación final» (`fg_lot:final_release`).

## LAB1-03a — certificate, rack label, scan at loading (E-LAB1-03-1…16; migration 0115)

### The certificate (baseline §4.6)

- `IssueLabCertificate` (`fg_lot:final_release`, step-up, plant-scoped): the lot (with its field code) and a break date with at least
  one valid specimen, and optionally a delivery that took the lot (`LAB_CERTIFICATE_REFUSED` otherwise). Number
  `CR-<field code>-<DDMMYY>`, then `-2`, `-3`… for the same lot and date (E-LAB1-03-2/3). Any verdict can be certified; only tested
  results print (E-LAB1-03-4).
- `qa.certificate` keeps a **snapshot** (E-LAB1-03-5): issuer, lot (field and internal code, product, machine and short code, shift,
  plant, production date), break date, the delivery's number, customer, RNC and site, the press, the signer (parameters
  `CERT_SIGNER_NAME` / `CERT_SIGNER_TITLE`, set in Calidad › Parámetros, E-LAB1-03-6), who issued it, and per specimen measures (the
  nominal ones flagged), area, weight, load, age, kg/cm² and MPa; the summary (specimens, average and minimum in kg/cm² and MPa, CV,
  predominant condition and failure) and the lot's absorption and density. Numbers are text with 2 decimals. `qa.certificate_test`
  lists its specimens.
- ISSUED → VOIDED once (`qa.certificate_guard`): `VoidLabCertificate` (`fg_lot:final_release`, step-up, reason, cause MANUAL), or by
  itself when one of its specimens is voided (`VoidCompressionTest` voids them with cause SPECIMEN_VOIDED and returns
  `certificatesVoided`). A voided certificate prints «ANULADO».
- Each certificate has a random 24-character public code for its QR: `/verificar/certificado/?c=<company>&k=<code>`.
- `GetLabLot` returns the lot's certificates.

### Printing (E-LAB1-03-1, 7, 9, 10, 13)

- Two document types in `Sales/Printing` with editable formats (Configuración › Formatos de impresión): `LAB_CERTIFICATE` (letter, the
  table's columns: specimen, conduce, measures, area, block type, production, break, age, weight, load, kg/cm², MPa; logo on by
  default) and `RACK_LABEL` (paper `ETIQUETA_100X150`, 100 × 150 mm, one label per page). `LabPrints` reads the certificate's snapshot
  and the lot's racks through SQL — no module reference.
- `GetLabCertificatePrint` (`lab:read`): `GET …/manufacturing/lab/certificates/{id}/print`. `GetRackLabelPrint` (`production:read`):
  `GET …/manufacturing/lots/{lotId}/rack-labels?rack=` — every live rack, or one. `GetPrintDocument` (`sales:read`) refuses both types.
- A format of either type is activated only when the example shows what is mandatory: the certificate's number, its QR, the gross-area
  note and «ANULADO» on a voided one; the label's field code and QR.
- The label's QR: `/calidad/lotes/?lote=<lotId>&rack=<n>`.
- The conduce (screen and print) shows each lot's field code when it has one, else its internal code (E-LAB1-03-13).

### Scan at loading (E-LAB1-03-11/12/16)

- `ConfirmLoaded` takes, per line, `scans` — `{ lotId, rackNo? }` in the order scanned. Each lot counts once
  (`log.delivery_line_scan`, append-only); it must hold stock of the line's item in the line's source location
  (`DELIVERY_SCAN_INVALID`) and not be blocked (`STOCK_BLOCKED_BY_QUALITY`).
- `RecordGateOut` takes the scanned lots first, in that order and up to their stock, then FIFO for the rest. A scanned lot blocked
  after loading stops the gate-out (`STOCK_BLOCKED_BY_QUALITY`): the truck carries it; Dispatch cancels the delivery and plans it again.

### Not yet

The screens and the public verification page (LAB1-03b); control chart, the age curve on screen and the history before Core,
`qa.legacy_lot` (LAB1-04, E-LAB1-01-15).
