# Web UI (PR-18b)

`web/` is the minimal Spanish UI of VS#1 (E-PR18-6, E-PR18b-1…11): Next.js 16 (App Router), React 19, strict TypeScript,
plain CSS, npm. It adds transport and presentation only; every rule stays in the API.

## How it is served (E-PR18b-2)

`next build` produces a **static export** (`web/out`, `output: 'export'`, `trailingSlash: true`): client-side pages only, no
Node server in production. The API host serves it from its own origin when `Rochell:WebRoot` points at the export, so the
browser talks to one origin: no CORS, and the SameSite=Strict session cookie works as is. Pages get a restrictive
Content-Security-Policy (`frame-ancestors 'none'`, everything limited to `'self'`; inline scripts are allowed because the export
bootstraps with them) and unknown paths the export's `404.html`. Detail pages take their id as a query parameter
(`/compras/orden/?id=…`) because a static export has no dynamic routes.

## Talking to the API (E-PR18b-3, 4, 6, 11)

- `src/api/schema.d.ts` is generated from `src/Rochell.Api/openapi.json` (`npm run gen:api`); `npm run check:api` (CI) fails when
  it is out of date. `src/api/client.ts` types every call from it: a command body or a query result that does not match the
  API does not compile.
- Decimals stay strings end to end (`src/lib/decimal.ts`): inputs are validated by format and scale, display only groups digits
  and trims trailing zeros. **No money or quantity arithmetic happens in the browser**; totals shown come from the API.
- Every form creates its `Idempotency-Key` when it opens (`useCommand`) and keeps it until the command succeeds; every POST sends
  `X-Rochell-Csrf: 1`.
- On `STEP_UP_REQUIRED` the form values and the key go to `sessionStorage`, the user re-authenticates (`/api/v1/auth/step-up`,
  `prompt=login`) and comes back to the filled form; they press the button again (no automatic resubmission), with the same key.
- Errors are shown by code from a Spanish dictionary (`src/lib/errors.ts`, checked by a unit test against the codes declared in
  `src/`); unknown codes show the API message and the correlation id.

## Who sees what (E-PR18b-5, 7, 8)

Navigation and buttons follow `/api/v1/session`: each assignment lists its permissions, company-wide or for one plant.
A plant-scoped user works in its plant (header selector; sent as `plantId`); a company-wide user picks the plant in the form.
`accounting_status` is shown to everyone and never changed from the UI (E-11); the link to the journals and Explain appears only
with `audit:read`.

| Area | Screens | Commands |
| --- | --- | --- |
| Compras | `/compras/ordenes/`, `/compras/ordenes/nueva/`, `/compras/orden/?id=` | create, submit, approve, reject, cancel (reject/cancel: E-PR18b-7) |
| Almacén | `/almacen/recepciones/`, `/almacen/recibir/?oc=`, `/almacen/recepcion/?id=`, `/almacen/correcciones/` | post receipt, reverse (Controller), create correction, approve/reject correction (Controller) |
| Cuentas por pagar | `/cxp/facturas/`, `/cxp/facturas/nueva/`, `/cxp/factura/?id=` | register, match, approve exception (Controller), post, reverse (Controller) |
| Cierre | `/cierre/conciliaciones/`, `/cierre/conciliacion/?id=`, `/cierre/periodos/` | run reconciliations, close component, request reopen, approve/reject reopen (second approver) |
| Auditoría | `/auditoria/asientos/?evento=`, `/auditoria/explicar/?entrada=` | — (EX-01) |
| Tesorería (VS2-08) | `/tesoreria/propuesta/`, `/tesoreria/pagos/`, `/tesoreria/pago/?id=`, `/tesoreria/extractos/`, `/tesoreria/conciliacion/?cuenta=&extracto=` | prepare (from the proposal), release (Controller, never the preparer), void, reverse (reason ≥ 10), import statement, match (suggested or picked; a CREDIT line only as a return), unmatch (Controller, reason), recognize charge (Controller) |
| Cuentas por pagar (VS2-08) | `/cxp/antiguedad/` | — (AP aging by the TREASURY policy buckets) |
| Auditoría (UI-01) | `/auditoria/verificar/`, `/auditoria/digests/` | verify hash chain (`hash:verify`; "not available" without WORM) |
| Seguridad (UI-01) | `/seguridad/usuarios/`, `/seguridad/solicitudes/` | request role assignment / revocation (`role:assign` / `role:revoke`), approve (step-up) or reject with a reason (`role:second_approve`) |
| Maestros / Contabilidad (UI-01) | `/maestros/plantas/`, `/contabilidad/cuentas/` | — (read only) |
| Maestros (VS2-08) | `/maestros/cuentas-bancarias/`, `/maestros/proveedor/?id=` | register / close a company bank account (Controller); request a supplier account (Tesorero), verify with evidence ≥ 20 or reject (Controller, not the requester) |

Master data has no screens in VS#1 (API only).

## Design and navigation (E-UI-1…6)

The approved design canvas "Rochell Core — Diseño de pantallas" (https://claude.ai/artifact/BqCTac3RWvcuK4va8D3cYW) sets the look:
IBM Plex Sans and Mono bundled with `@fontsource` (no external font service, E-UI-6), a toned neutral ground, one blue accent and
status badges whose colour always comes with the label (`StatusBadge`, `statusTone` in `labels.ts`, E-UI-5: CLEARED "Compensado",
a matched statement line "Conciliada"). The side menu (`Shell.tsx`, `NAV`) groups the screens in areas — Maestros, Compras,
Almacén, Cuentas por pagar, Tesorería, Contabilidad, Fiscal, Cierre — each item shown only with its read permission and each area
only with a visible item (E-UI-1); detail pages light up their list's item. Auditoría and Seguridad came with UI-01 (E-UI01-1). Money is shown with `Money` (a decimal string, grouped, never computed); a payment's total is the server's (E-UI-3).

## Configuration screens (E-B03-15)

Spanish, no visual design work (as E-PR18-6); each appears only with its permission and each action only with the command's
permission. Self-approval is not offered (the database refuses it anyway).

| Screen | Reads | Actions |
| --- | --- | --- |
| Proveedores `/maestros/proveedores/` | `master_data:read` | create, edit (`supplier:create` / `update`), activate (`supplier:activate`, step-up) |
| Materias primas `/maestros/articulos/` | `master_data:read` | create (`item:create`), unit conversion and activate (`item:activate`) |
| Mapas de cuentas `/contabilidad/mapas/` | `configuration:read` | approve DRAFT maps (`account_role_map:approve`); maps are loaded with `import-account-map` (E-B03-15-2) |
| Reglas contables `/contabilidad/reglas/` | `configuration:read` | approve DRAFT rule versions (`posting_rule:approve`) |
| Políticas `/contabilidad/politicas/` | `configuration:read` | prepare a version with every parameter (`accounting_policy:prepare`), approve (`accounting_policy:approve`) |
| Fuentes fiscales `/fiscal/fuentes/` | `configuration:read` | register (`fiscal_rule_source:register`); the SHA-256 is computed in the browser, the file is not uploaded (E-B03-15-3) |
| Reglas fiscales `/fiscal/reglas/` | `configuration:read` | configure from a JSON template, link a source, run regression cases (`fiscal_rule:configure`), activate (`fiscal_rule:activate`) |

## Accounting screens (FIN1-04, E-FIN1-04-1…11)

Menu **Contabilidad**: Diario de ajustes, Balanza, Mayor, Estados financieros (`ledger:read`); Catálogo de cuentas, Estructuras
de reporte, Mapas, Reglas, Políticas (`configuration:read`, which the Contador also holds — migration 0036).

| Page | What it does |
| --- | --- |
| `/contabilidad/ajustes/`, `/contabilidad/ajustes/nuevo/[?id=]`, `/contabilidad/ajuste/?id=` | List by status; form (active non-control accounts, debit or credit, plant, memo, ACR-TAX and auto-reverse checkboxes); the support file is hashed in the browser (SHA-256, not uploaded); the detail shows the server's totals and difference, "Enviar" only at 0.00, "Aprobar y contabilizar" / "Rechazar" for the approver who did not prepare it, "Reversar ajuste" with a reason |
| `/contabilidad/balanza/` | Range (current month by default), plant / supplier / bank filters with a "filtered" notice, "Cuadra" badge, CSV download, each account opens its ledger |
| `/contabilidad/mayor/?cuenta&desde&hasta` | Opening, movements with document, running balance, pages of 100, CSV, "Explicar" for `audit:read` |
| `/contabilidad/estados/` | Tabs Balance general / Estado de resultados; indented lines with expandable accounts, totals, "Cuadra" or the difference, CSV; missing classes or structure show where to fix them |
| `/contabilidad/cuentas/` | Class and status, "Solo cuentas sin clase"; the Controller creates, edits name and class, deactivates and activates |
| `/contabilidad/estructuras/`, `/contabilidad/estructura/?id=`, `/contabilidad/estructuras/nueva/?reporte[&desde]` | Versions; lines with accounts and the missing accounts; approve (Aprobador de políticas); editor that copies the active (or given) version, lines on top and each account's line below |

CSV links are plain `GET …?format=csv` (the session cookie goes with them). The dev stack classes every account by its first
digit and seeds approved structures and a Contador.

## Sales screens (VS3-10a, E-VS3-10-1…13)

Menu **Ventas** (Pedidos, Clientes), **Despacho** (Tablero de despacho); **Maestros** adds Productos terminados, Costos estándar,
Lista de precios, Vehículos y choferes; **Contabilidad** adds Apertura de inventario. Plants and locations come from
`GET /sales/plants` (`sales:read`), so sellers and dispatchers need no `master_data:read` (E-VS3-10-13).

| Page | What it does |
| --- | --- |
| `/ventas/clientes/`, `/ventas/cliente/?id=` | List and search; create (`customer:create`); edit contact data (RNC and name only while DRAFT); credit exposure; prepare terms (`customer_terms:prepare`), approve DRAFT terms (`customer_terms:approve`, step-up); activate (`customer:activate`, step-up) |
| `/maestros/productos-terminados/` | Finished goods: create (`item:create`, category BLOQUE / ADOQUIN / OTRO_PT), activate (`item:activate`) |
| `/maestros/costos-estandar/` | Versions; prepare (`standard_cost:prepare`: product, area by plant, unit cost), approve (`standard_cost:approve`) |
| `/maestros/precios/` | Versions with their lines; "Preparar nueva lista" starts from the list in force (`price_list:prepare`); approve (`price_list:approve`) |
| `/maestros/flota/` | Vehicles (plate, capacity) and drivers (cédula): register, change, activate / deactivate (`fleet:manage`) |
| `/contabilidad/apertura/`, `/contabilidad/apertura-lote/?id=` | Opening batches from a CSV sent base64 (`opening_inventory:prepare`); post (OPEN-INV) or reverse with a reason (`opening_inventory:post`, step-up) |
| `/ventas/pedidos/`, `/ventas/pedidos/nuevo/[?id=]`, `/ventas/pedido/?id=` | List by status; DRAFT form (products of the price list in force; the total is the server's); detail with lines, credit checks and exposure: edit / submit / cancel (Vendedor), approve (step-up) / reject (Crédito), close short (`sales_order:close`), plan a delivery (`delivery:manage`) |
| `/despacho/tablero/`, `/despacho/planificar/?pedido=`, `/despacho/conduce/?id=` | Orders to dispatch and deliveries by status; plan quantities per order line; the delivery's next step (start loading with our truck or the customer's plate, source location per line, weighing and gate with the ticket's SHA-256, POD with receiver, time, evidence and received / returned per line), return trip and cancel with a reason |

The screens never add or multiply amounts or quantities: ordered and delivered are shown side by side and the server refuses
what exceeds the open quantity. `lib/sales.ts` holds the pure helpers (base64 of the opening file, the next dispatch step).

## Billing, receipts and the sales journey (VS3-10b, E-VS3-10-6…14)

Menu **Facturación** (Por facturar, Facturas, Notas de crédito), **Cobros** (Recibos, Depósitos); **Ventas** adds Antigüedad de
CxC and Estado de cuenta. Receipts and deposits pick the company account from `GET /sales/bank-accounts` (masked, `sales:read`,
E-VS3-10-14); the treasurer matches receipts from the reconciliation screen with `GET /treasury/bank-statement-lines/{id}/receipt-candidates`
(`bank:read`, E-VS3-10-8).

| Page | What it does |
| --- | --- |
| `/facturacion/por-facturar/` | Delivered lines not invoiced, by customer; pick lines and create a DRAFT invoice (`invoice:create`) |
| `/facturacion/facturas/`, `/facturacion/factura/?id=` | Filters by status; issue with the e-CF type (step-up); fiscal package with copy buttons; record the e-CF (`fiscal_document:record`, totals typed from the portal, XML hashed); void a never-fiscalized invoice (`invoice:void`); credit note per line with what remains (`credit_note:create`); customer withholdings recorded (`customer_withholding:record`) and reversed (`customer_withholding:reverse`) |
| `/facturacion/notas/`, `/facturacion/nota/?id=` | Issue (`credit_note:issue`, step-up, not the invoice's issuer); fiscal package with the modified e-NCF; record the e-CF 34 |
| `/cobros/recibos/`, `/cobros/recibos/nuevo/`, `/cobros/recibo/?id=` | Filters; record by method (`receipt:record`); apply one amount per open invoice of the customer (`receipt:apply`); unapply a whole application with a reason; reverse (`receipt:reverse`, step-up) |
| `/cobros/depositos/`, `/cobros/deposito/?id=` | Pick cheques and cash in transit and the account, deposit (`receipt:deposit`); slip detail |
| `/ventas/antiguedad/`, `/ventas/estado-de-cuenta/?cliente=` | AR aging by the CREDIT buckets and the statement of account, each with its CSV |
| `/tesoreria/conciliacion/` | "Buscar cobros" on an unmatched line: match a transfer or a deposit (CREDIT) or a bounced cheque (DEBIT); "Cheque devuelto" marks a deposited cheque bounced (`receipt:bounce`, step-up) and matches the line |

Inicio counts orders pending credit, deliveries in transit, invoices with a pending e-CF and unapplied receipts (E-VS3-10-9). The
invoice detail lists its withholdings (`withholdings`) so they can be reversed. `components/Ecf.tsx` holds the copy field and the
e-CF form shared by invoices and credit notes.

The dev stack seeds VS#3 (`tests/Rochell.DevStack/SalesSeed.cs`): sales maps, rules and policies, SALES_ITBIS, BLOQUE-6 with cost,
opening stock and price, the customer Constructora Uno, a truck and a driver, a second account TEST_BANK ••••4321 for receipts
and one user per VS#3 role. `web/e2e/sales-journey.spec.ts` is E2E-S1 through the UI: order → delivery on our truck (gate and
POD) → invoice → e-CF → transfer receipt → application → statement → match, BANK-GL 0.00.

## Local development (E-PR18b-9)

`tests/Rochell.DevStack` starts PostgreSQL 17 in Docker, applies every migration, seeds a company from the test fixtures
(plant, locations, ACTIVE supplier and items, accounts and maps, periods, policies, approved posting rules, active fiscal rules
with TEST sources; VS2-08: a posted invoice of AP 10,620.00, the company bank account TEST_BANK 0123456789, the supplier's
account verified 73 h ago, R-09 and R-10 approved, BANK_CHARGES mapped and the TREASURY aging buckets) with one user per slice
role (Tesorero included) and a plant-scoped storekeeper, and runs the real API host on Kestrel with the
sealer on and the simulated IdP, whose `/dev-idp/authorize` page lets you pick the user. Nothing of it is deployed.

```bash
dotnet build Rochell.slnx -c Release
cd web && npm ci && npm run build && cd ..
dotnet run --project tests/Rochell.DevStack -c Release --no-build -- --web-root web/out    # http://localhost:5080

# or, with hot reload:
dotnet run --project tests/Rochell.DevStack -c Release --no-build -- --public-origin http://localhost:3000
cd web && npm run dev                                                                          # http://localhost:3000
```

`next dev` forwards `/api/*` and `/dev-idp/*` to the stack (`ROCHELL_API_ORIGIN`, default `http://localhost:5080`); the API
honours `X-Forwarded-*` from loopback in Development only, so the sign-in comes back to `:3000`. Use Chrome or Firefox: Safari
does not keep Secure cookies on `http://localhost`.

## Tests (E-PR18b-10)

| Command | What |
| --- | --- |
| `npm run lint`, `npm run typecheck` | ESLint (Next rules), `tsc --noEmit` |
| `npm run check:api` | generated types match `openapi.json` |
| `npm test` | Vitest: decimal formatting and validation, plant scope, step-up drafts, client headers and problems, error dictionary, ledger helpers (CSV link, SHA-256) |
| `npx playwright test` | Journeys in Chromium against the dev stack: the buyer creates and submits a PO, the approver approves it, the storekeeper receives it and sees it POSTED; the configuration journey; the security journey (UI-01: request → second approval → the new employee's menu; read-only audit and master screens); the treasury journey (VS#2 E2E-01 by UI): proposal → prepare (Tesorero) → release (Controller) → import CSV → suggested match → bank charge → BANK-GL difference 0.00 → payment Compensado; and the ledger journey (FIN1-04): adjustment prepared and submitted (Contador) → approved (Controller) → trial balance "Cuadra", CSV download, account ledger, statements |

The other flows are covered at the API level (`tests/Rochell.Api.Tests`, including AT-01 and AT-02 over HTTP).

## MFG1-07 — production screens (E-MFG1-07-1…11)

Menu group **Producción**; see `docs/engineering/manufacturing.md` (MFG1-07). Every select inside a `Field` carries an `aria-label`,
so `getByLabel(…, { exact: true })` finds it whatever option is selected.

## FIS1-05 — fiscal authorization screens (E-FIS1-05-1…12)

Menu **Fiscal** adds Autorizaciones fiscales (`sales:read`, so Crédito, Facturación and the Especialista fiscal see it); see
`docs/engineering/fiscal-authorizations.md`. `lib/authorizations.ts` holds the pure helpers (actions by status and permission, the
e-NCF prefix, the expiry count), unit-tested.

| Page | What it does |
| --- | --- |
| `/fiscal/autorizaciones/` | Filters by status and customer (`?estado=&cliente=`); certificate, customer, project, valid until, status, net authorized and consumed (the server's); "Registrar autorización" (`fiscal_authorization:register`); "Vencer autorizaciones vencidas" runs `expire-fiscal-authorizations` and says how many expired (`fiscal_authorization:suspend`) |
| `/fiscal/autorizaciones/nueva/[?id=]` | Register, or edit a DRAFT: customer (active, with RNC), certificate, issued on, valid until, project, CONFOTUR resolution, project term end, optional origin order of the customer; scope lines product × unit (the price list in force, `sales:read`) with quantity and net as text, validated by the server |
| `/fiscal/autorizacion/?id=` | Header, scope (authorized, consumed, available), documents (any of the 4 kinds; the SHA-256 is computed in the browser, the file is not uploaded), "Facturas que la consumen" (consumptions and releases), history; edit / submit (DRAFT), verify (step-up, hidden for the registrar), return to draft / reject with a reason (PENDING_VERIFICATION), suspend (ACTIVE) and reactivate (SUSPENDED) with a reason |
| `/ventas/proforma/?id=` | From the order's "Proforma" button: issuer, customer, lines with the ITBIS of the rules in force today, totals and a blank signature and stamp area; "Imprimir" calls `window.print()` and the print CSS hides the menu, header and buttons (no PDF) |

`/facturacion/por-facturar/` shows "Autorización fiscal (e-CF 44)" when the customer has ACTIVE authorizations (default "Ninguna —
con ITBIS"; each option names the certificate and what each scope line has available) and sends `fiscalAuthorizationId`. The invoice
of an e-CF 44 offers no 31/32 choice at issue; once issued it reads "Exenta — CONFOTUR, certificado …" and its fiscal package adds
regime, certificate, project and billing indicator 4; the e-CF form expects `E44` + 10 digits. Inicio counts "Autorizaciones por
verificar" for `fiscal_authorization:verify` (list query with PENDING_VERIFICATION, E-UI01-7). The reconciliation screens name
AUTH-CONSUMPTION, EXEMPT-WITHOUT-AUTH and AUTH-EXPIRY and their classifications in Spanish (`RECONCILIATIONS`,
`EXCEPTION_CLASSIFICATIONS` in `labels.ts`).

`web/e2e/fiscal-journey.spec.ts`: Facturación registers an authorization with a unique certificate (BLOQUE-6, 100 / 5,000.00),
attaches the DGII certificate and submits; the Especialista fiscal verifies it; the Vendedor's order (40 blocks) and its proforma
(ITBIS 360.00); Despacho delivers it at the gate; Facturación invoices it under the authorization (e-CF 44, total 2,000.00), issues it
and records an `E44` e-NCF; the authorization shows 2,000.00 consumed and the invoice's consumption. It opens its own order and
delivery by URL and leaves nothing open for the other journeys.

## QUO1-04 — sales quotation screens (E-QUO1-04-1…10)

Menu **Ventas** adds Cotizaciones (`sales:read`) before Pedidos; the detail, form and print pages light up it. See
`docs/engineering/quotations.md`. `lib/quotes.ts` holds the pure helpers, unit-tested (`tests/unit/quotes.test.ts`): the statuses
in Spanish (feminine, "Vencida" for a SENT quote past its validity — the server's `expired` flag), the actions by status and
permission, and `compareDecimals` / `isSpecialPrice`, an exact comparison of two decimal strings digit by digit (no JavaScript
number), used only to warn about a price below the list; nets and totals are always the server's.

| Page | What it does |
| --- | --- |
| `/ventas/cotizaciones/` | Filters by status, customer and "Solo vencidas" (`?estado=&cliente=&vencidas=1`); number, customer, date, valid until, net total, status (with "Vencida") and a "Precio especial" mark; "Nueva cotización" (`quote:manage`) |
| `/ventas/cotizaciones/nueva/[?id=]` | Create, or edit a DRAFT: customer (DRAFT or ACTIVE; a notice says a draft customer must be activated before converting), plant, valid until (15 days proposed), delivery term, site (required for delivered), customer reference, notes; lines of the price list in force with the list price beside each line and an optional quoted price (empty = the list's) — below the list it shows "Precio especial: requiere aprobación" |
| `/ventas/cotizacion/?id=` | Header, lines (list and quoted price, net, special mark), total, the price approval (who, when, whether it covers the current lines), copied from / copies, the order and the closing reason, history. DRAFT: edit, "Enviar a aprobación de precios" when a special price is not covered, otherwise "Marcar enviada al cliente"; PENDING_APPROVAL: "Aprobar precios" (step-up) and "Devolver a borrador" with a reason (`quote:approve_price`); SENT: "Convertir en pedido" while valid (goes to `/ventas/pedido/?id=<resultRef>`), "Marcar perdida" (reason); cancel (DRAFT / SENT, reason); "Copiar" at any status asks the new validity and opens the copy |
| `/ventas/cotizacion/imprimir/?id=` | Issuer, customer, reference, delivery, lines with the informative ITBIS (the server's, rules in force at the quote date), totals, validity, notes, the provisional general conditions (pending X-Q1) and signature lines; "Imprimir" calls `window.print()` with the proforma's print CSS |

The sales order detail reads "Desde cotización COT-…" (linked) when `header.quoteNo` is present. Inicio adds "Crear una cotización"
(`quote:manage`) and counts "Precios de cotización por aprobar" for `quote:approve_price` (list query with PENDING_APPROVAL,
E-UI01-7). `labels.ts` names SENT, CONVERTED and LOST; `errors.ts` the nine `QUOTE_*` codes.

`web/e2e/quote-journey.spec.ts`: the Vendedor quotes Constructora Uno 37 BLOQUE-6 at 45.00 (list 50.00, special-price warning) and
submits it; the Aprobador de políticas contables sees the Inicio counter and approves (step-up by the fresh sign-in); the Vendedor
sends it, the print view shows net 1,665.00, ITBIS 299.70 and total 1,964.70, converts it and lands on the order ("Desde cotización
COT-…", 45.00, total 1,665.00), which the credit check confirms; the quote then reads "Convertida en pedido" with the order. It opens
its own quote by URL and does not depend on the seeded sample quote.

## FIS2-03 — fiscal report screens (E-FIS2-03-1…8)

Menu **Fiscal** adds Reportes fiscales (`fiscal_report:read`: Especialista and Analista fiscal, Contador, Controller, Auditor,
Director); see `docs/engineering/fiscal-reports.md`. `lib/fiscalReports.ts` holds the pure helpers, unit-tested
(`tests/unit/fiscalReports.test.ts`): the default period (the previous month in the Dominican Republic, AAAAMM — date handling
only), period validation, the 606 codes in Spanish (the 11 types of goods and services, payment methods, id types) and the two
row warnings. Every amount, count and total is the server's.

| Page | What it does |
| --- | --- |
| `/fiscal/reportes/` — tab 606 | Period (AAAAMM, default the previous month); the fixed notice that only purchases registered in the system are included; header (company RNC, period, record count, total — the server's); "Descargar CSV para la herramienta DGII" (`?format=csv`, file `606-AAAAMM.csv`) with the three filing steps; the 23 fields grouped (supplier, voucher, amounts, ITBIS, ISR, others) in a table that scrolls sideways, "Pago de retención" on PAYMENT records, each row's warnings in Spanish, the NCF linking to `/cxp/factura/?id=` |
| `/fiscal/reportes/` — tabs IT-1, IR-17 | "Informativo: no es el formulario oficial de la DGII." IT-1: sales by e-CF type (invoices, taxed and exempt net, ITBIS), credit notes, the 606's purchase ITBIS (billed, to cost, to advance), customer withholdings. IR-17: withholdings to suppliers by tax and ISR type (records, base, amount) and the ITBIS / ISR totals |

`/fiscal/reglas/` offers the kind "Clasificación del 606" (`REPORT_606_CLASSIFICATION`) with a template (the four raw-material
categories as "09") and a help listing the 11 codes of the instructivo; its versions offer no "Correr pruebas" (READY with the
source alone; "Última prueba" reads "No aplica"). The withholding kind's help explains `isr_withholding_type` ("1"…"9", base NET).
`labels.ts` names TAX-606 and its three classifications; `errors.ts` `FISCAL_RULE_TESTS_NOT_APPLICABLE`.

`web/e2e/fiscal-reports-journey.spec.ts`: the Especialista fiscal opens Fiscal › Reportes fiscales, picks the current month (the
dev seed posts a supplier invoice today), sees the header, the records (as many as the count, one classified "09"), downloads
`606-AAAAMM.csv` (as many lines as records, 23 fields each), opens the invoice from its NCF, the IT-1 (purchase ITBIS) and IR-17
tabs, and finds the seeded classification on Reglas fiscales. It asserts shapes, not totals, which other journeys change.

## UX1-01b — mobile shell, forms, confirmations, notices and error catalogue (E-UX1-01-1…11)

Wave 1 of the UI audit (2026-09-30), on top of UX1-01a (display names and plant names in the API). No API change.

- **Shell (E-UX1-01-1).** Below 900 px a fixed top bar holds "☰ Menú", the screen title (its menu item, or its list's for a
  detail page: `screenTitle`) and the company; the menu opens as a side panel over the page (backdrop, focus kept inside, Escape
  or a tap outside or choosing an item closes it; the page behind is `inert`). The plant selector, "Actuar como", the user and
  "Cerrar sesión" move into the panel. Menu groups fold on every width; the folded groups are remembered in `localStorage`
  (`rochell.menu.collapsed`, wrapped in try/catch). The top bar reads "Name · main role" (`session.displayName`, the e-mail
  until the first sign-in brings a name; the e-mail as tooltip; the role of the company's first assignment).
- **Responsive (E-UX1-01-2).** Every table sits in `.table-wrap` and scrolls sideways inside its own box; on phones cells do not
  wrap except `td.wrap`. Line editors use `LineTable` (`components/ui.tsx`): below 700 px each row is a card and each cell shows
  its column header (copied into `data-label` after each render). `.form-actions` keeps a form's primary button at the bottom
  of the screen on phones (sticky) and places it last, apart from the secondary buttons. `RowActions` folds several row buttons
  under "Acciones" below 700 px (Curado y liberación).
- **Names and plants (E-UX1-01-3/4).** Seguridad shows "Nombre · correo" (`personLabel`, `components/Person.tsx`). A plant reads
  "Name (CODE)" through `plantName(idOrCode, fallback)` of the session (`lib/plants.ts`, from the session's company `plants`) or
  `<PlantName>`. Screens that hid an approval from its preparer compare against both the e-mail and the display name, since
  the actor fields now carry the name.
- **Formats (E-UX1-01-5).** `formatDate` → `29/09/2026`; `formatDateTime` → `29/09/2026 11:15 p. m.` (Dominican time, built
  from `Intl` parts); `<html lang="es-DO">`; money columns say "(RD$)" in the header and single figures use
  `<Money currency>` ("RD$ "); quantities use `formatQuantity` (trailing zeros dropped by string handling).
- **Forms (E-UX1-01-6).** `Field` takes `required` (a CSS asterisk with empty alternative text, so labels keep their plain text, + `aria-required`), `error` (under
  the input, `aria-describedby`, `aria-invalid`) and `hint`; `useFieldErrors().check({...})` keeps per-field messages and
  focuses the first invalid input; line cells use `fieldAria` + `FieldMessage`. Every create / edit form maps its former single
  message to the fields; rules spanning the whole form (debits = credits, "at least one line") stay next to the button.
- **Errors (E-UX1-01-7).** `lib/errors.ts` has a Spanish message for every server code: the public string constants of the
  `*Errors` classes of the production assemblies plus `SessionService.LoginRejected` / `TestIdentityUnavailable`
  (`tests/Rochell.ArchitectureTests/ErrorCatalogueTests.cs` fails and lists any missing code). An unknown code reads "No se pudo
  completar (CODE)." with the server's message folded under "Detalle técnico".
- **Confirmations (E-UX1-01-8).** `ConfirmDialog` (native modal `<dialog>`: focus inside, Escape and a tap outside cancel,
  "Cancelar" focused first) states the consequence and, for commands whose permission requires step-up, that re-authentication
  may follow. `ConfirmAction` wraps a button (the dialog's button reads "Confirmar: <label>"); `ReasonAction` now asks for its
  reason in the same dialog. Wired into posting / reversing shift summaries, settling costs, closing / reopening components,
  approving rules, policies, maps, structures, recipes, price lists, standard costs, quote prices, credit, customers and terms,
  activating fiscal rules, deactivating accounts, machines, shifts, trucks and drivers, issuing invoices and credit notes,
  voiding, releasing / blocking / scrapping lots, releasing payments, recognising bank charges, converting quotes, expiring
  authorizations, verifying supplier bank accounts and authorizations, posting / reversing opening stock.
- **Notices (E-UX1-01-9).** `ToastProvider` (`lib/toast.tsx`, in the layout, so a notice survives the navigation after a command)
  shows a green notice for 6 s. `useCommand(formId, path, success)` / `run(body, values, success)` take the message, a text or a
  function of the response and the document number of its result (the first "…No" field, `lib/notices.ts`); every command
  names its document and result ("Pedido PV-000012 enviado a crédito.").
- **Environment badge (E-UX1-01-10).** The API exposes no environment to the browser, so `lib/environment.ts` decides by host:
  `staging.…` → "STAGING", `localhost` / `127.0.0.1` → "PRUEBA", anything else (production) → no badge.
- **Tests (E-UX1-01-11).** `playwright.config.ts` adds the project `mobile` (390 × 844, touch) over the sales, purchase,
  production, treasury and quote journeys, against a second dev stack on port 5191 (the journeys expect the seeded data, so the
  two runs never share a database). `e2e/support.ts` holds the shared `signIn`, `nav` (opens "☰ Menú" on a phone),
  `confirmAction` (presses the button and "Confirmar: <label>"), `submit` (checks the primary button is visible and tappable)
  and `expectFits` (fails when `document.documentElement.scrollWidth` exceeds the viewport), run on every navigation and form
  submission. Vitest: `tests/unit/ux1.test.ts` (dates, quantities, plant names, environment badge, notices, screen title) and
  `tests/unit/ui.test.tsx` (Field wiring, confirmation dialog markup); `errors.test.ts` covers the new fallback.

`useSession().isMine(actor)` is the one place a screen asks whether a shown actor (name or e-mail) is the signed-in person, to
hide a decision the server would refuse; it is always false for a superadministrator, whose four-eyes controls are waived
(E-ADM-2-4).

## UX2-02 — configuration screens (E-UX2-1…13)

Wave 2 of the UI audit, on top of UX2-01 (`docs/engineering/configuration.md`). No API change; the helpers are pure and unit-tested
(`tests/unit/ux2.test.ts`).

- **Percentages (E-UX2-1).** `lib/decimal.ts`: `shiftDecimalPoint`, `fractionToPercent` ("0.005" → "0.5"), `percentToFraction`
  ("12.5" → "0.125", "5 %" accepted, "1,5" refused) and `formatPercent` move the decimal point on the text only (no JavaScript
  number). DECIMAL_PERCENT policy parameters and fiscal rule rates are typed and shown in % (`SuffixInput`, the unit after the
  input); the server still receives fractions.
- **Políticas (E-UX2-2/3/4)** `/contabilidad/politicas/` (`lib/policies.ts`). Each policy reads "Name (CODE)" with who prepares
  and approves (`preparerRoles` / `approverRoles`); a table of the parameters by label and unit ("Registro tardío (horas)"), the
  value in force ("2 %", "RD$ 1.00", "48 horas") and what it affects. The prepare form validates each value by its unit (whole days
  / hours, amounts and percentages with ≤ 4 decimals, bounds compared as text) with the example and `affects` as hint. A DRAFT
  version shows an approval card and its confirmation dialog with "En vigor → Propuesta" per parameter, changed rows highlighted
  (`policyDiff`, string comparison; the version in force is the ACTIVE one covering today, end date exclusive). Warnings "Sin
  versión en vigor" and "Falta el parámetro X: prepare una versión nueva" (definitions missing from the version in force). The
  history of versions is folded.
- **Mapas de cuentas (E-UX2-5/6)** `/contabilidad/mapas/`: roles by their names (`/finance/account-roles`); "Preparar mapa"
  (`account_role_map:prepare`: role by name, optional item category, account — only control accounts for a control role and
  regular accounts otherwise —, effective date) calls `prepare-account-role-map`; the preparer is not offered the approval; the
  alert "Roles sin cuenta" lists roles with `usedByActiveRule && !mappedToday`; a table of the account roles with their
  description. The journals page (`/auditoria/asientos/`) names the role too when the reader has `configuration:read`.
- **Reglas contables (E-UX2-7)** `/contabilidad/reglas/`: each version reads "Genera: Débito <role> / Crédito <role>"
  (`ruleLinesSummary`) and lists its lines with the explanation, placeholders shown readable ("{ncf}" → "[NCF]",
  `humanizeExplanation`); the alert "Reglas en borrador" lists the DRAFT versions.
- **Reglas fiscales (E-UX2-8)** `/fiscal/reglas/` (`lib/fiscalRuleForm.ts`): a guided form per kind — tax code, rate in %,
  effect, exempt item categories (checkboxes), party types (checkboxes), base, ISR withholding type ("1"…"9"), the 606 class of
  each raw-material category (with the 11 names) — builds exactly the JSON of `FiscalRuleDefinition` (the templates' key order,
  2-space JSON, rates as fractions; a rate read from a stored definition keeps its text while the percentage still means it, so
  every template round-trips byte for byte). "Ver JSON (avanzado)" shows the text that is sent and lets it be edited; a hand edit
  the form can show updates the form, otherwise the JSON wins and the form is not validated. The regression cases are a table
  (case, party type, item category, net, ITBIS, expected taxes with code, amount and effect) producing the same `cases` payload
  (`rowsToCases`), with per-cell messages; the first case takes the rule's tax code and effect and leaves the expected amount to
  the analyst. Stored definitions read in words ("ITBIS al 18 %", "Exentas: Bloque"), the JSON folded under "Ver JSON".
- **Configuración (E-UX2-9/10/11).** The menu group **Configuración** (last) gathers Centro de configuración, Empresa, Plantas y
  ubicaciones, Catálogo de cuentas, Estructuras de reporte, Mapas de cuentas, Reglas contables, Políticas, Fuentes fiscales and
  Reglas fiscales; the routes did not change. The lit item is the longest matching href. `/configuracion/` (Centro de
  configuración, `configuration:read`) reads `/reconciliation/setup-status`: the count of DONE steps out of 19, one card per area
  with a traffic light (red when a step is PENDING, amber when one is WARNING, green when all are DONE) and the 19 steps in order
  with their Spanish title, status, what is missing in words (`lib/setup.ts`: role names, report names, rule kinds, account role
  and policy names from their queries) and a link to the screen that resolves each. Inicio shows "Puesta en marcha" (progress and
  the next three steps) to `configuration:read` holders until `complete`. `/configuracion/empresa/`: the RNC (read only: another
  RNC is another company), the legal name and the plants' names, editable with `company:manage` (step-up) after a confirmation
  dialog; the session is read again so the header and plant names follow.
- **Roles (E-UX2-12).** Usuarios y roles offers the roles of `/identity/roles`, shows the chosen role's description under the
  select, a tooltip on each held role and a table "Roles y para qué sirven"; Solicitudes de rol shows the description under each
  requested role.
- **Notices.** "Mapa <role> → <account> guardado en borrador; falta su aprobación.", "Razón social cambiada a …", "Planta … renombrada: …".
- **Tests.** Vitest `tests/unit/ux2.test.ts`: percent helpers, policy units / validation / diff / version in force, fiscal form
  JSON per kind (templates round-trip exactly), case table payload, rule-line summary, setup labels, lights and progress.
  Playwright `e2e/configuration-journey.spec.ts` (desktop and mobile): the Controller prepares PURCHASING typing 7.5 % and the
  Aprobador de políticas sees "2 % → 7.5 %" highlighted in the card and the dialog and approves it; the Contador prepares a map
  of "Cargos y comisiones bancarias" and the Controller approves it; the Analista fiscal configures a withholding with the guided
  form and the advanced JSON reads exactly the server's; the Controller renames a plant, opens the Centro de configuración (19
  steps) and sees "Puesta en marcha" on Inicio while setup is incomplete. Everything it creates starts next year.
