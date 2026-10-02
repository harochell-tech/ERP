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
| Almacén | `/almacen/por-recibir/` (UX3-02), `/almacen/recepciones/`, `/almacen/recibir/?oc=`, `/almacen/recepcion/?id=`, `/almacen/correcciones/` | post receipt, reverse (Controller), create correction, approve/reject correction (Controller) |
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
| `/fiscal/autorizaciones/` | Filters by status and customer (`?estado=&cliente=`); certificate, customer, project, valid until, status, net authorized and consumed (the server's); "Registrar autorización" (`fiscal_authorization:register`); "Marcar como vencidas las que pasaron su fecha" (UX4-02; formerly "Vencer autorizaciones vencidas") runs `expire-fiscal-authorizations` and says how many expired (`fiscal_authorization:suspend`) |
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

## UX3-02 — critical flows per area, screens (E-UX3-1…14)

Wave 3 of the UI audit, on top of UX3-01 (`docs/engineering/flows.md`). No API change; the helpers are pure and unit-tested
(`tests/unit/ux3.test.ts`). Every rule stays the server's: readiness, open and maximum quantities, ITBIS, balances, net weight.

- **Guided close (E-UX3-1)** `/cierre/periodos/`: one card per month (`lib/close.ts`: `groupPeriodsByMonth`, "Agosto 2026"), each
  component folded with a summary badge ("Listo para cerrar", "Aún no termina", "Pendiente de verificar", its status once closed,
  "Reapertura solicitada"). Unfolded, an OPEN / REOPENED component lists `GET …/periods/{id}/close-readiness` as a checklist
  (`readinessChecklist`): the month ended, records sealed, and each blocking reconciliation by its Spanish name with its last run
  for the period ("Sin errores que bloqueen (verificada …)", "2 errores bloquean el cierre", "Sin verificar para este mes") and
  "Ver resultado" to the run. "Verificar ahora" (`reconciliation:run`, ended months) runs `RunReconciliation` with the component's
  blocking codes and `cutoffDate` = the period's end, then reloads the readiness. "Cerrar" appears only when the month ended and
  the server says `ready` (`closeAvailability`); a month that has not ended reads "Aún no termina" (future months are not asked
  for readiness). Reopen, approve and reject reopen are unchanged.
- **Reconciliations (E-UX3-2/3)**: the runs list and the run use the server's `name` and `guidance` ("Qué hacer"); the list filters
  by reconciliation (`ListReconciliationDefinitions`), runs one or all, and folds "Qué revisa cada conciliación" (severity, the
  components it blocks, guidance). Exceptions show `matchLabel` (the raw key as tooltip and as fallback), `classificationName`
  (fallback the code), severity "Error" / "Aviso" (`lib/reconciliations.ts`) and each classification's guidance. The hard-coded
  `RECONCILIATIONS` and `EXCEPTION_CLASSIFICATIONS` of `labels.ts` were removed: nothing uses them any more.
- **Explain (E-UX3-4)** `/auditoria/explicar/`: journal type, event type, command, posting rule ("R-01 · Recepción de mercancía"),
  integrity status and document kind in Spanish (`lib/explain.ts`, unknown codes as they come); the account role and the mapping by
  the role's name (`/finance/account-roles`, with `configuration:read`, as the journals page); plant, item and party names; the
  source document linked by kind (receipt and supplier invoice to their detail, reversal and correction to their lists); the
  mapping as a card "Por qué esta cuenta"; the ids, the mapping, the frozen inputs and the event payload folded under "Detalle
  técnico (avanzado)".
- **Por recibir (E-UX3-5)** `/almacen/por-recibir/` (menu Almacén, `goods_receipt:post`; the query is `purchase_order:read` and
  plant-scoped): one card per APPROVED / PARTIALLY_RECEIVED order, oldest first — supplier, order and approval dates, lines with
  ordered, received, "Pendiente" and "Máximo permitido" — and "Recibir" while a line can take more. `/almacen/recibir/` prefills
  each line with the server's `openQuantity` (`prefillQuantity`; the user may change or clear it) and shows "Pendiente" and
  "Máximo permitido" (from the orders to receive). Inicio's "Recibir material" counts the same query (APPROVED and
  PARTIALLY_RECEIVED) and links to Por recibir.
- **CxP (E-UX3-6)**: the supplier invoice list shows Neto, ITBIS, Total con ITBIS, Saldo and "Estado de pago" (`lib/payables.ts`:
  Sin contabilizar, Pendiente de pago, Pagada en parte, Pagada, Anulada, Reversada, with tones); the detail the same plus
  "Pagos" (number linked to `/tesoreria/pago/` with `payment:read`, value date, status, amount applied). The register form reads
  Proveedor → Orden de compra → lines → NCF, fecha, vencimiento.
- **Conduce (E-UX3-7)** `/despacho/conduce/imprimir/?id=` from `GET /sales/deliveries/{id}/print`, linked "Imprimir conduce" on the
  delivery: issuer and customer with RNC, site, plant, delivery and order numbers and dates, gate-out, vehicle and driver (own or the
  customer's), gross / tare / net kg (the server's net), lines with planned / issued / delivered and their lots, "Despachado por" and
  "Recibido por (nombre, cédula, firma)" boxes. Letter size through a named page (`@page conduce { size: letter }`), the proforma's
  print CSS otherwise. Until the gate-out it carries the diagonal watermark "BORRADOR – NO DESPACHADO" (`deliveryWatermark`,
  `components/Watermark.tsx`: absolute on screen, fixed on paper so every printed page carries it).
- **Evidence (E-UX3-8 (a))**: the weigh ticket and POD forms no longer show an editable SHA-256. Choosing the file computes it and
  shows "✓ Huella del archivo verificada: <file>"; the reference is proposed from the file name; the hint says "El archivo no se
  guarda en el sistema; conserve el original". The delivery reads "Evidencia: <reference> (huella verificada)" (hash as tooltip).
  The other evidence forms (e-CF, authorizations, adjustments) are unchanged.
- **Credit note (E-UX3-9)**: "Emitir nota de crédito" is hidden, with a notice why, when `invoiceIssuedById` is the signed-in user
  (`useSession().isMyUserId`, pure `isOwnUserId` in `lib/scope.ts`: never true for a SUPERADMIN, as `isMine`). The reason category
  ("Descuento", "Error de precio", "Otro") and the rate (in %) read in Spanish.
- **Quote print (E-UX3-10)**: the "(X-Q1)" wording is gone ("Documento no fiscal" stays); the diagonal watermark by status
  (`quoteWatermark`): BORRADOR (DRAFT, PENDING_APPROVAL), VENCIDA (the server's `expired`), PERDIDA, CANCELADA; none when sent and
  valid or converted.
- **Units (E-UX3-11)**: the consumption unit of a production run is a select of the material's base unit plus the units converted
  into it (`uomOptions`, also used by the purchase order form; a recorded unit stays listed); "Unidad base" of raw materials and
  finished goods and the conversion's "Unidad de compra" come from `GET /master-data/uoms` (`useUomCatalogue`, "kg (masa)").
- **Lots (E-UX3-12)** `/produccion/lotes/`: one "Acciones" button per lot (in its first cell, so it stays in view on a phone) opens a
  dialog with the actions `lotActions` allows — Liberar (location), Bloquear / Desbloquear (reason), Desechar unidades (location,
  units, reason; step-up notice) — each confirmed with "Confirmar: <acción>". The filter "Listos para liberar" (CURING with the
  API's `curingDone`) is the default for `fg_lot:release`.
- **Inicio (E-UX3-13)**, from existing queries: "Corridas de hoy sin resumen" (`shift_summary:record`: today's IN_PROGRESS runs without
  summary), "Resúmenes de turno en borrador" (`shift_summary:record`) and "Resúmenes de turno por contabilizar"
  (`shift_summary:post`): IN_PROGRESS runs with a DRAFT summary, linked to the card "Resúmenes en borrador" of Producción del día
  (any day, `#resumenes-borrador`); "Recetas por aprobar" (`recipe:approve`, DRAFT recipes); "Lotes listos para liberar"
  (`fg_lot:release`).
- **Notices**: "Conciliaciones de <component> verificadas al <end>.", "Conciliación «<name>» ejecutada: revise el resultado.", the
  lot actions' notices.
- **Tests.** Vitest `tests/unit/ux3.test.ts`: month grouping, checklist and close availability, severities, match-label and
  classification fallbacks, Explain labels and document links, receiving prefill, payment status labels and tones, watermarks,
  own-user check and reason labels, unit options and catalogue, lot filter and actions. Playwright: the ledger journey (desktop)
  checks the current month "Aún no termina", last month's ACR-NTX checklist, "Verificar ahora" and the run it links to; the
  purchase journey goes Inicio "Recibir material" → Almacén › Por recibir → receive with 40 prefilled; the treasury journey finds the
  seeded invoice "Pagada" with balance 0.00 and PAG-000001 among its payments; the sales journey opens the conduce print view with
  the watermark before the gate-out and without it after (net 1,000 kg), verifies the evidence fingerprints without a SHA-256
  field, and drafts a credit note the invoice's issuer is not offered to issue; the quote journey prints the draft with "BORRADOR"
  and the sent quote without watermark or X-Q1; the production journey has Calidad see the Inicio counter, open "Listos para
  liberar" and release the lot through its "Acciones" dialog. All but the ledger journey run on desktop and on the phone.

## UX4-02 — screens of Compras, Almacén, CxP, Tesorería, Contabilidad, Cierre, Auditoría and Fiscal (E-UX4-1…17)

Wave 4 of the UI audit, on top of UX4-01 (`docs/engineering/ux4.md`); UX4-03 covers the other areas. Every total and difference is
the server's (E-UX4-2). The pure helpers live in new files, unit-tested (`tests/unit/ux4a*.test.ts`): `lib/ux4a.ts`
(`previewQuery`, the POST transport of the previews — anti-CSRF header, no Idempotency-Key; `bankAccountLabel`),
`lib/ux4a-compras.ts`, `lib/ux4a-tesoreria.ts`, `lib/ux4a-contabilidad.ts`, `lib/ux4a-auditoria.ts`.

- **Compras.** The PO form previews through `POST …/purchase-orders/preview` 400 ms after the last change, once plant, supplier,
  date and every line are complete: "Neto (RD$)" per line, net, "ITBIS estimado" (or "No disponible: <reason>" when the fiscal gate
  is closed) and total; a preview error is one line and never blocks saving. "Guardar y enviar a aprobación" creates and submits
  (two commands); a DRAFT's detail says "Aún no enviada a aprobación". The list has Total (RD$, without ITBIS), a supplier filter
  (`?proveedor=`) and, for approvers, "Pendientes de mi aprobación (n)" (every PENDING_APPROVAL order in scope: the list has no
  creator, and the server refuses self-approval). The detail adds Neto and Pendiente per line, the total and the receipts as a
  table (location, item, quantity). Units read "tonelada (t)", "litro (L)"…; an item whose description repeats its code shows the
  code once. The OC-YYYY-NNNNNN number is assigned on save (E-UX4-5).
- **Almacén.** Recibir preselects the server's `defaultLocationId`; CURADO and TRANSITO are listed disabled "(no recibe materia
  prima)" (the server answers LOCATION_NOT_RECEIVABLE anyway, E-UX4-8). "Corregir cantidad" opens the correction form with examples
  ("-2.5 (faltaron 2.5 t)"); the corrections filters show the active one and explain an empty result; an empty receipts list links
  to Por recibir.
- **CxP.** "Cotejada con OC y recepción" (a local label; `labels.ts` MATCHED is unchanged); the accounting status appears only when
  it adds something (hidden for NOT_POSTED on a DRAFT or matched invoice and when it equals the document status). The register
  form takes "Total según factura (RD$)" (`printedTotal`, E-UX4-7) and the detail shows the server's difference. Tax effect
  "Crédito fiscal (deducible)" / "No deducible (va al costo)", rate in %, "Recibido sin facturar", "Diferencia de precio aceptada
  por <name>". The aging totals row is the server's `bucketTotals`; its empty state names the next step.
- **Tesorería.** Company accounts read "alias · BANCO ••••6789" everywhere in these areas; the Controller sets the alias on
  Maestros › Cuentas bancarias ("Poner alias" / "Cambiar alias", `set-bank-account-alias`, blank clears, E-UX4-6). The proposal
  opens with "Vence hasta" today + 7 (E-UX4-12) and the shortcuts Esta semana / 15 días / Todo ("Todo" sends 9999-12-31); when
  nothing falls due it counts what falls due later (a second query, rows counted) with "Ver todas". The payments list shows the
  server's count and total of the filter. Statements read "2 líneas, ninguna pendiente" / "1 de 2 pendiente de conciliar";
  "Importar extracto" sits by the title like "Preparar un pago". The bank reconciliation shows "Diferencia sin explicar" and the
  reconciling table Saldo del extracto + movimientos en libros no reflejados (`glItemsTotal`) − movimientos del banco no
  registrados (`lineItemsTotal`) + diferencia = Saldo en libros, every figure the server's.
- **Contabilidad.** Balanza: "Saldo deudor" / "Saldo acreedor" with the server's totals. Balance general: a summary with the result
  inside Patrimonio and "Total pasivo + patrimonio"; "Agrupado según el formato de reporte aprobado" instead of "Estructura versión
  N". Mayor: "Buscar cuenta" filters the account select (code or name, accents ignored). Ajustes: the support file follows
  E-UX3-8 (a) (hash hidden, "✓ Huella del archivo verificada", "Soporte actual" when editing), "Tiene efecto fiscal" instead of
  ACR-TAX, totals on save; the detail says "Debe aprobarlo: Controller, una persona distinta de quien lo preparó" and shows "Aprobó"
  only once approved; the list drops the Componente column and filters by month (in the browser, over the loaded page:
  `ListManualJournals` takes no date). Catálogo: the server's balance, "Desactivar" only at zero, "De control" explained, "Sin
  clasificar". Estructuras: "Preparar versión" is disabled while lines and accounts equal the copied version (the date is not
  compared: the form proposes today), "Agrupada bajo", "Activo (A)", "Regirá desde" for a draft and who approves next. Apertura:
  "Descargar plantilla CSV" (`planta,ubicacion,producto,cantidad,documento`), each column explained, the button says what is missing.
- **Cierre.** `/cierre/conciliaciones/` opens with the latest run of each reconciliation (`/reconciliation/runs/latest`, with the
  cutoff) and folds the full history; run totals are labelled with `sideALabel` / `sideBLabel`; the run page lists its facts (no
  "p. m.." sentence).
- **Auditoría.** `/auditoria/asientos/` without `?evento=` is "Buscar asientos" (`/audit/journals?text=`, by document number or id,
  `?buscar=`); with an event it is titled after its document ("Asientos de la recepción RM-…"), labels come from `lib/explain.ts`
  and "Reversa del asiento" links. Verificar integridad and Respaldos diarios inalterables show the last verification and per chain
  the latest daily backup, last sealed record, pending seal and seal errors (`/audit/integrity-status`). The menu items (in
  `Shell.tsx`, outside UX4-02) keep their former names.
- **Fiscal.** Fuentes fiscales: the E-UX3-8 (a) evidence pattern, "Prueba" / "Oficial (DGII)" explained (P-7), rows with title,
  version, type badge and short hash. Reportes: the period is a month picker (sent as AAAAMM); e-CF types "31 — Crédito fiscal"…
  (`ecfTypeLabel`) and the ISR types by name. Autorizaciones: "Vence en N días" / "Vence hoy" / "Venció hace N días" from
  `daysToExpiry`; the history has no "Por" column (the authorization history carries no actor).
- **Tests.** Playwright: the purchase journey (desktop and mobile) checks the preview (40,000.00), "Guardar y enviar a aprobación",
  "Pendientes de mi aprobación", the list total, Pendiente and the preselected location; the treasury journey sets the alias
  "Operativa" and finds "Operativa · TEST_BANK ••••6789" on the proposal, payment and reconciliation, the default "Vence hasta", the
  payments count and total and "Diferencia sin explicar" 0.00; the ledger journey the adjustment's verified fingerprint, equal
  debit and credit balance totals, the account search and "Total pasivo + patrimonio"; the security journey searches the journals
  by a supplier invoice's NCF and reads the integrity status; the fiscal journeys the month picker and "Vence en N días".

## UX4-03 — remaining findings, screens part 2 (E-UX4-1…17)

Wave 4 of the UI audit, on top of UX4-01 (`docs/engineering/ux4.md`): Ventas / Cotizaciones, Despacho, Facturación, Cobros,
Producción, Maestros (but the company bank accounts), Seguridad, Inicio and the shell. UX4-02 does the other areas. No API change;
every figure shown is the server's string (the previews and suggestions are queries), comparisons are on text only.

- **Shared (G-31).** `components/StateNotices.tsx`: `LoadingIndicator` (spinner + "Cargando…", `role="status"`), `EmptyState` (what
  the screen is for and next-step links, filtered by permission) and `StepsHelp`; styles in `components/states.css`. `lib/ux4b.ts`:
  `previewQuery` (POST query: the anti-CSRF header, no Idempotency-Key, E-UX4-3), `matchesSearch` (case- and accent-insensitive,
  every word in some field), `codeAndName` ("ADITIVO-P", not "ADITIVO-P — ADITIVO-P"), Inicio's configuration counters and the
  policy themes. Empty select options read "Seleccione…".
- **Shell (G-13, G-18, G-19).** The sign-in screen carries the brand and one primary button "Entrar con Google"; a signed-in person
  without roles reads "Aún no tiene acceso", what to ask and to whom, with the e-mail and "Copiar correo". Menu: "Cuentas por rol"
  (was "Mapas de cuentas", also the page heading and the setup steps), "Reglas de contabilización" (as the page heading), "Cuentas
  por cobrar por antigüedad", "Verificar integridad" and "Respaldos diarios inalterables" (A-09, the pages renamed by UX4-02).
  The invoice reads its e-CF type with its name ("31 — Crédito fiscal", `ecfTypeLabel` in `lib/ux4b.ts`, G-25; the portal package
  keeps the bare code to copy).
- **Inicio (G-15, G-16, G-17).** Configuration waiting for its approver, counted from the configuration lists (`configuration:read`)
  and leaving out what the reader prepared: policy versions (`accounting_policy:approve`), account role maps
  (`account_role_map:approve`), report structures (`report_structure:approve`), posting rules (`posting_rule:approve`), fiscal rule
  versions READY to activate (`fiscal_rule:activate`), and ACTIVE authorizations whose `daysToExpiry` is within the REVENUE_ACCOUNTING
  `authorization_expiry_alert_days` in force (`fiscal_authorization:suspend`; none without that policy value). "Cerrar o reabrir
  períodos" needs `period_component:close` (the Auditor no longer sees it). The "(E-11)" note is gone; no task reads as an empty
  state. The production counter reads "Resúmenes de turno por cerrar".
- **Políticas (G-22).** Policies by theme (Compras e inventario, Ventas y crédito, Producción, Contabilización y tesorería, Otras),
  and inside a policy its parameters by theme when some have one (`authorization_expiry_alert_days` under "Fiscal").
- **Maestros (C-32…35, V-38, V-40).** Materias primas lists raw materials only, with search, category filter, "Nueva materia prima"
  folded and the conversion as "1 saco equivale a 42.5 kg". Proveedores: "Nuevo proveedor" folded, search, "Saldo por pagar", the
  row buttons aligned; the supplier shows the payment term and links to its orders, invoices and AP aging, and says who may see bank
  accounts. Plantas / Empresa no longer mention "la herramienta de despliegue". Costos estándar explains the two ways to prepare
  a cost ("Opción 1 · Escribir el costo unitario", "Opción 2 · Calcular desde la receta") and says "Planta" / "Área de valuación".
  Productos terminados: search and category filter.
- **Seguridad (A-19, A-20).** Usuarios y roles: search by name or e-mail, filter by role ("Sin roles" too); "Solicitar revocación" is a
  quiet link beside each role, confirmed in a dialog that says the second approver decides it. Solicitudes de rol: the empty list
  explains the four-eyes flow and links to Usuarios y roles.
- **Previews (V-11, V-14, E-UX4-3/4).** The order and quote forms ask the server to price the draft 400 ms after the last change,
  once every line has a product and a quantity (`useSalesPreview`, `components/SalesUx4.tsx`): net per line, net total, estimated
  ITBIS (or `itbisUnavailableReason`) and total; a failure is a quiet note. The order form and a DRAFT order show
  `CreditPreviewCard` (GET `credit-preview?amount=` with the net): "Cabe en el crédito disponible…" or "Excede el crédito disponible
  por RD$ X…" (X the server's `availableAfter` without its sign) and the reasons in words; the order explains "Enviar a crédito".
- **Accounting visibility (E-UX4-11, V-05, V-23).** `canSeeAccounting` (`configuration:read` or `ledger:read`: Controller, Contador,
  Auditor) gates the invoice and credit-note accounting status and the delivery's control transfer ("Cuándo la mercancía pasó a
  ser del cliente (contabilidad)"). `SalesHistory` splits the commercial "Historial" from "Historial contable" (accounting readers).
- **Sales words (V-06, V-07, V-10, V-17, V-23, V-27, V-37).** Invoices read Emitida / Cobrada en parte / Cobrada / Acreditada /
  Anulada, credit notes Emitida / Anulada; "Facturas pendientes de cobro", "Crédito usado"; "Registrar entrega al cliente" and
  "Constancia de entrega firmada"; lots "Lote X: 100 (sale de LOC)" under "Ubicación de salida"; "Depósitos registrados"; "otra
  persona autorizada"; amounts inside sentences use `MoneyText` (RD$, not monospace).
- **Customers, prices, aging, statement, proforma (V-15…V-21).** "Nuevo cliente" is its own button, the search apart; the terms form
  starts from the ACTIVE terms and explains "Retener crédito" and "Preparar términos"; Lista de precios shows "Precios vigentes"
  (the ACTIVE version) first. AR aging: the server's `bucketTotals` in the footer ("(sin filtro)" while a filter hides rows),
  invoice and statement links, name and "Solo con saldo vencido" filters. The statement has "Vista para imprimir"
  (`/ventas/estado-de-cuenta/imprimir/`, the proforma's print CSS). The proforma carries BORRADOR (DRAFT / PENDING_CREDIT) or
  CANCELADO and "Válida al <fecha>".
- **Dispatch (V-25, V-26).** Planning shows "Pendiente", prefills "A despachar" and offers "Despachar todo" only when the pending
  quantity needs no arithmetic (nothing delivered and no open delivery, so pending = ordered, `pendingWithoutArithmetic`);
  otherwise "Lo verifica el sistema" and the open deliveries are named. The board shows the requested date and ordered / delivered
  per line (from the order details) and names empty statuses once.
- **Invoice and collections (V-30…V-36).** The invoice header is labelled facts with "Total con ITBIS" and "Pendiente de cobro";
  "Nueva nota de crédito" is a button offered with the e-CF accepted and the invoice Emitida or Cobrada en parte. A new receipt shows
  `SuggestReceiptApplication` (oldest first, editable, "Volver a la sugerencia"); "Registrar cobro" records it and then applies the
  amounts with the receipt's version (if applying fails the receipt opens with the suggestion prefilled). Bank accounts read
  "alias · banco ••••6789"; "Fecha valor" is explained; the receipts list has one status column ("Aplicado en parte · sin
  depositar") and the deposit account. Empty states with next steps on invoices, credit notes, Por facturar, orders, quotes,
  customers, receipts, deposits and the statement.
- **Production vocabulary and units (P-03, P-05, P-06, P-10, P-12, P-24, P-37, E-UX4-14)** (`lib/ux4bProduction.ts`): "Merma de
  mezcla" / "Merma en fresco", "Cerrar resumen del turno" (a POSTED summary reads "Cerrado"), "Costo acumulado en proceso", no
  "colector", no "Registrado" / "Real (unidad base)"; quantities carry their unit, the litre reads "L" (display only);
  `ProductionBadge` gives a released lot the done tone and a recipe "Activa".
- **Producción del día (P-13…P-18).** A 3-step help; the form's date is the page's day (`?dia=`); "Iniciar una corrida" folded and full
  width; "Totales del día" from `GetProductionDay` (runs, good units, `mixScrapUnits`, `freshScrapUnits`, `scrapUnits`); per run a
  "Siguiente paso" button (`runNextStep`); lot codes linked to `/produccion/lotes/?lote=`; materials with the server's signed
  difference, `differencePct`, the `outOfTolerance` mark and the tolerance ("±5 %").
- **Corrida (P-22…P-26).** A header card and the run's own recipe version (`recipeVersionId`, E-UX4-9); consumption shows
  `qtyPerBatch`, the recorded theoretical for the run's batches, real, signed difference, % and tolerance mark; the consumption
  location is chosen explicitly; the lot shows `curingHoursRemaining`. While the form is being filled only the per-batch figure
  shows (no server field gives the theoretical for the batches being typed).
- **Lots, recipes, machines, costs (P-29…P-41).** "Liberar" only once `curingDone` (else the remaining hours); no preselected release
  location; the run number links to its day. Recipe form folded, with the notice that it stays in draft for the Gerente de planta,
  hints for batch, cycle, rack and curing, minimum curing ≥ 1 h checked on the client, the unit beside the quantity per batch; the
  detail shows "Preparada por / Aprobada por" and a text-only comparison with the previous version. Machines and shifts: explicit
  headers. Costs: the month opens on the previous one; "Liquidar" only for ended months; RD$ and "—" explained.
- **Tests.** Vitest `tests/unit/ux4b.test.ts` (search, code and name, Inicio counters, alert window, policy themes, menu titles,
  `previewQuery`), `ux4b-sales.test.ts`, `ux4b-production.test.ts`. Playwright (desktop and mobile): `home-journey.spec.ts` (sign-in
  screen, no-roles page, Inicio configuration tasks and the Auditor without "Cerrar o reabrir períodos", policies by theme, menu
  names, master-data and user search); the sales journey (order and credit previews, planning prefilled, no control transfer for
  Despacho, "Emitida" without accounting status, the folded credit note, AR aging totals, a receipt recorded and applied from the
  suggestion); the quote journey (quote preview, credit preview); the production journey (minimum curing, day totals and
  variances with tolerance, a still-curing lot with remaining hours and no "Liberar").

## IMP-02 — bulk load screens (E-IMP-1…11, E-IMP-02-1…3)

| Screen | What it does |
| --- | --- |
| Maestros › Proveedores | «Importar desde ADM Cloud» (`supplier:import`): `PartyImportPanel` reads the `.xlsx` / `.csv` (≤ 5 MB, base64), «Revisar archivo» shows every row with what it would do and writes nothing, «Importar N proveedores» runs the command (step-up; the file is kept through the re-authentication). «Solo borradores», a checkbox per draft and «Activar seleccionados (N)» (`supplier:activate`, confirmation dialog, up to 500) |
| Maestros › Proveedor | Contact card: phone and e-mails, edited with `supplier:update` (`SetSupplierContact`); e-mails one per line, the first is the principal one |
| Ventas › Clientes | The same import panel for customers (`customer:import`). For the ticked drafts: «Aprobar términos de los seleccionados» (`customer_terms:approve`; the DRAFT terms of those customers) and «Activar seleccionados» (`customer:activate`); the notice says how many were done and why the others were skipped |
| Ventas › Cliente | «Correos»: the customer's whole list, one per line |

`lib/partyImport.ts` (unit-tested) holds the Spanish words for outcomes and reasons, the summary sentence, the e-mail list rules
and the batch summary. `lib/paging.ts` (`allSuppliers`, `allCustomers`) reads every page of the API (200 rows each): the lists
and the pickers of suppliers and customers no longer stop at the first 200 (E-IMP-02-1). Playwright: `import-journey.spec.ts`
(Comprador imports suppliers, Controller activates them; Vendedor imports customers, Controller approves their terms, Crédito
activates them), with synthetic `.csv` files.

## FIS1b-07 — proforma screens (E-FIS1b-1…11, E-FIS1b-01-1…14, E-FIS1b-05-1)

| Screen | What it does |
| --- | --- |
| Ventas › Nuevo pedido / Pedido | «Exención de ITBIS (CONFOTUR)»: no aplica, or in process with proformas collected with or without ITBIS (`exemptionPending`, `proformaCollectsItbis`; only in DRAFT). The order shows the mark and links to the proformas |
| Facturación › Proformas | Proformas by customer (open, invoiced, voided): delivery, due text, net, ITBIS, collected, deposit, balance, certification. `invoice:create` ticks whole OPEN proformas of one customer and creates the invoice: «Con ITBIS», or «Exenta e-CF 44» with an ACTIVE authorization that cites every ticked proforma (`authorizationsFor`) |
| Facturación › Proforma | Detail and print view (letter; signature and stamp of the supplier; watermark ANULADA): lines with ITBIS, what it collects, the receipts assigned, history. «Anular proforma» with a reason (`proforma:void`) |
| Cobros › Registrar cobro | Notice when the customer has proformas with a balance; «Dejar sin aplicar» records the receipt without applying the suggestion to invoices |
| Cobros › Recibo | «Asignar a proformas» (one amount per open proforma, `receipt:apply`), the assignments with «Liberar asignación» (reason), and «Devoluciones al cliente»: Cobros prepares (`customer_refund:prepare`), the Controller releases with step-up (`customer_refund:release`, never who prepared it), a PREPARED one is voided with a reason. The header shows unapplied, assigned and available |
| Fiscal › Registrar autorización | «Proformas que cita el certificado»: the customer's OPEN proformas no other authorization cites; ticking any hides the scope lines — the server computes the scope from them (E-FIS1b-01-8). The detail lists them and links to invoicing |
| Ventas › Antigüedad | Columns «En proforma» and «Depósito ITBIS» apart from the fiscal receivable, with each proforma under its customer |
| Ventas › Estado de cuenta | «Devolución al cliente» entries (DEV-…) and, below the balance, the open proformas with their own balance (screen and print view) |
| Tesorería › Conciliación | An UNMATCHED debit line offers «Conciliar con devolución DEV-…» for the released refunds of that account and amount |

`lib/proformas.ts` (unit-tested: `tests/unit/proformas.test.ts`) holds the Spanish words for states and certification, the due
text and `authorizationsFor`. `components/ReceiptProformas.tsx` holds the receipt's assignment and refund blocks. The UI does no
money arithmetic: amounts are the server's; comparisons only decide what to show.

Playwright `proforma-journey.spec.ts` (desktop and mobile, E2E-P1): Vendedor marks the order → Despacho delivers twice → Facturación
sees two proformas (not billable by conduce; aging column) → Cobros records 2,950.00 unapplied and assigns it → Facturación registers
the certification citing both → Especialista fiscal verifies → one e-CF 44 of 2,500.00, paid on issue → Cobros prepares the refund of
450.00 and cannot release it → the Controller releases it → the statement shows «Devolución al cliente». The dev seed approves P-36.
The refund's match with the bank statement is covered by E2E-P1 over the API (`ProformaAcceptanceTests`).

## MAIL-03 — documents by e-mail (E-MAIL-5, 6, 7, E-MAIL-01-4, 6…10)

A «Correo» section (`components/DocumentMail.tsx`) on Ventas › Cotización (`quote:email`), Facturación › Proforma
(`proforma:email`), Despacho › Conduce (`delivery:email`) and Ventas › Estado de cuenta (two of them, `statement:email`: the
statement of the period on screen and today's open invoices by age).

- «Enviar por correo» opens a form: the customer's saved e-mails ticked (`savedEmails`), «Otros correos» (commas, semicolons or
  spaces), an optional message. `recipientsOf` / `recipientsError` check 1–10 valid addresses before the command. The button is
  disabled, with the reason beside it, while the document would print with a watermark (E-MAIL-01-7) or when the deployment's
  `mailMode` is OFF; in REDIRECT the form says the mail will not reach the customer.
- The history lists every sending: recipients, state («En cola», «Enviado», «Fallido») with where it ended up (`deliveryText`),
  who sent it, «Descargar PDF» (the exact PDF sent) and «Reintentar» for a failed one (`mail:retry`). While a message is queued the
  list refreshes every 3 seconds.
- Without the permission the section shows only the history, and nothing when there is none.

Playwright `mail-journey.spec.ts` (desktop and mobile): the Vendedor sends the seeded quote (one saved e-mail unticked, one typed,
a malformed one refused), sees it «Enviado — Redirigido…» and downloads `COT-….pdf`; the Vendedor is not offered to send the
statement; Cobros sends it.

## FLT-01 — fleet details (E-FLT-1…5)

- Maestros › Vehículos y choferes: the vehicle form asks for the Ficha (required) and the insurance policy number; the table shows
  Ficha, Placa, Capacidad and Póliza, with one «Editar» per row (a vehicle from before shows «Sin ficha»). The driver form and row
  carry «Vencimiento de la licencia»; from 30 days before, the row shows «Licencia: vence en N días» / «venció hace N días»
  (`lib/fleet.ts`: `normalizeFleetCode`, `isFleetCode`, `vehicleName`, `licenseWarning`).
- Despacho: the truck picker names each vehicle «BR 09 · L123456 (12,000 kg)»; picking a driver whose licence is about to expire or
  expired shows the warning and lets the dispatch go on (E-FLT-4). The board lists Ficha and Chofer per conduce; the printed conduce
  shows «Ficha BR 09 · Placa … · chofer».
- Playwright: the sales journey edits the driver's licence date, sees the warning in both places and the ficha on the print.

## CF1-05 — «Venta de contado» (E-CF1-05-1…14)

- Ventas › Venta de contado (`/ventas/contado/`, with `cash_sale:create`): the cash sales, with buyer and total to pay. «Nueva venta
  de contado» (`/ventas/contado/nueva/`): plant, delivery, products (server preview with ITBIS and total) and the buyer; the notice
  on top states the identification amount of the rule in force, or that the rule is missing.
- The sale (`/ventas/venta-contado/?id=`): the three steps; «Enviar a pago» fixes the total; the «Cobro» block shows ITBIS, total,
  assigned, what counts and what is still owed (all from the server), the payments with their situation, and for Caja
  (`receipt:record` + `receipt:apply`) the payment form — «Cobrar» records the receipt and assigns it (`amountToAssign`: the smaller
  of the receipt and what is owed). Cash and cheques over what is owed are refused by the form (`paymentAmountError`). Without those
  permissions the block says «El cobro lo registra Caja». «Verificar pago», «Volver a borrador», «Quitar <recibo>» (with a reason),
  «Cancelar venta», «Planificar conduce».
- Ventas › Pedidos marks cash sales «Contado» and opens them on their screen; the credit order page sends a cash sale there.
- Facturación: the fiscal package of an E32 shows the buyer as receiver; `RecordEcfForm` with `consumer` makes the receiver optional
  (cédula or RNC, or passport, or none) for an E32 and for the E34 of an E32.
- Fiscal › Reglas fiscales: kind «Identificación del consumidor final» with a single amount field.
- Inicio: «Hacer una venta de contado» and «Ventas de contado pendientes de pago».
- `lib/cashSales.ts` (buyer identification, payment checks, steps); Playwright `cash-sale-journey.spec.ts` (desktop and mobile).

