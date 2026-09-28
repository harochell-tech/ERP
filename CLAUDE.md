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
| PR-16 | Merged: reconciliations, CloseComponent, reopen with second approver |
| PR-17 | Merged: Explain this entry, read-only query pipeline, POL-01 inputs on R-05/R-07B |
| PR-18a | Merged (#19): API (OIDC, 44 command endpoints, read queries, OpenAPI, hosted sealer/digest), AT-01/AT-02 over HTTP |
| PR-18b | Merged (#20): `web/` (Next.js static export served by the API), dev stack, Playwright journey |
| PR-19 | CC-04, PF-01 (`tests/Rochell.LoadHarness`, workflow `load`), traceability of all 70 acceptance tests, P-7 conformance (migration 0021). Merged (#21). Acceptance: `docs/acceptance/vs1.md`; B-02 review guide: `docs/acceptance/b02-ledger-review.md` (pending) |

| VS#2 | Supplier payments and banks — Frozen Baseline `docs/architecture/vs2/frozen-baseline-vs2.md` approved (E-VS2-1…10). VS2-01 (schema, E-VS2-01-1…14), VS2-02 (bank accounts, module `Rochell.Treasury`, E-VS2-02-1…8), VS2-03 (payments, R-09, E-VS2-03-1…9), VS2-04 (reversal, E-VS2-04-1…7), VS2-05 (statements, matching, R-10, E-VS2-05-1…10; migration 0028; test formats only — the real banks' formats wait for the owner's CSV samples), VS2-06 (BANK-GL, PAY-APPL, BANK-REC close, E-VS2-06-1…10, migration 0029), VS2-07 (treasury queries, E2E-01 over HTTP, E-VS2-07-1…7, migration 0030): `docs/engineering/banks-and-payments.md`. Screens: E-UI-1…6, design canvas https://claude.ai/artifact/BqCTac3RWvcuK4va8D3cYW; VS2-08 (treasury screens, grouped menu, Playwright treasury journey: `web.md`), VS2-09 (INV-P, payment concurrency, acceptance matrix `docs/acceptance/vs2.md`, workflow `inv-p`, E-VS2-09-1…6). VS#2 is code-complete; open: real bank formats (owner's samples), B-02. UI-01 (audit and security screens, plants, chart of accounts, Inicio counters, E-UI01-1…8, migration 0032) |
| FIN-1 / VS#3 | Baselines approved 2026-09-27: `docs/architecture/fin1/frozen-baseline-fin1.md` (E-FIN1-1…10), `docs/architecture/vs3/frozen-baseline-vs3.md` (E-VS3-1…17). FIN1-01 (ledger schema, migration 0033, E-FIN1-01-1…7) merged (#54); FIN1-02 (account and adjustment commands, MANUAL-EVIDENCE / TB-BALANCED, ACR close, migration 0034, E-FIN1-02-1…4) merged (#55): `docs/engineering/ledger.md`; FIN1-03 (trial balance, ledger, balance sheet, income statement, CSV, report structures, STRUCT-COVERAGE, migration 0035, E-FIN1-03-1…11, acceptance `docs/acceptance/fin1.md`) merged (#56); FIN1-04 (accounting screens, migration 0036, E-FIN1-04-1…11, `web.md`) merged (#57), deployed to staging. VS3-01 (sales master schema: customers, terms, standard cost, price list, vehicles, drivers, 13 account roles, 5 roles, migration 0037, E-VS3-01-1…17): `docs/engineering/sales.md`, merged (#58); VS3-02 (module `Rochell.Sales`, customer / terms / standard cost / price list / fleet commands and queries, supplier payment terms, migration 0038, E-VS3-02-1…12) merged (#59); VS3-02b (opening finished goods: migration batches, OPEN-INV, P-3 for finished goods, migration 0039, E-VS3-02b-1…10) merged (#60); VS3-03 (sales order and credit check, policy CREDIT, SAL-01/02, migration 0040, E-VS3-03-1…10) merged (#61); VS3-04 (deliveries, gate out, POD, control transfer, P-15/15R/16/30, REVENUE_ACCOUNTING, migration 0041, E-VS3-04-1…15) merged (#62); VS3-05 (invoice from deliveries, SALES_ITBIS, AR documents, AR-REC, external e-CF, void, P-18, migration 0042, E-VS3-05-1…14) merged (#63); VS3-06 (commercial credit note NC-, P-22, e-CF 34, SAL-08, migration 0043, E-VS3-06-1…10) merged (#64); VS3-07 (receipts, deposits, applications, customer withholding, bounced cheques, matching with the statement, P-23/24/25/27/29, AR-01…03, migration 0044, E-VS3-07-1…15) merged (#65); VS3-08 (AR-GL, CONTRACT-ASSET, RECEIPT-APPL, FISC-DOC, DELIVERY-OPEN, ACC-EVIDENCE for VS#3, AR-REC close, AR-04, migration 0045, E-VS3-08-1…12); next: VS3-09 (API, statement of account, AR aging, E2E-S1 over the API) |
| B-03 | E-B03-1…15. Staging live at `https://staging.industriasrochell.com.do` (the `sistema` VPS, 2.25.237.35; WORM + backups on Backblaze B2; company Block Rochell, S.R.L. (STAGING), RNC 131925332, synthetic data only). Deploy: Actions → `deploy-staging` on `main`. Runbook `docs/engineering/staging.md`. B03-3: test identities (E-B03-14, migration 0024, `identity.md`). B03-4: configuration screens (E-B03-15, migration 0025 `configuration:read`, `web.md`). PF-01 on staging passed (run 36315487038, p95 337 ms). Pending: first digest in WORM, verify over WORM |

Open blockers / conditions: **B-02** second reviewer for ledger PRs; **B-03** staging PostgreSQL 17 + WORM storage;
**A-01** Controller approves policy values and account maps; **A-02** official DGII sources for ITBIS / withholding.
**A-03** closed 2026-09-27 (Google sign-in works on staging).

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
- Module graph (E-PR08-1): every module → Platform; **Procurement** may also use Finance, Inventory, MasterData and Tax;
  **Treasury** may also use Finance and MasterData (E-VS2-02-1); **Sales** may also use Finance, Inventory, MasterData and Tax (E-VS3-02-2); Audit may use Finance and Inventory (E-PR15-7).
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
   (97 permissions, 21 roles incl. DIRECTOR (E-ADM-1), CONTADOR (FIN-1, + configuration:read in 0036) and the 5 VS#3 roles, 36 SoD rules; policy parameter counts in `AccountingPolicyTests`) — update them when you seed more.
8. The table inventory test compares `information_schema` order: check it against a real migrated database.
9. Deferred guarantees checked at COMMIT: journal balance; **P-1** every inventory GL line ↔ exactly one value entry;
   **P-3** valuation = Σ value entries = GL inventory; **K-25** `accounting_status = POSTED` ⇔ an unreversed AUTO journal of the
   posting event; **ADR-027** every status change has its `core.state_history` row in the same transaction.
10. Queries (`IQuery` / `IQueryHandler`, `QueryPipeline`) run READ ONLY after authorization; they also need a seeded
    `[RequiresPermission]` (ArchitectureTests covers both handler kinds).
11. Every command must write its result (`command_log.result_payload`) before COMMIT — the pipeline does it; hand-written SQL
    simulations must too.
12. `bool || text` in PostgreSQL gives `true` / `false` (psql only *displays* `t` / `f`): assertions on concatenated SQL use the
    full words.
13. Before each commit, re-derive expected test values by hand (the R-02B, R-03B, R-05, R-07B tests have worked examples).

## 6. Handy SQL

```sql
-- Posting rules and their state
SELECT r.code, v.version, v.status, v.close_component FROM fin.posting_rule r JOIN fin.posting_rule_version v USING (posting_rule_id) ORDER BY 1;
-- Journals of an event
SELECT posting_generation, journal_type, reverses_journal_id FROM fin.gl_journal WHERE source_event_id = '<event>';
```

## 7. PR-18 — approved decisions (E-PR18-1…7, E-PR18b-1…11, in `errata.md`)

API: `docs/engineering/api.md`. Web: `docs/engineering/web.md`.
- After any API change regenerate `src/Rochell.Api/openapi.json`
  (`ROCHELL_UPDATE_OPENAPI=1 dotnet test tests/Rochell.Api.Tests --filter OpenApiDocumentTests`) **and** the web types
  (`cd web && npm run gen:api`); CI fails if either is stale.
- The UI never does money or quantity arithmetic; decimals are strings. Every POST: `X-Rochell-Csrf: 1` + an `Idempotency-Key`
  created when the form opens; `STEP_UP_REQUIRED` → `/api/v1/auth/step-up`, the user presses again with the same key.
- Local stack: `dotnet run --project tests/Rochell.DevStack -c Release -- --web-root web/out` (Docker; Chrome/Firefox).
- The API host keeps `RochellEnvironments.EnsureSupported` (Development / Test / Staging in VS#1).
- Every acceptance test carries `[Trait("Acceptance", "<ID>")]`; `AcceptanceTraceabilityTests` fails if a baseline ID has no test
  or is missing from `docs/acceptance/vs1.md`.
- A second unique index on a table written by `INSERT … ON CONFLICT (pk) DO UPDATE` breaks concurrent upserts (23505): PF-01
  caught it (E-PR19-14).
