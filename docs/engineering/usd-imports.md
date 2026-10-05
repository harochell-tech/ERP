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
