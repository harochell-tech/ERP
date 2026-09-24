# Reconciliations and component close (PR-16)

## Reconciliations (`RunReconciliation`, `reconciliation:run`)

Eight checks over the whole ledger as of the run, zero tolerance (E-PR16-1). Each run is stored in `rec.recon_run`, each finding
in `rec.recon_exception` with its severity and the component it blocks.

| Code | Compares | Blocks |
| --- | --- | --- |
| AP-GL | Open AP documents vs AP_CONTROL, per supplier | AP-REC |
| INV-VALUE-GL | Valuation vs RAW_MATERIAL in the GL, per area × item | INV-MOV |
| INV-QTY-BALANCE | Stock balances vs quantity entries; valued quantity vs stock of the area's plants | INV-MOV |
| INV-VALUE-BALANCE | Valuation vs value entries, per area × item | INV-MOV |
| VAL-RESIDUAL | Orphan value (ERROR, fix with R-06); quantity > 0 with value ≤ 0 (warning) | INV-MOV |
| ACC-EVIDENCE | Each document's accounting status vs its journals | INV-MOV (inventory documents), AP-REC (invoices) |
| VALUE-GL-LINK | Each value entry has exactly one GL line of the same amount | INV-MOV |
| GRNI-AGING | Received and not invoiced for longer than `grni_aging_alert_days` (warning) | — |

## Closing (`CloseComponent`, Controller, step-up)

SERIALIZABLE, holding the period × component lock exclusively, so a posting into that period waits and then lands on the first
open day as a late entry (CC-05). It is rejected when the period has not ended yet, when the component is not OPEN/REOPENED,
when any ledger group of the period is not SEALED (run the sealer first — PD-02), or when a blocking reconciliation reports an
ERROR. On success it stores a snapshot (reconciliation totals and the component's balances) in `fin.close_snapshot` and records
its SHA-256 in the component state.

## Reopening (Patch 1 P-8)

`RequestReopen` (Controller, reason) → `ApproveReopen` or `RejectReopen` by the **Segundo aprobador de cierre** (Dirección),
never the requester. Approval moves the component CLOSED → REOPENED under the period lock; closing it again leaves a new
snapshot.
