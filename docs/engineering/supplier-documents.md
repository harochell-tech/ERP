# Supplier documents from received e-CF, the QR or a photo (OCR-1)

Baseline: `docs/architecture/ocr1/frozen-baseline-ocr1.md` (E-OCR-1…8). Alanube's reception API: `docs/fiscal/alanube-api.md`
(«Reception»).

## OCR1-01 — schema (migration 0106, E-OCR1-01-1…10)

| Table | What it holds |
| --- | --- |
| `pur.supplier_document` | A captured supplier document (E-OCR1-01-1): issuer RNC and name, buyer RNC, fiscal number (NCF `B` + 10 or e-NCF `E` + 12; its type is the two digits after the letter), date, total, ITBIS, security code and signature time (from the QR or the XML), the matched supplier, Alanube's id and reception status, the commercial response, which fields the AI read (`ai_fields`), when a QR was scanned. CAPTURED → REGISTERED (with its invoice) / DISCARDED (with a reason, final); REGISTERED → CAPTURED when its invoice is voided. One live document per issuer and fiscal number (E-OCR1-01-5); an invoice comes from one document at most; the issuer and the fiscal number never change; the header changes only while CAPTURED. |
| `pur.supplier_document_line` | The lines as read, insert-only, tagged `XML` or `AI`; the XML's set wins. Amounts are kept as read; the checks of E-OCR-6 compare them when shown. |
| `pur.supplier_document_file` | The XML in the database (one per document); a photo (JPG / PNG), scan or PDF in the evidence store (`IEvidenceStore`, B2 on staging) by its key; ≤ 10 MB; SHA-256; append-only (E-OCR1-01-7). |
| `pur.received_document_sync` | Per company, the last good and last attempted reading of Alanube's received documents and the last error (E-OCR1-01-9). |

The commercial response (E-OCR-3, E-OCR1-01-2/3) is `NOT_DECLARED` → `ACCEPTED` / `REJECTED` (with a reason) once and never changed;
only a document Alanube holds (`provider_id`) can be answered; `response_sent_at` marks that Alanube took it.

`tax.ecf_call` also records the reception calls: `RECEIVED_LIST`, `RECEIVED_GET`, `COMMERCIAL_RESPONSE`.

Permissions (E-OCR1-01-8): `supplier_document:capture` (Cuentas por pagar), `supplier_document:respond` (Cuentas por pagar, Contador);
the Contador also gains `supplier_invoice:read` to see the inbox. 155 permissions.

## OCR1-02 — received e-CF and the commercial response (no migration, E-OCR1-02-1…10)

**Alanube.** `IEcfReception` (`Rochell.Tax/Ecf/EcfReception.cs`): `ListReceivedAsync` (`GET /received-documents` by status, commercial
response, dates, page), `GetReceivedAsync` (`GET /received-documents/{id}`, with the XML — the document itself, base64 or an https link
downloaded without the token), `RespondAsync` (`POST /received-documents/{id}/commercial-response`). `AlanubeProvider`, the simulated one
(`AddReceived`, `SampleXml`, `Responses`) and the Off one implement it. Every call is a `tax.ecf_call` row (`RECEIVED_LIST`, `RECEIVED_GET`,
`DOWNLOAD`, `COMMERCIAL_RESPONSE`), never the token. `ReceivedEcfXml.Parse` reads the DGII XML by local names (no DTD, no external
entities): type, e-NCF, issuer RNC and name, buyer RNC, issue date, total, ITBIS, the security code (first six characters of the
signature value), the signature time (Santo Domingo → UTC) and the lines (code, name, quantity, unit, unit price, amount, billing indicator).

**Reading** — `ImportReceivedDocuments` (`ecf:process`, the daily process, E-OCR1-02-1): window from 30 days back the first time, then
from the last good reading less a day (`pur.received_document_sync`); four listings (received not answered / accepted / rejected, not
received); each new id read once (at most 50 per reading; the result says `more`). A new e-CF of this company's RNC (another buyer is
refused, E-OCR1-01-6) becomes a CAPTURED document with its XML and XML lines and the ACTIVE supplier of that RNC; it joins a live document
of the same issuer and e-NCF captured before (its header and lines replace what the AI read, `ai_fields` cleared, E-OCR1-02-8), and links
an invoice already registered with that e-NCF (REGISTERED). Known documents follow Alanube: reception status, the invoice registered since,
a response given in Alanube's portal (kept as sent). Alanube not answering ends the reading with `last_error`, the window unchanged.

**Response** — `RespondToSupplierDocument` (`supplier_document:respond`, step-up): only a document Alanube received, once; a rejection
needs a reason (≤ 250) and is refused while its invoice is posted (`SUPPLIER_DOCUMENT_POSTED`). `SendSupplierDocumentResponse`
(`ecf:process`) sends it; Alanube taking it sets `response_sent_at`; otherwise it is retried at the next pass (E-OCR1-02-3).

**Invoices** (E-OCR1-02-4/9): `PostSupplierInvoice` refuses an invoice whose e-CF was rejected (`SUPPLIER_DOCUMENT_REJECTED`) and accepts
the e-CF not yet answered on behalf of who posts; `VoidSupplierInvoice` and `ReverseSupplierInvoice` return the document to CAPTURED.

**Worker** — `ReceivedDocumentsService` (API, when the gateway is not Off): every `Rochell:Ecf:Interval`, per company, sends the answers
kept and, every `Rochell:Ecf:ReceptionInterval` (1 h) or at once after Alanube's webhook (`ReceptionNudge`, E-OCR1-02-7), reads the
received documents, again while a reading says there is more. 297 commands.

## OCR1-03 — inbox, pass to invoice, QR (migration 0107, E-OCR1-03-1…10)

**Server.** Migration 0107: `qr_url` (only `https://ecf.dgii.gov.do/` or `https://fc.dgii.gov.do/`) and `qr_total_amount` on
`pur.supplier_document`. `EcfStampUrl.Parse` (`Rochell.Tax/Ecf/EcfStamp.cs`) reads the DGII stamp link of a printed e-CF's QR (issuer and
buyer RNC, e-NCF, issue date, total, signature time → UTC, security code); anything else is null.
- `CaptureSupplierDocumentFromQr` (`supplier_document:capture`): refuses a link that is not the DGII's (`SUPPLIER_DOCUMENT_QR_INVALID`) or
  whose buyer is not the company (`SUPPLIER_DOCUMENT_NOT_OURS`); joins the live document of that issuer and e-NCF (the QR replaces what the
  AI read of date, total and buyer); refuses one already on an invoice (`SUPPLIER_DOCUMENT_ALREADY_INVOICED`); else captures a new one
  with the registry's name and the ACTIVE supplier of that RNC.
- `DiscardSupplierDocument` (`supplier_document:capture`): CAPTURED → DISCARDED with a reason.
- `RegisterSupplierInvoice` / `RegisterExpenseInvoice` take `supplierDocumentId`: the document must be CAPTURED, registrable (not a note
  33 / 34 / B03 / B04, received, not rejected) and of the same supplier RNC and number (`SUPPLIER_DOCUMENT_MISMATCH`); it becomes
  REGISTERED in the same transaction.
- Queries (`supplier_invoice:read`): `ListSupplierDocuments` (status, search by RNC / name / number, `unsentOver24Hours`), `GetSupplierDocument`
  (lines — the XML's, else the AI's —, files, checks `LINES_DO_NOT_ADD_UP`, `RNC_NOT_IN_REGISTRY`, `QR_TOTAL_DIFFERS`, `SUPPLIER_NOT_IN_CORE`,
  `NOT_RECEIVED`, `NOTE_NOT_REGISTERED`, `RESPONSE_UNSENT`, the linked invoice, the history, and the category and tax type of the supplier's
  latest expense invoice as suggestion), `GetSupplierDocumentFile` (the XML, base64). 299 commands.

**Screens.** Compras › Comprobantes recibidos (`/compras/comprobantes/`): tabs Pendientes / Registrados / Descartados / Todos, search,
«Escanear QR» (`QrScan`: the camera, a photo of the QR or its pasted link, decoded in the browser with `jsQR`). The detail
(`/compras/comprobante/?id=`): the flags in red, header and lines with «leído por IA» marks, Aceptar / Rechazar ante la DGII (step-up),
«Pasar a factura de gastos / de inventario» (`?documento=` on both forms: supplier, NCF, date, printed total; the expense form also takes
the lines and the suggestion), «Crear proveedor» (the supplier form opens with the RNC and the registry's name), «Descartar»,
«Verificar en la DGII», «Descargar XML», history. Inicio: «Comprobantes recibidos por registrar» and «Respuestas a la DGII sin enviar hace
más de 24 horas». Dev stack: `ReceivedSeed` (an e-CF 31 and a 34 of «Agregados del Este»; the company's RNC 131925332). Playwright:
`e2e/supplier-documents-journey.spec.ts`.

## OCR1-04 — photos and PDFs read by AI (migration 0112, E-OCR1-04-1…4)

- `CaptureSupplierDocumentFromImage` (`supplier_document:capture`): a JPG / PNG photo or a PDF (≤ 10 MB, ≤ 3 pages; recognised by its
  first bytes). The same file (SHA-256) is never read twice: its reading is reused. Otherwise, within PURCHASING `ocr_monthly_readings`
  (`SUPPLIER_DOCUMENT_READING_LIMIT`), `ISupplierDocumentReader` reads it; every reading is a `pur.supplier_document_reading` row (READ /
  UNREADABLE, what it read, model, tokens); a reader that does not answer (retried once) records nothing (`SUPPLIER_DOCUMENT_READ_FAILED`).
  Without an RNC or NCF it could read, the result asks for them (`needsInput`) and the person types them; what was typed is not «leído por
  IA». The file goes to the evidence store (`supplier-documents/{company}/{sha}.{ext}`) and a document is captured or completed (never
  over an XML or a QR) with AI lines and `ai_fields`. Reader off: `SUPPLIER_DOCUMENT_READER_OFF`; no store: `SUPPLIER_DOCUMENT_STORE_MISSING`.
- Reader: `AnthropicSupplierDocumentReader` (Messages API, the file as an image or document block, the forced tool `record_invoice` whose
  answer `InvoiceReadingFormat.Parse` keeps only well-formed values; amounts are strings parsed as decimals). Settings `Rochell:Ocr:Mode`
  (OFF / ANTHROPIC / SIMULATED), `Model` (`claude-sonnet-5-5`), `ApiKeyFile` (`secrets/ocr/anthropic-key` on the server), `Timeout`.
  The evidence store is now configured on its own (B2 bucket, or a folder in Development / Test), not only with the drivers' link key.
- `/api/v1/environment` says `ocrEnabled`; the inbox shows «Subir foto o PDF» (photos reduced on the phone to 2400 px JPEG), the detail
  «Descargar foto / PDF». `GetSupplierDocumentFile` serves photos and PDFs from the store. 302 commands.
