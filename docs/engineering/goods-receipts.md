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
