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
| `src/Rochell.Platform` | Shared kernel (PR-02+). Today: supported hosting environments only |
| `src/Rochell.{Identity,MasterData,Finance,Inventory,Procurement,Tax,Audit,Reconciliation}` | Bounded contexts; may reference only Platform |
| `src/Rochell.Migrations`, `src/Rochell.Migrations.Cli` | Forward-only migration runner and CLI |
| `src/Rochell.Api` | ASP.NET Core host (empty until PR-18) |
| `db/migrations` | Production schema migrations |
| `tests/migrations` | Test-only migrations |
| `tests/Rochell.Migrations.Tests` | Runner + schema tests on PostgreSQL 17 (Testcontainers) |
| `tests/Rochell.ArchitectureTests` | Guardrails: no floating point, module boundaries, repository conventions |

## Environments

`Development`, `Test`, `Staging` (`ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT`). Any other value fails at startup.
Credentials for Test/Staging come only from environment variables. The fiscal production gate does **not** use these settings (Patch 1.1, correction 3).

See `docs/engineering/migrations.md` and `docs/engineering/ci.md`.
