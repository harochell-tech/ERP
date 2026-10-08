# Printing — editable print formats (PRT-1)

Baseline `docs/architecture/prt1/frozen-baseline-prt1.md` (E-PRT-1…10, E-PRT-01-1…8; E-ENT-9).

## PRT-01 — one server rendering for screen, e-mail and reprint (migration 0102)

- `Rochell.Sales/Printing/PrintRenderer` (Platform takes no packages; PRT-03 decides where printing lives once Procurement prints): Liquid templates rendered with **Fluid** (`Fluid.Core`), HTML-encoded output, at most
  200,000 steps and 20 levels; a template sees only its model (strings, booleans, whole numbers, dictionaries, lists). The result is
  the full HTML (IBM Plex Sans / Mono embedded as base64 `@font-face`, `Assets/base.css`, the format's CSS) and, apart, the CSS and the
  body for the screen. `PrintText`: amounts (≥ 2 decimals, thousands with commas), quantities (no trailing zeros), dates dd/mm/yyyy
  and date-times in Dominican time «29/09/2026 11:15 p. m.» — the server formats, templates only place (E-PRT-01-3).
- `Rochell.Sales/Printing/PrintDocuments`: document types DELIVERY_NOTE, INVOICE, QUOTE, PROFORMA, ORDER_PROFORMA, STATEMENT,
  AR_AGING. Each model is built from the existing print queries (`GetDeliveryPrint`, `GetQuotePrint`, `GetInvoice` +
  `GetInvoiceFiscalPackage`, `GetProforma`, `GetSalesOrderProforma`, `GetCustomerStatement`, `GetArAging`) with Spanish keys
  (`emisor`, `cliente`, `lineas`, `totales`, `marca_agua`…). The format is the company's ACTIVE `md.print_format` of the type, else
  the built-in «Rochell» template (`Printing/Templates/<TYPE>.liquid`, version 0). Watermarks are decided here (conduce before the
  gate, quote by status, invoice without an accepted e-CF or voided, voided proforma, order proforma before confirmation).
- QR codes are inline SVG (QRCoder): the e-CF stamp, and the driver's QR on the conduce — and on the invoice for its deliveries still
  in transit (E-ENT-9; Core invoices only delivered deliveries today, so it does not show) — only when the screen passes its address
  (`BaseUrl`); the e-mail has none.
- `GetPrintDocument` (`sales:read`): `GET /api/v1/companies/{c}/sales/print/{documentType}/{id}?from&to` → `PrintedDocument`
  (type, format version, title, html, css, body). The statement takes the customer as id and the period.
- E-mails render the same document (`DocumentMail.PrintAsync`); `core.mail_message.print_format_version` records the version
  (E-PRT-7). `DocumentHtml` is gone.
- Migration 0102: `md.print_format` (company, type, version, DRAFT / ACTIVE / RETIRED with one ACTIVE per type, settings, Liquid
  body, CSS, note, who created and activated), `md.company_logo` (PNG / JPEG ≤ 1 MB, SHA-256), `core.mail_message.print_format_version`,
  permission `print_format:manage` for DIRECTOR (148 permissions). Commands and the editing screen come with PRT-02.
- Web: `PrintedDocument` shows the server's CSS and body in a shadow root (styles isolated both ways; Playwright reads through it) and
  prints it. The print pages of conduce, quote, invoice, statement and order proforma use it; the delivery proforma prints at
  `/facturacion/proforma/imprimir/`.
