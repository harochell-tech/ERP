# General ledger: adjustments, trial balance and statements (FIN-1)

Frozen Baseline `docs/architecture/fin1/frozen-baseline-fin1.md`, approved errata E-FIN1-1…10 and E-FIN1-01-1…7.

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
