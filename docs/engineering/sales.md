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

### Fleet details (FLT-01, migration 0076; E-FLT-1…5)

- `log.vehicle.fleet_code` — the «ficha» ("BR 09"): `^[A-Z0-9]+( [A-Z0-9]+)*$`, 2–12 characters, unique per company where present
  (partial unique index); `insurance_policy_no` (≤ 40). `log.driver.license_expires_on`. All nullable: rows from before stay without
  them (E-FLT-5).
- `RegisterVehicle` / `UpdateVehicle` take `FleetCode` (required, normalized to capitals with single spaces; `FLEET_CODE_INVALID`,
  `FLEET_CODE_DUPLICATE`) and `InsurancePolicyNo`; `RegisterDriver` / `UpdateDriver` take `LicenseExpiresOn`. An update writes the
  three vehicle members (or the driver's name and date) as given: the screen sends the current values back.
- Queries: `VehicleView.fleetCode` / `insurancePolicyNo`; `DriverView.licenseExpiresOn` / `daysToLicenseExpiry` (expiry − today's
  business date; the screen warns at ≤ 30, E-FLT-4 — nothing in the server blocks on it). `DeliverySummary.fleetCode` / `driverName`
  (our driver, or the customer's for a pickup); `GetDeliveryPrint.vehicleFleetCode`, also in the conduce sent by e-mail;
  `GET /sales/deliveries` filters by `vehicleId` and `driverId`.
- Tests: `FleetTests` (ficha normalization and uniqueness, policy, licence days, the delivery print and the list filters).

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

## Commercial credit note (VS3-06)

Migration `0043__sales_credit_note.sql`: `sal.credit_note` (NC-…, reason category and text, the three statuses with their
valid combinations, K-25 and ADR-027 evidence, e-NCF `E34…`), `sal.credit_note_line` (lines only while DRAFT and only of the
note's own invoice), `tax.external_fiscal_record` now for exactly one invoice or credit note, invoice status CREDITED, rule
P-22 (DRAFT), permissions `credit_note:create` / `credit_note:issue` for Facturación.

| Command (`/sales/…`) | Permission | Effect |
| --- | --- | --- |
| `create-credit-note` | `credit_note:create` | DRAFT NC-… on a fiscalized invoice: net per invoice line (≤ what it still has to credit), ITBIS at the invoice line's rate |
| `issue-credit-note` (step-up) | `credit_note:issue` | Not the invoice's issuer; rechecks the lines, AR open ≥ total; P-22, AR open down; CONFIRMED / POSTED / PENDING_EXTERNAL; invoice CREDITED once fully credited |
| `record-external-credit-note-document` | `fiscal_document:record` | e-NCF E34 and evidence checked against the note → ACCEPTED_EXTERNAL, FISCALIZES link |

P-22: Dr SALES_DISCOUNTS (party) net / Dr ITBIS_PAYABLE ITBIS / Cr AR_CONTROL (party, the invoice's AR document) total, in
AR-REC. The ITBIS of a credited line is `round(net × rate, 2)`, except the note that credits the rest of the line, which takes
the rest of its ITBIS, so a fully credited invoice leaves ITBIS_PAYABLE and AR_CONTROL at exactly 0. Issuing locks the
invoice first, so two notes of the same invoice are serialized; a DRAFT that another note overtook is refused at issue.

Queries: `GET /sales/credit-notes[/{id}]`, `/sales/credit-notes/{id}/fiscal-package` (with the modified e-NCF); `GetInvoice`
adds `creditNotes` and `creditable` (per line: credited and remaining net).
Tests: `CreditNoteTests` (SAL-08, remainder ITBIS and CREDITED, permissions and four eyes, overtaken draft).

## Customer receipts (VS3-07)

Migration `0044__customer_receipts.sql`: `fin.receipt` (REC-…, three status columns: RECORDED / BOUNCED / REVERSED,
UNAPPLIED / PARTIALLY_APPLIED / APPLIED, IN_TRANSIT / DEPOSITED / MATCHED; ADR-027 on the life cycle, K-25 on P-23 / P-24),
`fin.receipt_deposit` (DEP-…, total = its receipts, K-25 on P-29), `fin.ar_application` (append-only, an unapply adds the mirror
row), `fin.customer_withholding` (ACTIVE / REVERSED, certificate unique per customer and kind), statement lines matched to a
receipt or a deposit, rules P-23, P-24, P-25, P-27, P-29 (DRAFT), permissions and SoD.

| Command | Permission | Effect |
| --- | --- | --- |
| `sales/record-receipt` | `receipt:record` | REC-…; P-23: transfer → BANK at its value date, cheque / cash → CASH_IN_TRANSIT; Cr UNAPPLIED_RECEIPTS |
| `sales/deposit-receipts` | `receipt:deposit` | DEP-… of cheques and cash in transit; P-29 BANK / CASH_IN_TRANSIT |
| `sales/apply-receipt` | `receipt:apply` | P-25 per invoice; open amounts down; invoice PARTIALLY_PAID / PAID |
| `sales/unapply-receipt` | `receipt:apply` | Exact reversal of one application's P-25 |
| `sales/record-customer-withholding` | `customer_withholding:record` | ITBIS or ISR from the certificate; P-27 |
| `sales/reverse-customer-withholding` (step-up) | `customer_withholding:reverse` | Exact reversal of P-27 |
| `sales/mark-receipt-bounced` (step-up) | `receipt:bounce` | Deposited cheque: reversals of its live P-25, then P-24; BOUNCED |
| `sales/reverse-receipt` (step-up) | `receipt:reverse` | Nothing applied, not deposited or matched: exact reversal of P-23; REVERSED |
| `treasury/match-bank-line-to-receipt` | `bank_line:match` | CREDIT ↔ transfer receipt (±10 days) or deposit (+10 days); DEBIT ↔ bounced cheque |

`UnmatchBankLine` also takes back a receipt's or deposit's line (receipt / deposit and its receipts back to DEPOSITED).
Lock order: invoices (id order) → AR documents (id order) → receipt → bank account; a bounce refuses if an application was
added between its read and its locks. The invoice status follows its AR document (`InvoiceStanding`, E-VS3-07-11), also for
credit notes. BANK-GL items now include `OUTSTANDING_RECEIPT`, `OUTSTANDING_DEPOSIT`, `OUTSTANDING_BOUNCE`, and a transfer
receipt with its reversal both in transit cancel out.

Queries: `GET /sales/receipts[/{id}]`, `/sales/deposits[/{id}]`, `/sales/invoices?openOnly=true&partyId=`.
Tests: `ReceiptTests` (AR-01, AR-02, AR-03, concurrent applications, unapply / reverse / withholding corrections).

## AR reconciliations and the AR-REC close (VS3-08)

Migration `0045__ar_reconciliations.sql`: five reconciliations, their blocking, two REVENUE_ACCOUNTING parameters and AR-REC as
also required by P-15, P-15R, P-16 and P-30 (version 1, corrected while DRAFT).

| Reconciliation | Checks | Blocks |
| --- | --- | --- |
| AR-GL | Open AR documents = AR_CONTROL, per customer | AR-REC |
| CONTRACT-ASSET | Per delivery line: (delivered − invoiced) × order price = CONTRACT_ASSET + UNBILLED_RECEIVABLE (subledger = line); warning UNBILLED_AGED after `unbilled_aging_alert_days` | AR-REC |
| RECEIPT-APPL | Receipt applications + unapplied = amount; AR document original − open = applications + withholdings + credit notes; one P-25 AR line per application row; UNAPPLIED_RECEIPTS and CASH_IN_TRANSIT per receipt | AR-REC, BANK-REC |
| FISC-DOC | Invoices and credit notes dated ≤ cutoff with a pending e-CF | AR-REC |
| DELIVERY-OPEN | Deliveries in transit longer than `delivery_open_alert_hours` (warning; FAILED without the parameter) | — |
| ACC-EVIDENCE | Now also invoices, credit notes, withholdings, receipts, bounces and deposit slips | + AR-REC |

The AR-REC snapshot holds open AR and AR_CONTROL by customer plus the contract asset and unapplied receipts totals.
Tests: `ArCloseTests` (AR-04, and a pending e-CF plus a tampered AR document blocking the close).

## AR queries and E2E-S1 over the API (VS3-09)

Migration `0046__ar_aging.sql`: CREDIT parameters `ar_aging_bucket_1_days`, `ar_aging_bucket_2_days`, `ar_aging_bucket_3_days`.

| Query | Route | Notes |
| --- | --- | --- |
| GetArAging | `GET /sales/ar-aging?asOf=[&format=csv]` | Open invoices per customer by days past due in the CREDIT buckets (POLICY_MISSING without them); unapplied receipts apart; net |
| GetCustomerStatement | `GET /sales/customers/{id}/statement?from=&to=[&format=csv]` | From AR_CONTROL + UNAPPLIED_RECEIPTS of the customer; applications left out; ≤ 366 days |
| ListSalesOrders | `GET /sales/orders?from=&to=` | Order date |
| ListDeliveries | `GET /sales/deliveries?partyId=&from=&to=` | Gate-out date (Dominican Republic) or the planning date |

The order detail gives `salesOrderLineId` per line. CSV files share the ledger reports' writer (`LedgerCsv.Writer`).
Tests: `ArQueryTests` (buckets, advance apart, missing policy, statement and CSV, filters);
`Rochell.Api.Tests` · `SalesAcceptanceTests.E2ES1_…` (E2E-S1 over HTTP, E-VS3-09-7).

## Properties, concurrency and acceptance (VS3-11)

`SalesPropertyTests` (INV-S, E-VS3-11-1/2/5) runs seeded random sequences of the whole order-to-cash flow and re-sums the
invariants after every step, with AR-GL, CONTRACT-ASSET, RECEIPT-APPL, ACC-EVIDENCE and the inventory reconciliations every 25
steps (`ROCHELL_INVS_SEEDS`, `ROCHELL_INVS_STEPS`; workflow `inv-s`). `SalesConcurrencyTests` (E-VS3-11-3) races gate-outs over
the stock, credit notes over one line, an application against a bounce, and a receipt against the AR-REC close. The first runs
found a spurious version conflict between two gate-outs of the same order; `Orders.LockCurrentAsync` now locks and reads the order
in one step (E-VS3-11-7). The acceptance matrix is `docs/acceptance/vs3.md`, enforced by `AcceptanceVs3TraceabilityTests`.

## ENT1-01 — the driver's confirmation from the delivery note's QR: schema (E-ENT-1…8, E-ENT1-01-1…10)

Migration 0101:

- `log.driver_pin`: one PIN per driver, set by DESPACHO (`driver_pin:manage`): PBKDF2 hash (32 bytes), 16-byte salt, ≥ 100,000
  iterations; the PIN itself is never stored.
- `log.delivery_link`: one per own-transport delivery, created at gate out; `generation` (the token is an HMAC of the delivery and the
  generation under a server key, so the database holds no token and reprints show the same QR), `status` ACTIVE / LOCKED (exactly 5
  failed PINs) / CONFIRMED / ANNULLED, `expires_at` (7 days). `delivery_link:reopen` starts a new generation.
- `log.delivery_link_attempt`: every PIN try with the client address, append-only (5 per link, 30 per hour per address).
- `log.driver_confirmation`: append-only, one per link generation — receiver, optional cédula, FULL / DIFFERENCES (differences need a
  note), phone time, server time and the confirmed time (the phone's only up to the server's + 5 minutes), optional location, PHOTO /
  SIGNATURE evidence reference and SHA-256 in the private bucket, client address, event.
- `log.pod.driver_confirmation_id`: the POD made from, or completed from, a driver's confirmation.
- Service identity `…d004` «Confirmación de entrega» with role CONFIRMACION_ENTREGA, holding only `delivery:driver_confirm`;
  `rochell-migrate create-company` assigns it. 147 permissions, 29 roles.

## ENT1-02 — the driver's confirmation: server (E-ENT-1…6, E-ENT1-01-1…10)

- Gate out of an own-transport delivery opens its link (`log.delivery_link`, 7 days). A return trip, or a POD recorded by Dispatch,
  annuls an open link; a POD after the driver reported differences cites that confirmation (`log.pod.driver_confirmation_id`).
- `SetDriverPin` (`driver_pin:manage`): four digits, PBKDF2-SHA256 210,000 iterations with a 16-byte salt; events `DriverPinSet`
  (aggregate `DriverPin`, no PIN in the payload). `ReopenDeliveryLink` (`delivery_link:reopen`): a link ACTIVE or LOCKED of a delivery
  in transit gets a new generation, 0 failures and 7 more days; the old QR stops working.
- `DriverLinkKey`: the link's HMAC-SHA256 of `company:delivery:generation` under the server key (`Rochell:Deliveries:LinkKeyFile`),
  base64url. `GetDeliveryPrint.driverLinkPath` is `/entrega/?c=…&d=…&g=…&k=…` while the delivery is in transit and its link ACTIVE.
- Service identity CONFIRMACION_ENTREGA (`delivery:driver_confirm`): `VerifyDriverPin` and `ConfirmDeliveryByDriver` answer a wrong
  PIN instead of throwing, so the try (`log.delivery_link_attempt`) is committed: 5 wrong lock the link, 30 wrong an hour from one
  address answer THROTTLED. FULL inserts the confirmation, closes the link and records the POD through `RecordPodHandler.RecordAsync`
  with every line received; DIFFERENCES (a note is required) only the confirmation. Events on aggregate `DeliveryLink`.
- `GetDriverDelivery`: number, customer, site, driver, vehicle and lines without prices, and the state (INVALID, ACTIVE, EXPIRED,
  LOCKED, CONFIRMED, ANNULLED, RECORDED_BY_DISPATCH). `GetDelivery` gains `driverLink` and `driverConfirmation`; `GetDriverEvidence`
  (`sales:read`) returns the photo from the store after checking its SHA-256.
- Public API (`Rochell.Api/Deliveries/DriverPages.cs`, no sign-in, as the service identity): `GET /api/v1/public/deliveries/{c}/{d}?g&k`,
  `POST …/pin`, `POST …/confirm` (multipart with `evidence`, an `Idempotency-Key`; JPEG / PNG by their first bytes, ≤ 5 MB; the PIN
  is checked first, then the photo goes to the store under `entregas/<company>/<delivery>/<id>.jpg|png`, then the confirmation).
- Evidence store `IEvidenceStore` (Platform): `S3EvidenceStore` (private B2 bucket, own key files) or `FileSystemEvidenceStore`
  (Development / Test). 275 commands. Staging steps: `staging.md` › Drivers' page.
