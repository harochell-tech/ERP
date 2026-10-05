# PRS-1 acceptance — price lists per customer and delivery freight

Baseline PRS-1 (`docs/architecture/prs1/frozen-baseline-prs1.md`) §4, with the approved errata E-PRC1-1…11, E-SRV1-1…19, E-PRS-01-1…8,
E-PRS-02-1…6, E-PRS-03-1…6, E-PRS-04-1…10 and E-PRS-05-1…7. Every acceptance ID has at least one test tagged
`[Trait("AcceptancePrs1", "<ID>")]`; `AcceptancePrs1TraceabilityTests` fails if an ID has no tagged test or is missing from this
matrix (E-PRS-05-7).

## Matrix

| ID | What | Tests |
| --- | --- | --- |
| PRC-01 | Today's list becomes «General» with every customer on it | `PriceListFreightSchemaTests.GENERAL_holds_the_list_of_today_and_every_customer_and_is_never_deactivated` |
| PRC-02 | «Hoteles» prepared by the Controller, approved by the Aprobador de políticas, beside GENERAL | `CustomerPriceListTests.PRC02_a_named_list_is_created_and_priced_by_the_Controller_and_approved_by_someone_else_beside_GENERAL` |
| PRC-03 | The customer's list applies only once the Controller approves its terms | `CustomerPriceListTests.PRC03_05_the_customers_list_prices_its_orders_once_approved_GENERAL_fills_the_gaps_and_a_product_in_neither_is_refused` |
| PRC-04 | BLOQUE-6 from «Hoteles», BLOQUE-8 from «General» | the same test as PRC-03 |
| PRC-05 | A product in no list is refused | the same test as PRC-03 |
| PRC-06 | An order keeps its prices when the list changes; a new one takes them | `CustomerPriceListTests.PRC06_an_order_keeps_its_prices_when_the_list_changes_and_a_new_order_takes_the_new_version` |
| PRC-07 | A quote below the customer's list price is special | `CustomerPriceListTests.PRC07_08_a_quote_below_the_customers_list_is_special_and_a_cash_sale_prices_from_GENERAL` |
| PRC-08 | A cash sale prices from «General» | the same test as PRC-07 |
| SRV-01 | The freight item is one service, never priced, costed or stocked | `FreightPriceTests.SRV01_the_freight_item_is_one_service_created_and_activated_like_an_item_and_never_priced_or_costed`; schema guards in `PriceListFreightSchemaTests` |
| SRV-02 | Zones by Controller or Crédito; an inactive one is not offered | `FreightPriceTests.SRV02_zones_are_kept_by_Controller_or_Credito_named_once_renamed_and_an_inactive_one_takes_no_freight` |
| SRV-03 | 1,000 BLOQUE-6 to Bávaro: freight 3,000.00, total 45,000.00 | `FreightFlowTests.SRV03_06_freight_comes_from_the_customers_own_list_for_the_zone_never_on_a_pickup_nor_an_exempt_order` |
| SRV-04 | Freight only from the customer's own list | the same test as SRV-03 |
| SRV-05 | A pickup has neither zone nor freight | the same test as SRV-03 |
| SRV-06 | An order with a pending exemption carries no freight | the same test as SRV-03; an authorization cannot cite an order with freight: `FreightFlowTests.A_CONFOTUR_authorization_cannot_cite_an_order_that_carries_freight` |
| SRV-07 | 600 delivered with POD: revenue 25,200.00, freight 1,800.00, no cost on freight; the conduce shows it | `FreightFlowTests.SRV07_08_10_12_freight_is_revenue_at_the_POD_an_exempt_invoice_line_creditable_and_the_month_reconciles` |
| SRV-08 | The invoice: 25,200.00 + ITBIS 4,536.00 + freight 1,800.00 exempt = 31,536.00 | the same test as SRV-07 |
| SRV-09 | A cash sale with our truck includes the freight without ITBIS | `FreightFlowTests.SRV09_a_cash_sale_with_our_truck_takes_GENERALs_freight_into_its_total_without_ITBIS` |
| SRV-10 | A credit note on the freight line: P-22, ITBIS 0 | the same test as SRV-07 |
| SRV-11 | An exempt (proforma) order carries no freight | `FreightFlowTests.SRV11_13_a_quote_with_a_zone_shows_the_freight_and_its_order_keeps_it_when_the_list_changes_and_an_exempt_order_has_none`; SRV-03's exempt order |
| SRV-12 | Reconciliations with freight balance | the same test as SRV-07 |
| SRV-13 | A quote with a zone carries its freight into the order | the same test as SRV-11 |
| E2E-PR1 | List → customer → order with freight → delivery → invoice, over the API and the UI | API: `Rochell.Api.Tests` · `PriceListAcceptanceTests.E2EPR1_customer_list_order_with_freight_delivery_and_invoice_with_exempt_freight_over_HTTP`; UI: `web/e2e/price-journey.spec.ts` (desktop and mobile) |

Tests without a project name are in `Rochell.Sales.Tests`. Also: deactivation of lists (`CustomerPriceListTests`), the freight table
(`FreightPriceTests`), the withheld reasons (`FreightFlowTests`).

## Open conditions

- **X-1** — the accountant confirms that the freight of cargo is exempt of ITBIS, with its norm; until SALES_ITBIS lists TRANSPORTE among
  its exempt categories, orders go without freight and say why.
- **A-01** — the Controller approves P-16 version 2, account 40500 «Ingresos por transporte» and its FREIGHT_REVENUE map, the lists and
  their freight prices.
