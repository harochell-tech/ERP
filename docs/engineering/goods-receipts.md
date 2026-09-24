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
