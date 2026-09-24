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

Posting, AP and reversal: PR-13b.
