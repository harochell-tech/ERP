# Supplier invoices (PR-13a)

Scope (E-10): every line bills an inventory PO line (`line_kind = INVENTORY_PO`); the extension point is
`ISupplierInvoiceLineHandler`, with exactly one implementation (`InventoryPoLineHandler`).

| Command | Who | From → to |
| --- | --- | --- |
| `RegisterSupplierInvoice` | Cuentas por pagar | — → DRAFT (supplier ACTIVE, NCF/e-NCF format and unique, dates, lines of that supplier's approved POs) |
| `MatchSupplierInvoice` | Cuentas por pagar | DRAFT / MATCH_EXCEPTION → MATCHED or MATCH_EXCEPTION |
| `ApproveMatchException` | Controller, step-up, ≠ registrar | MATCH_EXCEPTION → MATCHED (price/amount only) |
| `VoidSupplierInvoice` | Cuentas por pagar | any unposted → VOIDED (the fiscal number is free again) |

Match per line (policy PURCHASING at the invoice date): available = received − invoiced. Quantity above available is an
exception that can only be fixed with a receipt correction and a re-match (E-PR13-1). Price is within tolerance when the unit
difference is within the percentage **or** the line difference is within the absolute amount (E-PR13-2).

## Posting (PR-13b)

`PostSupplierInvoice` (Cuentas por pagar), one transaction:

1. Lock the invoice, then its POs and PO lines; re-check quantities against received − invoiced (CC-03).
2. Fiscal gate and Tax Engine at the invoice date. Closed gate → rejected (SI-07). Non-recoverable ITBIS → **POSTING_BLOCKED**
   with the determination recorded, nothing else (SI-08); such an invoice can be voided.
3. R-04 (AP-REC): Dr GRNI (Q·P, plant + supplier), Dr ITBIS recoverable / Cr AP (Q·P + T − W, AP document), Cr withholding.
4. R-05 (INV-MOV), per line with D = Q·(P′ − P) ≠ 0: s = min(area stock, Q_base) / Q_base; s·D to inventory (PRICE_ADJUSTMENT
   value entry), the rest to PPV; AP takes D.
5. AP document, `qty_invoiced += Q`, BILLS links; the invoice is POSTED (checked at COMMIT against its journal).

## Reversal

`ReverseSupplierInvoice` (Controller, step-up), when the AP document is fully open: exact reversals of R-04 and R-05 (and of each
price adjustment), then R-07B moves (s − s′)·D between inventory and PPV, with s′ the stock coverage at the reversal date.
`qty_invoiced −= Q`, AP open → 0, invoice REVERSED.

**Setup:** approve R-04, R-05, R-07B; map ITBIS_RECOVERABLE, AP_CONTROL (control account), WITHHOLDING_PAYABLE and
PURCHASE_PRICE_VARIANCE; fiscal rules active (A-02).
