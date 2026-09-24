# Identity (PR-03)

- **Users** (`iam.user`): global; humans need an employee and a Google OIDC subject. Provisioned with
  `rochell-migrate create-user <email> <google-subject> <employee-id>` (deployment role).
- **Bootstrap grants**: `rochell-migrate grant-role <email> <ROLE_CODE> <company-rnc> [plant-id]` — e.g. the first
  `ADMIN_SEGURIDAD` and `SEGUNDO_APROBADOR_SEGURIDAD`. SoD still applies.
- **Regular role changes**: `RequestRoleAssignment` / `RequestRoleRevocation` (role:assign / role:revoke, step-up) →
  `ApproveRoleChange` by a different person (role:second_approve, step-up). The change happens in the approval transaction.
- **Sessions**: `SessionService.StartOidcSessionAsync(validated claims)`; login counts as re-authentication;
  `RecordStepUpAsync` after a Google re-prompt; `EndSessionAsync` on logout.
- **Authorization**: every handler declares `[RequiresPermission("x", StepUp = …)]`; `SqlCommandAuthorizer` checks session,
  expiry, assignment (company/plant, validity), permission and step-up inside the command transaction.
- **Row-level security**: `core.*` and `iam.role_assignment*` are filtered by `app.company_id`, set per transaction by the pipeline.
- **Roles and permissions of VS#1**: seeded in `db/migrations/0004__iam.sql` exactly as the frozen §14 matrix; test-only
  permissions live in `tests/migrations`.
