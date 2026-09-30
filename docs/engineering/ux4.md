# Remaining UX findings (UX4, E-UX4-1…17)

Wave 4 of the UX audit closes the medium and low findings. UX4-01 is the server side (migration `0064__ux4_server.sql`, one
command, eight queries, additive fields); UX4-02 and UX4-03 are the screens. E-UX4-1, 11, 12 and 14 are web-only; E-UX4-16 is out
of scope.

## Totals the screen cannot compute (E-UX4-2)

The UI does no money or quantity arithmetic, so every total a screen shows comes from the server (decimals as strings):

| Query | Added |
| --- | --- |
| `ListPurchaseOrders`, `GetPurchaseOrder` | `total` = Σ line nets; each line's `netAmount` = quantity × unit price, 2 decimals. The list already filters by `supplierId` |
| `GetArAging`, `GetApAging` | `bucketTotals`: current, bucket 1–3, over and total (the grand total) |
| `ListPayments` | `count` and `total` of every payment the filter selects (all pages, not only the page) |
| `GetTrialBalance` | per row `debitBalance` (the closing when positive) and `creditBalance` (its absolute value when negative), and `totalDebitBalance` / `totalCreditBalance` (equal when balanced). The CSV is unchanged |
| `GetBalanceSheet` | `totalEquityWithResults` = equity + result of the year + prior years; `totalLiabilitiesAndEquity` = liabilities + that |
| `ListAccounts` | `balance` (Σ debit − credit of every entry to date) and `hasEntries`: the chart offers Desactivar only at zero |
| `GetProductionDay` | `mixScrapUnits`, `freshScrapUnits`, `scrapUnits` (both) and `usageTolerancePct`; per material `differencePct` and `outOfTolerance` |
| `GetProductionRun` | per consumption `qtyPerBatch` (the run's recipe version), `difference` (real − theoretical, signed), `differencePct` (÷ theoretical × 100, 2 decimals; null at 0) and `outOfTolerance` (\|difference\| > `usage_tolerance_pct` × theoretical — USAGE-TOLERANCE's test; null when no PRODUCTION policy covers the run's date); `usageTolerancePct`, `recipeVersionId`, `recipeVersion`; the lot's `curingHoursRemaining` |
| `ListFgLots` | `curingHoursRemaining`: whole hours until the lot may be released, rounded up; 0 once it may |
| `ListFiscalAuthorizations`, `GetFiscalAuthorization` | `daysToExpiry` = valid until − today's business date (0 on the last day, negative after; null without a date) |
| `GetBankReconciliation` | `glItemsTotal`, `lineItemsTotal`: GL = statement + GL items − line items + difference |
| `ListReconciliationRuns`, `GetReconciliationRun` | `cutoffDate`, `sideALabel`, `sideBLabel` |
| `ListReconciliationDefinitions` | `sideALabel`, `sideBLabel` |

`Reconciliation.ListLatestReconciliationRuns` — GET `/reconciliation/runs/latest`, `reconciliation:read` — lists every
reconciliation (code, name, severity, side labels) with its latest run by run time, or none (A-23).

`rec.recon_definition.side_a_label` / `side_b_label` (0064, both or neither) say in Spanish what total A and total B are, for the
12 reconciliations that store totals: the 11 definitions with a totals query in `Reconciliations.cs` and BANK-GL (GL balance vs.
statement plus items in transit). A migration that adds a reconciliation with totals gives both (`Ux4ReconciliationTests` checks
that every stored run with totals has them).

## Previews (E-UX4-3, 4, 10)

Queries, so nothing is written: no `command_log`, no event, no determination. The three draft previews take lines, which do not
fit a query string: they are POST routes whose JSON body (the form's fields, without `companyId` / `sessionId`) is bound by
`QueryRunner.RunBodyAsync` and run by the query pipeline, READ ONLY. No `Idempotency-Key`; the anti-CSRF header as for any POST;
a malformed body or a JSON number for a decimal is 400 `INVALID_REQUEST`.

| Route | Query | Permission | Answer |
| --- | --- | --- | --- |
| POST `/procurement/purchase-orders/preview` `{plantId, partyId, orderDate, lines}` | `Procurement.PreviewPurchaseOrder` (plant-scoped) | `purchase_order:create` | CreatePurchaseOrder's validation, then per line net (quantity × price, 2 decimals) and ITBIS; net total, ITBIS total, total |
| POST `/sales/orders/preview` `{plantId, lines}` | `Sales.PreviewSalesOrder` | `sales_order:create` | priced as CreateSalesOrder (list in force); ITBIS in force today |
| POST `/sales/quotes/preview` `{plantId, lines}` | `Sales.PreviewQuote` | `quote:manage` | priced as CreateQuote; `specialPrice` when below the list |
| GET `/sales/customers/{partyId}/credit-preview?amount=` | `Sales.GetCreditPreview` | `sales:read` | limit, hold, exposure, `available`, `availableAfter`, overdue days vs. `overdue_days_block`, `fits`, `reasons` |
| GET `/sales/customers/{partyId}/receipt-application-suggestion?amount=` | `Sales.SuggestReceiptApplication` | `sales:read` | open invoices oldest first (due date, issue date, number) with the suggested amount each; applied, unapplied |

The ITBIS estimate is `TaxEngine.EstimateItbisAsync`: the same rule selection and fiscal gate as posting and invoicing, the same
`TaxCalculator`, ITBIS lines only (withholding is not previewed), nothing recorded. A closed gate is not an error here: ITBIS and
total are null and `itbisUnavailableCode` (FISCAL_GATE_CLOSED) / `itbisUnavailableReason` say why. Purchase ITBIS uses the order
date; sales ITBIS today's business date (a new quote's date). An exemption (CONFOTUR, e-CF 44) is decided at invoicing.

The credit preview is SubmitForCredit's rule read-only: `fits` = no hold, exposure + amount ≤ limit and overdue days ≤ block
(the CREDIT policy in force today; none is POSTING_PREREQUISITE_MISSING, as at submission). Reasons: CUSTOMER_TERMS_REQUIRED (no
approved terms), CREDIT_HOLD, CREDIT_LIMIT_EXCEEDED, OVERDUE_DAYS_EXCEEDED. `amount` in the query string is a decimal such as
`1500.00`; anything else is 400 `INVALID_PARAMETER`.

## Sequential numbers (E-UX4-5)

`DocumentNumbers.NextAsync`: OC-YYYY-NNNNNN for purchase orders (year of the order date) and RM-YYYY-NNNNNN for goods receipts
(year of the business date), the next after the year's highest six-or-seven-digit number of the company, under
`pg_advisory_xact_lock(hashtextextended('<prefix>-no:<company>:<year>'))` — the PAG-… mechanism. The receipt takes the lock after
the order and its lines (lock order N3 → N4 → number → stock). 0064 widens `purchase_order_no_format` / `goods_receipt_no_format` to
accept both forms; numbers issued before (8 hex characters) keep theirs and never move the sequence.

## Bank account alias (E-UX4-6)

`Treasury.SetBankAccountAlias(BankAccountId, ExpectedVersion, Alias)` — `bank_account:manage` (Controller), no step-up (it only
labels the account) — trims the alias, refuses more than 60 characters (BANK_ACCOUNT_ALIAS_INVALID), clears it when blank, bumps
the version and records `BankAccountAliasSet` with the previous alias. Every query that shows a company bank account returns it:
bank accounts (`alias`), payments and payment detail, statements, deposits (`bankAccountAlias`), the sales bank accounts
(`alias`) and receipts (`bankAccountAlias`, `bankCode`, `bankAccountNumber` masked — the transfer's account or the deposit's), so
the screen shows "alias · banco ••••6789".

## Supplier invoice (E-UX4-7)

`printedTotal` on `RegisterSupplierInvoice` (optional; > 0 with 2 decimals, else PRINTED_TOTAL_INVALID) is kept in
`pur.supplier_invoice.printed_total`. The list and detail return it with `printedTotalDifference` = printed − gross (net + ITBIS)
once the invoice is determined at posting; null before. The NCF check (FISCAL_NUMBER_INVALID) and the duplicate per supplier
(FISCAL_NUMBER_USED, backed by the partial unique index `si_fiscal_uq`) were already there; a concurrent registration of the same
NCF now also answers FISCAL_NUMBER_USED.

## Receiving location (E-UX4-8)

PostGoodsReceipt refuses a CURADO or TRANSITO location: LOCATION_NOT_RECEIVABLE. `ListPurchaseOrdersToReceive` returns each
order's `defaultLocationId` / `defaultLocationCode`: the plant's RECEPCION location, else its only location that is neither CURADO
nor TRANSITO, else null (then the screen asks). No schema change: `md.location` has no kind and is immutable.

## Recipes (E-UX4-9)

`PrepareRecipe` needs at least one curing hour. The CHECK `recipe_min_curing_positive` (min > 0 or not DRAFT) keeps existing
ACTIVE / SUPERSEDED versions valid and is validated when no DRAFT breaks it. A run keeps the recipe version it started with
(`mfg.production_run.recipe_version_id`): the summary's theoretical consumption and the lot's curing window use it even after a
newer version is approved — already so since MFG1-03, now covered by `Ux4ManufacturingTests`.

## Audit (E-UX4-15)

- `Audit.SearchJournals(Text)` — GET `/audit/journals?text=&limit=&offset=`, `audit:read`. A UUID matches a journal id, its source
  event or the event's document (aggregate). Other text (1–100 characters, case-insensitive) matches document numbers — goods
  receipt, supplier invoice NCF, payment, manual journal, sales invoice and credit note (number or e-NCF), receipt, deposit,
  delivery, production run — and returns the journals of that document's events and of events whose payload names the document
  (reversals, corrections, a run's summaries). Each hit: journal, event and document type, the document number, rule, journal type,
  generation, posting date, total debit, reversed journal. Newest first.
- `Audit.GetIntegrityStatus` — GET `/audit/integrity-status`, `audit:read`. The hash verification is a command, so its result is
  persisted in `core.command_log`: `lastVerification` is the latest committed VerifyHashChain (time, who, valid, per chain valid
  and seals), null when none ran (a verification that failed technically rolled back). Per chain: latest digest date, last sealed
  sequence, groups PENDING_SEAL and SEAL_ERROR now.

## Contador (E-UX4-13)

0064 grants CONTADOR `period:read` and `reconciliation:read`: the close checklist and the reconciliations are read by the person
who prepares the adjustments.

## Tests

`Ux4ProcurementTests`, `Ux4SalesTests`, `Ux4TreasuryTests`, `Ux4FinanceTests`, `Ux4ReconciliationTests`, `Ux4ManufacturingTests`,
`Ux4AuditTests` and `PreviewApiTests` (the POST query over HTTP: body binding, 400 on a number, no command_log), with worked values
in the comments; `IamSchemaTests` (CONTADOR) and `EndpointCoverageTests` (167 commands) are updated.
