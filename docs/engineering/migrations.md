# Migrations (forward-only)

Runner: `src/Rochell.Migrations` (provider-agnostic) + CLI `src/Rochell.Migrations.Cli` (`rochell-migrate`).

## Rules

1. File name `NNNN__snake_case_name.sql`, contiguous from `0001`. One migration per PR that changes schema.
2. A merged migration is **never edited or deleted**. The journal stores a SHA-256 of each script (LF-normalized, BOM removed); any change fails the next run.
3. There are no down migrations. Corrections are new migrations.
4. Each script runs in its own transaction together with its journal row. A failing script leaves no trace.
5. Runs are serialized with a PostgreSQL advisory lock; concurrent runners are safe.
6. `migrations.applied_migration` is append-only (trigger).
7. Migrations run with the **deployment role** (schema owner), never with the application role.
8. `db/migrations` = production schema. `tests/migrations` = test-only objects (e.g. R-T1); never shipped by the CLI.
9. Do not use `SAVEPOINT`-dependent tricks, `real`, `float4/8`, `double precision` or `money` (ADR-015, architecture tests).

## CLI

```bash
export DOTNET_ENVIRONMENT=Staging                   # Development | Test | Staging
export ConnectionStrings__Rochell="Host=...;Database=...;Username=rochell_deploy;Password=..."
dotnet run --project src/Rochell.Migrations.Cli -- status    # checksums + pending
dotnet run --project src/Rochell.Migrations.Cli -- migrate
```

After the first migration of a database, record its deployment environment once (Patch 1.1, correction 3):

```bash
dotnet run --project src/Rochell.Migrations.Cli -- init-environment TEST          # Development, Test, Staging databases
dotnet run --project src/Rochell.Migrations.Cli -- init-environment PRODUCTION    # production database only
```

The value can never be changed afterwards (the command refuses a different value; the table rejects UPDATE/DELETE).
Staging databases are TEST (E-B03-5).

A new deployment's company: `create-company <rnc> <legal-name>` (E-B03-7; staging uses a fictitious RNC and synthetic data).

Exit codes: `0` OK, `1` usage/configuration error, `2` migration error (modified, deleted, out-of-order or failing script).
