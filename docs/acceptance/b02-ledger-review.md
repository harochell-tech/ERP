# B-02 — Ledger review guide (Vertical Slice #1)

The Frozen Baseline makes a second-person review of the ledger code a condition of every ledger PR (§17, Definition of Done
item 3) and of the slice itself (§15.7: "100% verde y revisión de código de todo el módulo de ledgers aprobada por una segunda
persona"). The code was written with an AI assistant; each merge was approved by Alexander Rochell (product owner) on green CI.
**No independent engineer has reviewed it yet.** This guide is for that engineer. The slice is accepted when every area below is signed off
(`docs/acceptance/vs1.md`, E-PR19-8).

The review is about correctness of money and quantities, not style: can any sequence of commands, concurrent or not, leave the
ledgers inconsistent, lose or double a movement, post to the wrong account or period, or let a user do what the permission
matrix forbids?

## 1. Setup (about 30 minutes)

Prerequisites: .NET SDK 10, Docker (Testcontainers pulls `postgres:17.6-alpine`), optionally Node 24 for the UI.

```bash
git clone https://github.com/harochell-tech/ERP.git && cd ERP
dotnet build Rochell.slnx -c Release          # warnings are errors
dotnet test Rochell.slnx -c Release --no-build # ~536 tests, ~3 min; each test gets its own migrated database
```

To poke at a live system: `dotnet run --project tests/Rochell.DevStack -c Release -- --web-root web/out` (after
`cd web && npm ci && npm run build`) starts PostgreSQL, seeds a company with one user per role and serves the UI and API on
`http://localhost:5080` (Chrome/Firefox). The log prints the database; connect as the deployment role to inspect.

Read first, in this order (one hour): `docs/architecture/baseline/04-architecture-v2.1.1-frozen-baseline.md` §8–§13 (schema,
state machines, transactions, posting rules), `05-frozen-baseline-patch-1.md` P-1…P-8 and §9, `06-…-patch-1.1.md`, then
`docs/architecture/errata.md` (approved deviations; they override the baseline where they apply).

## 2. The transaction template

Every command runs in **one** database transaction, as the application role `rochell_app` (column-level grants, row-level
security by company):

| Step | Where |
| --- | --- |
| Ids pre-assigned, BEGIN, tenant settings for RLS | `src/Rochell.Platform/Commands/CommandPipeline.cs` (`ExecuteOnceAsync`) |
| Authorization (session, permission, plant scope, step-up) before any write | `src/Rochell.Identity/Authorization/SqlCommandAuthorizer.cs` |
| `core.command_log` row (idempotency; a duplicate waits on the unique index, then returns the stored result) | `CommandPipeline.cs`, guards in `db/migrations/0003__core_platform.sql` |
| Handler: events, documents, ledgers | module handlers (section 3) |
| Outbox, single UPDATE of the result, COMMIT (deferred checks fire here) | `CommandPipeline.cs` |
| Retry of the whole command on 40001 / 40P01 (3 times, same key) | `CommandPipeline.ExecuteAsync` |

Things to confirm: nothing is written before authorization; a failure anywhere rolls back everything including
`command_log` (AT-05, ID-04); a retried command cannot double-post (ID-01, ID-02).

## 3. Reading map

| Area | Code | Database guarantees |
| --- | --- | --- |
| Posting Engine | `src/Rochell.Finance/Posting/PostingEngine.cs` (`PrepareAsync` → `WriteAsync`; `PrepareReversalAsync` → `WriteReversalAsync`), `RuleDefinition.cs` | `0006__finance_core.sql`: `gl_entry_before_insert`, `gl_journal_balanced` (deferred), append-only triggers, versioned-config guards |
| Inventory ledger | `src/Rochell.Inventory/InventoryLedger.cs` (`ReceiveAsync`, `ReserveIssueAsync`/`WriteIssueAsync`, `PostMovementAsync`, `ReverseReceiptAsync`, `PostValueAdjustmentAsync`) | `0008__inventory.sql`: `value_entry_gl_link` (P-1, deferred), per-lot stock check, append-only; `0021`: position check by deltas |
| Goods receipt (R-01) | `src/Rochell.Procurement/GoodsReceipts/PostGoodsReceiptHandler.cs` | `0010__goods_receipt.sql` (evidence trigger = K-25, guards) |
| Receipt reversal (R-02A/B) | `GoodsReceipts/ReverseGoodsReceiptHandler.cs` | `0011__goods_receipt_reversal.sql` |
| Receipt correction (R-03A/B) | `ReceiptCorrections/ReceiptCorrectionHandlers.cs` | `0012__receipt_correction.sql` |
| Supplier invoice (R-04, R-05, R-07, R-07B) | `SupplierInvoices/SupplierInvoicePosting.cs`, `SupplierInvoiceHandlers.cs` | `0014`, `0015` |
| Repost and valuation residual (R-06) | `src/Rochell.Procurement/Ledger/LedgerHandlers.cs` | `0016`, `0017` (one reversal per value entry per cause) |
| Hash chain | `src/Rochell.Audit/LedgerSealer.cs`, `GroupReader.cs`, `ChainHash.cs`, `VerifyHashChain.cs` | `0018__hash_chain.sql` (`integrity_state`, registration triggers, sealed groups closed) |
| Reconciliations and close | `src/Rochell.Reconciliation/Reconciliations.cs`, `CloseHandlers.cs` (SERIALIZABLE), `ReopenHandlers.cs` | `0019__reconciliation_close.sql` |

Engineering notes per area: `docs/engineering/posting-engine.md`, `inventory.md`, `goods-receipts.md`, `supplier-invoices.md`,
`ledger-maintenance.md`, `hash-chain.md`, `reconciliation-close.md`.

## 4. Invariants to verify

For each row: is it enforced where the table says, can the application role bypass it, and does the listed test really fail
when it is broken?

| # | Invariant | Enforced by | Tests |
| --- | --- | --- | --- |
| L1 | Every journal balances (Σ debit = Σ credit) | deferred `fin.gl_journal_balanced` | AT-04, CC-04 |
| L2 | Every value entry has exactly one GL line of the same amount, posting date, plant and item; every RAW_MATERIAL line has a value entry (P-1) | deferred `inv.value_entry_gl_link`, CHECKs on `fin.gl_entry` | VAL-01, VAL-02, AT-05 |
| L3 | Valuation quantity = Σ quantity entries; valuation value = Σ value entries = Σ RAW_MATERIAL GL, per area × item (P-3) | deferred `inv.check_valuation_position` over control totals written by SECURITY DEFINER triggers (0021, E-PR19-10) | AT-01, AT-02, CC-04, `Position_control_totals_…` |
| L4 | Stock per location × item × lot = Σ its quantity entries; never negative | deferred `inv.quantity_entry_position`, CHECK `quantity >= 0` | IV-01, CC-01 |
| L5 | Ledgers are append-only for everyone (no UPDATE/DELETE/TRUNCATE) | `core.reject_mutation()` triggers + grants | schema tests per module |
| L6 | `accounting_status = POSTED` ⇔ an unreversed AUTO journal of the posting event exists (K-25, E-11) | deferred evidence triggers in 0010/0011/0012/0014 | AT-07, ACC-EVIDENCE |
| L7 | Exact reversals: one per journal, inverse of every line, own value entries; a value entry reversed at most once per cause | `WriteReversalAsync`, UNIQUE in 0006/0016/0017 | RC-01, RC-01b, SI-06, SI-06b, REV-01 |
| L8 | Moving average by valuation area; the last unit takes the remaining value; value entries 2 decimals half-up (E-PR07-1) | `InventoryLedger.ReserveIssueAsync` | IV-02, IV-05 |
| L9 | Posting date: the business date's period if its component is open, otherwise the first open day with `late_entry` (PD-01, CC-05) | `PostingEngine` period resolution with shared advisory lock per period × component; close takes it exclusively | PD-01, PD-02, CC-05 |
| L10 | A closed component accepts nothing; close requires every ledger group of the period SEALED and no blocking reconciliation ERROR | `CloseHandlers` (SERIALIZABLE), `close_component_state_guard` | PD-02, INT-02, INT-03 |
| L11 | Every status change has its `core.state_history` row in the same transaction (ADR-027) | deferred triggers per document | schema tests |
| L12 | A sealed group can never receive rows; an altered row makes its group SEAL_ERROR and breaks every later chain hash | 0018 triggers, `LedgerSealer` | HS-01, HS-02, INT-02 |
| L13 | Idempotency: a key is executed once per company × command type; a rejected command leaves no `command_log` row | `command_log` unique index and guards | ID-01…ID-07 |
| L14 | Tenant isolation: RLS on every company table; composite FKs keep children in their parent's company | RLS policies, composite FKs (incl. 0021) | TEN-01, TEN-02 |
| L15 | Permissions and SoD of §14; approver ≠ creator on the same document | `SqlCommandAuthorizer`, `iam.enforce_sod`, CHECKs | SC-01…SC-04, RO-01, RO-02 |

## 5. Where to look hardest

Ranked by risk:

1. **Position check by deltas (new in PR-19, E-PR19-10).** `db/migrations/0021__slice_conformance.sql` lines 98–181.
   Control totals on `inv.inv_valuation_balance` (`ledger_quantity`, `ledger_value`, `gl_value`) are added to by triggers on
   `inv_quantity_entry`, `inv_value_entry` and RAW_MATERIAL `gl_entry` inserts. Check: the backfill is right; no ledger insert
   path escapes the triggers; `SECURITY DEFINER` functions set `search_path` and cannot be called by the application
   (`REVOKE … FROM PUBLIC`); the application cannot write the control columns (column-level INSERT/UPDATE grants); the check is
   equivalent to the full sums the reconciliations compute (INV-QTY-BALANCE, INV-VALUE-BALANCE, INV-VALUE-GL).
2. **Lock order.** Before this review, CC-04 (50 workers, 60 s) showed 10–13 deadlocks per run, all resolved by the pipeline's
   retry. The PostgreSQL log traced every one to `InventoryLedger.LockStockOfItemAsync` (negative receipt corrections): it
   locked stock rows *in consumption order*, preferred lot first, so two corrections preferring different lots locked the same
   rows in opposite orders. It now locks in one canonical order (lot, location) and moves the preferred lot first afterwards,
   with the same consumption order as E-PR11-3; CC-04 then shows 0 deadlocks and about 25 % more completed commands. Check
   that every other multi-row `FOR UPDATE` (`LockPoLinesAsync`, `LockReceiptLinesAsync`, `LockStockAsync` callers) also locks in
   a canonical order; the test prints the deadlock count on every run.
3. **Price-difference allocation (R-05, R-07B, STOCK_COVERAGE, POL-01).** `SupplierInvoicePosting.cs`: the share `s` still in
   stock, the covered part to RAW_MATERIAL and the rest to PPV, and the reversal's journal B with `s′`. Worked figures: AT-03,
   SI-06 (s = 0.25, D = 2,000 → B = Dr RAW 500 / Cr PPV 500), SI-06b.
4. **Reallocation and corrections (R-02B, R-03A/B).** The formulas are in `errata.md` ("R-03B amounts …"); RC-01b gives
   Dr RAW 300 / Cr PPV 300.
5. **Repost chains (R-REP).** A reposted receipt or invoice must still reverse exactly (`reposts_value_entry_id` chain,
   `PostingEngine.RepostedValueEntry`).
6. **Rounding (R-08).** Only within `rounding_difference_tolerance` of the POSTING policy; nothing else absorbs differences.
7. **Close and reopen.** SERIALIZABLE close, snapshot hash, second approver, REOPENED only in the approval transaction.
8. **Grants and RLS.** A quick independent pass: connect as `rochell_app` and try to write ledgers, balances, command_log,
   integrity data (schema tests do this per module).

### Fixes from the interim review (E-VS1-6…11, PR #31) — review them too

An automated adversarial review (E-VS1-3) found five defects before this review; their fixes are new ledger code:
R-07B caps s′ at s and STOCK_COVERAGE uses the invoice's total quantity of the item (`SupplierInvoicePosting.cs`);
`CloseComponent` takes its period lock before its transaction begins (`IPreTransactionLocks` in `CommandPipeline.cs`);
migration `0022__interim_review_guards.sql` guards component transitions, rejects journals into a CLOSED component and checks
balance rows at COMMIT. Regression tests: `InterimReviewCloseTests`, `InterimReviewInvoiceTests`; issues #24–#29.

## 6. Approved deviations to be aware of

Read `docs/architecture/errata.md` in full; these are the ones that change ledger behaviour or guarantees:

| Errata | What |
| --- | --- |
| E-PR07-1, E-PR07-8 | Value entries 2 decimals; last unit takes the remaining value; position equality at COMMIT |
| E-PR13-3, E-PR14-1 | A supplier invoice with non-recoverable ITBIS is POSTING_BLOCKED (no journal); a goods receipt is never POSTING_BLOCKED (P-1 prevails) |
| E-PR14-3 … E-PR14-5 | Repost and residual adjustment (R-06) semantics |
| E-PR16-* | Reconciliations, blocking per component, close gate by ledger date |
| E-PR19-4 | P-7 `definition_hash` replaced by immutable fiscal definitions |
| E-PR19-10 | Position check by deltas (section 5.1) |
| E-PR19-12, E-PR19-5, E-PR19-6 | No ownership, availability or lot-issue-policy dimensions in VS#1 |
| E-PR19-14 | `UNIQUE (company_id, pk)` not on upserted tables (it breaks concurrent `ON CONFLICT`) |
| E-PR19-15 | `committed_at` from the platform clock |
| E-VS1-6 … E-VS1-11 | Fixes of the interim review (section 5, last subsection) |

## 7. How to report

Open one GitHub issue per finding (label `b02`), or comment on the files in a PR against `main`:

```
Area: (section 3 row)        Invariant: (L1…L15, or new)
Severity: Blocker (money/quantity can be wrong or lost) | Major (guarantee weaker than stated) | Minor
Scenario: exact commands / SQL that reproduce it
Expected vs actual:
```

Blockers and Majors are fixed through the normal PR flow (with an erratum when they touch the frozen design) before sign-off.

## 8. Sign-off

| Area | Reviewer | Date | Result | Notes |
| --- | --- | --- | --- | --- |
| Transaction template and idempotency | | | | |
| Posting Engine | | | | |
| Inventory ledger and position check (0021) | | | | |
| Goods receipt, reversal, correction | | | | |
| Supplier invoice posting and reversal | | | | |
| Repost and residual adjustment | | | | |
| Hash chain | | | | |
| Reconciliations, close, reopen | | | | |
| Grants, RLS, permissions | | | | |

When every row is signed, record the result in `docs/acceptance/vs1.md` (status table) and the slice closure is issued (§17).
