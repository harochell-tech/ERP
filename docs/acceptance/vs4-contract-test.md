# VS#4 — Contract test with Alanube (CT-01…16, N-01)

Baseline: v2.1 §5.1 (`docs/architecture/baseline/03-architecture-v2.1-build-readiness.md`), E-VS4-13, E-VS4-04-8. Run in Alanube's
**sandbox** before Production; the owner signs this table (N-01). The automatable cases come from
`rochell-migrate ecf-contract-test` (runbook: `docs/engineering/staging.md` › e-CF gateway); each answer below cites the report line.

Run: 2026-10-09, 21:32–21:35 UTC · report `/opt/rochell-staging/ct/report.jsonl` on the staging server (not committed: it holds short-lived signed file links) · sandbox ranges used: E31 0009820001 to 0009820054, E34 0009820901 · sandbox issuer RNC 132109122 (Alanube's test company for this account), buyer 131925332, sequence due 2028-12-31.

The tool runs on the `edge` network (`docker run --network rochell-staging_edge`, the API's image, entrypoint `rochell-migrate`): the `migrate` service sits on the internal network, without internet.

Still to answer with Alanube support: CT-02, CT-04, CT-10, CT-11, CT-15, CT-16; and repeat CT-05 with a timeout after sending.

| ID | Question | How | Result (Alanube's answer) | Consequence for Core |
| --- | --- | --- | --- | --- |
| CT-01 | Who assigns the e-NCF? | Automatic: 31 without / with `idDoc.encf` | Without `idDoc.encf`: 400 `instance.idDoc requires property "encf"`. With E310009820001: 201 REGISTERED | Core assigns (CORE_MANAGED, E-VS4-1) — **confirmed** |
| CT-02 | Can an e-NCF be reserved before sending? | Docs + Alanube support | | |
| CT-03 | A validation error — is the number consumed? | Automatic: invalid 31, then a valid one with the same number | A 31 without totals: 400 `instance requires property "totals"`; the same E310009820002 sent valid right after: 201 | A request refused by validation does not consume the number |
| CT-04 | After signing, before sending to the DGII | Alanube support (signing is theirs) | | |
| CT-05 | Client timeout after sending | Automatic: 1 ms timeout, then the same e-NCF again | The 1 ms timeout cancelled before the request left; the same E310009820003 sent again: 201. Inconclusive: repeat with a timeout once the request has left | AP3011 with the id → adopted (E-VS4-4) — confirmed by CT-06 |
| CT-06 | Idempotency | Automatic: the same document twice | 400 `AP3011 ENCF document is in process with id: …` naming the first document's id | Adopt that id (E-VS4-4) — **confirmed** |
| CT-07 | Retry with a change | Automatic: the same e-NCF with a changed item name | The same AP3011 with the first id: a number never carries a second content | A changed document needs a new e-NCF |
| CT-08 | DGII rejection | Automatic: ITBIS that does not match, followed to its answer | E310009820004 with a wrong ITBIS: 201, then FINISHED with **ACCEPTED_WITH_OBSERVATIONS**, not rejected; `sequenceConsumed: true` | The DGII sandbox does not check the ITBIS arithmetic: Core's own calculation is the control. A real rejection still to be seen |
| CT-09 | Conditional acceptance | A case that gives it (Alanube support) | Seen in CT-08: `status: FINISHED`, `legalStatus: ACCEPTED_WITH_OBSERVATIONS` | Mapped to ACCEPTED_CONDITIONAL (`EcfProcessing`) — **confirmed** |
| CT-10 | Annulment | `POST /cancellations` of an unused range | | E-VS4-12 |
| CT-11 | Contingency | Ask Alanube; the DGII sandbox outage, if any | | E-VS4-02-4 |
| CT-12 | Status query | Automatic: the first document until FINISHED | REGISTERED → TO_SEND (with `signatureDate`, `securityCode`, `documentStampUrl` at `ecf.dgii.gov.do/testecf/`) → FINISHED / ACCEPTED within about 20 s | Statuses seen: REGISTERED, TO_SEND, FINISHED; legal ACCEPTED, ACCEPTED_WITH_OBSERVATIONS |
| CT-13 | Credit note citing the original | Automatic: 34 citing CT-01's e-CF | E340009820901 citing E310009820001: 201, then FINISHED / ACCEPTED | The 34 works; the sandbox took a number of the same range under type 34 |
| CT-14 | Limits | Automatic: burst of 50 | 50 invoices one after the other (E310009820005…054) in about one minute: all 201 | Rate limit / codes: none seen at about 1 request per second |
| CT-15 | Security | Token issue, expiry and rotation, allowed IPs (Alanube support) | | |
| CT-16 | Webhooks | Panel configuration, payload, retries | | E-VS4-02-5 |

Sequence authority decided: CORE_MANAGED (E-VS4-1) ☐ confirmed ☐ changed to ___

Signed: Alexander Rochell ___ · date ___
