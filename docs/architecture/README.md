# Architecture — source of truth

Exported from the Claude Docs where the architecture was written and approved by Alexander Rochell. The Claude Docs remain the
editable originals; these Markdown copies are what the code is built against. If a copy and its Doc ever differ, stop and ask.

## Precedence (highest first)

| # | Document | Role |
| --- | --- | --- |
| 1 | [`errata.md`](errata.md) | Implementation errata E-PR02-1 … E-VS2-10, each approved by Alexander, plus the derived implementation rules. Overrides everything below for the point it covers. |
| 1b | [`vs2/frozen-baseline-vs2.md`](vs2/frozen-baseline-vs2.md) | **Frozen Baseline of Vertical Slice #2** (supplier payments and banks), approved 2026-09-25 with E-VS2-1…10. Builds on VS#1; errata override it where they apply. |
| 1c | [`vs3/frozen-baseline-vs3.md`](vs3/frozen-baseline-vs3.md) | **Frozen Baseline of Vertical Slice #3** (sales, dispatch, collections), approved 2026-09-27 with E-VS3-1…16. |
| 1d | [`fin1/frozen-baseline-fin1.md`](fin1/frozen-baseline-fin1.md) | **Frozen Baseline of FIN-1** (adjustment journal, trial balance, financial statements), approved 2026-09-27 with E-FIN1-1…10. |
| 1e | [`mfg1/frozen-baseline-mfg1.md`](mfg1/frozen-baseline-mfg1.md) | **Baseline of Manufacturing #1** (production, curing, release, standard cost), approved 2026-09-29 with E-MFG1-1…18. |
| 1f | [`fis1/frozen-baseline-fis1.md`](fis1/frozen-baseline-fis1.md) | **Baseline of Fiscal #1** (CONFOTUR fiscal authorizations, e-CF 44 exempt sales), approved 2026-09-29 with E-FIS1-1…16. |
| 1g | [`quo1/frozen-baseline-quo1.md`](quo1/frozen-baseline-quo1.md) | **Baseline of Quotations #1** (sales quotes, special-price approval, conversion into a sales order), approved 2026-09-29 with E-QUO1-1…14. |
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
