# HTTP API (PR-18a)

`Rochell.Api` is transport only: it maps HTTP onto the existing `CommandPipeline`, `QueryPipeline`, `SqlCommandAuthorizer`
and `SessionService`, and adds no business rule (E-PR18-1…7). The web UI (`web/`, PR-18b) consumes it through types
generated from the committed `src/Rochell.Api/openapi.json`.

## Sign-in and sessions (E-PR18-2)

| Step | What happens |
| --- | --- |
| `GET /api/v1/auth/login?returnUrl=/…` | Redirect to Google (authorization code + PKCE, `hd` = the organization's domain) |
| `GET /api/v1/auth/callback` | The OIDC handler redeems the code and validates the ID token (signature, issuer, audience, expiry, nonce); `SessionService.StartOidcSessionAsync` applies the Rochell rules (verified e-mail, hosted domain, active human user with an employee) and opens `iam.session` |
| Cookie | `__Host-rochell-session`: HttpOnly, Secure, SameSite=Strict, host-only. The value is the session id **protected with ASP.NET Data Protection**, so a session id read from `command_log` or `domain_event` cannot be used as a cookie |
| Callback page | A small HTML page that navigates to `returnUrl` (local paths only). A plain redirect would not work: the callback is a cross-site navigation, and the browser would not send the new Strict cookie on it |
| `GET /api/v1/auth/step-up?returnUrl=…` | New authorization with `prompt=login` and `max_age=0`. The id of the session to re-authenticate travels in the protected OIDC state, because the Strict cookie is not sent on the callback; `RecordStepUpAsync` checks that the same Google identity owns the session |
| `POST /api/v1/auth/logout` | Sets `logout_at` and deletes the cookie |
| `GET /api/v1/session` | User, expiry, step-up freshness, and per company the assignments (company-wide or per plant, each with the permissions it grants, E-PR18b-8) and permissions valid now. Does not refresh activity |

Expiry, idle timeout, step-up age and user status stay in the database and are checked on every request (E-PR03-2, E-PR03-6).
Every non-GET request under `/api` must send `X-Rochell-Csrf: 1` (anti-CSRF; no CORS is granted). No ASP.NET identity cookie
exists; the OIDC handler's transient sign-in scheme is never used.

CI uses a simulated IdP (`tests/Rochell.SimulatedIdp`, also used by the dev stack, see [web.md](web.md)): discovery, JWKS and the token endpoint answer the real
OIDC handler's backchannel, and tests play the browser leg. It exists only under `tests/`. Staging needs the Google Workspace
client (A-03).

## Commands (E-PR18-3)

`POST /api/v1/companies/{companyId}/{module}/{command}`: the module is the assembly that owns the handler (`master-data`,
`procurement`, `finance`, `tax`, `reconciliation`, `audit`, `identity`) and the command is its type name in kebab-case
(`create-purchase-order`). All 44 production commands are exposed; a test fails if a handler has no endpoint.

- The body is the command without `companyId`, `sessionId` and `idempotencyKey`: those come from the route, the cookie and
  the `Idempotency-Key` header (1–200 characters, one per intent). A body that sets them, has unknown members, misses a
  required member or is not JSON is a 400.
- Decimals are JSON strings (`"40000.00"`, ADR-015); a JSON number is rejected. Timestamps need an offset and are stored in
  UTC; dates are `yyyy-MM-dd`.
- Response: `{ commandId, resultRef, replayed, result }` where `result` is the handler's `command_log.result_payload`. A repeated
  key returns the first result with `replayed = true` and the header `Idempotency-Replayed: true`.
- `X-Correlation-Id` (a UUID) may be sent; otherwise one is generated. It is echoed and reaches `command_log`, events and
  `obs.request_log` (flushed every second by the host).

Errors are RFC 9457 `application/problem+json` with `code` (the domain code) and `correlationId`:

| Status | Codes |
| --- | --- |
| 400 | `INVALID_REQUEST`, `IDEMPOTENCY_KEY_REQUIRED`; on queries `INVALID_PARAMETER` |
| 401 | `SESSION_INVALID`, `SESSION_EXPIRED` |
| 403 | `NOT_AUTHORIZED`, `STEP_UP_REQUIRED` (the UI sends the user to `/auth/step-up` and retries with the same key), `CSRF_HEADER_REQUIRED` |
| 404 | queries only: `NOT_FOUND` |
| 409 | `VERSION_CONFLICT`, `CONCURRENCY_CONFLICT` (serialization retries exhausted; retry with the same key) |
| 422 | every other business rule (`APPROVER_IS_CREATOR`, `FISCAL_GATE_CLOSED`, …) |
| 503 | `SERVICE_UNAVAILABLE` (hash verification without WORM storage) |
| 500 | `INTERNAL_ERROR`, details only in the log under the correlation id |

## Queries (E-PR18-4)

`GET /api/v1/companies/{companyId}/{module}/{resource}` on the query pipeline (READ ONLY after authorization, E-PR17-1).
Lists take `limit` (1–200, default 50) and `offset`.

| Resource | Permission |
| --- | --- |
| `master-data/suppliers`, `master-data/items` (with UOM conversions), `master-data/plants` (with locations) | `master_data:read` |
| `procurement/purchase-orders`, `…/{id}` (lines, receipts, status history) | `purchase_order:read` |
| `procurement/goods-receipts`, `…/{id}` (lines and lots, reversal, corrections, history), `procurement/receipt-corrections` | `goods_receipt:read` |
| `procurement/supplier-invoices`, `…/{id}` (lines with match results, determined taxes, AP document, history) | `supplier_invoice:read` |
| `reconciliation/periods?year=` (component states, reopen requests) | `period:read` |
| `reconciliation/runs`, `…/{runId}` (exceptions) | `reconciliation:read` |
| `finance/events/{sourceEventId}/journals`, `finance/entries/{glEntryId}/explanation` (EX-01, E-PR17-6) | `audit:read` |

Plant scope: purchase-order, goods-receipt and master-data queries accept `plantId`. With it, the reader needs an assignment
for that plant or the whole company and sees only that plant's documents (a document of another plant is 404); without it a
company-wide assignment is required — the same rule as for commands. Migration `0020` seeds the five READ permissions and
grants them to the roles that work each document plus the Controller and the Auditor (44 permissions in total).

## Background services (E-PR18-5)

| Service | Switch | Behaviour |
| --- | --- | --- |
| Sealer | `Rochell:Sealer:Enabled`, `Interval` (5 s) | `LedgerSealer.SealAllAsync` as `rochell_sealer`; a failed pass is logged and retried at the next tick |
| Daily digest | `Rochell:Digest:Enabled`, `RunAt` (00:15 local), `SigningKeyPem` | Digests the previous day (also on start, if due). **Does not start** without WORM storage or the signing key; logs Critical and sealing continues |
| request_log | always | Flushes `obs.request_log` every second and at shutdown |

WORM (exactly one of the two; both, or an unusable one, logs Critical and leaves the digest off and `audit/verify-hash-chain`
answering 503):
- `Rochell:Audit:S3` — S3 Object Lock, COMPLIANCE mode (B-03, E-B03-3/4), any environment: `Bucket` (created with Object Lock),
  `Region`, `RetentionDays` (staging 7), optional `ServiceUrl` (S3-compatible endpoints) and `AccessKeyId` / `SecretAccessKey`
  (otherwise the AWS default chain). The host checks the bucket's Object Lock configuration before using it.
- `Rochell:Audit:FileSystemWormRoot` — `FileSystemWormStore`, accepted only when `core.deployment_environment` is TEST.
The outbox dispatcher is not hosted: VS#1 has no event consumers.

## Configuration

Section `Rochell` (environment variables `Rochell__…`): `AppConnectionString` (a `rochell_app` login),
`SealerConnectionString` (a `rochell_sealer` login, only with the sealer or digest on), `Identity:HostedDomain`,
`Oidc:Authority` (default Google), `Oidc:ClientId`, `Oidc:ClientSecret`, `DataProtectionKeysPath` (persist keys; losing them
only signs users out), `Audit:DigestPublicKeyPem`, `WebRoot` (the `web/out` export to serve from the same origin, E-PR18b-2; the
host refuses to start if it does not exist). In Development only, `X-Forwarded-*` from loopback is honoured (for `next dev`). The host refuses to start without the required values and only in
Development, Test or Staging. `/openapi/v1.json` is served in Development and Test.

## OpenAPI (E-PR18-7)

`OpenApiDocumentTests` compares the generated document with `src/Rochell.Api/openapi.json`. After an intended change:

```bash
ROCHELL_UPDATE_OPENAPI=1 dotnet test tests/Rochell.Api.Tests --filter OpenApiDocumentTests
```

The document marks decimals as `type: string, format: decimal`, omits the server-filled fields from command bodies, and lists
the `Idempotency-Key` and `X-Rochell-Csrf` headers and the session cookie.

## Tests

`tests/Rochell.Api.Tests` runs the real host (`WebApplicationFactory<Program>`, Test environment) on a migrated database per
test, as `rochell_app`, with the simulated IdP. It covers sign-in, rejection, forged cookies, logout, anti-CSRF, open redirects,
step-up, idempotent replay, every error class, plant scope, the sealer, WORM gating, endpoint coverage, the OpenAPI document, and
**AT-01 and AT-02 end to end over HTTP** (each actor signs in and works through the API; deployment configuration comes from
the fixtures).
