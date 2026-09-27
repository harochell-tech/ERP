# General ledger: adjustments, trial balance and statements (FIN-1)

Frozen Baseline `docs/architecture/fin1/frozen-baseline-fin1.md`, approved errata E-FIN1-1…10, E-FIN1-01-1…7, E-FIN1-02-1…4 and E-FIN1-03-1…11. Acceptance matrix: `docs/acceptance/fin1.md`.

## Schema (FIN1-01)

Migration `0033__ledger_adjustments_schema.sql`.

| Object | What it holds | Guarantees |
| --- | --- | --- |
| `fin.account` (extended) | `account_class` (ASSET, LIABILITY, EQUITY, REVENUE, COST, EXPENSE; nullable for existing accounts, E-FIN1-01-1) and `status` (ACTIVE, INACTIVE) | Code and control flag immutable; never deleted; an account with a balance cannot be made INACTIVE; the application may insert and edit name, class and status (`account:manage`, FIN1-02) |
| `fin.manual_journal` | Adjustment `AJ-000001` (E-FIN1-01-7): posting date, description, support reference and SHA-256 (E-FIN1-7), close component ACR-NTX / ACR-TAX (E-FIN1-6), auto-reverse flag, status DRAFT → PENDING_APPROVAL → POSTED / REJECTED (or back to DRAFT); POSTED → REVERSED | Preparer ≠ approver and ≠ rejecter (CHECK); only a DRAFT changes; at COMMIT a POSTED/REVERSED adjustment has its live MANUAL_ADJUSTMENT journal (K-25) and, when auto-reversing or REVERSED, its reversal; a PENDING_APPROVAL adjustment has at least two balanced lines; ADR-027 state history |
| `fin.manual_journal_line` | Lines per version of a DRAFT adjustment (debit or credit, 2 decimals, optional plant and party, E-FIN1-01-4) | Only active, non-control accounts (E-FIN1-2); append-only |
| `fin.gl_journal` (extended) | Type MANUAL_ADJUSTMENT without posting rule (E-FIN1-01-2); a reversal of one also has no rule | Lines of a rule-less journal carry role MANUAL_ADJUSTMENT and code P-34, and only they do (so a control account is never adjusted by hand); the close gate takes the component from the adjustment |
| `fin.account_role` | Technical role MANUAL_ADJUSTMENT (not control) | Never mapped (trigger on `account_role_map`) |
| `fin.close_component_state` | Components ACR-NTX and ACR-TAX, OPEN in every period (E-FIN1-01-3); `open-periods` opens them for new years | — |
| `fin.report_structure_version`, `fin.report_line`, `fin.report_line_account` | Versioned balance-sheet and income-statement lines (E-FIN1-5) | Preparer ≠ approver; one ACTIVE per report; lines only while DRAFT; each account at most once per structure |

Role **CONTADOR** (`manual_journal:prepare`, `ledger:read`); permissions `account:manage` and `manual_journal:approve`
(Controller), `report_structure:approve` (Aprobador de políticas), `ledger:read` (Contador, Controller, Auditor, Director); SoD
`manual_journal:approve` ≠ `manual_journal:prepare` (E-FIN1-01-5; 66 permissions, 25 SoD rules).

Tests: `LedgerSchemaTests`.

## Adjustment journal and editable chart (FIN1-02)

Migration `0034__ledger_adjustments.sql`: reconciliations MANUAL-EVIDENCE and TB-BALANCED (blocking ACR-NTX and ACR-TAX), and
0033's close gate for rule-less journals re-created (its variable shared the column's name: 42702).

| Command (`/finance/…`) | Permission | Effect |
| --- | --- | --- |
| `create-account`, `update-account` | `account:manage` | Code (1–20 chars, unique), name, class; code and control flag never change |
| `deactivate-account`, `activate-account` | `account:manage` | ACTIVE ⇄ INACTIVE; INACTIVE refused while Σ(debit − credit) ≠ 0 (E-FIN1-7, GL-07) |
| `prepare-manual-journal`, `update-manual-journal` | `manual_journal:prepare` | DRAFT `AJ-nnnnnn` (numbered under an advisory lock per company); every update writes a new line version |
| `submit-manual-journal`, `withdraw-manual-journal` | `manual_journal:prepare` | DRAFT ⇄ PENDING_APPROVAL; submit needs debits = credits and the ACR component open for the date |
| `approve-manual-journal` | `manual_journal:approve` + step-up | POSTED: journal MANUAL_ADJUSTMENT (lines P-34, role MANUAL_ADJUSTMENT); when auto-reversing, the exact reversal dated the 1st of the next month in the same transaction (E-FIN1-01-6, GL-04) |
| `reject-manual-journal` | `manual_journal:approve` | REJECTED with a reason |
| `reverse-manual-journal` | `manual_journal:approve` + step-up | REVERSED: exact reversal on today's business date, with a reason (not for auto-reversing ones) |

Validation: description 1–500 characters; support reference plus SHA-256 as 64 hex characters; component ACR-NTX or ACR-TAX;
posting date not in the future and inside an open period of its component (`PERIOD_CLOSED` otherwise — adjustments never move to
a later period the way late document postings do); at least two lines, each with exactly one positive side and at most 2
decimals, to active non-control accounts. Approver and rejecter differ from the preparer (SoD in the roles, CHECK in the table).

Queries (`ledger:read`): `GET /finance/manual-journals` (status filter, paging, total per adjustment) and
`GET /finance/manual-journals/{id}` (lines of the current version, state history). `GET /finance/accounts` now returns
`accountClass` and `status`. Explain this entry describes a P-34 line with the adjustment number, description, support and who
prepared and approved it.

Reconciliations: **MANUAL-EVIDENCE** — every POSTED / REVERSED adjustment has its MANUAL_ADJUSTMENT journal (and its reversal),
and no rule-less journal exists outside an adjustment; **TB-BALANCED** — debits = credits per journal and in total. Closing
ACR-NTX / ACR-TAX snapshots the balances of the accounts its adjustments moved in the period.

CLI: `import-accounts` accepts an optional fifth column with the class (E-FIN1-01-1).

Tests: `ManualJournalTests` (GL-01…04, GL-07, reversal, closed period), `AdjustmentCloseTests` (ACR-NTX close).

## Trial balance, ledger and statements (FIN1-03)

Migration `0035__financial_statements.sql`: reconciliation STRUCT-COVERAGE (WARNING, blocks nothing; E-FIN1-03-9).

| Endpoint (`/finance/…`) | Permission | Content |
| --- | --- | --- |
| `POST prepare-report-structure` | `account:manage` | New DRAFT version of BALANCE_SHEET (ASSET, LIABILITY, EQUITY accounts) or INCOME_STATEMENT (REVENUE, COST, EXPENSE) with all its lines: unique codes, existing parents without cycles, sign ±1, each account once and of the report's classes |
| `POST approve-report-structure` | `report_structure:approve` + step-up | DRAFT → ACTIVE, previous ACTIVE → SUPERSEDED; refused (`STRUCTURE_INCOMPLETE`) while an active account of its classes is on no line |
| `GET report-structures[/{id}]` | `configuration:read` | Versions; lines with their accounts and the active accounts still missing |
| `GET trial-balance?from&to[&plantId&partyId&bankAccountId]` | `ledger:read` | Per account: opening, debits, credits, closing; income accounts open on January 1 of `from`'s year and earlier results sit on the row "Resultados de ejercicios anteriores"; `balanced` when unfiltered totals give debits = credits and Σ opening = Σ closing = 0 |
| `GET accounts/{id}/ledger?from&to[&limit&offset]` | `ledger:read` | Opening, movements (document kind and number, rule line, running balance), closing |
| `GET balance-sheet?asOf` | `ledger:read` | Lines of the ACTIVE structure (sign × Σ(debit − credit) of the line and its descendants), accounts the structure misses, totals, result of the year and of prior years, `difference` = 0.00 |
| `GET income-statement?from&to` | `ledger:read` | Lines, revenue, cost, expenses, net income |

The four reports take `?format=csv` (`LedgerCsv`: UTF-8 with BOM, comma, point decimal, Spanish headers). Statements refuse
with `ACCOUNT_CLASS_MISSING` while an active account has no class and with `STRUCTURE_MISSING` without an approved structure.

Tests: `StatementTests` (GL-06), `TrialBalanceTests` (GL-05), `LedgerReportApiTests`, `AcceptanceFin1TraceabilityTests`.
