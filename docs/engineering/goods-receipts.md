# Goods receipts (PR-09)

`PostGoodsReceipt` (goods_receipt:post, Almacenista, plant-scoped) receives lines of an APPROVED / PARTIALLY_RECEIVED order
into a location of the order's plant.

Transaction (Patch 1 §5.2, C-01), in lock order:

1. Lock the order (N3) and its lines by id (N4); check location, weigh ticket, tolerance (`qty_ordered × (1 + tolerance) + approved over-receipt`).
2. Convert each line to the base UOM with the conversion effective on the receipt date; value = qty × PO price (2 decimals).
3. **Posting preflight (P-1):** R-01 active, accounts mapped, period open (late entry if the month's INV-MOV is closed). Any gap → nothing is written.
4. Event `GoodsReceiptPosted` → state history → receipt header → per line: lot, receipt line, inventory receipt (quantity + value entry),
   PO line `qty_received`, document link RECEIVES.
5. Order status → PARTIALLY_RECEIVED or RECEIVED (with history).
6. Journal R-01: Dr RAW_MATERIAL (plant, item, value entry) / Cr GRNI (plant, supplier).

At COMMIT the database checks: journal balanced, value entry ↔ GL line (P-1), inventory = GL (P-3), receipt POSTED has its journal (K-25),
status changes have their history rows (ADR-027).

**Setup:** approve R-01 (`ApprovePostingRuleVersion R-01 v1`) and map RAW_MATERIAL and GRNI accounts before the first receipt.

## Reversal (PR-10)

`ReverseGoodsReceipt` (goods_receipt:reverse, Controller, step-up) reverses a whole POSTED receipt when nothing was invoiced
and every lot is untouched since the receipt (otherwise: receipt correction, PR-11).

- **R-02 (A):** exact inverse of the R-01 journal and of each receipt value entry (never recalculated).
- **R-02B:** only if, after A, an area × item has quantity 0 and value ≠ 0, or quantity > 0 and value ≤ 0 — reallocated to
  0 or quantity × avg₀ (average just before the reversal) against PURCHASE_PRICE_VARIANCE (journal VALUATION_REALLOCATION).
- The receipt becomes REVERSED with its `goods_receipt_reversal` document (both checked at COMMIT); the order goes back to
  APPROVED / PARTIALLY_RECEIVED; the weigh ticket is free again.

**Setup:** approve R-02B and map PURCHASE_PRICE_VARIANCE before the first reversal that needs a reallocation.

## Corrections (PR-11)

`CreateReceiptCorrection` (Almacenista) proposes Δq on one receipt line with a reason and an evidence reference; under
materiality it is DRAFT, above it PENDING_APPROVAL. `ApproveReceiptCorrection` (Controller, step-up above materiality) posts it;
`RejectReceiptCorrection` rejects a pending one. Creator and approver always differ (SoD and a database CHECK).

- **Δq > 0 (R-03A):** +Δq to the receipt's lot; Dr RAW_MATERIAL / Cr GRNI for Δq × P; within tolerance or approved over-receipt.
- **Δq < 0 (R-03B):** q₁ = min(area stock, |Δq|) leaves stock at the average (receipt lot first, then oldest lots); q₂ = the rest
  goes to MATERIAL_USAGE_VARIANCE at P; Dr GRNI |Δq| × P; the price difference of q₁ to PURCHASE_PRICE_VARIANCE.
- The receipt becomes CORRECTED (and can then no longer be reversed); the order's received quantity and status follow.

**Setup:** approve R-03A and R-03B; map PURCHASE_PRICE_VARIANCE and MATERIAL_USAGE_VARIANCE; INVENTORY policy active.

## Transport, the supplier's note and the receipt report (REC-01)

E-REC-1…4, migration 0125. `PostGoodsReceipt` names our truck and driver (`VehicleId`, `DriverId`, both ACTIVE in
`log.vehicle` / `log.driver`, read through SQL) and the supplier's delivery note (`SupplierDeliveryNote`, 1–60 characters). A note
is received once per supplier while the receipt stands (case-insensitive; reversing the receipt frees it). The weigh ticket stays
optional; the screen asks «Fecha y hora de llegada» and folds the ticket.

- `ListReceivingFleet` (`/procurement/receiving-fleet`): our ACTIVE trucks and drivers for the screen.
- `GetGoodsReceiptReport` (`/procurement/goods-receipt-report`, `format=csv`): one row per line of the non-reversed receipts whose
  Dominican date falls between `from` and `to` (at most a year), filtered by supplier, material, location and plant, with totals
  per material and unit. Almacén › Reporte de recepciones shows it and downloads the CSV.

## Direct receipt without a purchase order (REC-02)

E-REC-6…8, E-REC-02-1…7, migration 0126. `PostDirectGoodsReceipt` (`goods_receipt:direct`, Almacenista and Comprador) takes the
supplier, location, arrival, the supplier's note, our truck and driver and the lines (item, unit, quantity, unit price). Core creates an
order with `origin = DIRECT_RECEIPT` (DRAFT → PENDING_APPROVAL in the same command) and records `pur.direct_receipt`.

- Each line is compared with its reference (`DirectReceipts.ReferenceAsync`): the last price from that supplier in that unit, else
  the standard price; within `direct_receipt_price_deviation_pct` (PURCHASING) when abs(price − reference) ≤ reference × the value.
- Every line within: the service identity «Recepción directa» approves the order and the receipt posts through
  `PostGoodsReceiptHandler.PostAsync` with the command's result id; the order ends RECEIVED.
- Otherwise the order waits. `ApprovePurchaseOrder` posts the held receipt (`DirectReceipts.PostOnApprovalAsync`) with the original
  arrival time; rejecting or cancelling cancels the order and closes the direct receipt, freeing the note. An open direct receipt
  holds its note against other receipts of that supplier.
- `GetPurchaseOrder` shows `origin` and `directReceipt` (with the price check); `ListPurchaseOrders` shows `origin`;
  `GetDirectReceiptReference` gives the screen the proposed price and the allowed deviation.
