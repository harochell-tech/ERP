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
