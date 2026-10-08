# Quality lab (LAB-1)

Baseline: `docs/architecture/lab1/frozen-baseline-lab1.md` (E-LAB1-1…10). Acceptance matrix: `docs/acceptance/lab1.md`.
The lab's code lives in `Rochell.Manufacturing/Quality` with its own schema `qa` (E-LAB1-01-1); the module graph does not change.
LAB-1 posts nothing to the ledger.

## LAB1-01 — schema, field code, short code, requirements, tests (E-LAB1-01-1…15; migration 0105)

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
  (E-LAB1-01-6; the same day is age 0, flagged `ageZero`). Per specimen (baseline §4.1): gross area = width × length — a missing
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

### Not in LAB1-01

The lot's evaluation, `FINAL_RELEASED`, the automatic block and the recall (LAB1-02); certificate, rack label and scan at the
gate-out (LAB1-03); control chart and the history before Core, `qa.legacy_lot` (LAB1-04, E-LAB1-01-15).
