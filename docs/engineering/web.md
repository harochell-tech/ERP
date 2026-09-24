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

Master data has no screens in VS#1 (API only).

## Local development (E-PR18b-9)

`tests/Rochell.DevStack` starts PostgreSQL 17 in Docker, applies every migration, seeds a company from the test fixtures
(plant, locations, ACTIVE supplier and items, accounts and maps, periods, policies, approved posting rules, active fiscal rules
with TEST sources) with one user per slice role and a plant-scoped storekeeper, and runs the real API host on Kestrel with the
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
| `npm test` | Vitest: decimal formatting and validation, plant scope, step-up drafts, client headers and problems, error dictionary |
| `npx playwright test` | Journey in Chromium against the dev stack: the buyer creates and submits a PO, the approver approves it, the storekeeper receives it and sees it POSTED |

The other flows are covered at the API level (`tests/Rochell.Api.Tests`, including AT-01 and AT-02 over HTTP).
