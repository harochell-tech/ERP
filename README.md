# Industrias Rochell ERP/MES — Rochell Core

Vertical Slice #1 (Purchase-to-Inventory-to-GL). Architecture is frozen; normative documents in order of precedence:

1. Architecture v2.1.1 — Frozen Baseline Patch 1.1
2. Frozen Baseline Patch 1
3. Architecture v2.1.1 — Errata & Frozen Baseline
4. Architecture v2.1 — Build Readiness Package
5. Architecture v2

Contradictions or ambiguities are reported as a proposed erratum; they are never resolved in code.

## Layout

| Path | Content |
| --- | --- |
| `src/Rochell.Platform` | Shared kernel, provider-agnostic (ADO.NET): command pipeline, idempotency, domain events, outbox/inbox, request log, canonical hashing, clock, ids |
| `src/Rochell.{Identity,MasterData,Finance,Inventory,Procurement,Tax,Audit,Reconciliation}` | Bounded contexts; may reference only Platform |
| `src/Rochell.Migrations`, `src/Rochell.Migrations.Cli` | Forward-only migration runner and CLI |
| `src/Rochell.Api` | ASP.NET Core host: OIDC sessions, command and query endpoints, OpenAPI (`openapi.json`), hosted sealer/digest, serves `web/` |
| `web/` | UI mínima (Next.js static export, Spanish), types generated from `openapi.json` |
| `db/migrations` | Production schema migrations |
| `tests/migrations` | Test-only migrations |
| `tests/Rochell.TestInfrastructure` | Shared PostgreSQL 17 fixture (Testcontainers), app-role login |
| `tests/Rochell.Migrations.Tests` | Runner + schema tests on PostgreSQL 17 |
| `tests/Rochell.Platform.Tests` | Platform tests (ID-01…07, CMD-01…03, hashing golden vectors, environment, privileges) |
| `tests/Rochell.Identity.Tests` | Identity tests (sessions, authorization, step-up, role changes, SoD, RLS) |
| `tests/Rochell.Api.Tests` | End-to-end API tests (AT-01/AT-02 over HTTP) |
| `tests/Rochell.SimulatedIdp`, `tests/Rochell.DevStack` | Simulated Google sign-in and the local development stack (test/dev only) |
| `tests/Rochell.LoadHarness` | PF-01 load harness (workflow `load`) |
| `tests/Rochell.ArchitectureTests` | Guardrails: no floating point, module boundaries, repository conventions |

## Environments

`Development`, `Test`, `Staging` (`ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT`). Any other value fails at startup.
Credentials for Test/Staging come only from environment variables. The fiscal production gate does **not** use these settings (Patch 1.1, correction 3).

## Local UI

```bash
dotnet build Rochell.slnx -c Release && (cd web && npm ci && npm run build)
dotnet run --project tests/Rochell.DevStack -c Release -- --web-root web/out    # http://localhost:5080 (Chrome/Firefox)
```

See `docs/engineering/web.md` for `next dev`. Slice acceptance: `docs/acceptance/vs1.md`. See `docs/engineering/` (migrations, platform, identity, api, web, ci) and `docs/architecture/errata.md`.
