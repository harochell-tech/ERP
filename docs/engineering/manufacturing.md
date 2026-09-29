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
