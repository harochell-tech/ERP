# Fiscal reports (FIS-2)

Frozen baseline: `docs/architecture/fis2/frozen-baseline-fis2.md` (E-FIS2-1…14). A fully electronic issuer files no 607 / 608
(DGII); the system prepares the 606 and informative IT-1 / IR-17 summaries.

## FIS2-01 — configuration (migration 0058, E-FIS2-01-1…10)

- **606 classification** — fiscal rule kind `REPORT_606_CLASSIFICATION`, definition `{"classes": {category: "01"…"11"}}` covering
  the four raw-material categories (CEMENTO, AGREGADO, ADITIVO, OTRA_MATERIA_PRIMA). It follows the rules' lifecycle (Analista
  configures, Especialista activates with step-up, versioned, official source; the 606 instructivo of 2026-02-12 is the source,
  registered with the existing command) but is READY with its source alone: test runs are refused
  (`FISCAL_RULE_TESTS_NOT_APPLICABLE`) and the activation gate (`tax.fiscal_rule_version_gate`, replaced in 0058) skips the
  test-run condition for this kind. One classification is active at a time (`ANOTHER_ITBIS_RULE_ACTIVE`). The Tax Engine skips it:
  it neither applies nor closes the purchase gate.
- **ISR withholding type** — optional key `isr_withholding_type` ("1"…"9", the 606's codes) on PURCHASE_WITHHOLDING definitions;
  without it the 606 leaves the type blank and TAX-606 warns (FIS2-02).
- **Permission** `fiscal_report:read` (READ): ESPECIALISTA_FISCAL, ANALISTA_FISCAL, CONTADOR, CONTROLLER, AUDITOR, DIRECTOR.
- Dev seed: an active classification with every raw material as "09".

Tests: `FiscalGateTests` (invalid definitions, the classification's lifecycle, the ISR type).

## FIS2-02 — the 606, the IT-1 / IR-17 summaries and TAX-606 (migration 0059, E-FIS2-02-1…12)

`tax.report_606(company, day)` (read-only SQL function, used by the query and TAX-606) returns the month's records:

| Record | Which invoices | Payment date, withholdings, method | ITBIS |
| --- | --- | --- | --- |
| `NCF` | Posted, not reversed supplier invoices with the NCF date in the month | Only if settled within the month (the last live application's value date); otherwise blank, no withholdings, method 4 | Invoiced, to cost (NON_RECOVERABLE_INPUT), to advance |
| `PAYMENT` | Invoices of an earlier month with withholdings, settled within this month | The payment date, the withholdings, method 2 | 0 (advanced in the NCF month) |

A withholding on the ITBIS base is ITBIS withheld; on the NET base it is ISR, with the rule's `isr_withholding_type`. Services,
proportionality, perceived taxes, selective, other taxes and tip are 0 (all purchases in the system are inventory goods).

| Endpoint (`fiscal_report:read`) | Returns |
| --- | --- |
| `GET /tax/reports/606?period=AAAAMM` | Header (company RNC, period, record count, total) and records; `&format=csv` → the rows in the DGII Excel tool's column order (fields 1–23), no header row, AAAAMMDD dates, zero amounts empty, UTF-8 without BOM, to paste into the official tool that validates and writes the TXT |
| `GET /tax/reports/it1-summary?period=` | Sales by e-CF type (taxed net = lines with an OUTPUT tax line, exempt otherwise, ITBIS), credit notes, the 606's purchase ITBIS, customer withholdings |
| `GET /tax/reports/ir17-summary?period=` | Withholdings to suppliers in the month's 606 by tax and ISR type: records, base, amount |

TAX-606 (WARNING, blocks nothing): `TAX606_ITBIS_DIFFERENCE` (the NCF records' ITBIS to advance vs ITBIS_RECOVERABLE posted in the
cutoff's month), `CLASSIFICATION_MISSING`, `ISR_WITHHOLDING_TYPE_MISSING`. Tests: `Report606Tests` (F2-01, F2-02, F2-03, F2-05,
F2-06), `It1SummaryTests` (F2-04).

## FIS2-03 — screens and end to end (E-FIS2-03-1…8)

Fiscal › Reportes fiscales (606 with the CSV for the DGII tool and the filing steps, IT-1 and IR-17 tabs) and the classification on
Fiscal › Reglas fiscales: `web.md`. Empty sums read "0.00" (the report's total and the IR-17 totals). E2E-F2: `AcceptanceTests`
over the API and `web/e2e/fiscal-reports-journey.spec.ts`; acceptance matrix `docs/acceptance/fis2.md`
(`AcceptanceFis2TraceabilityTests`).
