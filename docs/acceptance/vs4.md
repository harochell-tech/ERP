# VS#4 — e-CF gateway with Alanube: acceptance matrix

Baseline: `docs/architecture/vs4/frozen-baseline-vs4.md` §4 (ECF-01…10, E2E-ECF). Each test carries
`[Trait("AcceptanceVs4", "<ID>")]`; the browser journey is `web/e2e/ecf-journey.spec.ts`. Every automated test runs against the
simulated Alanube (E-VS4-02-6); the sandbox evidence is `docs/acceptance/vs4-contract-test.md` (CT-01…16, N-01).

| ID | Acceptance | Test | Notes |
| --- | --- | --- | --- |
| ECF-01 | An authorized 31 range; the invoice takes the next e-NCF, is sent and accepted; e-NCF, security code and QR on the invoice; XML and PDF kept | `Rochell.Sales.Tests.EcfIssuingTests.An_invoice_through_the_gateway_takes_the_next_eNCF_and_its_eCF_31_matches_it_to_the_cent` | The payload checked field by field against the invoice (E-VS4-03-11) |
| ECF-02 | A consumer invoice under the summary amount: e-CF 32 accepted in the same response | `Rochell.Sales.Tests.EcfIssuingTests.A_paid_cash_sale_is_an_eCF_32_naming_its_identified_buyer_paid_by_the_assigned_receipts` | One gateway step accepts it |
| ECF-03 | A commercial credit note: e-CF 34 citing the original with code 3 | `Rochell.Sales.Tests.EcfIssuingTests.A_credit_note_of_an_accepted_invoice_is_an_eCF_34_citing_it_with_code_3` | |
| ECF-04 | A CONFOTUR invoice: exempt e-CF 44 with the certification in the additional information | `Rochell.Sales.Tests.EcfIssuingTests.An_exempt_CONFOTUR_invoice_is_an_eCF_44_with_every_line_exempt_and_the_certification` | |
| ECF-05 | The DGII rejects: the invoice to correct with the reason; the number is not reused; the correction goes with another | `Rochell.Sales.Tests.EcfIssuingTests.A_rejected_invoice_is_resent_with_another_eNCF_once_its_data_is_corrected` | Also voided: `A_rejected_invoice_can_be_voided_and_a_sending_one_cannot` |
| ECF-06 | Timeout on sending: queried before resending; never two e-CF for one invoice | `Rochell.Tax.Tests.EcfGatewayTests.An_unknown_outcome_is_resolved_with_the_same_eNCF_and_never_a_second_one` | AP3011 names the document: adopted |
| ECF-07 | A webhook with a wrong header is ignored; with the right one it brings the query forward | `Rochell.Api.Tests.EcfGatewayApiTests.The_webhook_counts_only_with_the_secret_header_and_needs_no_CSRF_header` | Nudge: `A_webhook_nudge_brings_the_status_query_forward…` |
| ECF-08 | Alanube down: the invoice pending fiscal; contingency | `Rochell.Tax.Tests.EcfGatewayTests.Without_answers_past_the_policy_minutes_the_queue_goes_into_contingency_and_comes_back_by_itself` | E-VS4-04-5: no manual issuance while the e-CF is with Alanube |
| ECF-09 | A range exhausted or expired: no issuance; Inicio warns before | `Rochell.Tax.Tests.EcfGatewayTests.Each_issuance_takes_the_next_eNCF_and_the_last_one_closes_the_range` | Expired: `An_expired_range_refuses_the_issuance`; warning: `The_inbox_lists_by_status…ranges_running_out` |
| ECF-10 | A closed range with unused numbers: annulled through Alanube, approved by the Controller | `Rochell.Tax.Tests.EcfGatewayTests.ECF10_the_Controller_annuls_through_Alanube_the_unused_tail_of_a_closed_range_and_the_numbers_never_issued` | |
| E2E-ECF | Invoice → e-CF accepted → e-mail with the PDF and the QR | `web/e2e/ecf-journey.spec.ts` and `Rochell.Sales.Tests.EcfIssuingTests.An_accepted_invoice_is_emailed_with_the_PDF_carrying_the_QR_and_the_signed_XML_and_a_sending_one_is_not` | The sandbox run is CT-01…16 |

## Going to Production (E-VS4-05-5/6)

Staging becomes production on 2026-11-01; the gateway's PRODUCTION mode is switched on on the same server, in this order:

1. The contract test in the sandbox, its table signed by the owner (`vs4-contract-test.md`, N-01).
2. Alanube's production token in `secrets/ecf/alanube-token` on the server (`staging.md` › e-CF gateway).
3. The production ranges registered on Fiscal › Rangos e-NCF from the first number the current provider did not use, per type
   (31, 32, 34, 44), approved by the Controller; the sandbox ranges closed.
4. `ECF_MODE=PRODUCTION` and `ECF_BASE_URL=https://api.alanube.co/dom/v1/` in the GitHub Environment, only on the owner's explicit
   order, then `deploy-staging`.
