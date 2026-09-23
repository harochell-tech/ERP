## PR

- PR id (baseline §17 / Patch 1 §5.4): PR-__
- Baseline sections and ADRs implemented:

## Definition of Done (baseline §17, common)

- [ ] Listed tests of this PR and **all previous PRs** green in CI (PostgreSQL 17)
- [ ] Exactly one forward-only migration (if schema changes), numbered, never editing merged ones
- [ ] Reviewed by a second person (ledger reviewer mandatory for PR-02, PR-05, PR-07, PR-09…PR-16)
- [ ] Architecture tests green (module boundaries, no float/double, no UPDATE/DELETE on ledgers)
- [ ] No functionality outside the frozen baseline (explicit check)
- [ ] Any contradiction or ambiguity reported as a proposed erratum, not decided in code
