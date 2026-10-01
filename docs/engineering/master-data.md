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

## Bulk load of suppliers and customers (IMP-01, E-IMP-1…11, E-IMP-01-1…12, migration 0065)

The owner exports suppliers and customers from ADM Cloud as `.xlsx`. The browser sends the file base64 (≤ 5 MB); the server reads
the first sheet (`SpreadsheetFile`: shared and inline strings, numbers as written, no library; or UTF-8 `.csv`) and
`PartyImport` decides what each row would do. The preview queries and the import commands share that analysis, so the screen shows
exactly what the command then writes.

| Column | Use |
| --- | --- |
| Razón Social, ID Fiscal | Required columns. The identifier is 9 or 11 digits once dashes and spaces are removed |
| Teléfono 1, Correo Electrónico | Phone (≤ 30) and e-mails, split on `;` or `,` (≤ 10, repeated ones once) |
| Término de Pago | "Contado" = 0, "N días" = N (≤ 365); empty = none (suppliers) or cash (customers) |
| Límite de Crédito | Customers only, optional; 0 without it |
| Any other | Ignored; the preview lists them |

| Row outcome | Meaning |
| --- | --- |
| CREATE | New party, DRAFT. Legal name from `md.rnc_registry` when the RNC is there, else the file's |
| LINK | Customers only: the supplier with that RNC also becomes a customer and keeps its name (E-VS3-02-3) |
| EXISTS | Already a supplier / customer (`ALREADY_SUPPLIER`, `ALREADY_CUSTOMER`), or a customer-only party in the supplier file (`CUSTOMER_ONLY`) |
| DUPLICATE | The RNC appears earlier in the file |
| REJECTED | `NAME_INVALID`, `ID_MISSING`, `ID_INVALID`, `PHONE_INVALID`, `EMAIL_INVALID`, `TERMS_INVALID`, `CREDIT_LIMIT_INVALID` |

| Command / query | Permission | Notes |
| --- | --- | --- |
| `POST /master-data/suppliers/import-preview` (`PreviewSupplierImport`) | `supplier:import` | Read-only |
| `ImportSuppliers` | `supplier:import` + step-up (Comprador) | DRAFT suppliers with phone, e-mails and payment term; event `SuppliersImported` with the file's SHA-256; the result lists every row |
| `ActivateSuppliers` | `supplier:activate` + step-up | Up to 500; one that is not DRAFT is skipped and reported |
| `SetSupplierContact` | `supplier:update` | Phone and e-mails of a supplier, at any time |
| `POST /sales/customers/import-preview` (`PreviewCustomerImport`) | `customer:import` | Read-only |
| `ImportCustomers` | `customer:import` + step-up (Vendedor) | DRAFT customers and their DRAFT terms (prepared by the importer); event `CustomersImported` |
| `ApproveCustomerTermsBatch` | `customer_terms:approve` + step-up (Controller) | Up to 500; one the approver prepared, or not DRAFT, is skipped |
| `ActivateCustomers` | `customer:activate` + step-up (Crédito) | Up to 500; one without approved terms is skipped |

`md.party_email` (company, party, position 1–10, e-mail) holds each party's list; position 1 is also `md.party.email`, which the
existing queries read. Commands replace the list as a whole under the party's row lock. `CreateCustomer` / `UpdateCustomer` take
`emails`; `GetCustomer` and `ListSuppliers` return it. One import runs at a time per company (advisory lock). SoD:
`supplier:import` ≠ `supplier:activate`, `customer:import` ≠ `customer:activate`.

The host runs with invariant globalization: header matching folds Spanish accents itself instead of Unicode normalization.
Load suppliers first, then customers (E-IMP-01-10). Real files never enter the repository (E-IMP-11).
