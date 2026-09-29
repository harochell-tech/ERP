# QUO-1 acceptance — sales quotations

Baseline QUO-1 (`docs/architecture/quo1/frozen-baseline-quo1.md`) §6, with the approved errata E-QUO1-1…14 and E-QUO1-01…04.
Every acceptance ID has at least one test tagged `[Trait("AcceptanceQuo1", "<ID>")]`; `AcceptanceQuo1TraceabilityTests` fails if
an ID has no tagged test or is missing from this matrix (E-QUO1-04-9).

## Matrix

| ID | What | Tests |
| --- | --- | --- |
| QUO-01 | 1,000 blocks at the list price: COT-000001 DRAFT, 50.00, net 50,000.00, informative ITBIS 9,000.00 | `QuoteTests.QUO01_a_quote_is_priced_from_the_list_in_force_and_prints_with_informative_ITBIS` |
| QUO-02 | A line at 45.00 is not sent until the policy approver (not the Vendedor) approves the current lines | `QuoteTests.QUO02_a_price_below_the_list_is_sent_only_after_the_policy_approver_approves_the_current_lines`; guards: `QuoteSchemaTests` |
| QUO-03 | A sent quote is frozen; CopyQuote makes a new DRAFT with the same lines | `QuoteTests.QUO03_a_sent_quote_is_frozen_and_a_copy_carries_its_lines_and_prices_into_a_new_draft` |
| QUO-04 | Conversion: DRAFT order at the quoted prices with `quote_id`, quote CONVERTED, the order goes through credit | `QuoteTests.QUO04_a_sent_quote_becomes_a_draft_order_at_its_quoted_prices_that_keeps_them_and_goes_through_credit` |
| QUO-05 | An expired quote, or one of a customer not yet ACTIVE, is not converted | `QuoteTests.QUO05_an_expired_quote_or_one_of_a_customer_not_yet_active_is_not_converted` |
| QUO-06 | Two conversions at once: one order | `QuoteTests.QUO06_two_conversions_at_once_create_one_order`; schema: `QuoteSchemaTests.A_quote_becomes_at_most_one_order_and_the_link_never_changes` |
| QUO-07 | Lost only with a reason | `QuoteTests.QUO07_a_sent_quote_is_lost_only_with_a_reason_and_an_expired_one_is_listed_and_not_sent` |
| E2E-Q1 | Quote → special price approved → sent → printed → converted → order confirmed by credit, over the API and the UI | API: `Rochell.Api.Tests` · `QuoteAcceptanceTests.E2EQ1_quote_special_price_approved_sent_printed_converted_and_the_order_confirmed_over_HTTP`. UI: `web/e2e/quote-journey.spec.ts` (Playwright) |

Tests without a project name are in `Rochell.Sales.Tests`.

## Open conditions

- **X-Q1** — the printed format (logo, general conditions, legal text) from the owner.
- **A-01** — if a threshold for special prices is wanted later, it becomes a policy parameter (E-QUO1-3).
- **No real data** until B-02 or a zero-difference parallel run (E-VS1-2).
