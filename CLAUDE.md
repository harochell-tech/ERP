# Rochell Core — ERP/MES de Industrias Rochell

Owner: **Alexander Rochell** (Director de Operaciones, Block Rochell, S.R.L., Higüey, RD). Talk to him **in Spanish**, directly
and with actionable steps. Code, identifiers, comments, commit messages and `docs/` stay **in English** (as they are now).

Stack: .NET 10 / C# · PostgreSQL 17 · xUnit + Testcontainers · GitHub Actions (`ci / build-test`). Repo: `harochell-tech/ERP`.

## 1. Source of truth — read before touching code

`docs/architecture/README.md` lists the documents and their precedence:
`errata.md` > Patch 1.1 > Patch 1 > **Frozen Baseline v2.1.1** (§8–§17) > v2.1 > v2 > v1.

- The Frozen Baseline §17 is the PR plan; §15 the acceptance tests; §13 the posting rules; §14 the permission matrix.
- `docs/engineering/*.md` explains what each merged PR built. `docs/architecture/errata.md` holds every approved decision.

**Freezing rule.** Never resolve an ambiguity by implementing. Stop, and propose numbered errata to Alexander in Spanish:

| # | Problema | Propuesta |
| --- | --- | --- |
| E-PR15-1 | … | … |

Wait for his "apruebo E-PR15-1 a N" (or corrections) **before** writing code. Then add the rows to `errata.md`.
A previously approved errata wins over your own reading of the baseline (example: E-PR03-4 (a) made configure/activate of
fiscal rules document-level, not SoD — a test in Identity enforces it).

## 2. Status

| PR | State |
| --- | --- |
| PR-01 … PR-13b | Merged to `main`, CI green |
| PR-14, PR-15 | Merged: repost + valuation residual; hash chain (S3 Object Lock still pending B-03) |
| PR-16 | Reconciliations + CloseComponent + reopen with second approver (E-PR16-1…9 approved). Branch `pr-16-reconciliation-close` |
| PR-17 | Explain this entry (EX-01): explanation templates of every rule |
| PR-18 | API (OpenAPI) + minimal web UI; AT-01 and AT-02 end to end through the API |
| PR-19 | Concurrency and load suite: CC-04, PF-01, full regression → slice acceptance |

Open blockers / conditions: **B-02** second reviewer for ledger PRs; **B-03** staging PostgreSQL 17 + WORM storage;
**A-01** Controller approves policy values and account maps; **A-02** official DGII sources for ITBIS / withholding;
**A-03** Google Workspace OIDC client for staging.

## 3. Workflow per PR

1. `git checkout main && git pull && git checkout -b pr-NN-short-name`.
2. Read the baseline rows for the PR (§17) and the tests it must pass (§15). List every ambiguity → errata table → wait.
3. Implement: one forward-only migration `db/migrations/00NN__name.sql` (never edit a merged migration), code, tests,
   `docs/engineering/<topic>.md`, errata rows, and the table inventory in `tests/Rochell.Migrations.Tests/SchemaTests.cs`.
4. **Verify locally before pushing** (Docker Desktop must be running):
   ```bash
   dotnet format whitespace Rochell.slnx --verify-no-changes
   dotnet build Rochell.slnx -c Release          # warnings are errors
   dotnet test Rochell.slnx -c Release --no-build
   ```
5. `git push -u origin HEAD`, `gh pr create`, `gh pr checks --watch`. Report the result to Alexander in Spanish.
   Merge only with CI green and his go-ahead.

## 4. Architecture rules enforced by tests (`tests/Rochell.ArchitectureTests`)

- No `float` / `double` / `Half` anywhere in `src/`. Money `numeric(19,4)` (GL amounts 2 decimals), quantities `numeric(18,6)`.
- No decimal literals in `src/` except `0m`, `1m`, `100m` (type limits marked `// type-limit`). Thresholds and rates come from
  accounting policies or fiscal rules, never from code.
- Every production `ICommandHandler<T>` has exactly one `[RequiresPermission]`, and that permission is seeded in `db/migrations`.
- Test fixtures (`TEST.`, `TestStock`, `test:`, `TEST_`) live only in `tests/migrations`, never in `db/migrations`.
- Module graph (E-PR08-1): every module → Platform; **only Procurement** may also use Finance, Inventory, MasterData and Tax.
  Commands that need the Posting Engine and the inventory ledger together therefore live in Procurement (e.g. `Ledger/`).

## 5. Hard-won lessons (each one cost a CI round — do not repeat)

1. **Validate as the application role, on a freshly migrated database.** Tests run as `rochell_app` (column-level grants); the
   owner bypasses privileges and RLS. `SELECT … FOR UPDATE` needs the UPDATE privilege: on append-only tables use
   `pg_advisory_xact_lock` instead.
2. **Enum values:** `ALTER TYPE … ADD VALUE` cannot be used as a literal in the same transaction (each migration is one
   transaction). In CHECKs compare `movement_type::text IN (...)`; partial-index predicates cannot cast, so put such indexes in the
   **next** migration (see 0016 / 0017).
3. **`INSERT … ON CONFLICT DO UPDATE` checks CHECK constraints on the proposed row** before the conflict: a negative delta
   (e.g. `quantity >= 0`) fails even if the final value is valid. Removals update the existing row directly.
4. **Decimals in JSON are strings** (ADR-015): command results and event payloads with numbers like `18000.00` are rejected by
   the canonicalizer. Use `ToString(CultureInfo.InvariantCulture)`.
5. **`core.deployment_environment` is empty in test databases** (production sets it with `rochell-migrate init-environment`).
   Tests that need it initialize it (`TaxSetup.InitTestEnvironmentAsync`); code must fail loudly when it is missing.
6. **Connection pools:** each test gets its own database; setup connections use `Pooling=false` (`PostgresFixture`), otherwise a
   100+ test project exhausts `max_connections` (53300). Concurrency tests throttle themselves (e.g. `SemaphoreSlim(32)`):
   the app pool allows 100 connections, and with the sealer and fixture connections that already exceeds the server limit.
7. SoD pairs in `iam.sod_rule` are ordered (`permission_a < permission_b`). Seed counts are asserted by tests
   (39 permissions, 16 SoD rules; policy parameter counts in `AccountingPolicyTests`) — update them when you seed more.
8. The table inventory test compares `information_schema` order: check it against a real migrated database.
9. Deferred guarantees checked at COMMIT: journal balance; **P-1** every inventory GL line ↔ exactly one value entry;
   **P-3** valuation = Σ value entries = GL inventory; **K-25** `accounting_status = POSTED` ⇔ an unreversed AUTO journal of the
   posting event; **ADR-027** every status change has its `core.state_history` row in the same transaction.
10. Every command must write its result (`command_log.result_payload`) before COMMIT — the pipeline does it; hand-written SQL
    simulations must too.
11. Before each commit, re-derive expected test values by hand (the R-02B, R-03B, R-05, R-07B tests have worked examples).

## 6. Handy SQL

```sql
-- Posting rules and their state
SELECT r.code, v.version, v.status, v.close_component FROM fin.posting_rule r JOIN fin.posting_rule_version v USING (posting_rule_id) ORDER BY 1;
-- Journals of an event
SELECT posting_generation, journal_type, reverses_journal_id FROM fin.gl_journal WHERE source_event_id = '<event>';
```
