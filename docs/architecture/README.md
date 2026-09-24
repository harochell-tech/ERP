# Architecture — source of truth

Exported from the Claude Docs where the architecture was written and approved by Alexander Rochell. The Claude Docs remain the
editable originals; these Markdown copies are what the code is built against. If a copy and its Doc ever differ, stop and ask.

## Precedence (highest first)

| # | Document | Role |
| --- | --- | --- |
| 1 | [`errata.md`](errata.md) | Implementation errata E-PR02-1 … E-PR14-5, each approved by Alexander, plus the derived implementation rules. Overrides everything below for the point it covers. |
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
