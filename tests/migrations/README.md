# Test-only migrations

Objects that must exist only in test databases (e.g. posting rule R-T1 for the `IssueStock` fixture, Patch 1 §6).

- Same naming rule as `db/migrations`: `NNNN__snake_case.sql`, contiguous from `0001`, journaled as source `test`.
- Applied only by test projects, after all `main` migrations.
- Never referenced by `Rochell.Migrations.Cli` (checked by ArchitectureTests; TST-01 in a later PR).

Empty in PR-01.
