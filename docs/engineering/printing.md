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

## PRT-02 — Configuración › Formatos de impresión (E-PRT-3…10, E-PRT-02-1…7; migration 0103)

- `PrintSettings` (stored in `md.print_format.settings`): mode SENCILLO / AVANZADO, paper CARTA / MEDIA_CARTA / TICKET_80 (the ticket only
  for the invoice), margins 3–30 mm, font 9–20 px, row padding 2–20 px, title colour, logo (shown, width 10–120 mm, position), the
  columns of the document's table (key, title, shown, width %, alignment, order; `PrintFormatRules.Catalogue`) and fixed texts
  (header, footer, quote conditions, bank accounts). `PrintFormatRules.Css` turns them into CSS (`@page` size and margins first).
- The built-in templates read `formato.columnas` (header, rows by `l[c.clave]`, a totals row by column), `formato.textos` and `logo`;
  the models give each line the catalogue's keys and `totales_fila` (value and test id per column).
- Commands: `SavePrintFormatDraft` (one DRAFT per type; SENCILLO stores the built-in template, AVANZADO its own template and CSS after
  `EnsureSafe` — no scripts, `on…=`, `javascript:`, external `src` / `href` / `@import` / `url()` — and a parse),
  `ActivatePrintFormat` (step-up; `PrintSamples.EnsureMandatory` renders the type's example: invoice e-NCF, issuer and buyer RNC, QR,
  security code, signature date; conduce driver's QR and its draft watermark; then ACTIVE → RETIRED, draft → ACTIVE),
  `RestorePrintFormat` (a version, or 0 the built-in one, into the draft), `SetCompanyLogo` (PNG / JPEG by their bytes, ≤ 1 MB).
  All `print_format:manage`; 279 commands.
- Queries (`configuration:read`): `ListPrintFormats` (types, active and draft versions, history, logo), `GetPrintFormat` (a version's
  settings, template, CSS and the built-in template), `PreviewPrintFormat` (POST `…/sales/print-formats/preview`: the document of
  that number, else the latest of the type, else `PrintSamples.Model`; watermark «VISTA PREVIA»; nothing saved).
- Migration 0103: the DIRECTOR role is «Director» (no longer read-only: it changes the print formats).
- Web: `/configuracion/formatos/` (menu Configuración › Formatos de impresión): logo, one tab per document, simple settings or advanced
  template, preview (`DocumentView`), save, activate, versions with restore. Dev stack: a Director account. Journey:
  `e2e/print-formats-journey.spec.ts`.

## LAB1-03 — the lab's documents (E-LAB1-03-1, 9)

- Two more types: `LAB_CERTIFICATE` (letter only) and `RACK_LABEL` (paper `ETIQUETA_100X150`, 100 × 150 mm, the only one it offers).
  Their models are built by `LabPrints` and they print through their own queries — `GetLabCertificatePrint` (`lab:read`) and
  `GetRackLabelPrint` (`production:read`); `GetPrintDocument` refuses them. Details: `quality-lab.md`.
