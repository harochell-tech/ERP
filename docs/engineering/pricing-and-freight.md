# Price lists per customer and delivery freight (PRS-1)

Baseline `docs/architecture/prs1/frozen-baseline-prs1.md`; approved errata E-PRC1-1…11, E-SRV1-1…19 and the per-PR rows below.

## PRS-01 — schema (migration 0082; E-PRS-01-1…8)

| Object | What it holds | Guarantees |
| --- | --- | --- |
| `sal.price_list` | Named lists (code, name, ACTIVE / INACTIVE) | GENERAL is created for every company (and on first use by `sal.general_price_list(company)`), never inactive; a list goes inactive only when no customer terms in force or pending name it; never deleted |
| `sal.price_list_version.price_list_id` | The list a version belongs to | Today's versions belong to GENERAL; a version without a list is GENERAL's; versions are numbered per list; one ACTIVE version per list; a new version needs an ACTIVE list |
| `sal.customer_terms_version.price_list_id` | The customer's list (E-PRC1-6) | GENERAL when not given (E-PRS-01-8); changes only while the terms are DRAFT; must be ACTIVE |
| `sal.delivery_zone` | Delivery zones (E-SRV1-9) | Name unique without case, ACTIVE ⇄ INACTIVE, never deleted; `delivery_zone:manage` (CONTROLLER, CREDITO; 130 permissions) |
| `md.item` | Item type SERVICE, category TRANSPORTE | One TRANSPORTE item per company; the finished-good guards of lists, costs, recipes, opening stock, orders and quotes refuse it |
| `sal.price_list_freight` | Freight per (version, product, unit, zone) | Only into a DRAFT version, finished goods only, price > 0, never changed |
| `sal.sales_order.delivery_zone_id`, `sal.quote.delivery_zone_id` | The zone of an own-truck document | Only with `DELIVERED_OWN_TRANSPORT`; ACTIVE when set; changes only while DRAFT |
| `sal.sales_order_line` `price_list_version_id`, `freight_unit_price`, `freight_amount`; `sal.quote_line` freight columns | The product price's list version; the freight on the product's own line (E-PRS-01-1) | Freight both set or both empty, positive, amount to 2 decimals; only on a document with a zone and, for orders, without a pending exemption. `total_net` includes the freight |
| `sal.invoice_line.line_kind`, `sal.proforma_line.line_kind` | PRODUCT (default) or FREIGHT (E-PRS-01-2) | One line of each kind per delivery line; a PRODUCT line is a finished good, a FREIGHT line the service item; a FREIGHT proforma line has no ITBIS |
| `fin.account_role` FREIGHT_REVENUE | «Ingresos por transporte» (E-SRV1-18) | Not a control role; mapped by the Controller |

`FiscalRuleDefinition.ItemCategories` accepts TRANSPORTE, so SALES_ITBIS can list it among its exempt categories (E-SRV1-16).

Tests: `PriceListFreightSchemaTests` (Sales).

## PRS-02 — price lists per customer (migration 0083; E-PRS-02-1…6)

| Command / query | Permission | What it does |
| --- | --- | --- |
| `CreatePriceList` (code, name) | `price_list:prepare` | A named list, ACTIVE; priced by preparing its first version |
| `DeactivatePriceList` / `ReactivatePriceList` | `price_list:prepare` | Out of use and back; never GENERAL (`PRICE_LIST_GENERAL`), never a list in customers' terms in force or pending (`PRICE_LIST_IN_USE`) |
| `PreparePriceList` + `PriceListId` | `price_list:prepare` | A DRAFT version of that list (GENERAL when omitted), numbered per list; an inactive list is refused (`PRICE_LIST_INACTIVE`) |
| `ApprovePriceList` | `price_list:approve` | Supersedes only the version in force of the same list |
| `PrepareCustomerTerms` + `PriceListId` | `customer_terms:prepare` | The customer's list in the terms: the one given (ACTIVE), else the one the customer already has, else GENERAL |
| `ListPriceListHeaders` (GET `/sales/price-list-headers`) | `sales:read` | Lists with status, version in force, pending draft and customers on each |
| `ListPriceLists` (+ `priceListId`), `GetPriceList` | `sales:read` | Versions carry their list's id, code and name |

Pricing (`Pricing/CustomerPrices.cs`): orders, quotes and their previews take each (item, unit) from the version in force of the
customer's list (from the terms in force), else GENERAL's; neither refuses with `PRICE_MISSING`. A customer whose list has no
version in force, a customer without terms and every cash sale price from GENERAL. The order / quote header records the customer's
version (GENERAL's when it has none); each order and quote line records the version its price came from (migration 0083 adds
`sal.quote_line.price_list_version_id`); a quoted price on an order has none. Lines read `priceListCode` / `priceListName`, and an
order line `quotedPrice`; customer terms read `priceListId` / `priceListCode` / `priceListName`. The previews take `partyId`.
207 commands.

Tests: `CustomerPriceListTests` (PRC-02…08 and deactivation), `PriceListFreightSchemaTests` (PRC-01).
