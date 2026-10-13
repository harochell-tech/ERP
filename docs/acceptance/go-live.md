# Go-live — criteria and state (E-PROD-1…13)

Core on the staging server is the system of record since 2026-10-12 (E-PROD-1). The e-CF stays in ADM Cloud until the owner
orders `ECF_MODE=PRODUCTION`; 2026-11-01 is the legal limit. Criteria: baseline v2 §20
(`docs/architecture/baseline/02-architecture-v2-design-review.md`), measured on 2026-10-25 and 2026-10-31 (E-PROD-8). A criterion
not met carries an exception signed by the owner; those marked ★ admit none.

## Switches

| Piece | Since 2026-10-12 | Until Core issues the e-CF |
| --- | --- | --- |
| Outgoing mail | `MAIL_MODE=Live` (E-PROD-13) | Invoices by e-mail wait for the e-CF from Core |
| e-CF gateway | `ECF_MODE=OFF`, no `ECF_SANDBOX_RNC` (E-PROD-11) | Invoices take the manual channel: issued in Core, e-CF in ADM Cloud, its e-NCF recorded in Core |
| Fiscal reports | 606 and IT-1 filed from ADM Cloud (E-PROD-12) | Core's compared at each close |
| Address and badge | `staging.industriasrochell.com.do`, `PARALELO` | `core.industriasrochell.com.do`, no badge (E-PROD-3 / 4), once the DNS record and the OIDC redirect URI exist |

## Open before 2026-11-01

| # | What | Who |
| --- | --- | --- |
| 1 | Close the sandbox e-NCF ranges on Fiscal › Rangos e-NCF (E-PROD-11) | Controller |
| 2 | DNS record `core.industriasrochell.com.do` and the redirect URI in the Google OIDC client (E-PROD-3) | Owner |
| 3 | WORM retention in years and the bucket `industriasrochell-worm-prod` (E-PROD-6) | Owner, accountant |
| 4 | Backup lifecycle 35 daily + 12 monthly in B2, restore test (E-PROD-7) | Owner |
| 5 | Raw material reconciliation, date to be set (E-PROD-9) | Owner |
| 6 | A-01: policy values and account maps (before 2026-10-25) | Controller |
| 7 | Contract test CT-02, 04, 10, 11, 15, 16 and the signed table (N-01) | Owner, Alanube support |
| 8 | Alanube's production token on the server and the production ranges from the first number ADM Cloud did not use (E-VS4-05-5) | Owner, Controller |
| 9 | Evidence bucket and driver PINs (ENT-1) | Owner, Dispatch |
| 10 | Required reviewer on the GitHub Environment from 2026-11-01 (E-PROD-10) | Owner |

## Criteria (v2 §20)

| # | Criterion | Target | 2026-10-25 | 2026-10-31 |
| --- | --- | --- | --- | --- |
| A1 ★ | AR subledger − GL | 0.00 | | |
| A2 ★ | AP subledger − GL | 0.00 | | |
| A3 ★ | Inventory subledger (value) − GL | 0.00 | | |
| A4 ★ | Σ debits − Σ credits, every journal | 0.00 | | |
| A5 | Fixed assets subledger − GL | 0.00 | | |
| A6 ★ | Banks: book balance − statement, net of identified items in transit | 0.00 | | |
| A7 ★ | Events without a posting rule | 0 | | |
| A8 ★ | Posted documents that can be modified | 0 | | |
| A9 ★ | Hash chain verification of every ledger | 100 % valid | | |
| A10 | Month's sales, ADM Cloud against Core (E-PROD-12) | ≤ 0.1 %, all explained | | |
| B1 ★ | Σ quantity entries − stock balance per item × lot × location | 0 | | |
| B2 | Quantity in the system against the opening physical count | Equal | | |
| B3 ★ | Items with quantity 0 and a value | 0 | | |
| B4 ★ | Inventory lots without a cost | 0 | | |
| B5 ★ | Materials with more than one active issue mode | 0 | | |
| B6 | Racks of the two-week trial against the HMI / manual count | ≤ 1 %, explained | | |
| B7 | Theoretical silo against measured | Within tolerance | | |
| B8 ★ | Finished goods issues without a lot | 0 | | |
| C1 ★ | Test e-CF of every type in use | 100 % accepted | | |
| C2 ★ | e-CF sent or in a valid contingency | 100 % | | |
| C3 ★ | Duplicate e-NCF or unexplained gaps | 0 | | |
| C4 | 606 of the month against ADM Cloud's | 0 or all explained | | |
| C5 ★ | Sales ITBIS: e-CF against GL | 0.00 | | |
| C6 ★ | Fiscal authorizations in force loaded and verified | 100 % | | |
| C7 ★ | Active fiscal rules with their official source | 100 % | | |
| C8 | Active customers with a validated RNC / cédula | ≥ 98 % | | |
| D1 ★ | Critical users trained, with a practical test | 100 % | | |
| D2 | Order + delivery note + invoice by a trained user | ≤ 3 min | | |
| D3 | Recording a shift summary | ≤ 10 min | | |
| D4 | POD recorded within 24 h | ≥ 98 % | | |
| D5 | Common operations (p95) | < 2 s | | |
| D6 | Close reports | < 60 s | | |
| D7 ★ | Printing of delivery note, invoice, order and quote at each site | 100 % tested | | |
| D8 ★ | Open data-quality findings of level ERROR | 0 | | |
| E1 ★ | Full restore to an isolated environment | Passed within 30 days | | |
| E2 ★ | Measured RPO | ≤ 5 min | | |
| E3 | Measured RTO | ≤ 4 h | | |
| E4 ★ | MFA on financial and administrator roles | 100 % | | |
| E5 ★ | SoD conflicts without an approved exception | 0 | | |
| E6 | External pentest: critical / high findings open | 0 | | |
| E7 ★ | Runbooks (e-CF contingency, no Internet at the plant, restore) tested | 3 of 3 | | |
| E8 | On-site support on day 0 and +1 | Assigned | | |
| E9 ★ | Migration sign-off and opening balance certification signed | Yes | | |
