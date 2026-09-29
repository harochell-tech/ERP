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

## FIS1-02 — authorization commands, queries and the proforma (E-FIS1-02-1…10)

| Command | Permission | Rules |
| --- | --- | --- |
| `RegisterFiscalAuthorization`, `UpdateDraftAuthorization` | `fiscal_authorization:register` (Crédito, Facturación) | DRAFT; ACTIVE customer with RNC; unique certificate; scope of ACTIVE finished goods (base unit or convertible), qty > 0, net > 0 |
| `AttachAuthorizationDocument` | `:register` | Kind, reference, SHA-256; any non-terminal state |
| `SubmitForVerification` | `:register` | Needs a scope line and the CERTIFICADO_DGII document |
| `VerifyAuthorization` | `:verify` + step-up (Especialista fiscal) | ≠ registrar; not past `valid_until` → ACTIVE |
| `ReturnAuthorizationToDraft`, `RejectAuthorization` | `:verify` | Reason |
| `SuspendAuthorization`, `ReactivateAuthorization` | `:suspend` + step-up | Reason; reactivation not past `valid_until` |

Queries (`sales:read`): `GET /tax/fiscal-authorizations`, `/fiscal-authorizations/{id}`, `/proformas/{salesOrderId}` (ITBIS at
the rules in force today through `TaxEngine.PreviewSalesItbisAsync`, never stored).

## FIS1-03 — exempt e-CF 44 invoices (migration 0055, E-FIS1-03-1…10)

`CreateInvoiceFromDeliveries` with `fiscalAuthorizationId` → e-CF 44, every line covered (product × unit, quantity and net
available) by an ACTIVE, in-force authorization of the customer; otherwise invoice apart with ITBIS. `IssueInvoice` locks the
authorization, determines taxes with `TaxRequest.Exemption` (no ITBIS; inputs record the certificate), posts P-18 without its ITBIS
line, and consumes the scope (EXHAUSTED when used up). `VoidUnfiscalizedInvoice` returns the consumption; a credit note of an e-CF 44
(e-CF 34, ITBIS 0) returns the net it credits (never units). The fiscal package carries the exemption (regime, certificate, project,
billing indicator 4). Shared code: `Rochell.Tax.Authorizations.AuthorizationUsage` (`CoverAsync`, `ConsumeAsync`, `ReleaseAsync`).
