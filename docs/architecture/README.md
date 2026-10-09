# Architecture — source of truth

Exported from the Claude Docs where the architecture was written and approved by Alexander Rochell. The Claude Docs remain the
editable originals; these Markdown copies are what the code is built against. If a copy and its Doc ever differ, stop and ask.

## Precedence (highest first)

| # | Document | Role |
| --- | --- | --- |
| 1 | [`errata.md`](errata.md) | Implementation errata E-PR02-1 … E-LAB1-10, each approved by Alexander, plus the derived implementation rules. Overrides everything below for the point it covers. |
| 1b | [`vs2/frozen-baseline-vs2.md`](vs2/frozen-baseline-vs2.md) | **Frozen Baseline of Vertical Slice #2** (supplier payments and banks), approved 2026-09-25 with E-VS2-1…10. Builds on VS#1; errata override it where they apply. |
| 1c | [`vs3/frozen-baseline-vs3.md`](vs3/frozen-baseline-vs3.md) | **Frozen Baseline of Vertical Slice #3** (sales, dispatch, collections), approved 2026-09-27 with E-VS3-1…16. |
| 1d | [`fin1/frozen-baseline-fin1.md`](fin1/frozen-baseline-fin1.md) | **Frozen Baseline of FIN-1** (adjustment journal, trial balance, financial statements), approved 2026-09-27 with E-FIN1-1…10. |
| 1e | [`mfg1/frozen-baseline-mfg1.md`](mfg1/frozen-baseline-mfg1.md) | **Baseline of Manufacturing #1** (production, curing, release, standard cost), approved 2026-09-29 with E-MFG1-1…18. |
| 1f | [`fis1/frozen-baseline-fis1.md`](fis1/frozen-baseline-fis1.md) | **Baseline of Fiscal #1** (CONFOTUR fiscal authorizations, e-CF 44 exempt sales), approved 2026-09-29 with E-FIS1-1…16. |
| 1f-b | [`fis1/frozen-baseline-fis1b.md`](fis1/frozen-baseline-fis1b.md) | **Amendment to Fiscal #1**: the proforma as a collection document (one per delivery, receipts allocated before the e-CF, invoice from proformas, customer refund), approved 2026-10-01 with E-FIS1b-1…11 and E-FIS1b-01-1…14. |
| 1f-c | [`cf1/frozen-baseline-cf1.md`](cf1/frozen-baseline-cf1.md) | **Baseline of CF-1** (cash sales to final consumers, e-CF 32: paid in full before dispatch), approved 2026-10-02 with E-CF1-1…14 and E-CF1-01-1…9. |
| 1f-d | [`gas1/frozen-baseline-gas1.md`](gas1/frozen-baseline-gas1.md) | **Baseline of GAS-1** (purchases of expenses and services: expense lines without a registered item, expense categories, tax type per line, P-37), approved 2026-10-02 (E-GAS-1…12). |
| 1g | [`quo1/frozen-baseline-quo1.md`](quo1/frozen-baseline-quo1.md) | **Baseline of Quotations #1** (sales quotes, special-price approval, conversion into a sales order), approved 2026-09-29 with E-QUO1-1…14. |
| 1h | [`fis2/frozen-baseline-fis2.md`](fis2/frozen-baseline-fis2.md) | **Baseline of Fiscal #2** (report 606, IT-1 / IR-17 summaries, TAX-606; no 607 / 608 for a fully electronic issuer), approved 2026-09-29 with E-FIS2-1…14. |
| 1i | [`ent1/frozen-baseline-ent1.md`](ent1/frozen-baseline-ent1.md) | **Baseline of ENT-1** (driver confirms the delivery from the delivery note's QR with a PIN and a photo; POD from Core, ADM Cloud retired), approved 2026-10-08 with E-ENT-1…8. |
| 1j | [`mfg3/frozen-baseline-mfg3.md`](mfg3/frozen-baseline-mfg3.md) | **Baseline of MFG-3** (prefilled daily report, OEE per machine and shift, preventive maintenance by cycles / hours / days), approved 2026-10-08 with E-MFG3-1…11. |
| 1k | [`prt1/frozen-baseline-prt1.md`](prt1/frozen-baseline-prt1.md) | **Baseline of PRT-1** (one editable print format per document for screen, PDF and reprint; logo, columns, paper; mandatory fiscal content checked), approved 2026-10-08 with E-PRT-1…10. |
| 1l | [`lab1/frozen-baseline-lab1.md`](lab1/frozen-baseline-lab1.md) | **Baseline of LAB-1** (quality lab: compression and absorption tests per lot, 28-day evaluation, final release, recall, certificate), approved 2026-10-08 with E-LAB1-1…10. |
| 1m | [`ocr1/frozen-baseline-ocr1.md`](ocr1/frozen-baseline-ocr1.md) | **Baseline of OCR-1** (supplier invoices prepared from received e-CF, the printed e-CF's QR or a photo read by AI; commercial response to the DGII), approved 2026-10-09 with E-OCR-1…8. |
| 2 | [`baseline/06-frozen-baseline-patch-1.1.md`](baseline/06-frozen-baseline-patch-1.1.md) | Patch 1.1: prices > 0, STOCK_COVERAGE naming, deployment environment. |
| 3 | [`baseline/05-frozen-baseline-patch-1.md`](baseline/05-frozen-baseline-patch-1.md) | Patch 1 (P-1 … P-8): posting prerequisites roll back, exact reversals (R-02/R-07 A/B), schema and tests replaced. |
| 4 | [`baseline/04-architecture-v2.1.1-frozen-baseline.md`](baseline/04-architecture-v2.1.1-frozen-baseline.md) | **Frozen Baseline of Vertical Slice #1** (§8 schema … §17 PR plan) and errata E-1 … E-12. |
| 5 | [`baseline/03-architecture-v2.1-build-readiness.md`](baseline/03-architecture-v2.1-build-readiness.md) | v2.1 and Build Readiness Package (ADRs, whole-ERP scope). |
| 6 | [`baseline/02-architecture-v2-design-review.md`](baseline/02-architecture-v2-design-review.md) | v2 design review. |
| 7 | [`baseline/01-architecture-v1.md`](baseline/01-architecture-v1.md) | v1 (deliverable 1). Historical context only. |

## Freezing rule (Frozen Baseline §1)

The Frozen Baseline of VS#1 is the only valid specification for the slice. Any change needs (1) a new ADR or a numbered
errata, (2) Alexander's approval, (3) the affected acceptance tests updated **before** the code. Anyone — human or AI — who finds
an ambiguity stops and asks; ambiguities are never resolved by implementing.
