# Sales, dispatch and collections (VS#3)

Frozen Baseline `docs/architecture/vs3/frozen-baseline-vs3.md`, approved errata E-VS3-1…17 and E-VS3-01-1…17. No real
commercial, fiscal or accounting data until B-02 or a reconciled parallel run (E-VS3-16).

## Master schema (VS3-01)

Migration `0037__sales_master_schema.sql` creates the schemas `sal` and `log` with the master tables only; the documents of the
slice arrive with their PRs (E-VS3-01-17).

| Object | What it holds | Guarantees |
| --- | --- | --- |
| `md.party` (extended) | `is_customer`, `customer_status` (DRAFT → ACTIVE ⇄ BLOCKED), phone, e-mail, address | A party may be supplier and customer (E-VS3-01-1); RNC, legal name and party status change only while DRAFT; the customer flag never goes back; contact data change at any time; every customer status has its `core.state_history` row (aggregate `Customer`) — E-VS3-01-8/9 |
| `md.item` (extended) | Type FINISHED_GOOD with categories BLOQUE, ADOQUIN, OTRO_PT | Category matches the type (E-VS3-01-2) |
| `sal.customer_terms_version` | Payment days (0–365), credit limit (DOP), credit hold | Only for customers; DRAFT → ACTIVE → SUPERSEDED; content editable only in DRAFT; one ACTIVE per customer; approver ≠ preparer (E-VS3-17 (a), E-VS3-01-10) |
| `md.standard_cost_version` | Unit cost of a finished good per valuation area, base UoM | Finished goods only; one ACTIVE per item and area; approver ≠ preparer (E-VS3-01-3, E-VS3-01-13) |
| `sal.price_list_version`, `sal.price_list_line` | Prices in DOP without ITBIS per finished good and UoM | One ACTIVE list per company; each item and UoM once per version; lines only in DRAFT, never changed (E-VS3-01-14) |
| `log.vehicle`, `log.driver` | Plate and capacity; name and cédula | Unique plate / cédula per company, immutable; ACTIVE ⇄ INACTIVE with history; never deleted (E-VS3-01-15) |
| `fin.account_role` | 13 roles seeded unmapped (E-VS3-01-7, E-VS3-01-16) | Control: AR_CONTROL, CONTRACT_ASSET, UNAPPLIED_RECEIPTS, CASH_IN_TRANSIT (subledger **AR**), FINISHED_GOODS, FINISHED_GOODS_IN_TRANSIT (subledger INV); `gl_entry_role_subledger` enforces the subledger |

Every versioned table shares the transition rule `sal.version_transition_allowed` and records its status changes (aggregates
`CustomerTerms`, `StandardCost`, `PriceList`, `Vehicle`, `Driver`). RLS by company on every new table; the application role has
column-level UPDATE grants only (no DELETE).

Roles and permissions (E-VS3-01-11/12):

| Role | Permissions |
| --- | --- |
| VENDEDOR | `customer:create`, `customer:update`, `sales:read` |
| CREDITO | `customer:activate`, `customer_terms:prepare`, `sales:read` |
| DESPACHO | `fleet:manage`, `sales:read` |
| FACTURACION, COBROS | `sales:read` (their commands arrive with VS3-05…07) |
| CONTROLLER | + `customer_terms:approve`, `standard_cost:prepare`, `price_list:prepare`, `sales:read` |
| APROBADOR_POLITICAS | + `standard_cost:approve`, `price_list:approve` |
| AUDITOR, DIRECTOR | + `sales:read` |

SoD: `customer:create` ≠ `customer:activate`; prepare ≠ approve for customer terms, standard cost and price list. Totals: 77
permissions, 21 roles, 29 SoD rules. Staging's `seed.sh` lists a synthetic identity per new role.

Tests: `SalesMasterSchemaTests` (MasterData), `IamSchemaTests` (matrix), `SchemaTests` (inventory).

## Master commands (VS3-02)

Module `Rochell.Sales` (E-VS3-02-2). Migration `0038__supplier_payment_terms.sql`.

| Command (`/sales/…`) | Permission | Effect |
| --- | --- | --- |
| `create-customer` | `customer:create` | New LOCAL party (DRAFT) as DRAFT customer, or an existing party with that RNC flagged as customer (`existingParty`) |
| `update-customer` | `customer:update` | Contact data always; RNC and legal name only while the party is DRAFT |
| `activate-customer` | `customer:activate` + step-up | DRAFT → ACTIVE; needs ACTIVE terms; activates a DRAFT party too |
| `prepare-customer-terms` | `customer_terms:prepare` | Creates or replaces the single DRAFT (days 0–365, limit ≥ 0 with 2 decimals, credit hold) |
| `approve-customer-terms` | `customer_terms:approve` + step-up | DRAFT → ACTIVE from today; the previous ACTIVE → SUPERSEDED; approver ≠ preparer |
| `prepare-standard-cost`, `approve-standard-cost` | `standard_cost:*` (+ step-up to approve) | Same mechanics per item and valuation area; approval refused with `STOCK_EXISTS` while the item has stock there |
| `prepare-price-list`, `approve-price-list` | `price_list:*` (+ step-up to approve) | A new DRAFT with all its lines; approval replaces the list in force |
| `register-vehicle`, `update-vehicle`, `deactivate-vehicle`, `activate-vehicle`, `register-driver`, `update-driver`, `deactivate-driver`, `activate-driver` | `fleet:manage` | Fleet master data |
| `/master-data/set-supplier-payment-terms` | `supplier:update` | Supplier's payment days (null clears); the invoice form proposes the due date |
| `/master-data/create-finished-good` | `item:create` | Finished good in DRAFT (BLOQUE, ADOQUIN, OTRO_PT); `activate-item` (Controller) activates it (E-VS3-02-12) |

Queries (`sales:read`): `GET /sales/customers[/{partyId}]`, `/sales/customer-terms`, `/sales/standard-costs`,
`/sales/price-lists[/{id}]`, `/sales/vehicles`, `/sales/drivers`.

Tests: `CustomerTests`, `PricingTests`, `FleetTests` (`tests/Rochell.Sales.Tests`), `SupplierTests` (payment terms), `ItemTests` (finished goods).
