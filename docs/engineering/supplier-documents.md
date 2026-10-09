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
