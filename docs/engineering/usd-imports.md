# Imports and foreign suppliers in USD (USD-1)

Baseline `docs/architecture/usd1/frozen-baseline-usd1.md`; approved errata E-USD-1…9 and the per-PR rows below.

## USD1-01 — schema (migration 0085; E-USD1-01-1…7)

The books stay in pesos: every amount column that existed keeps its peso value; USD documents add their currency, rate and USD amounts.

| Object | What it holds | Guarantees |
| --- | --- | --- |
| `fin.exchange_rate` | DOP per USD per day, 4 decimals, with its source | DRAFT → ACTIVE (approved by someone else, `core.four_eyes`) → SUPERSEDED; DRAFT → DISCARDED; one ACTIVE per currency and day; an approved rate never changes; state history |
| `pur.purchase_order.currency`, `pur.supplier_invoice.currency` | DOP or USD | A FOREIGN supplier deals in USD, a LOCAL one in pesos; never changes |
| `pur.supplier_invoice` `exchange_rate`, `total_amount_fc`; lines `unit_price_fc`, `net_amount_fc` | The USD amounts beside the peso ones | A USD invoice has its rate and USD total; its number is the supplier's own (1–40 characters), a peso invoice keeps the NCF format |
| `pur.customs_declaration` | The DUA: number, date, DGA (a local supplier), CIF, duties, ITBIS, other charges, due date | Amounts in pesos to 2 decimals, something owed; one live DUA per number; state history |
| `pur.import_settlement` (+ `_document`, `_allocation`) | LI-YYYY-NNNNNN per shipment: the documents whose cost it gathers and the cost added per expense line (spare parts, trucks — E-USD-10/12) or received line (raw material) | Prepared by Cuentas por pagar, approved by someone else (four eyes); documents and allocations only while DRAFT; a document and a target line in one settlement only |
| `fin.ap_document` `currency`, `original_amount_fc`, `open_amount_fc`; `doc_type` CUSTOMS_DECLARATION | USD payables; the DGA's payable for a DUA | A USD payable has its USD amounts; its source is an invoice of the same currency or a DUA (owed in pesos) |
| `fin.bank_account.currency` | DOP or USD | — |
| `fin.payment` `amount_fc`, `exchange_rate`; `fin.ap_application.amount_fc` | A USD payment's USD amount and the bank's rate; the USD an application settles | A USD payment has both |
| `fin.gl_entry.amount_fc` | The USD of a line of a USD control | Currency USD only on AP_FOREIGN and BANK lines, with their USD amount to 2 decimals |
| Account roles | AP_FOREIGN (control, AP subledger), IMPORT_CLEARING (control), FX_GAIN, FX_LOSS, FX_UNREALIZED | Seeded unmapped (A-01) |
| Permissions | `exchange_rate:prepare` (TESORERO, CONTADOR), `exchange_rate:approve` (CONTROLLER), `import_settlement:prepare` (CUENTAS_POR_PAGAR), `import_settlement:approve` (CONTROLLER) | 134 permissions; prepare ≠ approve as SoD (47 rules) |

`fin.gl_entry.amount_fc` is not yet in the row hash: the posting engine writes it from USD1-03, and its hash then covers it.

Tests: `UsdImportSchemaTests` (Procurement).

## USD1-02 — exchange rates (migration 0086; E-USD1-02-1…8)

| Command / query | Permission | What it does |
| --- | --- | --- |
| `PrepareExchangeRate` (currency USD, date, rate, source) | `exchange_rate:prepare` | DRAFT; today or a past day (`EXCHANGE_RATE_FUTURE`); positive, 4 decimals, with its source |
| `ApproveExchangeRate` | `exchange_rate:approve` | Someone else than the preparer (SoD and four eyes); supersedes the ACTIVE rate of the same day |
| `DiscardExchangeRate` | `exchange_rate:prepare` | DRAFT → DISCARDED |
| `ListExchangeRates` (GET `/finance/exchange-rates?from=&to=`) | `exchange_rate:read` | The rates of a range (default the last 30 days) |
| `GetExchangeRateForDate` (GET `/finance/exchange-rates/for-date?date=`) | `exchange_rate:read` | The rate a document of that date takes and the day it comes from |

`ExchangeRateBook.ForDateAsync` is what every USD document will call: the ACTIVE rate of its day; on a Saturday or Sunday the last
ACTIVE rate before it; a weekday without its rate refuses with `EXCHANGE_RATE_MISSING` (a weekday holiday is entered by Tesorería with the
previous business day's rate, E-USD1-02-7). `ToPesos` rounds a USD amount at a rate to 2 decimals. Migration 0086: `exchange_rate:read`
for TESORERO, CONTADOR, CONTROLLER, CUENTAS_POR_PAGAR, COMPRADOR, AUDITOR (135 permissions). The Inicio notices of E-USD1-02-6 come with
the screens (USD1-07). 215 commands.

Tests: `ExchangeRateTests` (Finance).

## USD1-03 — foreign supplier orders and invoices (migration 0087; E-USD1-03-1…8)

- **Foreign supplier** (E-USD1-03-9/10): `CreateForeignSupplier` / `UpdateForeignSupplierDraft` — legal name, country (two letters,
  `COUNTRY_INVALID`) and optional foreign tax id, no RNC; unique per country and tax id (`FOREIGN_TAX_ID_DUPLICATE`); activated with
  `ActivateSupplier`. `UpdateSupplier` refuses it (`SUPPLIER_KIND_MISMATCH`). `ListSuppliers` adds `country` / `foreignTaxId`. The ADM
  Cloud import keeps loading local suppliers only. 217 commands.
- **Who**: a supplier of kind FOREIGN buys only expenses in USD (E-USD1-03-1). `CreatePurchaseOrder` and `RegisterSupplierInvoice`
  (inventory) refuse it with `FOREIGN_SUPPLIER_EXPENSES_ONLY`.
- **Order**: `CreateExpensePurchaseOrder` / `UpdateExpensePurchaseOrderDraft` take the currency from the supplier; USD lines have no tax
  type (`EXPENSE_TAX_TYPE_CURRENCY` otherwise; a peso line without one gets the same code). `ApprovePurchaseOrder` compares the
  approval limit and step-up threshold with the peso value at today's rate (`EXCHANGE_RATE_MISSING` without it). `PreviewExpensePurchaseOrder`
  returns no taxes when every line is without tax type.
- **Invoice**: `RegisterExpenseInvoice` for a foreign supplier takes the supplier's own number (1–40 characters,
  `FOREIGN_INVOICE_NUMBER_INVALID`), prices in USD, no tax type, and the rate of its date (`ExchangeRateBook.ForDateAsync`). Lines keep
  `unit_price_fc` / `net_amount_fc`; `net_amount` is the peso net with the rounding cent on the largest line, `unit_price` the USD price at
  the rate to 6 decimals; the header keeps `exchange_rate`, `total_amount_fc` and the peso `total_amount`.
- **Match**: without order, the peso total (no taxes) against `expense_invoice_approval_threshold`; with order, the USD price against the
  order's USD price (percentage tolerance), the amount difference valued in pesos at the invoice's rate.
- **Posting (P-38)**: `P38-DR-EXP` per line to its category's account, `P38-CR-AP` to AP_FOREIGN with `amount_fc` = the USD total. The AP
  document is in USD (`original_amount_fc` / `open_amount_fc`). No tax determination (the posted-evidence check accepts a USD invoice
  without one). Reversal: the exact inverse; the AP document closes in both currencies.
- **Posting engine**: `PostingLineInput.AmountFc` makes a line USD (`currency = 'USD'`, `amount_fc`); `GlEntryRow.AmountFc` enters the row
  hash only when present, so every peso row hashes as before. `PostingEngine.ReadEntry(reader, amountFcOrdinal)` and the audit's group
  reader read it.
- **Reconciliation**: AP-GL and the AP-REC close snapshot compare open AP with AP_CONTROL + AP_FOREIGN.
- **606**: `tax.report_606` leaves out USD invoices (E-USD1-03-7).
- **Categories**: `PrepareExpenseCategory` accepts a non-control ASSET account with 606 type 04; `UpdateExpenseCategoryDraft` keeps such a
  category at 04 (DB guard `pur.expense_category_account_valid`).
- **Queries**: supplier invoice list / detail add `currency`, `totalAmountUsd`, `exchangeRate`, `openAmountUsd` (ITBIS 0 and gross =
  total for USD; the printed total of a USD invoice is in USD), lines `unitPriceUsd` / `netAmountUsd`, AP document currency and USD
  amounts; purchase order list / detail add `currency` (totals in the order's currency).

Tests: `ForeignInvoiceTests` (USD-02, USD-03, rounding, refusals, fixed-asset categories).

## USD1-04 — DUA and import settlement (migration 0088; E-USD1-04-1…9)

| Command / query | Permission | What it does |
| --- | --- | --- |
| `RegisterCustomsDeclaration` | `supplier_invoice:post` | Registers and posts the DUA (P-39) and the DGA's payable (`fin.ap_document` doc_type CUSTOMS_DECLARATION) |
| `ReverseCustomsDeclaration` | `supplier_invoice:reverse` (step-up) | Exact reversal while unpaid and in no live settlement |
| `PrepareImportSettlement` / `UpdateImportSettlementDraft` / `CancelImportSettlement` | `import_settlement:prepare` | DRAFT LI-YYYY-NNNNNN with its documents and computed allocation |
| `ApproveImportSettlement` | `import_settlement:approve` (step-up, four eyes) | Posts P-40 on the settlement date |
| `ReverseImportSettlement` | `import_settlement:approve` (step-up) | Exact reversal; documents are free again |
| `ListCustomsDeclarations` (GET `/procurement/customs-declarations`), `ListImportSettlements`, `GetImportSettlement` | `supplier_invoice:read` | |

- **Documents** (`ImportSettlements.GatherAsync`): `SUPPLIER_INVOICE` = a POSTED foreign expense invoice in USD of the plant (its lines
  receive cost); `EXPENSE_INVOICE` = a POSTED expense invoice of the plant (its peso net is cost); `CUSTOMS_DECLARATION` = a POSTED DUA of the
  plant (duties + other charges). A document is in one DRAFT/POSTED settlement at a time (`pur.import_settlement_document_live_once`
  trigger, the rows locked while gathering); the date is not future nor before its latest document.
- **Allocation**: total cost × line net ÷ Σ goods nets, rounded; the cent left to the largest line. Stored in
  `pur.import_settlement_allocation` (EXPENSE_LINE only, E-USD1-04-8) and recomputed identically at approval.
- **Posting**: P-39 `P39-DR-CLR` (IMPORT_CLEARING, subledger IMPORT = the DUA), `P39-DR-ITBIS`, `P39-CR-AP`; P-40 `P40-DR-COST` per goods
  line to its category account, `P40-CR-CLR` per DUA, `P40-CR-EXP` per expense invoice line to its category account. `IMPORT` is a new
  subledger type; IMPORT_CLEARING ⇔ IMPORT.
- **Treasury**: payment screens read payables through the view `fin.ap_source` (supplier invoice or «DUA <number>»), so the DGA's payable is
  proposed and paid like any other; the proposal leaves out USD payables and a peso payment refuses them (`AP_DOCUMENT_CURRENCY`) until
  USD1-05. PAY-APPL counts DUA payables.
- **Reversal guards**: an expense invoice or DUA in a live settlement is not reversed (`IMPORT_DOCUMENT_IN_SETTLEMENT`).

Tests: `ImportSettlementTests` (USD-05 with the baseline's figures, the settlement rules, the DUA). 224 commands.

## USD1-05a — USD bank accounts and payments of USD payables (migration 0089; E-USD1-05-1…6)

- **Bank accounts**: `RegisterBankAccount` takes `Currency` (DOP default, or USD).
- **Foreign supplier accounts**: `RequestPartyBankAccount` accepts, for a FOREIGN supplier, an account number or IBAN of 5–34 letters and
  digits (stored upper case); the bank code holds the SWIFT/BIC or bank name. The CHECK allows `^[A-Z0-9]{5,34}$`; the trigger
  `md.party_bank_account_number_kind` keeps digits only for local suppliers.
- **Plan** (`PaymentRules.ValidatePlanAsync` → `Plan`): one invoice currency per payment; peso invoices only from a peso account. USD
  invoices: applications in USD (≤ `open_amount_fc`), `amount_fc` = Σ USD; from a USD account the approved rate of the value date
  (`ExchangeRate` must be empty), from a peso account the bank's rate typed in `PrepareSupplierPayment.ExchangeRate` /
  `UpdatePreparedPayment.ExchangeRate` (`PAYMENT_EXCHANGE_RATE_INVALID`); `amount` = USD × rate rounded. `fin.payment.currency` is the bank
  account's; a prepared payment keeps it.
- **Release** (P-41, event `ForeignPaymentReleased`): each application relieves `round(open_amount × USD ÷ open_amount_fc)` pesos (the whole
  `open_amount` when it pays the whole USD balance); `P41-DR-AP` with its USD, `P41-CR-BANK` (with the USD when the account is in USD),
  `P41-DR-FXL` / `P41-CR-FXG` for bank pesos − relieved pesos. `fin.ap_application` keeps pesos and `amount_fc`; the AP document's
  `open_amount` and `open_amount_fc` go down together. Reversal restores both.
- **Checks**: `fin.payment_amount_allocated` and PAY-APPL compare USD for payments with `amount_fc`; PAY-APPL's journal check accepts
  `P41-DR-AP`.
- **Queries**: the payment proposal lists USD payables again with `currency` and `openAmountUsd`; `GetPayment` adds `currency`,
  `amountUsd`, `exchangeRate`.

Tests: `ForeignPaymentTests` (USD-07, partial payments from a USD account with a gain, currency and IBAN rules).

## USD1-05b — transfers between own accounts and USD statements (migration 0090; E-USD1-05b-1…5)

| Command / query | Permission | What it does |
| --- | --- | --- |
| `PrepareBankTransfer` | `payment:prepare` | PREPARED TRF-YYYY-NNNNNN; `Amount` in USD when a USD account takes part, `ExchangeRate` only across currencies |
| `VoidBankTransfer` | `payment:void` | PREPARED → VOIDED with a reason |
| `ReleaseBankTransfer` | `payment:release` (step-up, four eyes) | Posts P-42 on the value date |
| `ReverseBankTransfer` | `payment:reverse` (step-up) | Exact reversal today, reason ≥ 10 characters |
| `MatchBankLineToTransfer` | `bank_line:match` | The origin's DEBIT or the destination's CREDIT line, by that side's amount, within value date … +10 days |
| `ListBankTransfers` (GET `/treasury/bank-transfers`) | `payment:read` | |

- **Amounts** (`fin.bank_transfer`, checked by `fin.bank_transfer_amounts_ok`): DOP→DOP the same pesos, no rate; DOP→USD `from_amount` =
  USD × rate; USD→DOP `to_amount` = USD × rate; USD→USD the same USD and `amount_dop` at the day's approved rate.
- **P-42**: `P42-DR-BANK` to the destination, `P42-CR-BANK` from the origin, both `amount_dop`; a USD account's line carries its USD.
- **Statements**: a payment's line on a USD account matches `amount_fc` (`BankLines.LockPaymentAsync`); `UnmatchBankLine` handles
  transfer lines; `RecognizeBankCharge`, receipt / deposit and refund matching refuse USD accounts (`BankLines.RequirePesoAccountAsync`).
- **BANK-GL**: every BANK amount is `sign(debit − credit) × amount_fc` on USD lines, `debit − credit` on peso lines — a USD account reconciles in
  USD; transfer entries are OUTSTANDING_TRANSFER until their line is matched, and a transfer and its reversal in transit cancel.

Tests: `BankTransferTests` (Reconciliation; USD-09). 229 commands.
