# Alanube API FE DOM (Dominican Republic e-CF) — technical summary

Source: Alanube developer portal (`https://developer.alanube.co`, product "API FE DOM", OpenAPI 3.0.3, `info.version` 1.0.0,
reference pages updated 2026-06-04). Every `reference/*.md` page embeds the OpenAPI definition of its operation; field names
below are copied from those definitions. Researched 2026-10-07. Factual summary only.

Conventions used here: **Req = Y** means the field is in the schema's `required` list. "Conditional" in the docs
("Condicional a …") is DGII business-rule conditionality that the JSON schema does **not** encode — Alanube / DGII validate it
server-side.

---

## 1. Environments, base URLs, authentication

| Item | Value (from the OpenAPI `servers` block / docs) |
| --- | --- |
| Sandbox base URL | `https://sandbox.alanube.co/dom/v1/` |
| Production base URL | `https://api.alanube.co/dom/v1/` |
| MCP endpoint (sandbox, optional) | `https://sandbox-mcp.alanube.co/mcp/rd` (same Bearer token; wraps the REST API) |
| Auth scheme | `securitySchemes.BearerAuth = { type: http, scheme: bearer }`; every operation declares `security: [{BearerAuth: []}]` |
| Header | `Authorization: Bearer <token>` |
| Content type | `application/json` (except `POST /sign-document`, which is `multipart/form-data`) |

- **How the token is obtained / renewed / expiry: not documented.** No login, token or refresh endpoint exists in the
  published API. The MCP page says only: "Token de sandbox: solicítalo desde el dashboard de tu cuenta de Alanube". The token is
  bound to a **user**, and "la empresa asociada al token" is the user's `main` company (see §3). The format (JWT or opaque) and
  lifetime are not stated.
- **Multi-company:** one user owns one `main` company and any number of `associated` companies. Endpoints exist in two forms:
  the plain form acts on the main company; the `…/idCompany/{idCompany}` form (or a `company.id` / `idCompany` body field)
  acts on an associated company.
- **Rate limits: not documented** (no 429 response, no rate headers anywhere in the DOM reference).
- **Idempotency: not documented for DOM.** There is no idempotency header. De-facto duplicate protection is the e-NCF itself:
  `AP3001 ENCF_USED` ("ENCF document was used"), `AP3011 ENCF_IN_PROCESS` ("ENCF document is in process with id: …" — added
  2025-09-30 to stop concurrent emissions with the same sequence), `AP3005 PRIMARY_USED` ("Primary id was used").
  (An idempotency section exists only in a Costa Rica (CRI) changelog, not DOM.)
- Report endpoints limit date ranges to 90 days (`AP9003 DATE_RANGE_IS_TOO_LONG`).
- IDs are ULIDs (26 chars, `AP10100 ID_PATTERN`).

### Error format

Two formats coexist in the docs:

- Older (still in most OpenAPI response schemas): `{ "code": "<string>", "message": "<string or string[]>" }`, e.g.
  `{"message": ["instance.identification is not of a type(s) string"], "code": 400}`,
  `{"message": "An unexpected error has occurred", "code": "EPR500"}`.
- **Unified format** (changelog "Mejora de la documentación y unificación del formato de errores"; live in sandbox; production
  date given as both "3 de febrero de 2026" and "22 de abril de 2026" in the same page; old formats deprecated):

```json
{ "code": 400, "errors": [ { "code": "ERROR_CODE", "message": "Error description in English", "field": "fieldName" } ] }
```

`code` (top) = HTTP status (number); `errors[].code` = unique string id (e.g. `AP3001`); `field` optional.
Documented HTTP statuses on document endpoints: 201 (created), 200 (queries), 400 (validation), 404 (not found), 500.

---

## 2. Asynchronous issuance model and status lifecycle

- **Issuance is asynchronous for every type except e-CF 32 under DOP 250,000.** `POST` returns **201** immediately with an
  Alanube `id` and `status` (typically `REGISTERED`); the DGII result arrives later (poll `GET /<type>/{id}` or webhook
  `emissionFinished`). The MCP page: "si emites cualquier otro tipo de documento (31, 33, 34, 41, 43, 44, 45, 46, 47), DGII
  procesa la emisión de forma asíncrona. La primera respuesta trae un estado interno del ciclo de vida (`REGISTERED`,
  `WAITING_RESPONSE`, …)".
- **e-CF 32 < DOP 250,000** is **synchronous** (the 201 already carries `legalStatus` and `governmentResponse`), unless DGII
  communication fails, in which case the document falls back to async and the 201 carries a `response` array, e.g.
  `{"response":[{"message":"The connection with DGII has timed out. We have scheduled your document for automatic transmission. Check it out in a moment","code":"AEP2006"}]}`.
  E32 ≥ DOP 250,000 is async. E32 responses also carry `resumeXml` ("XML del resumen firmado enviado a la DGII").
- **Who assigns the e-NCF: the integrator.** `idDoc.encf` is **required** (13 chars, e.g. `E310000000005`) together with
  `idDoc.sequenceDueDate` (`FechaVencimientoSecuencia`, required on 31/33/41/43/44; absent on 32 and 34). Alanube does not
  allocate sequences; there is no endpoint to register NCF ranges. Unused ranges are voided through `POST /cancellations`.
  `AP10104 ENCF_PATTERN`: prefix must be one of E31, E32, E33, E34, E41, E43, E44, E45, E46, E47 and 13 chars total.
- **Sender RNC check** (2025-09-22): `sender.rnc` must equal the issuing company's `identification` (main company if
  `company` is omitted); otherwise the document ends failed with `AP1016 SENDER_RNC_NOT_MATCH`.

### `status` (Alanube processing state) — enum in every response schema

`REGISTERED` → `TO_SEND` → `WAITING_RESPONSE` → `TO_NOTIFY` → `FINISHED`

Additional values that appear **only in changelogs**, not in the OpenAPI enums: `PENDING` (credit/debit note waiting for the
referenced invoice to reach a final DGII state) and `FAILED` (rejected by Alanube's own business rules before reaching DGII;
the body then carries `"error": {"code": "AP3012", "message": "…"}` and `legalStatus: null`).

### `legalStatus` (DGII outcome)

`ACCEPTED` | `ACCEPTED_WITH_OBSERVATIONS` | `REJECTED` (null until known). The description text says "ante la DIAN" (copy
from the Colombian product). `governmentResponse.code` (integer, example `4`) is "Código que corresponde al estado legal del
documento ante la DGII"; `governmentResponse.value[]` = `{ "codigo": "<string>", "valor": "<message>" }`, example
`{"valor": "El campo MontoGravadoI1 del área Totales de la sección Encabezado no coincide con la sumatoria de los valores del detalle gravado con tasa 18% menos descuentos mas recargos.", "codigo": 1934}`.

### E32 async retry flow (changelog "Flujo de procesamiento asíncrono – Facturas de consumo (E32)")

On a 500 / timeout with DGII: immediate DGII lookup → if not found, lookups at 5, 10 and 15 min after the request → if still
not found, resend; on another 500 the loop restarts; on "documento duplicado" one more lookup (accepted → accepted, else
rejected); any other error → rejected. After it is marked accepted/rejected "cualquier gestión adicional debe realizarse de
forma manual".

### Credit / debit note async validations (changelog 2025-09-30)

Before sending a note to DGII Alanube checks the referenced invoice in its own records:
- referenced document still in process → note stays `PENDING`;
- referenced `legalStatus` `ACCEPTED|ACCEPTED_WITH_OBSERVATIONS` → note is sent;
- referenced `REJECTED` (or failed) → note `status = FAILED`, `AP3012 DOCUMENT_REFERENCED_DOCUMENT_REJECTED`.
- Remaining amount = invoice total + Σ accepted debit notes − Σ accepted credit notes.
  `modificationCode = 3` (corrige montos): note total must **not exceed** the remainder, else `FAILED` / `AP3013`.
  `modificationCode = 1` (anulación total): note total must **equal** the remainder, else `FAILED` / `AP3014`.
  `modificationCode = 2` (corrige texto): total must be 0, else HTTP 400 `AP3015` (the changelog text also mentions `AP3016`).

---

## 3. Webhooks

Configured per company in the `webhooks` object of `POST /company`, `PATCH /company`, `PATCH /company/{id}` (no separate
webhook endpoints). Alanube sends `POST` requests to the configured URL. On save it sends a test `POST` with body
`{message: 'Test message'}` and reports HTTP/network errors in its response (`AP1014 WEBHOOK_URL_INVALID`).

| Webhook | Path in `webhooks` | Fires when |
| --- | --- | --- |
| `emissionFinished` | `webhooks.documents.emissionFinished` | "finaliza el proceso de un documento electrónico" |
| `cancellations` | `webhooks.documents.cancellations` | "finaliza el proceso de anulaciones" |
| `reception` | `webhooks.documents.reception` | "se recibe un documento o se genera una aprobación comercial" |
| `governmentStatusChanged` | `webhooks.general.governmentStatusChanged` | "cambia el estado de la compañía ante la DGII" |

Each entry: `url` (string, required except on `governmentStatusChanged`), `headers` (object of custom headers sent with each
call, e.g. `{"x-api-key": "api_key"}` or `{"Authorization": "Bearer tokendeautenticacion"}`), `status` (`active` | `inactive`).

`emissionFinished` (only) also accepts `auth` — a pre-call to obtain headers:
`auth.status` (req, `active|inactive`), `auth.url` (req), `auth.headersRequest` (object), `auth.body` (object, sent as POST
body), `auth.headersResponse.fields` (req, string[] — response headers of the auth endpoint copied into the main call),
`auth.headersResponse.cache.status` (req) and `.ttl` (req, integer seconds).

**Retries** (documented under `auth`): Alanube retries the webhook on 401/403 (only when an auth endpoint is configured:
it refreshes the token and retries once; a second 401/403 gives up), on 5xx, and on network errors (timeout, DNS…). Number of
retries, back-off and ordering are not documented.

**Not documented:** the webhook **payload** (body schema) for any event, any **signature** / HMAC verification (the only
protection is the static custom headers you configure), event ids, de-duplication.

---

## 4. Company endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| POST | `/company` | Create company (`createCompany`) → 201 |
| GET | `/company` | Company of the token (`getSelfCompany`) |
| PATCH | `/company` | Partial update of the token's company ("únicamente se actualizará la información enviada") |
| GET | `/company/{id}` | Company by id |
| PATCH | `/company/{id}` | Partial update by id |
| GET | `/companies/associated?limit=1..100&from=<cursor>` | Associated companies, cursor-paginated (`metadata.from`, `metadata.to` = next cursor or null, `metadata.results_count`) |
| GET | `/companies/{id}/emitted-documents` | Total documents issued by a company (all time) |
| GET | `/companies/{id}/accepted-documents` | Total accepted (`ACCEPTED|ACCEPTED_WITH_OBSERVATIONS`) |
| GET | `/reports/users/documents/total?legalStatus=A,B&dateFrom&dateUntil` | Totals per user/company (≤ 90 days, default last 30) |
| GET | `/reports/companies/{idCompany}/documents/total` | Same for one company |
| GET | `/provider-info` | Alanube provider data (`softwareType` "EXTERNO", `softwareName` "Alanube", `providerData.rnc` 132109122, name "Alanube Soluciones SRL") |

### `POST /company` request body

| Field | Type | Req | Notes |
| --- | --- | --- | --- |
| `name` | string | Y | Nombre/Razón social |
| `tradeName` | string | | Nombre comercial |
| `identification` | string | Y | pattern `^[0-9]{9}$|^[0-9]{11}$` (RNC or cédula) |
| `type` | string | | `main` \| `associated` (`AP1004 HAS_MAIN_COMPANY`: a user has one main company; `AP1015 MUST_HAVE_MAIN_COMPANY`) |
| `address` | string | Y | max 100 |
| `province` | string | | |
| `municipality` | string | | |
| `email` | string | | email, max 80 |
| `certificate` | object | Y | `name` (Y), `extension` (Y, e.g. `pfx`, no dot — `AP16003`), `content` (Y, base64 of a PKCS#12 — `AP1011`, `AP16004`), `password` (Y) |
| `webhooks` | object | | see §3 |
| `notificationByEmail` | object | | `enabled` (boolean, Y; default disabled) — Alanube e-mails the receiver when DGII accepts; `message` (≤ 300) appended to the e-mail |
| `logo` | string | | base64 image ≤ 150 KB (`AP1009`), embedded in every PDF; documents keep the logo they were created with |

Response 201 / GET: `{ "company": { id, name, tradeName, identification, type, address, province, municipality, email,
companyUrls: { reception, approval, authentication }, certificate: { name, extension, issuerName, startDate, endDate },
webhooks, notificationByEmail } }`. `companyUrls` ("Urls necesarias para el proceso de certificación y las cuales serán
registradas en la DGII para la recepción de documentos", example `https://almost.alanube.co/dom/v1/<id>`) are the receiver
URLs you register with DGII.

**The company record holds no NCF ranges / sequences** — only identity, certificate, webhooks, e-mail settings and logo.
Certificate errors: `AP1001 INVALID_CERTIFICATE`, `AP1003 CERTIFICATE_REQUIRED`, `AP1005`/`AP1013` expired,
`AP1010 INVALID_CERTIFICATE_PASSWORD`, `AP1012 INVALID_CERTIFICATE_LENGTH`.

---

## 5. DGII certification (set de pruebas) through Alanube

| Method | Path | Body / params | Response |
| --- | --- | --- | --- |
| POST | `/set-tests` | `idCompany` (ulid, optional — associated company), `retryNumber` (number 0–99, "solo se usa para cambiar el número con el que se genera el set de pruebas"), `itemExample` (Y): `billingIndicator` (Y, 1–4; 0 not allowed), `itemName` (Y, ≤ 80), `goodServiceIndicator` (Y, 1 Bien / 2 Servicio), `itemDescription` (≤ 1000), `unitPriceItem` (Y, integer 1–99,999,999) | 201 `{ id, status ("REGISTERED"), idCompany, companyIdentification, creationDate }` |
| GET | `/check-set-tests/{id}` and `/check-set-tests/{id}/idCompany/{idCompany}` | — | 200 `{ testId, idCompany, status (e.g. "ACCEPTED"), retryNumber, totalDocumentProcessed (e.g. 20), documentsInfo[]: { documentType, idDocument, status: ACCEPTED|REJECTED|IN_PROGRESS, encf, xml, pdf }, resumesInfo: { zipUrl, documents[]: { documentType, idDocument, status, encf } } }` |
| POST | `/sign-document` and `/sign-document/idCompany/{idCompany}` | `multipart/form-data`, field `xml` (binary) | 200 `{ "signedDocumentUrl": "…" }` — "Firmar los documentos requeridos para el proceso de la dada de alta utilizando la información de la compañía" |

So Alanube generates the DGII test set (≈ 20 documents plus the consumption summaries) from one representative item, sends it
and reports per-document status; `sign-document` signs arbitrary XML files the DGII onboarding portal requires, with the
company's certificate. Errors: `AP4001 SET_TEST_IN_PROGRESS`, `AP4004 SET_TEST_NOT_FOUND`, `AP4005 SET_TEST_DUPLICATED_RETRY_NUMBER`,
`AP1006 COMPANY_INFO_INCOMPLETE_FOR_SET_TEST`, `AP1008 COMPANY_HAS_BEEN_CERTIFIED`. The remaining DGII portal steps (declaración
jurada, registering `companyUrls`, approval) are not described.

---

## 6. e-CF 31 — Factura de Crédito Fiscal Electrónica

### Endpoints

| Method | Path | Notes |
| --- | --- | --- |
| POST | `/fiscal-invoices` | Issue (`createInvoiceFiscals`) → **201** |
| GET | `/fiscal-invoices/{id}` | Status (`checkInvoiceFiscals`); query `pdfType` = `generic` (default) \| `pos` |
| GET | `/fiscal-invoices/{id}/idCompany/{idCompany}` | Same for an associated company |
| POST | `/fiscal-invoices/notify-by-email` | Body `{ id (Y), idCompany, mail (email; default = buyer e-mail of the document), pdfType }` → 200 `{ "message": "Notify by email event emitted." }` |

Headers: `Authorization: Bearer <token>`, `Content-Type: application/json`. Other types follow the same pattern (§7).

### Request body (complete list of fields in the OpenAPI schema)

| Field (JSON path) | Type | Req | Constraints / enum | DGII tag | Notes (from docs) |
|---|---|---|---|---|---|
| `company` | object |  |  |  | Información de la compañía con la que se emitirá (diferente a la principal) |
| `company.id` | string |  | format=ulid |  | Id de la compañía |
| `idDoc` | object | Y |  | IdDoc | Identificación del documento |
| `idDoc.encf` | string | Y | minLength=13 maxLength=13 | eNCF | Secuencia autorizada por la DGII. |
| `idDoc.sequenceDueDate` | string | Y | format=date | FechaVencimientoSecuencia | Fecha de vencimiento de la secuencia de e-NCF. |
| `idDoc.deferredDeliveryIndicator` | integer |  | maxLength=1 | IndicadorEnvioDiferido | Identifica a los contribuyentes que han sido previamente autorizados a tener ventas a través de dispositivos móviles offline, tales como ventas con Handheld, entre otros. Condicional a que se encuentre autorizado hacer envíos d… |
| `idDoc.taxAmountIndicator` | integer |  | enum=[0, 1] | IndicadorMontoGravado | Indica si en cada línea de detalle (productos/servicios), el monto se encuentra con ITBIS incluido (impuestos adicionales no están incluidos en el precio del item). El campo recibe uno de los siguientes valores: 0. Si los monto… |
| `idDoc.incomeType` | integer | Y | enum=[1, 2, 3, 4, 5, 6] maxLength=2 | TipoIngresos | Indica el tipo de ingreso recibido, según clasificación del formato de envío de ventas de Bienes o Servicios. |
| `idDoc.paymentType` | integer | Y | enum=[1, 2] maxLength=2 | TipoPago | Indica el tipo de pago del cliente. Las facturas por entrega gratuita (código 3), no son válidas para crédito fiscal. |
| `idDoc.paymentDeadline` | string |  | format=date | FechaLimitePago | Solo para facturas a crédito. Condicional a que el tipo de pago sea a crédito. |
| `idDoc.paymentTerm` | string |  | maxLength=15 | TerminoPago | Indica el tiempo establecido para el pago de la factura y se debe especificar si el mismo es en horas, días, semanas, meses u otro. Ejemplo: 1) 72 horas 2) 120 días 3) 1 semana 4) 3 meses. |
| `idDoc.paymentFormsTable` | array |  | maxItems=7 | TablaFormasPago | Hasta 07 repeticiones. Contiene los dos campos siguientes. |
| `idDoc.paymentFormsTable[].paymentMethod` | integer | Y | enum=[1, 2, 3, 4, 6, 7, 8] maxLength=2 | FormaPago | Indica el método en que se pagará la factura. |
| `idDoc.paymentFormsTable[].paymentAmount` | number | Y | maxLength=19 | MontoPago | Indica el monto asociado para cada forma de pago. Condicional a que exista una forma de pago. |
| `idDoc.paymentAccountType` | string |  | enum=['CT', 'AH', 'OT'] maxLength=2 | TipoCuentaPago | Cuenta de origen de la transferencia o del cheque. |
| `idDoc.paymentAccountNumber` | string |  | maxLength=28 | NumeroCuentaPago | Número de la cuenta si la forma de pago es por cheque o transferencia bancaria. |
| `idDoc.bankPayment` | string |  | maxLength=75 | BancoPago | Banco de la Cuenta. |
| `idDoc.dateFrom` | string |  | format=date | FechaDesde | Período de facturación para Servicios Periódicos Ej. Energía eléctrica, telefónica, otros. Fecha desde (Fecha inicial del servicio facturado). |
| `idDoc.dateUntil` | string |  | format=date | FechaHasta | Período de facturación para Servicios Periódicos. Fecha hasta (Fecha final del servicio facturado). |
| `idDoc.totalPages` | integer |  | pattern=[1-999] maxLength=3 | TotalPaginas | Indica el total de páginas en la que será impreso el e-CF. Cuenta las veces que se repite el campo Página No. de la sección Paginación. Condicional a que exista paginación. |
| `sender` | object | Y |  | Emisor | Emisor |
| `sender.rnc` | string | Y | maxLength=11 | RNCEmisor | Corresponde al RNC del emisor. |
| `sender.companyName` | string | Y | maxLength=150 | RazonSocialEmisor | Nombre o Razón Social del emisor. |
| `sender.tradename` | string |  | maxLength=150 | NombreComercial | Nombre Comercial. |
| `sender.branchOffice` | string |  | maxLength=20 | Sucursal | Indica nombre de la sucursal que emite el e-CF. Corresponde a un dato administrado por el emisor. |
| `sender.address` | string | Y | maxLength=100 | DireccionEmisor | Datos correspondientes a Domicilio de operación del Emisor. |
| `sender.municipality` | string |  | pattern=^[0-9]{6}$ | Municipio | Dato correspondiente al domicilio de operación del Emisor. (see catalog) |
| `sender.province` | string |  | pattern=^[0-9]{6}$ | Provincia | Dato correspondiente al domicilio de operación del Emisor. (see catalog) |
| `sender.phoneNumber` | array |  | maxItems=3 | TablaTelefonoEmisor | Se pueden incluir 3 repeticiones. |
| `sender.phoneNumber[]` | string |  |  |  |  |
| `sender.mail` | string |  | format=email maxLength=80 | CorreoEmisor | Dato correspondiente al correo electrónico del emisor. |
| `sender.webSite` | string |  | format=hostname maxLength=50 | WebSite | Dato correspondiente a la página web del emisor. |
| `sender.economicActivity` | string |  | maxLength=100 | ActividadEconomica | Dato correspondiente a la actividad económica del Emisor (se puede incluir sólo la actividad económica que corresponde a la transacción). |
| `sender.sellerCode` | string |  | maxLength=60 | CodigoVendedor | Identificador del Vendedor. |
| `sender.internalInvoiceNumber` | string |  | maxLength=20 | NumeroFacturaInterna | Corresponde al número interno de la factura. |
| `sender.internalOrderNumber` | string |  | maxLength=20 | NumeroPedidoInterno | Corresponde al número de pedido interno asignado a la factura. |
| `sender.saleArea` | string |  | maxLength=20 | ZonaVenta | Corresponde a la zona de venta del vendedor. |
| `sender.saleRoute` | string |  | maxLength=20 | RutaVenta | Corresponde a la ruta de venta del vendedor. |
| `sender.additionalInformationIssuer` | string |  | maxLength=250 | InformacionAdicionalEmisor | Otra información relativa al Emisor. |
| `sender.stampDate` | string | Y | format=date | FechaEmision | Fecha de emisión del e-CF. |
| `buyer` | object | Y |  | Comprador | Receptor |
| `buyer.rnc` | string | Y | maxLength=11 | RNCComprador | Corresponde al RNC del comprador. |
| `buyer.companyName` | string | Y | maxLength=150 | RazonSocialComprador | Nombre o Razón Social del comprador. En caso de que el e-CF sea tipo 46, el campo es condicional a que exista el campo ‘RNC Comprador’ o ‘Identificador Extranjero’. |
| `buyer.contact` | string |  | maxLength=80 | ContactoComprador | Nombre y teléfono de contacto del comprador. |
| `buyer.mail` | string |  | format=email maxLength=80 | CorreoComprador | Dato correspondiente al correo electrónico del comprador. |
| `buyer.address` | string |  | maxLength=100 | DireccionComprador | Dirección del comprador. |
| `buyer.municipality` | string |  | pattern=^[0-9]{6}$ | MunicipioComprador | Dato correspondiente a dirección de comprador. (see catalog) |
| `buyer.province` | string |  | pattern=^[0-9]{6}$ | ProvinciaComprador | Dato correspondiente a dirección de comprador. (see catalog) |
| `buyer.deliverDate` | string |  | format=date | FechaEntrega | Corresponde a la fecha de entrega del ítem. |
| `buyer.contactDelivery` | string |  | maxLength=100 | ContactoEntrega | Dato de contacto donde será realizada la entrega o envio del ítem (distinto al comprador). |
| `buyer.deliveryAddress` | string |  | maxLength=100 | DireccionEntrega | Corresponde a la dirección o destino del contacto de entrega. |
| `buyer.additionalPhone` | string |  | pattern=\d{3}-\d{3}-\d{4} maxLength=12 | TelefonoAdicional | Dato del teléfono correspondiente al contacto de entrega. |
| `buyer.purchaseOrderDate` | string |  | format=date | FechaOrdenCompra | Corresponde a la fecha de la orden de compra. |
| `buyer.purchaseOrderNumber` | string |  | maxLength=20 | NumeroOrdenCompra | Corresponde al número de orden de compra. |
| `buyer.internalCode` | string |  | maxLength=20 | CodigoInternoComprador | Para identificación interna del comprador, por ejemplo, código del cliente, número de medidor, etc. |
| `buyer.responsibleForPayment` | string |  | maxLength=20 | ResponsablePago | Corresponde a la identificación del que realiza el pago del documento. |
| `buyer.additionalInformation` | string |  | maxLength=150 | Informacionadicionalcomprador | Otra información relativa al comprador. |
| `additionalInformation` | object |  |  | InformacionesAdicionales | Informaciones adicionales |
| `additionalInformation.shippingDate` | string |  | format=date | FechaEmbarque | Corresponde a la fecha del embarque. |
| `additionalInformation.shipmentNumber` | string |  | maxLength=25 | NumeroEmbarque | Dato correspondiente al número de embarque. |
| `additionalInformation.containerNumber` | string |  | maxLength=100 | NumeroContenedor | Dato correspondiente al número de contenedor. |
| `additionalInformation.referenceNumber` | integer |  | maxLength=20 | NumeroReferencia | Dato correspondiente al número de referencia. |
| `additionalInformation.grossWeight` | number |  | maxLength=19 | PesoBruto | Corresponde al peso bruto del contenedor. |
| `additionalInformation.netWeight` | number |  | maxLength=19 | PesoNeto | Corresponde a peso neto del contenedor. |
| `additionalInformation.grossWeightUnit` | integer |  | maxLength=2 | UnidadPesoBruto | Corresponde a la unidad de medida en la que se encuentra el peso bruto de la mercancía. (see catalog) |
| `additionalInformation.unitNetWeight` | integer |  | maxLength=2 | UnidadPesoNeto | Corresponde a la unidad de medida en la que se encuentra el peso neto de la mercancía. (see catalog). |
| `additionalInformation.bulkQuantity` | number |  | maxLength=19 | CantidadBulto | Corresponde a la cantidad de bultos que ampara el documento. |
| `additionalInformation.bulkUnit` | integer |  | maxLength=2 | UnidadBulto | Corresponde a la unidad de medida en la que se encuentra los bultos. (see catalog) |
| `additionalInformation.bulkVolume` | number |  | maxLength=19 | VolumenBulto | Corresponde al volumen de los bultos. |
| `additionalInformation.unitVolume` | integer |  | maxLength=2 | UnidadVolumen | Corresponde a la unidad de medida en la que se encuentra el volumen de los bultos. (see catalog) |
| `transport` | object |  |  | Transporte | Transporte |
| `transport.driver` | string |  | maxLength=20 | Conductor | Corresponde al código o nombre del conductor. |
| `transport.transportDocument` | integer |  | maxLength=20 | DocumentoTransporte | Corresponde al documento de transporte del conductor. |
| `transport.file` | string |  | maxLength=20 | Ficha | Corresponde a la ficha del transporte. |
| `transport.licensePlate` | string |  | maxLength=7 | Placa | Corresponde al número de placa del vehículo del transporte. |
| `transport.transportationRoute` | string |  | maxLength=20 | RutaTransporte | Corresponde a la ruta establecida de transporte. |
| `transport.transportationZone` | string |  | maxLength=20 | ZonaTransporte | Corresponde a la zona de transporte. |
| `transport.albaranNumber` | string |  | maxLength=20 | NumeroAlbaran | Corresponde al número de albarán de entrega. |
| `totals` | object | Y |  | Totales | TOTALES |
| `totals.totalTaxedAmount` | number |  | maxLength=19 | MontoGravadoTotal | Total de la suma de valores de monto gravado ITBIS a diferentes tasas. Condicional a que exista Monto gravado1, y/o Monto gravado 2 y/o Monto gravado 3. |
| `totals.i1AmountTaxed` | number |  | maxLength=19 | MontoGravadoI1 | Total de la suma de valores de Ítems gravados asignados a ITBIS tasa 1 (tasa 18%), menos descuentos más recargos. 12 Condicional a que en la línea de detalle exista algún ítem gravado a tasa ITBIS1. |
| `totals.i2AmountTaxed` | number |  | maxLength=19 | MontoGravadoI2 | Total de la suma de valores de Ítems gravados asignados a ITBIS tasa 2(tasa 16%), menos descuentos más recargos. Condicional a que en la línea de detalle exista algún ítem gravado a tasa ITBIS2. |
| `totals.i3AmountTaxed` | number |  | maxLength=19 | MontoGravadoI3 | Total de la suma de valores de Ítems gravados asignados a ITBIS tasa 3 (tasa 0%), menos descuentos más recargos. Condicional a que en la línea de detalle exista algún ítem gravado a tasa ITBIS3. |
| `totals.exemptAmount` | number |  | maxLength=19 | MontoExento | Total de la suma de valores de ítems exentos, menos descuentos más recargos. Condicional a que en la línea de detalle exista algún ítem exento. |
| `totals.itbisS1` | integer |  | maxLength=2 | ITBIS1 | Tasa de ITBIS 1 (18%). Condicional a que en la línea de detalle exista ítem gravado a tasa 1. |
| `totals.itbisS2` | integer |  | maxLength=2 | ITBIS2 | Tasa de ITBIS 2 (16%). Condicional a que en la línea de detalle exista ítem gravado a tasa 2. |
| `totals.itbisS3` | integer |  | maxLength=2 | ITBIS3 | Tasa de ITBIS 3 (0%). Condicional a que en la línea de detalle exista ítem gravado a tasa 3. |
| `totals.itbisTotal` | number |  | maxLength=19 | TotalITBIS | Total de la suma de valores de ITBIS a diferentes tasas. Condicional a que exista Total ITBIS Tasa 1, y/o Total ITBIS Tasa 2 y/o Total ITBIS Tasa 3. |
| `totals.itbis1Total` | number |  | maxLength=19 | TotalITBIS1 | Valor numérico igual a Monto Gravado ITBIS Tasa1 por la Tasa ITBIS 1. Condicional a que exista Monto Gravado tasa 1 y tasa ITBIS 1. Si existen impuestos selectivos al consumo que formen parte de la base imponible del ITBIS, est… |
| `totals.itbis2Total` | number |  | maxLength=19 | TotalITBIS2 | Valor numérico igual a Monto Gravado ITBIS Tasa2*tasa ITBIS 2. Condicional a que exista Monto Gravado tasa 2 y tasa ITBIS 2. |
| `totals.itbis3Total` | number |  | maxLength=19 | TotalITBIS3 | Valor numérico igual a Monto gravado ITBIS Tasa3*tasa ITBIS 3. Condicional a que exista Monto Gravado tasa 3 y tasa ITBIS 3. |
| `totals.additionalTaxAmount` | number |  | maxLength=19 | MontoImpuestoAdicional | Sumatoria de los campos Monto Impuesto Selectivo al Consumo Específico, Monto Impuesto Selectivo Ad Valorem y Monto Otros Impuestos Adicionales. |
| `totals.additionalTaxes` | array |  | maxItems=20 | ImpuestosAdicionales | Se pueden incluir 20 repeticiones de pares código – valor. Incluye los cinco campos siguientes: |
| `totals.additionalTaxes[].taxType` | integer | Y |  | TipoImpuesto | Dato correspondiente al Código del impuesto adicional de acuerdo con la Tabla I (Codificación Tipos de Impuestos Adicionales). (see catalog) |
| `totals.additionalTaxes[].additionalTaxRate` | number |  |  | TasaImpuestoAdicional | Dato correspondiente a la Tasa del Impuesto Adicional. Se debe indicar la tasa de Impuesto. (see catalog) |
| `totals.additionalTaxes[].selectiveTaxAmountSpecificConsumption` | number |  | maxLength=19 | MontoImpuestoSelectivoConsumoEspecifico | Valor del impuesto selectivo al consumo (ISC) específico asociado al código de impuesto adicional. Condicional a que exista código del 006 al 022 23. El cálculo del monto del ISC específico dependerá de la tasa correspondiente … |
| `totals.additionalTaxes[].amountSelectiveConsumptionTaxAdvalorem` | number |  | maxLength=19 | MontoImpuestoSelectivoConsumoAdvalorem | Valor del impuesto selectivo al consumo (ISC) ad valorem asociado al código de impuesto adicional. Condicional a que exista código del 023 al 039 24. El cálculo del monto del ISC ad valorem dependerá de la tasa correspondiente … |
| `totals.additionalTaxes[].otherAdditionalTaxes` | number |  | maxLength=19 | OtrosImpuestosAdicionales | Valor del impuesto adicional asociado al código de impuesto adicional. Condicional a que exista código del 001 al 005. El cálculo del monto del impuesto adicional dependerá de la tasa correspondiente al código del impuesto en l… |
| `totals.totalAmount` | number | Y | maxLength=19 | MontoTotal | Monto Gravado Total + Monto exento +Total ITBIS + Monto del Impuesto adicional. |
| `totals.nonBillableAmount` | number |  | maxLength=19 | MontoNoFacturable | Total de la suma de montos de bienes o servicios con Indicador de facturación=0. Condicional a que en la línea de detalle exista algún ítem con indicador facturación igual a cero (0). |
| `totals.amountPeriod` | number |  | maxLength=19 | MontoPeriodo | Total de la suma de Monto Total y Monto no Facturable. |
| `totals.previousBalance` | number |  | maxLength=19 | SaldoAnterior | Saldo Anterior. Se incluye sólo con fines de ilustrar con claridad el cobro. |
| `totals.amountAdvancePayment` | number |  | maxLength=19 | MontoAvancePago | Pago parcial por adelantado de la factura que se emite. |
| `totals.payValue` | number |  | maxLength=19 | ValorPagar | Valor cobrado. |
| `totals.itbisTotalRetained` | number |  | maxLength=19 | TotalITBISRetenido | Monto del ITBIS correspondiente a la retención que será realizada por el comprador. Condicional a que en la línea de detalle exista retención. |
| `totals.isrTotalRetention` | number |  | maxLength=19 | TotalISRRetencion | Monto del Impuesto Sobre la Renta correspondiente a la retención realizada de la prestación o locación de servicios. Condicional a que en la línea de detalle exista retención. |
| `totals.itbisTotalPerception` | number |  | maxLength=19 | TotalITBISPercepcion | Monto del ITBIS que el contribuyente cobra a terceros como adelanto del impuesto que éste percibirá en sus operaciones. Condicional a que en la línea de detalle exista percepción. |
| `totals.isrTotalPerception` | number |  | maxLength=19 | TotalISRPercepcion | Monto del Impuesto Sobre la Renta que el contribuyente cobra a terceros como adelanto del impuesto que éste percibirá en sus operaciones. Condicional a que en la línea de detalle exista percepción. |
| `otherCurrency` | object |  |  | OtraMoneda | Otra moneda encabezado |
| `otherCurrency.currencyType` | string | Y | enum=['BRL', 'CAD', 'CHF', 'CHY', 'XDR', 'DKK', 'EUR', 'GBP', 'JPY', 'NOK', 'SCP', 'SEK', 'USD', 'VEF'] | TipoMoneda | Moneda alternativa en que se expresan los Montos. Condicional a que la facturación sea realizada en moneda extranjera. 33 Este campo debe tener uno de los valores indicados en la ‘Tabla Codificación Monedas’. Por ejemplo: “USD”… |
| `otherCurrency.exchangeRate` | number | Y | maxLength=7 | TipoCambio | Factor de conversión utilizado. Condicional a que existan datos en código otra moneda. |
| `otherCurrency.totalTaxedAmountOtherCurrency` | number |  | maxLength=19 | MontoGravadoTotalOtraMoneda | Total de la suma de valores de Monto gravado ITBIS Otra Moneda a diferentes tasas. Condicional a que exista datos en código otra moneda y Monto gravado ITBIS en otra moneda a distintas tasas (18%, 16% y 0%). |
| `otherCurrency.amountTaxed1OtherCurrency` | number |  | maxLength=19 | MontoGravado1OtraMoneda | Total de la suma de valores de Ítems gravados asignados a ITBIS tasa 1 (tasa 18%), menos descuentos en Otra Moneda más recargos en Otra Moneda 35. (Asignados a ítem gravados en Otra Moneda). Condicional a que exista datos en có… |
| `otherCurrency.amountTaxed2OtherCurrency` | number |  | maxLength=19 | MontoGravado2OtraMoneda | Total de la suma de valores de Ítems gravados asignados a ITBIS tasa 2 en Otra Moneda (tasa 16%), menos descuentos en Otra Moneda más recargos en Otra Moneda. (Asignados a ítem gravados en Otra Moneda). Condicional a que exista… |
| `otherCurrency.amountTaxed3OtherCurrency` | number |  | maxLength=19 | MontoGravado3OtraMoneda | Total de la suma de valores de Ítems gravados asignados a ITBIS tasa 3 en Otra Moneda (tasa 0%), menos descuentos en Otra Moneda más recargos en Otra Moneda. (Asignados a ítem gravados en Otra Moneda). Condicional a que exista … |
| `otherCurrency.exemptAmountOtherCurrency` | number |  | maxLength=19 | MontoExentoOtraMoneda | Total de la suma de valores de ítems exentos, menos descuentos en Otra Moneda más recargos en Otra Moneda (asignados a ítems exentos). Condicional a que exista datos en código otra moneda y el ítem contenga indicador de factura… |
| `otherCurrency.itbisTotalOtherCurrency` | number |  | maxLength=19 | TotalITBISOtraMoneda | Total de la suma de valores de ITBIS en Otra Moneda a diferentes tasas. Condicional a que exista Total ITBIS Tasa 1 en Otra Moneda, y/o Total ITBIS Tasa 2 en Otra Moneda y/o Total ITBIS Tasa 3 en Otra Moneda. |
| `otherCurrency.itbis1TotalOtherCurrency` | number |  | maxLength=19 | TotalITBIS1OtraMoneda | Valor numérico igual a Monto Gravado ITBIS en Otra Moneda Tasa1*tasa ITBIS 1. Condicional a que exista Monto gravado ITBIS Tasa 1 Otra Moneda. Condicional a que exista Monto Gravado tasa 1 en Otra Moneda. |
| `otherCurrency.itbis2TotalOtherCurrency` | number |  | maxLength=19 | TotalITBIS2OtraMoneda | Valor numérico igual a Monto Gravado ITBIS en Otra Moneda Tasa2*tasa ITBIS 2. Condicional a que exista Monto gravado ITBIS Tasa 2 Otra Moneda. |
| `otherCurrency.itbis3TotalOtherCurrency` | number |  | maxLength=19 | TotalITBIS3OtraMoneda | Valor numérico igual a Monto Gravado ITBIS en Otra Moneda Tasa3*tasa ITBIS 3. Condicional a que exista Monto gravado ITBIS Tasa 3 Otra Moneda. |
| `otherCurrency.additionalTaxAmountOtherCurrency` | number |  | maxLength=19 | MontoImpuestoAdicionalOtraMoneda | Sumatoria de los campos Monto Impuesto Selectivo al Consumo Específico en Otra Moneda, Monto Impuesto Selectivo Ad Valorem en Otra Moneda y Monto Otros Impuestos Adicionales en Otra Moneda. Condicional a que exista datos en cód… |
| `otherCurrency.additionalTaxesOtherCurrency` | array |  | maxItems=20 | ImpuestosAdicionalesOtraMoneda | Se pueden incluir 20 repeticiones de pares código – valor. Incluye los cinco campos siguientes: |
| `otherCurrency.additionalTaxesOtherCurrency[].taxTypeOtherCurrency` | integer | Y |  | TipoImpuestoOtraMoneda | Dato correspondiente al Código del impuesto adicional de acuerdo con la ‘Tabla de Codificación Tipos de Impuestos Adicionales’. (see catalog) |
| `otherCurrency.additionalTaxesOtherCurrency[].additionalTaxRateOtherCurrency` | number |  |  | TasaImpuestoAdicionalOtraMoneda | Dato correspondiente a la Tasa del Impuesto Adicional. Se debe indicar la tasa de Impuesto. (see catalog) |
| `otherCurrency.additionalTaxesOtherCurrency[].selectiveTaxAmountSpecificConsumptionOtherCurrency` | number |  | maxLength=19 | MontoImpuestoSelectivoConsumoEspecificoOtraMoneda | Valor del campo Monto Impuesto Selectivo al Consumo Específico referenciado al tipo de cambio del código Otra Moneda especificado. Condicional a que exista código de impuesto adicional del 006 al 022, este completado el campo c… |
| `otherCurrency.additionalTaxesOtherCurrency[].amountSelectiveConsumptionTaxAdvaloremOtherCurrency` | number |  | maxLength=19 | MontoImpuestoSelectivoConsumoAdvaloremOtraMoneda | Valor del campo Monto Impuesto Selectivo al Consumo Ad Valorem referenciado al tipo de cambio del código Otra Moneda especificado. Condicional a que exista código de impuesto adicional del 023 al 039, este completado el campo c… |
| `otherCurrency.additionalTaxesOtherCurrency[].otherAdditionalTaxesOtherCurrency` | number |  | maxLength=19 | OtrosImpuestosAdicionalesOtraMoneda | Valor del Monto Impuesto Adicionales referenciado al tipo de cambio del código Otra Moneda especificado. Condicional a que exista código de impuesto adicional del 001 al 005, este completado el campo código otra moneda y el cam… |
| `otherCurrency.totalAmountOtherCurrency` | number | Y | maxLength=19 | MontoTotalOtraMoneda | Monto gravado total en Otra Moneda + Monto exento en Otra Moneda+ Total ITBIS en Otra Moneda+ Monto del Impuesto Adicional en Otra Moneda. Condicional a que exista al menos un monto en otra moneda. |
| `itemDetails` | array | Y | maxItems=1000 minItems=1 | DetallesItem | Detalle ítem |
| `itemDetails[].lineNumber` | integer | Y |  | NumeroLinea | Línea que numera el ítem. Desde 1 a 1000 repeticiones. |
| `itemDetails[].itemCodeTable` | array |  | maxItems=5 | TablaCodigosItem | Se pueden incluir 5 repeticiones de pares código – valor. Incluye los dos campos siguientes: |
| `itemDetails[].itemCodeTable[].codeType` | string |  | maxLength=14 | TipoCodigo | Tipo de codificación utilizada para el ítem Standard: EAN, PLU, DUN o Interna (Hasta 5 tipos de códigos) |
| `itemDetails[].itemCodeTable[].itemCode` | string |  |  | CodigoItem | Código del ítem de acuerdo a tipo de codificación indicada en campo anterior. (Hasta 5 códigos) |
| `itemDetails[].billingIndicator` | integer | Y | enum=[0, 1, 2, 3, 4] maxLength=1 | IndicadorFacturacion | Indica si el ítem es exento, si es gravado, o No facturable. Indicará las distintas tasas: 0: No Facturable 1: ITBIS 1 ítem gravado a ITBIS tasa1 (18%). 2: ITBIS 2 ítem gravado a ITBIS tasa2 (16%). 3: ITBIS 3 ítem gravado a ITB… |
| `itemDetails[].retention` | object |  |  | Retencion | Retención |
| `itemDetails[].retention.indicatorAgentWithholdingPerception` | integer |  | enum=[1, 2] | IndicadorAgenteRetencionoPercepcion | Para Agentes de Retención o Percepción. Indica para cada transacción si es agente retenedor del producto que está vendiendo o el servicio. Condicional a que exista retención. 1: "R" 2: "P" |
| `itemDetails[].retention.itbisAmountWithheld` | number |  | maxLength=19 | MontoITBISRetenido | Monto del ITBIS correspondiente a la retención que será realizado por el comprador. Condicional a que exista retención. 53 |
| `itemDetails[].retention.isrAmountWithheld` | number |  | maxLength=19 | MontoISRRetenido | Monto del Impuesto Sobre la Renta correspondiente a la retención realizada de la prestación o locación de servicios. El e-CF tipo 41 es condicional a que exista retención y el ‘Indicador Bien o Servicio’ sea igual a 2. |
| `itemDetails[].itemName` | string | Y | maxLength=80 | NombreItem | Nombre del producto o servicio. |
| `itemDetails[].goodServiceIndicator` | integer | Y | enum=[1, 2] | IndicadorBienoServicio | Identifica si el ítem corresponde a Bien o Servicio. 1: Bien 2: Servicio |
| `itemDetails[].itemDescription` | string |  | maxLength=1000 | DescripcionItem | Descripción Adicional del ítem. |
| `itemDetails[].quantityItem` | number | Y | maxLength=19 | CantidadItem | Cantidad del ítem 55 |
| `itemDetails[].unitMeasure` | integer |  |  | UnidadMedida | Indica la unidad de medida que está expresada la cantidad. (see catalog) |
| `itemDetails[].quantityReference` | number |  | maxLength=19 | CantidadReferencia | Cantidad para la unidad de medida de referencia (no se usa para el cálculo del Monto Ítem). Condicional a que el ítem esté gravado con códigos de impuestos adicionales entre 006-022 en la Tabla de Codificación Tipo de Impuestos… |
| `itemDetails[].referenceUnit` | integer |  | maxLength=2 | UnidadReferencia | Indica la unidad de medida de referencia. Condicional a que esté completado el campo Cantidad de referencia. (see catalog) |
| `itemDetails[].subquantityTable` | array |  | maxItems=5 | TablaSubcantidad | Se deberá incluir esta tabla para fines del cálculo de los impuestos selectivos al consumo a productos derivados de alcohol y cervezas y productos del tabaco y cigarrillos. Condicional a que exista código desde 006 hasta 039 se… |
| `itemDetails[].subquantityTable[].subquantity` | number |  | maxLength=19 | Subcantidad | Cantidad de unidades de referencia que tiene la unidad del ítem. Condicional a que el ítem esté gravado con códigos de impuestos adicionales desde 006 hasta 022 en la Tabla de Codificación Tipo de Impuestos Adicional. 58 |
| `itemDetails[].subquantityTable[].codeSubquantity` | integer |  | maxLength=2 | CodigoSubcantidad | Indica la unidad de medida de la subcantidad. (see catalog) |
| `itemDetails[].degreesAlcohol` | number |  | maxLength=6 | GradosAlcohol | Corresponde al porcentaje de alcohol en el volumen de concentración total alcohólica por unidad de producto. Condicional a que el ítem esté gravado con códigos de impuestos adicionales 006 hasta 018 en la Tabla de Codificación … |
| `itemDetails[].unitPriceReference` | number |  | maxLength=19 | PrecioUnitarioReferencia | Precio unitario para la unidad de medida de referencia (no se usa para el cálculo del monto Total). Condicional a que el ítem esté gravado con códigos de impuestos adicionales desde 023 hasta 039 en la Tabla de Codificación Tip… |
| `itemDetails[].elaborationDate` | string |  | format=date | FechaElaboracion | Dato correspondiente a la fecha de elaboración del ítem. |
| `itemDetails[].expirationDateItem` | string |  | format=date | FechaVencimientoItem | Dato correspondiente a la fecha de vencimiento del ítem. |
| `itemDetails[].unitPriceItem` | number | Y | maxLength=21 | PrecioUnitarioItem | Dato correspondiente al precio unitario del ítem. |
| `itemDetails[].discountAmount` | number |  | maxLength=19 | DescuentoMonto | Totaliza todos los subdescuentos otorgados al ítem en montos. Condicional a que exista Monto Subdescuento. |
| `itemDetails[].subDiscounts` | array |  | maxItems=12 | TablaSubDescuento | Condicional a que exista descuento en el ítem. Se pueden incluir 12 repeticiones. Incluye los tres campos siguientes: |
| `itemDetails[].subDiscounts[].subDiscountRate` | string |  | enum=['$', '%'] | TipoSubDescuento | Indica si el Subdescuento está en monto ($) o porcentaje (%). Condicional a que exista descuento en el ítem. |
| `itemDetails[].subDiscounts[].subDiscountPercentage` | number |  | maxLength=5 | SubDescuentoPorcentaje | Valor del Subdescuento en porcentaje %. Condicional a que exista tipo Subdescuento en porcentaje (%). |
| `itemDetails[].subDiscounts[].subDiscountAmount` | number |  | maxLength=19 | MontoSubDescuento | Correspondiente al valor del descuento expresado en monto. Si va el subdescuento en %, deberá ir el monto del subdescuento. |
| `itemDetails[].surchargeAmount` | number |  | maxLength=19 | RecargoMonto | Totaliza todos los Subrecargos otorgados al ítem en montos. Condicional a que exista Monto Subrecargo. |
| `itemDetails[].subSurcharge` | array |  | maxItems=12 | TablaSubRecargo | Condicional a que exista recargo en el ítem. Se pueden incluir 12 repeticiones de pares Tipo – Valor. Incluye los tres campos siguientes: |
| `itemDetails[].subSurcharge[].subSurchargeType` | string |  | enum=['$', '%'] | TipoSubRecargo | Indica si el Subrecargo está en $ o %. Condicional a que exista recargo en el ítem. |
| `itemDetails[].subSurcharge[].subSurchargePercentage` | number |  | maxLength=5 | SubRecargoPorcentaje | Valor del Subrecargo en porcentaje %. Condicional a que exista tipo Subrecargo en porcentaje (%). |
| `itemDetails[].subSurcharge[].subSurchargeAmount` | number |  | maxLength=19 | MontoSubRecargo | Correspondiente al valor del Subrecargo expresado en monto. Condicional a que exista subrecargo. Si va el subrecargo en %, deberá ir el monto del subrecargo. |
| `itemDetails[].additionalTaxes` | array |  | maxItems=2 | TablaImpuestoAdicional | Se pueden incluir 2 repeticiones de códigos de impuesto. (see catalog) |
| `itemDetails[].additionalTaxes[].taxType` | integer | Y |  | TipoImpuesto | Dato correspondiente al Código del impuesto adicional de acuerdo a la Tabla de Codificación Tipos de Impuestos Adicionales (Tabla I). Condicional a que el ítem este gravado con Impuesto Adicional. |
| `itemDetails[].otherCurrencyDetail` | object |  |  | OtraMonedaDetalle | Otra moneda detalle. Indicar precios en monedas alternativas. |
| `itemDetails[].otherCurrencyDetail.priceOtherCurrency` | number | Y | maxLength=21 | PrecioOtraMoneda | Dato correspondiente al precio Unitario del Ítem en otra moneda. Condicional a que el ítem sea en Otra Moneda. |
| `itemDetails[].otherCurrencyDetail.discountOtherCurrency` | number |  | maxLength=19 | DescuentoOtraMoneda | Corresponde al valor de descuento otorgado en Otra Moneda. |
| `itemDetails[].otherCurrencyDetail.surchargeAnotherCurrency` | number |  | maxLength=19 | RecargoOtraMoneda | Corresponde el valor de recargo otorgado en Otra Moneda. |
| `itemDetails[].otherCurrencyDetail.amountItemOtherCurrency` | number | Y | maxLength=19 | MontoItemOtraMoneda | (Precio Unitario en otra moneda * Cantidad) – Descuento en otra moneda + Recargo en otra moneda. Condicional a que el Precio del ítem y Descuentos o Recargo (si existen) sean en Otra Moneda. |
| `itemDetails[].itemAmount` | number | Y | maxLength=19 | MontoItem | (Precio Unitario del ítem * Cantidad) – Monto Descuento + Monto Recargo |
| `subtotals` | array |  | maxItems=20 | Subtotales | Subtotales |
| `subtotals[].subTotalNumber` | integer |  | maxLength=2 | NumeroSubTotal | Número de Subtotal |
| `subtotals[].subtotalDescription` | string |  | maxLength=40 | DescripcionSubtotal | Título del Subtotal |
| `subtotals[].order` | integer |  | maxLength=2 | Orden | Ubicación para Impresión. De uso para el contribuyente como ayuda para indicar cómo imprimirá Subtotales. |
| `subtotals[].subTotalAmountTaxedTotal` | number |  | maxLength=19 | SubTotalMontoGravadoTotal | Valor de la sumatoria del Subtotal Monto Gravado ITBIS Tasa 1, ITBIS Tasa 2 e ITBIS Tasa 3; en DOP$ u otra moneda. |
| `subtotals[].subTotalAmountTaxedI1` | number |  | maxLength=19 | SubTotalMontoGravadoI1 | Valor del monto gravado del Subtotal asignados a ítem con ITBIS tasa 1(18%); en DOP$ u otra moneda. |
| `subtotals[].subTotalAmountTaxedI2` | number |  | maxLength=19 | SubTotalMontoGravadoI2 | Valor del monto gravado del Subtotal asignados a ítem con ITBIS tasa 2 (16%); en DOP$ u otra moneda. |
| `subtotals[].subTotalAmountTaxedI3` | number |  | maxLength=19 | SubTotalMontoGravadoI3 | Valor del monto gravado del Subtotal asignados a ítem con ITBIS tasa 3 (0%); en DOP$ u otra moneda. |
| `subtotals[].itbisSubTotal` | number |  | maxLength=19 | SubTotaITBIS | Valor de la sumatoria del Subtotal ITBIS Tasa 1, subtotal ITBIS Tasa 2 y Subtotal ITBIS Tasa 3; en DOP$ u otra moneda. |
| `subtotals[].itbis1SubTotal` | number |  | maxLength=19 | SubTotaITBIS1 | Valor del total de ITBIS tasa 1 del Subtotal; en DOP$ u otra moneda. |
| `subtotals[].itbis2SubTotal` | number |  | maxLength=19 | SubTotaITBIS2 | Valor del total de ITBIS tasa 2 del Subtotal; en DOP$ u otra moneda. |
| `subtotals[].itbis3SubTotal` | number |  | maxLength=19 | SubTotaITBIS3 | Valor del total de ITBIS tasa 3 del Subtotal; en DOP$ u otra moneda. |
| `subtotals[].subTotalAdditionalTax` | number |  | maxLength=19 | SubTotalImpuestoAdicional | Valor de los Impuestos adicionales del Subtotal. Aplica en subtotales en DOP$ u otra moneda. |
| `subtotals[].subTotalExempt` | number |  | maxLength=19 | SubTotalExento | Valor Exento del Subtotal. Aplica en subtotales en DOP$ u otra moneda. |
| `subtotals[].subTotalAmount` | number |  | maxLength=19 | MontoSubTotal | Valor de la línea de subtotal. Corresponde a la sumatoria de Subtotal Monto Gravado ITBIS Total, Subtotal ITBIS, Subtotal Impuestos adicionales y/o Subtotal Exento. Aplica en subtotales en DOP$ u otra moneda. |
| `subtotals[].lines` | integer |  | maxLength=2 | Lineas | Indica la cantidad de líneas que se subtotaliza. |
| `discountsOrSurcharges` | array |  | maxItems=20 | DescuentosORecargos | Descuento o recargo |
| `discountsOrSurcharges[].lineNumber` | integer |  | maxLength=2 | NumeroLinea | Número de descuento o recargo. De 1 a 20 repeticiones. Condicional a que exista Descuento o Recargo global.75 |
| `discountsOrSurcharges[].fitType` | string |  | enum=['D', 'R'] maxLength=1 | TipoAjuste | D(descuento) o R(recargo). Condicional a que se aplique descuento global o recargo global. |
| `discountsOrSurcharges[].norma1007Indicator` | integer |  | maxLength=1 | IndicadorNorma1007 | Indica si el descuento que se aplica es según lo establecido en la norma 10-07. |
| `discountsOrSurcharges[].descriptionDiscountOrSurcharge` | string |  | maxLength=45 | DescripcionDescuentooRecargo | Especificación de descuento o recargo. |
| `discountsOrSurcharges[].typeValue` | string |  | enum=['%', '$'] maxLength=1 | TipoValor | Indica si existe descuento o recargo aplicado en Porcentaje o Monto. Condicional a que exista descuento o recargo global. Posibles valores: "%" "$" |
| `discountsOrSurcharges[].discountValueOrSurcharge` | number |  | maxLength=6 | ValorDescuentooRecargo | Valor del Descuento o Recargo en porcentaje. Condicional a que descuento o recargo global sea en %. |
| `discountsOrSurcharges[].discountAmountOrSurcharge` | number |  | maxLength=19 | MontoDescuentooRecargo | Valor del descuento o recargo. Si se refiere al tipo de valor $ se debe indicar el monto. |
| `discountsOrSurcharges[].discountAmountOrSurchargeOtherCurrency` | number |  | maxLength=19 | MontoDescuentooRecargoOtraMoneda | Valor en otra moneda. Aplica en montos de descuento o recargo global. |
| `discountsOrSurcharges[].indicatorBillingDiscountOrSurcharge` | integer |  | maxLength=1 | IndicadorFacturacion | Indica si el descuento o recargo afecta a ítems: 0: No Facturable 1: ITBIS 1 ítem gravado a ITBIS tasa1 (18%). 2: ITBIS 2 ítem gravado a ITBIS tasa2 (16%). 3: ITBIS 3 ítem gravado a ITBIS tasa3 (0%). 4: Exento (E) |
| `pagination` | array |  | maxItems=100 minItems=2 | Paginacion | En esta sección se indica la cantidad de páginas del e-CF en la Representación Impresa y cuales ítems estarán en cada una. Cada objeto deberá repetirse para el total de páginas especificadas. La sección paginación es condiciona… |
| `pagination[].pageNo` | integer |  | maxLength=3 | PaginaNo | Indica la numeración de la página que contiene los datos del e-CF al realizar una representación impresa, siempre y cuando sea mayor a una página. |
| `pagination[].noLineFrom` | integer |  | maxLength=3 | NoLineaDesde | Indica el no. de la línea de detalle del primer ítem que contiene la página. Condicional a que sea completado el campo Página No. |
| `pagination[].noLineUntil` | integer |  | maxLength=3 | NoLineaHasta | Indica el no. de la línea de detalle del último ítem que será incluido en la página. Condicional a que sea completado el campo No. Línea Desde. |
| `pagination[].subtotalAmountTaxedPage` | number |  | maxLength=19 | SubtotalMontoGravadoPagina | Total de la suma de valores de monto gravado ITBIS a diferentes tasas, de las líneas correspondientes a la página que se indica. Condicional a que exista Subtotal Monto gravado1, y/o Subtotal Monto gravado 2 y/o Subtotal Monto … |
| `pagination[].subtotalAmountTaxed1Page` | number |  | maxLength=19 | SubtotalMontoGravado1Pagina | Total de la suma de valores de Ítems gravados asignados a ITBIS tasa 1 (tasa 18%), de las líneas correspondientes a la página que se indica, menos descuentos más recargos. 78 Condicional a que la página contenga algún ítem grav… |
| `pagination[].subtotalAmountTaxed2Page` | number |  | maxLength=19 | SubtotalMontoGravado2Pagina | Total de la suma de valores de Ítems gravados asignados a ITBIS tasa 2 (tasa 16%), de las líneas correspondientes a la página que se indica, menos descuentos más recargos. Condicional a que la página contenga algún ítem gravado… |
| `pagination[].subtotalAmountTaxed3Page` | number |  | maxLength=19 | SubtotalMontoGravado3Pagina | Total de la suma de valores de Ítems gravados asignados a ITBIS tasa 3 (tasa 0), de las líneas correspondientes a la página que se indica, menos descuentos más recargos. Condicional a que la página contenga algún ítem gravado a… |
| `pagination[].exemptSubtotalPage` | number |  | maxLength=19 | SubtotalExentoPagina | Total de la suma de valores correspondientes a ítems exentos indicados en el no. de línea ‘Desde’ ‘Hasta’, de la página. Condicional a que en la página contenga algún ítem exento. |
| `pagination[].itbisSubtotalPage` | number |  | maxLength=19 | SubtotalItbisPagina | Total de la suma de valores de Subtotal ITBIS 1, Subtotal ITBIS 2 y Subtotal ITBIS 3, a las diferentes tasas, de las líneas correspondientes a la página que se indica. Condicional a que exista Subtotal ITBIS 1, Subtotal ITBIS 2… |
| `pagination[].itbis1SubtotalPage` | number |  | maxLength=19 | SubtotalItbis1Pagina | Valor numérico igual al subtotal monto gravado 1 por la tasa ITBIS 1, de la línea indicada en la página. Condicional a que exista Subtotal Monto Gravado tasa 1. |
| `pagination[].itbis2SubtotalPage` | number |  | maxLength=19 | SubtotalItbis2Pagina | Valor numérico igual al subtotal monto gravado 2 por la tasa ITBIS 2, de la línea indicada en la página. Condicional a que exista Subtotal Monto Gravado tasa 2. |
| `pagination[].itbis3SubtotalPage` | number |  | maxLength=19 | SubtotalItbis3Pagina | Valor numérico igual al subtotal monto gravado 3 por la tasa ITBIS 3, de la línea indicada en la página. Condicional a que exista Subtotal Monto Gravado tasa 3. |
| `pagination[].subtotalAdditionalTaxPage` | number |  | maxLength=19 | SubtotalImpuestoAdicionalPagina | Sumatoria de los campos del área Subtotal Impuesto Adicional. Condicional a que se complete campos del área Subtotal Impuesto Adicional. |
| `pagination[].subtotalAdditionalTax` | object |  |  | SubtotalImpuestoAdicional | Subtotal impuesto adicional |
| `pagination[].subtotalAdditionalTax.subtotalSelectiveTaxForSpecificConsumptionPage` | number |  | maxLength=19 | SubtotalImpuestoSelectivoConsumoEspecificoPagina | Valor del impuesto selectivo al consumo específico y Ad Valorem, correspondientes a los ítems del campo ‘No. de línea desde’ a ‘No. de línea hasta’, indicados en la página. Condicional a que los ítems del campo ‘No. de línea de… |
| `pagination[].subtotalAdditionalTax.subtotalOtherTax` | number |  | maxLength=19 | SubtotalOtrosImpuesto | Valor del impuesto adicional (exceptuando el impuesto selectivo al consumo específico y Ad Valorem), correspondientes a los ítems del campo ‘No. de línea desde’ a ‘No. de línea hasta’, indicados en la página. Condicional a que … |
| `pagination[].subtotalAmountPage` | number |  | maxLength=19 | MontoSubtotalPagina | Sumatoria de los campos Subtotal Monto Gravado Total Pagina, Subtotal Exento Pagina, Subtotal ITBIS Pagina y Subtotal Impuesto Adicional Pagina. Condicional a que se complete el campo Página No. |
| `pagination[].subtotalNonBillableAmountPage` | number |  | maxLength=19 | SubtotalMontoNoFacturablePagina | Suma de todos los valores correspondientes a ítems no facturables, que estén indicados en el no. de línea ‘Desde’ ‘Hasta’, de la página. Condicional a que la página incluya ítems no facturables. |
| `informationReference` | object |  |  | InformacionReferencia | Información de referencia. |
| `informationReference.ncfModified` | string |  | maxLength=19 | NCFModificado | Es el número del comprobante fiscal que será afectado o remplazado por una secuencia electrónica. Tanto el comprobante afectado o reemplazado, como la secuencia electrónica, deben estar emitidos por el mismo RNC/Cédula. Condici… |
| `informationReference.rncOtherTaxpayer` | string |  | maxLength=11 | RNCOtroContribuyente | Aplica cuando el RNC del que emite el e-CF no coincide con el comprobante fiscal modificado (debido a que el RNC se encuentre dado de baja por disolución, fusión o escisión). En ese caso, se debe validar que el campo “RNC otro … |
| `informationReference.ncfModifiedDate` | string |  | format=date | FechaNCFModificado | Fecha del número de comprobante fiscal modificado. Condicional a que la emisión del e-CF corresponda a un reemplazo de Comprobante Fiscal no electrónico emitido en contingencia. |
| `informationReference.modificationCode` | integer |  | enum=[4, 5] |  | Código utilizado para indicar si el e-CF del comprobante fiscal modificado es con la finalidad de: 4. Reemplazo NCF emitido en contingencia 5. Referenciar Factura de Consumo Electrónica |
| `config` | object |  |  |  | Configuración adicional del documento electrónico |
| `config.pdf` | object |  |  |  | Configuración de la generación del PDF |
| `config.pdf.type` | string |  | enum=['generic', 'pos'] default=generic |  | Tipo de PDF a generar |
| `config.pdf.note` | string |  | maxLength=250 |  | Nota a incluir en el PDF. |

Numbers are JSON numbers in the schema (`type: number`, `maxLength: 19`, `minimum: 0`, `maximum: 10000000000000000`);
`AP10102 INVALID_STRING_AT_NUMBER_FORMAT` ("Must be a string with a valid number format. example: \"123.45\"") suggests some
fields accept decimal strings — see open questions. No `additionalProperties` allowed (`AP10068`).

### Enum meanings (e-CF 31)

| Field | Values |
| --- | --- |
| `idDoc.incomeType` (`TipoIngresos`) | 1 Ingresos por operaciones (no financieros) · 2 financieros · 3 extraordinarios · 4 por arrendamientos · 5 por venta de activo depreciable · 6 Otros |
| `idDoc.paymentType` (`TipoPago`) | 1 Contado · 2 Crédito (3 Gratuito exists but "no son válidas para crédito fiscal"; enum on 31 is [1, 2]) |
| `idDoc.paymentFormsTable[].paymentMethod` (`FormaPago`) | 1 Efectivo · 2 Cheque/Transferencia/Depósito · 3 Tarjeta de Débito/Crédito · 4 Venta a Crédito · 6 Permuta · 7 Nota de crédito · 8 Otras formas de pago (5 Bonos o certificados de regalo → only e-CF 32) |
| `idDoc.paymentAccountType` (`TipoCuentaPago`) | `CT` Corriente · `AH` Ahorro · `OT` Otra |
| `idDoc.taxAmountIndicator` (`IndicadorMontoGravado`) | 0 line amounts **without** ITBIS · 1 line amounts **with ITBIS included** |
| `itemDetails[].billingIndicator` (`IndicadorFacturacion`) | 0 No facturable · 1 ITBIS tasa 1 (18 %) · 2 ITBIS tasa 2 (16 %) · 3 ITBIS tasa 3 (0 %) · 4 Exento |
| `itemDetails[].goodServiceIndicator` (`IndicadorBienoServicio`) | 1 Bien · 2 Servicio |
| `itemDetails[].retention.indicatorAgentWithholdingPerception` | 1 "R" (retención) · 2 "P" (percepción) |
| `itemDetails[].subDiscounts[].subDiscountRate`, `subSurcharge[].subSurchargeType`, `discountsOrSurcharges[].typeValue` | `$` amount · `%` percentage |
| `discountsOrSurcharges[].fitType` (`TipoAjuste`) | `D` descuento · `R` recargo |
| `informationReference.modificationCode` on 31 | 4 Reemplazo NCF emitido en contingencia · 5 Referenciar Factura de Consumo Electrónica |
| `config.pdf.type` | `generic` (default) · `pos` |
| `otherCurrency.currencyType` | `BRL CAD CHF CHY XDR DKK EUR GBP JPY NOK SCP SEK USD VEF` (catalog page adds `HTG`, `MXN`) |

### How specific concepts are expressed

- **ITBIS** — per line by `billingIndicator` (1/2/3 = 18/16/0 %, 4 = exempt, 0 = not billable). No per-line ITBIS amount field
  exists. Header totals: `totals.i1AmountTaxed` / `i2AmountTaxed` / `i3AmountTaxed` (`MontoGravadoI1..3`, sums of line
  `itemAmount` per rate, "menos descuentos más recargos"), `totals.totalTaxedAmount` (`MontoGravadoTotal`), rates
  `totals.itbisS1` / `itbisS2` / `itbisS3` (integers 18 / 16 / 0), amounts `totals.itbis1Total` / `itbis2Total` /
  `itbis3Total` (= taxed amount × rate; selective taxes that are part of the ITBIS base are added first) and
  `totals.itbisTotal`. `totals.totalAmount` = MontoGravadoTotal + MontoExento + TotalITBIS + MontoImpuestoAdicional.
- **`IndicadorMontoGravado`** = `idDoc.taxAmountIndicator` (0/1, conditional on taxed lines). With 1, `unitPriceItem` /
  `itemAmount` include ITBIS; additional taxes are never included in the price. (DGII error 1934 example above shows DGII
  cross-checks `MontoGravadoI1` against the lines.)
- **Exempt lines** — `billingIndicator = 4`; sum in `totals.exemptAmount` (`MontoExento`). On 44 and 43 every line must be 4
  and `exemptAmount` is required.
- **Discounts / surcharges per line** — `itemDetails[].discountAmount` (`DescuentoMonto`, total of the line's sub-discounts) +
  `itemDetails[].subDiscounts[]` (≤ 12: `subDiscountRate` `$|%`, `subDiscountPercentage`, `subDiscountAmount` — the amount is
  required even when the type is `%`); surcharges symmetric: `surchargeAmount` + `subSurcharge[]` (`subSurchargeType`,
  `subSurchargePercentage`, `subSurchargeAmount`). `itemAmount` = (`unitPriceItem` × `quantityItem`) − `discountAmount` +
  `surchargeAmount`.
- **Global discounts / surcharges** — `discountsOrSurcharges[]` (≤ 20): `lineNumber`, `fitType` (`D`/`R`), `norma1007Indicator`,
  `descriptionDiscountOrSurcharge` (≤ 45), `typeValue` (`%`/`$`), `discountValueOrSurcharge` (percentage, ≤ 999),
  `discountAmountOrSurcharge`, `discountAmountOrSurchargeOtherCurrency`, `indicatorBillingDiscountOrSurcharge` (0–4, which
  ITBIS bucket it affects). Totals are documented as "menos descuentos más recargos".
- **Currency (USD)** — amounts in the main blocks are always DOP. Foreign currency is *additional*: header
  `otherCurrency` { `currencyType` (Y, e.g. `USD`), `exchangeRate` (Y, `TipoCambio`, max 7 chars, e.g. 100.8),
  `totalTaxedAmountOtherCurrency`, `amountTaxed1/2/3OtherCurrency`, `exemptAmountOtherCurrency`, `itbisTotalOtherCurrency`,
  `itbis1/2/3TotalOtherCurrency`, `additionalTaxAmountOtherCurrency`, `additionalTaxesOtherCurrency[]`,
  `totalAmountOtherCurrency` (Y) } and per line `itemDetails[].otherCurrencyDetail` { `priceOtherCurrency` (Y),
  `discountOtherCurrency`, `surchargeAnotherCurrency`, `amountItemOtherCurrency` (Y) }. Error `AP15001 CURRENCY_TYPE_OTHER_CURRENCY`.
- **Payment terms** — `idDoc.paymentType` (`TipoPago`, 1 contado / 2 crédito), `idDoc.paymentDeadline` (`FechaLimitePago`,
  date, "Solo para facturas a crédito"), `idDoc.paymentTerm` (`TerminoPago`, free text ≤ 15, e.g. "120 días"),
  `idDoc.paymentFormsTable[]` (≤ 7 pairs `paymentMethod` + `paymentAmount`), and `paymentAccountType`,
  `paymentAccountNumber` (≤ 28), `bankPayment` (≤ 75) for cheque/transfer.
- **"InformacionAdicional"** — three different places:
  `additionalInformation` (`InformacionesAdicionales`: shipping/container data — `shippingDate`, `shipmentNumber`,
  `containerNumber`, `referenceNumber` (integer), `grossWeight`, `netWeight`, `grossWeightUnit`, `unitNetWeight`, `bulkQuantity`,
  `bulkUnit`, `bulkVolume`, `unitVolume`; units are codes from the unit catalog);
  `sender.additionalInformationIssuer` (`InformacionAdicionalEmisor`, ≤ 250); `buyer.additionalInformation`
  (`Informacionadicionalcomprador`, ≤ 150). Free PDF note: `config.pdf.note` (≤ 250 in schema; error `AP11002` says ≤ 500).
- **Transport** — `transport` { `driver`, `transportDocument` (integer), `file` (`Ficha`), `licensePlate` (≤ 7),
  `transportationRoute`, `transportationZone`, `albaranNumber` }.
- **Withholding** (31) — per line `retention` { `indicatorAgentWithholdingPerception`, `itbisAmountWithheld`, `isrAmountWithheld` };
  header `totals.itbisTotalRetained`, `isrTotalRetention`, `itbisTotalPerception`, `isrTotalPerception`.
- **Units** — `itemDetails[].unitMeasure` integer 1–62 (see §12).
- **Dates** — request dates are `format: date` (`YYYY-MM-DD` in examples, e.g. `sender.stampDate` = `FechaEmision`).

### Response 201 (`POST /fiscal-invoices`)

| Field | Type | Meaning |
| --- | --- | --- |
| `id` | string (ulid) | Alanube document id (use for GET / notify) |
| `stampDate` | date | Fecha de emisión |
| `status` | enum | `REGISTERED`, `TO_SEND`, `WAITING_RESPONSE`, `TO_NOTIFY`, `FINISHED` |
| `companyIdentification` | string | Issuer RNC |
| `encf` | string | e-NCF (echo of the one sent) |
| `xml` | uri | "XML firmado listo para ser enviado a la DGII" (pre-signed S3 URL) |
| `pdf` | uri | Printed representation (S3 URL) |
| `documentStampUrl` | uri | DGII "timbre" URL (QR target), e.g. `https://ecf.dgii.gov.do/testecf/ConsultaTimbre?RncEmisor=…&RncComprador=…` |
| `signatureDate` | date-time | Fecha de firma |
| `securityCode` | string | Código de seguridad (6 chars, e.g. `MYUVCT`) |
| `sequenceConsumed` | boolean | "Número de secuencia consumido" |

```json
{
  "id": "01G021Z3QSRZ58GSTBH7TPGD2J",
  "stampDate": "1990-12-31",
  "status": "REGISTERED",
  "companyIdentification": "132109122",
  "encf": "E310000001727",
  "xml": "https://api-alanube-e-provider-dom-test.s3.amazonaws.com/users/…/fiscalInvoice/….xml?…",
  "pdf": "https://api-alanube-e-provider-dom-test.s3.amazonaws.com/users/…pdf?…",
  "documentStampUrl": "https://ecf.dgii.gov.do/testecf/ConsultaTimbre?RncEmisor=123...&RncComprador=456...",
  "signatureDate": "…",
  "securityCode": "MYUVCT",
  "sequenceConsumed": false
}
```

### Response 200 (`GET /fiscal-invoices/{id}`)

`id`, `stampDate`, `status`, **`legalStatus`** (`ACCEPTED|ACCEPTED_WITH_OBSERVATIONS|REJECTED`), `companyIdentification`,
**`trackId`** (uuid assigned by DGII), **`documentNumber`** (the e-NCF; note the create response calls it `encf`),
`documentStampUrl`, `xml` ("XML firmado enviado a la DGII"), `pdf`, `signatureDate`, `securityCode`, `sequenceConsumed`,
`governmentResponse` { `code` (integer), `value[]` { `codigo`, `valor` } }. Failed documents (changelog) add
`error` { `code`, `message` }. Status codes: 200, 400, 404, 500.

---

## 7. Other e-CF types — differences from type 31

All share the 31 structure (same field names) unless listed. Each type also has `GET /<path>/{id}`,
`GET /<path>/{id}/idCompany/{idCompany}` and (except 43 and 47) `POST /<path>/notify-by-email`.

| Type | Issue endpoint |
| --- | --- |
| 32 Factura de Consumo | `POST /invoices` |
| 33 Nota de Débito | `POST /debit-notes` |
| 34 Nota de Crédito | `POST /credit-notes` |
| 41 Compras | `POST /purchases` |
| 43 Gastos Menores | `POST /minor-expenses` |
| 44 Regímenes Especiales | `POST /special-regimes` |
| 45 Gubernamental | `POST /gubernamentals` |
| 46 Exportaciones | `POST /export-supports` |
| 47 Pagos al Exterior | `POST /payment-abroad-supports` |

### e-CF 32 (`/invoices`)
- Sync below DOP 250,000; async at or above (§2). 201 adds `legalStatus`, `documentNumber`, `resumeXml`, `governmentResponse`,
  `response[]` { `code`, `message` } and does **not** list `encf`.
- `buyer` optional ("Obligatorio para E32 con monto total mayor o igual a $250.000 DOP"); `buyer.rnc` and `buyer.companyName`
  not required; new `buyer.foreignIdentifier` (`IdentificadorExtranjero`, ≤ 20) when the buyer is foreign without RNC/cédula and
  the total ≥ DOP 250,000. Error `AP3006 BUYER_RNC_OR_FOREIGN_IDENTIFIER_REQUIRED`.
- No `idDoc.sequenceDueDate`.
- `paymentType` enum [1, 2, 3] (3 Gratuito); `paymentMethod` adds 5 (Bonos o certificados de regalo).
- No `retention` per line, no `itbisTotalRetained` / `isrTotalRetention` / perception totals.
- `itemDetails` without `maxItems`: up to 10,000 lines below DOP 250,000, up to 1,000 at or above.
- Extra `itemDetails[].mining` { `netWeightKilogram`, `netWeightMining`, `affiliationType` 1/2, `settlement` 1/2 }.
- `informationReference.modificationCode` enum [4] only.
- The schema marks `company.id` as required inside `company` (the `company` object itself is optional).

### e-CF 34 Nota de Crédito (`/credit-notes`)
- `informationReference` **required**: `ncfModified` (Y, ≤ 19, pattern `^(?!E33|E34).*$` — cannot reference another note;
  `AP14002`), `ncfModifiedDate` (Y, date), `modificationCode` (Y, 1 Anulación total · 2 Corrige texto · 3 Corrige montos · 4
  Reemplazo NCF emitido en contingencia; `AP14003`), `reasonForModification` (≤ 90, `RazonModificacion`, e.g. "Error en precio"),
  `rncOtherTaxpayer` (optional).
- `idDoc.creditNoteIndicator` (Y, 0/1, `IndicadorNotaCredito`): 1 when the note is dated more than 30 calendar days after the
  affected e-CF (no right to reduce ITBIS) — `AP13006`.
- `idDoc.incomeType` optional, but required when `ncfModified` starts with E31, E32, E44, E45 or E46.
- No `sequenceDueDate`, `paymentTerm`, `paymentFormsTable`, `paymentAccountType`, `paymentAccountNumber`, `bankPayment`.
- `paymentType` [1, 2, 3]; `buyer` optional (+ `foreignIdentifier`); `mining` block available.
- Async validations against the referenced invoice (§2): `AP3012`, `AP3013`, `AP3014`, `AP3015`.

### e-CF 33 Nota de Débito (`/debit-notes`)
- Same reference block as 34 (`informationReference` Y, `ncfModified` Y with the same pattern, `modificationCode` Y [1–4],
  `reasonForModification`), but `ncfModifiedDate` is **not** marked required and there is no `creditNoteIndicator`.
- Keeps `sequenceDueDate` (Y) and the payment fields; `incomeType` optional (required when referencing E31/32/44/45/46);
  `paymentType` [1, 2, 3]; `buyer` optional (+ `foreignIdentifier`).

### e-CF 44 Regímenes Especiales (`/special-regimes`)
- Every `itemDetails[].billingIndicator` must be **4** (exempt; enum [4], default 4); `totals.exemptAmount` required.
- Removed: `idDoc.taxAmountIndicator`, all ITBIS totals (`totalTaxedAmount`, `i1..i3AmountTaxed`, `itbisS1..3`,
  `itbisTotal`, `itbis1..3Total`), retention/perception, ISC specific / ad-valorem amounts, the matching other-currency
  ITBIS fields, `norma1007Indicator`, selective-tax line fields.
- `buyer.rnc` not required; `buyer.foreignIdentifier` added; `paymentType` [1, 2, 3]; `modificationCode` [4].
- No field for a CONFOTUR / exemption authorization number exists in the schema.

### e-CF 41 Compras (`/purchases`)
- Same `sender` / `buyer` blocks, but `buyer` describes the **supplier** ("Nombre o Razón Social del proveedor").
- `itemDetails[].retention` and `retention.indicatorAgentWithholdingPerception` **required**; `isrAmountWithheld` conditional on
  `goodServiceIndicator = 2`.
- Removed: `incomeType`, `deferredDeliveryIndicator`, `dateFrom/dateUntil`, seller/route fields, delivery and PO fields of
  `buyer`, `additionalInformation`, `transport`, additional/selective taxes, `nonBillableAmount`.
- `paymentType` [1, 2, 3]; `modificationCode` [4].

### e-CF 43 Gastos Menores (`/minor-expenses`)
- **No `buyer` block.** `idDoc` = `encf`, `sequenceDueDate` (Y), `paymentType` (optional, [1,2,3]), `totalPages`.
- Lines exempt only (`billingIndicator` = 4); totals: `exemptAmount` (Y), `totalAmount` (Y), `amountPeriod`, `previousBalance`,
  `amountAdvancePayment`, `payValue`.
- Removed: all ITBIS, retention, discounts/surcharges (line and global), additional taxes; no notify-by-email endpoint.

---

## 8. Cancellations (anulación de rangos e-NCF)

`POST /cancellations` — "se usan para anular rangos de numeración que no se usarán" (DGII ANECF). It voids **unused
sequence ranges**, not issued documents (issued documents are reversed with a credit note, `modificationCode = 1`).

| Field | Type | Req | DGII tag / notes |
| --- | --- | --- | --- |
| `company.id` | string (ulid) | Y if `company` sent | associated company |
| `header.rncSender` | number | Y | `RncEmisor` (max 99999999999) |
| `header.cancelledEncfQuantity` | (no type) | Y | `CantidadeNCFAnulados` — total count across all lines |
| `cancellations[]` | array ≤ 10 | Y | `Anulacion` |
| `cancellations[].lineNumber` | integer ≤ 10 | Y | `NoLinea` |
| `cancellations[].ecfType` | integer | Y | 31, 32, 33, 34, 41, 43, 44, 45, 46, 47 (`TipoeCF`) |
| `cancellations[].rangeCancelledEnfc[]` | array ≤ 10,000 | Y | `TablaRangoSecuenciasAnuladaseNCF` |
| `…rangeCancelledEnfc[].encfFrom` / `encfUntil` | string ≤ 13 | Y | `SecuenciaeNCFDesde` / `SecuenciaeNCFHasta` |
| `cancellations[].cancelledEncfQuantity` | integer | Y | count for this type |

```json
{ "header": { "rncSender": 133109124, "cancelledEncfQuantity": 3 },
  "cancellations": [ { "lineNumber": 1, "ecfType": 31,
    "rangeCancelledEnfc": [ { "encfFrom": "E310000000010", "encfUntil": "E310000000012" } ],
    "cancelledEncfQuantity": 3 } ] }
```

201 → `{ id, stampDate, status (REGISTERED…FINISHED), companyIdentification }`. Status 400, 500.
`GET /cancellations/{id}` (and `/cancellations/{id}/idCompany/{idCompany}`) → 200 `{ id, stampDate, status,
companyIdentification, xml, governmentResponse: { codigo, valor } }` (note: here `governmentResponse` is a flat object, not
`value[]`). Webhook `cancellations` fires when finished.

---

## 9. Directory and DGII status

- `GET /check-directory?rnc=<9–11 digits>` (and `/check-directory/idCompany/{idCompany}`) — directory of companies active for
  e-invoicing. 200 = array of `{ nombre, rnc, urlRecepcion, urlAceptacion, urlOpcional }`. Sandbox returns only DGII's own
  URLs; production returns active companies. 404 `{"message": "company not found", "code": 404}`.
- `GET /check-dgii-status` (and `/check-dgii-status/idCompany/{idCompany}`):
  - no params → `{ "response": [ { "servicio": "…", "estatus": "…" } ] }`;
  - `?environment=1|2|3` (1 PreCertificación, 2 Producción, 3 Certificación) → `{ "estado": "disponible" }`;
  - `?maintenance=yes` → `{ "ventanaMantenimientos": [ { ambiente, horaInicio, horaFin, dias[] } ] }`.

---

## 10. Reception of documents from suppliers (brief)

Alanube runs the DGII receiver endpoints for each company (`companyUrls`): external issuers obtain a seed (`SemillaModel`
XML), sign it with their certificate, exchange it for a Bearer token (`{ token, expedido, expira }`, 1 h in the example), then
post the e-CF XML or the commercial approval (ACECF). Alanube automatically answers each received e-CF with an acuse de recibo
(`ARECF`, `Estado` 0 = recibido, 1 = no recibido).

| Method | Path | Notes |
| --- | --- | --- |
| GET | `/received-documents` (and `/received-documents/idCompany/{idCompany}`) | query `status` (`RECEIVED`\|`NOT_RECEIVED`, default RECEIVED), `commercialResponse` (`NOT_DECLARED`\|`ACCEPTED`\|`REJECTED`, default NOT_DECLARED), `rnc` (issuer), `documentNumber` (pattern `^(E31|E33|E34|E44)\d{10}$`; requires `rnc`), `limit` 1–1000 (25), `page`, `start`/`end` (date; default last 30 days UTC). 200 `{ metadata: { current_page, limit, from, to }, documents: [...] }`, sorted by `documentStampDate` desc |
| GET | `/received-documents/{id}` | `{ id, issuerIdentification, buyerIdentification, documentType ("33"), documentNumber, documentStampDate, signatureDateTime, totalAmount ("1180", string), status, errorMsg, commercialResponse, timestamp, xml }` |
| POST | `/received-documents/{id}/commercial-response` | body `{ "commercialResponse": "ACCEPTED"|"REJECTED" (Y), "notAcceptedDetail": "<reason if REJECTED>" }` → 200 `{ "commercialApprovalInfo": { "id": "…" } }` (generates the commercial approval for the document) |
| GET | `/received-commercial-approvals?limit&page` (+ `/{id}`, `/idCompany/{idCompany}` variants) | approvals your customers sent for your e-CF |
| GET | `/commercial-approvals/{id}` | approval you generated |
| GET | `/reception/acknowledgments/{idCompany}[/{id}]` | acuses de recibo your customers returned for documents you issued (only for documents ACCEPTED by DGII) |
| GET | `/reception/receipts/{idCompany}[/{id}]` | acuses Alanube returned on your behalf to external issuers |
| POST | `/reception/dgii/receive-document` | **test only**: body `{ idCompany (Y), documentType ("31"|"33"|"34"|"44"|"47") }` simulates DGII sending you a document |

Webhook `reception` fires on a received document or commercial approval (payload undocumented).

---

## 11. Error dictionary (main codes)

| Code | Key | Meaning | HTTP |
| --- | --- | --- | --- |
| AP3001 | ENCF_USED | e-NCF already used | 400 |
| AP3002 | XML_NOT_FOUND | XML not found | 400 |
| AP3003 | SIGNATURE_NOT_FOUND | Signature not found | 400 |
| AP3004 | SENDER_RNC_NOT_FOUND | Sender RNC not found | 400 |
| AP3005 | PRIMARY_USED | Primary id was used | 400 |
| AP3006 | BUYER_RNC_OR_FOREIGN_IDENTIFIER_REQUIRED | buyer RNC / foreign id required above DOP 250,000 | 400 |
| AP3007 | EXTRA_INFO_NOT_FOUND | referencedDocumentNumber / referencedDocumentDate not found | 400 |
| AP3008 | SCHEMA_IS_NOT_VALID | XML Schema is not valid | 400 |
| AP3009 | TRACK_ID_NOT_FOUND | Track id not found | 404 |
| AP3010 | DOCUMENT_IN_PROCESS | Document is in process | 400 |
| AP3011 | ENCF_IN_PROCESS | e-NCF already being processed | 400 |
| AP3012 | DOCUMENT_REFERENCED_DOCUMENT_REJECTED | referenced document rejected or failed | 400 |
| AP3013 | DOCUMENT_REFERENCED_INSUFFICIENT_AMOUNT | credit note exceeds the remainder | 400 |
| AP3014 | CREDIT_NOTE_CANCEL_DIFFERENT_AMOUNT | cancellation note ≠ remainder | 400 |
| AP3015 | CN_TEXT_MODFY_CODE_TYPE_TO_BE_ZERO | modificationCode 2 must total 0 | 400 |
| AP3016 | INVOICE_BUYER_REQUIRED | Invoice buyer is required | 400 |
| AP1001…AP1016 | company / certificate | invalid/expired certificate, wrong password, PKCS12 format, logo > 150 KB, webhook URL invalid, `AP1016 SENDER_RNC_NOT_MATCH` | 400/404 |
| AP16001…16007 | company sync validation | type enum, cert extension/base64, logo base64, webhook status enums | — |
| AP4001…4005 | set test | in progress, id required, not found, retry number | 400 |
| AP7001 | ACKNOWLEDGMENT_NOT_FOUND | — | 404 |
| AP10000…10088 | schema / type validation | `required` AP10067, `additionalProperties` AP10068, `type` AP10069, `format` AP10070, `maxLength` AP10072, `enum` AP10079, `pattern` AP10078, typed variants AP10004 (string) / AP10005 (number) … | 400 |
| AP10100…10107 | common | ULID AP10100, e-mail AP10101, number-as-string AP10102, RNC 9–11 digits AP10103, e-NCF prefix AP10104, municipality AP10105, province AP10106, phone `XXX-XXX-XXXX` AP10107 | 400 |
| AP11001/11002 | config | PDF type, PDF note | 400 |
| AP12001/12002 | discountsOrSurcharges | fitType D/R, typeValue %/$ | 400 |
| AP13001…13006 | idDoc | taxAmountIndicator, incomeType 1–6, paymentType 1–3, paymentMethod 1–8, paymentAccountType, creditNoteIndicator | 400 |
| AP14001…14003 | informationReference | modification code, note referencing a note | 400 |
| AP15001 | CURRENCY_TYPE_OTHER_CURRENCY | invalid currency | 400 |
| AP0001/AP0002 | users | user exists / not found | 400/404 |
| AP9002 / AP9003 | general | invalid request / date range > 90 days | 400 |
| EPR500 | UNEXPECTED_ERROR | unexpected error | 500 |
| AEP2001…AEP2013 | DGII communication | `AEP2003` auth with DGII, `AEP2004` general (502), `AEP2005` conn reset (502), `AEP2006` timeout (504), `AEP2007` unavailable (504), `AEP2008` not found (404), `AEP2009` bad request (400), `AEP2010` unauthorized (401), `AEP2011` parse (502), `AEP2012` host unreachable (502), `AEP2013` leaf signature verification (502), `AEP2002` environment not supported (404) | — |

DGII rejection details come in `governmentResponse.value[].codigo/valor` (DGII's own codes, e.g. 1934), not in this table.

---

## 12. Catalogs

- **Currency** (`otherCurrency.currencyType`): ISO-like 3-letter codes — `BRL`, `CAD`, `CHF`, `CHY` (yuan, sic), `XDR`, `DKK`,
  `EUR`, `GBP`, `JPY`, `NOK`, `SCP` (libra escocesa), `SEK`, `USD`, `VEF`, `HTG`, `MXN`. The OpenAPI enum lacks `HTG` and `MXN`.
- **Units of measure** (`unitMeasure`, `referenceUnit`, `grossWeightUnit`, `unitNetWeight`, `bulkUnit`, `unitVolume`,
  `codeSubquantity`): **integer codes 1–62** (DGII table). Examples: 1 BARR Barril · 2 BOL Bolsa · 4 BULTO Bultos · 6 CAJ Caja ·
  8 CM Centímetro · 13 DOC Docena · 15 GL Galones · 17 GR Gramo · 18 GRAN Granel · 19 HOR Hora · **21 KG Kilogramo** · 23 LB Libra ·
  24 LITRO Litro · 25 LOT Lote · 26 M Metro · 27 M2 Metro cuadrado · **28 M3 Metro cúbico** · 31 PAQ Paquete · 34 PZA Pieza ·
  **39 TONE Tonelada** · **43 UND Unidad** · 44 EA Elemento · 45 MILLAR Millar · 46 SAC Saco · 51 Q Quintal · 53 P2 Pie cuadrado ·
  55 PULG Pulgadas · 62 OZT Onzas Troy.
- **Province / municipality**: 6-digit codes (`^[0-9]{6}$`, e.g. province `020000`, municipality `020101`), validated
  (`AP10105`, `AP10106`); the catalog page linked from the field descriptions is not in the published index.
- **Additional tax types** (`taxType` 1–39, DGII Tabla I): linked page not in the published index.

---

## 13. Open questions (ambiguous or missing in the docs)

1. **Token lifecycle**: how API tokens are issued (dashboard only?), whether they expire, how to rotate them, whether one token
   per user covers all associated companies, and whether sandbox and production tokens differ.
2. **Rate limits** and concurrency limits: none documented.
3. **Idempotency**: no idempotency key; is a retried `POST` with the same `idDoc.encf` after a network timeout answered with
   `AP3001`/`AP3011` only, or can it return the existing document id? (`AP3011` message includes "with id: …".)
4. **Webhook payload**: body schema for `emissionFinished`, `cancellations`, `reception`, `governmentStatusChanged` is not
   documented; no signature/HMAC; retry count/back-off not stated.
5. **Status enum vs. reality**: `PENDING` and `FAILED` (and the `error` object) appear only in changelogs, not in the OpenAPI
   `status` enum; the meaning of each of `TO_SEND` / `WAITING_RESPONSE` / `TO_NOTIFY` and which is terminal besides `FINISHED`
   is not defined. Is `legalStatus` present on 201 for types other than 32?
6. **`governmentResponse.code` values** (e.g. 4) are not enumerated (DGII codes 1 aceptado / 2 rechazado / 3 en proceso /
   4 aceptado condicional are the DGII convention but not stated by Alanube).
7. **`sequenceConsumed`** semantics: when is a sequence consumed on rejection (relevant to whether a rejected e-NCF can be
   reused)?
8. **Decimal precision / number format**: JSON `number` in the schema vs. `AP10102` "Must be a string with a valid number
   format"; rounding rules (2 decimals?) not stated.
9. **`taxAmountIndicator` = 1** (prices with ITBIS included): which fields Alanube expects to be gross (`unitPriceItem`,
   `itemAmount`) and how `i1AmountTaxed` must then be computed — explained only in a linked YouTube video.
10. **File URLs**: `xml` / `pdf` are pre-signed S3 URLs (contain `x-amz-security-token`); expiry for DOM is not stated (a
    Colombian changelog mentions 60-minute links). Are files retrievable later only by re-querying the document?
11. **QR**: no QR image is returned; `documentStampUrl` is the timbre URL. Whether `documentStampUrl` / `securityCode` are
    final at 201 (before DGII acceptance) is not stated.
12. **e-CF 32 required `company.id`**: the schema marks it required inside the optional `company` object — copy error or real?
13. **Debit note `ncfModifiedDate`** is optional in the 33 schema but required in 34.
14. **Error format rollout**: unified `errors[]` format production date stated as both 2026-02-03 and 2026-04-22.
15. **e-CF 44 exemption authorization**: no field for an exemption / authorization number (e.g. CONFOTUR); only
    `additionalInformation` / free-text fields exist.
16. **USD**: only DOP amounts plus the `otherCurrency` mirror are supported; no rule is stated for which exchange rate to use or
    for rounding between the two sets.
17. **Province / municipality and additional-tax catalogs** are linked from field descriptions but not published in the
    `llms.txt` index (old `e-api-a-la-nube-dom.readme.io` links).
18. **Commercial approval / acuse webhooks** and list-item schemas (`documents[]` in `/received-documents`) are unresolved refs
    in the OpenAPI.
19. **Certification**: the full DGII onboarding flow (which XML documents `sign-document` is meant for, how `companyUrls` are
    registered, when production use is enabled) is not described.

---

## 14. URLs read

Index and guides:
- https://developer.alanube.co/llms.txt
- https://developer.alanube.co/docs.md
- https://developer.alanube.co/docs/webhooks.md
- https://developer.alanube.co/docs/recepción-de-documentos.md
- https://developer.alanube.co/docs/recepción-de-documentos-integradores-externos.md
- https://developer.alanube.co/docs/alanube-mcp.md
- (404, do not exist: https://developer.alanube.co/docs/introduccion.md, https://developer.alanube.co/docs/autenticacion.md)

Reference (all downloaded; the ones analysed in detail are marked *):
- Company: createcompany-1.md*, getselfcompany-1.md*, updateselfcompany-1.md*, getcompany-1.md*, updatecompany-1.md, getassociatedcompanies.md*
- Certification: createsettest.md*, checksettestmaincompany.md*, checksettestassociatedcompany.md, signdocumentcertification.md*, signdocumentcertificationbycompany.md*, checkproviderinfo.md*
- 31: createinvoicefiscals.md*, checkinvoicefiscals.md*, checkinvoicefiscalsbycompany.md*, notifybyemailinvoicefiscals.md*
- 32: createinvoices.md*, checkinvoices.md*, checkinvoicesbycompany.md, notifybyemailinvoices.md*
- 33: createdebitnotes.md*, checkdebitnotes.md, checkdebitnotesbycompany.md, notifybyemaildebitnotes.md
- 34: createcreditnotes.md*, checkcreditnotes.md*, checkcreditnotesbycompany.md, notifybyemailcreditnotes.md
- 41: createpurchases.md*, checkpurchases.md, checkpurchasesbycompany.md, notifybyemailpurchases.md
- 43: createminorexpenses.md*, checkminorexpenses.md, checkminorexpensesbycompany.md
- 44: createspecialregimes.md*, checkspecialregimes.md, checkspecialregimesbycompany.md, notifybyemailspecialregimes.md
- 45/46/47: creategubernamentals.md, checkgubernamentals.md, notifybyemailgubernamentals.md, createexportsupports.md, checkexportsupports.md, notifybyemailexportsupports.md, createpaymentabroadsupports.md, checkpaymentabroadsupports.md
- Cancellations: createcancelations.md*, checkcancelations.md*, checkcancelationsbycompany.md
- Directory / DGII: checkdirectory.md*, checkdirectorybycompany.md, checkdgiistatus.md*, checkdgiistatusbycompany.md
- Reception: post_reception-dgii-receive-document.md*, get_received-documents.md*, get_received-documents-idcompany-idcompany.md, get_received-documents-id.md*, get_received-documents-id-idcompany-idcompany.md, get_received-commercial-approvals.md*, get_received-commercial-approvals-idcompany-idcompany.md, get_received-commercial-approvals-id.md, get_received-commercial-approvals-id-idcompany-idcompany.md, post_received-documents-id-commercial-response.md*, post_received-documents-id-commercial-response-idcompany-idcompany.md, get_commercial-approvals-id.md, get_commercial-approvals-id-idcompany-idcompany.md, get_reception-acknowledgments-idcompany-id.md, get_reception-acknowledgments-idcompany.md*, get_reception-receipts-idcompany-id.md, get_reception-receipts-idcompany.md*
- Reports: gettotaldocumentsbyuser.md*, gettotaldocumentsbycompany.md, getemitteddocuments.md*, getaccepteddocuments.md
- Catalogs: tabla-codificación-monedas.md*, codificación-de-unidades-de-medida.md*
- Errors: general.md*, documents.md*, dgii.md*, users.md*, companies.md*, settest.md*, acknowledgments.md*, typevalidations.md*, schemavalidations.md*, commonvalidations.md*, config-error-table.md*, discounts-or-surcharges-error-table.md*, id-doc-error-table.md*, information-reference-error-table.md*, other-currency-error-table.md*, company-error-table.md*
- Changelogs: api-fe-dom-flujo-de-procesamiento-asíncrono-facturas-de-consumo-e32.md*, api-fe-dom-mejora-de-la-documentación-y-unificación-del-formato-de-errores.md*, api-fe-dom-validaciones-en-el-rnc-del-sender.md*, api-fe-dom-validaciones-para-notas-de-créditodébito.md*, nuevos-enlaces-de-descarga-zip.md* (Colombia), plus the CRI / PER / PAN changelogs (other countries, not used)

All reference URLs have the form `https://developer.alanube.co/reference/<name>`; changelogs `https://developer.alanube.co/changelog/<name>`.
Pages were fetched as raw Markdown (curl) and their embedded OpenAPI JSON was parsed programmatically, so the field lists
above are complete for the operations analysed.
