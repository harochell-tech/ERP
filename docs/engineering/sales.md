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

## Opening finished goods (VS3-02b)

Migration `0039__opening_finished_goods.sql`: movement type OPENING, schema `mig` (`migration_batch`,
`opening_inventory_line`), rule OPEN-INV (DRAFT), reconciliation MIGRATION-CLEARING (warning), permissions
`opening_inventory:prepare` (Controller) and `opening_inventory:post` (Aprobador de políticas). E-VS3-02b-1 amends E-VS3-01-5:
no CLI insert.

| Command (`/sales/…`) | Effect |
| --- | --- |
| `prepare-opening-inventory` | CSV `planta,ubicacion,producto,cantidad,documento` (base64) and cutover date → DRAFT batch; every line valued at the ACTIVE standard cost (2 decimals) |
| `post-opening-inventory` (step-up, not the preparer) | Posting date = cutover − 1 (INV-MOV open, never moved); one lot per line `AP-<item>-<cutover>-<n>`; OPENING quantity and value entries; OPEN-INV journal; the lines become live |
| `reverse-opening-inventory` (step-up, reason) | Exact reversal of the whole batch while no lot moved and INV-MOV is open; the lines stop being live, so the file can be loaded again |

Guarantees: batch four eyes and status evidence (ADR-027, K-25 against its journal); lines only in DRAFT and only finished goods;
unique live document and posted file. P-3 now counts FINISHED_GOODS and FINISHED_GOODS_IN_TRANSIT with RAW_MATERIAL, and so do
INV-VALUE-GL, VALUE-GL-LINK and the INV-MOV close snapshot.

Queries (`configuration:read`): `GET /sales/opening-batches[/{batchId}]`. Tests: `OpeningInventoryTests`.

## Sales order and credit (VS3-03)

Migration `0040__sales_order_credit.sql`: `sal.sales_order` (PV-…, statuses DRAFT, PENDING_CREDIT, CONFIRMED, … CANCELLED),
`sal.sales_order_line` (versioned while DRAFT; delivered and invoiced quantities with `qty_invoiced ≤ qty_delivered`),
`sal.credit_check` (every evaluation, decided once), policy CREDIT (`overdue_days_block`), permissions `sales_order:create`,
`sales_order:cancel`, `credit:approve` and SoD credit:approve ≠ sales_order:create.

| Command (`/sales/…`) | Permission | Effect |
| --- | --- | --- |
| `create-sales-order`, `update-sales-order-draft` | `sales_order:create` | DRAFT priced from the list in force (net of ITBIS) |
| `submit-for-credit` | `sales_order:create` | Evaluates exposure + order against the limit, hold and overdue days → CONFIRMED (auto) or PENDING_CREDIT |
| `approve-credit` (step-up), `reject-credit` (reason) | `credit:approve` | PENDING_CREDIT → CONFIRMED / DRAFT; not the order's creator |
| `cancel-sales-order` | `sales_order:cancel` | DRAFT, PENDING_CREDIT or CONFIRMED without deliveries → CANCELLED with a reason |

`CreditExposure` computes the parts (open AR and overdue days are 0 until the receivables exist). A customer's credit decisions
are serialized by an advisory lock. Queries: `GET /sales/orders[/{id}]`, `GET /sales/customers/{partyId}/exposure`.

Tests: `SalesOrderTests` (SAL-01, SAL-02, rejection and resubmission, validations, concurrency).

## Deliveries and control transfer (VS3-04)

Migration `0041__delivery_control_transfer.sql`: movement type TRANSFER, `md.location.is_transit`, role UNBILLED_RECEIVABLE,
policy REVENUE_ACCOUNTING, `log.delivery_term_policy`, `log.delivery`, `log.delivery_line`, `log.delivery_line_lot`, `log.pod`,
`inv.control_assessment`, rules P-15, P-15R, P-16, P-30 (DRAFT), permissions `delivery:manage` and `sales_order:close`.

| Command (`/sales/…`) | Effect |
| --- | --- |
| `plan-delivery` | CD-… of a CONFIRMED / PARTIALLY_DELIVERED order; quantity ≤ ordered − delivered − planned |
| `start-loading` | Our vehicle and driver (site) or the customer's plate and driver (pickup) |
| `confirm-loaded` | Source location per line; the base quantity must be there (unit conversion in force) |
| `record-gate-out` (C-08) | Weighing (net ≤ capacity), FIFO lots; pickup: ISSUE + P-16 (COGS / FINISHED_GOODS, CONTRACT_ASSET or UNBILLED_RECEIVABLE / REVENUE_PRODUCT), order delivered; site: TRANSFER to TRANSITO + P-15 |
| `record-pod` (C-09) | Received → ISSUE + P-16 from transit; returned → TRANSFER back + P-15R; missing → ISSUE + P-30; exceptions need a reason |
| `record-return-trip` | Everything back (P-15R), RETURNED |
| `cancel-delivery` | Before the gate, with a reason |
| `close-short-sales-order` (`sales_order:close`) | PARTIALLY_DELIVERED → CLOSED, reason, no open deliveries, not the order's creator |

Each step appends its own events (GoodsIssued, ControlTransferred, GoodsReturnedFromTransit, TransitLossRecognized), one per
rule, and writes an `inv.control_assessment` (GATE_OUT / POD, TRANSFERRED / RETAINED with the revenue policy version).
`PostingEngine.PostingDateAsync` dates the movements before their journals, so each issue is valued after the previous one.
Queries: `GET /sales/deliveries[/{id}]` (lines, lots, POD, assessments, history).

Tests: `DeliveryTests` (SAL-03, SAL-04, SAL-05, return trip, transport / weight / close-short rules).

## Invoice, sales ITBIS and external e-CF (VS3-05)

Migration `0042__sales_invoice.sql`: SALES_ITBIS / OUTPUT, close component AR-REC, `fin.ar_document`, `sal.invoice` (three
statuses of E-1 with their valid combinations, K-25 and ADR-027 evidence), `sal.invoice_line`, `tax.external_fiscal_record`,
link types INVOICES / FISCALIZES, rule P-18 (DRAFT), permissions and SoD; the invoiced quantity of a delivery line may go
back on a void.

| Command (`/sales/…`) | Permission | Effect |
| --- | --- | --- |
| `create-invoice-from-deliveries` | `invoice:create` | DRAFT FA-… of delivered, not yet invoiced lines of one customer |
| `issue-invoice` (step-up) | `invoice:issue` | Sales ITBIS (Tax Engine, SALE), AR document, P-18, invoiced quantities; CONFIRMED / POSTED / PENDING_EXTERNAL |
| `record-external-fiscal-document` | `fiscal_document:record` | e-NCF and evidence checked against the invoice → ACCEPTED_EXTERNAL |
| `void-unfiscalized-invoice` (step-up) | `invoice:void` | Never fiscalized, no receipts → VOIDED / REVERSED; the delivery becomes billable again |

Queries: `GET /sales/invoices[/{id}]`, `/sales/invoices/{id}/fiscal-package`, `/sales/billable-deliveries`.
Tests: `InvoiceTests` (SAL-06, SAL-07, SAL-09 concurrent, void, fiscal gate).
