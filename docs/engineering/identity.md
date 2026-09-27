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

## Test identities (E-B03-14, TEST databases only)

One person can exercise every flow without weakening any control. A PROBADOR (`identity:act_as`, SECURITY) acts as synthetic
users (`iam.user.kind = SYNTHETIC`: no employee, no Google subject, an e-mail as label), each holding its own roles, so segregation
of duties and the four-eyes CHECKs apply to them exactly as to people.

- **Acting session**: `iam.session.auth_method = ACT_AS`, `user_id` = the synthetic user, `authenticated_session_id` = the
  person's Google session. Commands carry the acting session, so the trail names both the identity and the person behind it.
- **Lifetime**: usable while the Google session is open, its person active, and within that session's absolute lifetime; its
  own idle timeout applies. Logout ends both. Step-up re-authenticates the person with Google (`RecordStepUpAsync`).
- **Database guards (migration 0024)**: synthetic users, ACT_AS sessions and grants of a role holding `identity:act_as` are
  refused unless `core.current_environment()` is TEST; an ACT_AS session also needs the person's `identity:act_as` and a role of
  the synthetic user in the tenant set by the caller.
- **Provisioning**: `rochell-migrate create-synthetic-user <email>` then `grant-role`; the tester gets `grant-role … PROBADOR`.
- **UI**: the header selector "Actuar como…" lists the identities (`GET /api/v1/auth/test-identities`), switches
  (`POST /api/v1/auth/act-as`) and returns (`POST /api/v1/auth/act-as/stop`); while acting it shows "Identidad de prueba".

## Director (E-ADM-1)

Role `DIRECTOR` (migration `0031__director_role.sql`) holds every READ permission of the matrix — including
`bank_account_number:read` — and no WRITE or SECURITY permission: it opens every screen and report and executes nothing, so it
takes part in no segregation-of-duties pair. Nobody holds every permission: SoD pairs and the four-eyes CHECKs (preparer ≠
releaser, requester ≠ verifier, creator ≠ approver…) stay. In staging the tester works every role through "Actuar como…" (test
identities for all roles, `deploy/staging/seed.sh`). In production the Director role is granted like any other: `rochell-migrate
grant-role` at bootstrap, then role requests with a second approver.
