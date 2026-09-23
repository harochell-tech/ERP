# CI and merge policy

Workflow: `.github/workflows/ci.yml`, job `build-test`: restore → `dotnet format whitespace --verify-no-changes` → build Release (warnings are errors) → tests (unit, architecture, PostgreSQL 17 via Testcontainers) → TRX artifacts.

## Blocking merges (manual, one-time, repository admin)

GitHub → Settings → Branches → Branch protection rule for `main`:

- Require a pull request before merging, with **1 approval** (second-person review, DoD item 3).
- Require status checks to pass: **`build-test`**; require branches to be up to date.
- Do not allow bypassing the above settings (include administrators).
- Block force pushes and deletions.

Without this rule the workflow reports failures but cannot block a merge by itself.

## Local run

Requires .NET SDK 10 and Docker (Testcontainers pulls `postgres:17.6-alpine`).

```bash
dotnet test Rochell.slnx
```
