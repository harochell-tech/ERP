# Inventory base (PR-07)

- **Ledgers (append-only, hashed):** `inv.inv_quantity_entry` (base UOM, `numeric(18,6)`) and `inv.inv_value_entry` (DOP, 2 decimals).
- **Balances:** `inv.inv_stock_balance` (location × item × lot, never negative) and `inv.inv_valuation_balance` (valuation area × item: moving average).
- **Lots:** one per receipt line (`InventoryLedger.CreateLotAsync`).

**Receipt in a handler:** pre-assign `valueEntryId` → `PostingEngine.PrepareAsync` (the RAW_MATERIAL line carries plant, item,
`SubledgerRef` and `InvValueEntryId` = `valueEntryId`) → append event → `CreateLotAsync` → `ReceiveAsync(..., PostingDate = plan.PostingDate)`
→ `PostingEngine.WriteAsync`.

**Issue:** `ReserveIssueAsync` (conditional UPDATE: fails with `INSUFFICIENT_STOCK`, never negative; moving-average value under the
valuation lock; last unit takes the remaining value) → posting plan → event → `WriteIssueAsync` → `WriteAsync`. In VS#1 only the
R-T1 test fixture issues stock.

**Checked at COMMIT (deferred triggers):** each value entry has exactly one GL line with the same signed amount, posting date,
plant and item (P-1); for the touched position, valuation balance = Σ value entries = Σ GL RAW_MATERIAL, and quantities agree
across ledger, stock and valuation balances (P-3, AT-01, AT-02). Lock order: stock → valuation → GL period balance.
