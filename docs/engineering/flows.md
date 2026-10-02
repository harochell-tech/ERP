# Critical flows per area (UX3, E-UX3-1…14)

Wave 3 of the UX audit gives each area's critical flow what its screen needs from the server. UX3-01 is the server side
(migration `0063__flows_ux.sql`, queries only: no new command, permission or error code); UX3-02 the screens.

## Close readiness (E-UX3-1)

`Reconciliation.GetCloseReadiness(PeriodId)` — GET `/reconciliation/periods/{periodId}/close-readiness`, `period:read` — answers,
read-only, whether each component of a period can close, with the close guards' own rules (`CloseComponentHandler`):

| Field | Rule |
| --- | --- |
| `ended` | the period's `ends_on` is before today's business date (else the close fails with `PERIOD_NOT_ENDED`) |
| `sealed` | no `audit.integrity_state` group of the period is unsealed: GL / inventory by posting date, domain events by business date (else `INTEGRITY_NOT_SEALED`) |
| per blocking reconciliation | each `rec.recon_blocking` code of the component with its Spanish name, its latest run **for the period** — the latest `rec.recon_run` whose `cutoff_date` is the period's end (none otherwise) — that run's status and time, and `blockingErrors`: its ERROR findings whose component is null or the component (as `ReconRun.Blocks`); null without a run |
| `ready` | ended, sealed, the component OPEN or REOPENED, and every blocking reconciliation has a run for the period with zero blocking errors |

`ended` and `sealed` are the same for every component of the period and are also given at the top. The close itself still runs
the blocking reconciliations again at the period's end; readiness only reads what was stored.

"Verificar ahora" on the close screen is the existing `RunReconciliation` command with `ReconCodes` = the component's blocking
codes and `CutoffDate` = the period's end. The cutoff is stored with each run (`rec.recon_run.cutoff_date`); BANK-GL reconciles at
it and the date-bound reconciliations use it (CONTRACT-ASSET ageing, FISC-DOC, WIP-OPEN, SHIFT-OPEN, PRODUCTION-CLOSE-ORDER,
AUTH-EXPIRY, TAX-606, CONTROLS-WAIVED); the global ones evaluate the whole ledger as of the run. A run without a cutoff (as of
today) does not count for a past period. `CloseReadinessTests` covers a blocking error (not ready, counts), a clean run
(ready, and the close then succeeds), a run without cutoff (ignored), an unsealed period and a period that has not ended.

## Reconciliations in words (E-UX3-2)

- `rec.recon_definition.name` and `.guidance` (NOT NULL from 0063): the Spanish name of each of the reconciliations (32 with PROFORMA-ASIG and CASH-SALE) and what
  to do about its findings, written from what its SQL compares. A migration that adds a reconciliation must give both.
- `rec.recon_classification (classification, name, guidance)`, immutable, `SELECT` for `rochell_app`: the 57 classifications the
  reconciliations produce.
- `ListReconciliationRuns` and `GetReconciliationRun` add the run's `name` and `guidance`; each exception adds
  `classificationName` and `guidance` (null for a classification without a row).
- `Reconciliation.ListReconciliationDefinitions` — GET `/reconciliation/definitions`, `reconciliation:read` — lists code, name,
  guidance, severity and blocking components.

Coverage is enforced by `ReconciliationTextTests`: the classification literals are embedded in thirty SQL strings, so a static
list the SQL would use means rewriting every reconciliation. Instead the test reads `src/Rochell.Reconciliation/Reconciliations.cs`
(every `'UPPER_SNAKE'` literal, minus a short explicit list of account roles, statuses, reports and rule kinds the SQL filters on),
`BankGl.cs` (each literal passed with a severity to `new ReconFinding`) and the migrated `tax.report_606` (the warnings TAX-606
unnests), and requires the set to equal the table. A new literal fails the test until it gets a row or is declared not a
classification — nothing slips through silently, and a stale row fails it too.

## Readable match keys (E-UX3-3)

`GetReconciliationRun` adds `matchLabel` to each exception, resolved in one SQL statement inside the Reconciliation module
(`MatchLabels`; the module graph allows Platform only, SQL may read any table):

| Key shape | Label |
| --- | --- |
| party id (AP-GL, AR-GL) | legal name |
| `area/item` (INV-VALUE-GL, INV-VALUE-BALANCE, VAL-RESIDUAL), `area:area/item` | area code / item code |
| `stock:location/item/lot` | location / item / lot codes |
| PO line id (GRNI-AGING) | `<PO number> línea n · item` |
| value or GL entry id (VALUE-GL-LINK) | area / item, or account · name · date |
| `GR:`, `GRR:`, `RC:`, `SI:`, `FA:`, `NC:`, `REC:`, `BNC:`, `DEP:`, `RET:`, `CHG:` + id (ACC-EVIDENCE) | the document number (reversal / correction of a receipt, NCF · supplier, statement reference · date) |
| `ap_doc:` (PAY-APPL) | NCF · supplier |
| `application:` / `APP:` | payment / receipt number |
| `line:` / `aged:` (CONTRACT-ASSET) | `CD-… línea n · item` |
| `collector:` | plant · item · month |
| `OP-DAY:` / `COST-SET:` + period id | component · period dates |
| `auth-line:id/n`, `invoice-line:` | certificate / invoice number and line |
| `account:code` | code · name |
| bank account id prefix (BANK-GL) | bank code, number masked as `••••1234` (never the full number) · rest of the key |

Any other key — including already readable ones such as `PAY:PAG-000001` or `journal:<id>` — has no label (null) and the screen
shows the raw key.

## Explain names (E-UX3-4)

`ExplainEntry`'s `entry` adds `plantCode`, `plantName`, `itemCode`, `itemDescription` and `partyLegalName` beside `plantId`,
`itemId` and `partyId` (null when the line has none).

## Receiving (E-UX3-5)

`Procurement.ListPurchaseOrdersToReceive` — GET `/procurement/purchase-orders/to-receive?plantId=&supplierId=&limit=&offset=`,
`purchase_order:read` (the permission the receive screen already uses to open an order; Almacenista holds it) and plant-scoped like
the other PO queries — lists APPROVED and PARTIALLY_RECEIVED orders, oldest first, with supplier, order date and approval time, and
per line: item, UoM, ordered, received, `openQuantity` = max(ordered − received, 0) and `maxReceivable` = max(trunc(ordered × (1 +
receipt tolerance) + approved over-receipt − received, 6), 0) — exactly PostGoodsReceipt's check (`received + quantity ≤ maximum`,
quantities with at most 6 decimals). `GetPurchaseOrder`'s lines add `openQuantity`.

## Accounts payable (E-UX3-6)

`ListSupplierInvoices` and `GetSupplierInvoice` keep `totalAmount` (net) and add:

| Field | Value |
| --- | --- |
| `itbisTotal` | Σ of the tax determination's RECOVERABLE_INPUT and NON_RECOVERABLE_INPUT lines; null until the invoice is determined (at posting) |
| `grossTotal` | net + `itbisTotal` (null with it). The payable is gross − withholding (R-04) |
| `openAmount` | the AP document's open amount; null until posted |
| `paymentStatus` | VOIDED (document voided), REVERSED (accounting reversed), NOT_POSTED (no AP document), PAID (open 0), PARTIAL (open below original), OPEN |

`GetSupplierInvoice` adds `payments`: each payment applied to its AP document (payment number, value date, status and the net
amount applied — 0 once reversed), read from `fin.ap_application` and `fin.payment` in SQL (Procurement does not reference
Treasury). Prepared payments reserve nothing (E-VS2-6), so they are not listed.

## Delivery note (E-UX3-7)

`Sales.GetDeliveryPrint(DeliveryId)` — GET `/sales/deliveries/{deliveryId}/print`, `sales:read` — mirrors `GetQuotePrint`: issuer
and customer (legal name, RNC), the order's site address, plant code and name, delivery and order numbers, order date, the
business date it was planned on, status, delivery term, gate-out time, vehicle and driver (own fleet or the customer's for a
pickup), gross / tare / net kg (net = gross − tare on the server: the screen does no arithmetic), weigh ticket, lines (item,
UoM, planned, issued, delivered) with their lots, and who received it (POD name and time).

## Smaller additions

- E-UX3-9: `CreditNoteDetail.invoiceIssuedById`, the user who issued the invoice (the credit note's issuer must differ, so the
  screen can tell before the command refuses).
- E-UX3-11: `MasterData.ListUoms` — GET `/master-data/uoms`, `master_data:read`, plant-scoped — the `md.uom` catalogue (code and
  dimension; there is no name column).
- E-UX3-8 (a), E-UX3-10, 12, 13 are web-only (the SHA-256 field is hidden, the quote watermark, …): no server change.

## Wave 4

The remaining findings (E-UX4-1…17: totals from the server, previews, sequential OC / RM numbers, bank alias, printed total,
receiving location, recipe curing minimum, receipt suggestion, Contador's reads, journal search and integrity status) are in
[ux4.md](ux4.md).
