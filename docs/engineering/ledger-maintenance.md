# Ledger maintenance (PR-14)

## RepostEvent (T-11, Controller, step-up)

For a journal posted with a wrong account mapping: fix the mapping first (close the wrong map, activate the right one), then
`RepostEvent(sourceEventId, ruleCode, reason)`. One transaction:

1. Generation n is reversed exactly (REVERSAL journal of the repost event).
2. Generation n + 1 of the same event and rule is posted with the rule and mappings in force: same rule lines, dimensions and
   amounts, at the event's business date (late entry if that component is closed).
3. Each inventory line gets a REPOST pair of value entries (−amount pointing to the original, +amount for n + 1): the valuation
   does not change and every GL inventory line keeps exactly one value entry (P-1).

The document keeps its status: it is POSTED because an unreversed AUTO journal of its event exists (generation n + 1).
A reposted receipt or invoice can still be reversed; the Posting Engine follows the repost chain back to the original value entry.

## ApproveValuationResidualAdjustment (T-12, Controller, step-up)

Removes an orphan value (area × item with quantity 0 and value ≠ 0) with R-06, against PURCHASE_PRICE_VARIANCE or
INVENTORY_ADJUSTMENT as the INVENTORY policy says (`valuation_residual_account_role`). Other VAL-RESIDUAL findings are reported by
the reconciliation (PR-16), not adjusted here.

**Setup:** approve R-06; activate an INVENTORY policy version that includes `valuation_residual_account_role` (A-01); map
INVENTORY_ADJUSTMENT if that is the chosen role.
