# FIS-1 acceptance — CONFOTUR fiscal authorizations and exempt sales

Baseline FIS-1 (`docs/architecture/fis1/frozen-baseline-fis1.md`) §8, with the approved errata E-FIS1-1…16 and E-FIS1-01…05.
Every acceptance ID has at least one test tagged `[Trait("AcceptanceFis1", "<ID>")]`; `AcceptanceFis1TraceabilityTests` fails if
an ID has no tagged test or is missing from this matrix (E-FIS1-05-11).

## Matrix

| ID | What | Tests |
| --- | --- | --- |
| FIS-01 | Registered by Crédito / Facturación, the registrar cannot verify; the Especialista fiscal verifies → ACTIVE | `FiscalAuthorizationTests.FIS01_an_authorization_is_registered_with_its_certificate_and_verified_by_the_fiscal_specialist` |
| FIS-02 | Exempt invoice of 600 blocks: e-CF 44, ITBIS 0, P-18 without ITBIS, 600 / 30,000.00 consumed | `ExemptInvoiceTests.FIS02_an_exempt_invoice_is_an_eCF_44_without_ITBIS_that_consumes_the_authorization` |
| FIS-03 | Beyond the scope: refused; the sale can be invoiced with ITBIS | `ExemptInvoiceTests.FIS03_04_05_beyond_the_scope_out_of_it_or_while_suspended_the_invoice_carries_ITBIS` |
| FIS-04 | Another customer's or another product's authorization: refused | `ExemptInvoiceTests.FIS03_04_05_beyond_the_scope_out_of_it_or_while_suspended_the_invoice_carries_ITBIS` |
| FIS-05 | SUSPENDED or expired: refused; issued invoices unchanged | `ExemptInvoiceTests.FIS03_04_05_beyond_the_scope_out_of_it_or_while_suspended_the_invoice_carries_ITBIS` |
| FIS-06 | Credit note of an exempt invoice: no ITBIS, the consumption returns, EXHAUSTED → ACTIVE | `ExemptInvoiceTests.FIS06_08_the_eCF_44_is_recorded_as_E44_and_a_void_or_a_credit_note_returns_the_consumption` |
| FIS-07 | Two exempt invoices at once for the whole balance: one consumes | `ExemptInvoiceTests.FIS07_two_exempt_invoices_issued_at_once_for_the_whole_balance_consume_it_once` |
| FIS-08 | External e-CF E44 with equal totals → ACCEPTED_EXTERNAL; an E31 for a 44 invoice is refused | `ExemptInvoiceTests.FIS06_08_the_eCF_44_is_recorded_as_E44_and_a_void_or_a_credit_note_returns_the_consumption` |
| FIS-09 | A month with exempt sales: AUTH-CONSUMPTION and EXEMPT-WITHOUT-AUTH MATCHED, AR-REC closes | `FiscalAuthorizationReconciliationTests.FIS09_a_month_with_exempt_sales_reconciles_and_AR_REC_closes`; tampering blocks the close: `FiscalAuthorizationReconciliationTests.Tampered_consumption_and_an_invoice_without_ITBIS_outside_eCF_44_are_found_and_block_the_AR_REC_close` |
| E2E-F1 | Proforma → registration → verification → delivery → e-CF 44 invoice → credit note, over the API and the UI | API: `Rochell.Api.Tests` · `FiscalAcceptanceTests.E2EF1_proforma_authorization_verification_delivery_exempt_eCF_44_and_credit_note_over_HTTP`. UI: `web/e2e/fiscal-journey.spec.ts` (Playwright) |

Tests without a project name are in `Rochell.Sales.Tests`. Also: expiry (`FiscalAuthorizationReconciliationTests.AUTH_EXPIRY_warns_ahead_and_the_expiry_command_moves_lapsed_authorizations_to_EXPIRED`), the
daily run as PROCESO_DIARIO (`BackgroundServiceTests`, `ServiceSessionTests`).

## Open conditions

- **X-1** — the accountant confirms the open questions (certificate validity, substitutes, 607 / IT-1, the certificate number in
  `InformacionAdicionalComprador`).
- **X-2** — an anonymized real DGII certificate and CONFOTUR resolution to adjust the fields.
- **A-01** — the Controller approves `authorization_expiry_alert_days` (REVENUE_ACCOUNTING).
- **B-02** and **no real data** until B-02 or a zero-difference parallel run (E-VS1-2).
