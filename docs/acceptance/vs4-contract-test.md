# VS#4 — Contract test with Alanube (CT-01…16, N-01)

Baseline: v2.1 §5.1 (`docs/architecture/baseline/03-architecture-v2.1-build-readiness.md`), E-VS4-13, E-VS4-04-8. Run in Alanube's
**sandbox** before Production; the owner signs this table (N-01). The automatable cases come from
`rochell-migrate ecf-contract-test` (runbook: `docs/engineering/staging.md` › e-CF gateway); each answer below cites the report line.

Run: date ___ · report file ___ · sandbox ranges used: E31 ___ to ___, E34 ___

| ID | Question | How | Result (Alanube's answer) | Consequence for Core |
| --- | --- | --- | --- | --- |
| CT-01 | Who assigns the e-NCF? | Automatic: 31 without / with `idDoc.encf` | | Core assigns (CORE_MANAGED, E-VS4-1) — confirm |
| CT-02 | Can an e-NCF be reserved before sending? | Docs + Alanube support | | |
| CT-03 | A validation error — is the number consumed? | Automatic: invalid 31, then a valid one with the same number | | |
| CT-04 | After signing, before sending to the DGII | Alanube support (signing is theirs) | | |
| CT-05 | Client timeout after sending | Automatic: 1 ms timeout, then the same e-NCF again | | AP3011 with the id → adopted (E-VS4-4) — confirm |
| CT-06 | Idempotency | Automatic: the same document twice | | |
| CT-07 | Retry with a change | Automatic: the same e-NCF with a changed item name | | |
| CT-08 | DGII rejection | Automatic: ITBIS that does not match, followed to its answer | | The e-NCF is consumed (E-VS4-6) — confirm |
| CT-09 | Conditional acceptance | A case that gives it (Alanube support) | | ACCEPTED_CONDITIONAL fields |
| CT-10 | Annulment | `POST /cancellations` of an unused range | | E-VS4-12 |
| CT-11 | Contingency | Ask Alanube; the DGII sandbox outage, if any | | E-VS4-02-4 |
| CT-12 | Status query | Automatic: the first document until FINISHED | | Statuses seen: ___ |
| CT-13 | Credit note citing the original | Automatic: 34 citing CT-01's e-CF | | |
| CT-14 | Limits | Automatic: burst of 50 | | Rate limit / codes: ___ |
| CT-15 | Security | Token issue, expiry and rotation, allowed IPs (Alanube support) | | |
| CT-16 | Webhooks | Panel configuration, payload, retries | | E-VS4-02-5 |

Sequence authority decided: CORE_MANAGED (E-VS4-1) ☐ confirmed ☐ changed to ___

Signed: Alexander Rochell ___ · date ___
