# Master data (PR-04)

| Object | Created by | Lifecycle | Notes |
| --- | --- | --- | --- |
| Plant + valuation area | `rochell-migrate create-plant <company-rnc> <PLANT> <AREA>` | Immutable | One valuation area per plant |
| Location | `rochell-migrate create-location <company-rnc> <PLANT> <LOCATION>` | Immutable | Unique code per plant |
| Supplier (`md.party`) | `CreateSupplier` (supplier:create) | DRAFT → ACTIVE via `ActivateSupplier` (supplier:activate, step-up) | `UpdateSupplier` only in DRAFT; RNC 9/11 digits, unique per company |
| Raw material (`md.item`) | `CreateRawMaterial` (item:create) | DRAFT → ACTIVE via `ActivateItem` (item:activate) | Code upper-case; closed category list; base UOM immutable |
| UOM conversion | `DefineUomConversion` (item:activate) | New conversion closes the open one | Always towards base UOM; never retroactive; no overlaps (exclusion constraint) |

Every change increments `version` by 1; commands pass `ExpectedVersion` and get `VERSION_CONFLICT` if someone changed the row first.
All company-scoped tables use composite foreign keys `(company_id, id)` and row-level security.

## DGII RNC registry (E-RNC-1…8, migration 0047)

`md.rnc_registry` holds the DGII's weekly "Listado de todos los RNC" (about 790 thousand taxpayers); it is public reference data
shared by every company (no `company_id`, no RLS) and the application only reads it.

- **Import** (deployment role): `rochell-migrate import-rnc-registry <DGII_RNC.zip|txt> [yyyy-MM-dd]`. `RncRegistryFile.Parse` reads
  Latin-1, eleven `|`-separated fields per record, and joins the lines of the few names that carry line breaks. Rows without a 9- or
  11-digit number, a name or a status are skipped; a repeated number keeps its last row. The import replaces the whole table in one
  transaction and appends a row to `md.rnc_registry_import` (source date — the ZIP entry's unless given —, SHA-256, rows, skipped,
  who). A file without valid rows replaces nothing. On staging: `deploy/staging/rnc-weekly.sh` (see `staging.md`).
- **Lookup** (`rnc:read`): `GET /master-data/rnc/{rnc}` → `RncLookup` (`found`, names, status, regime and the registry's date).
  The customer and supplier forms call it when the RNC field is left (`web/src/components/RncLookup.tsx`): they propose the
  legal name when it is empty and warn when the number is missing or not ACTIVO. They never block, and `CreateSupplier` /
  `CreateCustomer` still check the format only (`IRncRegistry` runs inside the command transaction and does no I/O).
- **Review** (`rnc:read`): `GET /master-data/rnc-registry` → the last import and the discrepancies (NOT_FOUND, NOT_ACTIVE,
  NAME_DIFFERS) among the company's customers and suppliers; page Maestros › Padrón RNC.
