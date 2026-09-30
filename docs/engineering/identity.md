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

(The "nobody holds every permission" part is superseded by the superadministrator, E-ADM-2, below.)

Role `DIRECTOR` (migration `0031__director_role.sql`) holds every READ permission of the matrix — including
`bank_account_number:read` — and no WRITE or SECURITY permission: it opens every screen and report and executes nothing, so it
takes part in no segregation-of-duties pair. Nobody holds every permission: SoD pairs and the four-eyes CHECKs (preparer ≠
releaser, requester ≠ verifier, creator ≠ approver…) stay. In staging the tester works every role through "Actuar como…" (test
identities for all roles, `deploy/staging/seed.sh`). In production the Director role is granted like any other: `rochell-migrate
grant-role` at bootstrap, then role requests with a second approver.

## Daily process (E-FIS1-04-7)

Service identity `…d002` ("Proceso diario", `IdentityConstants.DailyProcessUserId`; the deployment identity `…d001` stays a
`granted_by` only). Role `PROCESO_DIARIO` grants only `fiscal_authorization:suspend`; database triggers keep it for service
identities and give them no other role. Migration 0056 assigns it in every existing company and `rochell-migrate create-company`
in each new one. Only the API opens its sessions (`SessionService.StartServiceSessionAsync`, `auth_method = 'SERVICE'`, no step-up)
and ends them after each run; a trigger allows SERVICE sessions only for service identities and no other kind for them.
`SqlCommandAuthorizer` accepts them like any session (permission, company, lifetimes); `DescribeAsync` — the cookie path —
rejects them, so they never reach HTTP. The API's fiscal expiry service (`api.md`) is its only user.

## Screens and queries (UI-01)

`iam:read` (migration `0032__ui01_identity_audit.sql`, E-UI01-4) reads `identity/users` (the company's users with their active
roles, plus users with no assignment anywhere yet) and `identity/role-requests?status=` (pending first, with requester, approver
or rejecter and reason). `identity/reject-role-change` (`role:second_approve`, E-UI01-6) closes a pending request as REJECTED with
a mandatory reason; like the approval, never by the requester nor the affected user (command and CHECK). Screens: Seguridad ›
Usuarios y roles (request assignment / revocation, company-wide) and Solicitudes de rol (approve / reject). A READ permission
added after 0031 is granted to DIRECTOR explicitly by its migration (0032 does so for `iam:read`).

## Display names (UX1-01a, E-UX1-01-3)

`iam.user.display_name` (migration 0060) holds the "name" claim of the Google ID token (the API asks for the `profile` scope). It
is written at every sign-in and step-up when the token carries one (trimmed, at most 200 characters; a token without it leaves it
unchanged) and never used for identity: the e-mail and the OIDC subject stay the identity. Queries that show who prepared,
approved or changed something return `coalesce(display_name, email)`; the users and role-request lists keep the e-mail and add
`displayName` / `userDisplayName`; the session description adds `displayName` and each company's `plants` (code and name).

## Superadministrator (ADM-2, E-ADM-2-1…7)

Role `SUPERADMIN` (migration `0061__superadmin.sql`) holds every permission except `identity:act_as`; a trigger on
`iam.permission` grants it each permission a later migration adds. An assignment is company-wide and ends within 90 days
(`iam.role_assignment_superadmin_term` fills 90 days when no end is given and refuses more); it is revoked earlier like any role
(`valid_to` may be brought forward while in the future) and renewed with a new request. The first one is granted with
`rochell-migrate grant-role <email> SUPERADMIN <rnc>`, which prints the end date.

`iam.controls_waived(company, user[, at])` is true while the user holds an unexpired SUPERADMIN assignment. With it:

- `iam.enforce_sod()` does not check the user (roles granted meanwhile remain after the assignment ends);
- the 22 "decider ≠ preparer" rules, now `core.four_eyes(decider, preparer, constraint)` triggers (SQLSTATE 23514 and the former
  constraint name), and the role-request requester ≠ decider rules let the same person decide; the affected user of a role request
  still never decides, and nobody requests or grants roles for themselves;
- the 24 command checks call `ControlWaiver.WaivedAsync(context, ct)` (Platform, application clock) only when decider = preparer.

Each waiver sets `app.controls_waived` for the transaction: `core.command_log.controls_waived` is set when the command's result
is written and `core.state_history.controls_waived` on rows inserted after it. CONTROLS-WAIVED (WARNING) lists the waived
commands of the cutoff's month.
