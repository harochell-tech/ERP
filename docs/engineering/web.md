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
