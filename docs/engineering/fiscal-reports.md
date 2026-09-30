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
