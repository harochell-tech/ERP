# Accounting policies (PR-06)

Thresholds are data, never code (ADR-039). Three policies per company, each versioned with an effective range:

| Policy | Parameters |
| --- | --- |
| PURCHASING | receipt_tolerance_pct, match_qty_tolerance_pct, match_price_tolerance_pct, match_amount_tolerance_abs |
| INVENTORY | inventory_adjustment_materiality, grni_aging_alert_days, invoice_price_variance_allocation_method (STOCK_COVERAGE) |
| POSTING | rounding_difference_tolerance, late_entry_hours |

- `PrepareAccountingPolicyVersion` (Controller): all parameters of the policy, validated against `acc.policy_parameter_definition`; values as strings (`"0.02"`).
- `ApproveAccountingPolicyVersion` (APROBADOR_POLITICAS or another holder of `accounting_policy:approve`, step-up): the preparer cannot approve; the open version is closed at the new start date.
- `PolicyResolver.ResolveAsync(context, policy, date)` returns the version and values; a missing policy is a missing prerequisite.
- Approved versions and their parameters are immutable; ACTIVE ranges never overlap.
