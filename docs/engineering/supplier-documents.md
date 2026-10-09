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
