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
