# Purchase orders (PR-08)

States (§11.1): DRAFT → PENDING_APPROVAL → APPROVED → (PR-09: PARTIALLY_RECEIVED / RECEIVED) → (PR-13: CLOSED); CANCELLED from
DRAFT, PENDING_APPROVAL or APPROVED when nothing was received. Every status change writes `core.state_history` (checked at COMMIT).

| Command | Permission | Notes |
| --- | --- | --- |
| CreatePurchaseOrder | purchase_order:create | ACTIVE supplier and items; price > 0; UOM = base or convertible on the order date |
| UpdatePurchaseOrderDraft | purchase_order:create | Replaces lines while DRAFT |
| SubmitPurchaseOrder | purchase_order:submit | DRAFT → PENDING_APPROVAL |
| ApprovePurchaseOrder | purchase_order:approve | Approver ≠ creator; purchasing approver ≤ `po_approval_limit` (Controller unlimited); step-up above `po_approval_step_up_threshold`; copies `receipt_tolerance_pct` and the policy version |
| RejectPurchaseOrder | purchase_order:approve | PENDING_APPROVAL → DRAFT, reason required |
| CancelPurchaseOrder | purchase_order:cancel | Reason required; nothing received |
| ApproveOverReceipt | purchase_order:approve_over_receipt (step-up) | Adds to `qty_over_receipt_approved` of one line |

All commands carry the plant (scope) and the expected version where they change the header.
