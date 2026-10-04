# Purchases of expenses and services (GAS-1)

Baseline: `docs/architecture/gas1/frozen-baseline-gas1.md` (E-GAS-1…12). Purchase orders and supplier invoices take expense lines —
a free description, a quantity, a price, an expense category and a tax type — that never go through the warehouse. Raw materials
keep their registered item, their receipt, the three-way match and their automatic ITBIS.

## GAS1-01 — schema (migration 0077; E-GAS-01-1…11)

Schema only: no command writes these columns yet, and every existing row is of class INVENTORY.

| Object | What |
| --- | --- |
| `pur.expense_category` | Code (unique among those not INACTIVE), name, `account_id`, `goods_type_606` ("01"…"11"), `line_class` (SERVICE / GOODS), status DRAFT → ACTIVE ⇄ INACTIVE, `prepared_by`, `approved_by`. The account is an ACTIVE account of class EXPENSE that is not a control account, and never changes; name, 606 type and class change only while DRAFT (E-GAS-01-3). Approver ≠ preparer (`core.four_eyes`, E-GAS-01-4). State history required (ADR-027). |
| `pur.purchase_order.doc_class` | INVENTORY or EXPENSE, immutable. An EXPENSE order is never `PARTIALLY_RECEIVED` / `RECEIVED`; it goes APPROVED ⇄ CLOSED (E-GAS-01-10). |
| `pur.purchase_order_line` | `item_id` and `uom` are now nullable; `description`, `expense_category_id`, `tax_rule_id`. A line is an inventory line (item and unit) or an expense line (description, category, tax type, never received, no unit — E-GAS-01-2), of the class of its order. `qty_invoiced` ≤ `qty_received` for inventory, ≤ `qty_ordered` for expenses. |
| `pur.supplier_invoice` | `doc_class` and `plant_id` (required for EXPENSE, absent for INVENTORY — E-GAS-01-8), both immutable. |
| `pur.supplier_invoice_line` | `line_kind` INVENTORY_PO or EXPENSE; `po_line_id` nullable; `description`, `expense_category_id`, `tax_rule_id`. A line is of the class of its invoice; with an order line, of the invoice's supplier and with the order line's own category and tax type. |
| `pur.expense_line_valid` | An expense line names an ACTIVE category and a fiscal rule of kind `PURCHASE_TAX_TYPE`. |
| Fiscal rule kind `PURCHASE_TAX_TYPE` | The tax type of an expense line (E-GAS-3); several in force at once. Effects `SELECTIVE_TAX`, `OTHER_TAX`, `LEGAL_TIP` on determination lines (E-GAS-10). The definition and the calculation come in GAS1-02. |
| Account roles | `SELECTIVE_TAX_EXPENSE`, `OTHER_TAX_EXPENSE`, `LEGAL_TIP_EXPENSE` (mapped by the Controller) and the technical `PURCHASE_EXPENSE` (never mapped: the account is the category's; excluded from the map lists and from the setup status). |
| Posting rule P-37 | `ExpenseInvoicePosted`, AP-REC, seeded DRAFT: Dr `PURCHASE_EXPENSE` (`expense_net`), Dr `ITBIS_RECOVERABLE`, Dr the three tax expense roles; Cr `AP_CONTROL` (`payable`, subledger AP), Cr `WITHHOLDING_PAYABLE`. Its reversal is the exact inverse of its journal, as R-07 is of R-04. |
| Policy parameter | `expense_invoice_approval_threshold` (PURCHASING, AMOUNT): from this total an expense invoice without an order needs another person's approval (E-GAS-01-9). |
| Permissions | `expense_category:prepare` (CONTADOR, CONTROLLER), `expense_category:approve` (CONTROLLER). 129 permissions. |

Tests: `Rochell.Procurement.Tests.ExpensePurchaseSchemaTests` (each case sets its rows up inside one block and rolls back with a
sentinel, since no command exists yet).

## GAS1-02 — tax types (no migration; E-GAS-02-1…7)

- **Definition** (`FiscalRuleDefinition`): `PURCHASE_TAX_TYPE` is `{"label", "components": [{"tax_code", "rate", "effect"}]}` with
  effects RECOVERABLE_INPUT, SELECTIVE_TAX, OTHER_TAX or LEGAL_TIP; tax codes unique within a type; no components = exempt.
  `PURCHASE_WITHHOLDING` takes an optional `"applies_to"` (INVENTORY, EXPENSE_SERVICE, EXPENSE_GOODS — `TaxLineScopes`).
- **Calculation** (`TaxCalculator`): a line with a tax type (`TaxableLine.TaxTypeRuleId`) gets that rule's components, each on the
  line's net (`Components`); a line with an item gets the PURCHASE_ITBIS rule as before. Withholdings apply by party type and by
  the line's scope; the ITBIS base of a withholding is the line's ITBIS, never its selective tax, other taxes or tip.
- **Engine** (`TaxEngine`): `TaxLineInput(SubjectLineId, ItemId?, NetAmount, TaxTypeRuleId?, ExpenseScope?)` — an item, or a tax
  type with its scope. Only the tax types the lines name are loaded: each must be ACTIVE on the date (otherwise
  `FISCAL_GATE_CLOSED`, naming it) and must exist (`TAX_SUBJECT_INVALID`). The purchase ITBIS rule is required only when there are
  lines with an item. The determination's inputs record `taxType` (the rule code) and `scope` for expense lines.
  `EstimateItbisAsync` estimates expense lines with their type.
- **Test runs**: a tax type's case gives the net and expects every component (`FiscalTestCase.Scope` serves withholding rules
  limited by scope).
- **Query** `ListPurchaseTaxTypes` (`GET /tax/purchase-tax-types?date=`, `master_data:read`): the types ACTIVE on the date with
  label and components.
- **Pack** `deploy/fiscal/tax-types-2026-10.json`: ITBIS_18, ITBIS_16, EXENTO, TELECOM (18 + 10 + 2), SEGUROS (ISC 16 %),
  CONSUMO_PROPINA (18 + 10), with their cases and four sources (`titulo3.pdf`, `titulo4.pdf`, `ley153-98.pdf`, `ley16-92.pdf` in
  `docs/fiscal/fuentes/`; the last three are the owner's to download). Loaded with `rochell-migrate load-fiscal-rules`
  (`configuration-load.md`); a person activates each type. Rates and bases are the accountant's to confirm (X-1).
- **Web**: Fiscal › Reglas fiscales names the kind and reads a tax type and a withholding's scope in words
  (`describeFiscalDefinition`); the guided form of a tax type comes with the expense screens (GAS1-07).

Tests: `Rochell.Tax.Tests.PurchaseTaxTypeTests`, `web/tests/unit/cashSales.test.ts`.

## GAS1-03 — expense categories and the configuration load (migration 0078; E-GAS-03-1…10)

- **Commands** (`Rochell.Procurement/Expenses`): `PrepareExpenseCategory` and `UpdateExpenseCategoryDraft` (`expense_category:prepare`),
  `ApproveExpenseCategories` (one or up to 500, `expense_category:approve`, step-up; approver ≠ preparer unless waived; every check
  before any write, so a refused category is reported SKIPPED and the others go on), `DeactivateExpenseCategory`
  (`expense_category:prepare`; also discards a draft), `ReactivateExpenseCategory` (`expense_category:approve`; only an approved
  category whose code is free). Errors `EXPENSE_CATEGORY_NOT_FOUND`, `EXPENSE_CATEGORY_INVALID`, `EXPENSE_CATEGORY_CODE_USED`,
  `EXPENSE_ACCOUNT_INVALID`. 200 commands.
- **Query** `ListExpenseCategories` (`GET /procurement/expense-categories?status=`, `master_data:read`) with the account's code and
  name and who prepared and approved.
- **Load** (migration 0078 gives «Carga de configuración» `account:manage` and `expense_category:prepare`):
  `rochell-migrate load-chart <rnc> deploy/chart/block-rochell-2026-10.json` (`ChartPackLoader`: CreateAccount per account; an
  existing code is EXISTS or DIFFERENT, never changed) and `rochell-migrate load-expense-categories <rnc>
  deploy/expenses/categories-block-rochell-2026-10.json` (`ExpenseCategoryPackLoader`: DRAFT categories; a missing account is
  ACCOUNT_MISSING). Exit code 3 when something was DIFFERENT, missing or refused.
- **Packs** generated from the owner's ADM Cloud export of 2026-10-02: 139 accounts (10 control; 35 rows left out, E-GAS-03-8) and
  37 categories.

Tests: `ExpenseCategoryTests` (prepare / correct / approve / take out of use / replace; the batch; both loads twice;
the missing account).

## GAS1-04 — the expense invoice without a purchase order (migration 0079; E-GAS-04-1…7)

- **Register** `RegisterExpenseInvoice` (`supplier_invoice:register`): an ACTIVE supplier, a free NCF, dates, the plant and 1–200 lines
  (description 1–200 characters, an ACTIVE category, a tax type in force on the invoice's date, quantity and price with up to 6
  decimals; net = round(qty × price, 2)). `total_amount` is the net, as for inventory invoices. 201 commands.
- **Match** (`MatchSupplierInvoice`): for an expense invoice the total with taxes (`TaxEngine.EstimateItbisAsync`, every component of
  the lines' types) against PURCHASING `expense_invoice_approval_threshold`: below → MATCHED, from it → MATCH_EXCEPTION, approved by
  `ApproveMatchException` (Controller ≠ registrar). No `pur.match_result` rows: there is no order to compare with.
- **Post** (`PostSupplierInvoice`): the determination (`TaxRequest` lines with tax type and the scope of the category's class), then
  P-37 — `P37-DR-EXP` per line with the category's account (`PostingLineInput.AccountId`; the engine takes the account of the role
  `PURCHASE_EXPENSE` from the document, never from a map, and requires an ACTIVE account that is not a control account), ITBIS,
  selective tax, other taxes, tip, AP (subledger the AP document) and withholdings. Event `ExpenseInvoicePosted`; the AP document's
  amount is net + taxes − withholdings.
- **Reverse** (`ReverseSupplierInvoice`, Controller, step-up): the exact reversal of the P-37 journal while the AP document is fully
  open. Migration 0079: a REVERSED invoice no longer holds its NCF (any supplier invoice).
- **Queries**: invoice lines of an expense invoice carry description, category and tax type (order and item null); the detail and the
  list carry `docClass`; the gross includes selective tax, other taxes and tip.
- **Staging**: before posting, the Controller approves P-37 and maps ITBIS_RECOVERABLE (14400), SELECTIVE_TAX_EXPENSE (63950),
  OTHER_TAX_EXPENSE (63960) and LEGAL_TIP_EXPENSE (63900); the PURCHASING policy needs a version with the approval amount.

Tests: `ExpenseInvoiceTests` (GAS-03…09, GAS-13 and the refusals), with hand-derived amounts.

## GAS1-05 — the expense purchase order (no migration; E-GAS-05-1…6)

- **Order**: `CreateExpensePurchaseOrder` / `UpdateExpensePurchaseOrderDraft` (`purchase_order:create`; ACTIVE supplier, 1–200 lines
  with an ACTIVE category and a tax type in force on the order date); `SubmitPurchaseOrder`, `ApprovePurchaseOrder`,
  `RejectPurchaseOrder` and `CancelPurchaseOrder` as for any order (cancel refuses an order an invoice bills);
  `CloseExpensePurchaseOrder` (`purchase_order:cancel`, APPROVED → CLOSED with a reason). `UpdatePurchaseOrderDraft` refuses an
  expense order. 204 commands.
- **Never received**: `PostGoodsReceipt` refuses it (`PURCHASE_ORDER_NOT_RECEIVABLE`) and `ListPurchaseOrdersToReceive` leaves it out.
- **Invoice against it**: `RegisterExpenseInvoice` with `PurchaseOrderId` — an APPROVED expense order of the supplier and the invoice's
  plant; every line names a different order line (`ExpenseLineInput.PurchaseOrderLineId`) with its category and tax type.
  `MatchSupplierInvoice` writes `pur.match_result` per line: quantity against `qty_ordered − qty_invoiced` (exceeding is never
  approvable), price within `match_price_tolerance_pct` or `match_amount_tolerance_abs`; no approval amount. Posting locks the order
  and its lines, re-checks the quantity, adds `qty_invoiced`, writes BILLS links and closes the order once complete; the reversal
  subtracts and reopens a CLOSED order with something left to bill (`PurchaseOrderReopened`).
- **Preview** `POST /procurement/expense-purchase-orders/preview` (`PreviewExpensePurchaseOrder`, `purchase_order:create`): net per
  line, the taxes of each line's type and the totals; null taxes with the reason when a type is not in force.
- **Queries**: order lines carry description, category and tax type (item and unit null); `openQuantity` of an expense line is what is
  still to bill; detail and list carry `docClass`.

Tests: `ExpensePurchaseOrderTests` (GAS-10, GAS-11, price exception, the order's rules for an invoice, the preview).

## Block Rochell's chart (A-01)

From the ADM Cloud export of 2026-10-02 (E-GAS-11, E-GAS-12). The Controller approves the categories and their accounts; the
accountant confirms the 606 types.

- Existing accounts that become categories: 63200 Electricidad, 63300 Teléfono, 63400 Suministros de oficina, 63500 Combustible
  (one category), 63600 Mantenimiento de oficina, 63700 Reparaciones, 63800 Servicios de limpieza, 64000 Seguros (606 type 11),
  64100 / 64200 / 64300 / 64900 servicios, 63100 / 63150 rentas (type 03), 62100 / 62200 / 62250 / 62500 mercadeo, 62300 viajes,
  62400 representación (type 05), 61200 / 60900 / 61100 / 60800 / 61000 / 60700 personal bought from third parties (type 01). The
  rest propose type 02.
- Tax accounts: 63950 Impuesto selectivo al consumo, 63900 Propinas.
- To create: 14400 ITBIS adelantado en compras, 63550 Peajes, 63960 Otros impuestos y tasas, and the plant and fleet accounts
  66100 Mantenimiento de maquinaria y planta, 66150 Repuestos de maquinaria, 66200 Lubricantes y grasas, 66250 Neumáticos,
  66300 Mantenimiento de vehículos, 66350 Fletes y acarreos contratados, 66400 Seguridad y vigilancia, 66450 Agua,
  66500 Herramientas menores, 66550 Equipos de protección personal (codes proposed; the accountant may change them).
