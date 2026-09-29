# Fiscal authorizations — CONFOTUR exempt sales (FIS-1)

Frozen Baseline `docs/architecture/fis1/frozen-baseline-fis1.md` (E-FIS1-1…16). This page records what each merged PR built.

## FIS1-01 — schema (migration 0054, E-FIS1-01-1…10)

| Table | Rules |
| --- | --- |
| `tax.fiscal_authorization` | One per DGII exemption certificate (unique number per company); customer with an RNC; regime CONFOTUR; lifecycle DRAFT → PENDING_VERIFICATION → ACTIVE ⇄ SUSPENDED / EXHAUSTED → EXPIRED, or REJECTED; verifier ≠ registrar; data frozen once submitted; `core.state_history` for every status |
| `tax.fiscal_authorization_line` | Scope: finished good × sale unit, authorized quantity and net; consumed quantity and net between 0 and the authorized; lines only in DRAFT |
| `tax.fiscal_authorization_document` | CERTIFICADO_DGII, RESOLUCION_CONFOTUR, LISTA_MATERIALES, PROFORMA with reference and SHA-256; immutable |
| `tax.fiscal_authorization_consumption` | One row per exempt invoice line; releases are inverse rows |

`sal.invoice.fiscal_authorization_id`: e-CF 44 exactly when an authorization is set, and then no ITBIS. Permissions
`fiscal_authorization:register` (Crédito, Facturación), `:verify` and `:suspend` (Especialista fiscal, who also reads sales).
