# CI and merge policy

Workflow: `.github/workflows/ci.yml`, job `build-test`: restore → `dotnet format whitespace --verify-no-changes` → build Release (warnings are errors) → tests (unit, architecture, PostgreSQL 17 via Testcontainers) → web (E-PR18b-10): `npm ci`, lint, typecheck, generated API types in sync with `openapi.json`, Vitest, `next build`, and the Playwright journey against the real API (`tests/Rochell.DevStack`) → TRX artifacts (and the Playwright report on failure).

## Blocking merges (manual, one-time, repository admin)

GitHub → Settings → Branches → Branch protection rule for `main`:

- Require a pull request before merging, with **1 approval** (second-person review, DoD item 3).
- Require status checks to pass: **`build-test`**; require branches to be up to date.
- Do not allow bypassing the above settings (include administrators).
- Block force pushes and deletions.

Without this rule the workflow reports failures but cannot block a merge by itself.

## Local run

Requires .NET SDK 10, Docker (Testcontainers pulls `postgres:17.6-alpine`) and, for `web/`, Node 24 (`web/.nvmrc`).

```bash
dotnet test Rochell.slnx
cd web && npm ci && npm run lint && npm run typecheck && npm run check:api && npm test && npm run build
npx playwright install chromium && npx playwright test   # needs the Release build of the solution
```
