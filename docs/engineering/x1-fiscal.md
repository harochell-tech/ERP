# Fiscal decisions of X-1 (E-X1-1…20)

Sources: `docs/fiscal/x1-dossier-2026-10.md`. Each change ships in its own PR.

## X1-01 — late credit notes, CONFOTUR validity, the X-1 pack (migration 0108, E-X1-01-1…5)

**Credit notes after 30 days** (E-X1-5, E-X1-01-1…3). `IssueCreditNote` compares the invoice's `invoice_date` + 30 calendar days with
the issue date: later, the note's lines lose their ITBIS and its total is its net (migration 0108 lets a DRAFT note's `tax_total` /
`total` and its lines' `itbis` go to zero, nothing else); P-22 then has no ITBIS line, so the invoice's ITBIS stays payable; the result
and the `CreditNoteIssued` event carry `withoutItbis`. The e-CF 34 already sends `creditNoteIndicator` 1 after 30 days (VS4-03).
`GetCreditNote` gives `itbisUntil` (invoice date + 30) and the draft says from which date the note goes without ITBIS.

**CONFOTUR** (E-X1-20, E-X1-01-4): an authorization registered or updated without `validUntil` is valid until `issuedOn` + 180 days.

**Pack** (E-X1-01-5): `deploy/fiscal/x1-2026-10.json` — «Seguro de vida» in four dated versions and `IDENTIFICACION_CONSUMIDOR`
(CONSUMER_ID_THRESHOLD 250,000.00). The loader now takes several versions of one rule, one per effective date in date order (a version of
the same date that differs, or a later one already there, is left as it is); its step keys carry the date. Two sources — DGII Aviso
10-26 and the e-CF format v1.0 — are PDFs the DGII site will not hand to a program: a person saves them as
`docs/fiscal/fuentes/aviso-10-26.pdf` and `docs/fiscal/fuentes/ecf-formato-v1.0.pdf`; until then the loader reports them MISSING_FILE
and skips the rules that cite them.
