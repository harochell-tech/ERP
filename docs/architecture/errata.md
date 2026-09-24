# Approved implementation errata

Errata approved by Alexander Rochell while implementing Vertical Slice #1. They complement the frozen baseline
(precedence: Frozen Baseline Patch 1.1 → Patch 1 → Errata & Frozen Baseline → v2.1 → v2) without changing its intent.

| Id | PR | Decision |
| --- | --- | --- |
| E-PR02-1 | PR-02 | `core.command_log.session_id` had no FK in PR-02 because `iam` did not exist; PR-03 adds `command_log_session_fk → iam.session`. |
| E-PR02-2 | PR-02 | Row hash of `core.domain_event` covers all columns except `row_hash`, in declared order. |
| E-PR02-3 | PR-02 | Canonical JSON (RFC 8785) accepts only integers with \|n\| ≤ 2^53−1; decimal values travel as strings (ADR-015). |
| E-PR03-1 | PR-03 | `iam.role_assignment.plant_id` and `iam.role_assignment_request.plant_id` have no FK until PR-04 creates `md.plant`; PR-04 adds composite FKs. |
| E-PR03-2 | PR-03 | Step-up ("reautenticación reciente") maximum age is host configuration; initial value 5 minutes. |
| E-PR03-3 | PR-03 | PR-03 implements login from already-validated OIDC claims (IdP simulated in tests); HTTP OIDC middleware wiring moves to PR-18 with the first endpoints. |
| E-PR03-4 | PR-03 | (a) `fiscal_rule:configure` ↔ `fiscal_rule:activate` is document-level only (existing CHECK). (b) The AUDITOR role cannot be combined with WRITE/SECURITY permissions. (c) Holders of `role:assign`/`role:revoke` cannot hold WRITE permissions. Permissions are classified READ / WRITE / SECURITY. |
| E-PR03-5 | PR-03 | In VS#1 users are provisioned by the deployment role (`rochell-migrate create-user`); bootstrap grants use `rochell-migrate grant-role` with a seeded SERVICE identity as `granted_by`. `user:register` is deferred. |
| E-PR03-6 | PR-03 | Office sessions: absolute lifetime 12 h, inactivity 30 min, both configurable. Requires `iam.session.last_activity_at` (refreshed at most once a minute). |
| E-PR03-7 | PR-03 | SoD exceptions are out of VS#1: `role_assignment.sod_exception_id` must be NULL. |
| E-PR03-8 | PR-03 | Login requires `email_verified = true`, `hd` = configured domain and an OIDC subject linked to an ACTIVE HUMAN user with employee. MFA is enforced by Google Workspace, not verified in the token. |
| E-PR04-1 | PR-04 | `DefineUomConversion` requires `item:activate` (Controller): a conversion changes inventory quantities. |
| E-PR04-2 | PR-04 | Plants (with their valuation area) and locations are created by the deployment role: `rochell-migrate create-plant` / `create-location`. |
| E-PR04-3 | PR-04 | VS#1 master lifecycle is DRAFT → ACTIVE; the Controller's activation is the approval. The enum keeps REVIEW/APPROVED/OBSOLETE. |
| E-PR04-4 | PR-04 | `UpdateSupplier` only in DRAFT; changes to active suppliers (with re-approval) come after VS#1. |
| E-PR04-5 | PR-04 | RNC validated by format only (9 digits RNC / 11 digits cédula, separators removed) through `IRncRegistry`; `rnc_validated_at` stays NULL until the DGII lookup exists. |
| E-PR04-6 | PR-04 | The schema allows FOREIGN parties; the VS#1 command creates LOCAL suppliers only. |
| E-PR04-7 | PR-04 | `item_category` is a closed list: CEMENTO, AGREGADO, ADITIVO, OTRA_MATERIA_PRIMA. |
| E-PR04-8 | PR-04 | Conversions always point to the item's base UOM; a new one closes the open one and cannot start before today. |
| E-PR04-9 | PR-04 | UOM seed: kg, t (mass), m3, l (volume), un (count). |

Implementation rules derived from the above (no architectural change):

- Commands that are not plant-scoped require a company-wide assignment (`plant_id IS NULL`); plant-scoped commands accept a
  company-wide assignment or one for that plant.
- Segregation-of-duties checks are per company and serialized per user with an advisory lock.
- The outbox dispatcher processes companies one at a time so row-level security applies to it as to any command.
- `md.item.description` (NOT NULL) was added as a trivial field omitted by the frozen schema ("no necesito todavía cada campo trivial").
- Master rows use optimistic concurrency: every change increments `version` by exactly 1 (database guard) and commands carry `expected_version`.
