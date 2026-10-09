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

## X1-01b — the month's deliveries not invoiced hold its close (migration 0109, E-X1-01-6, E-X1-01b-1/2)

CONTRACT-ASSET adds UNBILLED_AT_CLOSE (ERROR, AR-REC) at a month-end cutoff — the close runs its reconciliations at the period's end — for
each delivery line with control transferred by then (its first CONTRACT_ASSET / UNBILLED_RECEIVABLE entry) and not fully invoiced,
unless an acceptance of that period lists it. `AcceptUnbilledDeliveries` (`unbilled_delivery:accept`, Controller, step-up, reason 3–500)
records `sal.unbilled_acceptance` with the lines unbilled at that moment (`sal.unbilled_acceptance_line`, append-only); a line delivered
later holds the close again; nothing left → `NOTHING_TO_ACCEPT`. Contabilidad › Períodos y cierre: «Aceptar conduces sin facturar» on
AR-REC while CONTRACT-ASSET has blocking errors. 300 commands, 156 permissions.

## X1-02 — withholdings by what is bought and by the supplier's document series (migration 0110, E-X1-02-1…3)

- `pur.expense_category.isr_withholding_type` (606 ISR types 1–9): set when preparing or correcting a draft, or by the Controller on an
  approved category with `SetExpenseCategoryIsrType` (`expense_category:approve`, step-up; proposed as E-X1-02-5, to be ratified). An
  ISR withholding rule that names a type withholds only on the expense lines of categories of that type; inventory lines, which have
  no category, follow the rule's `applies_to` as before.
- A withholding rule may carry `document_series` (`["B"]`, `["E"]`); the engine gets the series from the supplier's fiscal number
  (`B…` / `E` + 12) and leaves out the rule when it does not match (NG 02-2026: the 30 % to companies on B-series only). Previews,
  without a number, withhold as before. The determination records the series and each line's ISR type.
- Screens: Compras › Categorías de gasto (column and field «Retención ISR»), Fiscal › Reglas (withholding «Comprobantes del proveedor»).
  301 commands.
