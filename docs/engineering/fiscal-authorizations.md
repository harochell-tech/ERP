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

## FIS1-04 — reconciliations and expiry (migration 0056, E-FIS1-04-1…6)

| Reconciliation | Severity | Finds |
| --- | --- | --- |
| AUTH-CONSUMPTION | ERROR, blocks AR-REC | `AUTH_LINE_CONSUMPTION_DIFFERENCE`: a scope line's consumed totals ≠ consumptions − releases. `INVOICE_CONSUMPTION_DIFFERENCE`: an issued e-CF 44 line's live consumption ≠ its net − issued credit notes (0 once voided). |
| EXEMPT-WITHOUT-AUTH | ERROR, blocks AR-REC | `EXEMPT_WITHOUT_AUTHORIZATION`: an issued invoice with ITBIS 0 that is not an e-CF 44 and has an item its SALES_ITBIS rule taxes. |
| AUTH-EXPIRY | WARNING | `AUTHORIZATION_EXPIRING` (ACTIVE / EXHAUSTED / SUSPENDED, `valid_until` within `authorization_expiry_alert_days` of the cutoff) and `PROJECT_TERM_ENDED`. |

The threshold is the REVENUE_ACCOUNTING parameter `authorization_expiry_alert_days`; without it AUTH-EXPIRY is FAILED only when the
company has authorizations. `ExpireFiscalAuthorizations` (`POST /tax/expire-fiscal-authorizations`, `fiscal_authorization:suspend`,
no step-up) moves every ACTIVE, SUSPENDED or EXHAUSTED authorization past `valid_until` to EXPIRED (event
`FiscalAuthorizationExpired`), locking rows in id order; a second run finds nothing. The API runs it daily at 00:30 as the daily process
(E-FIS1-04-7, `identity.md`, `api.md`; on in Staging). Tests: `FiscalAuthorizationReconciliationTests`
(FIS-09), `ServiceSessionTests`, `BackgroundServiceTests` (the daily run).

## FIS1-05 — screens and end to end (E-FIS1-05-1…12)

Screens (Fiscal › Autorizaciones fiscales, the proforma, exempt invoicing from "Por facturar", the e-CF 44 invoice): `web.md`. The
detail query now returns `consumptions` — each consumption at an invoice's issue and each release (void, credit note), with the
invoice number. The dev seed adds an ACTIVE authorization `CERT-DEV-0001` of the sample customer (500 blocks / 25,000.00). E2E-F1:
`FiscalAcceptanceTests` over the API and `web/e2e/fiscal-journey.spec.ts`; acceptance matrix `docs/acceptance/fis1.md`
(`AcceptanceFis1TraceabilityTests`).
