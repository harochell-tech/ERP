# Configuration (UX2, E-UX2-1…13)

Wave 2 of the UX audit makes the configuration readable and guided. UX2-01 is the server side (migration
`0062__configuration_ux.sql`); UX2-02 the screens.

## Readable catalogues (E-UX2-2, 5, 12)

- `acc.accounting_policy.name` and, per parameter, `acc.policy_parameter_definition.label`, `unit` (PERCENT, AMOUNT, DAYS,
  HOURS, OPTION; MINUTES since 0096), `example` (as shown on screen) and `affects`. The catalogues stay immutable: 0062 fills them with their
  triggers disabled for its own transaction. A migration that adds a parameter fills the four texts too
  (`ConfigurationUxTests.Every_policy_parameter_and_account_role_carries_its_screen_texts`).
- `Finance.ListAccountingPolicies` returns the texts, the policy name and the roles holding `accounting_policy:prepare` /
  `:approve` (SUPERADMIN left out).
- `fin.account_role.name`; `Finance.ListAccountRoles` (GET `/finance/account-roles`, `configuration:read`) lists the roles
  but MANUAL_ADJUSTMENT, with `usedByActiveRule` (a line of an ACTIVE posting rule version posts to it) and `mappedToday`
  (an ACTIVE mapping covers today's business date, any item category). `ListAccountRoleMaps` adds `accountRoleName`.
- `ListPostingRules` adds each version's `lines`: side, account role and name, amount source and the Spanish explanation
  template (with its `{placeholders}`), in definition order.
- `iam.role.description`; `Identity.ListRoles` (GET `/identity/roles`, `iam:read`) lists the roles people hold (not
  PROCESO_DIARIO nor PROBADOR) with description and permissions.

## Missing policy parameter (E-UX2-4)

`ResolvedPolicy` answers `POSTING_PREREQUISITE_MISSING` naming the parameter when the ACTIVE version predates it (a later
migration added it); the command rolls back and the screen asks for a new version.

## Account role maps on screen (E-UX2-6 (b))

`Finance.PrepareAccountRoleMap(AccountRole, ItemCategory?, AccountId, EffectiveFrom)` — permission
`account_role_map:prepare` (CONTADOR, CONTROLLER) — inserts a DRAFT mapping (`AccountRoleMapPrepared`, state DRAFT).
It refuses MANUAL_ADJUSTMENT and unknown roles or categories (`ACCOUNT_ROLE_INVALID`) and an inactive account or a
control / regular mismatch (`MAP_ACCOUNT_INVALID`). `ApproveAccountRoleMap` is unchanged (four eyes, step-up) except that its
event is numbered after the ones already recorded. The CLI import stays.

## Company and plants (E-UX2-11)

`MasterData.UpdateCompanyLegalName` and `MasterData.UpdatePlantName` (`company:manage`: CONTROLLER; step-up) change the legal
name (1–200 characters) and a plant's name (1–100), each with an event (`CompanyLegalNameChanged`, `PlantNameChanged`,
payload with the previous value). `md.company_guard` keeps the RNC and the id and refuses deletes. `MasterData.GetCompany`
(GET `/master-data/company`, `configuration:read`) returns RNC, legal name and plants.

## Setup status (E-UX2-9/10)

`Reconciliation.GetSetupStatus` (GET `/reconciliation/setup-status`, `configuration:read`) computes 19 steps in SQL:

| # | Code | Done when | `missing` lists |
| --- | --- | --- | --- |
| 1 | COMPANY | always | — |
| 2 | PLANTS | every plant has a name | unnamed plant codes |
| 3 | USERS | CONTROLLER, ADMIN_SEGURIDAD and SEGUNDO_APROBADOR_SEGURIDAD are held | missing role codes |
| 4 | PERIODS | a period covers today | TODAY |
| 5 | ACCOUNTS | every active account has a class | account codes |
| 6 | REPORT_STRUCTURES | an ACTIVE balance sheet and income statement structure | report codes |
| 7 | ACCOUNT_MAPS | every role an ACTIVE rule posts to is mapped today | role codes |
| 8 | POSTING_RULES | every rule has an ACTIVE version covering today | rule codes |
| 9 | POLICIES | every policy has an ACTIVE version covering today with every parameter | policy codes |
| 10 | FISCAL_SOURCES | a source of the deployment environment | SOURCE |
| 11 | FISCAL_RULES | an ACTIVE rule of each kind | rule kinds |
| 12 | BANK_ACCOUNTS | an ACTIVE bank account | BANK_ACCOUNT |
| 13 | SUPPLIERS | an ACTIVE supplier | SUPPLIER |
| 14 | ITEMS | an ACTIVE raw material and finished good | item types |
| 15 | STANDARD_COSTS | every active finished good has an ACTIVE standard cost | item codes |
| 16 | PRICE_LIST | an ACTIVE price list | PRICE_LIST |
| 17 | CUSTOMERS | an ACTIVE customer, each with ACTIVE terms | customer names |
| 18 | RECIPES | every active finished good has an ACTIVE recipe | item codes |
| 19 | OPENING_INVENTORY | a POSTED opening inventory batch | BATCH |

A step is DONE with nothing missing, WARNING when something is missing but the area was started, PENDING otherwise;
`complete` is true when all 19 are DONE.
